# Architecture Decision Records

| # | Decision | Status |
|---|---|---|
| [0001](0001-cache-is-pure-cdc-projection.md) | The cache is a pure projection of CDC | Proposed |
| [0002](0002-ordering-keys.md) | Ordering keys: `version` / `(recorded_at, id)` | Proposed |
| [0003](0003-hash-per-vehicle-lua-guards.md) | One hash per vehicle, field groups, Lua CAS | Proposed |
| [0004](0004-deletes-and-tombstones.md) | Terminal tombstones, no compaction | Proposed |
| [0005](0005-delivery-semantics.md) | At-least-once, store-after-write, fail-fast | Proposed |
| [0006](0006-snapshot-and-replay.md) | Snapshot→stream and replay | Proposed |
| [0007](0007-positions-keyed-by-vehicle.md) | Positions keyed by `vehicle_id` | Proposed |
| [0008](0008-technology-choices.md) | Technology & libraries | Proposed |
| [0009](0009-local-environment.md) | devenv + kind + devspace + just | Proposed |

Status flow: `Proposed` → `Accepted` (after the design review) → `Superseded by ADR-xxxx`. Template: `.claude/skills/write-adr/SKILL.md`.
