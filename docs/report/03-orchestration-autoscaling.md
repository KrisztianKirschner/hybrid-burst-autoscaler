# 3. Orchestration & Autoscaling: Swarm vs. Kubernetes

## 3.1 Docker Swarm — Custom Controller

**Build and deploy path.**

- App images are built and tested on GitHub-hosted `ubuntu-latest` runners, which are natively `linux/amd64` like the cluster VMs and EC2 workers. On `main` they are pushed to GHCR with a `sha-<commit>` tag; pull requests only build.
- The stack file (`stacks/swarm/hba.yml`) references that exact tag. A new image version reaches the cluster only through a reviewed commit that changes the tag.
- The Ansible `stacks` role deploys it from the manager with `docker_stack` (`prune: true`, so services removed from the file are also removed from the cluster), on the Pi runner after manual approval.

**Overlay address pool, and a correction.** Swarm's default overlay pool, `10.0.0.0/8`, contains the lab network `10.0.20.0/24`. An overlay network could therefore be given the lab's own range and cut containers off from real lab hosts such as `obs-01`. The `swarm` role sets the pool to `10.200.0.0/16` instead (address plan: `docs/network.md`). The first attempt didn't take effect: a swarm left over from earlier manual testing already existed, and the Ansible module treated "swarm present" as done without checking the pool. The pool can only be set at `swarm init`, so the swarm had to be recreated.

## 3.2 Kubernetes — KEDA

## 3.3 Comparison

## 3.4 Scope Notes
