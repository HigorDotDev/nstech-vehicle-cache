# 02 — Specification & Implementation Plan

Status: **Draft** · Owner: Higor Buiatti · Related: ADR-0001…0009, `01-c4.md`, `03-test-behaviors.md`

## 1. Problem statement

Maintain in Valkey, per vehicle, the **current state** and the **latest position**, built **only** from Debezium CDC events of `public.vehicles` and `public.positions`. The two topics are independently partitioned and lagged; delivery is at-least-once; the stream can be replayed from offset 0. The cache must be correct under duplicates, disorder, restarts, deletes and the snapshot→streaming transition, and a full replay must rebuild an **identical** cache.

## 2. The core idea (one paragraph to defend in the interview)

The cache is a **materialized view that equals a function of the source tables**:

```
cache(v).state    = vehicles row v (state, version, state_changed_at)      -- or tombstone if v in retired_vehicle_ids
cache(v).position = argmax over positions where vehicle_id = v of (recorded_at, id)
```

This equality is the e2e **oracle** (§9). It holds under source preconditions enforced by triggers (§3): ids are immutable and never reused, `version` is DB-assigned, positions are append-only, TRUNCATE is rejected. One documented exception: a vehicle deleted **before** the initial snapshot never reaches CDC, so the cache cannot hold its tombstone — the seed data therefore contains no deletes (§8, ADR-0004).

Each Valkey hash holds **two independent field groups** (state, position), each guarded by its own **monotonic ordering key**, updated by an **atomic compare-and-set Lua script**. Each group is a last-writer-wins register keyed by a total order, so applying any permutation/duplication of the event set yields the same result (the merge is commutative, associative and idempotent). That single property gives us: duplicates are no-ops, stale events lose, cross-topic interleaving is irrelevant, position-before-vehicle needs no buffer, restart needs no state, and replay rebuilds the identical cache.

## 3. Source model (PostgreSQL)

Full DDL: `infra/postgres/001_schema.sql` (the file is the source of truth; this section states its contract).

| Table | Columns | Rules (enforced by triggers) |
|---|---|---|
| `vehicles` | `id bigint PK` (external fleet id), `plate`, `state` (CHECK: `REGISTERED/ACTIVE/IDLE/MAINTENANCE/DECOMMISSIONED`), `state_changed_at timestamptz` (business time, informational), `version bigint` | `BEFORE INSERT`: `version := 1`, reject ids found in `retired_vehicle_ids`. `BEFORE UPDATE`: `version := OLD.version + 1`, reject any change of `id`. `AFTER DELETE`: insert id into `retired_vehicle_ids`. `BEFORE TRUNCATE`: reject. |
| `positions` | `id bigint identity PK`, `vehicle_id bigint` (**no FK**, ADR-0003), `lat`/`lng double precision` (not `numeric`: avoids base64 decimals), `recorded_at timestamptz` (event time, ordering key), `inserted_at` (ingestion time, never used) | Append-only: `BEFORE UPDATE OR DELETE` and `BEFORE TRUNCATE` reject. Index `(vehicle_id, recorded_at DESC, id DESC)` serves only the oracle. |
| `retired_vehicle_ids` | `vehicle_id PK`, `retired_at` | Trigger-maintained. **Not captured** by Debezium, never read by the worker. Blocks id reuse; tells the oracle "deleted" from "never existed". |

These triggers are what make the convergence precondition (§6) true at the source: exactly one payload per `(id, version)` and per `(vehicle_id, recorded_at, id)`.

Postgres runs with `wal_level=logical`. Publication created by Debezium (`publication.autocreate.mode=filtered`). Default `REPLICA IDENTITY` is enough: a delete only needs the PK (ADR-0004).

## 4. CDC contract (what the worker relies on)

Connector: `infra/debezium/connector.json` is the canonical, complete config. Key settings:

| Setting | Value | Why |
|---|---|---|
| `plugin.name` | `pgoutput` | built into Postgres, no extension |
| `topic.prefix` | `fleet` | topics `fleet.public.vehicles`, `fleet.public.positions` |
| `table.include.list` | `public.vehicles,public.positions` | |
| `snapshot.mode` | `initial` | snapshot (`op=r`) then stream (ADR-0006) |
| `key/value.converter` | `JsonConverter`, `schemas.enable=false` | plain JSON envelope, easy to parse and to fixture |
| `message.key.columns` | `public.positions:vehicle_id` | positions of one vehicle share a partition (ADR-0007; optimization, not correctness) |
| `tombstones.on.delete` | `true` (default) | worker ignores null-value records; keeps the topic compaction-ready |
| `skipped.operations` | `none` | a TRUNCATE (blocked by trigger anyway) would surface as `op=t` → poison, instead of being silently dropped (default skips `t`) |
| `heartbeat.interval.ms` | `10000` | confirms slot progress even when only non-captured tables (e.g. `retired_vehicle_ids`) produce WAL, so Postgres can recycle it |
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
| `r` | snapshot read | `after` | same as `c` |
| `c` | insert | `after` | `UpsertState` / `ApplyPosition` |
| `u` | update | `after` | `UpsertState` (positions: **poison** — append-only at the source) |
| `d` | delete | `before.id` **only** — other `before` columns are zero-filled under default replica identity (verified in fixtures) | `DeleteVehicle` (positions: **poison**) |
| any other (`t`, `m`, …) | truncate / message | — | **poison** (fail-fast, ADR-0005) |
| *(null value)* | Kafka tombstone | — | `Skip`, offset stored |

