# Load Test: the k6 Script and the First Pilot

**Status:** 2026-10-24. The k6 script (`loadtest/k6/baseline.js`) works. Three full runs ("pilot 1" to "pilot 3") went from a laptop over Tailscale against the on-prem Swarm, without bursting. They agree on job time and capacity (2.3 jobs/s). Pilots 1 and 2 showed an autoscaler oscillation in the warm-up; after the scale-down rule was changed to a 300 s stabilization window, pilot 3 shows none and is the Swarm on-prem reference (section 4). The load generator is the laptop, not an AWS host ([ADR 0008](adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)). Two short experiments on 2026-11-08 added one AWS burst node (section 4.1). Burst run 1 on 2026-11-16 used the burst controller with the full profile (section 4.2): 44 % more throughput, with one task left Pending by Swarm. The run script that writes Grafana annotations, burst runs 2 and 3, two more on-prem runs and the Kubernetes runs are not done yet (section 5).

**Audience:** anyone running or changing the load test, or reading the results in the report.

**Related:** [docs/app.md](app.md) §5 (the frozen `medium` preset and image set), [docs/autoscaler.md](autoscaler.md) (the controller under test), [docs/observability.md](observability.md) §6 (the run dashboard), [docs/csharp-workload-v1.2.md](csharp-workload-v1.2.md) §5.1 (`POST /jobs`).

---

## 1. What the script does

One `POST /jobs` per iteration, with the frozen preset `medium` and a source image from set v2, rotated evenly over `src/img-01.jpg` … `img-07.jpg`. Nothing else: it does not poll `GET /jobs/{id}`, because the worker metrics already record processing, and polling would add load of its own. k6 therefore measures only how fast the API **accepts** a job (a few milliseconds); the queue, replicas and job times come from Prometheus.

**An open model.** The script uses the `ramping-arrival-rate` executor: jobs arrive at a set rate whatever the cluster does, like independent clients. A closed model (a fixed number of virtual users, each waiting for its last request) would send less load as soon as the system slowed down, and the queue would never grow, which is the thing under test.

The API is reachable only from the tailnet and the VPC. There is no token (decision of 2026-10-23, [app.md](app.md) §5); the network layer is the access control.

## 2. The load profile (frozen)

The rates are multiples of a reference, the **on-prem capacity**: what 6 replicas can finish per second. It is set once and **does not follow the capacity of the cluster at run time**. Every run, with or without bursting and on either orchestrator, gets the same absolute load; otherwise adding burst nodes would raise the rates, and a difference between runs could not be told from a difference in load.

| Stage | Rate (× reference) | Duration | With the reference 2.3 jobs/s |
|---|---|---|---|
| warm-up | 0.6 | 2 min | 1.4 jobs/s |
| ramp | 0.6 → 1.6 | 2 min | up to 3.7 jobs/s |
| overload | 1.6 | 5 min | 3.7 jobs/s |
| stop | 0 | 1 s | |
| idle (never reached, see below) | 0 | 3 min | |

The whole profile is about 1,590 jobs (pilot 1 ran with the older reference 2.5 and sent 1,712) and takes **9 minutes**. k6 ends when it has sent its last job and does not wait for the idle stage, so the queue drains **after** k6 has finished (about 4 more minutes in pilot 1). A run therefore ends when the queue reads 0, not when k6 exits.

The reference is **2.3 jobs/s**, from pilot 1 (section 4): 6 replicas finish about 2.3 jobs/s, not the 2.54 that 6 ÷ 2.36 s (the single-replica calibration) would suggest. Pilot 1 itself still ran with the reference 2.5 (1.5 → 4.0 jobs/s); 2.3 applies from the next run. It is the same kind of fixed input as the preset version and the image set. Change it only with a new measurement, and then for all runs.

Two details of the profile:

- **The warm-up is not below capacity at first.** A run starts with one replica (0.42 jobs/s) and 1.4–1.5 jobs/s of arrivals, so the queue grows from the first job until the autoscaler has scaled up (46 s in pilot 1).
- **Rates are whole numbers per 10 s.** k6 accepts only integer rates, so the script counts per 10 seconds: 0.6 × 2.3 jobs/s becomes 14 per 10 s.

## 3. Running it

```powershell
k6 run --summary-export=loadtest/results/<run>.json loadtest/k6/baseline.js
```

