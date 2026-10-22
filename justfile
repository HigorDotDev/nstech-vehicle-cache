set shell := ["bash", "-euo", "pipefail", "-c"]

cluster := "vehicle-cache"
ns := "vehicle-cache"
ctx := "kind-vehicle-cache"
k := "kubectl --context kind-vehicle-cache -n vehicle-cache"
kafka_groups := "kubectl --context kind-vehicle-cache -n vehicle-cache exec kafka-0 -- /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --group vehicle-cache"

# List recipes
default:
    @just --list

# Bring the whole environment up (idempotent): kind, infra, worker, connector
up: cluster config deploy register-connector

# Full proof from a clean namespace: snapshot, chaos, rewind, oracle, replay (docs/04-environment.md §3)
e2e:
    #!/usr/bin/env bash
    set -euo pipefail
    k="{{k}}"
    step() { echo; echo "━━ [$(date -u +%H:%M:%S)] $*"; }
    trap 'echo; echo "✖ e2e FAILED at: ${current:-setup} (see: just logs worker | connect, just lag, .claude/skills/e2e-debug)"' ERR
    started=$SECONDS
    mkdir -p out && rm -f out/d0.txt out/d1.txt out/d2.txt

    current="1 reset";               step "$current — fresh namespace (Postgres, Kafka, slot, Valkey)"
    just down
    current="2 deploy";              step "$current — infra + worker (no connector yet)"
    just cluster config deploy
    current="3 seed";                step "$current — pre-snapshot rows, no deletes (TB-11)"
    just seed
    current="4 connector";           step "$current — register Debezium: initial snapshot emits op=r"
    just register-connector

    current="5-6 scenario + kill";   step "$current — streaming hard cases; force-kill the worker mid-stream (TB-12)"
    just job-start writers-scenario
    for _ in $(seq 1 120); do   # wait until the burst is visibly being projected, then pull the plug
      projected=$($k exec deploy/valkey -- valkey-cli --scan --pattern 'vehicle:10[0-5][0-9]' | wc -l)
      [ "$projected" -ge 10 ] && break; sleep 0.5
    done
    scenario_done=$($k get job writers-scenario -o jsonpath='{.status.succeeded}')
    $k delete pod -l app=worker --grace-period=0 --force 2>/dev/null
    if [ "${scenario_done:-0}" -ge 1 ]; then echo "kill landed AFTER the scenario finished — TB-12 not exercised"; false; fi
    echo "worker force-killed mid-stream ($projected burst vehicles projected so far)"
    just job-wait writers-scenario
    $k rollout status deployment/worker --timeout=180s

    current="7 checkpoint";          step "$current — cache == oracle, dump d0"
    just expect
    just wait-caught-up
    just dump d0

    current="8 rewind";              step "$current — shift offsets back ≤100/partition: partial forced redelivery (TB-13)"
    just lag | awk '$1=="vehicle-cache" {print "  before: " $2 "[" $3 "] offset " $4}'
    just rewind
    current="9 verify";              step "$current — cache == oracle after redelivery, d0 == d1 (TB-13, TB-15)"
    just wait-caught-up
    just expect
    just dump d1
    diff -u out/d0.txt out/d1.txt
    echo "d0 == d1 ($(wc -l < out/d1.txt) vehicles): redelivery changed nothing"

    current="10 replay";             step "$current — FLUSHALL + offsets to earliest: rebuild from offset 0 (TB-14)"
    just replay
    just expect
    just wait-caught-up
    just dump d2
    diff -u out/d1.txt out/d2.txt
    echo "d1 == d2: full replay rebuilt a byte-identical cache"

    current="11 summary";            step "$current"
    snapshot_reads=$(just topic fleet.public.vehicles 2>/dev/null | grep -c '"op":"r"' || true)
    echo "snapshot reads (op=r) on vehicles topic: $snapshot_reads"
    [ "$snapshot_reads" -ge 1 ] || { echo "no op=r events: the snapshot path was not exercised (TB-11)"; false; }
    # A cache that "passes" without ever rejecting a stale/duplicate fix would mean the scenario had no disorder.
    for _ in $(seq 1 30); do   # the worker logs its counters every 10 s
      stale_rejected=$($k logs deploy/worker | grep 'Projection fleet.public.positions' | tail -1 | sed -n 's/.*rejected=\([0-9]*\).*/\1/p')
      [ -n "$stale_rejected" ] && break; sleep 1
    done
    echo "worker after replay:"; $k logs deploy/worker | grep 'Projection' | tail -2 | sed 's/^/  /'
    echo "stale/duplicate positions rejected during replay: ${stale_rejected:-none}"
    [ "${stale_rejected:-0}" -gt 0 ] || { echo "replay rejected no positions: the scenario exercised no disorder"; false; }
    echo "cache: $(wc -l < out/d2.txt) vehicles · dumps in out/d0.txt out/d1.txt out/d2.txt"
    echo "covered: TB-01..TB-09 (scenario), TB-11 snapshot, TB-12 kill, TB-13 rewind, TB-14 replay, TB-15 oracle"
    echo "✔ e2e PASSED in $((SECONDS - started))s"

