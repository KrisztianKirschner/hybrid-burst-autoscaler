# Terraform: Layout, State and Pipeline

**Status:** 2026-09-29. `terraform/proxmox` is live. `terraform/aws` is planned and not written yet.

**Audience:** anyone changing the infrastructure code: adding a VM, changing the VM template, or adding the AWS root.

**Related:** [docs/ansible.md](ansible.md) (what happens on the VMs after Terraform creates them), report section 2.3 (why the CPU and disk options are set the way they are).

---

## 1. What Terraform owns

**Terraform decides what exists; Ansible decides what runs on it.**

| Terraform owns | Terraform does not own |
|---|---|
| The Ubuntu cloud image and the VM template on Proxmox | Anything installed inside a VM (Docker, Swarm, …): that is Ansible |
| Every on-prem VM: name, VMID, IP, size | Swarm services and Kubernetes objects: that is `stacks/` |
| Each VM's inventory entry for Ansible (`ansible_host`) | Burst EC2 instances: planned to be launched by a script, not by an apply per scale event |
| Later: AWS scaffolding (VPC, subnet, security group, S3) | |

## 2. Layout

```
terraform/
  proxmox/                 # root config: on-prem, homelab-core-01
    backend.tf             # S3 remote state
    version.tf             # Terraform + provider versions
    providers.tf           # Proxmox API connection
    variables.tf           # endpoint, API token, SSH key (all sensitive)
    template.tf            # cloud image download + VM template 9999
    main.tf                # local.vms: the list of VMs, and the vm module call
    inventory.tf           # ansible_host resources for the Ansible inventory
    .terraform.lock.hcl    # pinned provider versions: commit it
    modules/
      vm/                  # one cloned VM (main.tf, variables.tf, version.tf)
  aws/                     # planned: second root config, own state
```

**Two root configs, not one.** `proxmox/` and `aws/` are separate roots with separate state. They need different credentials, change at a different pace, and a mistake in one should not be able to touch the other. A shared root would mean every plan needs both Proxmox and AWS credentials.

## 3. State

```hcl
backend "s3" {
  bucket       = "hybrid-burst-tfstate"
  key          = "proxmox/terraform.tfstate"   # aws/ will use "aws/terraform.tfstate"
  region       = "eu-central-1"
  use_lockfile = true
  encrypt      = true
}
```

- **One bucket, one key per root.** The bucket was created by hand once, with versioning on, so every earlier state can be restored.
- **`use_lockfile = true`** uses S3's native lock file (Terraform 1.10+). No DynamoDB table is needed. This is why `required_version` is `>= 1.10.0`.
- **The state contains the inventory.** Ansible reads its hosts from this state (see [docs/ansible.md](ansible.md)), so running Ansible also needs read access to it.

## 4. The VM template (`template.tf`)

1. `proxmox_download_file` fetches a **pinned, dated** Ubuntu 26.04 cloud image (`resolute/20260921`), not `current`. A new image would change the guest OS between test runs; changing the date is a deliberate, reviewed change.
2. `proxmox_virtual_environment_vm.template` imports it as **template 9999** (`ubuntu-2604-template`): q35 machine, 2 cores of CPU type `host`, 4 GB, 20 GB VirtIO disk with `discard` and `iothread`, serial console, cloud-init user `proxima` with the SSH key.

The CPU type and disk options are not defaults. The reasons are in report section 2.3 ("Guest CPU model", "Guest disk options").

The QEMU guest agent is disabled (`agent { enabled = false }`). Terraform does not wait for it, and nothing reads IPs through it: IPs are static and come from `main.tf`.

## 5. VMs (`main.tf` + `modules/vm`)

All VMs are defined in **one map**, `local.vms`. The module is called once per entry with `for_each`.

```hcl
"swarm-ctl" = {
  vmid       = 211
  ip_address = "10.0.20.11/24"
  disk_size  = 30
  cluster    = "swarm"     # label for Ansible
  node_role  = "control"   # label for Ansible
}
```

| Field | Required | Default (module) | Notes |
|---|---|---|---|
| key (`"swarm-ctl"`) | yes | | VM name and hostname |
| `vmid` | yes | | Convention: 200 + last IP octet |
| `ip_address` | yes | | CIDR, static |
| `cluster` | yes | | `swarm`, `k8s`, `none`: read by Ansible, not by the module |
| `node_role` | yes | | `control`, `worker`, `observability`: read by Ansible, not by the module |
| `cores` | no | 2 | |
| `memory` | no | 4096 (MB) | |
| `disk_size` | no | 30 (GB) | |

Fixed in the module: node `pve`, storage `local-lvm`, bridge `vmbr1` on VLAN 20, gateway `10.0.20.1` (OPNsense), user `proxima`, CPU type `host`. Each VM is a full clone of template 9999 and gets its IP and SSH key through cloud-init.

Optional fields are passed as `try(each.value.cores, null)`. The module variables are `nullable = false`, so a `null` falls back to the module default instead of reaching the provider.

## 6. Hand-off to Ansible (`inventory.tf`)

