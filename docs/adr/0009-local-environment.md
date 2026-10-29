# ADR-0009 — Local environment: devenv + kind + devspace + just, plain manifests

- **Status:** Accepted (author decision, 2026-10-21; the optional design-review call was not held)
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** 04-environment

## Context
Mandated tooling: devenv, devspace, just, local Kubernetes. Graded on "single command from a clean checkout, zero manual steps".

## Decision
- **devenv** pins every CLI (incl. .NET 10 SDK, kind, kubectl, devspace, just, jq, valkey-cli, psql). `kcat` was dropped: it does not build from devenv-nixpkgs; topics are read with `kafka-console-consumer.sh` inside the Kafka pod.
- **kind** as the local cluster (runs on any Docker host; created by `just up` if absent).
- **devspace** builds images and deploys **plain YAML manifests** in `k8s/`.
- Connector registered by an idempotent Kubernetes **Job** (`PUT /connectors/{name}/config`).
- **just** is the only interface; recipes wait on readiness, never on `sleep`.

## Alternatives considered
| Option | Rejected because |
|---|---|
| Strimzi operator (Kafka + KafkaConnector CRDs) | Production-grade, but operator install + CRD reconciliation adds minutes and moving parts to a cold start |
| Helm charts (Bitnami etc.) | Opaque values, image/licensing churn; plain manifests are short and reviewable |
| k3d / minikube | Equivalent; kind is the most common in CI and needs no VM |
| docker-compose | Not Kubernetes — fails the brief |

## Consequences
+ Everything in the C4 Container diagram is a manifest a reviewer can read in a minute.
− Not HA, RF=1 — explicitly a local environment.
