# ADR-0006 — Snapshot → streaming and replay semantics

- **Status:** Accepted (author decision, 2026-10-21; the optional design-review call was not held)
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §4, §9; TB-11, TB-14

## Context
With `snapshot.mode=initial`, Debezium first emits `op=r` for existing rows (consistent snapshot at a slot LSN) and then streams changes after that LSN. A re-snapshot (new connector name, lost offsets) re-emits current rows.

## Decision
- `op=r` is handled exactly like `c`/`u` (upsert through the same guard). No special code path, no "snapshot finished" barrier.
- Because ordering keys come from the row (`version`, `recorded_at`, `id`), a snapshot read can never override newer streamed data, and a re-snapshot is a no-op on a converged cache.
- **Replay** = worker stopped → `FLUSHALL` → group offsets reset to earliest → worker started. Proven by TB-14 in `just e2e`.
- `heartbeat.interval.ms` set so the connector keeps confirming slot progress when the WAL it reads contains no captured changes (low traffic on the captured tables, activity only in other databases of the same cluster); otherwise the slot pins WAL. Heartbeats only help **while the connector runs** — see Consequences for a stopped connector.
- Precondition: the pre-snapshot data set contains **no deletes**. A row deleted before the snapshot never appears in CDC, so the cache cannot hold its tombstone (ADR-0004).

## Alternatives considered
- *`snapshot.mode=never` + seed the cache another way* — violates "only from CDC".
- *Special-case snapshot (e.g. clear the cache when `snapshot=first`)* — order-dependent, breaks under re-snapshot or partial snapshot.
- *Incremental snapshots (signal table)* — useful for adding tables later; not needed here.

## Consequences
+ One code path for all ops; replay is just "consume again".
− Replaying a long positions topic is slow; acceptable here. Production evolution: a compacted per-vehicle "latest position" topic.
− **A re-snapshot is a no-op only if no deletes were missed.** If the slot is lost (dropped, or invalidated by `max_slot_wal_keep_size`) and vehicles are deleted during the CDC gap, the new snapshot simply does not emit them: the cache keeps their state forever while the table no longer has them. Recovery = rebuild (fresh topics or offsets to earliest after `FLUSHALL`), not a re-snapshot on top of the old cache.
− **A stopped connector pins WAL without bound**: the slot retains every segment until Debezium returns, and no `max_slot_wal_keep_size` is configured here. Production: set it, alert on `pg_current_wal_lsn() - confirmed_flush_lsn` and `wal_status`, and accept that exceeding it means the rebuild above.
