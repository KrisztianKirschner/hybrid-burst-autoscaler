# 1. Introduction

## 1.1 Problem Statement

Container orchestrators are usually compared on paper — feature matrices, marketing claims about "Kubernetes has autoscaling, Swarm doesn't." Few comparisons measure actual autoscaling *behavior* under a real, running, queue-driven workload, and fewer still look at what happens when local capacity runs out and the system has to burst into a public cloud to keep up.

## 1.2 Goal & Thesis

This project builds a small queue-based image processing service and runs it, under measured load (k6), on both Docker Swarm (a hand-rolled autoscaling controller) and Kubernetes (KEDA), in a hybrid architecture that bursts from on-premises hardware into AWS once local worker capacity saturates. The goal is not to declare a "winner" — it's to measure and report the concrete behavioral difference between a hand-rolled controller and a declarative autoscaler responding to the same queue-depth signal, and to get genuine hands-on experience with the course's required tooling (Docker, Docker Swarm, Kubernetes, Terraform, AWS).

The central argument: cloud bursting only makes economic sense if the on-prem plateau and the cloud-triggered lift are both real, measured events — not assumed ones. That measurement, not a working demo alone, is the actual deliverable.

## 1.3 Scope

**In scope:** Docker Swarm vs. Kubernetes autoscaling comparison (custom Python controller vs. KEDA `ScaledObject`); Terraform/Ansible-provisioned on-premises cluster; an AWS burst tier joined over a Tailscale mesh link; k6-driven load testing; Prometheus/Grafana observability.

**Explicitly out of scope**, with full reasoning kept in the decision records (`docs/adrs/`):
- OpenNebula — would duplicate Proxmox's role and, more importantly, would outsource the exact bursting logic this project exists to build by hand.
- A CPU/memory-based HPA on its own (it doesn't fit a queue-depth design; KEDA drives an HPA with the queue instead) and Cluster Autoscaler/Karpenter (the "real" version of the bursting logic built here by hand, but with no Swarm equivalent): mentioned for context, not implemented.
- A friend's closed-source application as the core workload — shelved over confidentiality concerns unrelated to any technical constraint.
