# ADR-0008 — Technology and library choices

- **Status:** Accepted (author decision, 2026-10-21; the optional design-review call was not held)
- **Date:** 2026-10-21
- **Deciders:** Higor Buiatti
- **Related:** 05-conventions

## Decision
| Concern | Choice | Why / alternative |
|---|---|---|
| Source DB | PostgreSQL 17, `pgoutput` | Built-in logical decoding, no extension (`wal2json`/`decoderbufs` need installs) |
| CDC | Debezium 3.x on Kafka Connect | Mandated; JSON converter `schemas.enable=false` → simple fixtures. Avro + Schema Registry rejected: another container for no correctness gain here |
| Broker | Apache Kafka 3.9, KRaft, single broker | No ZooKeeper; RF=1 is fine locally |
| Cache | Valkey 8 | Mandated; hashes + Lua |
| Kafka client | **Confluent.Kafka** | librdkafka-based, de-facto .NET client, explicit offset-store control. Alt: KafkaFlow/MassTransit — abstractions hide the offset semantics we must control |
| Valkey client | **StackExchange.Redis** | Mature, RESP-compatible with Valkey, script SHA caching. Alt: Valkey GLIDE for .NET — official but younger; revisit later |
| DB client (writers/verifier) | Npgsql | Standard; no ORM needed |
| CLI | System.CommandLine | Writers/verifier subcommands |
| Tests | xUnit + Shouldly + Testcontainers | Real Valkey for Lua; FluentAssertions v8+ requires a commercial license |
| Hosting | `Microsoft.Extensions.Hosting` Worker Service | `BackgroundService` per topic |
