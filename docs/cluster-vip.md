# Cluster VIP: VRRP (keepalived) and internal DNS

**Status:** 2026-11-18. DNS and the `keepalived` role are live on both clusters. Every load-test run, Swarm and Kubernetes, went through `api.hba.internal` → `10.0.20.100`. Which cluster holds the VIP follows `active_cluster` in Terraform (§4).

**Audience:** anyone pointing a client, k6 or a dashboard at the cluster, and anyone switching between the Swarm and Kubernetes clusters.

**Related:** [docs/network.md](network.md) (address plan, Tailscale paths), [docs/ansible.md](ansible.md) (inventory groups and the `cluster`/`node_role` labels the role uses).

---

## 1. What it does

Clients use one name and one IP, whichever node or orchestrator is behind it:

```
client (tailnet)
  │  api.hba.internal ──(Tailscale split DNS → OPNsense Unbound)──► 10.0.20.100
  ▼
10.0.20.100   held by exactly one cluster node at a time (keepalived MASTER)
  ▼
routing mesh (Swarm) / ServiceLB on :8080 (K8s) → api
```

The DNS record never changes. VRRP moves the IP underneath it. DNS caching and TTLs, the usual weakness of DNS-based failover, therefore do not matter.

**What it does not do (yet):** make the API highly available. `api` and `redis` run only on the control node. If that node fails, the service is down whichever node holds the VIP. Today the VIP gives a stable address that does not depend on node IPs or on the orchestrator. Failover starts to matter once `api` runs on more than one node.

## 2. How VRRP works

VRRP (Virtual Router Redundancy Protocol, RFC 3768 / RFC 5798) lets several machines share one IP. keepalived is the standard Linux implementation.

1. Every node runs keepalived with the same VIP and VRRP ID (`virtual_router_id`), and its own priority.
2. The highest-priority healthy node becomes **MASTER**. It adds the VIP to its interface and sends an advertisement every `advert_int` seconds. The others are **BACKUP**.
3. When advertisements stop for about 3 × `advert_int`, the highest backup becomes master. It sends a **gratuitous ARP** ("10.0.20.100 is now at my MAC"), so OPNsense and the switch update immediately.
4. A failing health check (`track_script`) puts a node into **FAULT**, and it gives up the VIP even though the machine is still up.
5. **Preemption** is on by default: a higher-priority node that comes back takes the VIP back. That is a second short interruption.

Compared with Cisco HSRP from CCNA: same idea, open standard instead of proprietary, Master/Backup instead of Active/Standby, 1 s advertisements instead of 3 s hellos, preemption on instead of off, IP protocol 112 instead of UDP 1985. HSRP's `standby 1 ip`, `priority`, `preempt` and `track` map to keepalived's `virtual_ipaddress`, `priority`, `nopreempt` (inverse) and `track_script`.

## 3. Configuration in this repo

| Setting | Value | Set in |
|---|---|---|
| VIP | `10.0.20.100/24` | `ansible/inventory/group_vars/all.yml` (`cluster_vip`) |
| Cluster that may hold it | `swarm` or `k8s` | `terraform/proxmox/variables.tf` (`active_cluster`), handed to Ansible as a host variable |
| VRRP ID | `51` | `roles/keepalived/defaults/main.yml` |
| Advertisement interval | 1 s | role defaults |
| Priority | control node 150, workers 100 (tie: highest IP wins) | role defaults, from the `node_role` label |
| Peers | unicast to the other nodes of the same cluster | role defaults, from `groups[cluster]` |
| Health check | `curl` to `http://127.0.0.1:8080/api/Test` every 2 s, timeout 1 s; FAULT after 2 failures, healthy after 2 successes | role defaults |

**Why these choices:**

- **Unicast, not multicast.** Standard VRRP uses multicast `224.0.0.18`. Unicast peers (a keepalived extension) do not depend on multicast working through the Proxmox bridge. A firewall between the nodes must allow IP protocol 112.
- **The control node has the highest priority** because it runs `api`. With the VIP there, requests skip one routing-mesh hop.
- **Labels, not group names.** The role uses `node_role` and `groups[cluster]` ([ADR 0002](adrs/0002-terraform-labels-ansible-constructed-inventory.md)), so it runs unchanged on the Kubernetes nodes.
- **The health check is temporary.** It will point to `/healthz/live` once the API has it, and to Traefik's `/ping` once Traefik is deployed. Through the routing mesh, the current check tests whether the cluster can serve the API, not only the local node. If `api` is down, every node fails the check and the VIP disappears, which is the honest answer to "can this cluster serve requests".

## 4. Only one cluster holds the VIP

The Swarm and Kubernetes node sets use the **same VIP**, so DNS, Tailscale and the k6 scripts do not change between orchestrators. Both orchestrators are then tested through an identical entry path.

If both sets ran keepalived at once, each set would elect its own master (unicast peers only see their own cluster). The result is a duplicate IP and requests landing on the wrong cluster. `active_cluster` prevents this in two ways: Terraform keeps only that cluster's VMs powered on, and keepalived is started and enabled only on hosts whose `cluster` label matches (the role stops and disables it on any other host it runs on). Since the pipeline runs with `--limit active`, the first of the two is what protects the stopped set (below).

