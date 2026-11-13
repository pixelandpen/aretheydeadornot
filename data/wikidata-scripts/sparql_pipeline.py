#!/usr/bin/env python3
"""
Strategy 1: Wikidata SPARQL Time-Windowed Celebrity Pipeline -> SQLite

Gathers humans born since 1900 across target occupations from the Wikidata
SPARQL endpoint using range-safe birth-date windowing, automatic window
splitting on timeout, polite rate-limiting, and idempotent SQLite upserts.
"""

import argparse
import datetime
import json
import logging
import sqlite3
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Dict, List, Optional, Tuple

WIKIDATA_SPARQL_URL = "https://query.wikidata.org/sparql"

# Original target occupations (Wikidata Q-ID -> human-readable label)
BASE_OCCUPATIONS: Dict[str, str] = {
    "Q33999": "actor",
    "Q10800557": "film actor",
    "Q10798782": "television actor",
    "Q639669": "musician",
    "Q177220": "singer",
    "Q488205": "singer-songwriter",
    "Q2066131": "athlete",
    "Q937857": "association football player",
    "Q3665646": "basketball player",
    "Q10871364": "baseball player",
    "Q19204627": "American football player",
    "Q36180": "writer",
    "Q482980": "author",
    "Q82955": "politician",
    "Q245068": "comedian",
    "Q15253558": "activist",
    "Q1476215": "human rights activist",
    "Q14915627": "military leader",
    "Q189290": "military officer",
    "Q191808": "religious leader",
    "Q2259532": "cleric",
}

# Business & Science occupations (added in pass 2)
NEW_OCCUPATIONS: Dict[str, str] = {
    # Business
    "Q43845": "businessperson",
    "Q131524": "entrepreneur",
    "Q484876": "chief executive officer",
    "Q806798": "banker",
    "Q557880": "investor",
    # Science, Engineering & Medicine
    "Q901": "scientist",
    "Q169470": "physicist",
    "Q593644": "chemist",
    "Q864503": "biologist",
    "Q170790": "mathematician",
    "Q82594": "computer scientist",
    "Q188094": "economist",
    "Q11063": "astronomer",
    "Q81096": "engineer",
    "Q205375": "inventor",
    "Q39631": "physician",
    "Q212980": "psychologist",
}

DEFAULT_OCCUPATIONS: Dict[str, str] = {**BASE_OCCUPATIONS, **NEW_OCCUPATIONS}


def init_db(db_path: str) -> sqlite3.Connection:
    """Initializes the SQLite database with tables, indexes, and convenience views."""
    conn = sqlite3.connect(db_path, timeout=60.0)
    # Use TRUNCATE journal mode instead of WAL so WSL2 (/mnt/c) + Windows apps
    # never collide on memory-mapped -shm files across the 9P filesystem bridge.
    conn.execute("PRAGMA journal_mode=TRUNCATE;")
    conn.execute("PRAGMA synchronous=NORMAL;")
    conn.execute("PRAGMA foreign_keys=ON;")

    conn.executescript(
        """
        CREATE TABLE IF NOT EXISTS people (
            qid TEXT PRIMARY KEY,
            name TEXT,
            birth_date TEXT,
            death_date TEXT,
            sitelinks INTEGER DEFAULT 0,
            updated_at TEXT DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS person_occupations (
            qid TEXT NOT NULL,
            occupation_qid TEXT NOT NULL,
            occupation_name TEXT NOT NULL,
            PRIMARY KEY (qid, occupation_qid),
            FOREIGN KEY (qid) REFERENCES people(qid) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_people_birth_date ON people(birth_date);
        CREATE INDEX IF NOT EXISTS idx_people_sitelinks ON people(sitelinks DESC);
        CREATE INDEX IF NOT EXISTS idx_occ_qid ON person_occupations(occupation_qid);

        -- View that presents one row per person with semicolon-separated occupations
        CREATE VIEW IF NOT EXISTS people_with_occupations AS
        SELECT
            p.qid,
            p.name,
            p.birth_date,
            p.death_date,
            GROUP_CONCAT(po.occupation_name, '; ') AS occupations,
            p.sitelinks,
            p.updated_at
        FROM people p
        LEFT JOIN person_occupations po ON p.qid = po.qid
        GROUP BY p.qid;

        -- Checkpoint table so the pipeline can pause and resume cleanly
        CREATE TABLE IF NOT EXISTS sync_progress (
            occupation_qid TEXT NOT NULL,
            window_start TEXT NOT NULL,
            window_end TEXT NOT NULL,
            status TEXT NOT NULL,
            rows_processed INTEGER DEFAULT 0,
            elapsed_sec REAL DEFAULT 0.0,
            completed_at TEXT DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (occupation_qid, window_start, window_end)
        );
        """
    )
    conn.commit()
    return conn