Parsing rules: unknown envelope fields are ignored (real payloads also carry `transaction`, `source.sequence`, `xmin`, `ts_us`/`ts_ns`); `source.table` must match the topic's table; `source.name` and `source.snapshot` are never used for decisions. Fields **not** used for ordering: `ts_ms`/`ts_us`/`ts_ns`, `source.lsn`, `txId`, Kafka offset/timestamp — rationale in ADR-0002.

**Real fixtures rule:** the parser's unit tests use JSON captured from the running connector (`tests/VehicleCache.UnitTests/Fixtures/`, see its README for provenance and findings), not hand-written guesses.

## 5. Read model (Valkey)

One hash per vehicle: `vehicle:{id}`.

| Field | Group | Source | Notes |
|---|---|---|---|
| `state` | state | `after.state` | absent ⇒ vehicle not (yet) known |
| `plate` | state | `after.plate` | |
| `stateChangedAt` | state | `after.state_changed_at` | canonical timestamp (below) |
| `stateVersion` | state | `after.version` | **guard** |
| `lat`, `lng` | position | `after.lat/lng` | canonical number (below) |
| `recordedAt` | position | `after.recorded_at` | canonical timestamp (below) |
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

**Canonical formats** (so the cache does not depend on Debezium's formatting, and the verifier can build identical strings from Npgsql values): timestamps are re-formatted as `yyyy-MM-ddTHH:mm:ss.ffffffZ` (UTC, always 6 fractional digits — `CdcTimestamps`); integers and doubles use invariant culture, shortest round-trip (`-19`, `-18.9186`). Re-formatting is a pure function of the payload, so invariant 2 holds.

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

**Precondition** (the argument is only valid if it holds): each ordering key identifies exactly one payload — `(vehicle id, version)` → one row image, `(vehicle id, recordedAtUs, positionId)` → one position. Otherwise "first arrival wins" and order matters. The source triggers (§3) guarantee it; the TB-10 generator respects it. Numbers compared in Lua (`version`, ids, µs) are assumed < 2^53 (exact as doubles).

## 7. Worker components (`src/VehicleCache.Worker` + `src/VehicleCache.Projection`)

```
KafkaCdcConsumer (BackgroundService, one per topic, group "vehicle-cache")
   └─ consume → {Vehicle|Position}ChangeMapper.Map(value) → CacheCommand
                                                          └─ IVehicleCacheWriter (Valkey, Lua script)
                                                               └─ on return: consumer.StoreOffset(result)
```

| Component | Project | Responsibility | Pure? |
|---|---|---|---|
| `DebeziumEnvelopeParser` (internal) | Projection | bytes → typed envelope (source-generated `System.Text.Json`, required fields enforced); table check; `before.id` | yes |
| `VehicleChangeMapper` | Projection | value → `UpsertState` / `DeleteVehicle` / `Skip` (null value) | yes |
| `PositionChangeMapper` | Projection | value → `ApplyPosition` / `Skip`; `u`/`d` are poison | yes |
| `CdcTimestamps` | Projection | canonical timestamp format + epoch µs | yes |
| `CacheCommand` (records) | Projection | `UpsertState`, `ApplyPosition`, `DeleteVehicle` (each a `CacheWrite` that knows its key, script and ARGV), `Skip(reason)` | yes |
| `LuaScripts` | Projection | embedded `.lua` resources | yes |
| `ValkeyVehicleCacheWriter` | Worker | runs the command's script (`ScriptEvaluateAsync`; SE.Redis caches SHA and uses EVALSHA), returns `Applied`/`Rejected`/`Skipped` | I/O |
| `KafkaCdcConsumer` | Worker | poll loop, `enable.auto.offset.store=false`, `StoreOffset` after write, auto-commit of stored offsets | I/O |
| `ProjectionMetrics` | Worker | counters applied / rejected-stale / skipped (logs + `/metrics` optional) — **never in Valkey** | I/O |

Consumer settings: `enable.auto.commit=true` + `enable.auto.offset.store=false` (commit only what was stored), `auto.offset.reset=earliest`, `isolation.level` default, `partition.assignment.strategy=cooperative-sticky`. Failure policy: parse error or Valkey error after retries ⇒ log with topic/partition/offset and **stop the host** (non-zero exit, Kubernetes restarts the pod; the event is redelivered). No DLQ in scope (ADR-0005).

Concurrency: one consume loop per topic, sequential within the loop. Safe to scale to N replicas or parallelize per partition because correctness is enforced in Valkey, not in process memory.

## 8. Mock writers (`src/VehicleCache.MockWriters`)

One console binary (System.CommandLine), Npgsql only. Never touches Kafka/Valkey.

| Command | Behavior |
|---|---|
| `scenario <file> [--seed]` | Executes a deterministic step list (`scenarios/*.json`). Used by e2e and integration tests. |
| `seed` | Shorthand for `scenario scenarios/seed.json`: rows written **before** the connector exists (snapshot test, TB-11). **Contains no deletes** — a pre-snapshot delete is invisible to CDC (§2, ADR-0004). |
| `vehicles --rate --count --seed --out-of-order-pct --delete-pct` | Inserts vehicles, random valid state transitions, deletes. "Out-of-order" = an update whose `state_changed_at` is *older* than the row's current one (a late report); the DB accepts it (see ADR-0002 §Decision 1). |
| `positions --rate --vehicles --seed --late-pct --dup-pct --orphan-pct` | Inserts positions; `late` = `recorded_at` in the past relative to the vehicle's latest; `dup` = same `(vehicle_id, recorded_at, lat, lng)` inserted twice; `orphan` = vehicle id not registered yet (registered later). |

Scenario step kinds: `insertVehicle`, `updateVehicle` (explicit business time — may be older than the current one), `deleteVehicle`, `insertPosition` (`times > 1` = duplicate fix), `burst` (seeded bulk: per vehicle an optional orphan fix, insert, a random mix of updates — some late — and positions — some late or duplicated —, optional delete followed by a late fix; vehicles interleaved). A `burst` expands deterministically (`BurstGenerator`, no clock, no I/O), so a scenario file always produces the same statements. Global `--rate` paces statements (e2e: 150/s ≈ 9 s for `scenarios/e2e.json`, ~1,400 statements) so chaos steps land mid-stream. Every step carries a `note` naming the TB it exercises.

`vehicles` / `positions` are the brief's free-running "configurable rate" writers (seeded, but timestamps come from the clock); the e2e uses only `seed` and `scenario`.

## 9. Verifier (`src/VehicleCache.Verifier`) — test harness, allowed to read Postgres

| Command | Behavior |
|---|---|
| `expect --timeout 120s` | Builds the **oracle** from Postgres (§2 formulas; deleted ids from `retired_vehicle_ids`; values in the §5 canonical formats) and polls Valkey until equal or timeout. Prints a diff on failure. |
| `dump --out file.json` | `SCAN vehicle:*` → canonical JSON (sorted keys and fields). |
| `diff a.json b.json` | Byte-equality of two canonical dumps; prints the differing keys. |

## 10. Non-goals (explicit cuts)

Exactly-once / Kafka transactions · DLQ · schema registry/Avro · auth/TLS · Strimzi/Helm · multi-replica worker in e2e · tombstone TTL · read API (readers use Valkey directly; a `valkey-cli` example is in the README) · observability stack.

## 11. Implementation plan (sequenced by risk, time-boxed)

| # | Phase | Box | Done when | Agent |
|---|---|---|---|---|
| 0 | **Env spike** (biggest risk: WSL2 + Nix + devenv + kind + devspace) | 1.5h | `just up` brings Postgres+Kafka+Connect+Valkey on kind; connector registered; `kafka-console-consumer.sh` shows a real event | platform |
| 1 | Docs v1: ADRs `Proposed`, spec, C4, TBs → **book the 30-min design review** | 1h | reviewed by `architecture-reviewer` + `interview-examiner` | main |
| 2 | Projection core: parser + mappers (fixtures captured in phase 0), Lua scripts, **permutation/duplication convergence test** on Testcontainers Valkey | 1.5h | TB-01…TB-11, 16, 17 green; TB-10 proven to fail when a guard is removed (mutation check) | worker, test |
| 3 | Worker host: consumers, offset store, fail-fast, image | 1h | ✅ pod runs in kind, cache fills from CDC; consumer poison test on real Kafka | worker, platform |
| 4 | Writers + `scenarios/e2e.json` (covers every TB) | 1h | ✅ seed + e2e scenarios run as Jobs on kind; cache matched the DB on every hard case (70/70 keys); ScenarioRunner tested on real Postgres | source |
| 5 | `just e2e`: seed → connector (snapshot) → scenario → worker kill → offset rewind → expect → dump → flush+replay → expect → diff | 1.5h | green twice in a row from `just clean` | platform, test |
| 6 | Final docs, README, PR description, AI log, interview rehearsal | 1h | `architecture-reviewer` finds no drift; C4 = manifests | main, examiner |

**Cut line if time runs out (in order):** random-load writer modes → `message.key.columns` → Component diagram polish → metrics. **Never cut:** convergence test, replay diff, offset-rewind duplicate injection, ADRs.

## 12. Open questions for the design review

1. Vehicle ordering = DB row truth (`version`) vs business time (`state_changed_at`). We chose row truth (ADR-0002); confirm their read path expects "mirror of the table".
2. Deleted vehicle representation (tombstone hash forever) vs key removal — are readers OK checking `deleted`?
3. Are positions for unknown vehicles legitimate data (no FK) in their domain?
