# 02 — Specification & Implementation Plan

Status: **Draft** · Owner: Higor Buiatti · Related: ADR-0001…0009, `01-c4.md`, `03-test-behaviors.md`

## 1. Problem statement

Maintain in Valkey, per vehicle, the **current state** and the **latest position**, built **only** from Debezium CDC events of `public.vehicles` and `public.positions`. The two topics are independently partitioned and lagged; delivery is at-least-once; the stream can be replayed from offset 0. The cache must be correct under duplicates, disorder, restarts, deletes and the snapshot→streaming transition, and a full replay must rebuild an **identical** cache.

## 2. The core idea (one paragraph to defend in the interview)

The cache is a **materialized view that equals a function of the source tables**:

```
cache(v).state    = vehicles row v (state, version, state_changed_at)      -- or tombstone if row deleted
cache(v).position = argmax over positions where vehicle_id = v of (recorded_at, id)
```

Each Valkey hash holds **two independent field groups** (state, position), each guarded by its own **monotonic ordering key**, updated by an **atomic compare-and-set Lua script**. Each group is a last-writer-wins register keyed by a total order, so applying any permutation/duplication of the event set yields the same result (the merge is commutative, associative and idempotent). That single property gives us: duplicates are no-ops, stale events lose, cross-topic interleaving is irrelevant, position-before-vehicle needs no buffer, restart needs no state, and replay rebuilds the identical cache.

## 3. Source model (PostgreSQL)

```sql
-- infra/postgres/001_schema.sql
CREATE TABLE vehicles (
    id               bigint      PRIMARY KEY,             -- externally assigned fleet id, NEVER reused (ADR-0004)
    plate            text        NOT NULL,
    state            text        NOT NULL CHECK (state IN ('REGISTERED','ACTIVE','IDLE','MAINTENANCE','DECOMMISSIONED')),
    state_changed_at timestamptz NOT NULL,                -- business time, informational for readers
    version          bigint      NOT NULL DEFAULT 1       -- ordering key (ADR-0002), owned by trigger
);

CREATE FUNCTION vehicles_bump_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.version := OLD.version + 1;   -- writers cannot set or regress it
    RETURN NEW;
END $$;

CREATE TRIGGER trg_vehicles_version BEFORE UPDATE ON vehicles
    FOR EACH ROW EXECUTE FUNCTION vehicles_bump_version();

CREATE TABLE positions (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vehicle_id  bigint           NOT NULL,                -- deliberately NO foreign key (ADR-0003)
    lat         double precision NOT NULL,                -- double, not numeric: avoids Debezium base64 decimals
    lng         double precision NOT NULL,
    recorded_at timestamptz      NOT NULL,                -- business (event) time — ordering key
    inserted_at timestamptz      NOT NULL DEFAULT now()   -- ingestion time — never used for ordering
);
CREATE INDEX ix_positions_latest ON positions (vehicle_id, recorded_at DESC, id DESC);  -- used only by the verifier oracle

-- Test-harness only: NOT in table.include.list, never read by the worker.
-- Lets the oracle tell "deleted" from "never existed" after the row is gone.
CREATE TABLE writer_deletions (vehicle_id bigint PRIMARY KEY, deleted_at timestamptz NOT NULL DEFAULT now());
```

Postgres runs with `wal_level=logical`. Publication created by Debezium (`publication.autocreate.mode=filtered`). Default `REPLICA IDENTITY` is enough: a delete only needs the PK (ADR-0004).

## 4. CDC contract (what the worker relies on)

Connector (`infra/debezium/connector.json`, see `debezium-event-contract` skill for the full list):

| Setting | Value | Why |
|---|---|---|
| `plugin.name` | `pgoutput` | built into Postgres, no extension |
| `topic.prefix` | `fleet` | topics `fleet.public.vehicles`, `fleet.public.positions` |
| `table.include.list` | `public.vehicles,public.positions` | |
| `snapshot.mode` | `initial` | snapshot (`op=r`) then stream (ADR-0006) |
| `key/value.converter` | `JsonConverter`, `schemas.enable=false` | plain JSON envelope, easy to parse and to fixture |
| `message.key.columns` | `public.positions:vehicle_id` | positions of one vehicle share a partition (ADR-0007; optimization, not correctness) |
| `tombstones.on.delete` | `true` (default) | worker ignores null-value records; keeps the topic compaction-ready |
| `heartbeat.interval.ms` | `10000` | lets the slot advance when only one table is busy (WAL growth) |
| `time.precision.mode` | default (`adaptive`) | `timestamptz` arrives as ISO-8601 string (`ZonedTimestamp`) |

Topics: `cleanup.policy=delete`, long retention (e2e: infinite). With compaction the replay contract changes — see ADR-0004 §Consequences.

Envelope consumed (value, schemaless JSON):

