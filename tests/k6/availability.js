import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const responses = new Counter('target_responses');

export const options = {
  scenarios: {
    availability: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.RATE || 10),
      timeUnit: '1s',
      duration: __ENV.DURATION || '5m',
      preAllocatedVUs: 20,
      maxVUs: 100,
      gracefulStop: '10s',
    },
  },
  thresholds: {
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
    http_req_duration: ['p(95)<500'],
    dropped_iterations: ['count==0'],
  },
};

export default function () {
  const response = http.get(__ENV.TARGET_URL || 'http://target.test/', {
    timeout: '5s',
    redirects: 0,
  });
  check(response, {
    'HTTP 200': (r) => r.status === 200,
    'expected body': (r) => r.body === 'Hello World!',
    'version present': (r) => Boolean(r.headers['X-App-Version']),
    'pod present': (r) => Boolean(r.headers['X-Pod-Name']),
  });
  responses.add(1, {
    version: response.headers['X-App-Version'] || 'unknown',
    pod: response.headers['X-Pod-Name'] || 'unknown',
    status: String(response.status),
  });
}
