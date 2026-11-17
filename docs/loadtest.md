# Load Test: the k6 Script and the First Pilot

**Status:** 2026-11-16. The k6 script (`loadtest/k6/baseline.js`) works. Three full runs ("pilot 1" to "pilot 3") went from a laptop over Tailscale against the on-prem Swarm, without bursting. They agree on job time and capacity (2.3 jobs/s). Pilots 1 and 2 showed an autoscaler oscillation in the warm-up; after the scale-down rule was changed to a 300 s stabilization window, pilot 3 shows none and is the Swarm on-prem reference (section 4). The load generator is the laptop, not an AWS host ([ADR 0008](adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)). Two short experiments on 2026-11-08 added one AWS burst node (section 4.1). On 2026-11-16 two as-built burst runs found two problems, Swarm stranding Pending tasks and a boot that depended on the Snap Store (sections 4.2–4.4); both were fixed the same evening. Three measured burst runs and two more on-prem runs followed (sections 4.5 and 4.6): **bursting raises throughput from 2.33 to 3.80 jobs/s (+63 %) and cuts the queue peak from 459 to 127 (−72 %), for about 5 cents of AWS time per run.** The run script that writes Grafana annotations and the Kubernetes runs are not done yet (section 5).

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

## 4.2 As-built burst run 1 (2026-11-16, Swarm, before the fixes)

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

**Finding 2: Swarm left one task Pending for the whole overload, so the burst ran with 9 workers instead of 10.** At 16:19:03 the task `hba_worker.9` failed to schedule (`no suitable node (insufficient resources on 4 nodes; scheduling constraints not satisfied on 1 node)`). When the second node joined at 16:20:51, Swarm placed the other Pending task there but not this one, although the node still had one free CPU, and the task's status was never updated again. The scheduler didn't retry it after the first failure. The burst controller saw the task (Pending 1 until the end) but could not add a third node, because `max_nodes` is 2. Two minutes into the overload, one of the four AWS replicas was missing, about 0.36 jobs/s. The cause was found in Swarm's source the same evening (§4.4). Whether the Kubernetes scheduler retries in the same situation is a point for the Kubernetes runs.

**Finding 3: the warm-up alone triggered the first node.** At 1.4 jobs/s the queue grew while one replica worked, and the replica controller asked for 7 replicas (ceil(depth / 5)) at t+40 s. The seventh task found no room on-prem, so a node was launched in the warm-up, when on-prem alone would have coped. The node arrived in time for the overload, but it was paid for from the first minute.

**Finding 4: half of the burst cost came after the queue was empty.** Of the 29 instance-minutes, 14.7 fell after 16:24:46: the replicas stay at 10 for the 300 s stabilization window, then a node must be unneeded for 120 s before it is drained. Both rules are there against flapping ([autoscaler.md](autoscaler.md) §4, [burst.md](burst.md) §4); this is their price in a burst.

**Limits.** One run. The battery and the hotspot break two of the §3 conditions. With 9 workers instead of 10, this run does not show the designed burst capacity; as-built run 2 (§4.3) repeated finding 2.

## 4.3 As-built burst run 2 (2026-11-16, before the fixes)

Same setup as as-built run 1, 23 minutes later, on the same hotspot. k6 ran 16:37:23–16:46:24 UTC: 1,585 jobs, 0 failed, 0 dropped (`loadtest/results/swarm-burst-2.json`). **The burst nearly failed:** 7 workers instead of 10 for most of the overload.

| | Pilot 3 | As-built run 1 | As-built run 2 |
|---|---|---|---|
| Workers during the overload | 6 | 9 (6 + 3 AWS) | **7 (6 + 1 AWS)** |
| Throughput while overloaded | 2.36 jobs/s | 3.40 | **2.66** (on-prem 2.32, AWS 0.34) |
| Queue peak | 448 | 167 | **417** (t+540 s) |
| Drain: peak to empty queue | 3 min 10 s | 60 s | 2 min 40 s |
| k6 start to empty queue | 12.2 min | 9.9 min | 11.7 min |
| Burst nodes launched / joined | – | 2 / 2 | **3 / 1** |
| Burst instance time | – | 29.0 min ($0.06) | 31.0 min ($0.06) |

**Finding 1: two of three nodes never booted.** Their boot script stopped at `snap install aws-cli`: the Snap Store refused the connection (`connection refused`, both nodes in eu-central-1a), while the Ubuntu archive and Docker's repository answered in about 10 ms from the same instances. Docker was never installed, so the nodes never joined; the controller terminated each one after `max_node_provision_time` (480 s), as designed. Two later test nodes in eu-central-1c failed on the same step differently: one timed out, one hung. The failures came and went and hit two zones, so the cause is the Snap Store's availability that evening, not the network or the zone.

**Finding 2: Swarm stranded Pending tasks again,** worse than in run 1: the one node that joined got 1 of 4 waiting tasks, and 3 stayed `Pending` until the end.

**Finding 3: a node was launched after the queue was empty.** During the 300 s replica window the stranded tasks still counted as Pending, so when the controller terminated the first failed node it launched a replacement at 16:49:53, 50 s after the queue emptied. It also failed to boot.

