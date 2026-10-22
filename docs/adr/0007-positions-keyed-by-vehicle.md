# ADR-0007 — Positions topic keyed by `vehicle_id` (optimization)

- **Status:** Accepted (author decision, 2026-10-21; the optional design-review call was not held)
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §4

## Context
By default Debezium keys records by the table PK, so `positions` are spread across partitions by `positions.id`: events of one vehicle land on different partitions with independent progress.

## Decision
Set `message.key.columns=public.positions:vehicle_id`, so all positions of a vehicle share a partition. **Correctness does not depend on this** (the guard handles any order); it reduces cross-partition reordering, keeps per-vehicle work on one consumer, and enables future per-vehicle batching.

## Alternatives considered
- Keep the default key — correct, but more reordering and more rejected writes.
- `PartitionRouting` / `ComputePartition` SMT — same effect, more config.
- Single-partition topics — trivial transport ordering but no scalability; and transport order still ≠ `recorded_at` order.

## Consequences
− Hot vehicles create skew; irrelevant at challenge scale.
− Keying by `vehicle_id` makes log compaction of this topic *incorrect* (it would keep the last-inserted fix, not the latest by `recorded_at`) — see ADR-0004. Retention stays `cleanup.policy=delete`.