```json
{
  "before": null,
  "after":  { "id": 42, "plate": "ABC1D23", "state": "ACTIVE", "state_changed_at": "2026-10-21T14:30:00.000000Z", "version": 3 },
  "source": { "connector": "postgresql", "name": "fleet", "snapshot": "false", "db": "fleet",
              "schema": "public", "table": "vehicles", "txId": 771, "lsn": 24023128, "ts_ms": 1792600200000 },
  "op": "u",
  "ts_ms": 1792600200123
}
```

| `op` | Meaning | Payload used | Worker command |
|---|---|---|---|
| `r` | snapshot read | `after` | same as `c`/`u` |
| `c` | insert | `after` | `UpsertState` / `ApplyPosition` |
| `u` | update | `after` | `UpsertState` (positions: never updated → treated as `ApplyPosition`, logged as anomaly) |
| `d` | delete | `before.id` **only** — other `before` columns are zero-filled under default replica identity (verified in fixtures) | `DeleteVehicle` (positions: ignored, logged) |
| *(null value)* | Kafka tombstone | — | skipped, offset stored |

Fields **not** used for ordering: `ts_ms` (both), `source.lsn`, `txId`, Kafka offset/timestamp. They are logged for diagnostics only. Rationale in ADR-0002.

**Real fixtures rule:** the parser's unit tests use JSON captured from the running connector (`tests/VehicleCache.UnitTests/Fixtures/`, see its README for provenance and findings), not hand-written guesses.

## 5. Read model (Valkey)

One hash per vehicle: `vehicle:{id}`.

| Field | Group | Source | Notes |
|---|---|---|---|
| `state` | state | `after.state` | absent ⇒ vehicle not (yet) known |
| `plate` | state | `after.plate` | |
| `stateChangedAt` | state | `after.state_changed_at` | ISO-8601, as emitted |
| `stateVersion` | state | `after.version` | **guard** |
| `lat`, `lng` | position | `after.lat/lng` | |
| `recordedAt` | position | `after.recorded_at` | ISO-8601, as emitted |
| `recordedAtUs` | position | parsed `recorded_at` → epoch µs | **guard** (part 1); < 2^53, safe as Lua number |
| `positionId` | position | `after.id` | **guard** (part 2, tie-break) |
| `deleted` | tombstone | `op=d` | `1` ⇒ every other field removed, all later writes rejected |

Reader contract (documented in README):

| Hash | Meaning |
|---|---|
| missing | unknown vehicle |
| `deleted=1` | vehicle removed — treat as not found |
| `state` present | registered; position may be absent (no position yet) |
| only position fields | position received before the vehicle row — show location, state `UNKNOWN` |

No TTLs, no secondary indexes, no counters in the keyspace (purity — invariant 2). The verifier enumerates with `SCAN MATCH vehicle:*`.

## 6. Conditional write rules (Lua, `src/VehicleCache.Projection/Lua/`)

All three scripts run atomically on the single key `KEYS[1] = vehicle:{id}` and return `1` (applied) or `0` (rejected: stale/duplicate/deleted).

```
apply_state(version, state, plate, stateChangedAt):
    if deleted == 1                      → 0
    if stateVersion exists and version <= stateVersion → 0     -- duplicate or stale
    HSET state, plate, stateChangedAt, stateVersion            → 1

apply_position(recordedAtUs, positionId, lat, lng, recordedAt):
    if deleted == 1                      → 0
    if recordedAtUs exists and (recordedAtUs, positionId) <= (cur.recordedAtUs, cur.positionId) lexicographically → 0
    HSET lat, lng, recordedAt, recordedAtUs, positionId        → 1

delete_vehicle():
    if deleted == 1                      → 0                   -- duplicate delete
    DEL key ; HSET deleted 1                                   → 1
```

Why these converge: each guard is a strict total order on a field group, and `deleted` is an absorbing element that is checked first by every script. Positions processed before a delete get wiped by it; positions processed after it are rejected — same final value either way (TB-09).

## 7. Worker components (`src/VehicleCache.Worker` + `src/VehicleCache.Projection`)

```
KafkaCdcConsumer (BackgroundService, one per topic, group "vehicle-cache")
   └─ consume → DebeziumEnvelopeParser → I{Vehicle|Position}ChangeMapper → CacheCommand
                                                                           └─ IVehicleCacheWriter (Valkey, Lua EVALSHA)
                                                                                └─ on success: consumer.StoreOffset(result)
```

| Component | Project | Responsibility | Pure? |
|---|---|---|---|
| `DebeziumEnvelopeParser` | Projection | bytes → `ChangeEvent<T>` (`op`, `before`, `after`, `source.snapshot`); null value ⇒ `Tombstone` | yes |
| `VehicleChangeMapper` | Projection | `ChangeEvent<VehicleRow>` → `UpsertState` / `DeleteVehicle` | yes |
| `PositionChangeMapper` | Projection | `ChangeEvent<PositionRow>` → `ApplyPosition` (µs conversion) | yes |
| `CacheCommand` (records) | Projection | `UpsertState`, `ApplyPosition`, `DeleteVehicle`, `Skip(reason)` | yes |
| `LuaScripts` | Projection | embedded `.lua` resources + SHA loading | yes |
| `ValkeyVehicleCacheWriter` | Worker | executes the script for a command, returns `Applied`/`Rejected` | I/O |
| `KafkaCdcConsumer` | Worker | poll loop, `enable.auto.offset.store=false`, `StoreOffset` after write, auto-commit of stored offsets | I/O |
| `ProjectionMetrics` | Worker | counters applied / rejected-stale / skipped (logs + `/metrics` optional) — **never in Valkey** | I/O |

