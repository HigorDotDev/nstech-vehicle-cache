---
name: cdc-correctness-check
description: Checklist that walks a change in the Vehicle Cache worker, Lua scripts, connector config or e2e pipeline through the eight CDC hard cases (stale, duplicate, cross-stream race, delete, restart, replay, snapshot, poison) and the purity/CDC-only invariants. Use before declaring any worker, projection, connector or e2e change done, and before committing.
---

# CDC correctness check

For each case, write one line: **case → how the changed code handles it → which test proves it (TB id)**. Any "not sure" or "no test" is a finding — fix or record it as an explicit cut.

| # | Case | Must hold | TB |
|---|---|---|---|
| 1 | Stale state (older `version` after newer) | rejected by `apply_state` (`<=`) | 04 |
| 2 | Stale position (older `recorded_at`, higher id) | rejected; tie on `recorded_at` broken by id | 03, 06 |
| 3 | Duplicate of any event | script returns 0; dump byte-identical | 05 |
| 4 | Position before vehicle | stored immediately; state joins later; both orders give same hash | 07 |
| 5 | Delete, then stale state/position (or reverse order) | `{deleted:1}` absorbing; order-independent | 08, 09 |
| 6 | Worker killed between write and commit | offset stored only after write; redelivery is a no-op | 12, 13 |
| 7 | Replay from offset 0 into empty cache | byte-identical dump; no wall-clock/offset/host in values | 14 |
| 8 | Snapshot `op=r` then stream; re-snapshot | `r` = upsert through same guard; older snapshot read rejected | 11 |
| 9 | Poison record | process stops; offset not stored; never skipped | 16 |

Then confirm the invariants with quick greps:

```bash
grep -rn "Npgsql" src/VehicleCache.Worker src/VehicleCache.Projection          # must be empty
grep -rnE "HashSet|StringSet|KeyDelete|\"HSET\"|\"DEL\"" src/VehicleCache.Worker  # only inside the Lua writer, if at all
grep -rnE "ts_ms|\.Lsn|TopicPartitionOffset.*<|Message\.Timestamp" src/VehicleCache.Projection  # never in comparisons
grep -rnE "DateTime(Offset)?\.(Utc)?Now|GetUtcNow" src/VehicleCache.Worker src/VehicleCache.Projection  # never feeding cache values
```

Finally ask: *"Would a naive `SET` worker also pass the tests I just ran?"* If yes, the tests do not exercise disorder — add a case to the convergence test or the scenario.
