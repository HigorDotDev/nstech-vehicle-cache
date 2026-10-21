# ADR-0003 — One hash per vehicle, independent field groups, Lua compare-and-set

- **Status:** Proposed
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §5, §6; TB-05, TB-07, TB-09, TB-10

## Context
Two topics update the same logical record with independent lag. A check-then-act in C# races between the two consumers (and between replicas). Positions can arrive before the vehicle row (orphans are legitimate: no FK, devices may report before registration).

## Decision
- Key `vehicle:{id}` → Valkey **hash** with two field groups (state; position) plus `deleted`.
- Each group has its own guard and is written by its own **Lua script** (`apply_state`, `apply_position`, `delete_vehicle`), atomic on a single key (no cross-slot issue if clustered later).
- **No buffering:** a position for an unknown vehicle is written immediately; the hash itself is the join. The state group fills in later.
- Each group is a last-writer-wins register over a strict total order; `deleted` is absorbing and checked first. Hence the merge is commutative, associative and idempotent ⇒ any permutation/duplication converges (TB-10).
- `positions.vehicle_id` has **no foreign key**.

## Alternatives considered
| Option | Rejected because |
|---|---|
| Separate keys `state:{id}` / `pos:{id}` | Two round trips per read; no atomic view; tombstone must be checked across keys |
| `WATCH`/`MULTI` optimistic transactions | Retry loops, more round trips, same guarantee as a script |
| In-process lock / per-vehicle actor | Doesn't cover multiple replicas or restarts |
| Buffer orphan positions in memory / pending set | Lost on restart, unbounded, order-dependent ⇒ breaks replay identity |
| Valkey Functions (`FUNCTION LOAD`) | Nicer lifecycle, but EVALSHA + reload-on-NOSCRIPT is simpler and fully supported by StackExchange.Redis |
| JSON string value (`SET vehicle:{id} <json>`) | Whole-document rewrite; still needs a script for the guard |

## Consequences
+ Correctness lives in ~40 lines of Lua, testable against a real Valkey via Testcontainers.
+ The worker is stateless; scaling or parallelizing is safe.
− Logic split between C# (mapping) and Lua (guards) — mitigated by keeping Lua minimal and covering it with the convergence property test.
− A reader can observe a hash with position but no state (documented as "state UNKNOWN").
