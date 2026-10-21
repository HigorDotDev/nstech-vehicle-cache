---
name: e2e-debug
description: Runbook for diagnosing a failing `just up` or `just e2e` in the Vehicle Cache kind cluster — pod health, Postgres logical replication and slot, Kafka Connect/Debezium connector status, topics and consumer lag, worker logs, Valkey dump vs oracle diff. Use when the environment does not come up or the verifier reports a mismatch or timeout.
---

# e2e debug runbook

Work **upstream → downstream**; stop at the first broken hop. Namespace: `vehicle-cache` (`alias k='kubectl -n vehicle-cache'`).

1. **Pods** — `k get pods`; `k describe pod <x>` for `ImagePullBackOff` (image not loaded into kind? → `docker exec vehicle-cache-control-plane crictl images`), `CrashLoopBackOff` → `k logs <x> --previous`.
2. **Postgres** — `k exec sts/postgres -- psql -U postgres -d fleet -c "show wal_level"` (must be `logical`); `select slot_name, active, confirmed_flush_lsn from pg_replication_slots;`; `select * from pg_publication_tables;`.
3. **Connector** — `k exec deploy/connect -- curl -s localhost:8083/connectors/fleet/status` → connector + task `RUNNING`. `FAILED` → read `trace`. Not found → the registration Job failed: `k logs job/register-connector`.
4. **Topics** — `k exec sts/kafka -- /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list`; peek with `kafka-console-consumer.sh ... --max-messages 3 --from-beginning`. Compare the payload shape with the fixtures.
5. **Consumer lag** — `just lag` (`kafka-consumer-groups.sh --describe --group vehicle-cache`). Lag stuck > 0 + worker running → worker blocked (poison? Valkey down?).
6. **Worker** — `just logs worker`; look for `Outcome=` counts, fail-fast lines with `{Topic} {Partition} {Offset}`, restarts.
7. **Valkey** — `just cache > out/actual.json`; `k exec deploy/valkey -- valkey-cli HGETALL vehicle:<id>`.
8. **Verifier diff** — for each mismatching vehicle, classify:
   | Symptom | Likely cause |
   |---|---|
   | older position in cache | guard compares wrong field / string compare instead of numeric / µs conversion bug |
   | deleted vehicle has fields | script order (deleted not checked first) or `DEL` without tombstone |
   | vehicle missing state but DB has it | connector not capturing `vehicles`, or worker stuck on that topic |
   | replay dump ≠ first dump | impure value (clock/offset/host) or float formatting not round-trip |
   | extra keys | oracle missing `retired_vehicle_ids` or orphan positions not in oracle |
   | timeout, lag 0 | oracle wrong, not the worker — re-check the SQL |

Rules: never "fix" e2e by adding `sleep` or raising timeouts before you know which hop is slow; if a fix changes behavior, update the TB/spec in the same commit.
