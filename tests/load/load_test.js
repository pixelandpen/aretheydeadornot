import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Rate } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const SCENARIO = __ENV.SCENARIO || 'ramp';

const apiLatency = new Trend('api_query_time_ms');
const rateLimitedRate = new Rate('http_429_rate');

const HOT_NAMES = [
  'Michael Jackson',
  'Dolly Parton',
  'Nelson Mandela',
  'Taylor Swift',
  'Elvis Presley',
  'John Williams',
  'Queen Elizabeth II',
  'Betty White',
  'David Bowie',
  'Keanu Reeves',
];

const LONG_TAIL_NAMES = [
  'John Smith',
  'Mary Johnson',
  'Robert Brown',
  'James Davis',
  'Patricia Miller',
  'Michael Wilson',
  'Linda Moore',
  'William Taylor',
  'Elizabeth Anderson',
  'Richard Thomas',
];

export const options =
  SCENARIO === 'spike'
    ? {
        stages: [
          { duration: '10s', target: 20 },
          { duration: '20s', target: 250 },
          { duration: '20s', target: 250 },
          { duration: '10s', target: 0 },
        ],
        thresholds: {
          http_req_failed: ['rate<0.05'],
          http_req_duration: ['p(95)<250'],
        },
      }
    : {
        stages: [
          { duration: '15s', target: 25 },
          { duration: '30s', target: 75 },
          { duration: '30s', target: 150 },
          { duration: '15s', target: 0 },
        ],
        thresholds: {
          http_req_failed: ['rate<0.02'],
          http_req_duration: ['p(95)<100'],
        },
      };

export default function () {
  const roll = Math.random();
  let url;

  if (roll < 0.70) {
    const name = HOT_NAMES[Math.floor(Math.random() * HOT_NAMES.length)];
    url = `${BASE_URL}/api/v1/search?name=${encodeURIComponent(name)}`;
  } else if (roll < 0.90) {
    const base = LONG_TAIL_NAMES[Math.floor(Math.random() * LONG_TAIL_NAMES.length)];
    const suffix = Math.floor(Math.random() * 1000);
    url = `${BASE_URL}/api/v1/search?name=${encodeURIComponent(`${base} ${suffix}`)}`;
  } else if (roll < 0.95) {
    url = `${BASE_URL}/api/v1/recent-deaths?limit=8`;
  } else {
    url = `${BASE_URL}/api/v1/in-memory?days=3&limit=8`;
  }

  const res = http.get(url);

  rateLimitedRate.add(res.status === 429);

  check(res, {
    'status is 200': (r) => r.status === 200,
    'latency < 100ms': (r) => r.timings.duration < 100,
  });

  const serverMs = Number(res.headers['X-Query-Time-Ms']);
  if (!Number.isNaN(serverMs)) {
    apiLatency.add(serverMs);
  }

  sleep(0.1);
}
