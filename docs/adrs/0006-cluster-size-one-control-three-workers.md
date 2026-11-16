---
status: accepted
date: 2026-10-16
decision-makers: Krisztián Kirschner
---

# One control node and three 2-vCPU workers per cluster

## Context and Problem Statement

Both clusters (Swarm and Kubernetes) run on one Proxmox host, `homelab-core-01`: a Ryzen 7 5700G with 8 cores / 16 threads and 32 GB RAM. The on-prem capacity of a cluster sets the **plateau**: the point where the queue keeps growing because every on-prem worker is busy, and bursting to AWS becomes necessary. That plateau and the lift after bursting are the main result. How many worker nodes should each cluster have, and how big?

Until now the plans disagreed. CLAUDE.md said "3 Swarm VMs" (1 manager + 2 workers, plateau 4), while the report draft and the workload design assumed 3 workers (plateau 6). Only `swarm-ctl` and `swarm-01` existed, so the on-prem ceiling was 2 replicas.

## Decision Drivers

* **The plateau must come from the cluster, not from the host.** If busy replicas compete for physical cores with each other or with the rest of the system, job times grow as replicas are added, and the curve shows host contention instead of orchestrator behaviour.
* **A plateau big enough to show.** The run needs a visible "on-prem saturated" phase before the burst workers join.
* **Fits the host.** OPNsense (2 vCPU / 2 GB) is always on; 3 unrelated small VMs are shut down before tests. Proxmox itself needs RAM.
* **Same capacity unit on both tiers.** An on-prem worker should equal an AWS burst worker (`c6a.large`, 2 vCPU / 4 GiB), so "one more node" means the same everywhere.
* **Identical shape for both orchestrators**, so the comparison measures the orchestrator, not the cluster size.

## Considered Options

* 1 control + 2 workers per cluster (plateau 4)
* **1 control + 3 workers per cluster (plateau 6)**
* 1 control + 4 workers per cluster (plateau 8)
* Fewer, larger workers (e.g. 2 workers with 3 vCPU)

Every option keeps 2 vCPU / 4 GB per VM unless stated, one job replica per vCPU (workload doc §10.1), and no job replicas on the control node.

## Decision Outcome

Chosen option: "1 control + 3 workers per cluster". Swarm: `swarm-ctl`, `swarm-01..03`; Kubernetes: `k8s-ctl`, `k8s-01..03`; plus `obs-01`. All VMs are 2 vCPU / 4 GB. Only the cluster under test is powered on.

Host budget during a test:

| | vCPU | RAM |
|---|---|---|
| 4 cluster VMs | 8 | 16 GB |
| `obs-01` | 2 | 4 GB |
| OPNsense (always on) | 2 | 2 GB |
| **Total** | **12 of 16 threads** | **22 of 32 GB** (~10 GB left for Proxmox) |

### Consequences

* Good, because the 6 busy replicas fit within the **8 physical cores**, leaving about 2 cores for the control node (`api`, `redis`), `obs-01` (Prometheus, RustFS handling every upload) and OPNsense. Up to the plateau, each replica can run on its own core.
* Good, because a plateau of 6 gives a clear saturated phase before bursting, and a burst worker adds a third of the on-prem capacity per node, which is easy to see in the throughput graph.
* Good, because the on-prem worker matches `c6a.large`, so per-node capacity is the same on both tiers.
* Bad, because both cluster sets together would need 20 vCPU / 38 GB, more RAM than the host has. They are never powered on together; switching is a power-off/power-on.
* Bad, because vCPUs are hyper-threads. Under full load a busy replica can share a core with a system process on its sibling thread, so job times at the plateau may be a little slower than with a single replica. That is measured and reported, not hidden.
* Neutral: the autoscaler's `max_replicas` is 6 on-prem (raised again for bursting), and Kubernetes needs a CPU request below 1 (e.g. `900m`) so that 2 worker pods fit per node next to the system pods (workload doc §10.1).

## Pros and Cons of the Options

### 1 control + 2 workers (plateau 4)

* Good, because it leaves the most headroom: 10 vCPU / 18 GB during a test.
* Bad, because a plateau of 4 is small: bursting starts almost immediately, and the saturated phase is short.

### 1 control + 4 workers (plateau 8)

* Good, because it gives the largest on-prem capacity.
* Bad, because 8 busy replicas need all 8 physical cores, and the control node, `obs-01` and OPNsense still need CPU. Job times would grow with every added replica, so the plateau would be shaped by the host.
* Bad, because 14 vCPU / 26 GB during a test leaves only ~6 GB for Proxmox.

### Fewer, larger workers

* Good, because fewer VMs mean less per-node overhead.
* Bad, because an on-prem node would no longer match a burst node, and "add one node" would mean different capacity per tier.

## More Information

**Correction:** while the cluster was being built, CLAUDE.md still described 3 Swarm VMs, and a suggestion during that work was to add just one worker to "match the plan". The report draft (`docs/report/02-architecture.md` §2.2) and the workload design (`docs/csharp-workload-v1.2.md` §1, §10.1) already assumed 3 workers. This ADR settles it; CLAUDE.md was updated to match.

**Before load tests:** shut down the 3 unrelated VMs on the host, and keep RAM ballooning off for the project VMs (fixed memory), so memory reclaim under host pressure can't distort the measurements.

Related: [ADR 0003](0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md) (burst workers), [docs/autoscaler.md](../autoscaler.md) §3 (`max_replicas`).