| Environment variable | Default | Meaning |
|---|---|---|
| `API_URL` | `http://api.hba.internal:8080` | the cluster VIP, resolvable on the tailnet |
| `ONPREM_CAPACITY_RPS` | 2.3 | the reference above; do not change between runs |
| `STAGE_SCALE` | 1 | `0.1` shortens every stage tenfold, for a smoke run (about 1 minute, 173 jobs) |

`STAGE_SCALE` stays set in a PowerShell session until `Remove-Item Env:STAGE_SCALE`; a forgotten value gives a smoke run instead of the full one. `k6` does not create the `loadtest/results/` folder.

**Before a run:** queue at 0, `hba_worker` at 1 replica, `autoscaler_controller` running, no burst node in the swarm, the worker image still `sha-5de4c05`, nothing deploying from CI, and the machine running k6 awake for the whole run.

**k6 thresholds** (the script's pass/fail rules): `http_req_failed` under 1 %, `checks` above 99 % (every response a 202), p95 of the acceptance time under 500 ms, and `dropped_iterations` equal to 0. The last one matters most: a dropped iteration means k6 could not keep the rate, so the generator, not the cluster, set the load.

**Where it runs, and when a run counts.** k6 runs from the author's laptop on the tailnet, outside the cluster ([ADR 0008](adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)). A run is valid only if `dropped_iterations` is 0, no request failed, the laptop was on mains power with sleep off, on the same network as the other runs, and nothing heavy ran beside it. Otherwise the run is repeated.

## 4. Pilots 1 to 3 (2026-10-24, laptop, on-prem only)

Three full runs on the same day, from the laptop, with the autoscaler between 1 and 6 replicas and no bursting. **Pilot 1** used the reference 2.5 jobs/s (arrivals 1.5 → 4.0 jobs/s); **pilots 2 and 3** used the frozen reference 2.3 (1.4 → 3.7 jobs/s). Pilots 1 and 2 ran with the 60 s scale-down cooldown; **pilot 3 ran with the 300 s scale-down stabilization window** that finding 3 led to ([autoscaler.md](autoscaler.md) §4), and is the Swarm on-prem reference. The run windows (12:31:24–12:40:25, 13:06:22–13:15:23 and 14:50:59–15:00:00 UTC) are derived from the queue peak, because k6 ends with its last submission. Queue and replica figures are from Prometheus; the k6 figures are from `loadtest/results/pilot-1.json` to `pilot-3.json`.

| | Pilot 1 (ref. 2.5, cooldown) | Pilot 2 (ref. 2.3, cooldown) | Pilot 3 (ref. 2.3, stabilization) |
|---|---|---|---|
| Jobs sent (failed, dropped) | 1,712 (0, 0) | 1,585 (0, 0) | 1,585 (0, 0) |
| Acceptance time, median / p95 / max | 7.2 / 9.2 / 141 ms | 7.2 / 9.6 / 210 ms | 7.2 / 10.2 / 153 ms |
| Replicas dipped below 6 during the run | not checked | **yes, 6 → 1 → 6** | **no** |
| Queue peak | 593 | 496 | **448** |
| Time from k6 start to 6 replicas running | 46 s | 51 s | 51 s |
| Drain: peak to empty queue | 4 min 15 s | 3 min 30 s | 3 min 10 s |
| k6 start to empty queue | 13.3 min | 12.5 min | 12.2 min |
| k6 start to the first scale-down after the queue emptied | 13.4 min | 12.6 min | 17.2 min (6 → 1 in one step) |
| Job duration while saturated, median / p95 | 2.61 / 2.98 s | 2.61 / 2.98 s | 2.60 / 2.97 s |
| Throughput while saturated (replicas all 6) | not clean (dip) | 2.35 jobs/s | 2.36 jobs/s |
| Throughput during the drain | 2.37 jobs/s | 2.36 jobs/s | 2.35 jobs/s |
| Queue wait, median (rough, see below) | 106 s | 90 s | 70 s |
| Failed jobs | none | none | none |

CPU steal was 0 on every node in all runs. While the cluster was saturated the workers were 97.7 % busy in pilot 2 (13:09:00–13:15:23, all six replicas working) and **99 %** busy in pilot 3 (14:54:15–15:00:00); `swarm-ctl` stayed at 3 %, `obs-01` at 7–8 % and `ts-gateway` at 0.8 %. The API was never a bottleneck.

**Finding 1: the capacity is 2.3 jobs/s, not 2.54.** When the cluster is really saturated, six replicas finish 2.35 jobs/s (pilot 2: 898 jobs in the 383 s from 13:09:00 to the peak, with a mean job time of 2.56 s, and 6 ÷ 2.56 = 2.34). The drain after k6 stopped gives the same 2.35–2.37 jobs/s in all three pilots, and pilot 3, whose replicas never dipped, measures 2.36 jobs/s while saturated. The single-replica calibration (6 ÷ 2.36 s = 2.54) overstates it. This is the reference in section 2.

**Finding 2: parallel jobs are slower.** The median job took **2.61 s** (p95 2.98 s) against 2.36 s measured with one replica at a time: +10.6 %, and identical in both pilots. Six busy vCPUs share caches, memory bandwidth and SMT siblings on one host (a likely cause; not isolated).

**Finding 3: the autoscaler oscillates in the warm-up** (pilot 2, 13:06–13:09 UTC). The queue filled while one replica worked, desired replicas went to 6 at 13:07:00, the queue emptied at 13:08:00, desired fell to 1 and the scheduled replicas to 1 by 13:08:30, and the queue rose again to bring desired back to 6 by 13:09:00. At 1.4 jobs/s the arrivals lie between the capacity of one replica (0.42 jobs/s) and of six (2.3 jobs/s), so the rule `ceil(depth / 5)` builds up and empties the queue in turn, and the 60 s scale-down cooldown is shorter than the cycle. It cost about 45 s with fewer than six replicas and is part of why the queue peaked at 496, not the 420–450 expected. Pilot 1 may show the same; it has not been checked. It is a point for the comparison with KEDA, whose HPA waits 300 s before scaling down by default.

**Finding 3, fixed in pilot 3.** The controller now scales down only to the highest `desired` of the last 300 s (PRs #61 and #58), the rule of the HPA's scale-down stabilization window. In the same warm-up, `desired` fell back to 1 at 14:52:30 while the queue was empty, but the scheduled replicas stayed at 6 from 14:52:00 to 15:08:00: one scale-up, no dip. The queue peaked at 448, in line with the 420–450 expected without the oscillation (496 in pilot 2). After the queue emptied at 15:03:10 the replicas stayed at 6 for exactly 300 s and then dropped to 1 in one step at 15:08:10. The price is five more minutes at six replicas after every run: 17 minutes from k6 start to back at one replica, against 12–13.

**Correction.** A first reading of pilot 1 said a further 6 % was lost between jobs (throughput 2.16 jobs/s, CPU 93 %). That was wrong: the window it used began when six replicas first ran and so included the oscillation above, with fewer than six busy. In the truly saturated window all six replicas are busy, at 97.7 % CPU.

**Queue wait.** The last job submitted waits about the queue peak divided by the capacity: 496 ÷ 2.35 ≈ 3.5 minutes in pilot 2 (593 ÷ 2.35 ≈ 4.2 minutes in pilot 1). The median and p95 in Prometheus (106 / 280 s and 90 / 276 s) come from histogram buckets that are coarse above 60 s, and the p95 values exceed that physical maximum, so treat them as rough.

**Limits.** Three runs on one day under the same conditions: job time (2.60–2.61 s) and drain rate (2.35–2.37 jobs/s) agree to the second digit, and the differences between the runs are in the queue shape, which follows the autoscaler policy. Three runs, two of them with another policy, are too few for a confidence interval. They came from a laptop on a home line, which is now the setup ([ADR 0008](adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)); the validity checks in section 3 were met in all. The windows are derived from the queue peak, not from run annotations, and the queries behind these numbers are not committed yet.

## 4.1 Quick burst runs (2026-11-08, one burst node, experiments)

Two short runs to see whether a burst node adds real capacity, and whether the instance type is right. **These are experiments, not measured runs:** the autoscaler was paused with `hba_worker` fixed at 8 replicas (6 on-prem, 2 on one burst node launched by hand), and the profile was shortened (`STAGE_SCALE=0.33`: 526 jobs in 3 minutes, peak 3.7 jobs/s). Everything else was unchanged: the worker image `sha-5de4c05`, image set v2, the `medium` preset and k6 from the laptop. Each run sent 526 jobs with 0 failed and 0 dropped iterations. Summaries: `loadtest/results/quick-burst-1.json` and `quick-burst-2.json`; the per-tier numbers come from Prometheus.

| | Run 1: `c6a.large` (21:17 UTC) | Run 2: `c7a.large` (21:40 UTC) |
|---|---|---|
| Physical cores on the node | 1 (2 threads) | 2 |
| Saturated throughput, on-prem (6 replicas) | 2.36 jobs/s | 2.40 jobs/s |
| Saturated throughput, AWS (2 replicas) | 0.44 jobs/s | 0.72 jobs/s |
| Total | 2.80 jobs/s | 3.12 jobs/s |
| Per replica, AWS / on-prem | 0.22 / 0.39 jobs/s | 0.36 / 0.40 jobs/s |
| Median job, AWS / on-prem | 4.48 / 2.60 s | 2.78 / 2.53 s |
| Mean processing step, AWS / on-prem | 4.36 / 2.51 s | 2.63 / 2.47 s |
| Queue peak | 98 | 63 |
| Queue empty after k6 stopped | 51 s | 31 s |

Saturated throughput is the rate over the 30 s windows between about t+100 s and t+200 s.

**Finding 1: a burst node adds real capacity.** On-prem stayed at its 2.36–2.40 jobs/s plateau (pilot 3: 2.36), so the burst node didn't disturb it, and the total rose by the node's own throughput. With `c7a.large` the 8 replicas reached 3.12 jobs/s, 97 % of the 3.2 that 8 on-prem replicas would give.

**Finding 2: vCPU count isn't core count.** `c6a.large` was chosen for its 2 vCPUs, but `lscpu` on the node shows 1 core with 2 threads, so its two replicas shared a core: 74 % longer per job, and a 19 % lift from 33 % more replicas. On `c7a.large` (2 physical cores) a job is about 10 % slower than on-prem. The network is not the cause in either run (download and upload are under 6 % of the job on AWS). Both types report the same .NET version and 256-bit vectors in `hba_worker_info`, though `c7a` also has AVX-512. The remaining 10 % is unexplained; a lower clock and the distance to the queue are candidates.

**Finding 3: cost per job.** At the Frankfurt on-demand prices ($0.0873/h and $0.1171/h), `c7a.large` costs 34 % more per hour but 18 % less per job at saturation, because each node finishes 64 % more jobs.

**Time to capacity:** see [burst.md](burst.md) §9 (76 s from terminating a node to two workers on its replacement, one sample).

**Limits.** One short run per instance type, with fixed replicas and no autoscaler, so neither the scale-up behaviour nor the frozen 9-minute profile was tested. Run 1 and run 2 were 22 minutes apart on the same evening.

## 4.2 Burst run 1 (2026-11-16, Swarm, controller-driven)

The first full run with automatic bursting: the frozen 9-minute profile, the replica controller between 1 and 10 replicas (`max_replicas: 10`), and the burst controller adding and removing up to two `c7a.large` nodes on its own ([burst.md](burst.md)). Everything else as in pilot 3: worker image `sha-5de4c05`, image set v2, preset `medium`, k6 from the laptop. k6 ran 16:14:51–16:23:52 UTC (t = 0 at k6 start). Summary: `loadtest/results/swarm-burst-1.json`; the other figures are from Prometheus, the burst controller's log and the Auto Scaling group's activity history.

**Validity.** 1,585 jobs sent, 0 failed, 0 dropped iterations, so the cluster received the frozen load. Two of the §3 conditions were not met: the laptop ran on battery and on a mobile hotspot, not the network of the pilots. Acceptance times were higher (p95 32 ms, max 2.4 s, against 10 ms and 153 ms in pilot 3), but the open model sends at the set rate regardless, and none of the cluster figures below depend on the laptop's network.

| | Pilot 3 (on-prem, max 6) | Burst run 1 (max 10, 2 burst nodes) |
|---|---|---|
| Jobs sent (failed, dropped) | 1,585 (0, 0) | 1,585 (0, 0) |
| Queue peak | 448 | **167** (t+535 s) |
| Drain: peak to empty queue | 3 min 10 s | **60 s** |
| k6 start to empty queue | 12.2 min | **9.9 min** |
| Throughput while overloaded | 2.36 jobs/s (6 replicas) | **3.40 jobs/s** (9 replicas: on-prem 2.33, AWS 1.07) |
| Throughput during the drain | 2.35 jobs/s | 3.38 jobs/s |
| Job duration, median / p95 | 2.60 / 2.97 s | on-prem 2.63 / 2.98 s, AWS 2.77 / 3.53 s |
| Replicas dipped during the run | no | no |
| Queue wait, median (rough) | 70 s | 31 s |
| Burst instance time (cost) | none | 29.0 instance-minutes (about $0.06) |

On-prem CPU was 99.4–99.5 % busy while overloaded, steal stayed at 0, `swarm-ctl` peaked at 6.5 % and `ts-gateway` at 8 % (0.8 % in the pilots: it now carries the burst nodes' traffic).

**Timeline.**

| UTC | t | Event |
|---|---|---|
| 16:15:34 | +43 s | First worker task Pending (replicas 6 → 7 in the warm-up) |
| 16:15:44 | +53 s | Pending for 10 s: desired capacity 0 → 1 (AWS launch at 16:15:49) |
| 16:17:21 | +150 s | First job finished on AWS: **107 s from the first Pending task** |
| 16:19:03–16:19:30 | +252–279 s | Replicas 7 → 10; 3 tasks Pending, one fits on the first node |
| 16:19:19 | +268 s | Desired capacity 1 → 2 (launch at 16:19:28); the node is ready at 16:20:51 |
| 16:23:46 | +535 s | Queue peak, 167 |
| 16:24:46 | +595 s | Queue empty |
| 16:29:50 | +899 s | Replicas 10 → 1, after the 300 s stabilization window |
| 16:31:45–16:32:36 | | Both nodes drained, terminated and removed, one after the other |

The nodes joined the swarm 68 s and 77 s after launch, in line with the 76 s measured on 2026-11-08 ([burst.md](burst.md) §9).

**Finding 1: bursting raised throughput by 44 % and cut the queue peak by 63 %.** On-prem held its plateau (2.33 jobs/s against 2.36 in pilot 3), so the burst nodes added capacity without disturbing it. 3.40 jobs/s is exactly what 9 replicas predict: 2.33 + 3 × 0.357, the per-replica rate of `c7a.large` measured in §4.1. It is still below the 3.7 jobs/s arrival rate, which is why the queue kept growing to 167 instead of levelling off.

**Finding 2: Swarm left one task Pending for the whole overload, so the burst ran with 9 workers instead of 10.** At 16:19:03 the task `hba_worker.9` failed to schedule (`no suitable node (insufficient resources on 4 nodes; scheduling constraints not satisfied on 1 node)`). When the second node joined at 16:20:51, Swarm placed the other Pending task there but not this one, although the node still had one free CPU, and the task's status was never updated again. The scheduler didn't retry it after the first failure. The burst controller saw the task (Pending 1 until the end) but could not add a third node, because `max_nodes` is 2. Two minutes into the overload, one of the four AWS replicas was missing, about 0.36 jobs/s. The cause inside Swarm is not isolated. Whether the Kubernetes scheduler retries in the same situation is a point for the Kubernetes runs.

**Finding 3: the warm-up alone triggered the first node.** At 1.4 jobs/s the queue grew while one replica worked, and the replica controller asked for 7 replicas (ceil(depth / 5)) at t+40 s. The seventh task found no room on-prem, so a node was launched in the warm-up, when on-prem alone would have coped. The node arrived in time for the overload, but it was paid for from the first minute.

**Finding 4: half of the burst cost came after the queue was empty.** Of the 29 instance-minutes, 14.7 fell after 16:24:46: the replicas stay at 10 for the 300 s stabilization window, then a node must be unneeded for 120 s before it is drained. Both rules are there against flapping ([autoscaler.md](autoscaler.md) §4, [burst.md](burst.md) §4); this is their price in a burst.

**Limits.** One run. The battery and the hotspot break two of the §3 conditions. With 9 workers instead of 10, this run does not show the designed burst capacity; run 2 and 3 decide whether finding 2 is repeatable.

## 5. Still to do

1. **k6 → Prometheus remote write** ([observability.md](observability.md) §10), so the offered rate sits on the same timeline as the queue.
2. **A run script** that writes the `hba-run` Grafana annotations at the start and end, waits for the queue to reach 0, and exports the window with `query_range` ([observability.md](observability.md) §6).
3. **Pilot 3 is the Swarm on-prem reference.** Repeat it only if the policy, the presets or the profile change.
4. **Burst runs 2 and 3,** then **two more on-prem runs** with `max_replicas` back at 6, so each condition has three runs (pilot 3 is the first on-prem one). Burst nodes still have no host metrics (no node-exporter on them), so the CPU panels miss them.
5. **Kubernetes.** Measure its on-prem capacity separately and report it, but drive it with the same absolute load.

## Sources

- k6: [ramping-arrival-rate](https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/ramping-arrival-rate/), [open and closed models](https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/open-vs-closed/), [thresholds](https://grafana.com/docs/k6/latest/using-k6/thresholds/), [k6/execution](https://grafana.com/docs/k6/latest/javascript-api/k6-execution/)
