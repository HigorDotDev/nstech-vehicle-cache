# 06 — AI Workflow & Rejected Suggestions

The brief says AI use is expected and that the interview probes "where an AI suggestion would have been wrong". This file is the evidence.

## How AI was used

- **Human-authored intent:** problem framing, invariants (CLAUDE.md), ordering-key choice, the convergence argument.
- **AI-refined:** document drafts, Mermaid diagrams, boilerplate (manifests, consumer loop, DI), test scaffolding.
- **Guardrails:** domain subagents with narrow scopes (`.claude/agents/`) and a read-only `architecture-reviewer` that audits every change against the invariants, ADRs and C4. Every AI change passes the `cdc-correctness-check` skill and real-payload fixtures.

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

| 13 | (2026-10-21, own first draft) Treat a position `u` as an upsert "logged as anomaly" and ignore position `d` | An update to the current-latest row is rejected by the `<=` guard; deleting the latest row moves the DB argmax backwards — the cache silently diverges from the table either way | Positions append-only via trigger; position `u`/`d` are poison (ADR-0003) |
| 14 | (2026-10-21, own first draft) `version` trigger only `BEFORE UPDATE` | An INSERT could set any version; delete + re-insert or an id change forks `(id, version)` into two payloads — "first arrival wins" breaks convergence | `BEFORE INSERT OR UPDATE` trigger, immutable id, `retired_vehicle_ids` blocks reuse (spec §3/§6) |
| 15 | (2026-10-21, own first draft) Store timestamps "as emitted" by Debezium | Ties oracle equality to Debezium's formatting (the delete fixture shows `…00Z` without fractions) | Worker re-formats to a canonical `…ffffffZ` (pure function of the payload) |
| 16 | (2026-10-21, own first draft) Simulate a crash with `kubectl delete pod` | That is SIGTERM + 30 s grace: the consumer's `Close()` commits offsets, so the crash path is never exercised | `--grace-period=0 --force` in the e2e chaos step |

| 17 | (2026-10-21, own draft) `just e2e` starts with `just down` (delete namespace) | Worked on the dev machine, but on a fresh machine there is no kind cluster/context yet, so step 1 failed — caught only because the e2e was re-run after `just clean` | `down` is a no-op without a cluster; "green twice in a row from `just clean`" stays a release criterion (spec §11) |
| 18 | (2026-10-21, own draft) Rewind by 500 records per partition | Every partition held < 500 records, so the "rewind" silently became a full replay without a flush — TB-13 and TB-14 tested the same thing | Shift by 100 and print offsets before/after the reset |

| 19 | (2026-10-21, own draft) "Valkey needs no persistence: it can always be rebuilt" | True only by hand: after a Valkey restart the worker resumes from committed offsets and the cache stays partial | ADR-0001 consequence + `just replay` runbook; automatic generation marker documented as evolution |
| 20 | (2026-10-21, own draft) "A poison record halts its partition" | `StopApplication()` stops the whole process — both consumers, every partition | ADR-0005 corrected; DLQ / per-vehicle quarantine as evolution |
| 21 | (2026-10-21, own draft) Lenient timestamp parsing (`TryParse` + `AssumeUniversal`) | A date-only or zone-less value would be silently accepted and guessed as UTC instead of being poison | `TryParseExact` with a required zone; unit tests for the rejected shapes |
| 22 | (2026-10-21, own draft) Only `apply_position` was mutation-checked | `apply_state` / `delete_vehicle` could have had untested guards; the e2e only printed rejected counts | All three guards mutation-checked; e2e now requires > 0 rejected positions and ≥ 1 snapshot read |

| 23 | (2026-10-29, own draft) `devspace deploy` relying on devspace's build cache | Found by a manual fresh-clone run: after `just clean`, devspace "skipped" the unchanged image and so never loaded it into the new kind cluster → worker `ImagePullBackOff`. Earlier automated runs always had a code change that forced a rebuild, hiding it | `devspace deploy --force-build` (Docker layer cache keeps it cheap) |

Entries 19–22 came from the final `architecture-reviewer` pass and a self-review of failure modes before opening the PR.

Entries 13–16 were found by the `architecture-reviewer` subagent auditing the drafts before Phase 2 — an AI reviewing AI output is part of the workflow, but every fix was reasoned through and is covered by a test or an e2e step.

_Add entries as they happen during implementation, with the date._

## Assumptions verified against real payloads (Phase 0)

See `tests/VehicleCache.UnitTests/Fixtures/README.md`: zero-filled delete `before`; one shared `source.lsn` for all snapshot rows; ISO-8601 µs timestamps; positions keyed by `vehicle_id`.
