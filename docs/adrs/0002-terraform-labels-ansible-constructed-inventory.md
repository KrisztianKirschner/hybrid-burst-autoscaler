---
status: accepted
date: 2026-09-29
decision-makers: Krisztián Kirschner
---

# Ansible inventory from Terraform labels, grouped by `constructed`

## Context and Problem Statement

Ansible needs to know every host and which groups it belongs to (Swarm control node, Swarm workers, observability, and later Kubernetes and AWS burst workers). Terraform already defines every Proxmox VM and its IP in `local.vms`. How should the inventory be produced, and where should the rules live that decide which host is in which group?

## Decision Drivers

* One source of truth for hosts and IPs. A hand-written copy drifts.
* Separation of concerns: Terraform describes machines, Ansible decides configuration. Changing a group should not need a `terraform apply`.
* Grouping rules in one place, whatever the host came from.
* Hosts come from more than one source. AWS burst workers are launched by a script, not Terraform, so they never appear in Terraform state.
* The same scheme has to cover the Kubernetes cluster later without name clashes.
* Terraform and Ansible run in separate CI workflows and jobs, each with a fresh checkout. Whatever Ansible reads has to be reachable from any job, not only from the job that ran `terraform apply`.

## Considered Options

* Hand-written static `hosts.yml`
* Terraform renders an inventory file (`templatefile` + `local_file`)
* Terraform assigns Ansible group names (`ansible_host.groups`) plus a static parent-groups file
* Terraform assigns labels (`cluster`, `node_role`), Ansible's `constructed` plugin builds all groups
* `community.general.proxmox` inventory plugin

## Decision Outcome

Chosen option: "Terraform assigns labels, `constructed` builds all groups". Terraform's `ansible_host` resources carry `cluster` and `node_role` as host variables and no group names. `inventory/99-constructed.yml` holds every grouping rule. Tag-based grouping is the standard way dynamic inventories are used (e.g. the `aws_ec2` plugin's tag-based groups), and `constructed` exists to merge several inventory sources into shared groups. Red Hat ships it as a feature of Ansible Automation Platform 2.4.

How it works in practice: [docs/ansible.md](../ansible.md).

### Consequences

* Good, because IPs and host names exist only in `terraform/proxmox/main.tf`.
* Good, because the inventory data lives in the S3 state, so every CI job (and a local run) reads the same data after `terraform init`. Nothing is generated or passed between jobs.
* Good, because all group logic is in one Ansible file. Regrouping touches no Terraform.
* Good, because if Ansible ever targets long-lived AWS hosts, they get the same labels as EC2 tags and land in the same groups with no special case. Burst workers are not among them: they are baked into an AMI ([ADR 0003](0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md)).
* Good, because `cluster` + `node_role` gives unambiguous names for both orchestrators (`swarm_control`, `k8s_control`), where a bare `manager` group would mix them.
* Bad, because running Ansible needs an initialised Terraform directory, which means read access to the state bucket. Locally that is a read-only credential that did not exist before.
* Bad, because there are two new dependencies: the `ansible/ansible` Terraform provider and the `cloud.terraform` collection.
* Bad, because with `strict: false` a host with a misspelt label silently joins no group. It is caught by checking `ansible-inventory --graph` for hosts under `ungrouped`.

## Pros and Cons of the Options

### Hand-written static `hosts.yml`

* Good, because it is the simplest and most readable option. For about seven fixed VMs it would work.
* Bad, because every VM and IP is written twice, in Terraform and in the inventory.
* Bad, because burst workers still need a dynamic source, so grouping would be split anyway.

### Terraform renders an inventory file

A template (e.g. `templates/inventory.tftpl`) is rendered by `templatefile()` and written as `generated.ini` by a `local_file` resource.

* Good, because it is the most widely described Terraform-to-Ansible approach, and a familiar one from an earlier project.
* Good, because it needs no extra provider or collection, and the result is a plain file anyone can read.
* Bad, because the file only exists in the checkout of the `terraform-apply` job. The Ansible pipeline is a separate workflow with its own fresh checkout, so the file is not there. Making it available means passing it as an artifact between workflows, committing it back to `main` from CI, or running Ansible inside the Terraform apply job. Each one couples the two pipelines, and the Ansible PR check would still have no file.
* Bad, because local `--check` runs would also need the file, which only CI can produce.
* Bad, because group names are written into the Terraform template.

### Terraform assigns Ansible group names

This was the first proposal in this project.

* Good, because the host list comes from Terraform state and nothing is generated.
* Bad, because Ansible group names live in Terraform, so a regroup needs an apply.
* Bad, because grouping ends up in three places: Terraform (`groups`), a static file (parent groups like `swarm` and `docker`), and the `aws_ec2` config for burst workers.

### Terraform assigns labels, `constructed` builds groups

* Good, for the reasons under Consequences.
* Neutral, because the `terraform_provider` plugin has no `keyed_groups`/`compose` support of its own, which is why a separate `constructed` source is needed.

### `community.general.proxmox` inventory plugin

* Good, because it reads VMs straight from the Proxmox API, with no Terraform state involved.
* Bad, because it finds IPs through the QEMU guest agent, which the VM template disables (`agent { enabled = false }`).
* Bad, because groups would have to come from Proxmox tags, a third place for labels.

## More Information

**Correction.** The first proposal put Ansible group names into Terraform (`groups = ["swarm_managers"]`) and the parent groups into a static file. It was replaced before any Ansible code used it: it coupled the two tools, and it gave no uniform way to group burst workers.

**Burst workers** are baked into an AMI by Packer with the same Ansible roles, and only join at boot ([ADR 0003](0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md)). The CI inventory therefore never contains them. An `aws_ec2` source is only needed if Ansible ever configures long-lived AWS hosts. None is planned: the k6 load generator that would have been one runs from the laptop instead ([ADR 0008](0008-run-k6-from-the-laptop-not-an-aws-host.md)).

**Revisit** if the burst tier or the Kubernetes cluster is dropped. Without several host sources, a static `hosts.yml` would be simpler.

Sources:

* [ansible.builtin.constructed](https://docs.ansible.com/projects/ansible-core/devel/collections/ansible/builtin/constructed_inventory.html)
* [How to build your inventory (inventory directories, load order)](https://docs.ansible.com/projects/ansible/latest/inventory_guide/intro_inventory.html)
* [How to combine static and dynamic inventories in Ansible (OneUptime)](https://oneuptime.com/blog/post/2026-02-21-how-to-combine-static-and-dynamic-inventories-in-ansible/view)
* [Constructed inventory in AAP 2.4 (Red Hat)](https://www.redhat.com/en/blog/how-to-use-the-new-constructed-inventory-feature-in-aap-2.4)
* [AWS EC2 dynamic inventory guide](https://docs.ansible.com/projects/ansible/latest/collections/amazon/aws/docsite/aws_ec2_guide.html)
* [cloud.terraform `terraform_provider` plugin source](https://github.com/ansible-collections/cloud.terraform/blob/main/plugins/inventory/terraform_provider.py)
