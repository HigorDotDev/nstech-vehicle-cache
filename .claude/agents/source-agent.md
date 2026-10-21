---
name: source-agent
description: Owns the CDC source side — PostgreSQL DDL/init scripts, the Debezium connector JSON, the mock writers console (scenario/seed/vehicles/positions commands) and the deterministic scenario files. Use for infra/postgres, infra/debezium, src/VehicleCache.MockWriters and scenarios/.
tools: Read, Write, Edit, Bash, Grep, Glob
model: inherit
---

# Source Agent — Vehicle Cache

## Read before coding
`docs/02-spec.md` §3, §4, §8 · ADR-0002 (version trigger, out-of-order semantics) · ADR-0004 (ids never reused) · ADR-0003 (no FK) · skill `debezium-event-contract`.

## Responsibilities
- `infra/postgres/001_schema.sql` exactly as spec §3 (incl. `writer_deletions`, the version trigger, no FK, `double precision`).
- `infra/debezium/connector.json` exactly as the spec §4 table; registration is idempotent (`PUT .../config`).
- `VehicleCache.MockWriters`: System.CommandLine with `scenario`, `seed`, `vehicles`, `positions`. Seeded `Random`, `TimeProvider`. Writes **only** to Postgres via Npgsql. On delete, insert into `writer_deletions` in the same transaction.
- `scenarios/e2e.json` must exercise every TB the e2e claims: pre-snapshot seed rows, late positions, duplicate inserts, equal `recorded_at`, orphan positions (vehicle inserted later), out-of-order business updates, delete followed by a late position, delete of a vehicle with no positions.

## Injection semantics (be precise — the interviewers will ask)
- *Out-of-order vehicle update* = an UPDATE whose `state_changed_at` is older than the row's current value. The DB accepts it; `version` still increments; the cache mirrors the DB (ADR-0002). Transport-level disorder of vehicle events comes from the e2e offset rewind and the convergence test, not from the writer.
- *Late position* = `recorded_at` earlier than the vehicle's newest position, inserted later (higher id).
- *Duplicate position* = identical `(vehicle_id, recorded_at, lat, lng)` inserted twice (two ids).

## Never
- Touch Kafka or Valkey from the writers.
- Set `version` explicitly in an UPDATE (the trigger owns it).
- Reuse a vehicle id after delete.
