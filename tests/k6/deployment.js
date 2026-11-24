import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const responses = new Counter('target_responses');
const httpErrors = new Counter('target_http_errors');
const failedChecks = new Counter('target_failed_checks');
export const options = {
  hosts: { [__ENV.TARGET_HOST]: `127.0.0.1:${__ENV.TUNNEL_PORT}` },
  scenarios: {
    deployment: {
      executor: 'constant-vus',
      vus: Number(__ENV.USERS),
      duration: `${__ENV.MAX_SECONDS}s`,
      gracefulStop: '5s',
    },
  },
};

export default function () {
  const r = http.get(__ENV.TARGET_URL, { timeout: '5s', redirects: 0 });
  const checks = {
    'HTTP 200': r => r.status === 200,
    'expected body': r => r.body === 'Hello World!',
    'version present': r => Boolean(r.headers['X-App-Version']),
    'pod present': r => Boolean(r.headers['X-Pod-Name']),
  };
  check(r, checks);
  httpErrors.add(r.status < 200 || r.status >= 400 ? 1 : 0);
  failedChecks.add(Object.values(checks).filter(fn => !fn(r)).length);
  responses.add(1, {
    version: r.headers['X-App-Version'] || 'unknown',
    pod: r.headers['X-Pod-Name'] || 'unknown',
    status: String(r.status),
  });
}

export function handleSummary(data) {
  return { [__ENV.SUMMARY_PATH]: JSON.stringify(data) };
}
