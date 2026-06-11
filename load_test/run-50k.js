// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// k6 load test: POST /api/send with N concurrent virtual users.
// Mirrors load_test/run-50k.js from the TS repo, scaled down for PoC.
//
// Usage:
//   set API_URL=https://<aks-lb-ip>/api/send
//   set API_KEY=<your-x-api-key>
//   set REPEAT=1
//   k6 run load_test/run-50k.js

import http from 'k6/http';
import { check, sleep } from 'k6';

const API_URL = __ENV.API_URL || 'http://localhost:8080/api/send';
const API_KEY = __ENV.API_KEY || '';
const REPEAT = parseInt(__ENV.REPEAT || '1', 10);

export const options = {
  scenarios: {
    burst: {
      executor: 'shared-iterations',
      vus: parseInt(__ENV.VUS || '1', 10),
      iterations: parseInt(__ENV.ITERATIONS || '1', 10),
      maxDuration: '5m',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<3000'],
  },
};

export default function () {
  const payload = JSON.stringify({
    message: `📢 k6 broadcast at ${new Date().toISOString()}`,
    repeat: REPEAT,
  });

  const res = http.post(API_URL, payload, {
    headers: {
      'Content-Type': 'application/json',
      'x-api-key': API_KEY,
    },
    timeout: '120s',
  });

  check(res, {
    'status 202': (r) => r.status === 202,
    'has jobId': (r) => {
      try {
        return JSON.parse(r.body).jobId !== undefined;
      } catch {
        return false;
      }
    },
  });

  if (res.status === 202) {
    const job = JSON.parse(res.body);
    console.log(`Job ${job.jobId} enqueued ${job.enqueued} msgs (total=${job.total})`);
  } else {
    console.error(`POST /api/send failed: ${res.status} ${res.body}`);
  }

  sleep(1);
}
