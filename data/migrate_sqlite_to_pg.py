#!/usr/bin/env python3
"""Bulk-migrates the Wikidata SQLite database into PostgreSQL using COPY."""

import datetime
import os
import sqlite3
import time
import unicodedata
import psycopg


def normalize_name(text: str | None) -> str:
    if not text:
        return ""
    decomposed = unicodedata.normalize("NFKD", text.strip())
    chars = []
    prev_space = False
    for ch in decomposed:
        if unicodedata.combining(ch):
            continue
        if ch.isspace():
            if not prev_space:
                chars.append(" ")
                prev_space = True
        else:
            chars.append(ch.lower())
            prev_space = False
    return "".join(chars).strip()


def parse_iso_date(raw: str | None) -> datetime.date | None:
    if not raw or len(raw) < 10:
        return None
    date_part = raw[:10]
    try:
        return datetime.date.fromisoformat(date_part)
    except ValueError:
        return None


def main() -> None:
    sqlite_path = os.environ.get("SQLITE_PATH", "/data/celebrities-latest-all.db")
    pg_dsn = os.environ["PG_PRIMARY_DSN"]
    batch_size = int(os.environ.get("BATCH_SIZE", "50000"))

    start = time.time()
    print(f"Connecting to SQLite: {sqlite_path}")
    sq_conn = sqlite3.connect(f"file:{sqlite_path}?mode=ro", uri=True)
    sq_cur = sq_conn.cursor()

    print("Connecting to PostgreSQL primary...")
    with psycopg.connect(pg_dsn) as pg_conn:
        with pg_conn.cursor() as cur:
            print("Truncating existing tables...")
            cur.execute("TRUNCATE TABLE person_occupations, people;")
            pg_conn.commit()

            print("Dropping secondary indexes for fast bulk COPY...")
            cur.execute("""
                DROP INDEX IF EXISTS idx_people_norm_name_sitelinks;
                DROP INDEX IF EXISTS idx_people_norm_name_trgm;
                DROP INDEX IF EXISTS idx_people_recent_deaths;
                DROP INDEX IF EXISTS idx_people_in_memory;
            """)
            pg_conn.commit()

            print("Copying people...")
            sq_cur.execute("SELECT qid, name, birth_date, death_date, sitelinks FROM people")
            people_count = 0
            with cur.copy(
                "COPY people (qid, name, normalized_name, birth_date, death_date, death_month_day, sitelinks) FROM STDIN"
            ) as copy:
                while rows := sq_cur.fetchmany(batch_size):
                    for qid, name, birth_raw, death_raw, sitelinks in rows:
                        if not qid or not name:
                            continue
                        b_date = parse_iso_date(birth_raw)
                        d_date = parse_iso_date(death_raw)
                        d_md = (d_date.month * 100 + d_date.day) if d_date else None
                        copy.write_row((
                            qid,
                            name,
                            normalize_name(name),
                            b_date,
                            d_date,
                            d_md,
                            sitelinks or 0,
                        ))
                        people_count += 1
                    if people_count % 250000 == 0:
                        print(f"  Copied {people_count:,} people...")
            pg_conn.commit()
            print(f"Copied {people_count:,} people total.")

            print("Copying person_occupations...")
            sq_cur.execute("SELECT qid, occupation_qid, occupation_name FROM person_occupations")
            occ_count = 0
            with cur.copy(
                "COPY person_occupations (qid, occupation_qid, occupation_name) FROM STDIN"
            ) as copy:
                while rows := sq_cur.fetchmany(batch_size):
                    for qid, occ_qid, occ_name in rows:
                        if not qid or not occ_qid or not occ_name:
                            continue
                        copy.write_row((qid, occ_qid, occ_name))
                        occ_count += 1
                    if occ_count % 500000 == 0:
                        print(f"  Copied {occ_count:,} occupations...")
            pg_conn.commit()
            print(f"Copied {occ_count:,} occupations total.")

            print("Rebuilding indexes (this may take 1-2 minutes)...")
            cur.execute("""
                CREATE INDEX IF NOT EXISTS idx_people_norm_name_sitelinks
                    ON people (normalized_name, sitelinks DESC);
                CREATE INDEX IF NOT EXISTS idx_people_norm_name_trgm
                    ON people USING GIN (normalized_name gin_trgm_ops);
                CREATE INDEX IF NOT EXISTS idx_people_recent_deaths
                    ON people (death_date DESC, sitelinks DESC)
                    WHERE death_date IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_people_in_memory
                    ON people (death_month_day, sitelinks DESC)
                    WHERE death_date IS NOT NULL;
                ANALYZE people;
                ANALYZE person_occupations;
            """)
            pg_conn.commit()

    elapsed = time.time() - start
    print(f"Migration complete in {elapsed:.1f}s!")


if __name__ == "__main__":
    main()
