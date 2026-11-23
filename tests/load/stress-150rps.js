// Margin test: ramps the balance reads from 50 to 150 requests/second to show how far the system is from its limit.
import { readBalance, writeEntry } from './common.js';

export const options = {
  scenarios: {
    reads: {
      executor: 'ramping-arrival-rate', exec: 'reads', startRate: 50, timeUnit: '1s',
      preAllocatedVUs: 100, maxVUs: 400,
      stages: [
        { target: 50, duration: '30s' },
        { target: 100, duration: '1m' },
        { target: 150, duration: '1m' },
        { target: 150, duration: '2m' },
        { target: 50, duration: '30s' },
      ],
    },
    writes: {
      executor: 'constant-arrival-rate', exec: 'writes',
      rate: 10, timeUnit: '1s', duration: '5m', preAllocatedVUs: 20, maxVUs: 100,
    },
  },
  thresholds: {
    'http_req_failed{name:read-balance}': ['rate<0.05'],
    'http_req_duration{name:read-balance}': ['p(95)<1000'],
    'http_req_failed{name:write-entry}': ['rate<0.01'],
  },
};

export function reads() { readBalance(); }
export function writes() { writeEntry(); }
