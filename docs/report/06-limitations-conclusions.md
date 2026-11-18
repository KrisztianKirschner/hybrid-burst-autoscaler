# 6. Limitations & Conclusions

## 6.1 Limitations

- **One physical host** carries every on-prem VM. CPU steal stayed at 0, but the six workers share caches and memory bandwidth (jobs run 10 % slower in parallel than alone).
- **Three runs per condition,** too few for confidence intervals; ranges are reported instead. The runs of each condition agree within about 1 % in throughput and 3 % in queue peak (one early-burst exception, Section 5.2), so the differences reported are systematic, but small effects rest on one load profile.
- **One load profile** (a warm-up below capacity, then a ramp). A steady or falling load could favour the fresher Kubernetes signal; that is untested.
- **The load generator is a laptop** on a home line, not an isolated host ([ADR 0008](../adrs/0008-run-k6-from-the-laptop-not-an-aws-host.md)); no run dropped an iteration, so it never limited the load.
- **k3s, not upstream Kubernetes,** and the worker runs without AppArmor confinement there. The cause of the 2.5 % slower jobs is not isolated.
- **Swarm and Kubernetes runs were on different days** (2026-10-24 to 2026-11-17), with the same host, images and profile.
- **Burst nodes boot from a stock image,** so time-to-capacity includes the software install, and they export no host metrics.

## 6.2 Reflection

Several first assumptions were wrong and were corrected by measurement, as recorded in the ADRs: a `c6a.large` "2 vCPU" node turned out to be one physical core; the Swarm autoscaler oscillated until it copied the HPA's 300 s rule; Swarm silently stranded Pending tasks, found only in its source code; the Snap Store failed burst boots on one evening; containerd's AppArmor profile killed the .NET worker. Each became a finding rather than a footnote. The largest Swarm-versus-Kubernetes difference first looked like the orchestrator and turned out to be half plumbing (the age of the signal), which only a controlled extra experiment could show.

## 6.3 Conclusion

**Cloud bursting delivers what the architecture promised, on both orchestrators:** two rented nodes lift a saturated on-prem cluster by **+63 %**, cut the queue peak by two thirds or more and the backlog by three minutes, for **about 5 cents per peak**. Half of that cost is paid after the work, and set by the scale-down policy, not the orchestrator.

**Swarm and Kubernetes behave almost the same under one shared rule.** Kubernetes needs no custom code to autoscale (34 lines of YAML against 120 lines of Python) and no scheduler workaround to burst, and fills new nodes faster; Swarm's jobs run about 2.5 % faster, and its stale signal happened to give it headroom on a rising load. The choice between them should rest on operations and ecosystem, not on autoscaling performance, which this workload shows to be a matter of configuration more than of orchestrator.
