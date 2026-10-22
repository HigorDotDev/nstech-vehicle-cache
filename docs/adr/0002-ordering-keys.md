# ADR-0002 — Ordering keys: `version` for vehicles, `(recorded_at, id)` for positions

- **Status:** Accepted (author decision, 2026-10-21; the optional design-review call was not held)
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §3, §6; TB-03, TB-04, TB-06, TB-11

## Context
"Newer" must be decided for two different kinds of data:
- `vehicles` is **mutable**: the cache should show the row's current value.
- `positions` is **append-only facts**: "latest" is a query over many rows by **business time**; GPS fixes are routinely ingested late, so insert order ≠ event order.

Candidates: Debezium `ts_ms`, `source.lsn`, Kafka offset, business timestamps, an explicit row version.

## Decision
1. **Vehicles — `vehicles.version`**, a `bigint` owned by a `BEFORE INSERT OR UPDATE` trigger: `1` on insert, `OLD.version + 1` on update, id changes rejected (writers cannot set, regress or fork it — one payload per `(id, version)`, the precondition of spec §6). The cache mirrors the DB row: if the write path accepts a late business update (older `state_changed_at`), the DB *and* the cache show it. Rejecting stale business updates is a write-path rule (`UPDATE … WHERE state_changed_at <= @ts`), not a cache rule — otherwise the cache becomes a second source of truth that disagrees with the table.
2. **Positions — `(recorded_at, id)` lexicographic.** `recorded_at` is event time; `id` (identity) is a deterministic tie-break for equal timestamps / duplicate inserts.
3. `ts_ms`, `source.lsn`, `txId`, Kafka offset/timestamp are **logged, never compared**.

## Alternatives considered
| Option | Rejected because |
|---|---|
| Debezium `ts_ms` | Capture time, not change order; all snapshot rows share the snapshot time; can collide; clock-based |
| `source.lsn` for vehicles | Monotonic per row in steady state, but: all snapshot rows carry the same LSN; a re-snapshot re-emits identical data with higher LSN; PITR restore / failover to another timeline breaks monotonicity; needs a real DB to produce realistic test values. A good *fallback* if a schema change is impossible. |
| Kafka offset | Per partition only, resets if the topic is recreated, meaningless across topics |
| `state_changed_at` (business time) for vehicles | Makes cache ≠ table when the DB accepts a late report; equal timestamps need a tie-break anyway. **Open question for the design review.** |
| `inserted_at` for positions | Ingestion time — exactly the thing that is out of order |

## Consequences
+ Pure integer/µs comparisons, trivially testable without Kafka or Postgres.
+ Snapshot events (`op=r`) need no special casing: they carry the row's `version`.
− Requires a schema change (`version` + trigger) in the source DB — acceptable here; in real systems it often already exists (optimistic concurrency column).
· Note: Lua compares numbers as doubles. `recorded_at` as µs epoch (~1.8e15), `version` and ids are assumed < 2^53, so comparisons are exact.
