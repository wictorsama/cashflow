// RNF-02: the consolidated balance takes 50 requests/second for 5 minutes with at most 5% loss
// (project target: under 1%). Entries are written at the same time, so the cache is also being invalidated.
import { readBalance, writeEntry } from './common.js';

export const options = {
  scenarios: {
    reads: {
      executor: 'constant-arrival-rate', exec: 'reads',
      rate: 50, timeUnit: '1s', duration: '5m', preAllocatedVUs: 50, maxVUs: 200,
    },
    writes: {
      executor: 'constant-arrival-rate', exec: 'writes',
      rate: 5, timeUnit: '1s', duration: '5m', preAllocatedVUs: 10, maxVUs: 50,
    },
  },
  thresholds: {
    'http_req_failed{name:read-balance}': ['rate<0.01'],            // project target (the challenge allows 5%)
    'http_req_duration{name:read-balance}': ['p(95)<500', 'p(99)<1000'], // RNF-03
    'http_req_failed{name:write-entry}': ['rate<0.01'],
    'http_req_duration{name:write-entry}': ['p(95)<500'],
    dropped_iterations: ['count<1'],                                 // k6 could not keep the arrival rate
  },
};

export function reads() { readBalance(); }
export function writes() { writeEntry(); }
