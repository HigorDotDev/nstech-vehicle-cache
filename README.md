# Vehicle Cache — a CDC-driven read model

A .NET 10 worker keeps a Valkey cache that answers, for every vehicle, **"where is it now, and what state is it in?"** — built **only** from Debezium change events of two PostgreSQL tables (`vehicles`, `positions`) streamed through Kafka. The worker never talks to the database. Replaying the topics from offset 0 rebuilds a byte-identical cache.

```
mock writers ──SQL──▶ PostgreSQL ──WAL──▶ Debezium (Kafka Connect) ──▶ Kafka ──▶ .NET worker ──Lua CAS──▶ Valkey ◀── readers
```

## Run it

**Prerequisites** — a Linux or macOS host (or WSL2) with:

- **Docker** (cgroup v2 — the default on current distros and Docker Desktop for Mac; WSL2 users see [docs/04-environment.md §1](docs/04-environment.md))
- **Nix + [devenv](https://devenv.sh/getting-started/)** — devenv pins every other tool: .NET 10 SDK, kind, kubectl, devspace, just, jq, valkey-cli, psql

```bash
devenv shell          # first run downloads the toolchain
just up               # kind cluster + Postgres, Kafka, Debezium, Valkey, worker; connector registered  (~3 min cold)
just e2e              # the full proof below, from a clean namespace                                 (~2–5 min)
```

`just e2e` exits non-zero on any failure. Everything else is optional: `just --list` shows the helpers (`just cache`, `just lag`, `just topic`, `just psql`, `just logs worker`, …). `just clean` deletes the cluster.

### What `just e2e` proves

| Step | What happens | Behaviour |
|---|---|---|
| reset → deploy | fresh namespace: Postgres (logical WAL), Kafka (KRaft), Debezium, Valkey, worker | — |
| seed → connector | rows written **before** Debezium exists reach the cache through the initial snapshot (`op=r`) | TB-11 |
| scenario + kill | ~1,400 writes: late GPS fixes, duplicate fixes, positions before their vehicle, out-of-order business updates, deletes followed by late fixes; the worker is **force-killed mid-stream** (checked) | TB-01…09, TB-12 |
| checkpoint | verifier: cache == oracle computed from Postgres; dump `d0` | TB-15 |
| rewind | committed offsets shifted back ≤100/partition → forced redelivery; dump `d1` must equal `d0` | TB-13 |
| replay | `FLUSHALL` + offsets to earliest → rebuild from offset 0; dump `d2` must equal `d1` byte for byte | TB-14 |

Unit and integration tests (48, Testcontainers — needs Docker): `dotnet test VehicleCache.slnx` inside `devenv shell`. The key one, **TB-10**, applies 200 random permutations of an event history with ~20% duplicates and requires every resulting cache to equal an independently computed reference; removing an ordering guard makes it fail on the first permutation.

### Explore it visually / debug it (optional)

```bash
just ui     # Kafka UI on http://localhost:18080, Postgres on :15432, Valkey on :16379 — Ctrl+C to stop
just dev    # same, but the in-cluster worker is paused: run VehicleCache.Worker from your IDE (profile `local-kind`)
```

Insert a row in `vehicles`/`positions` with DBeaver, watch the change event in Kafka UI, step through `KafkaCdcConsumer` → `CdcRecordHandler` → mapper → Lua with breakpoints, and see `vehicle:{id}` change in Redis Insight. Details: [docs/04-environment.md §4](docs/04-environment.md).

## How it stays correct

The cache is defined as a function of the source tables, and every write is a commutative, idempotent merge:

```
vehicle:{id}.state    = the vehicles row (ordered by a trigger-owned `version`)     — or {deleted:1} once deleted
vehicle:{id}.position = argmax over its positions of (recorded_at, id)
```

- **One Valkey hash per vehicle, two independently guarded field groups.** Each group is written by a Lua compare-and-set script that only accepts a strictly newer ordering key; `deleted` is absorbing. Any order and any number of duplicates converge to the same hash — so duplicates, stale events, cross-topic races, restarts and replays need no special handling. ([ADR-0003](docs/adr/0003-hash-per-vehicle-lua-guards.md))
- **Ordering keys come from the data, not the transport:** `vehicles.version` for state, `(recorded_at, id)` for positions. Never Debezium `ts_ms`, LSN or Kafka offsets. ([ADR-0002](docs/adr/0002-ordering-keys.md))
- **Positions before their vehicle are stored immediately** — the hash is the join; no buffers. ([ADR-0003](docs/adr/0003-hash-per-vehicle-lua-guards.md))
- **Deletes leave a tombstone**; ids are never reused (enforced by a trigger). ([ADR-0004](docs/adr/0004-deletes-and-tombstones.md))
- **At-least-once:** offsets are stored only after the Lua script returned; a malformed event stops the worker instead of being skipped. ([ADR-0005](docs/adr/0005-delivery-semantics.md))
- **The source enforces the preconditions** the convergence argument needs (DB-assigned versions, immutable and never-reused ids, append-only positions, no TRUNCATE). ([spec §3, §6](docs/02-spec.md))

### Reading the cache

```bash
just cache                                    # every vehicle, canonical form
kubectl --context kind-vehicle-cache -n vehicle-cache exec deploy/valkey -- valkey-cli HGETALL vehicle:3
```

| `HGETALL vehicle:{id}` | Meaning |
|---|---|
| empty | unknown vehicle |
| `deleted 1` | vehicle was deleted — treat as not found |
| `state …` (+ position fields) | registered; position fields absent until the first fix |
| only position fields | location known, state unknown — the vehicle row has not arrived yet, was never registered, or was deleted before the initial snapshot (the cache carries no metadata to tell these apart) |

Fields: `state plate stateChangedAt stateVersion` · `lat lng recordedAt recordedAtUs positionId` · `deleted`.

**Operations note:** Valkey runs without persistence because the cache is rebuildable from Kafka — but the rebuild is not automatic. If Valkey restarts empty, run `just replay` (stop worker → `FLUSHALL` → offsets to earliest → start). Automatic detection is a documented evolution ([ADR-0001](docs/adr/0001-cache-is-pure-cdc-projection.md)). A malformed CDC event stops the worker on purpose (it is never skipped); see [ADR-0005](docs/adr/0005-delivery-semantics.md).

## Repository map

| Path | What |
|---|---|
| [`docs/01-c4.md`](docs/01-c4.md) | C4 context, container, component and a runtime sequence of the hard cases (Mermaid) |
| [`docs/02-spec.md`](docs/02-spec.md) | Spec + implementation plan: source model, CDC contract, Valkey schema, Lua rules, components, writers, verifier |
| [`docs/03-test-behaviors.md`](docs/03-test-behaviors.md) | TB-01…17 in Given/When/Then, each mapped to the automated test that proves it |
| [`docs/adr/`](docs/adr/README.md) | ADR-0001…0009 — decisions, alternatives, consequences |
| [`docs/04-environment.md`](docs/04-environment.md) | devenv / kind / devspace / just design and the e2e pipeline |
| [`docs/06-ai-workflow.md`](docs/06-ai-workflow.md) | How AI was used, and every AI (or own) suggestion that turned out wrong |
| `src/VehicleCache.Projection` | Pure: Debezium envelope → cache command; the three Lua scripts |
| `src/VehicleCache.Worker` | Kafka consumers (one per topic) and the Valkey writer |
| `src/VehicleCache.MockWriters` | Writes to Postgres only: `seed`, `scenario`, `vehicles`, `positions` |
| `src/VehicleCache.Verifier` | e2e oracle (SQL over Postgres) and canonical cache dumps |
| `tests/` | Unit tests over **captured** Debezium payloads; Testcontainers integration tests |
| `scenarios/`, `infra/`, `k8s/` | Deterministic scenarios, DDL + connector config, plain Kubernetes manifests |
| `.claude/` | The Claude Code agents and skills used to build this (see the AI workflow doc) |

## Scope, cuts and known limits

Deliberately out of scope ([spec §10](docs/02-spec.md)): exactly-once, DLQ, Avro/schema registry, auth/TLS, Strimzi/Helm, multi-replica worker in e2e, tombstone TTL, a read API. Known limits, each discussed in its ADR: a vehicle deleted *before* the initial snapshot is invisible to CDC; tombstones accumulate; replay identity assumes unlimited topic retention and no log compaction; ids must never be reused.
