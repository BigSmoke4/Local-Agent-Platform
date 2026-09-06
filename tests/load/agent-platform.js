import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  scenarios: {
    api_read: { executor: 'constant-vus', vus: Number(__ENV.VUS || 20), duration: __ENV.DURATION || '30s' }
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<750']
  }
};

const base = __ENV.BASE_URL || 'http://localhost:8080';
const key = __ENV.API_KEY;

export default function () {
  const headers = { 'X-Api-Key': key };
  const health = http.get(`${base}/health/live`);
  check(health, { 'liveness 200': r => r.status === 200 });
  if (key) {
    const sessions = http.get(`${base}/api/agent/sessions`, { headers });
    check(sessions, { 'sessions 200': r => r.status === 200 });
    const telemetry = http.get(`${base}/api/telemetry/current`, { headers });
    check(telemetry, { 'telemetry 200': r => r.status === 200 });
  }
  sleep(0.2);
}
