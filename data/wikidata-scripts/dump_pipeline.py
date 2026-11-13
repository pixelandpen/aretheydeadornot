#!/usr/bin/env python3
"""
Strategy 2: Wikidata Full JSON Dump Streaming Pipeline -> SQLite

Streams Wikidata's `latest-all.json.bz2` (or `.gz` / stdin from `lbzip2 -dc`)
line-by-line in constant memory, filtering for humans (P31=Q5) born >= 1900 (P569)
who have at least one target occupation (P106), and bulk-inserting batches into
the same SQLite schema used by `sparql_pipeline.py`.
"""

import argparse
import bz2
import gzip
import io
import json
import logging
import sys
import time
from typing import Dict, Iterable, List, Optional, Tuple

from sparql_pipeline import DEFAULT_OCCUPATIONS, NEW_OCCUPATIONS, init_db


def extract_claim_qids(claims: dict, prop: str) -> List[str]:
    """Extracts target entity Q-IDs (e.g., 'Q5') for a given property (e.g., 'P31')."""
    qids = []
    for statement in claims.get(prop, []):
        if statement.get("rank") == "deprecated":
            continue
        mainsnak = statement.get("mainsnak", {})
        if mainsnak.get("snaktype") != "value":
            continue
        val = mainsnak.get("datavalue", {}).get("value", {})
        if isinstance(val, dict) and "id" in val:
            qids.append(val["id"])
    return qids


def extract_earliest_date(claims: dict, prop: str) -> Optional[str]:
    """
    Extracts the preferred (or earliest normal-rank) ISO date string for P569/P570.
    Prefers statements marked 'preferred' (which resolves dual-date cases like Marilyn Monroe),
    falling back to MIN(date) across 'normal' rank statements.
    """
    preferred_dates = []
    normal_dates = []

    for statement in claims.get(prop, []):
        rank = statement.get("rank", "normal")
        if rank == "deprecated":
            continue
        mainsnak = statement.get("mainsnak", {})
        if mainsnak.get("snaktype") != "value":
            continue
        time_str = mainsnak.get("datavalue", {}).get("value", {}).get("time")
        if not time_str:
            continue
        # Wikidata prefixes positive years with '+', e.g. '+1926-06-01T00:00:00Z'
        clean_date = time_str.lstrip("+")
        if rank == "preferred":
            preferred_dates.append(clean_date)
        else:
            normal_dates.append(clean_date)

    if preferred_dates:
        return min(preferred_dates)
    if normal_dates:
        return min(normal_dates)
    return None


def extract_label(labels: dict) -> Optional[str]:
    """Extracts the person's name using English -> mul -> major European fallback languages."""
    for lang in ("en", "mul", "es", "fr", "de", "it", "pt"):
        if lang in labels:
            return labels[lang].get("value")
    # Last resort: any available label
    if labels:
        first_entry = next(iter(labels.values()))
        return first_entry.get("value")
    return None


def parse_entity_line(
    line: str,
    target_occupations: Dict[str, str],
    quoted_target_qids: Tuple[str, ...],
    min_birth_year: int = 1900,
) -> Optional[Tuple[Tuple[str, str, str, Optional[str], int], List[Tuple[str, str, str]]]]:
    """
    Parses a single JSON line from the Wikidata dump.
    Returns ((qid, name, birth_date, death_date, sitelinks), [(qid, occ_qid, occ_name), ...])
    if the item matches all criteria, or None otherwise.
    """
    line = line.strip().rstrip(",")
    if not line or line in ("[", "]"):
        return None

    # Fast pre-check before JSON parsing: skip lines that don't mention P569 (birth date), Q5 (human), and P106
    if '"P569"' not in line or '"Q5"' not in line or '"P106"' not in line:
        return None

    # Second fast C-string pre-check: skip JSON parsing unless at least one target occupation QID is in the line
    if not any(q in line for q in quoted_target_qids):
        return None

    try:
        entity = json.loads(line)
    except json.JSONDecodeError:
        return None

    claims = entity.get("claims", {})

    # 1. Must be human (P31 = Q5)
    if "Q5" not in extract_claim_qids(claims, "P31"):
        return None

    # 2. Must have at least one target occupation (P106)
    occ_qids = extract_claim_qids(claims, "P106")
    matched_occs = [q for q in occ_qids if q in target_occupations]
    if not matched_occs:
        return None

    # 3. Must have birth date >= min_birth_year (P569)
    birth_date = extract_earliest_date(claims, "P569")
    if not birth_date or birth_date.startswith("-"):
        return None
    try:
        birth_year = int(birth_date[:4])
    except ValueError:
        return None
    if birth_year < min_birth_year:
        return None

    qid = entity.get("id", "")
    name = extract_label(entity.get("labels", {})) or qid
    death_date = extract_earliest_date(claims, "P570")
    sitelinks = len(entity.get("sitelinks", {}))

    person_tuple = (qid, name, birth_date, death_date, sitelinks)
    occ_tuples = [(qid, occ_qid, target_occupations[occ_qid]) for occ_qid in set(matched_occs)]
    return person_tuple, occ_tuples


