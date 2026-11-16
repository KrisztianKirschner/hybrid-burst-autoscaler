---
status: accepted
date: 2026-10-14
decision-makers: Krisztián Kirschner
---

# Collect workload metrics with a Prometheus agent inside the cluster that pushes to obs-01

## Context and Problem Statement

The central Prometheus and Grafana run on `obs-01`, outside both clusters, so that the Swarm and Kubernetes runs end up in one store ([docs/observability.md](../observability.md) §1). The metrics the autoscalers and the report depend on come from inside the cluster: the queue length (via redis-exporter, next to Redis on the overlay network) and the `hba_` metrics of every worker replica. Neither has a published port. How do these metrics reach the Prometheus on `obs-01`?

## Decision Drivers

* **One store for both orchestrators.** The comparison graphs need Swarm and Kubernetes runs in the same Prometheus, with a label to tell them apart.
* **Per-replica metrics.** Every worker replica must be its own series. Job duration per tier and replicas per tier are computed from them.
* **Replicas come and go.** The autoscaler adds and removes replicas during a run, burst workers on AWS included. Discovery must follow without a config change.
* **Same pattern on Kubernetes.** Whatever is built for Swarm should have a direct Kubernetes equivalent, so the setup itself is not a difference between the runs.
* **Fresh enough for the controller.** The Swarm controller reads the queue length from Prometheus, so the data age is part of its reaction time.

## Considered Options

* `obs-01` scrapes published ports
* A full Prometheus server inside the cluster
* A Prometheus server inside the cluster, federated by `obs-01`
* **A Prometheus agent inside the cluster that remote-writes to `obs-01`**

## Decision Outcome

Chosen option: "A Prometheus agent inside the cluster that remote-writes to `obs-01`".

On Swarm, the `monitoring` stack runs redis-exporter and Prometheus in agent mode on the manager, both on the overlay network of the `hba` stack. The agent finds worker tasks with `dockerswarm_sd_configs`, scrapes every 5 s, adds `cluster: swarm` to every series, and pushes to `obs-01`, which runs with `--web.enable-remote-write-receiver`. Kubernetes gets the same agent later, with `kubernetes_sd_configs` and `cluster: k8s`. Setup details are in [docs/observability.md](../observability.md) §5.

### Consequences

* Good, because both orchestrators land in one store, separated by one label, and the comparison dashboards switch between them with a variable.
* Good, because each worker task is scraped on its own overlay address, so every replica is its own series.
* Good, because the agent keeps no queryable data and no long retention: it buffers samples and forwards them. If `obs-01` is briefly down, it resends from its write-ahead log.
* Good, because the Kubernetes setup is the same idea with a different discovery mechanism, which is itself a point for the report.
* Bad, because there is one more component per cluster, and its config is a Swarm config that cannot be updated in place. The `stacks` role versions it by a content hash.
* Bad, because the agent needs the Docker socket on the manager, which gives it full control of that node's Docker.
* Bad, because the remote-write receiver on `obs-01` accepts writes from anything that can reach port 9090. It is only reachable from the lab VLAN and the tailnet.
* Neutral, because the Swarm controller sees data that is 5 s scrape + remote-write delay + its own polling loop old. KEDA reads Redis directly. The intervals on both sides must be matched before the comparison runs; that gets its own ADR.

## Pros and Cons of the Options

### `obs-01` scrapes published ports

* Good, because there is nothing extra inside the cluster.
* Bad, because a published port on Swarm goes through the routing mesh, which load-balances each scrape to a random replica. The metrics of all replicas would be mixed into one series.
* Bad, because publishing a port per replica (host mode) needs a fixed port per node and breaks with two replicas on one node.

### A full Prometheus server inside the cluster

* Good, because it can scrape the overlay network directly.
* Bad, because its data goes down with the cluster, and the Swarm and Kubernetes runs end up in two separate stores. Comparing them needs two datasources and two sets of queries.

### A Prometheus server inside the cluster, federated by `obs-01`

* Good, because `obs-01` keeps pulling, which is the usual Prometheus model.
* Bad, because there are two full servers with their own storage, and the federation scrape adds another interval to the data age.
* Bad, because `obs-01` needs a route into the cluster for the federation endpoint, which brings back the published-port problem for one service.

### A Prometheus agent inside the cluster with remote write

* Good, for the reasons under Consequences.

## More Information

**Discovery: `dockerswarm_sd_configs` instead of DNS.** Swarm's DNS name `tasks.<service>` also lists every replica, but returns only IP addresses. `dockerswarm_sd_configs` with `role: tasks` reads the Docker API on the manager and adds labels such as the node hostname. That label is needed to check that each worker node runs two replicas ([docs/app.md](../app.md) §4) and to tell on-prem nodes from burst nodes. The cost is the Docker socket mount and running the agent as root.

**Tested 2026-10-14** on a local single-node swarm, with `remote_write` pointed at an unused address so no test data reached `obs-01`: both scrape jobs up, the worker target found on its overlay address with the `node` label, a second replica picked up after scaling.

Sources:

* [Prometheus agent mode](https://prometheus.io/blog/2021/11/16/agent/)
* [Prometheus `dockerswarm_sd_config`](https://prometheus.io/docs/prometheus/latest/configuration/configuration/#dockerswarm_sd_config) and [Docker Swarm guide](https://prometheus.io/docs/guides/dockerswarm/)
* [Prometheus remote-write receiver flag](https://prometheus.io/docs/prometheus/latest/command-line/prometheus/)
* [Docker: Swarm routing mesh](https://docs.docker.com/engine/swarm/ingress/)