```hcl
resource "ansible_host" "vm" {
  for_each = local.vms
  name     = each.key
  variables = {
    ansible_host = split("/", each.value.ip_address)[0]   # IP without /24
    ansible_user = "proxima"
    cluster      = each.value.cluster
    node_role    = each.value.node_role
  }
}
```

This uses the `ansible/ansible` provider. It creates nothing on Proxmox; it only writes an entry into Terraform state, which Ansible's `cloud.terraform.terraform_provider` inventory plugin reads. **No group names are set here.** Groups are built from the two labels on the Ansible side, so regrouping never needs a Terraform change. Details: [docs/ansible.md](ansible.md).

The resources must stay in the **root** module (`terraform/proxmox`), not in `modules/vm`: the inventory plugin ignores child modules by default.

## 7. Variables and credentials

| Variable / env | Source in CI | Used for |
|---|---|---|
| `TF_VAR_PROXMOX_ENDPOINT` | secret `PROXMOX_ENDPOINT` | Proxmox API URL |
| `TF_VAR_PROXMOX_API_TOKEN` | secret `PROXMOX_API_TOKEN` | dedicated Proxmox API token, not a user password |
| `TF_VAR_ssh_public_key` | variable `SSH_PUBLIC_KEY` | cloud-init key for user `proxima` |
| `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` | secrets `TERRAFORM_STATE_AWS_*` | S3 state only (IAM user `tf-state-proxmox`) |

All three Terraform variables are `sensitive = true`, so their values are masked in plan output. That matters because the repo is public and Actions logs are world-readable.

`providers.tf` sets `insecure = true`: the Proxmox API uses a self-signed certificate. Acceptable on the homelab LAN; revisit if the API is ever reached over an untrusted network.

## 8. CI pipeline

Two workflows, split by what they need access to.

**`terraform-scan.yml`: GitHub-hosted runner, no credentials.** Runs on every PR and push that touches `terraform/`, once per root (matrix, currently `[proxmox]`):

1. `terraform init -backend=false` (no state access needed)
2. `terraform fmt -check -recursive`
3. `terraform validate`
4. `tflint --recursive`, results uploaded as SARIF to GitHub code scanning. Findings fail the job.

**`terraform-proxmox.yml`: self-hosted Pi runner, real credentials.**

| Trigger | Job | What happens |
|---|---|---|
| PR touching `terraform/proxmox/**` | `terraform-plan` | `init` + `plan`. PRs from forks are skipped (they get no secrets). |
| Merge to `main` | `terraform-plan`, then `terraform-apply` | `apply` waits for approval in the `proxmox` GitHub Environment, then re-plans to a file and applies exactly that file. |

- **Only the Pi can change real infrastructure.** It is the only runner on the homelab LAN and the only place holding the Proxmox token.
- **Plan again before apply.** `main` may have moved since the PR's plan, so the apply job makes a fresh plan and applies that saved plan, not a new one computed during apply.
- **Never cancel mid-apply.** `cancel-in-progress: false` on both the workflow and the apply job. A cancelled apply can leave a half-created VM and a stale lock.
- `-lock-timeout=5m` waits for a lock held by another run instead of failing at once.

## 9. Working locally

Local machines run `plan` only. `apply` runs only in CI.

```bash
cd terraform/proxmox

# format + validate: no credentials needed
terraform init -backend=false
terraform fmt -recursive
terraform validate

# plan: needs state access and the TF_VAR_* values in the environment
terraform init
terraform plan
```

After adding or upgrading a provider, run `terraform init -upgrade` and **commit the updated `.terraform.lock.hcl`**. CI installs exactly what the lock file lists.

## 10. Common changes

**Add a VM:** add an entry to `local.vms` with `vmid`, `ip_address`, `cluster` and `node_role`. Open a PR and check that the plan shows one `module.vm[...]` and one `ansible_host.vm[...]` to add, and nothing else changing. After merge and apply, Ansible picks it up with no further change.

**Change the Ubuntu image:** change the dated URL in `template.tf`. This replaces the template. Existing VMs are full clones and are not rebuilt automatically, so re-create them deliberately if they should run the new image.

**Resize a VM:** set `cores`, `memory` or `disk_size` on its entry. Check the plan: a disk can grow in place but not shrink.

## 11. Known gaps

- **The user name `proxima` is written in three places:** `template.tf`, the module default in `modules/vm/variables.tf`, and `inventory.tf`. A single `local` would remove the risk of them drifting apart.
- **`modules/vm/outputs.tf` is empty.** Nothing needs module outputs yet; the inventory reads IPs from `local.vms` directly.
- **Powering a cluster off is not modelled yet.** The plan is to keep both cluster VM sets defined and have only the one under test running. The module has no `started` switch yet, so this is currently done by hand in Proxmox, and the next apply may try to start the VMs again.
- **`terraform/aws` does not exist yet.** When it does, add `aws` to the `terraform-scan.yml` matrix and create a matching `terraform-aws.yml` pipeline with its own `aws` Environment.
- **`templates/` in `terraform/proxmox` is empty.** Remove it, or use it once there is something to template.
