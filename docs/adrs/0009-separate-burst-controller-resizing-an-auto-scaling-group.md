---
status: accepted
date: 2026-10-30
decision-makers: Krisztián Kirschner
---

# Burst with a separate controller that resizes an Auto Scaling group on Pending tasks

## Context and Problem Statement

On-prem capacity is 6 worker replicas: 3 workers, each with 2 vCPU, and 1 CPU reserved per task. Pilot 3 measured the ceiling at 2.36 jobs/s ([loadtest.md](../loadtest.md)). A manual test showed that an EC2 instance can join the swarm through the AWS gateway and process jobs. What is missing is the automation: what decides that more nodes are needed, how they are launched, and how they are removed again.

CLAUDE.md left one part of this open: launch burst instances with `terraform apply` per scale event, or with a direct AWS API call from the autoscaler.

## Decision Drivers

* **A fair comparison.** Both orchestrators must burst by the same rule. Otherwise the comparison measures two different burst implementations instead of the replica scaling (hand-rolled controller vs KEDA).
* **Reproducibility.** On-prem-only runs (pilot 3) must stay repeatable without code changes.
* **Cost control.** Paid nodes must go away when on-prem has room again, and a bug must not be able to start many instances.
* **Time.** The report is due 2026-11-22, and the Kubernetes cluster is still to be built.
* **Closeness to the native tools,** for the report: the Kubernetes Cluster Autoscaler is "the real version" of what is built here by hand.

## Considered Options

* Trigger:
  * **Tasks left `Pending` for lack of resources**
  * The queue or desired replicas, compared with a fixed on-prem capacity
* Where the logic lives:
  * **A separate burst controller**
  * One more step in the existing replica controller's loop
* How instances are created:
  * **An EC2 Auto Scaling group, resized by the controller**
  * Direct `RunInstances` / `TerminateInstances` calls
  * `terraform apply` per scale event
* What happens to a node after a burst:
  * **Terminate it**
  * Stop it and start it again next time (an ASG warm pool)
* For Kubernetes:
  * **The same burst controller with a Kubernetes adapter**
  * The real Cluster Autoscaler

## Decision Outcome

**Chosen:** a separate burst controller that watches for `Pending` worker tasks and resizes an Auto Scaling group (min 0, max 2, `c7a.large`, changed from `c6a.large` on 2026-11-08, see below; stock Ubuntu 26.04 AMI). It drains a node and terminates that exact instance when the node's tasks fit on-prem again. Instances are terminated, not stopped. The same controller serves Kubernetes through a second adapter. The policy fields are named after the Cluster Autoscaler's flags.

Details are in [docs/burst.md](../burst.md).

### Consequences

* Good, because both orchestrators burst with identical logic and values, so the comparison isolates the replica scaling.
* Good, because "burst off" means "the burst service isn't deployed". With `max_replicas` 6, pilot 3 is reproducible as it is.
* Good, because the scheduler's own `Pending` state replaces a fixed capacity number, so the trigger stays right when a VM is down.
* Good, because AWS enforces the cap (`max_size`) independently of the controller.
* Good, because it closes the CLAUDE.md open decision: Terraform owns the static parts (launch template, ASG), and the controller makes one API call per scale event.
* Bad, because it adds a service, an image and an AWS access key on-prem. The controller can't use an instance role there. The key is limited to resizing and terminating in one ASG.
* Bad, because the trigger reacts one step later than a queue-based one, roughly 10–20 s. That is small next to the boot time.
* Bad, because a cold start includes the software install, so time-to-capacity is longer than it could be. Packer or a warm pool would shorten it later.
* Neutral: the scale-down rule removes a full burst node whenever its tasks fit on-prem. The Cluster Autoscaler would keep a node above its utilization threshold. This is a named difference, chosen because burst minutes cost money.

## Pros and Cons of the Options

### Trigger on the queue or desired replicas

* Good, because it reacts a few seconds earlier.
* Bad, because it needs the on-prem capacity as a fixed number, which goes wrong when a VM is down.
* Bad, because it repeats the replica math, and on Kubernetes it would have to read KEDA's internal target.

### One more step in the replica controller

* Good, because it is one process with one image and no second service.
* Bad, because on Kubernetes KEDA owns the replicas, so a separate burst component is needed there anyway. The two orchestrators would then burst with different code.
* Bad, because a bug in the burst code could stop replica scaling, and the AWS key would sit in the replica controller.

### Direct `RunInstances` / `TerminateInstances`

* Good, because it avoids one AWS concept.
* Bad, because the AMI, subnet, security group and profile then live in Python instead of Terraform.
* Bad, because no AWS-side cap exists, and a retry in another zone has to be written by hand.

### `terraform apply` per scale event

* Good, because Terraform would own everything.
* Bad, because an apply takes minutes and needs the state lock, which slows every burst.
* Bad, because only the Pi holds those credentials.

### Stop and start (warm pool)

* Good, because a started instance skips the install and the image pull, so time-to-capacity is shorter.
* Bad, because stopped Swarm nodes stay in the swarm as `down`, which changes the scale-down steps, and its interaction with ASG lifecycle hooks is untested.
* Neutral: it is a later speed-up, like Packer, measured against the cold-start baseline.

### The real Cluster Autoscaler on Kubernetes

* Good, because it is the native tool, with no hand-written code.
* Bad, because Swarm has no equivalent, so the two orchestrators would burst differently.
* Bad, because CLAUDE.md scopes it as "mention, don't implement", and a mixed on-prem/AWS cluster (k3s, as built: [ADR 0010](0010-k3s-as-a-second-vm-set-switched-by-active-cluster.md)) is not its usual setup.

## More Information

**Correction, 2026-11-08: the instance type.** This decision first chose `c6a.large`, counting 2 vCPUs as 2 replicas. Those 2 vCPUs are one physical core with two threads, so a test with two replicas on one node ran a job in 4.48 s against about 2.6 s on-prem, and the node added 19 % throughput instead of about 33 %. `c7a.large` has 2 physical cores: 2.78 s per job and about 30 % more throughput ([loadtest.md](../loadtest.md) §4.1). The lesson for the report: vCPU count isn't core count.

**Outcome, 2026-11-17: built and measured on both orchestrators.** The Kubernetes adapter shares the decision logic and the policy values; both clusters gained +62–63 % throughput from the same two nodes, with the same time-to-capacity (77 s and 78 s) and instance time ([loadtest.md](../loadtest.md) §4.9, [burst.md](../burst.md) §1.1). Swarm needed one workaround Kubernetes didn't: re-creating stranded Pending tasks ([burst.md](../burst.md) §3.1).

**Revisit if** the cold start makes the burst lift too short to measure. The first fix then is to lengthen the load profile's hold stage. Packer or a warm pool comes second.

**What this changes in the docs:** [autoscaler.md](../autoscaler.md) §9 described bursting as one more step in the replica controller's loop; it now points to [burst.md](../burst.md). CLAUDE.md's open decision on `terraform apply` vs boto3 is answered here. CLAUDE.md still lists it as open, because CLAUDE.md lags behind the docs.

Related: [ADR 0003](0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md) (boot path, Packer), [ADR 0006](0006-cluster-size-one-control-three-workers.md) (on-prem capacity), [ADR 0007](0007-site-to-site-with-two-linux-tailscale-routers.md) (the network path burst nodes use).
