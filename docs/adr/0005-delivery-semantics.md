# ADR-0005 — At-least-once delivery, offsets stored after write, fail-fast on poison

- **Status:** Proposed
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** spec §7; TB-12, TB-13, TB-16

## Context
The worker can crash between writing Valkey and committing offsets. Debezium itself re-emits events after its own restart. Exactly-once across Kafka and Valkey is not available.

## Decision
- Confluent.Kafka with `enable.auto.offset.store=false` and auto-commit: the consumer **stores** an offset only after the Lua call returned (Applied or Rejected); the background commit only commits stored offsets.
- Duplicates are made harmless by the conditional writes (ADR-0003) — idempotency by construction, no dedup table.
- Unparseable records or persistent Valkey errors (after bounded retries with backoff) ⇒ log with coordinates and **stop the host**; Kubernetes restarts the pod; the record is redelivered. No DLQ in scope.

## Alternatives considered
| Option | Rejected because |
|---|---|
| Kafka transactions / EOS | Only covers Kafka→Kafka; the side effect is in Valkey |
| Store offsets in Valkey atomically with the write | Possible, but couples cache to transport and breaks purity; unnecessary since writes are idempotent |
| Plain auto-commit (auto offset store) | Can commit before processing ⇒ data loss on crash |
| Skip and log poison messages | Silently wrong cache — the exact failure mode the brief warns about |
| DLQ topic | Valid production choice; out of scope, noted as evolution |

## Consequences
+ Simple, standard, survives kill -9 (TB-12).
− A single poison record halts its partition until fixed (deliberate: correctness over availability for a read model that can be rebuilt).
− Redelivery window = commit interval; harmless.
