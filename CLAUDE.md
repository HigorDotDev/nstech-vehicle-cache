# Vehicle Cache — CDC-driven read model (NsTech Data Platform challenge)

A .NET 10 worker consumes Debezium CDC events (PostgreSQL → Kafka Connect → Kafka) for two tables, `vehicles` and `positions`, and maintains a Valkey read model answering, per vehicle: **"where is it now, and what state is it in?"**. The original brief is in `brief/case.md` (git-ignored — do not commit it, do not quote it in deliverables).

What is graded: Design docs 40% · Interview 25% · Reproducibility (`just e2e`) 20% · Build 15%. **Correctness of the projection and the docs beat features.** Do not gold-plate: target is 6–8 focused hours.

## Before generating anything

Every decision already lives in `docs/`. **No code may diverge from the docs silently.** If a doc is ambiguous or missing what the task needs, stop and ask; if a decision changes, update the ADR/spec first, then the code. Docs are the deliverable — code that contradicts them costs more points than missing code.

## Document map

| File | Content | Authority |
|---|---|---|
| `docs/01-c4.md` | C4 Context / Container / Component (Mermaid) | Must match what `just e2e` runs |
| `docs/02-spec.md` | Read model, event shapes, DDL, Valkey schema, Lua rules, components, implementation plan | **Source of truth for code** |
| `docs/03-test-behaviors.md` | Given/When/Then behaviors `TB-xx`, each mapped to an automated test | Source of truth for tests |
| `docs/04-environment.md` | devenv / kind / devspace / just design, `just e2e` pipeline | Source of truth for infra |
| `docs/05-conventions.md` | Coding rules, always/never, definition of done | Applies to every agent |
| `docs/06-ai-workflow.md` | How AI was used + log of AI suggestions that were rejected | Interview asset — keep it updated |
| `docs/adr/` | ADR-0001…0009, one decision each | Final decisions + alternatives |

Precedence when two docs disagree: **ADR > spec > everything else**, and the disagreement itself is a bug to fix in the same change.

## Non-negotiable invariants (break one = the challenge is failed)

1. **CDC-only.** The worker never opens a connection to PostgreSQL. Not to "fix up", not at startup, not in health checks. (The e2e *verifier* may read Postgres — it is the test oracle, not the worker.)
2. **Purity.** Every value written to Valkey is derived from event payloads. No wall-clock, no Kafka offsets, no hostnames in the cache. Otherwise replay cannot produce an identical cache.
3. **Conditional writes only.** Every cache mutation goes through a Lua script that compares the ordering key atomically. No blind `HSET`/`SET`/`DEL` from C#.
4. **Ordering keys** (ADR-0002): vehicle state → `vehicles.version` (DB trigger, monotonic per row); position → `(recorded_at, id)`. Never Debezium `ts_ms`, never Kafka offset, never consumer arrival order.
5. **Convergence.** For any permutation and any duplication of the same set of events, the final cache is identical. Field groups (state / position) are guarded independently so cross-topic interleaving does not matter.
6. **Deletes are terminal.** Vehicle ids are never reused; a delete leaves a tombstone hash (`deleted=1`) and rejects later state/position writes.
7. **At-least-once.** Offsets are stored only after the Valkey write succeeded. Unparseable events crash the consumer (fail-fast), they are never silently skipped.

## Agents (`.claude/agents/`)

| Agent | Use for | Never |
|---|---|---|
| `architecture-reviewer` | Review any change against the invariants, ADRs and C4; docs/code drift | Writes code |
| `worker-agent` | `src/VehicleCache.Worker`, `src/VehicleCache.Projection`, Lua scripts | Touches infra/manifests, queries Postgres |
| `source-agent` | DDL, Debezium connector config, `src/VehicleCache.MockWriters`, scenarios | Touches Kafka/Valkey from writers |
| `platform-agent` | `devenv.nix`, `devspace.yaml`, `k8s/`, `justfile`, `src/VehicleCache.Verifier` wiring | Changes projection rules |
| `test-agent` | Unit, Testcontainers integration, e2e verifier assertions, TB traceability | Weakens an assertion to make it pass |
| `interview-examiner` | Grill the author with "what breaks if…" questions; find holes in the docs | Writes code or docs |

Prefer delegating to the domain agent over doing cross-domain work in the main session.

## Skills (`.claude/skills/`)

- `cdc-correctness-check` — the 8 hard cases checklist; run before declaring any worker/infra change done.
- `debezium-event-contract` — envelope fields, op codes, snapshot markers, type mappings, gotchas.
- `valkey-conditional-write` — the Lua guard patterns and how to add a new field group.
- `write-adr` — ADR template and rules.
- `test-behavior` — how to add a `TB-xx` and link it to an automated test.
- `e2e-debug` — runbook for a red `just e2e` (connector status, lag, cache dump).

## Development flow (each step reviewed before the next)

1. Decide → ADR (status `Proposed`, then `Accepted` after the design review call).
2. Specify → `docs/02-spec.md` + C4 updated.
3. Behaviors → `docs/03-test-behaviors.md`.
4. Tests first for the projection (Lua via Testcontainers), then implementation.
5. Infra/e2e.
6. `architecture-reviewer` pass + `cdc-correctness-check` skill, then commit.

## Repo layout (target)

```
src/VehicleCache.Projection/   # pure: Debezium parsing → CacheCommand; Lua scripts as embedded resources
src/VehicleCache.Worker/       # BackgroundServices, Kafka consumers, Valkey writer
src/VehicleCache.MockWriters/  # one binary: `vehicles`, `positions`, `scenario <file>` commands
src/VehicleCache.Verifier/     # e2e oracle (reads Postgres + Valkey), cache dump/diff
tests/VehicleCache.UnitTests/
tests/VehicleCache.IntegrationTests/   # Testcontainers: Valkey (Lua), optional full pipeline
scenarios/                     # deterministic e2e scenarios (JSON)
infra/                         # postgres init SQL, connector JSON
k8s/                           # plain manifests deployed by devspace
devenv.nix  devenv.yaml  devspace.yaml  justfile  README.md
docs/  .claude/
```

## Conventions (summary — full list in `docs/05-conventions.md`)

- Code, docs, commits and PR in **English**. DB identifiers `snake_case`, JSON from Debezium is consumed as-is.
- .NET 10, nullable enabled, `TreatWarningsAsErrors`, file-scoped namespaces, `TimeProvider` (never `DateTime.Now`).
- Each commit small and green; the PR stays **open, never merged**.
