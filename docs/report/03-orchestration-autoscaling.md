# 3. Orchestration & Autoscaling: Swarm vs. Kubernetes

Both clusters run the same three images (`hba-api`, `hba-worker`, Redis), the same worker settings (1 CPU per replica, so a 2-vCPU node holds two and a third waits; no health check, [ADR 0004](../adrs/0004-no-liveness-check-on-workers.md)), behind the same VIP and URL. Both scale replicas by **one rule: `ceil(queue / 5)` replicas, between 1 and 6 on-prem (10 with bursting), straight up, and down only to the highest target of the last 300 s.**

## 3.1 Docker Swarm: a hand-rolled controller

Swarm has no autoscaler, so the project has one: **120 lines of Python** plus a 10-line policy file ([autoscaler.md](../autoscaler.md)). Every 5 s it reads the queue length from Prometheus, computes the target and calls the Docker API to scale `hba_worker`.

- **The signal is 4.4–7.9 s old:** a Prometheus agent inside the swarm scrapes Redis every 5 s and remote-writes to `obs-01` ([ADR 0005](../adrs/0005-in-cluster-prometheus-agent-with-remote-write.md)).
- **Correction: the first scale-down rule oscillated.** With a 60 s cooldown, the warm-up load (between one and six replicas' capacity) filled and emptied the queue in turn, and the replicas went 6 → 1 → 6 (pilot 2). The fix copies the Kubernetes HPA's rule: scale down only to the highest target of the last 300 s.
- **Deployment:** the Ansible `stacks` role runs `docker stack deploy` from the manager; secrets become Swarm secrets.
- **Correction: the overlay address pool.** Swarm's default pool (`10.0.0.0/8`) contains the lab network, so the swarm had to be recreated with `10.200.0.0/16`; the pool can only be set at `swarm init`.

## 3.2 Kubernetes: k3s and KEDA

Kubernetes runs as **k3s**, a single-binary distribution that bundles containerd, flannel, CoreDNS, ServiceLB and a Helm controller: for two Kubernetes beginners, one install instead of choosing and wiring each part (kubeadm) ([ADR 0010](../adrs/0010-k3s-as-a-second-vm-set-switched-by-active-cluster.md), [kubernetes.md](../kubernetes.md)). The control node is tainted so that workers stay off it, as on Swarm.

**KEDA** reads the Redis list directly and feeds an HPA, which sets the worker Deployment's replicas: **34 lines of YAML**, no code. The defaults differ from the Swarm controller (HPA every 15 s with a ±10 % dead band and limited steps; KEDA polls every 30 s), so they were **set to the Swarm controller's values** (5 s, no band, full jump, 300 s window), and one run at full defaults was added as an extra ([ADR 0012](../adrs/0012-configure-keda-to-the-swarm-controllers-timing.md)). **One difference stayed on purpose:** KEDA's signal is current; Swarm's is Prometheus' 4.4–7.9 s old copy (Section 5.3).

**Finding while building it:** containerd's default AppArmor profile killed the .NET worker at warm-up (exit 139, 5 of 7 pods), by blocking a signal the runtime sends to itself; Docker's profile doesn't. The worker runs unconfined on Kubernetes, the one security setting that differs between the two clusters ([ADR 0011](../adrs/0011-run-the-worker-without-apparmor-confinement-on-kubernetes.md)).

## 3.3 Bursting: one controller, two adapters

Replicas and nodes are scaled separately, as KEDA and the Cluster Autoscaler split it on Kubernetes ([ADR 0009](../adrs/0009-separate-burst-controller-resizing-an-auto-scaling-group.md), [burst.md](../burst.md)):

1. The replica autoscaler asks for more replicas than fit on-prem; the scheduler leaves the extra ones **`Pending`** (no free CPU).
2. A separate **burst controller** sees them for 10 s and sets an EC2 Auto Scaling group to `ceil(pending / 2)` nodes (max 2, `c7a.large`).
3. Each node boots, reads a join token from SSM, and joins the cluster over the Tailscale link; the scheduler places the waiting replicas on it.
4. When a node's replicas fit on-prem again for 120 s, the controller drains it, terminates that exact instance and removes the node.

The decision logic (125 lines) and the policy values are **shared**; only a ~110-line adapter per orchestrator differs. So both burst identically, and the comparison isolates the orchestrators.

**Finding: Swarm strands Pending tasks.** In the first burst runs, nodes joined with free CPUs, yet tasks stayed `Pending` until the end (1 of 4 AWS workers missing in one run, 3 of 4 in another). Swarm's source showed why: an unplaced task is retried only while it belongs to the service's current version, and every scale step makes a new version. The Swarm adapter now re-creates such tasks after 15 s. Kubernetes' scheduler retries unschedulable pods when a node is added, so its adapter needs no such step.

## 3.4 Comparison

| | Swarm | Kubernetes (k3s) |
|---|---|---|
| Replica autoscaler | own controller, 120 lines Python + policy | KEDA `ScaledObject`, 34 lines YAML |
| Signal | Prometheus, 4.4–7.9 s old | Redis, current |
| Rule and timing | `ceil(queue/5)`, 5 s, 300 s down-window | the same, configured on KEDA and the HPA |
| Node bursting | shared controller + Swarm adapter | shared controller + Kubernetes adapter |
| Scheduler workaround | re-create stranded tasks | none needed |
| Request path | VIP → routing mesh → `api` | VIP → ServiceLB → kube-proxy → `api` |
| Worker confinement | Docker's default AppArmor profile | unconfined |

**Scope.** The Kubernetes **Cluster Autoscaler** and Karpenter are the native equivalent of the burst controller; they were not used, because Swarm has no equivalent and the two orchestrators had to burst by the same logic. The CPU-based HPA alone does not fit a queue-driven workload; here it is only KEDA's engine.