def build_sparql_query(
    occupation_qid: str,
    start_date: str,
    end_date: str,
    min_sitelinks: int = 0,
) -> str:
    """
    Builds a fast, range-safe SPARQL query for a single occupation and date window.
    Uses MIN(?birthDate) and MIN(?deathDate) to deduplicate multi-date records (like Marilyn Monroe).
    """
    sitelink_filter = (
        f"FILTER(?sitelinks >= {min_sitelinks})" if min_sitelinks > 0 else ""
    )
    return f"""
SELECT ?person ?personLabel
       (MIN(?birthDate) AS ?born)
       (MIN(?deathDate) AS ?died)
       (MAX(?sitelinks) AS ?maxSitelinks)
WITH {{
  SELECT ?person ?birthDate WHERE {{
    hint:Query hint:optimizer "None".
    ?person wdt:P106 wd:{occupation_qid} ;
            wdt:P569 ?birthDate .
    hint:Prior hint:rangeSafe true .
    FILTER(?birthDate >= "{start_date}T00:00:00Z"^^xsd:dateTime &&
           ?birthDate <  "{end_date}T00:00:00Z"^^xsd:dateTime)
  }}
}} AS %slice
WHERE {{
  INCLUDE %slice
  OPTIONAL {{ ?person wdt:P570 ?deathDate . }}
  OPTIONAL {{ ?person wikibase:sitelinks ?sitelinks . }}
  {sitelink_filter}
  SERVICE wikibase:label {{
    bd:serviceParam wikibase:language "en,mul,es,fr,de,it,pt".
    ?person rdfs:label ?personLabel .
  }}
}}
GROUP BY ?person ?personLabel
"""


class SparqlTimeoutError(Exception):
    """Raised when a SPARQL query times out so the caller can split the date window."""


def execute_sparql(
    query: str,
    user_agent: str,
    max_retries: int = 4,
    timeout_sec: int = 65,
) -> List[dict]:
    """
    Executes a SPARQL query against Wikidata with Retry-After and exponential backoff.
    Raises SparqlTimeoutError if the query consistently times out (502/504/Timeout).
    """
    data = urllib.parse.urlencode({"query": query, "format": "json"}).encode("utf-8")
    headers = {
        "Accept": "application/sparql-results+json",
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
        "User-Agent": user_agent,
    }

    for attempt in range(1, max_retries + 1):
        req = urllib.request.Request(WIKIDATA_SPARQL_URL, data=data, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=timeout_sec) as resp:
                payload = json.loads(resp.read().decode("utf-8"))
                return payload.get("results", {}).get("bindings", [])

        except urllib.error.HTTPError as e:
            if e.code == 429:
                retry_after = int(e.headers.get("Retry-After", 30 * attempt))
                logging.warning(f"HTTP 429 rate limit hit. Sleeping {retry_after}s (attempt {attempt}/{max_retries})...")
                time.sleep(retry_after)
            elif e.code in (500, 502, 503, 504):
                # 502 / 504 on Wikidata usually means the query exceeded the 60s limit
                if attempt >= 2 and e.code in (502, 504):
                    raise SparqlTimeoutError(f"HTTP {e.code} gateway timeout on window") from e
                wait = 10 * attempt
                logging.warning(f"HTTP {e.code} from Wikidata. Retrying in {wait}s (attempt {attempt}/{max_retries})...")
                time.sleep(wait)
            else:
                raise

        except (urllib.error.URLError, TimeoutError) as e:
            if attempt >= 2:
                raise SparqlTimeoutError(f"Request timed out: {e}") from e
            wait = 10 * attempt
            logging.warning(f"Network/timeout error ({e}). Retrying in {wait}s...")
            time.sleep(wait)

    raise SparqlTimeoutError("Exceeded max retries for SPARQL query")


