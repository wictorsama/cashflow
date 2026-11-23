import http from 'k6/http';
import { check } from 'k6';

// Addresses as seen from where k6 runs (inside Docker, the host is host.docker.internal).
export const GATEWAY = __ENV.GATEWAY_URL || 'http://host.docker.internal:8080';
const KEYCLOAK = __ENV.KEYCLOAK_URL || 'http://host.docker.internal:8082';
const CLIENT_ID = __ENV.CLIENT_ID || 'merchant-integration';
const CLIENT_SECRET = __ENV.CLIENT_SECRET || 'local-dev-merchant-secret';

// Tokens live 5 minutes: each VU keeps its own and renews it a minute before it expires.
let token = null;
let tokenAt = 0;

export function authHeaders(extra = {}) {
  const now = Date.now();
  if (token === null || now - tokenAt > 240000) {
    const res = http.post(
      `${KEYCLOAK}/realms/cashflow/protocol/openid-connect/token`,
      { grant_type: 'client_credentials', client_id: CLIENT_ID, client_secret: CLIENT_SECRET },
      { tags: { name: 'token' } },
    );
    check(res, { 'token issued': (r) => r.status === 200 });
    token = res.json('access_token');
    tokenAt = now;
  }
  return { headers: { Authorization: `Bearer ${token}`, ...extra } };
}

// "Today" for the merchant is the Sao Paulo day (UTC-3).
export function today() {
  return new Date(Date.now() - 3 * 3600 * 1000).toISOString().slice(0, 10);
}

export function readBalance() {
  const res = http.get(`${GATEWAY}/v1/balances/${today()}`, { ...authHeaders(), tags: { name: 'read-balance' } });
  check(res, { 'balance 200': (r) => r.status === 200 });
  return res;
}

export function writeEntry() {
  const body = JSON.stringify({ type: Math.random() < 0.7 ? 'Credit' : 'Debit', amount: 1 + Math.floor(Math.random() * 500), entryDate: today(), description: 'load' });
  const key = `${__VU}-${__ITER}-${Date.now()}`;
  const res = http.post(`${GATEWAY}/v1/entries`, body, {
    ...authHeaders({ 'Content-Type': 'application/json', 'Idempotency-Key': key }),
    tags: { name: 'write-entry' },
  });
  check(res, { 'entry 201': (r) => r.status === 201 });
  return res;
}
