#!/usr/bin/env python3
"""
Salvages all readable rows from a WAL-corrupted SQLite database into a fresh,
clean `.db` file (with `journal_mode=TRUNCATE` so WSL2 and Windows never collide on `-shm`).

Handles both:
1. B-tree / page corruption inside the database (scans forward & backward around bad pages)
2. Corrupted `-wal` / `-shm` files (creates a stripped copy with WAL header bytes reset to 0x01)
"""

import argparse
import logging
import os
import shutil
import sqlite3
from sparql_pipeline import init_db


def create_wal_stripped_copy(src_path: str, tmp_path: str) -> str:
    """
    Copies the main SQLite file (ignoring the corrupted -wal and -shm files)
    and resets header bytes 18 and 19 from 0x02 (WAL) to 0x01 (Rollback)
    so SQLite doesn't choke on a corrupted -wal file when opening.
    """
    shutil.copyfile(src_path, tmp_path)
    with open(tmp_path, "r+b") as f:
        f.seek(18)
        f.write(b"\x01\x01")
    return tmp_path


def extract_from_connection(src: sqlite3.Connection, dst: sqlite3.Connection) -> tuple[int, int]:
    people_recovered = 0
    occ_recovered = 0

    # Read `people` both forward and backward by rowid so if a middle page is damaged,
    # we salvage everything before it AND everything after it.
    for query in (
        "SELECT qid, name, birth_date, death_date, sitelinks, updated_at FROM people ORDER BY rowid ASC",
        "SELECT qid, name, birth_date, death_date, sitelinks, updated_at FROM people ORDER BY rowid DESC",
    ):
        try:
            cur = src.execute(query)
            batch = []
            hit_error = False
            while True:
                try:
                    row = cur.fetchone()
                except sqlite3.DatabaseError as e:
                    logging.warning(f"Skipped damaged page in `people` ({e}); scanning from other end...")
                    hit_error = True
                    break
                if row is None:
                    break
                batch.append(row)
                if len(batch) >= 5000:
                    with dst:
                        dst.executemany(
                            "INSERT OR IGNORE INTO people (qid, name, birth_date, death_date, sitelinks, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                            batch,
                        )
                    people_recovered += len(batch)
                    batch.clear()
            if batch:
                with dst:
                    dst.executemany(
                        "INSERT OR IGNORE INTO people (qid, name, birth_date, death_date, sitelinks, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                        batch,
                    )
                people_recovered += len(batch)
            if not hit_error:
                break
        except sqlite3.DatabaseError as e:
            logging.warning(f"Could not start query on `people`: {e}")

    for query in (
        "SELECT qid, occupation_qid, occupation_name FROM person_occupations ORDER BY rowid ASC",
        "SELECT qid, occupation_qid, occupation_name FROM person_occupations ORDER BY rowid DESC",
    ):
        try:
            cur = src.execute(query)
            batch = []
            hit_error = False
            while True:
                try:
                    row = cur.fetchone()
                except sqlite3.DatabaseError as e:
                    logging.warning(f"Skipped damaged page in `person_occupations` ({e}); scanning from other end...")
                    hit_error = True
                    break
                if row is None:
                    break
                batch.append(row)
                if len(batch) >= 5000:
                    with dst:
                        dst.executemany(
                            "INSERT OR IGNORE INTO person_occupations (qid, occupation_qid, occupation_name) VALUES (?, ?, ?)",
                            batch,
                        )
                    occ_recovered += len(batch)
                    batch.clear()
            if batch:
                with dst:
                    dst.executemany(
                        "INSERT OR IGNORE INTO person_occupations (qid, occupation_qid, occupation_name) VALUES (?, ?, ?)",
                        batch,
                    )
                occ_recovered += len(batch)
            if not hit_error:
                break
        except sqlite3.DatabaseError as e:
            logging.warning(f"Could not start query on `person_occupations`: {e}")

    return people_recovered, occ_recovered


def recover_database(src_path: str, dst_path: str) -> None:
    logging.info(f"Opening/creating clean target database '{dst_path}' (will merge/upsert without deleting existing rows)...")
    dst = init_db(dst_path)

    # Pass 1: Try reading from the original file (with its -wal if readable)
    logging.info(f"Pass 1: Salvaging rows from '{src_path}'...")
    try:
        src = sqlite3.connect(src_path)
        extract_from_connection(src, dst)
        src.close()
    except sqlite3.DatabaseError as e:
        logging.warning(f"Pass 1 encountered WAL error ({e}). Proceeding to Pass 2...")

    # Pass 2: Also try reading from a WAL-stripped copy of the 43MB main file
    tmp_path = f"{src_path}.tmp_nowal"
    try:
        logging.info("Pass 2: Checking main database file without corrupted -wal/-shm...")
        create_wal_stripped_copy(src_path, tmp_path)
        src_nowal = sqlite3.connect(tmp_path)
        src_nowal.execute("PRAGMA writable_schema=ON;")
        extract_from_connection(src_nowal, dst)
        src_nowal.close()
    except Exception as e:
        logging.warning(f"Pass 2 note: {e}")
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)

    total_people = dst.execute("SELECT COUNT(*) FROM people").fetchone()[0]
    total_occs = dst.execute("SELECT COUNT(*) FROM person_occupations").fetchone()[0]
    logging.info(
        f"Recovery complete! Clean database '{dst_path}' now has "
        f"{total_people:,} unique people and {total_occs:,} occupation links."
    )
    dst.close()


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s", datefmt="%H:%M:%S")
    parser = argparse.ArgumentParser(description="Salvage rows from a corrupted SQLite DB into a fresh .db file")
    parser.add_argument("src", help="Corrupted database path (e.g. celebrities-latest-all)")
    parser.add_argument("dst", help="New clean database path (e.g. celebrities-latest-all.db)")
    args = parser.parse_args()
    recover_database(args.src, args.dst)
