import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

// All settings come from the environment, so the same file runs the pilot and the measured run.
const API_URL = __ENV.API_URL || 'http://api.hba.internal:8080';
// Fixed reference that defines the load, not the capacity at run time: what 6 on-prem replicas
// finish (measured in pilot 1, docs/loadtest.md §2). The same in every run, bursting or not.
const ONPREM_CAPACITY_RPS = Number(__ENV.ONPREM_CAPACITY_RPS || 2.3);
// 1 = full run (9 min of k6, the queue drains after it ends); 0.1 = ten times shorter, a smoke run
const STAGE_SCALE = Number(__ENV.STAGE_SCALE || 1);

const PRESET = 'medium';   // the calibrated, frozen preset
const IMAGES = 7;          // src/img-01.jpg .. img-07.jpg (source set v2)

// k6 wants whole numbers for a rate, so it is counted per 10 s: 0.6 x 2.3 jobs/s = 14 per 10 s
const rate = (multiple) => Math.round(ONPREM_CAPACITY_RPS * multiple * 10);
const duration = (minutes) => `${Math.max(1, Math.round(minutes * 60 * STAGE_SCALE))}s`;

export const options = {
  scenarios: {
    baseline: {
      // Open model: jobs arrive at this rate whatever the cluster does, like real clients
      executor: 'ramping-arrival-rate',
      startRate: rate(0.6),
      timeUnit: '10s',
      preAllocatedVUs: 20,
      maxVUs: 100,
      stages: [
        { target: rate(0.6), duration: duration(2) },  // warm-up: starts on 1 replica, so the queue builds until the autoscaler has scaled up
        { target: rate(1.6), duration: duration(2) },  // ramp up through capacity
        { target: rate(1.6), duration: duration(5) },  // overload: queue grows, on-prem plateaus
        { target: 0, duration: '1s' },                 // stop submitting
        { target: 0, duration: duration(3) },          // stay open while the queue drains
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    // accepting a job is a Redis push: a loose bound that still catches a broken API
    'http_req_duration{name:post_job}': ['p(95)<500'],
    // k6 itself must keep the rate, or the generator and not the cluster set the load
    dropped_iterations: ['count==0'],
  },
};

// Runs once: stop before ramping up against an API that is not there
export function setup() {
  const res = http.get(`${API_URL}/healthz/ready`);
  if (res.status !== 200) {
    throw new Error(`API not ready at ${API_URL}: HTTP ${res.status}`);
  }
}

// One iteration = one job
export default function () {
  // Global counter across all VUs, so the images rotate evenly like in the calibration
  const n = (exec.scenario.iterationInTest % IMAGES) + 1;
  const body = JSON.stringify({ input_key: `src/img-0${n}.jpg`, preset: PRESET });

  const res = http.post(`${API_URL}/jobs`, body, {
    headers: { 'Content-Type': 'application/json' },
    tags: { name: 'post_job' },   // fixed name, so the metrics do not split per request
  });

  check(res, { 'accepted (202)': (r) => r.status === 202 });
}
