# Celebrity Birth & Death Database Pipeline (Wikidata -> SQLite)

This directory contains two interchangeable pipelines that populate the same SQLite database (`celebrities.db`) with people born since 1900 across target occupations (actors, musicians, athletes, writers, politicians, comedians, activists, military leaders, religious leaders, etc.).

## Database Schema (`celebrities.db`)

Both pipelines create and upsert into the same normalized schema:

* **`people`**: One row per unique person (`qid PRIMARY KEY`, `name`, `birth_date`, `death_date`, `sitelinks`, `updated_at`)
* **`person_occupations`**: Junction table (`qid`, `occupation_qid`, `occupation_name`)
* **`people_with_occupations` (SQL View)**: Ready-to-query view that joins `people` and `person_occupations` into one row per person with semicolon-separated occupations (`actor; comedian; writer`).
* **`sync_progress`**: Tracks completed `(occupation_qid, window_start, window_end)` slices so `sparql_pipeline.py` can be stopped with `Ctrl+C` and resumed at any time without re-fetching completed slices.

---

## Strategy 1: Time-Windowed SPARQL Pipeline (`sparql_pipeline.py`)

Uses range-indexed `birthDate` (`wdt:P569` + `hint:Prior hint:rangeSafe true`) windows per occupation. If any window is too large and hits a Wikidata 60s timeout (`502`/`504`), the script **automatically splits that date window in half** recursively down to months/days.

### Quick Smoke Test (2 requests, ~4 seconds)
```bash
python sparql_pipeline.py --test
```

### Run Full Pipeline (All 500K+ listings per occupation)
```bash
python sparql_pipeline.py --db celebrities.db --start-year 1900 --step-years 2 --delay 1.5
```

### Run Faster "Notable People Only" Pipeline (e.g. 10+ Wikipedia language articles)
```bash
python sparql_pipeline.py --db celebrities.db --start-year 1900 --step-years 5 --min-sitelinks 10
```

---

## Strategy 2: Local JSON Dump Streaming Pipeline (`dump_pipeline.py`)

Streams Wikidata's full entity dump (`latest-all.json.bz2`, ~95 GB compressed, available at [Wikidata entity dump](https://dumps.wikimedia.org/wikidatawiki/entities/)) line-by-line in constant memory (`< 100 MB` RAM) with zero rate limits or network timeouts.

### 1. Download the Dump (Resumable with `aria2c` or `curl -C -`)
```bash
# Using aria2c (16 connections, much faster from mirrors):
aria2c -x 16 -s 16 https://dumps.wikimedia.org/wikidatawiki/entities/latest-all.json.bz2
```

### 2. Stream Into SQLite
**Option A: Direct Python streaming (single-core `.bz2` decompression)**
```bash
python dump_pipeline.py latest-all.json.bz2 --db celebrities.db
```

**Option B: Multi-core decompression with `lbzip2` or `pbzip2` (5x–8x faster on multi-core CPUs)**
```bash
lbzip2 -dc latest-all.json.bz2 | python dump_pipeline.py - --db celebrities.db
```

---

## Example SQLite Queries

```sql
-- Top 25 most famous people in the database (with semicolon-separated occupations)
SELECT qid, name, birth_date, death_date, occupations, sitelinks
FROM people_with_occupations
ORDER BY sitelinks DESC
LIMIT 25;

-- Count of people per occupation
SELECT occupation_name, COUNT(*) AS total
FROM person_occupations
GROUP BY occupation_name
ORDER BY total DESC;
```