Consumer settings: `enable.auto.commit=true` + `enable.auto.offset.store=false` (commit only what was stored), `auto.offset.reset=earliest`, `isolation.level` default, `partition.assignment.strategy=cooperative-sticky`. Failure policy: parse error or Valkey error after retries ⇒ log with topic/partition/offset and **stop the host** (non-zero exit, Kubernetes restarts the pod; the event is redelivered). No DLQ in scope (ADR-0005).

Concurrency: one consume loop per topic, sequential within the loop. Safe to scale to N replicas or parallelize per partition because correctness is enforced in Valkey, not in process memory.

## 8. Mock writers (`src/VehicleCache.MockWriters`)

One console binary (System.CommandLine), Npgsql only. Never touches Kafka/Valkey.

| Command | Behavior |
|---|---|
| `scenario <file> [--seed]` | Executes a deterministic step list (`scenarios/*.json`). Used by e2e and integration tests. |
| `vehicles --rate --count --seed --out-of-order-pct --delete-pct` | Inserts vehicles, random valid state transitions, deletes. "Out-of-order" = an update whose `state_changed_at` is *older* than the row's current one (a late report); the DB accepts it (see ADR-0002 §Decision 1). |
| `positions --rate --vehicles --seed --late-pct --dup-pct --orphan-pct` | Inserts positions; `late` = `recorded_at` in the past relative to the vehicle's latest; `dup` = same `(vehicle_id, recorded_at, lat, lng)` inserted twice; `orphan` = vehicle id not registered yet (registered later). |

Scenario step kinds: `insertVehicle`, `updateVehicle`, `deleteVehicle`, `insertPosition`, `sleep`, `barrier` (wait N ms for CDC). Seeded `Random` ⇒ reproducible runs.

## 9. Verifier (`src/VehicleCache.Verifier`) — test harness, allowed to read Postgres

| Command | Behavior |
|---|---|
| `expect --timeout 120s` | Builds the **oracle** from Postgres (§2 formulas; deleted ids come from `writer_deletions`, see below) and polls Valkey until equal or timeout. Prints a diff on failure. |
| `dump --out file.json` | `SCAN vehicle:*` → canonical JSON (sorted keys and fields). |
| `diff a.json b.json` | Byte-equality of two canonical dumps; prints the differing keys. |

## 10. Non-goals (explicit cuts)

Exactly-once / Kafka transactions · DLQ · schema registry/Avro · auth/TLS · Strimzi/Helm · multi-replica worker in e2e · tombstone TTL · read API (readers use Valkey directly; a `valkey-cli` example is in the README) · observability stack.

## 11. Implementation plan (sequenced by risk, time-boxed)

| # | Phase | Box | Done when | Agent |
|---|---|---|---|---|
| 0 | **Env spike** (biggest risk: WSL2 + Nix + devenv + kind + devspace) | 1.5h | `just up` brings Postgres+Kafka+Connect+Valkey on kind; connector registered; `kafka-console-consumer.sh` shows a real event | platform |
| 1 | Docs v1: ADRs `Proposed`, spec, C4, TBs → **book the 30-min design review** | 1h | reviewed by `architecture-reviewer` + `interview-examiner` | main |
| 2 | Projection core: parser + mappers (fixtures captured in phase 0), Lua scripts, **permutation/duplication convergence test** on Testcontainers Valkey | 1.5h | TB-01…TB-10 green at integration level | worker, test |
| 3 | Worker host: consumers, offset store, fail-fast, image | 1h | pod runs in kind, cache fills | worker, platform |
| 4 | Writers + `scenarios/e2e.json` (covers every TB) | 1h | scenario produces all hard cases | source |
| 5 | `just e2e`: seed → connector (snapshot) → scenario → worker kill → offset rewind → expect → dump → flush+replay → expect → diff | 1.5h | green twice in a row from `just clean` | platform, test |
| 6 | Final docs, README, PR description, AI log, interview rehearsal | 1h | `architecture-reviewer` finds no drift; C4 = manifests | main, examiner |

**Cut line if time runs out (in order):** random-load writer modes → `message.key.columns` → Component diagram polish → metrics. **Never cut:** convergence test, replay diff, offset-rewind duplicate injection, ADRs.

## 12. Open questions for the design review

1. Vehicle ordering = DB row truth (`version`) vs business time (`state_changed_at`). We chose row truth (ADR-0002); confirm their read path expects "mirror of the table".
2. Deleted vehicle representation (tombstone hash forever) vs key removal — are readers OK checking `deleted`?
3. Are positions for unknown vehicles legitimate data (no FK) in their domain?
