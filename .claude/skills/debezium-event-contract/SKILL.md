---
name: debezium-event-contract
description: Reference for the Debezium PostgreSQL change-event contract used by the Vehicle Cache — envelope fields, op codes, snapshot markers, Kafka tombstones, key format, type mappings (timestamptz, double, numeric) and connector settings, plus how to capture real payloads as fixtures. Use when parsing events, writing fixtures, editing connector.json, or debugging an unexpected payload.
---

# Debezium event contract (PostgreSQL connector, JSON converter, schemas disabled)

**Rule zero:** never trust this file (or any model's memory) over a real payload. Capture fixtures from the running connector (below) and make tests use them.

## Envelope (record value)

| Field | Use |
|---|---|
| `op` | `r` snapshot read · `c` insert · `u` update · `d` delete (`t` truncate, `m` message — not expected; treat as poison) |
| `before` | `null` for `c`/`r`/`u` (default replica identity); for `d` **only the PK is real** — the other columns are present but zero-filled (`""`, `1970-01-01T00:00:00Z`, column defaults). Verified in `vehicles.delete.json`. Never read `before.version`. |
| `after` | full row for `r`/`c`/`u`; `null` for `d` |
| `source.snapshot` | `"true"`, `"first"`, `"first_in_data_collection"`, `"last_in_data_collection"`, `"last"`, `"false"`, `"incremental"` — informational only |
| `source.lsn`, `source.txId`, `source.sequence`, `source.ts_ms`/`ts_us`/`ts_ns`, `ts_ms`/`ts_us`/`ts_ns` | diagnostics/logging only — **never ordering** (ADR-0002). All snapshot rows share one `source.lsn` (verified). |
| `source.table` | `vehicles` / `positions` — assert it matches the topic |

After a delete, Debezium emits a second record with the same key and a **null value** (Kafka tombstone) when `tombstones.on.delete=true`. Handle `ConsumeResult.Message.Value == null` → `Skip`.

## Key
Default: JSON of the PK (`{"id":42}`). With `message.key.columns=public.positions:vehicle_id` the positions key becomes `{"vehicle_id":7}`. The worker should not depend on the key's content — the row is the source of truth.

## Type mappings that bite
| Postgres | JSON (defaults) | Note |
|---|---|---|
| `bigint` | number | fits `long` |
| `double precision` | number | use for lat/lng |
| `numeric(p,s)` | **base64 string** (`decimal.handling.mode=precise`) | avoid, or set `decimal.handling.mode=double/string` |
| `timestamptz` | ISO-8601 string with `Z` (`io.debezium.time.ZonedTimestamp`) | parse invariant; keep µs |
| `timestamp` (no tz) | epoch **micros** number (`MicroTimestamp`) under `adaptive` | avoid ambiguity: use `timestamptz` |
| `text` | string | |

## Connector config (spec §4)
The canonical, complete config is **`infra/debezium/connector.json`** — read it there; never copy it into docs or skills (copies drift). Settings that matter most: `pgoutput`, `snapshot.mode=initial`, `message.key.columns=public.positions:vehicle_id`, `tombstones.on.delete=true`, `skipped.operations=none`, `heartbeat.interval.ms`, `topic.creation.default.*` (3 partitions, `cleanup.policy=delete`, `retention.ms=-1`), JSON converters with `schemas.enable=false`.

Register with `PUT http://connect:8083/connectors/fleet/config` (idempotent). Check `GET /connectors/fleet/status` → connector and task `RUNNING`.

## Capturing fixtures
```bash
kubectl -n vehicle-cache exec kafka-0 -- /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server localhost:9092 --topic fleet.public.vehicles --from-beginning \
  --property print.key=true --property print.offset=true --timeout-ms 5000
```
Save one example per case under `tests/VehicleCache.UnitTests/Fixtures/` named `vehicles.snapshot-first.json`, `vehicles.update.json`, `vehicles.delete.json`, `vehicles.tombstone.json` (empty), `positions.insert.json`, … and record the Debezium image tag in a `README` next to them.

## Snapshot semantics in one paragraph
`snapshot.mode=initial` takes a consistent snapshot (`op=r`) of existing rows when no offsets exist, then streams from the slot's LSN. Changes committed during the snapshot are streamed afterwards. Recreating the connector under a new name (or losing Connect offsets) re-snapshots. The worker treats `r` exactly like `c`/`u`; the row `version` makes this safe (ADR-0006).
