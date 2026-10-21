# 03 — Test Behaviors

Status: **Phase 3** — U and I levels automated and green (43 tests); E level lands in Phase 5. Every behavior has an id `TB-xx` and an **Automation** line naming the test that proves it (tests carry `[Trait("TB","xx")]`). A behavior without automation must say why (and it is a cut, listed in the PR description).

Levels: **U** = unit (pure, no I/O, captured fixtures) · **I** = integration (Testcontainers Valkey running the real Lua) · **E** = e2e (`just e2e` on kind).

Notation: `cache(7)` = `HGETALL vehicle:7`. "Identical" always means equal **canonical dumps** (keys and fields sorted — spec §9), never raw `HGETALL` order: Valkey listpack hashes keep insertion order.

Mutation check (Phase 2): removing the ordering guard from `apply_position.lua` makes TB-10 fail on the first permutation — the suite does catch a naive "consume → HSET" worker.

---

### TB-01 — Vehicle state update is reflected
- **Given** `cache(7) = {state: ACTIVE, stateVersion: 1}`
- **When** an event for vehicle 7 with `version=2, state=MAINTENANCE` is applied
- **Then** `cache(7).state = MAINTENANCE` and `stateVersion = 2`
- Automation: I `GuardTests.Newer_version_is_applied` · U `VehicleChangeMapperTests.Update_maps_to_upsert_with_trigger_bumped_version` · E scenario

### TB-02 — New position is reflected
- **Given** `cache(7)` has position `(recordedAt 10:00, id 5)`
- **When** position `(10:05, id 6, -18.91, -48.27)` is applied
- **Then** `cache(7)` has `lat=-18.91, lng=-48.27, recordedAt=2026-10-21T10:05:00.000000Z, positionId=6`
- Automation: I `GuardTests.Newer_position_is_applied` · U `PositionChangeMapperTests.Insert_maps_to_apply_position_with_exact_microseconds` · E

### TB-03 — Stale position is ignored (late GPS, ingested after a newer one)
- **Given** `cache(1)` has the captured position `(10:06:00.123456, id 1)`
- **When** the captured late position `(10:01, id 2)` arrives — higher id (inserted later) but older `recorded_at`
- **Then** the script returns `0` and `cache(1)` still has `(10:06:00.123456, id 1)`
- Automation: I `GuardTests.Older_recorded_at_is_rejected_even_with_higher_id` (real fixtures) · E (writer `late`)

### TB-04 — Stale state is ignored (redelivered older version)
- **Given** `cache(7).stateVersion = 3`
- **When** an event with `version=2` is applied (redelivery after rebalance / offset rewind)
- **Then** returns `0`, `cache(7)` unchanged
- Automation: I `GuardTests.Older_version_is_rejected` · E (offset rewind step)

### TB-05 — Duplicate CDC event is a no-op
- **Given** a set of events (state, positions, orphan, delete) was applied
- **When** every event is applied again
- **Then** every script returns `0` and the canonical dump is byte-identical before/after
- Automation: I `GuardTests.Reapplying_any_event_does_not_change_dump` · E (offset rewind)

### TB-06 — Equal recorded_at tie-break is deterministic
- **Given** two positions for vehicle 7 with the same `recorded_at`, ids 20 and 21 (writer `dup`)
- **When** they are applied in either order
- **Then** `positionId = 21` in both orders
- Automation: I `GuardTests.Tie_breaks_on_position_id` (both orders)

### TB-07 — Position arrives before the vehicle exists
- **Given** no `cache(2)`
- **When** the captured orphan position `(10:02, id 3)` for vehicle 2 is applied, then vehicle 2 `v1 REGISTERED`
- **Then** after the position: `cache(2)` has only position fields (reader sees location, state UNKNOWN); after the insert: both groups present, position preserved
- **And** applying the two events in the opposite order yields an identical canonical dump
- Automation: I `GuardTests.Position_before_vehicle_is_joined_and_order_independent` · U `PositionChangeMapperTests.Orphan_position_maps_like_any_other` · E (writer `orphan`, no FK)

### TB-08 — Delete creates a tombstone
- **Given** `cache(7)` with state and position
- **When** a `d` event for vehicle 7 is applied
- **Then** `cache(7) = {deleted: 1}` (all other fields removed)
- **And** the Kafka tombstone (null value) that follows is skipped
- **And** the delete is read from `before.id` only (other `before` columns are zero-filled — captured fixture)
- Automation: I `GuardTests.Delete_leaves_only_tombstone` · U `VehicleChangeMapperTests.Delete_uses_only_before_id_because_other_columns_are_zero_filled`, `VehicleChangeMapperTests.Null_value_is_kafka_tombstone` · E

### TB-09 — Nothing resurrects a deleted vehicle
- **Given** `cache(7) = {deleted: 1}`
- **When** a state event (any version), any position, or a repeated delete for 7 is applied
- **Then** all return `0`; `cache(7)` unchanged
- **And** position-then-delete and delete-then-position converge to `{deleted: 1}`
- Automation: I `GuardTests.Deleted_is_absorbing`, `GuardTests.Position_and_delete_converge_in_either_order` · E (late position after delete)

