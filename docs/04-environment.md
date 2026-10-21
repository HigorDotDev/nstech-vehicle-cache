# 04 — Reproducible Environment

Status: **Draft** — validate everything here in Phase 0 (env spike) before writing worker code. Graded at 20%: "if it needs a README of manual fixes, it failed".

## 1. Tooling layers

| Layer | Tool | Owns |
|---|---|---|
| Toolchain pinning | `devenv` (Nix) | `dotnet-sdk_10`, `kind`, `kubectl`, `devspace`, `just`, `jq`, `valkey` (cli), `postgresql_17` (psql) — same versions for everyone |
| Cluster | `kind` | single-node cluster `vehicle-cache` |
| Build + deploy | `devspace` | builds the 3 .NET images, loads them into kind, applies `k8s/` manifests |
| Orchestration | `just` | the only entry points: `up`, `e2e`, `down`, `clean`, plus helpers |

Reviewer prerequisites (README): Linux/macOS (or WSL2 Ubuntu) with **Docker** and **Nix + devenv** installed. Everything else comes from devenv. Entry: `devenv shell` then `just up` / `just e2e` (or `devenv shell -- just e2e`).

> **Author's machine (Windows 11):** devenv requires Nix ⇒ work inside a **WSL2 Ubuntu distro** (only `docker-desktop` exists today), with Docker Desktop's WSL integration enabled for it, and the repo cloned inside the Linux filesystem (`~/src`, not `/mnt/c` — file watching and I/O are far slower there). Install the .NET 10 SDK only via devenv; the Windows SDKs are irrelevant.
>
> **Host requirement: cgroup v2.** kind 0.32's default node runs Kubernetes ≥ 1.35, which refuses cgroup v1. Modern Linux distros and macOS Docker already use v2. On WSL2, Docker reported `CgroupVersion=1`; fixed with `kernelCommandLine = cgroup_no_v1=all` in `%USERPROFILE%\.wslconfig`, then `wsl --shutdown` and a full Docker Desktop restart. Check: `docker info --format '{{.CgroupVersion}}'` → `2`. Also set: `memory=10GB`, `swap=4GB`, and `appendWindowsPath=false` in `/etc/wsl.conf` so only devenv tools are on PATH.

## 2. Kubernetes layout (`k8s/`, plain manifests — ADR-0009)

`k8s/infra/` is deployed by devspace; `k8s/jobs/` are one-shot steps applied by `just`. Config files stay in `infra/` and are turned into ConfigMaps by `just config` (`postgres-init` from `infra/postgres/`, `debezium-connector` from `infra/debezium/connector.json`).

| Manifest | Kind | Image (pinned) | Notes |
|---|---|---|---|
| `k8s/infra/postgres.yaml` | StatefulSet + Service + PVC | `postgres:17.6` | `-c wal_level=logical -c max_replication_slots=4 -c max_wal_senders=4`; init SQL mounted from ConfigMap |
| `k8s/infra/kafka.yaml` | StatefulSet + Service + PVC | `apache/kafka:3.9.1` | KRaft single node, RF=1, 3 partitions, `log.retention.ms=-1`, `enableServiceLinks: false` |
| `k8s/infra/connect.yaml` | Deployment + Service | `quay.io/debezium/connect:3.2.0.Final` | internal topics RF=1; topics created by the connector (`topic.creation.default.*`) |
| `k8s/infra/valkey.yaml` | Deployment + Service | `valkey/valkey:8.1.3` | no persistence (cache is rebuildable — that is the point) |
| `k8s/jobs/register-connector.yaml` | Job | `curlimages/curl:8.14.1` | `PUT /connectors/fleet/config` (idempotent), then polls until connector + task RUNNING |
| `k8s/app/worker.yaml` | Deployment (1 replica) | `vehicle-cache-worker` (built by devspace from `src/VehicleCache.Worker/Dockerfile`: `sdk:10.0.401` → `runtime:10.0.12`, non-root) | env: Kafka bootstrap, Valkey endpoint — **no Postgres** |
| `k8s/jobs/writers-seed.yaml`, `writers-scenario.yaml` | Job (`backoffLimit: 0`) | `vehicle-cache-writers:local` (devspace, fixed tag because `just` applies Jobs directly) | `seed`; `scenario scenarios/e2e.json --rate 150` |
| `k8s/jobs/verifier-*.yaml` | Job | `vehicle-cache-verifier` | `expect`, `dump` — *Phase 5* |

