# 04 — Reproducible Environment

Status: **Final** — `just e2e` green twice in a row after `just clean` (274 s from a deleted cluster, 141 s warm). Graded at 20%: "if it needs a README of manual fixes, it failed".

## 1. Tooling layers

| Layer | Tool | Owns |
|---|---|---|
| Toolchain pinning | `devenv` (Nix, `devenv.lock`) | `dotnet-sdk_10`, `kind`, `kubectl`, `devspace`, `just`, `jq`, `valkey` (cli, manual debugging), `postgresql_17` (psql) — same versions for everyone |
| Cluster | `kind` | single-node cluster `vehicle-cache` |
| Build + deploy | `devspace` | builds the 3 .NET images, loads them into kind, applies `k8s/infra/` and `k8s/app/` |
| Orchestration | `just` | the only entry points: `up`, `e2e`, `down`, `clean`, plus helpers |

Reviewer prerequisites (README): Linux/macOS (or WSL2 Ubuntu) with **Docker** (cgroup v2) and **Nix + devenv**. Everything else comes from devenv. Entry: `devenv shell` then `just up` / `just e2e` (or `devenv shell -- just e2e`).

> **Author's machine (Windows 11):** devenv requires Nix ⇒ work inside a **WSL2 Ubuntu distro**, with Docker Desktop's WSL integration enabled for it, and the repo cloned inside the Linux filesystem (`~/src`, not `/mnt/c` — I/O is far slower there). The .NET 10 SDK comes only from devenv.
>
> **Host requirement: cgroup v2.** kind 0.32's default node runs Kubernetes ≥ 1.35, which refuses cgroup v1. Modern Linux distros and macOS Docker already use v2. On WSL2, Docker reported `CgroupVersion=1`; fixed with `kernelCommandLine = cgroup_no_v1=all` in `%USERPROFILE%\.wslconfig`, then `wsl --shutdown` and a full Docker Desktop restart. Check: `docker info --format '{{.CgroupVersion}}'` → `2`. Also set: `memory=10GB`, `swap=4GB`, and `appendWindowsPath=false` in `/etc/wsl.conf` so only devenv tools are on PATH.

## 2. Kubernetes layout (`k8s/`, plain manifests — ADR-0009)