## 4.4 The two fixes (2026-11-16 evening)

Both as-built runs measured the system as it was built, and both failures are findings. The measured runs in §4.5 use the fixed system; the two sets are not averaged.

- **Boot: the AWS CLI from AWS's zip installer instead of the snap** (`terraform/aws/templates/burst-swarm-user-data.sh`, [burst.md](burst.md) §7). A node now downloads only from the Ubuntu archive, Docker and AWS at boot. A test node joined about 53 s after the launch request, against 68–77 s with the snap; in the measured runs nodes joined 43–67 s after launch.
- **Stranded tasks: the burst controller re-creates them** ([burst.md](burst.md) §3.1). Cause, from Swarm's source: a task that can't be placed is put back in the scheduler's queue only while it belongs to the service's current revision, and every `scale` makes a new revision. The replica controller scales up in steps (7 → 9 → 10), so the waiting tasks of each earlier step drop out of the queue for good. If tasks wait next to a free slot for 15 s, the controller scales the service down by that many tasks and back up; Swarm removes unassigned tasks first, so running workers are not touched. The first version used docker-py's `scale()`, which restarted every worker after a stack deploy; it now sends the exact current spec, as the Docker CLI does. A live test confirmed it: the stranded task was re-created and placed, no running task was restarted, and the replica count stayed intact.

## 4.5 Measured burst runs 1–3 (2026-11-16, after the fixes)

Three runs back to back with the fixed system, from the laptop on mains power on home Wi-Fi, not the hotspot. Everything else as in pilot 3: worker image `sha-5de4c05`, image set v2, preset `medium`, `max_replicas: 10`, burst controller `sha-6c0936a`. Summaries: `loadtest/results/swarm-burst-fixed-1.json` to `-3.json`. Time-to-capacity is measured from the controller resizing the group to the first job finished on AWS.

| | Measured 1 | Measured 2 | Measured 3 | Median (range) |
|---|---|---|---|---|
| k6 start (UTC) | 21:12:10 | 21:46:51 | 22:05:13 | |
| Jobs sent (failed, dropped) | 1,585 (0, 0) | 1,585 (0, 0) | 1,585 (0, 0) | |
| Acceptance time, p95 | 13.7 ms | 11.7 ms | 9.8 ms | |
| Workers during the overload | 10 | 10 | 10 | |
| First node requested | t+57 s (warm-up) | t+52 s (warm-up) | t+246 s (ramp) | |
| Time-to-capacity (resize → first AWS job) | 103 s | 83 s | 74 s | **83 s** (74–103) |
| All 4 AWS workers busy | t+385 s | t+425 s | t+350 s | |
| Stranded tasks re-created | 1 | 1 | 2 | |
| Throughput, all 10 workers busy | 3.80 jobs/s | 3.77 | 3.80 | **3.80** (3.77–3.80) |
| of which on-prem / AWS | 2.35 / 1.45 | 2.32 / 1.46 | 2.36 / 1.44 | |
| Queue peak | 127 (t+390 s) | 123 (t+440 s) | 178 (t+360 s) | **127** (123–178) |
| k6 start to empty queue | 9.6 min | 9.6 min | 9.75 min | **9.6 min** |
| Job duration on-prem, median / p95 | 2.63 / 2.98 s | 2.63 / 2.98 s | 2.61 / 2.98 s | |
| Job duration AWS, median / p95 | 2.79 / 3.70 s | 2.76 / 3.56 s | 2.78 / 3.64 s | |
| Burst instance time | 27.8 min | 27.5 min | 25.5 min | **27.5 min** (about $0.05) |
| of which after the queue was empty | 14.5 min | 14.6 min | 14.6 min | |

No run failed a node, no job failed, and every node was drained, terminated and removed by the controller after the run.

**Finding 1: the fixed burst tier delivers the designed capacity: 3.80 jobs/s, +61 % over on-prem.** That is exactly 6 on-prem replicas (2.32–2.36) plus 4 AWS replicas (1.44–1.46, 0.36 each, as in §4.1). It lies above the overload's 3.7 jobs/s, so in every run the queue peaked during the overload and was already shrinking when k6 stopped, which never happened on-prem.

**Finding 2: Swarm strands tasks in every burst run; the re-create step is required, not optional.** It fired in all three runs (1, 1 and 2 tasks), 15–20 s after a free slot appeared. Without it these runs would have looked like the as-built ones.

**Finding 3: whether the burst starts in the warm-up varies between runs, and decides the queue peak.** In measured runs 1 and 2 the warm-up queue pushed the replicas to 7, and the first node was launched in the warm-up; in run 3 the replicas stayed at 6 and both nodes were launched only in the ramp, at t+246 s. Run 3 therefore had the highest queue peak (178) but the lowest cost (25.5 instance-minutes). The on-prem runs (§4.6) show why: the warm-up queue peaks at 29–31 jobs, right at the 30 where `ceil(depth / 5)` first asks for a seventh replica, so a job or two of timing decides whether a node is launched in the warm-up.