kind node image is not pinned separately: the kind version is pinned by `devenv.lock`, and each kind release pins its default node image.

Readiness: every long-running container has a readiness probe; `just` waits with `kubectl wait`/`kubectl rollout status`, never with `sleep`.

Gotcha (Phase 0): Kubernetes service links inject `KAFKA_PORT=tcp://…` / `CONNECT_PORT=…` env vars, which the Kafka and Debezium images interpret as configuration. Every pod sets `enableServiceLinks: false`.

## 3. `just` recipes (contract)

```
just up        # kind cluster (if missing) + devspace build + deploy infra and worker; connector registered; idempotent
just down      # delete namespace
just clean     # delete kind cluster
just logs X    # kubectl logs for worker|connect|...
just topic T   # print a CDC topic from the beginning
just psql      # psql on the source DB
just seed      # writers Job: pre-snapshot data set (no deletes)
just scenario  # writers Job: scenarios/e2e.json at 150 statements/s
just connector # connector status
just e2e       # full proof from a clean namespace (below); exits non-zero on any failure  — Phase 5
just cache     # canonical dump of Valkey to stdout                                       — Phase 5
just lag       # consumer group lag for vehicle-cache                                     — Phase 5
```

`just e2e` pipeline (each step fails the run on error):

1. `reset` — delete + recreate namespace (fresh Postgres, Kafka, Valkey, slot)
2. `deploy-infra` — Postgres, Kafka, Connect, Valkey; wait ready
3. `seed` — writers Job writes the **pre-snapshot** data set, **no deletes** (TB-11; spec §8)
4. `register-connector` — Job; initial snapshot emits `op=r`
5. `deploy-worker` — starts consuming (snapshot first)
6. `scenario` + `chaos:kill-worker` — the scenario Job is started in the background; while it runs, `kubectl delete pod -l app=worker --grace-period=0 --force` (no SIGTERM, no graceful `Close()`/commit — exercises the crash path, TB-12); wait for the Job to complete
7. `checkpoint` — verifier `expect` → `dump > out/d0.json`
8. `chaos:rewind` — `stop-worker` (below) → `kafka-consumer-groups.sh --group vehicle-cache --reset-offsets --shift-by -500 --all-topics --execute` (≤ 500 records **per partition**) → scale 1 (TB-13)
9. `verify` — verifier `expect` (TB-15) → `dump > out/d1.json` → `diff d0 d1` (TB-13: redelivery changed nothing)
10. `replay` — `stop-worker` → `valkey-cli FLUSHALL` → `--reset-offsets --to-earliest --all-topics` → scale 1 → verifier `expect` → `dump > out/d2.json` → `diff d1 d2` (TB-14)
11. Print summary: events applied / rejected per reason (from worker logs), TB ids covered, durations.

`stop-worker` = scale to 0 → `kubectl wait --for=delete pod -l app=worker` → poll `kafka-consumer-groups.sh --describe --group vehicle-cache --state` until `Empty` (offset resets are refused while members are still registered).

## 4. Risks to retire in Phase 0

| Risk | Check |
|---|---|
| devenv/Nix on WSL2 | `devenv shell -- dotnet --version` prints 10.x |
| devspace pushing to kind | ✅ verified (Phase 3): devspace builds with the local Docker daemon and loads the image into the kind node automatically (`docker exec vehicle-cache-control-plane crictl images`); no registry needed |
| Debezium JSON shape | capture one `r`, `c`, `u`, `d` + tombstone with `kafka-console-consumer.sh` (in the Kafka pod) → save as test fixtures |
| `timestamptz` format | confirm ISO-8601 string with microseconds and `Z` |
| Connect internal topics RF | Connect starts with 1 broker |
| Kafka CLI availability for rewind | run `kafka-consumer-groups.sh` via `kubectl exec` into the Kafka pod |
| Cold-start time | `just e2e` total < 10 min on a laptop; pre-pull images in `just up` |