def upsert_batch(
    conn: sqlite3.Connection,
    bindings: List[dict],
    occupation_qid: str,
    occupation_name: str,
) -> int:
    """Upserts a batch of SPARQL result bindings into SQLite inside a single transaction."""
    people_rows = []
    occ_rows = []

    for row in bindings:
        person_uri = row.get("person", {}).get("value", "")
        if not person_uri:
            continue
        qid = person_uri.rsplit("/", 1)[-1]
        name = row.get("personLabel", {}).get("value", qid)
        born = row.get("born", {}).get("value")
        died = row.get("died", {}).get("value")
        sitelinks_str = row.get("maxSitelinks", {}).get("value", "0")
        try:
            sitelinks = int(float(sitelinks_str))
        except ValueError:
            sitelinks = 0

        people_rows.append((qid, name, born, died, sitelinks))
        occ_rows.append((qid, occupation_qid, occupation_name))

    with conn:
        conn.executemany(
            """
            INSERT INTO people (qid, name, birth_date, death_date, sitelinks, updated_at)
            VALUES (?, ?, ?, ?, ?, CURRENT_TIMESTAMP)
            ON CONFLICT(qid) DO UPDATE SET
                name = COALESCE(excluded.name, people.name),
                birth_date = COALESCE(excluded.birth_date, people.birth_date),
                death_date = COALESCE(excluded.death_date, people.death_date),
                sitelinks = MAX(people.sitelinks, excluded.sitelinks),
                updated_at = CURRENT_TIMESTAMP;
            """,
            people_rows,
        )
        conn.executemany(
            """
            INSERT OR IGNORE INTO person_occupations (qid, occupation_qid, occupation_name)
            VALUES (?, ?, ?);
            """,
            occ_rows,
        )

    return len(people_rows)


def is_window_completed(
    conn: sqlite3.Connection,
    occupation_qid: str,
    start_date: str,
    end_date: str,
) -> bool:
    cur = conn.execute(
        """
        SELECT 1 FROM sync_progress
        WHERE occupation_qid = ? AND window_start = ? AND window_end = ? AND status = 'completed'
        """,
        (occupation_qid, start_date, end_date),
    )
    return cur.fetchone() is not None


def mark_window_completed(
    conn: sqlite3.Connection,
    occupation_qid: str,
    start_date: str,
    end_date: str,
    rows_processed: int,
    elapsed_sec: float,
) -> None:
    with conn:
        conn.execute(
            """
            INSERT INTO sync_progress (occupation_qid, window_start, window_end, status, rows_processed, elapsed_sec, completed_at)
            VALUES (?, ?, ?, 'completed', ?, ?, CURRENT_TIMESTAMP)
            ON CONFLICT(occupation_qid, window_start, window_end) DO UPDATE SET
                status = 'completed',
                rows_processed = excluded.rows_processed,
                elapsed_sec = excluded.elapsed_sec,
                completed_at = CURRENT_TIMESTAMP;
            """,
            (occupation_qid, start_date, end_date, rows_processed, elapsed_sec),
        )


def generate_year_windows(start_year: int, end_year: int, step_years: int = 1) -> List[Tuple[str, str]]:
    """Generates (start_date, end_date) ISO strings in step_years increments."""
    windows = []
    y = start_year
    while y <= end_year:
        next_y = min(y + step_years, end_year + 1)
        windows.append((f"{y:04d}-01-01", f"{next_y:04d}-01-01"))
        y = next_y
    return windows


