# ADR-0004 — Deletes: terminal tombstone hash; Kafka tombstones ignored; no compaction

- **Status:** Accepted (author decision, 2026-10-21; the optional design-review call was not held)
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §2, §3, §4, §5; TB-08, TB-09, TB-15

## Context
Vehicles can be deleted. Debezium emits `op=d` followed by a Kafka tombstone (null value). Under `REPLICA IDENTITY DEFAULT` the delete's `before` carries the full column list, but **only the PK is real** — the other columns are zero-filled (`""`, `1970-01-01T00:00:00Z`, column defaults), as the captured `vehicles.delete.json` fixture shows. With at-least-once and replay, an older update may be re-applied *after* the delete.

## Decision
- On `op=d`: read `before.id` only; `delete_vehicle` replaces the hash with `{deleted: 1}`. `deleted` is **absorbing**: state and position scripts reject writes to a deleted vehicle.
- **Vehicle ids are never reused** (externally assigned fleet ids) — **enforced at the source**: an `AFTER DELETE` trigger records the id in `retired_vehicle_ids` and the insert trigger rejects retired ids. This is what makes "deleted" terminal and safe. `retired_vehicle_ids` is not captured by Debezium; the verifier oracle uses it.
- Kafka tombstone records (null value) are skipped (offset stored).
- Topics use `cleanup.policy=delete` with unlimited retention; no log compaction.
- Tombstone hashes are kept forever (no TTL) in scope.
- `REPLICA IDENTITY DEFAULT` is sufficient (a delete needs only the PK).

## Alternatives considered
| Option | Rejected because |
|---|---|
| `DEL` the key | A redelivered/replayed older event recreates the vehicle (resurrection) — visible to readers until the delete is reprocessed |
| Tombstone with TTL | Stale events arriving after TTL resurrect; replay dumps differ by TTL |
| Treat `DECOMMISSIONED` as delete | It is a state; the row still exists. Both are represented distinctly |
| Versioned delete to allow re-creation | Needs `REPLICA IDENTITY FULL` and an incarnation concept; unnecessary while ids are never reused |
| Read `before.version` to version the delete | `before` non-key columns are zero-filled under default replica identity — the "version" would be a column default, not data |

## Consequences
+ Delete and stale events commute ⇒ convergence holds.
− Tombstones accumulate (one tiny hash per deleted vehicle). Production evolution: TTL ≥ max replay horizon, or a periodic sweep.
− **Deletes before the initial snapshot are invisible to CDC**: the cache shows no key (or a position-only hash, since positions survive without FK) instead of `{deleted:1}`. Readers treat both as "not found"; the e2e seed contains no deletes so the oracle stays exact (spec §2).
− **If ids had to be reusable**, the tombstone would block the new vehicle. Mitigation: an incarnation id (e.g. UUID per registration) — stated as a known limit.
− **If topics become compacted**, a deleted vehicle's events eventually disappear; a replay would then produce *no key* instead of `{deleted:1}`. Readers treat both as "not found", but the byte-identical replay check would have to ignore tombstone hashes. Documented, not implemented.
− **Compacting `positions` would be wrong, not just different:** it is keyed by `vehicle_id` (ADR-0007), so compaction keeps the *last inserted* fix per vehicle — which may be a late one — not the argmax by `recorded_at`. A replay would restore an older position. Never compact the raw positions topic; a derived, compacted "latest position per vehicle" topic would be the safe evolution.
