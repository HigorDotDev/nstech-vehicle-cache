# ADR-0006 — Snapshot → streaming and replay semantics

- **Status:** Proposed
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §4, §9; TB-11, TB-14

## Context
With `snapshot.mode=initial`, Debezium first emits `op=r` for existing rows (consistent snapshot at a slot LSN) and then streams changes after that LSN. A re-snapshot (new connector name, lost offsets) re-emits current rows.

## Decision
- `op=r` is handled exactly like `c`/`u` (upsert through the same guard). No special code path, no "snapshot finished" barrier.
- Because ordering keys come from the row (`version`, `recorded_at`, `id`), a snapshot read can never override newer streamed data, and a re-snapshot is a no-op on a converged cache.
- **Replay** = worker stopped → `FLUSHALL` → group offsets reset to earliest → worker started. Proven by TB-14 in `just e2e`.
- `heartbeat.interval.ms` set so the replication slot advances even if one table is idle (prevents WAL retention growth).

## Alternatives considered
- *`snapshot.mode=never` + seed the cache another way* — violates "only from CDC".
- *Special-case snapshot (e.g. clear the cache when `snapshot=first`)* — order-dependent, breaks under re-snapshot or partial snapshot.
- *Incremental snapshots (signal table)* — useful for adding tables later; not needed here.

## Consequences
+ One code path for all ops; replay is just "consume again".
− Replaying a long positions topic is slow; acceptable here. Production evolution: a compacted per-vehicle "latest position" topic.
