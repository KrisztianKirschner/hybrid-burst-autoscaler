# 5. Results & Analysis

All figures are medians of three runs (range in brackets), computed by one script for both orchestrators. Full tables and timelines: [loadtest.md](../loadtest.md) §4.5–4.9.

| | Swarm on-prem | Swarm + burst | Kubernetes on-prem | Kubernetes + burst |
|---|---|---|---|---|
| Throughput while saturated | 2.34 jobs/s | **3.78** jobs/s | 2.28 jobs/s | **3.72** jobs/s |
| of which on-prem / AWS | | 2.34 / 1.44 | | 2.29 / 1.43 |
| Lift from bursting | | **+62 %** | | **+63 %** |
| Queue peak | 459 (448–461) | 127 (123–178) | 497 (495–501) | 171 (171–179) |
| k6 start to empty queue | 12.4 min | 9.6 min | 12.8 min | 9.8 min |
| Job duration, median (on-prem / AWS) | 2.61 s | 2.63 / 2.78 s | 2.67 s | 2.67 / 2.79 s |
| AWS instance time per run | | 28.8 min (about $0.06) | | 27.6 min (about $0.05) |
| Failed jobs | 0 | 0 | 0 | 0 |

## 5.1 On-Premises Plateau

Both clusters saturate at their six replicas, with worker CPUs 99 % busy and **zero CPU steal**: the host was not oversubscribed. The plateau is **2.34 jobs/s on Swarm and 2.28 on Kubernetes**, well below the overload's 3.7 jobs/s, so for five minutes the queue grows by about 1.4 jobs every second, peaks near 460–500 jobs, and needs a further 3.5 minutes to drain after the load stops. The last job waits about 3.5 minutes in the queue. This is the hardware limit the architecture is built around: 6 one-CPU replicas on a host with 8 physical cores.

## 5.2 Burst Behavior

**Bursting works the same on both orchestrators, and lifts both by the same share.** Two `c7a.large` nodes add four replicas and 1.43–1.44 jobs/s on either orchestrator, with the same AWS job times. Capacity rises above the arrival rate, so the queue stops growing within about three minutes of the overload's start, peaks at 28–34 % of the on-prem peak (−72 % Swarm, −66 % Kubernetes), and is empty about three minutes sooner. On-prem kept its plateau while the burst ran.

**Time-to-capacity** (launch to the first job finished on AWS) is the same on both: **77 s on Swarm, 78 s on Kubernetes**, most of it the node's boot and software install. The orchestrators differ in what comes around it:

- **Kubernetes fills new nodes faster.** From the first request to all four AWS workers busy took **78–82 s** on Kubernetes and **104 s** in Swarm's comparable run, because kube-scheduler places waiting pods as soon as a node is Ready. On Swarm the controller had to re-create stranded tasks in every run (Section 3.3).
- **Kubernetes bursts later.** It requested its first node in the ramp in all three runs; Swarm did so already in the warm-up in two of three. That is the signal-age effect of Section 5.3: Swarm's stale signal overshoots the warm-up to 7 replicas, and the seventh has no room on-prem. The early node lowered Swarm's queue peak (123–127 against 178 in its ramp-only run and 171–179 on Kubernetes) for about 2 instance-minutes more (28.8 against 26.5), a small share, because most of the instance time comes after the work.

## 5.3 Swarm vs. Kubernetes: Where the Small Gap Comes From

On-prem, Kubernetes' queue peaked **8 % higher** (497 against 459). The isolation runs split this gap into two halves of about 20 jobs each:

| Cause | Extra queued jobs | Kind |
|---|---|---|
| **Signal age.** KEDA's current queue value stops the warm-up at 24 jobs → 5 replicas, the right size for the warm-up's 1.4 jobs/s. Swarm's 4–8 s old value lets the queue reach 29 → 6 replicas, which happens to be ready for the ramp. With Swarm's signal, KEDA also chose 6, and the peak fell to 477. | about 20 | configuration |
| **Slower jobs on the Kubernetes nodes:** 2.3 % longer per job, 2.6 % less throughput at equal CPU use. Candidates: the k3s agent's overhead (idle workers 1.3–1.7 % busy against Swarm's 0.8–1.0 %), CPU weights under contention, the network path. Not isolated. | about 20 | orchestrator |

So **reacting faster is not the same as reacting better**: with a rising load, the stale signal gave Swarm accidental headroom, and KEDA at full defaults (15 s HPA interval) behaved like Swarm for the same reason (peak 469, one run). A comparison of autoscalers has to control where their signal comes from, or it partly measures the plumbing. What remains of the orchestrators themselves is **about 2.5 % per job**, small next to bursting's +63 %. Accepting a request takes about 3 ms longer on Kubernetes (one more forwarding hop), far below anything the capacity depends on.

## 5.4 Cost Analysis

At the Frankfurt on-demand price of `c7a.large` ($0.1171/h), a burst run costs **about 5–6 cents**: 27.6–28.8 instance-minutes. More than **half of it, 15.7–15.8 minutes, falls after the queue is already empty**, on both orchestrators: the 300 s replica window, then 120 s before a node counts as unneeded, then the drain. Those windows protect against flapping; shortening them could save up to half of the bill, at that risk. The policy, shared by both, sets the cost, not the orchestrator.

The alternative is owning the peak: 10 replicas would need two more worker VMs, 10 busy single-CPU replicas on a host with 8 physical cores, bought and powered for a peak that lasts minutes. Renting it for 5 cents per peak is the cloud-bursting argument in numbers.
