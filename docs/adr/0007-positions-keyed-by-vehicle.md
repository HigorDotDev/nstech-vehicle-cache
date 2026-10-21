# ADR-0007 — Positions topic keyed by `vehicle_id` (optimization)

- **Status:** Proposed
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
− Hot vehicles create skew; irrelevant at challenge scale. Cut candidate if time is short.
