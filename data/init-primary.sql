CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS people (
    qid TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    birth_date DATE NULL,
    death_date DATE NULL,
    death_month_day SMALLINT NULL,
    sitelinks INTEGER NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS person_occupations (
    qid TEXT NOT NULL REFERENCES people(qid) ON DELETE CASCADE,
    occupation_qid TEXT NOT NULL,
    occupation_name TEXT NOT NULL,
    PRIMARY KEY (qid, occupation_qid)
);

-- Indexes for sub-100ms read-replica queries
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
