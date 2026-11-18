---
status: accepted
date: 2026-11-17
decision-makers: Krisztián Kirschner
---

# Configure KEDA and the HPA to the Swarm controller's timing, not to their defaults

## Context and Problem Statement

The Swarm side scales with our own controller ([autoscaler.md](../autoscaler.md)): every 5 s it reads the queue from Prometheus, scales straight to `ceil(queue / 5)` between 1 and 6 (10 with bursting), and scales down only to the highest target of the last 300 s. On Kubernetes, KEDA reads the queue and hands it to a Horizontal Pod Autoscaler (HPA), which sets the replicas.

Out of the box the two differ in timing, not only in mechanism: the HPA decides every 15 s, ignores changes within ±10 % of the target, and scales up by at most 4 pods or 100 % per 15 s; KEDA polls every 30 s. Compared at their defaults, a difference in results could come from these numbers rather than from the orchestrators.

## Decision Drivers

* **The comparison should measure the orchestrators,** not two sets of default intervals ([observability.md](../observability.md) §8 asked for this before the runs).
* **Each setting must have a documented counterpart,** so a reader can check the pairing.
* **The defaults are a finding too:** "what happens if you just install it" is what most users get.

## Considered Options

* **A: the same rules on both** (KEDA and the HPA set to the Swarm controller's values)
* B: each at its defaults
* Both A and B

## Decision Outcome

**Chosen: A for the measured comparison, with B as an extra.** `stacks/k8s/keda.yml` and the `k3s` role set: `pollingInterval: 5`, HPA sync period 5 s, tolerance 0, scale-up of up to 10 pods per 15 s with no stabilization, scale-down after a 300 s window, `listLength: "5"`, 1–6 replicas on-prem and 1–10 with bursting. The pairing, setting by setting, is in [kubernetes.md](../kubernetes.md) §4.

**One difference is left on purpose: where the signal comes from.** KEDA reads Redis itself, at the moment it decides; the Swarm controller reads Prometheus, whose newest sample is 4.4–7.9 s old. Changing it would mean either a slower KEDA or a different Swarm architecture. Instead its effect was measured.

### Consequences

* Good, because the measured differences that remain are small and explained ([loadtest.md](../loadtest.md) §4.7, §4.8): the same rule gives the same lift.
* Good, because the one deliberate difference turned into the comparison's most interesting result. With the live signal, KEDA settled at **5** workers in the warm-up where Swarm's staler signal overshot to **6**, and Kubernetes' queue then peaked 8 % higher. Three runs with KEDA reading Swarm's Prometheus series confirmed it: the warm-up went to 6, as on Swarm, and the queue-peak gap halved (498 → 477, against Swarm's 459). The other half is the 2.5 % slower jobs on the Kubernetes nodes.
* Good, because one run with every default (B) behaved like Swarm too: the 15 s HPA delay acts like the stale signal (6 workers at 40 s, queue peak 469).
* Bad, because the measured Kubernetes setup is not "out of the box". Readers who install KEDA with defaults get a slower but, on this load profile, not worse autoscaler.
* Bad, because the HPA flags are cluster-wide (`kube-controller-manager-arg`), so they apply to every HPA. Only one HPA exists here.
* Neutral: B has one run, not three. It is reported as indicative only.

## Pros and Cons of the Options

### B: each at its defaults

* Good, because it is what most installations look like.
* Bad, because a result could not be attributed: a 15 s decision interval, a ±10 % band and step limits each change the warm-up on their own.

### Both, three runs each

* Good, because it gives both answers with the same confidence.
* Bad, because it costs another hour of runs before the deadline; A plus one B run answered the question.

## More Information

**What it means for the report:** reacting faster is not the same as reacting better. With a rising load the stale signal gave spare capacity by accident; headroom is better asked for on purpose, for example with a lower jobs-per-replica target.

**Revisit if** a different load profile is tested (a steady or falling load could favour the live signal), or two more B runs are wanted ([loadtest.md](../loadtest.md) §5, item 7).

Related: [ADR 0005](0005-in-cluster-prometheus-agent-with-remote-write.md) (why the Swarm controller's signal comes through Prometheus), [ADR 0009](0009-separate-burst-controller-resizing-an-auto-scaling-group.md) (bursting uses one controller for both, for the same reason).
