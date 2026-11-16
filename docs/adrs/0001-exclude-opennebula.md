---
status: accepted
date: 2026-09-29
decision-makers: Krisztián Kirschner
---

# Exclude OpenNebula from the project

## Context and Problem Statement

The course covers OpenNebula (labs in weeks 6–7), and it is a natural fit for a "hybrid cloud" project. Should it be part of the on-premises stack, next to or in place of Proxmox?

## Decision Drivers

* The course goal is hands-on experience. The parts built by hand here (the Swarm controller, the Terraform + boto3 burst-worker lifecycle) are the deliverable, not overhead to automate away.
* One physical host (`homelab-core-01`, 32 GB RAM). RAM is already the tight resource.
* Fixed deadline (22 Nov 2026). Every extra platform competes with the core comparison for time.
* Autoscaling in this project happens at the container orchestrator layer (Swarm service replicas, KEDA), not at the VM layer.

## Considered Options

* Proxmox only, no OpenNebula
* OpenNebula replacing Proxmox as the virtualization layer
* OpenNebula as the hybrid-cloud layer, bursting into AWS

## Decision Outcome

Chosen option: "Proxmox only, no OpenNebula". OpenNebula would duplicate what Proxmox already does, and its AWS integration works at a different layer from the one this project measures.

### Consequences

* Good, because the bursting logic stays hand-built and visible, and it can be compared directly between the hand-rolled Swarm controller and KEDA.
* Good, because the existing Proxmox Terraform module (`bpg/proxmox`) is reused, and no RAM goes to an extra control plane.
* Bad, because the project gets no OpenNebula experience of its own. The course labs cover that instead.

## Pros and Cons of the Options

### Proxmox only

* Good, because it is already running and already managed by Terraform.
* Good, because nothing else on the host needs rebuilding.
* Neutral, because Proxmox has no cloud or bursting features of its own. The project builds those.

### OpenNebula replacing Proxmox

* Good, because it gives hands-on experience with a course tool.
* Bad, because the host would need rebuilding, and the other VMs on it (OPNsense, existing tenants) would have to move.
* Bad, because it teaches nothing new for the thesis question. It is a like-for-like swap at the VM layer.

### OpenNebula as the hybrid-cloud layer

* Bad, because since OpenNebula 6.0 AWS capacity comes from Edge Cluster Provisioning (OneProvision). It adds whole virtualization hosts to OpenNebula. It does not add container nodes to a Swarm or Kubernetes cluster, which is the event this project measures.
* Bad, because it would hand over the provisioning logic that is meant to be built by hand here.

## More Information

**Correction.** The first version of this decision said OpenNebula "ships a native cloud-bursting driver to AWS/EC2". That was outdated: the EC2 and Azure hybrid drivers existed up to 5.12 and were removed in 6.0 in favour of Edge Cluster Provisioning ([6.0 compatibility guide](https://docs.opennebula.io/6.0/intro_release_notes/release_notes/compatibility.html), [OpenNebula/one#5204](https://github.com/OpenNebula/one/issues/5204)). The decision still stands, but the reason changed. OpenNebula still automates AWS capacity, only at the host layer instead of the container-node layer.

**Revisit** in a separate project if OpenNebula itself becomes the subject, for example comparing VM-level bursting (OneProvision) with container-level bursting.