`k8s/infra/` and `k8s/app/` are deployed by devspace; `k8s/jobs/` are one-shot steps applied by `just job <name>` (fails fast with the Job's logs). Config files stay in `infra/` and are turned into ConfigMaps by `just config` (`postgres-init` from `infra/postgres/`, `debezium-connector` from `infra/debezium/connector.json`).

| Manifest | Kind | Image (pinned) | Notes |
|---|---|---|---|
| `k8s/infra/postgres.yaml` | StatefulSet + Service + PVC | `postgres:17.6` | `-c wal_level=logical -c max_replication_slots=4 -c max_wal_senders=4`; init SQL mounted from ConfigMap |
| `k8s/infra/kafka.yaml` | StatefulSet + Service + PVC | `apache/kafka:3.9.1` | KRaft single node, RF=1, 3 partitions, `log.retention.ms=-1` |
| `k8s/infra/connect.yaml` | Deployment + Service | `quay.io/debezium/connect:3.2.0.Final` | internal topics RF=1; topics created by the connector (`topic.creation.default.*`) |
| `k8s/infra/valkey.yaml` | Deployment + Service | `valkey/valkey:8.1.3` | no persistence: rebuildable from Kafka with `just replay` — but not automatically (ADR-0001) |
| `k8s/app/worker.yaml` | Deployment (1 replica) | `vehicle-cache-worker` (devspace, tag replaced by devspace) from `src/VehicleCache.Worker/Dockerfile`: `sdk:10.0.401` → `runtime:10.0.12`, non-root | env: Kafka bootstrap, Valkey endpoint — **no Postgres** |
| `k8s/jobs/register-connector.yaml` | Job | `curlimages/curl:8.14.1` | `PUT /connectors/fleet/config` (idempotent), then polls until connector + task RUNNING |
| `k8s/jobs/writers-seed.yaml`, `writers-scenario.yaml` | Job (`backoffLimit: 0`) | `vehicle-cache-writers:local` | `seed`; `scenario scenarios/e2e.json --rate 150` |
| `k8s/jobs/verifier-expect.yaml`, `verifier-dump.yaml` | Job (`backoffLimit: 0`) | `vehicle-cache-verifier:local` | `expect --timeout 180` (reads Postgres + Valkey); `dump` (Valkey only, stdout) |

Writers and verifier images use the fixed tag `local` because `just` applies their Jobs directly (devspace only rewrites tags in the manifests it deploys). Each Job run is a new pod, so a rebuilt `local` image is always picked up. The kind node image is not pinned separately: the kind version is pinned by `devenv.lock`, and each kind release pins its default node image.

Readiness: every long-running container has a readiness probe; `just` waits with `kubectl rollout status`, `kubectl wait` or short polling loops on an observable condition — never a blind `sleep`.

Gotchas found on the way: Kubernetes service links inject `KAFKA_PORT=tcp://…` / `CONNECT_PORT=…` env vars, which the Kafka and Debezium images interpret as configuration — every pod sets `enableServiceLinks: false`. Npgsql probes GSSAPI at startup — the writers/verifier images install `libgssapi-krb5-2` to keep the output clean.

## 3. `just` recipes (contract)

```
just up        # kind cluster (if missing) + config + devspace build/deploy (infra + worker) + connector; idempotent
just e2e       # full proof from a clean namespace (below); exits non-zero on any failure
just down      # delete the namespace (no-op when the cluster does not exist yet)
just clean     # delete the kind cluster
just seed      # writers Job: pre-snapshot data set (no deletes)
just scenario  # writers Job: scenarios/e2e.json at 150 statements/s
just expect    # verifier Job: poll until cache == oracle
just dump X    # canonical cache dump into out/X.txt
just cache     # canonical cache dump to stdout
just rewind    # stop worker, shift offsets back ≤100/partition, start worker
just replay    # stop worker, FLUSHALL, offsets to earliest, start worker
just lag       # consumer group offsets and lag
just logs X    # kubectl logs for worker|connect|kafka|postgres|valkey
just topic T   # print a CDC topic from the beginning
just psql      # psql on the source DB
just connector # connector status
```

`just e2e` pipeline — the step labels are the ones it prints (`━━ [time] <step>`); any failing step aborts with `✖ e2e FAILED at: <step>`:

1. **reset** — `just down` (fresh Postgres, Kafka, replication slot, Valkey)
2. **deploy** — `just cluster config deploy`: kind (if missing), ConfigMaps, devspace build + deploy of infra **and** the worker; wait for rollouts. The worker starts with no topics yet and simply polls.
3. **seed** — writers Job writes the **pre-snapshot** data set, **no deletes** (TB-11; spec §8)
4. **connector** — register Debezium; the initial snapshot emits `op=r`
5-6. **scenario + kill** — the scenario Job starts in the background; once ≥ 10 burst vehicles are projected, `kubectl delete pod -l app=worker --grace-period=0 --force` (no SIGTERM, no graceful `Close()`/commit — the crash path, TB-12). The run **fails** if the scenario had already finished at kill time. Then wait for the Job and the replacement pod.
7. **checkpoint** — verifier `expect` → `wait-caught-up` → `dump d0`
8. **rewind** — print offsets → `stop-worker` → `--reset-offsets --shift-by -100 --all-topics` (≤ 100 records **per partition** — partial, unlike step 10) → print offsets → start worker (TB-13)
9. **verify** — `wait-caught-up` → `expect` (TB-15) → `dump d1` → `diff -u out/d0.txt out/d1.txt` (TB-13: redelivery changed nothing)
10. **replay** — `stop-worker` → `FLUSHALL` → `--reset-offsets --to-earliest --all-topics` → start worker → `expect` → `wait-caught-up` → `dump d2` → `diff -u out/d1.txt out/d2.txt` (TB-14)
11. **summary** — asserts ≥ 1 snapshot read (`op=r`) and > 0 positions rejected by the guards during the replay (proof the scenario contained disorder); prints worker counters and total time.

Helpers: `stop-worker` = scale to 0 → wait for pod deletion → poll `kafka-consumer-groups.sh --describe --state` until `Empty` (offset resets are refused while members are registered). `wait-caught-up` = poll consumer-group lag until 0 on every partition of both topics.

## 4. Dev mode: visual tools and IDE debugging (not part of `just e2e`)

| Recipe | What it does |
|---|---|
| `just ui` | Deploys Kafka UI (`k8s/dev/kafka-ui.yaml`, `ghcr.io/kafbat/kafka-ui:v1.3.0`) and port-forwards everything to `localhost`; the in-cluster worker keeps running. Ctrl+C stops the forwards. |
| `just dev` | Same, but stops the in-cluster worker so a worker started from the IDE receives every event; Ctrl+C restarts the in-cluster worker. |

| Port (localhost) | Target | Tool |
|---|---|---|
| 15432 | PostgreSQL (`fleet`, `postgres`/`postgres`) | DBeaver / pgAdmin |
| 16379 | Valkey | Redis Insight |
| 29092 | Kafka `EXTERNAL` listener (advertised as `localhost:29092`; in-cluster clients keep `kafka:9092`) | worker started from the IDE |
| 18080 | Kafka UI | browser — topics, messages, consumer group, Debezium connector |
| 18083 | Kafka Connect REST | browser / curl |

Ports are non-standard on purpose (no clash with a local Postgres/Redis). WSL2 forwards `localhost` ports to Windows, so Windows apps can use them directly. Each forward restarts itself if its pod is replaced.

IDE debugging: launch profile `local-kind` (`src/VehicleCache.Worker/Properties/launchSettings.json`) sets `DOTNET_ENVIRONMENT=Development` (`appsettings.Development.json` → `localhost:29092`, `localhost:16379`, Debug logs with one short line per command and its outcome) and `DOTNET_hostBuilder__reloadConfigOnChange=false` — config file watching over `\\wsl.localhost\…` made the host hang silently. Windows builds of the WSL checkout go to `bin/win` / `obj/win` (`Directory.Build.props`) so Windows and Linux toolchains never overwrite each other's restore output. Verified: a worker started on Windows against the kind cluster consumed and projected live inserts, a late fix (`Rejected`) and a delete.

## 5. Risks retired in Phase 0 (all verified)

| Risk | Outcome |
|---|---|
| devenv/Nix on WSL2 | ✅ `devenv shell -- dotnet --version` → 10.0.401 (`kcat` dropped: does not build from devenv-nixpkgs) |
| kind on the host | ✅ after enabling cgroup v2 (§1) |
| devspace images into kind | ✅ devspace builds with the local Docker daemon and loads images into the kind node; no registry |
| Debezium JSON shape | ✅ real `r`/`c`/`u`/`d`/tombstone captured as fixtures (`tests/VehicleCache.UnitTests/Fixtures/README.md`) |
| `timestamptz` format | ✅ ISO-8601 with `Z`, usually 6 fractional digits — normalized by the worker anyway |
| Connect / Kafka with 1 broker | ✅ RF=1 everywhere |
| Kafka CLI for rewind/replay | ✅ `kafka-consumer-groups.sh` via `kubectl exec kafka-0` |
| Cold-start time | ✅ `just e2e` 274 s from a deleted cluster (images cached by Docker), 141 s warm |
