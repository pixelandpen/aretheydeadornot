# Testing & Load Simulation

This directory contains:
1. **xUnit Unit Tests** ([`Core.UnitTests`](Core.UnitTests/)) — fast, isolated tests for domain logic.
2. **k6 Load & Spike Simulator** ([`load/load_test.js`](load/load_test.js)) — realistic mixed-traffic load tests for latency, cache hit behavior, and rate-limiting verification.

---

## 1. Running Unit Tests (xUnit)

With Docker (no local .NET SDK required):
```bash
docker run --rm -v "$(pwd):/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test
```

Covered areas:
- `NameNormalizerTests`: trimming, whitespace collapsing, accent/diacritic folding, case folding.
- `DisambiguationServiceTests`: `sitelinks` ranking and *"Did you mean...?"* threshold selection.
- `InMemoryWindowCalculatorTests`: `MM * 100 + DD` anniversary key generation across month, year-end, and leap-year boundaries.
- `PersonRecordTests`: `IsDead` status and age calculation.

---

## 2. Running Load Tests (`k6`)

[`load/load_test.js`](load/load_test.js) simulates realistic mixed traffic:
- **70%** hot celebrity searches (exercises `HybridCache` L1 + Redis)
- **20%** randomized long-tail/cache-miss searches (exercises PostgreSQL B-tree & `pg_trgm` indexes)
- **10%** landing-page queries (`/api/v1/recent-deaths`, `/api/v1/in-memory`)

### Against Local Docker Compose
```bash
docker run --rm -i --network host \
  -e BASE_URL=http://localhost:8080 \
  grafana/k6 run - < tests/load/load_test.js
```

### Against Azure AKS (or any remote URL)
```bash
# Ramp test (25 -> 75 -> 150 VUs over 90s)
docker run --rm -i \
  -e BASE_URL=http://<EXTERNAL_IP> \
  grafana/k6 run - < tests/load/load_test.js

# Spike test (burst to 250 VUs)
docker run --rm -i \
  -e BASE_URL=http://<EXTERNAL_IP> \
  -e SCENARIO=spike \
  grafana/k6 run - < tests/load/load_test.js
```

---

## 3. Rate Limiting & Query Protection Implementation

Initial load testing showed that while cached queries ran in **`0 ms`** server time, the **20% randomized cache-miss queries** triggered `pg_trgm` fuzzy scans across 2.29M rows, saturating a burstable PostgreSQL instance (`p(95) = 21.21 s` under a 250-VU spike).

To protect the database and keep p95 latency under 100ms, two layers were added:

1. **Per-IP Sliding-Window Rate Limiter ([`src/Api/Program.cs`](../src/Api/Program.cs) & [`src/Api/appsettings.json`](../src/Api/appsettings.json)):**
   - Uses `UseForwardedHeaders()` (`X-Forwarded-For` / `X-Forwarded-Proto`) so rate limiting keys on the real client IP behind NGINX / `ingress-nginx`.
   - Applies ASP.NET Core's `AddRateLimiter` (`"api-per-ip"` sliding-window policy: `PermitLimit: 20` requests per `WindowSeconds: 10` per pod, `QueueLimit: 0`) to `/api/v1/*`.
   - Leaves `/health/live` and `/health/ready` unthrottled so Kubernetes probes are never rejected.
   - Rejected requests receive `HTTP 429 Too Many Requests` with a `Retry-After` header and JSON error payload.

2. **Bounded Fuzzy Fallback ([`src/Infrastructure/Repositories/PostgresPersonRepository.cs`](../src/Infrastructure/Repositories/PostgresPersonRepository.cs)):**
   - Skips `pg_trgm` fallback for queries shorter than 3 characters.
   - Runs fuzzy fallback inside a scoped transaction with `SET LOCAL pg_trgm.similarity_threshold = 0.55` and `SET LOCAL statement_timeout = '1500ms'`.
   - Catches `PostgresErrorCodes.QueryCanceled` (`57014`) and gracefully returns an empty result instead of `HTTP 500`.

---

## 4. Observed Benchmark Results (Azure AKS, 250-VU Spike Test)

Tested against Azure AKS (`Standard_D2s_v6` nodes) + Azure Database for PostgreSQL Flexible Server (`B_Standard_B2s`) + Azure Managed Redis (`Balanced_B0`) with 2.29M people loaded:

| Metric | Before (No Rate Limit / Unbounded Fuzzy) | After (Rate Limit + 1.5s Fuzzy Cap) |
| :--- | :--- | :--- |
| **Throughput** | `55.0 req/s` (`3,681` requests) | **`1,013.6 req/s`** (`59,922` requests) |
| **`http_req_duration` p(95)** | `21.21 s` | **`71.4 ms`** |
| **Server `api_query_time_ms` avg** | `2,748 ms` (max `33,395 ms`) | **`32.4 ms`** (max `689 ms`) |
| **Server `api_query_time_ms` median** | `0 ms` | **`0 ms`** |
| **`http_429_rate`** | `0.00%` | **`99.26%`** (flood shed cleanly) |

*(Note: `k6` counts `429` responses under `http_req_failed` unless `http.expectedStatuses(200, 429)` is configured; shedding excess single-IP flood traffic with `429` while keeping `p(95)` at `71.4 ms` and `0` server errors is the intended behavior.)*
