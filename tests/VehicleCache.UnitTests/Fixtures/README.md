# Captured Debezium fixtures

Captured on 2026-10-21 from the real pipeline (`just up`), not hand-written.

- Debezium: `quay.io/debezium/connect:3.2.0.Final`, PostgreSQL connector, `pgoutput`
- PostgreSQL `17.6`, JSON converter with `schemas.enable=false`, `infra/debezium/connector.json`
- Each fixture is the record **value**; `<name>.key.json` is the record **key**. An empty value file = Kafka tombstone (null value).

| Fixture | Topic / partition / offset | What it shows |
|---|---|---|
| `vehicles.insert` | `fleet.public.vehicles` 0/0 | `op=c`, `version=1` |
| `vehicles.update` | `fleet.public.vehicles` 0/1 | `op=u`, `before=null`, `version=2` (trigger) |
| `vehicles.delete` | `fleet.public.vehicles` 0/3 | `op=d`, `after=null`, **`before` has non-PK columns zero-filled** (see below) |
| `vehicles.tombstone` | `fleet.public.vehicles` 0/4 | null value after the delete |
| `vehicles.snapshot-first` | `snap.public.vehicles` 1st | `op=r`, `source.snapshot="first"` |
| `vehicles.snapshot-last-in-collection` | `snap.public.vehicles` 2nd | `op=r`, `source.snapshot="last_in_data_collection"`, **same `source.lsn` as the first** |
| `positions.insert` | `fleet.public.positions` 2/0 | key `{"vehicle_id":1}` (`message.key.columns`), `recorded_at` with µs |
| `positions.insert-late` | `fleet.public.positions` 2/1 | higher `id`, older `recorded_at` |
| `positions.insert-orphan` | `fleet.public.positions` 1/0 | position for vehicle 2, committed before vehicle 2's insert |

Snapshot fixtures come from a temporary second connector (`topic.prefix=snap`, own slot/publication) registered after the rows existed, then removed.

## Findings that changed the docs

1. **Delete `before` is not trustworthy beyond the PK.** Under `REPLICA IDENTITY DEFAULT`, Debezium fills the non-key columns with type defaults (`""`, `1970-01-01T00:00:00Z`, column default `1` for `version`). The parser reads only `before.id` on `op=d`.
2. **All snapshot rows share one `source.lsn`** (`26393600` here) — confirms ADR-0002's rejection of LSN as a per-row version.
3. Timestamps (`timestamptz`) arrive as ISO-8601 with exactly 6 fractional digits and `Z`.
4. Debezium 3.2 adds `ts_us`/`ts_ns` to the envelope and `source`; ignored by the worker.
