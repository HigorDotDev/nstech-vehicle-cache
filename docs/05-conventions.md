# 05 — Conventions & Definition of Done

## General

- English everywhere in the repo (code, docs, commits, PR). Chat with the author may be Portuguese.
- KISS beats extensibility: three executables + one pure library. No MediatR, no generic repository, no DI abstractions with a single implementation unless a test needs the seam.
- Comments only where the code cannot say it — the *why* of an ordering rule, never the *what*.

## .NET

- .NET 10, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `Directory.Build.props` + `Directory.Packages.props` (central package management).
- File-scoped namespaces, `record`/`record struct` for events and commands, `sealed` by default.
- `TimeProvider` for any time read (writers only — the worker never reads the clock for cache values).
- `System.Text.Json` with source generation for the Debezium models; `JsonNamingPolicy.SnakeCaseLower` for `after`/`before` rows.
- Logging: `ILogger` with structured fields `{Topic} {Partition} {Offset} {VehicleId} {Op} {Outcome}`; no string interpolation in log templates.
- Async all the way; `CancellationToken` from `BackgroundService.ExecuteAsync` passed down.

## Libraries (ADR-0008)

Confluent.Kafka · StackExchange.Redis · Npgsql (writers, verifier only) · System.CommandLine · xUnit · Shouldly (FluentAssertions v8+ is commercial — avoid) · Testcontainers (Redis-compatible generic container with `valkey/valkey` image, Kafka, PostgreSQL).

## Always

- Route every cache mutation through `IVehicleCacheWriter` → Lua.
- Store an offset only after the write returned (Applied **or** Rejected — both are "processed").
- Add/extend a `TB-xx` before adding a behavior; reference it in the test name or `[Trait("TB","07")]`.
- Capture real Debezium payloads as fixtures; never hand-craft the envelope shape from memory.
- Update `docs/06-ai-workflow.md` when an AI suggestion is rejected for a correctness reason.

## Never

- Npgsql (or any DB client) in `VehicleCache.Worker` / `VehicleCache.Projection`.
- Wall-clock, offsets, pod names or counters inside `vehicle:*` hashes.
- `ts_ms`, `source.lsn`, Kafka timestamp or arrival order as an ordering key.
- Swallowing a parse/write exception and moving on.
- In-memory buffers/joins in the worker (they do not survive restart and break replay determinism).
- `sleep` in `just` recipes where a `kubectl wait` or a verifier poll can be used.
- Weakening an assertion or extending a timeout to make e2e pass without understanding why it failed.

## Definition of Done (per change)

1. Builds with zero warnings; unit + integration tests green.
2. Related `TB-xx` automated (or cut documented).
3. Docs updated in the same commit (spec / ADR / C4) — `architecture-reviewer` reports no drift.
4. `cdc-correctness-check` skill run for worker/infra changes.
5. For infra changes: `just e2e` green from `just down`.

## Git

- Branch `feat/vehicle-cache`; small conventional commits (`feat(worker): …`, `docs(adr): …`, `test(projection): …`).
- History tells the story: docs/ADRs commits **before** implementation commits (it mirrors their RFC → ADR → Spec → tests → PR chain).
- Open the PR to `main`, **do not merge**. PR description: summary, how to run, decisions table (links to ADRs), what was cut and why, known limitations, AI usage note.
