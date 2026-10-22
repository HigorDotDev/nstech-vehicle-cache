# Architecture Decision Records

| # | Decision | Status |
|---|---|---|
| [0001](0001-cache-is-pure-cdc-projection.md) | The cache is a pure projection of CDC | Accepted |
| [0002](0002-ordering-keys.md) | Ordering keys: `version` / `(recorded_at, id)` | Accepted |
| [0003](0003-hash-per-vehicle-lua-guards.md) | One hash per vehicle, field groups, Lua CAS | Accepted |
| [0004](0004-deletes-and-tombstones.md) | Terminal tombstones, no compaction | Accepted |
| [0005](0005-delivery-semantics.md) | At-least-once, store-after-write, fail-fast | Accepted |
| [0006](0006-snapshot-and-replay.md) | Snapshot→stream and replay | Accepted |
| [0007](0007-positions-keyed-by-vehicle.md) | Positions keyed by `vehicle_id` | Accepted |
| [0008](0008-technology-choices.md) | Technology & libraries | Accepted |
| [0009](0009-local-environment.md) | devenv + kind + devspace + just | Accepted |

Status flow: `Proposed` → `Accepted` → `Superseded by ADR-xxxx`. All nine were accepted by the author on 2026-10-21; the optional design-review call with NsTech was not held. Template: `.claude/skills/write-adr/SKILL.md`.