# Visual tools on localhost (Kafka UI + ports for DBeaver / Redis Insight); the in-cluster worker keeps running. Ctrl+C stops.
ui: (_tools "keep-worker")

# Debug mode: like `ui`, but the in-cluster worker is stopped so the worker you start from the IDE (F5) gets every event
dev: (_tools "stop-worker")

_tools mode:
    #!/usr/bin/env bash
    set -euo pipefail
    k="{{k}}"
    $k apply -f k8s/dev/kafka-ui.yaml >/dev/null
    $k rollout status deployment/kafka-ui --timeout=240s >/dev/null
    if [ "{{mode}}" = "stop-worker" ]; then just stop-worker; fi
    pids=()
    cleanup() {
      kill "${pids[@]}" 2>/dev/null || true
      if [ "{{mode}}" = "stop-worker" ]; then echo; echo "restoring the in-cluster worker…"; just start-worker; fi
    }
    trap cleanup EXIT
    # Each forward restarts itself if its pod is replaced (e.g. after `kubectl delete pod`).
    fwd() { ( while true; do $k port-forward "$1" "$2" >/dev/null 2>&1 || true; sleep 1; done ) & pids+=($!); }
    fwd svc/postgres   15432:5432
    fwd svc/valkey     16379:6379
    fwd pod/kafka-0    29092:29092
    fwd svc/kafka-ui   18080:8080
    fwd svc/connect    18083:8083
    sleep 2
    echo ""
    echo "  Kafka UI          http://localhost:18080        topics, messages, consumer group, Debezium connector"
    echo "  PostgreSQL        localhost:15432  db=fleet  user=postgres  password=postgres   (DBeaver / pgAdmin)"
    echo "  Valkey            localhost:16379                (Redis Insight; keys vehicle:*)"
    echo "  Kafka (external)  localhost:29092                (used by the worker started from the IDE)"
    echo "  Kafka Connect     http://localhost:18083/connectors/fleet/status"
    if [ "{{mode}}" = "stop-worker" ]; then
      echo ""
      echo "  In-cluster worker is STOPPED. Start VehicleCache.Worker from your IDE with the 'local-kind'"
      echo "  profile (DOTNET_ENVIRONMENT=Development), set breakpoints, then INSERT/UPDATE/DELETE in DBeaver."
    fi
    echo ""
    echo "  Ctrl+C to stop$([ "{{mode}}" = "stop-worker" ] && echo " (the in-cluster worker is restarted)")."
    wait

# Create the kind cluster and namespace if missing
cluster:
    kind get clusters 2>/dev/null | grep -qx {{cluster}} || kind create cluster --name {{cluster}} --wait 180s
    kubectl --context {{ctx}} create namespace {{ns}} --dry-run=client -o yaml | kubectl --context {{ctx}} apply -f -

# Render config files from infra/ into ConfigMaps
config:
    {{k}} create configmap postgres-init --from-file=infra/postgres/ --dry-run=client -o yaml | {{k}} apply -f -
    {{k}} create configmap debezium-connector --from-file=connector.json=infra/debezium/connector.json --dry-run=client -o yaml | {{k}} apply -f -

# Build images and deploy infra + worker, then wait until everything is ready
deploy:
    devspace deploy --kube-context {{ctx}} --namespace {{ns}}
    {{k}} rollout status statefulset/postgres --timeout=300s
    {{k}} rollout status statefulset/kafka --timeout=300s
    {{k}} rollout status deployment/valkey --timeout=300s
    {{k}} rollout status deployment/connect --timeout=300s
    {{k}} rollout status deployment/worker --timeout=180s

# Register (or update) the Debezium connector and wait until it is RUNNING
register-connector: (job "register-connector")

# Pre-snapshot data set (no deletes)
seed: (job "writers-seed")

# Streaming hard cases + seeded burst (scenarios/e2e.json, ~150 statements/s)
scenario: (job "writers-scenario")

# Poll until Valkey equals the Postgres oracle (verifier, TB-15)
expect: (job "verifier-expect")

# Canonical cache dump into out/<name>.txt
dump name:
    mkdir -p out
    just job verifier-dump > out/{{name}}.txt
    echo "dumped $(wc -l < out/{{name}}.txt) vehicles to out/{{name}}.txt"

# Canonical cache dump to stdout
cache:
    @just job verifier-dump

