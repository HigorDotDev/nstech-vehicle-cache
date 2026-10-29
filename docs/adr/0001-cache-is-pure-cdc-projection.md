# ADR-0001 — The cache is a pure projection of CDC

- **Status:** Accepted (author decision, 2026-10-21; the optional design-review call was not held)
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §2, §5; TB-14, TB-15, TB-17

## Context
Readers need low-latency "state + latest position" per vehicle. The brief forbids the worker from reading the source DB and requires that a replay from offset 0 rebuilds the identical cache.

## Decision
1. The worker's only inputs are the two Debezium topics. No DB client is referenced by the Worker/Projection assemblies (enforced by an architecture test).
2. **Purity:** every value written to Valkey is a deterministic function of event payloads. No wall-clock, offsets, hostnames or counters in `vehicle:*`.
3. The cache is defined as a function of the source tables (state = current row or tombstone; position = `argmax(recorded_at, id)`), which gives the e2e verifier a precise **oracle** computed from Postgres.

## Alternatives considered
- *Worker reads Postgres to backfill/repair* — violates the brief; couples the read path to the write DB.
- *Cache includes processing metadata (`updatedAt`, offset)* — useful for debugging, but replay can never be byte-identical. Moved to logs/metrics.

## Consequences
+ Replay identity is testable by byte-diffing canonical dumps.
+ Valkey needs no persistence: it can always be rebuilt.
− **…but the rebuild is not automatic.** If Valkey restarts empty (OOM, eviction, node loss) the worker resumes from its committed offsets and the cache stays partial until each vehicle changes again. Recovery today is `just replay` (stop worker → `FLUSHALL` → offsets to earliest). Production evolution: a generation marker outside `vehicle:*` (e.g. `cache:generation`) checked at startup to trigger the reset automatically, with blue/green keyspaces so readers never see a half-built cache; or Valkey AOF. Detection is out of scope here.
− **Replay identity requires unlimited retention on both topics** (configured: `retention.ms=-1`). With limited retention a replay loses every vehicle whose last change or delete is older than the retention window — and the latest position of a parked vehicle can be arbitrarily old. Production alternatives: re-snapshot (new connector offsets / incremental snapshot) or a compacted "latest per vehicle" topic.
− Rebuild time is bounded by topic size (positions are high-volume).
