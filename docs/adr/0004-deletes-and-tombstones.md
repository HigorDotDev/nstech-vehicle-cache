# ADR-0004 — Deletes: terminal tombstone hash; Kafka tombstones ignored; no compaction

- **Status:** Proposed
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §4, §5; TB-08, TB-09

## Context
Vehicles can be deleted. Debezium emits `op=d` (with `before` = PK under default replica identity) followed by a Kafka tombstone (null value). With at-least-once and replay, an older update may be re-applied *after* the delete.

## Decision
- On `op=d`: `delete_vehicle` replaces the hash with `{deleted: 1}`. `deleted` is **absorbing**: state and position scripts reject writes to a deleted vehicle.
- **Vehicle ids are never reused** (externally assigned fleet ids). This is what makes "deleted" terminal and safe.
- Kafka tombstone records (null value) are skipped (offset stored).
- Topics use `cleanup.policy=delete` with long retention; no log compaction.
- Tombstone hashes are kept forever (no TTL) in scope.
- `REPLICA IDENTITY DEFAULT` is sufficient (a delete needs only the PK).

## Alternatives considered
| Option | Rejected because |
|---|---|
| `DEL` the key | A redelivered/replayed older event recreates the vehicle (resurrection) — visible to readers until the delete is reprocessed |
| Tombstone with TTL | Stale events arriving after TTL resurrect; replay dumps differ by TTL |
| Treat `DECOMMISSIONED` as delete | It is a state; the row still exists. Both are represented distinctly |
| Versioned delete to allow re-creation | Needs `REPLICA IDENTITY FULL` and an incarnation concept; unnecessary while ids are never reused |

## Consequences
+ Delete and stale events commute ⇒ convergence holds.
− Tombstones accumulate (one tiny hash per deleted vehicle). Production evolution: TTL ≥ max replay horizon, or a periodic sweep.
− **If ids were ever reused**, the tombstone would block the new vehicle. Mitigation: an incarnation id (e.g. UUID per registration) — stated as a known limit.
− **If topics become compacted**, a deleted vehicle's events eventually disappear; a replay would then produce *no key* instead of `{deleted:1}`. Readers treat both as "not found", but the byte-identical replay check would have to ignore tombstone hashes. Documented, not implemented.