**Finding 4: half of every burst is paid for after the work is done.** 14.5–14.6 of about 27 instance-minutes fell after the queue emptied, in all three runs: the 300 s replica stabilization window plus the 120 s scale-down wait plus draining. Shorter windows would cut the cost but risk flapping.

**Finding 5: AWS jobs have a longer tail.** The median job on AWS is 5–7 % slower than on-prem (2.76–2.79 s against 2.61–2.63 s), the p95 19–24 % slower (3.56–3.70 s against 2.98 s).

**Limits.** Three runs on one evening, too few for confidence intervals; the ranges are reported instead. Acceptance times (p95 10–14 ms) match the pilots' 10 ms, so the laptop's network did not limit the load. Burst nodes have no host metrics, so their CPU use is not shown.

## 4.6 On-prem runs 2 and 3, and the comparison (2026-11-16)

Two more on-prem runs right after the measured burst runs, from the same laptop and network, so that each condition has three runs (pilot 3 is the first on-prem one). They ran with `max_replicas: 6`, as pilot 3 did. **This was a live override:** Git held `max_replicas: 10` for the burst runs, and the replica controller was pointed at a Swarm config identical to it except for `max_replicas: 6` (22:23–23:00 UTC), then switched back. The burst controller stayed deployed and never acted, since with 6 replicas no task is ever `Pending`. Summaries: `loadtest/results/swarm-onprem-2.json` and `-3.json`.

| | Pilot 3 (2026-10-24) | On-prem 2 | On-prem 3 |
|---|---|---|---|
| k6 start (UTC) | 14:50:59 | 22:23:50 | 22:42:22 |
| Jobs sent (failed, dropped) | 1,585 (0, 0) | 1,585 (0, 0) | 1,585 (0, 0) |
| Time to 6 replicas ready | 51 s | 50 s | 55 s |
| Warm-up queue peak | | 29 | 31 |
| Throughput, all 6 busy | 2.36 jobs/s | 2.33 | 2.33 |
| Throughput during the drain | 2.35 jobs/s | 2.33 | 2.34 |
| Queue peak | 448 | 459 (t+540 s) | 461 (t+545 s) |
| Drain: peak to empty queue | 3 min 10 s | 3 min 25 s | 3 min 20 s |
| k6 start to empty queue | 12.2 min | 12.4 min | 12.4 min |
| Job duration, median / p95 | 2.60 / 2.97 s | 2.63 / 2.98 s | 2.62 / 2.98 s |
| k6 start to back at 1 replica | 17.2 min | 17.5 min | 17.5 min |

The two new runs agree with pilot 3 three weeks apart: throughput within 1.3 %, queue peak within 3 %.

**The comparison: on-prem against burst, three runs each, median (range).**

| | On-prem (max 6) | Burst (max 10, 2 × `c7a.large`) | Change |
|---|---|---|---|
| Throughput while saturated | 2.33 jobs/s (2.33–2.36) | 3.80 jobs/s (3.77–3.80) | **+63 %** |
| Queue peak | 459 (448–461) | 127 (123–178) | **−72 %** |
| k6 start to empty queue | 12.4 min (12.2–12.4) | 9.6 min (9.6–9.75) | **−2.8 min (−23 %)** |
| Job duration, median | 2.62 s (2.60–2.63) | on-prem 2.63 s, AWS 2.78 s | AWS +6 % |
| Failed jobs | 0 | 0 | |
| AWS cost per run | none | 27.5 instance-minutes (25.5–27.8), about $0.05 | |

**What it shows.** The on-prem cluster saturates at 2.33 jobs/s, below the overload's 3.7 jobs/s, so its queue grows for the whole overload and takes over three minutes to drain afterwards. Bursting two nodes into AWS lifts the capacity above the arrival rate: the queue stops growing 2–3 minutes into the overload, peaks at a quarter of the on-prem peak, and is empty 2.8 minutes sooner. That costs about 5 cents per burst, half of which is spent after the work is done (§4.5, finding 4). Owning the same capacity on-prem would mean 10 busy single-core replicas on a host with 8 physical cores (ADR 0006), for a peak that lasts minutes.

## 5. Still to do

1. **k6 → Prometheus remote write** ([observability.md](observability.md) §10), so the offered rate sits on the same timeline as the queue.
2. **A run script** that writes the `hba-run` Grafana annotations at the start and end, waits for the queue to reach 0, and exports the window with `query_range` ([observability.md](observability.md) §6).
3. **Pilot 3 is the Swarm on-prem reference.** Repeat it only if the policy, the presets or the profile change.
4. **Swarm: done** (§4.5, §4.6): three measured burst runs and three on-prem runs. Open: burst nodes have no host metrics (no node-exporter on them), so the CPU panels miss them.
5. **Kubernetes.** Measure its on-prem capacity separately and report it, but drive it with the same absolute load.

## Sources

- k6: [ramping-arrival-rate](https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/ramping-arrival-rate/), [open and closed models](https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/open-vs-closed/), [thresholds](https://grafana.com/docs/k6/latest/using-k6/thresholds/), [k6/execution](https://grafana.com/docs/k6/latest/javascript-api/k6-execution/)
