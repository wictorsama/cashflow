// Steady background traffic while a failure is injected by scripts/resilience-test.ps1.
// It only measures; the script reports the failures per kind of request.
import { readBalance, writeEntry } from './common.js';

const duration = __ENV.DURATION || '90s';

export const options = {
  scenarios: {
    reads: { executor: 'constant-arrival-rate', exec: 'reads', rate: 30, timeUnit: '1s', duration, preAllocatedVUs: 50, maxVUs: 200 },
    writes: { executor: 'constant-arrival-rate', exec: 'writes', rate: 10, timeUnit: '1s', duration, preAllocatedVUs: 20, maxVUs: 100 },
  },
  // k6 only records per-request-kind metrics for names used in a threshold; these always pass and just enable the breakdown.
  thresholds: {
    'http_req_failed{name:read-balance}': ['rate>=0'],
    'http_req_failed{name:write-entry}': ['rate>=0'],
    'http_req_duration{name:read-balance}': ['max>=0'],
    'http_req_duration{name:write-entry}': ['max>=0'],
  },
};

export function reads() { readBalance(); }
export function writes() { writeEntry(); }