### TB-10 — Convergence under permutation and duplication (the property behind 03–09)
- **Given** a generated event set for 50 vehicles (versions 1..n, late/duplicate-timestamp/orphan positions, deletes; seed `20261021`) that respects the spec §6 precondition (one payload per ordering key)
- **When** it is applied in 200 random permutations, each with ~20% random duplicates, each into an empty Valkey
- **Then** every canonical dump equals the reference projection computed independently from spec §2/§5 (max version, max `(recordedAtUs, id)`, delete wins)
- Automation: I `ConvergenceTests.Any_permutation_with_duplicates_converges` (seed and run printed on failure)

### TB-11 — Snapshot → streaming transition
- **Given** rows exist in Postgres **before** the connector is registered, and the worker cache is empty
- **When** the connector snapshots (`op=r`; `source.snapshot` is one of `true`, `first`, `first_in_data_collection`, `last_in_data_collection`, `last` — the worker never branches on it) and then streams updates to the same rows
- **Then** snapshot events are applied as upserts, later stream events with higher `version` win, and a snapshot read of an equal or older version after a stream event is rejected
- Automation: U `VehicleChangeMapperTests.Snapshot_read_maps_to_upsert` (captured `first` and `last_in_data_collection`) · I `GuardTests.Snapshot_event_older_than_stream_is_rejected` · E (seed phase runs before connector registration)

### TB-12 — Worker crash mid-stream loses nothing
- **Given** the scenario Job is writing and the worker has processed part of both topics
- **When** the worker pod is force-deleted (`--grace-period=0 --force`: no SIGTERM, no graceful `Close()`/commit) and restarts
- **Then** it resumes from the last **committed** offset; re-delivered events are rejected or re-apply identical values; final cache = oracle
- Automation: E step 6 `chaos:kill-worker` + `verifier expect`

### TB-13 — Forced redelivery (offset rewind)
- **Given** the worker caught up and dump `D0` was taken
- **When** the worker is stopped (group `Empty`), offsets are shifted back by up to 500 records **per partition** on both topics, and the worker restarts
- **Then** final cache = oracle and dump `D1` is byte-identical to `D0`
- Automation: E steps 7–9 (`checkpoint`, `chaos:rewind`, `verify` + `diff D0 D1`)

### TB-14 — Full replay from offset 0 rebuilds the identical cache
- **Given** dump `D1` of a converged cache
- **When** the worker is stopped, `FLUSHALL`, group reset `--to-earliest`, worker restarted, converge
- **Then** dump `D2` is byte-identical to `D1` **and** equals the oracle
- Automation: E step 10 `replay` + `verifier diff D1 D2`

### TB-15 — Cache equals the oracle (end state)
- **Given** a full e2e run (snapshot + scenario + chaos)
- **When** the verifier computes the oracle from Postgres (`vehicles` rows, `argmax(recorded_at, id)` per vehicle, `retired_vehicle_ids`; spec §5 canonical formats)
- **Then** every `vehicle:*` hash equals its oracle entry and there are no extra keys
- Precondition: the seed (pre-snapshot) data contains no deletes (spec §2)
- Automation: E `verifier expect`

### TB-16 — Poison event fails fast, never silently skipped
- **Given** a record whose value is not a valid Debezium envelope for its topic: not JSON, missing required fields, wrong table, `op` other than `r/c/u/d`, a position `u`/`d` (append-only), an unparseable timestamp
- **When** the worker consumes it
- **Then** the mapper throws `CdcFormatException`; the worker logs topic/partition/offset and exits non-zero; the offset is **not** stored
- Automation: U `VehicleChangeMapperTests.Malformed_events_are_poison`, `Event_from_another_table_is_poison`, `PositionChangeMapperTests.Update_or_delete_of_append_only_positions_is_poison`, `Invalid_timestamp_is_poison` · I `ConsumerTests.Poison_record_stops_without_storing_offset_and_is_redelivered` (real Kafka + Valkey: committed offset stays at the poison record, a restarted consumer stops on it again) · U `CdcRecordHandlerTests.Poison_record_throws_before_touching_the_cache`

### TB-17 — The worker never talks to PostgreSQL
- **Given** the Worker and Projection assemblies
- **Then** neither references a database client (Npgsql); Projection also references no Kafka/Valkey client; the worker Deployment has no Postgres env/secret
- Automation: U `ArchitectureTests.Worker_has_no_database_client_reference`, `ArchitectureTests.Projection_is_pure` (direct references) · review checklist for the Deployment

---

## Traceability to the brief

| Brief requirement | TB |
|---|---|
| state update reflected | 01 |
| new position reflected | 02 |
| out-of-order position/state ignored when stale | 03, 04, 06 |
| duplicate CDC event is a no-op | 05, 13 |
| delete/tombstone removes/flags | 08, 09 |
| position before vehicle | 07 |
| worker restart loses nothing | 12 |
| full replay rebuilds identical cache | 14 |
| snapshot vs streaming | 11 |
| cross-stream races | 07, 09, 10 |
