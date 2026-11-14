# Load Testing Simulator (`k6`)

Simulates realistic mixed traffic against the API to measure p50/p95/p99 latency, throughput (RPS), cache effectiveness, and whether rate limiting is needed:
- **70%** hot celebrity searches (tests `HybridCache` L1 + Redis)
- **20%** randomized long-tail/cache-miss searches (tests PostgreSQL + index latency under load)
- **10%** landing-page endpoints (`/api/v1/recent-deaths`, `/api/v1/in-memory`)

## Run Against Local Docker Compose
```bash
docker run --rm -i --network host \
  -e BASE_URL=http://localhost:8080 \
  grafana/k6 run - < tests/load/load_test.js
```

## Run Against Azure AKS
```bash
docker run --rm -i \
  -e BASE_URL=http://<EXTERNAL_IP> \
  grafana/k6 run - < tests/load/load_test.js
```

## Run the Spike / Burst Scenario (250 VUs)
```bash
docker run --rm -i \
  -e BASE_URL=http://<EXTERNAL_IP> \
  -e SCENARIO=spike \
  grafana/k6 run - < tests/load/load_test.js
```
