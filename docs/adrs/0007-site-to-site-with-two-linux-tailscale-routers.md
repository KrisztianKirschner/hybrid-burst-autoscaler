---
status: accepted
date: 2026-10-20
decision-makers: Krisztián Kirschner
---

# Connect the VPC and the lab with two Linux Tailscale subnet routers

## Context and Problem Statement

Burst workers on EC2 must join the same Swarm (and later Kubernetes) cluster as the on-prem VMs. Swarm is a full mesh: every node must reach every other node's advertised address in both directions on 2377/tcp (managers), 7946/tcp+udp and 4789/udp, and the manager identifies a node by the address it sees. The lab VLAN `10.0.20.0/24` sits behind OPNsense and is reachable from outside only through Tailscale. How do burst nodes and the k6 load generator get a working, measurable path to the lab?

[network.md §2](../network.md) planned a site-to-site link with OPNsense as the lab-side router and SNAT turned off. On 2026-10-17 this ADR was first drafted (as `proposed`) to replace the AWS half with Tailscale on every burst node. Testing then broke the OPNsense half of both plans (below), and the AWS half was decided the other way.

## Decision Drivers

* **Swarm must see each node's real address.** A rewritten source address makes every burst node look like the router.
* **Time to capacity is measured** ([ADR 0003](0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md)). Each extra step at boot is on the burst critical path.
* **Measurements must not be distorted.** A relayed (DERP) path adds latency and varies.
* **Secrets on burst nodes are exposed** to anything that reaches the metadata service, including containers.
* **Little always-on cost**, and no change to the home network.

## Findings that shaped the decision

1. **SNAT cannot be turned off on OPNsense.** A burst node's ping arrived at `swarm-ctl` from `10.0.20.1`. The outbound NAT page has only automatic WAN rules, the Tailscale plugin has no SNAT setting, and Tailscale documents `--snat-subnet-routes=false` as [Linux only](https://tailscale.com/kb/1019/subnets); OPNsense is FreeBSD.
2. **Swarm does not tolerate it (test A, 2026-10-20).** A burst node joined with `--advertise-addr` and `--data-path-addr` set to its `100.x` address. `docker node inspect` showed `Status.Addr` **`10.0.20.1`**, and attaching a container to an overlay network failed with `context deadline exceeded`.
3. **There is double NAT.** OPNsense's WAN address is `192.168.100.38`, behind the home gateway `192.168.100.1`, which randomises ports (`tailscale netcheck`: `MappingVariesByDestIP: true`). OPNsense's static-port rule works, but cannot fix the outer layer. A **direct** path still forms when the AWS peer has a public IP and an open UDP port: `pong … via 3.72.95.67:41641 in 14ms`, against 52 ms through DERP.

## Considered Options

* Tailscale on each burst node, OPNsense as the lab router (the 2026-10-17 proposal)
* Tailscale on each burst node, plus a Linux lab router
* **A Linux Tailscale router on each side (site-to-site), burst nodes without Tailscale**
* Tailscale on every Swarm VM (no lab router)
* AWS Site-to-Site VPN (managed IPsec to OPNsense)
* A private subnet with a NAT gateway

## Decision Outcome

Chosen option: "A Linux Tailscale router on each side".

```
burst 10.100.x ─► VPC route ─► AWS gateway (t3.micro) ══ WireGuard (direct) ══► ts-gateway 10.0.20.2 ─► Swarm VMs 10.0.20.11–.14
                               tag:aws-router, SNAT off                         tag:lab-router, SNAT off
                               advertises 10.100.0.0/16                          advertises 10.0.20.8/29
```