# Consumer group offsets and lag
lag:
    {{kafka_groups}} --describe 2>/dev/null

# Wait until the worker has consumed everything (lag 0 on every partition of both topics)
wait-caught-up:
    #!/usr/bin/env bash
    set -euo pipefail
    for _ in $(seq 1 180); do
      lag=$({{kafka_groups}} --describe 2>/dev/null | awk '$1=="vehicle-cache" && NF>=6 { if ($4=="-") { if ($5!="0") bad=1 } else lag+=$6 } END { print (bad ? -1 : lag+0) }')
      [ "$lag" = "0" ] && { echo "worker caught up (lag 0)"; exit 0; }
      sleep 1
    done
    echo "worker did not catch up (lag=$lag)"; exit 1

# Scale the worker to 0 and wait until the consumer group is Empty (offset resets are refused otherwise)
stop-worker:
    #!/usr/bin/env bash
    set -euo pipefail
    k="{{k}}"
    $k scale deployment/worker --replicas=0 >/dev/null
    $k wait --for=delete pod -l app=worker --timeout=120s 2>/dev/null || true
    for _ in $(seq 1 120); do
      {{kafka_groups}} --describe --state 2>/dev/null | grep -qw Empty && { echo "worker stopped, group Empty"; exit 0; }
      sleep 1
    done
    echo "consumer group never became Empty"; exit 1

# Scale the worker back to 1
start-worker:
    {{k}} scale deployment/worker --replicas=1 >/dev/null
    {{k}} rollout status deployment/worker --timeout=180s

# Forced redelivery: shift committed offsets back by up to 100 per partition (TB-13) — partial, unlike replay
rewind: stop-worker
    {{kafka_groups}} --reset-offsets --shift-by -100 --all-topics --execute >/dev/null 2>&1
    @echo "offsets shifted back by up to 100 per partition"
    just lag | awk '$1=="vehicle-cache" {print "  after:  " $2 "[" $3 "] offset " $4}'
    just start-worker

# Full rebuild: empty the cache and replay both topics from offset 0 (TB-14)
replay: stop-worker
    {{k}} exec deploy/valkey -- valkey-cli FLUSHALL >/dev/null
    @echo "cache flushed"
    {{kafka_groups}} --reset-offsets --to-earliest --all-topics --execute >/dev/null 2>&1
    @echo "offsets reset to earliest"
    just start-worker

# Run a one-shot Job from k8s/jobs/<name>.yaml; its logs go to stdout, status to stderr
job name: (job-start name) (job-wait name)

# Start a one-shot Job without waiting
job-start name:
    {{k}} delete job {{name}} --ignore-not-found --wait >/dev/null
    {{k}} apply -f k8s/jobs/{{name}}.yaml >/dev/null

# Wait for a Job: fail fast with its logs if it fails
job-wait name:
    #!/usr/bin/env bash
    set -euo pipefail
    k="{{k}}"
    for _ in $(seq 1 600); do
      succeeded=$($k get job {{name}} -o jsonpath='{.status.succeeded}')
      failed=$($k get job {{name}} -o jsonpath='{.status.failed}')
      if [ "${succeeded:-0}" -ge 1 ]; then $k logs job/{{name}}; exit 0; fi
      if [ "${failed:-0}" -ge 1 ]; then echo "job {{name}} failed:" >&2; $k logs job/{{name}} --tail=60 >&2 || true; exit 1; fi
      sleep 0.5
    done
    echo "job {{name}} timed out" >&2; $k logs job/{{name}} --tail=60 >&2 || true; exit 1

# Delete the namespace (keeps the cluster); a no-op when the cluster does not exist yet (fresh machine)
down:
    if kind get clusters 2>/dev/null | grep -qx {{cluster}}; then kubectl --context {{ctx}} delete namespace {{ns}} --ignore-not-found --wait; else echo "no kind cluster '{{cluster}}' yet: nothing to delete"; fi

# Delete the kind cluster
clean:
    kind delete cluster --name {{cluster}}

# Tail logs: just logs worker | connect | kafka | postgres | valkey
logs app:
    {{k}} logs -l app={{app}} --tail=200

# Print events of a CDC topic from the beginning: just topic fleet.public.vehicles
topic name="fleet.public.vehicles":
    {{k}} exec kafka-0 -- /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 \
      --topic {{name}} --from-beginning --property print.key=true --property print.partition=true \
      --property print.offset=true --timeout-ms 5000 2>/dev/null || true

# Open psql on the source database
psql:
    {{k}} exec -it postgres-0 -- psql -U postgres -d fleet

# Connector status
connector:
    {{k}} exec deploy/connect -- curl -s localhost:8083/connectors/fleet/status