def split_date_window(start_date: str, end_date: str) -> List[Tuple[str, str]]:
    """Splits a [start_date, end_date) window in half if a query times out."""
    d1 = datetime.date.fromisoformat(start_date)
    d2 = datetime.date.fromisoformat(end_date)
    delta_days = (d2 - d1).days
    if delta_days <= 1:
        raise RuntimeError(f"Cannot split 1-day window [{start_date}, {end_date}) any further.")
    mid = d1 + datetime.timedelta(days=delta_days // 2)
    mid_str = mid.isoformat()
    return [(start_date, mid_str), (mid_str, end_date)]


def process_window(
    conn: sqlite3.Connection,
    occupation_qid: str,
    occupation_name: str,
    start_date: str,
    end_date: str,
    min_sitelinks: int,
    user_agent: str,
    min_delay: float,
) -> int:
    """
    Processes a single (occupation, [start_date, end_date)) window.
    If the window times out on Wikidata, automatically splits it into two smaller windows!
    """
    if is_window_completed(conn, occupation_qid, start_date, end_date):
        logging.debug(f"Skipping completed window {occupation_name} ({occupation_qid}) [{start_date} .. {end_date})")
        return 0

    query = build_sparql_query(occupation_qid, start_date, end_date, min_sitelinks)
    t0 = time.perf_counter()

    try:
        bindings = execute_sparql(query, user_agent=user_agent)
    except SparqlTimeoutError:
        logging.warning(
            f"Window [{start_date} .. {end_date}) timed out for {occupation_name} ({occupation_qid}). "
            f"Splitting window in half..."
        )
        sub_windows = split_date_window(start_date, end_date)
        total = 0
        for sub_start, sub_end in sub_windows:
            total += process_window(
                conn,
                occupation_qid,
                occupation_name,
                sub_start,
                sub_end,
                min_sitelinks,
                user_agent,
                min_delay,
            )
        return total

    elapsed = time.perf_counter() - t0
    count = upsert_batch(conn, bindings, occupation_qid, occupation_name)
    mark_window_completed(conn, occupation_qid, start_date, end_date, count, elapsed)

    logging.info(
        f"[{occupation_name:<18} | {occupation_qid}] {start_date} -> {end_date} : "
        f"{count:>5} people upserted in {elapsed:.2f}s"
    )

    # Adaptive polite delay: wait at least min_delay or 1.2x the query time
    sleep_time = max(min_delay, elapsed * 1.2)
    time.sleep(sleep_time)
    return count


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Wikidata SPARQL Celebrity Birth/Death Pipeline -> SQLite"
    )
    parser.add_argument(
        "--db",
        default="celebrities.db",
        help="Path to SQLite database file (default: celebrities.db)",
    )
    parser.add_argument(
        "--start-year",
        type=int,
        default=1900,
        help="Start birth year inclusive (default: 1900)",
    )
    parser.add_argument(
        "--end-year",
        type=int,
        default=datetime.date.today().year,
        help="End birth year inclusive (default: current year)",
    )
    parser.add_argument(
        "--step-years",
        type=int,
        default=2,
        help="Initial window size in years per query (default: 2; auto-splits if a window times out)",
    )
    parser.add_argument(
        "--min-sitelinks",
        type=int,
        default=0,
        help="Minimum Wikipedia sitelinks filter (default: 0 to collect ALL 500K+ listings; set e.g. 5 or 10 for faster famous-only run)",
    )
    parser.add_argument(
        "--delay",
        type=float,
        default=1.5,
        help="Minimum delay in seconds between SPARQL requests (default: 1.5)",
    )
    parser.add_argument(
        "--user-agent",
        default="CelebrityDBPipeline/1.0 (https://github.com/graham/celebrity-db; contact@example.com) Python-urllib/3",
        help="Custom User-Agent header required by Wikimedia Bot Policy",
    )
    parser.add_argument(
        "--test",
        action="store_true",
        help="Run a quick 1-window smoke test (comedians born 1950-1952) and print sample rows",
    )
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )

    conn = init_db(args.db)

    if args.test:
        logging.info("Running smoke test: comedians (Q245068) and actors (Q33999) born 1950-1951...")
        process_window(conn, "Q245068", "comedian", "1950-01-01", "1951-01-01", args.min_sitelinks, args.user_agent, args.delay)
        process_window(conn, "Q33999", "actor", "1950-01-01", "1951-01-01", args.min_sitelinks, args.user_agent, args.delay)
        cur = conn.execute(
            """
            SELECT qid, name, birth_date, death_date, occupations, sitelinks
            FROM people_with_occupations
            ORDER BY sitelinks DESC
            LIMIT 10
            """
        )
        print("\nTop 10 test rows from `people_with_occupations` view:")
        for row in cur.fetchall():
            print(" ", row)
        conn.close()
        return

    windows = generate_year_windows(args.start_year, args.end_year, args.step_years)
    logging.info(
        f"Starting pipeline: {len(DEFAULT_OCCUPATIONS)} occupations x {len(windows)} windows "
        f"({args.start_year}-{args.end_year}, step={args.step_years}y) -> {args.db}"
    )

    total_upserted = 0
    try:
        for occ_qid, occ_name in DEFAULT_OCCUPATIONS.items():
            for start_date, end_date in windows:
                total_upserted += process_window(
                    conn=conn,
                    occupation_qid=occ_qid,
                    occupation_name=occ_name,
                    start_date=start_date,
                    end_date=end_date,
                    min_sitelinks=args.min_sitelinks,
                    user_agent=args.user_agent,
                    min_delay=args.delay,
                )
    except KeyboardInterrupt:
        logging.info("Interrupted by user! Progress is saved in `sync_progress` — rerun to resume.")
    finally:
        cur = conn.execute("SELECT COUNT(*) FROM people")
        unique_people = cur.fetchone()[0]
        logging.info(f"Finished session. Total unique people in DB: {unique_people:,}")
        conn.close()


if __name__ == "__main__":
    main()