* **Lab side:** an unprivileged LXC container `ts-gateway` at `10.0.20.2` with `/dev/net/tun` passed through. It advertises only **`10.0.20.8/29`**, the block holding the Swarm VMs. Tailscale prefers the more specific route, so everything else in the `/24` (`obs-01`, the VIP) still goes through OPNsense. Its Tailscale listens on **UDP 41642**, because OPNsense's own Tailscale uses 41641; OPNsense has a static-port outbound NAT rule for it.
* **AWS side:** one gateway instance (`t3.micro`, x86 like everything else) in a public subnet, `source_dest_check` off, a VPC route `10.0.20.0/24` → its network interface, and **inbound UDP 41641** in its security group, which is what makes the path direct despite the double NAT. Stopped between test runs.
* **Return routes on the cluster VMs:** `10.100.0.0/16` and `100.64.0.0/10` via `10.0.20.2`. The second is needed because the `/29` also carries SSH and Ansible from tailnet devices (laptop, CI Pi) to these VMs.
* **Grants on subnets**, not tags, since neither router rewrites addresses.
* Burst nodes join Swarm with their plain VPC address and run no Tailscale.
* **The gateway's Tailscale secret is out of the burst nodes' reach.** It lives under `/hba/gateway/*`, the burst nodes' parameters under `/hba/burst/*`, and each instance role may read only its own path. The burst role no longer has `AmazonSSMManagedInstanceCore`: that managed policy allows `ssm:GetParameter` on every parameter, so any process on a burst node (containers included, through the metadata service) could have read the gateway secret and, through `autoApprovers`, created a router for `10.100.0.0/16`. Both roles get only the five actions Session Manager needs instead (`ssm:UpdateInstanceInformation`, `ssmmessages:Create/OpenControlChannel`, `ssmmessages:Create/OpenDataChannel`). Considered and rejected: an explicit Deny on the gateway path (unclear whether it also blocks a recursive `GetParametersByPath` on `/hba`), a customer-managed KMS key for the secret (works, about $1/month, more to manage), and accepting the risk.

### Consequences

* Good, because Swarm sees real addresses in both directions, which is the requirement test A showed cannot be worked around.
* Good, because burst nodes hold no Tailscale credentials and do no Tailscale login at boot.
* Good, because one direct path is set up before a run and can be checked (`tailscale ping`), instead of one path per new node negotiated through a hard NAT.
* Good, because it is the classic hybrid-cloud pattern, which is easy to explain, and the home network stays unchanged.
* Bad, because the AWS gateway is a single point of failure during a run, and must be started before each test (a step for the test runner).
* Bad, because SSH and Ansible to the Swarm VMs now depend on `ts-gateway` as well. If it fails, disabling its route in the admin console restores the OPNsense path only after the `100.64.0.0/10` return route is also removed.
* Bad, because every cluster VM needs two extra routes, including the Kubernetes VMs later.
* Neutral: about $0.01/h while the gateway runs; under $1/month for its disk while stopped.
* Neutral: overlay networks still need MTU 1230 (Tailscale 1280 minus the 50-byte VXLAN header). *Correction 2026-11-17: never applied and not needed; MSS clamping on the routers and TCP's own path-MTU handling were enough on Swarm and Kubernetes ([network.md](../network.md) §2.3).*

### Lessons from the rollout (kept on purpose)

* The `/29` route was advertised before the Swarm VMs had their return routes, and SSH from the laptop timed out (ping still worked). **Add the return routes first, then advertise the route.**
* A router address used in examples (`10.0.20.5`) differed from the one actually given to the container (`10.0.20.2`). The router address must be one variable, used by both the container and the routes.
* `tailscale netcheck` reported a random port even with the static-port rule in place; `pfctl -ss` on OPNsense showed the rule working and the private WAN address, which revealed the double NAT.

## Pros and Cons of the Other Options

### Tailscale on each burst node, OPNsense as the lab router

* Good, because it needs nothing new on either side.
* Bad, because it does not work: test A.

### Tailscale on each burst node, plus a Linux lab router

* Good, because no AWS instance and no AWS single point of failure; the pieces were already built and tested.
* Bad, because every burst node holds an OAuth secret that needs **Devices: Core (Write)**, logs in at boot, and negotiates its own path through the double NAT. Kept as the fallback if the gateway work slips.

### Tailscale on every Swarm VM

* Bad, because lab-to-lab traffic would be tempted onto the tunnel, which changes what the comparison measures, and the swarm would have to be rebuilt on `100.x` addresses.

### AWS Site-to-Site VPN

* Good, because AWS runs the gateway, and routed IPsec does not SNAT, so no lab router would be needed.
* Bad, because about $0.05/h per connection, tunnel setup takes minutes, and it needs a reachable public address on the lab side, which the double NAT does not give.

### A private subnet with a NAT gateway

* Bad, because everything behind an AWS NAT gateway connects through DERP relays only ([Tailscale AWS reference architecture](https://tailscale.com/docs/reference/reference-architectures/aws)), and it costs money while idle.

## More Information

Build details, addresses and the test procedure: [network.md §2.2](../network.md). Tailscale credentials: [aws.md §4](../aws.md). Related: [ADR 0003](0003-configure-onprem-with-ansible-bake-burst-workers-with-packer.md).

Not built yet (2026-10-20): the AWS gateway in `terraform/aws`, the permanent netplan routes, and `ts-gateway` as code (Terraform container + Ansible role).