def open_dump_stream(dump_path: str) -> Iterable[str]:
    """Opens `-` (stdin), `.bz2`, `.gz`, or plain `.json` for streaming line-by-line."""
    if dump_path == "-":
        return io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
    if dump_path.endswith(".bz2"):
        return bz2.open(dump_path, mode="rt", encoding="utf-8")
    if dump_path.endswith(".gz"):
        return gzip.open(dump_path, mode="rt", encoding="utf-8")
    return open(dump_path, mode="r", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Strategy 2: Stream Wikidata JSON dump into SQLite"
    )
    parser.add_argument(
        "dump_path",
        help="Path to latest-all.json.bz2 / .gz, or '-' to read from stdin (e.g. `lbzip2 -dc latest-all.json.bz2 | python dump_pipeline.py -`)",
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
        help="Minimum birth year inclusive (default: 1900)",
    )
    parser.add_argument(
        "--batch-size",
        type=int,
        default=5000,
        help="Number of matched people to buffer in memory before committing to SQLite (default: 5000)",
    )
    parser.add_argument(
        "--skip-lines",
        type=int,
        default=0,
        help="Fast-forward past the first N lines of the dump when resuming an interrupted run (default: 0)",
    )
    parser.add_argument(
        "--max-lines",
        type=int,
        default=0,
        help="Stop automatically after scanning N lines (0 = read until end of dump)",
    )
    parser.add_argument(
        "--only-new",
        action="store_true",
        help="Only extract the newly added Business & Science occupations (NEW_OCCUPATIONS) for fast incremental updates",
    )
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )

    target_occupations = NEW_OCCUPATIONS if args.only_new else DEFAULT_OCCUPATIONS
    quoted_target_qids = tuple(f'"{qid}"' for qid in target_occupations.keys())
    logging.info(
        f"Active occupations ({len(target_occupations)}): {', '.join(target_occupations.values())}"
    )

    conn = init_db(args.db)
    people_batch = []
    occ_batch = []
    lines_scanned = 0
    people_matched = 0
    t0 = time.perf_counter()

    def flush_batches():
        nonlocal people_batch, occ_batch
        if not people_batch:
            return
        with conn:
            conn.executemany(
                """
                INSERT INTO people (qid, name, birth_date, death_date, sitelinks, updated_at)
                VALUES (?, ?, ?, ?, ?, CURRENT_TIMESTAMP)
                ON CONFLICT(qid) DO UPDATE SET
                    name = excluded.name,
                    birth_date = excluded.birth_date,
                    death_date = excluded.death_date,
                    sitelinks = excluded.sitelinks,
                    updated_at = CURRENT_TIMESTAMP;
                """,
                people_batch,
            )
            conn.executemany(
                """
                INSERT OR IGNORE INTO person_occupations (qid, occupation_qid, occupation_name)
                VALUES (?, ?, ?);
                """,
                occ_batch,
            )
        people_batch.clear()
        occ_batch.clear()

    logging.info(f"Streaming Wikidata dump from '{args.dump_path}' -> {args.db}...")
    try:
        with open_dump_stream(args.dump_path) as stream:
            for line in stream:
                lines_scanned += 1
                if args.max_lines and lines_scanned > args.max_lines:
                    logging.info(f"Reached --max-lines ({args.max_lines:,}); stopping cleanly...")
                    break

                if lines_scanned <= args.skip_lines:
                    if lines_scanned % 1_000_000 == 0:
                        elapsed = time.perf_counter() - t0
                        logging.info(
                            f"Fast-forwarding: skipped {lines_scanned:,} / {args.skip_lines:,} lines ({lines_scanned / elapsed:,.0f}/sec)..."
                        )
                    continue

                if lines_scanned % 250_000 == 0:
                    elapsed = time.perf_counter() - t0
                    rate = lines_scanned / elapsed
                    logging.info(
                        f"Scanned {lines_scanned:,} entities ({rate:,.0f}/sec) | "
                        f"Matched {people_matched:,} people"
                    )

                result = parse_entity_line(
                    line, target_occupations, quoted_target_qids, args.start_year
                )
                if result is None:
                    continue

                person_tuple, occ_tuples = result
                people_batch.append(person_tuple)
                occ_batch.extend(occ_tuples)
                people_matched += 1

                if len(people_batch) >= args.batch_size:
                    flush_batches()

        flush_batches()
    except KeyboardInterrupt:
        logging.info("Interrupted! Flushing remaining batch to SQLite...")
        flush_batches()
    finally:
        elapsed = time.perf_counter() - t0
        logging.info(
            f"Done in {elapsed:.1f}s. Scanned {lines_scanned:,} entities, "
            f"upserted {people_matched:,} matching people."
        )
        conn.close()


if __name__ == "__main__":
    main()
