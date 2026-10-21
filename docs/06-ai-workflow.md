# 06 — AI Workflow & Rejected Suggestions

The brief says AI use is expected and that the interview probes "where an AI suggestion would have been wrong". This file is the evidence.

## How AI was used

- **Human-authored intent:** problem framing, invariants (CLAUDE.md), ordering-key choice, the convergence argument.
- **AI-refined:** document drafts, Mermaid diagrams, boilerplate (manifests, consumer loop, DI), test scaffolding.
- **Guardrails:** domain subagents with narrow scopes (`.claude/agents/`), a read-only `architecture-reviewer`, and an `interview-examiner` that attacks the design. Every AI change passes the `cdc-correctness-check` skill and real-payload fixtures.

## Rejected / corrected AI suggestions (keep appending)

| # | Suggestion | Why it is wrong | What we did |
|---|---|---|---|
| 1 | Order vehicle updates by Debezium `ts_ms` | `ts_ms` is capture/processing time; snapshot rows all get the snapshot time; not monotonic per row across restarts | Per-row `version` bumped by trigger (ADR-0002) |
| 2 | Use `source.lsn` as the state version | Works per row in steady state, but snapshot rows share one LSN, a re-snapshot re-emits same data at a higher LSN, and a restore/failover breaks monotonicity; also untestable without a DB | `version` column; LSN logged only |
| 3 | "Read the current value, compare in C#, then write" | Check-then-act race between the two consumers / replicas | Single Lua script per key (ADR-0003) |
| 4 | Buffer positions in memory until the vehicle arrives | Lost on restart, unbounded, makes replay depend on timing | Position group stored immediately; the hash *is* the join (ADR-0003) |
| 5 | `DEL vehicle:{id}` on delete | A redelivered older update resurrects the vehicle | Absorbing tombstone `deleted=1` (ADR-0004) |
| 6 | Debezium keys positions by vehicle, so per-vehicle order is guaranteed | Default key is the table PK (`positions.id`) ⇒ positions of one vehicle spread across partitions; and even with one partition, insert order ≠ `recorded_at` order | Guard on `(recorded_at, id)`; `message.key.columns` only as an optimization (ADR-0007) |
| 7 | Kafka transactions / exactly-once | Kafka EOS does not extend to Valkey writes | At-least-once + idempotent conditional writes (ADR-0005) |
| 8 | Store `updatedAt = DateTime.UtcNow` in the cache for observability | Breaks replay-identical dumps | Purity invariant; observability in logs/metrics |
| 9 | 5-project layered solution (Domain/Infrastructure/…) | Gold-plating for a 6–8h budget; adds no testability over one pure library | Projection (pure) + 3 executables |
| 10 | `numeric(9,6)` for lat/lng | Debezium default `decimal.handling.mode=precise` emits base64 bytes in JSON | `double precision` columns |

| 11 | (2026-10-21) Delete events carry the old row in `before` under default replica identity, so `before.version` can version the delete | Captured `vehicles.delete.json`: non-PK columns are **zero-filled** (`plate:""`, `state_changed_at:1970-01-01…`, `version` = column default). Trusting them would write garbage or a fake version | Parser reads only `before.id`; delete is terminal and needs no version (ADR-0004) |
| 12 | (2026-10-21) Pin the kind node image to an older Kubernetes to work around the cluster failing to start on WSL2 | Masks the real cause (Docker on cgroup v1; Kubernetes ≥ 1.35 refuses it) and makes the author's environment differ from the reviewers' Linux hosts | Enabled cgroup v2 in WSL (`kernelCommandLine = cgroup_no_v1=all`); kind default node image kept |

_Add entries as they happen during implementation, with the date._

## Assumptions verified against real payloads (Phase 0)

See `tests/VehicleCache.UnitTests/Fixtures/README.md`: zero-filled delete `before`; one shared `source.lsn` for all snapshot rows; ISO-8601 µs timestamps; positions keyed by `vehicle_id`.
