# 4. Load Testing Methodology

## 4.1 Workload & Test Scenarios

**The workload** is a queue-based image service in C# (.NET 10): `POST /jobs` puts a job on a Redis list and returns `202` at once; workers pull one job at a time, resize and encode an image (ImageSharp, preset `medium`, a fixed set of 7 source images) and store the result in RustFS on-prem or S3 on AWS. One job takes **2.36 s** on one idle replica and about 2.6 s with all six busy. A worker uses one CPU, so the on-prem tier saturates at a known, measurable capacity.

**One frozen load profile for every run** ([loadtest.md](../loadtest.md) §2). Rates are multiples of the measured on-prem capacity, **2.3 jobs/s**, and stay fixed whatever the cluster under test can do:

| Stage | Rate | Duration |
|---|---|---|
| warm-up | 0.6 × = 1.4 jobs/s | 2 min |
| ramp | → 1.6 × = 3.7 jobs/s | 2 min |
| overload | 1.6 × = 3.7 jobs/s | 5 min |

That is 1,585 jobs in 9 minutes. The overload is 60 % above on-prem capacity, so on-prem alone must fall behind, and bursting (10 replicas) must be able to catch up.

**Scenarios, three runs each:**

| | Swarm | Kubernetes |
|---|---|---|
| On-prem only (max 6 replicas) | 3 runs | 3 runs |
| With bursting (max 10, up to 2 AWS nodes) | 3 runs | 3 runs |
| Isolation: KEDA reading Swarm's Prometheus signal | | 3 runs |
| KEDA and HPA at their defaults | | 1 run |

Everything else stayed frozen across all of them: images (worker `sha-5de4c05`), preset, image set, profile, VM sizes, scaling rule.

## 4.2 k6 Setup & Isolation

- **Open model:** k6's `ramping-arrival-rate` executor sends at the set rate whatever the cluster does, like independent clients. A closed model would send less as soon as the cluster slowed down, and the queue, the thing under test, would never grow.
- **Outside the system under test:** k6 runs on a laptop on the tailnet, never on a cluster VM ([ADR 0008](../adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)). It only submits jobs; it does not poll results, which would add load.
- **A run counts only if** k6 dropped no iteration (it kept the rate), no request failed, the laptop was on mains power with sleep off, and the run started with an empty queue and one worker. Every run reported here met these; two earlier burst runs on a mobile hotspot are reported separately in [loadtest.md](../loadtest.md) §4.2–4.3.

## 4.3 Metrics Collected

Everything lands in **one Prometheus** on `obs-01`, labelled by `cluster`, so both orchestrators are measured by the same store and the same queries ([observability.md](../observability.md)):

- **Queue length** (`redis_key_size`), the autoscaling signal;
- **replicas** desired, scheduled, ready and busy;
- **per job:** duration and queue wait, by `tier` (`onprem` / `aws`);
- **node CPU** busy and steal (oversubscription check);
- **burst:** the controller's Pending count and node events, and the Auto Scaling group's own launch and termination history.

**One script computes every figure for both orchestrators** (`analysis/run_metrics.py`), over windows derived from the k6 start and the queue itself. It reproduces the earlier hand-made Swarm figures, which makes the two columns comparable. Prometheus gives rates; counts come from k6 (1,585 accepted, 0 failed in every run) and, for the Kubernetes burst runs, from Redis (all 1,585 done).
