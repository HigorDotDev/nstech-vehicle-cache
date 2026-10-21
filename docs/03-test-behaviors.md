# 03 — Test Behaviors

Status: **Draft**. Every behavior has an id `TB-xx` and an **Automation** line naming the test that proves it. A behavior without automation must say why (and it is a cut, listed in the PR description).

Levels: **U** = unit (pure, no I/O) · **I** = integration (Testcontainers Valkey running the real Lua; optional Kafka) · **E** = e2e (`just e2e` on kind).

Notation: `cache(7)` = `HGETALL vehicle:7`.

---

### TB-01 — Vehicle state update is reflected
- **Given** `cache(7) = {state: ACTIVE, stateVersion: 1}`
- **When** a `u` event for vehicle 7 with `version=2, state=MAINTENANCE` is applied
- **Then** `cache(7).state = MAINTENANCE` and `stateVersion = 2`
- Automation: I `StateGuardTests.Newer_version_is_applied` · E scenario step `s-update`

### TB-02 — New position is reflected
- **Given** `cache(7)` has position `(recordedAt 10:00, id 5)`
- **When** a `c` position event `(10:05, id 6, -18.91, -48.27)` is applied
- **Then** `cache(7)` has `lat=-18.91, lng=-48.27, recordedAt=10:05, positionId=6`
- Automation: I `PositionGuardTests.Newer_position_is_applied` · E

### TB-03 — Stale position is ignored (late GPS, ingested after a newer one)
- **Given** `cache(7)` has position `(10:05, id 6)`
- **When** position `(10:01, id 7)` arrives — higher id (inserted later) but older `recorded_at`
- **Then** the script returns `0` and `cache(7)` still has `(10:05, id 6)`
- Automation: I `PositionGuardTests.Older_recorded_at_is_rejected_even_with_higher_id` · E (writer `late`)

### TB-04 — Stale state is ignored (redelivered older version)
- **Given** `cache(7).stateVersion = 3`
- **When** an event with `version=2` is applied (redelivery after rebalance / offset rewind)
- **Then** returns `0`, `cache(7)` unchanged
- Automation: I `StateGuardTests.Older_version_is_rejected` · E (offset rewind step)

### TB-05 — Duplicate CDC event is a no-op
- **Given** any event `e` was applied
- **When** `e` is applied again (same `version` / same `(recordedAt, id)`)
- **Then** returns `0` and the canonical dump is byte-identical before/after
- Automation: I `IdempotencyTests.Reapplying_any_event_does_not_change_dump` · E (offset rewind)

### TB-06 — Equal recorded_at tie-break is deterministic
- **Given** two positions for vehicle 7 with the same `recorded_at`, ids 20 and 21 (writer `dup`)
- **When** they are applied in either order
- **Then** `positionId = 21` in both orders
- Automation: I `PositionGuardTests.Tie_breaks_on_position_id`

### TB-07 — Position arrives before the vehicle exists
- **Given** no `cache(9)`
- **When** position `(10:00, id 30)` for vehicle 9 is applied, then vehicle 9 insert `v1 REGISTERED`
- **Then** after the position: `cache(9)` has only position fields (reader sees location, state UNKNOWN); after the insert: both groups present, position preserved
- **And** applying the two events in the opposite order yields the identical hash
- Automation: I `CrossStreamTests.Position_before_vehicle_is_joined` · E (writer `orphan`, no FK)

### TB-08 — Delete creates a tombstone
- **Given** `cache(7)` with state and position
- **When** a `d` event for vehicle 7 is applied
- **Then** `cache(7) = {deleted: 1}` (all other fields removed)
- **And** the Kafka tombstone (null value) that follows is skipped and its offset stored
- Automation: I `DeleteTests.Delete_leaves_only_tombstone` · U `ParserTests.Null_value_is_tombstone` · E

### TB-09 — Nothing resurrects a deleted vehicle
- **Given** `cache(7) = {deleted: 1}`
- **When** a stale `u` (any version) or any position for 7 is applied
- **Then** both return `0`; `cache(7)` unchanged
- **And** position-then-delete and delete-then-position converge to `{deleted: 1}`
- Automation: I `DeleteTests.Deleted_is_absorbing` · E (late position after delete)

### TB-10 — Convergence under permutation and duplication (the property behind 03–09)
- **Given** a generated event set E for 50 vehicles (inserts, updates, deletes, late/dup/orphan positions), seed fixed
- **When** E is applied in 200 random permutations, each with ~20% random duplicates, each into an empty Valkey
- **Then** all 200 canonical dumps are identical **and** equal to the reference projection computed by sorting E by ordering keys
- Automation: I `ConvergenceTests.Any_permutation_with_duplicates_converges` (seed printed on failure)

### TB-11 — Snapshot → streaming transition
- **Given** rows exist in Postgres **before** the connector is registered, and the worker cache is empty
- **When** the connector snapshots (`op=r`, `source.snapshot` = `true`/`first`/`last`) and then streams updates to the same rows
- **Then** snapshot events are applied as upserts, later stream events with higher `version` win, and a snapshot event re-read after a newer stream event is rejected
- Automation: U `ParserTests.Snapshot_read_maps_to_upsert` (captured fixture) · I `StateGuardTests.Snapshot_event_older_than_stream_is_rejected` · E (seed phase runs before connector registration)

### TB-12 — Worker restart mid-stream loses nothing
- **Given** writers are producing and the worker has processed part of both topics
- **When** the worker pod is deleted (SIGKILL-equivalent, no graceful commit) and restarts
- **Then** it resumes from the last **stored** offset; events re-delivered are no-ops or re-apply identical values; final cache = oracle
- Automation: E step `chaos:kill-worker` + `verifier expect`

### TB-13 — Forced redelivery (offset rewind)
- **Given** the worker caught up
- **When** the worker is scaled to 0, the group offsets are shifted back by 500 on both topics, worker scaled to 1
- **Then** final cache = oracle and dump unchanged vs before the rewind
- Automation: E step `chaos:rewind`

### TB-14 — Full replay from offset 0 rebuilds the identical cache
- **Given** dump `D1` of a converged cache
- **When** worker scaled to 0, `FLUSHALL`, group reset `--to-earliest`, worker scaled to 1, converge
- **Then** dump `D2` is byte-identical to `D1` **and** equals the oracle
- Automation: E step `replay` + `verifier diff D1 D2`

### TB-15 — Cache equals the oracle (end state)
- **Given** a full e2e run (snapshot + scenario + chaos)
- **When** the verifier computes the oracle from Postgres (`vehicles` rows, `argmax(recorded_at,id)` per vehicle, `writer_deletions`)
- **Then** every `vehicle:*` hash equals its oracle entry and there are no extra keys
- Automation: E `verifier expect`

### TB-16 — Poison event fails fast, never silently skipped
- **Given** a record on `fleet.public.vehicles` whose value is not a valid Debezium envelope
- **When** the worker consumes it
- **Then** the worker logs topic/partition/offset and exits non-zero; the offset is **not** stored
- Automation: I `ConsumerFailureTests.Poison_record_stops_without_storing_offset` (Testcontainers Kafka) — *cut candidate*: U-level test of the handler instead

### TB-17 — The worker never talks to PostgreSQL
- **Given** the Worker project
- **Then** it has no reference to Npgsql and its Deployment has no Postgres env/secret
- Automation: U `ArchitectureTests.Worker_has_no_Npgsql_reference` (assembly reference check) · review checklist

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