**Switching orchestrators** (one variable since PR #90; [ADR 0010](adrs/0010-k3s-as-a-second-vm-set-switched-by-active-cluster.md)):

1. Change `active_cluster` in `terraform/proxmox/variables.tf` (`swarm`, `k8s` or `none`) in a pull request and merge it.
2. The Terraform pipeline powers the other set off and this set on (`started` and `on_boot` follow the variable), and records `active_cluster` on every host in the inventory.
3. The Ansible pipeline runs with `--limit active`, so it only touches the powered-on set; there keepalived is enabled, and its master claims the VIP.

**Never power a set on by hand in Proxmox.** With `--limit active`, Ansible never contacts the stopped set, so its keepalived keeps the state of its last run: still enabled, from when it was active. Powered on by hand, it would claim the VIP next to the active set, and both sets on at once also need more RAM than the host has ([ADR 0006](adrs/0006-cluster-size-one-control-three-workers.md)). The switch to Kubernetes was verified on 2026-11-17 (four VMs up, Swarm off, VIP on `k8s-ctl`); **the switch back to Swarm has not been run yet.**

**How Kubernetes answers on the VIP.** Traefik is switched off on both clusters, so neither has an ingress controller; the request path stays the same as on Swarm. The `api` Service is of type `LoadBalancer` on port 8080, and k3s's built-in ServiceLB answers it on every node, like Swarm's routing mesh. The URL `http://api.hba.internal:8080` is the same for both. It costs one more hop: ServiceLB forwards to kube-proxy, which forwards to the pod, about 3 ms more per request ([loadtest.md](loadtest.md) §4.7, finding 3).

## 5. DNS

`hba.internal` uses `.internal`, which ICANN reserved for private networks in 2024. The names are never visible on the public internet. There is no public domain, and so no public TLS certificate: traffic inside the tailnet is already encrypted by WireGuard.

**These settings are manual (OPNsense GUI and Tailscale admin console).** They are set once and do not change when nodes or orchestrators change.

| Where | Setting |
|---|---|
| OPNsense → Unbound DNS → Overrides → Hosts | Host `*`, domain `hba.internal`, type A, IP `10.0.20.100`, **no PTR** (a PTR for a wildcard would return the literal `*.hba.internal`) |
| OPNsense → Unbound DNS → Access Lists | Allow `100.64.0.0/10` (Tailscale clients), otherwise their queries get `REFUSED` |
| Tailscale → DNS → Nameservers | Custom nameserver `10.0.20.1`, restricted to `hba.internal` (split DNS) |

The wildcard also answers for the bare `hba.internal`.

**Clients that need it:** laptops, which also run k6 ([ADR 0008](adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)), and the CI Pi on the tailnet (they must accept Tailscale DNS settings). Burst workers do not: they join the swarm by IP, and inside containers Swarm's own DNS resolves service names.

**AWS: no DNS is needed (decided 2026-10-24).** Nothing in the VPC has to resolve `hba.internal`: burst workers join by IP, and k6 runs from the laptop ([ADR 0008](adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)). The Route 53 private hosted zone that was planned for an AWS k6 host (`*.hba.internal` → `10.0.20.100`, about $0.50/month) is dropped. If a host in the VPC ever needs the name, that zone is the cheap way; the Route 53 Resolver outbound endpoint forwarding `hba.internal` to OPNsense costs about $180/month and is not used.

## 6. Checking it

```powershell
Resolve-DnsName api.hba.internal                     # through split DNS → 10.0.20.100
Resolve-DnsName api.hba.internal -Server 10.0.20.1   # Unbound directly; isolates Tailscale problems
```

| DNS symptom | Cause |
|---|---|
| Direct query times out | Firewall rule for TCP/UDP 53 on the Tailscale interface, or the Tailscale grant lacks port 53 |
| `REFUSED` | Unbound access list |
| `NXDOMAIN` | Override not applied, or a typo in the domain |
| Direct works, normal lookup fails | Split DNS entry, MagicDNS off, or the client does not accept Tailscale DNS |

On the nodes (once the role is deployed):

- `ip -br addr`: the VIP is on exactly one node.
- `journalctl -u keepalived`: `Entering MASTER STATE` / `Entering BACKUP STATE`.
- OPNsense → Interfaces → Diagnostics → ARP Table: the VIP's MAC changes on failover.

**Failover test:** curl `http://api.hba.internal:8080/api/Test` in a loop every 200 ms, then stop keepalived on the master. The number of failed requests is the failover gap.

## 7. Known limitations

- **Not HA for the API** (§1).
- **Layer 2 only.** VRRP relies on ARP inside one network segment. AWS VPCs ignore gratuitous ARP, so the cloud equivalent is a load balancer or moving an Elastic IP. Not a problem here: the VIP stays on-prem.
- **No VRRP authentication.** Any host on VLAN 20 could announce itself as master. VRRPv3 dropped authentication; VRRPv2's password is sent in clear text anyway. The protection is that VLAN 20 is only reachable through OPNsense.
- **DNS is partly manual** (§5), the one exception to configuration as code here.

## 8. Sources

- [keepalived configuration synopsis](https://keepalived.readthedocs.io/en/latest/configuration_synopsis.html)
- [RFC 5798: VRRP version 3](https://datatracker.ietf.org/doc/html/rfc5798), [RFC 3768: VRRP version 2](https://datatracker.ietf.org/doc/html/rfc3768)
- [kubeadm HA considerations (keepalived + HAProxy, kube-vip)](https://github.com/kubernetes/kubeadm/blob/main/docs/ha-considerations.md)
- [Docker: Swarm routing mesh](https://docs.docker.com/engine/swarm/ingress/)
- [OPNsense: Unbound DNS](https://docs.opnsense.org/manual/unbound.html)
- [Tailscale: DNS and split DNS](https://tailscale.com/kb/1054/dns)
- [AWS: Route 53 private hosted zones](https://docs.aws.amazon.com/Route53/latest/DeveloperGuide/hosted-zones-private.html)
