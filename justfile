set shell := ["bash", "-euo", "pipefail", "-c"]

cluster := "vehicle-cache"
ns := "vehicle-cache"
ctx := "kind-vehicle-cache"
k := "kubectl --context kind-vehicle-cache -n vehicle-cache"

# List recipes
default:
    @just --list

# Bring the whole environment up (idempotent)
up: cluster config
    devspace deploy --kube-context {{ctx}} --namespace {{ns}}
    {{k}} rollout status statefulset/postgres --timeout=300s
    {{k}} rollout status statefulset/kafka --timeout=300s
    {{k}} rollout status deployment/valkey --timeout=300s
    {{k}} rollout status deployment/connect --timeout=300s
    just register-connector
    {{k}} rollout status deployment/worker --timeout=180s

# Create the kind cluster if it does not exist
cluster:
    kind get clusters 2>/dev/null | grep -qx {{cluster}} || kind create cluster --name {{cluster}} --wait 180s
    kubectl --context {{ctx}} create namespace {{ns}} --dry-run=client -o yaml | kubectl --context {{ctx}} apply -f -

# Render config files from infra/ into ConfigMaps
config:
    {{k}} create configmap postgres-init --from-file=infra/postgres/ --dry-run=client -o yaml | {{k}} apply -f -
    {{k}} create configmap debezium-connector --from-file=connector.json=infra/debezium/connector.json --dry-run=client -o yaml | {{k}} apply -f -

# Register (or update) the Debezium connector and wait until it is RUNNING
register-connector:
    {{k}} delete job register-connector --ignore-not-found
    {{k}} apply -f k8s/jobs/register-connector.yaml
    {{k}} wait --for=condition=complete job/register-connector --timeout=180s || ({{k}} logs job/register-connector; exit 1)
    {{k}} logs job/register-connector | tail -1

# Pre-snapshot data set (no deletes) — run before register-connector in e2e
seed: (job "writers-seed")

# Streaming hard cases + seeded burst (scenarios/e2e.json, ~150 statements/s)
scenario: (job "writers-scenario")

# Run a one-shot Job from k8s/jobs/<name>.yaml; fails fast and prints its logs on failure
job name:
    #!/usr/bin/env bash
    set -euo pipefail
    k="{{k}}"
    $k delete job {{name}} --ignore-not-found --wait >/dev/null
    $k apply -f k8s/jobs/{{name}}.yaml >/dev/null
    for _ in $(seq 1 300); do
      succeeded=$($k get job {{name}} -o jsonpath='{.status.succeeded}')
      failed=$($k get job {{name}} -o jsonpath='{.status.failed}')
      if [ "${succeeded:-0}" -ge 1 ]; then $k logs job/{{name}} | tail -1; exit 0; fi
      if [ "${failed:-0}" -ge 1 ]; then echo "job {{name}} failed:"; $k logs job/{{name}} --tail=50 || true; exit 1; fi
      sleep 1
    done
    echo "job {{name}} timed out"; $k logs job/{{name}} --tail=50 || true; exit 1

# Delete the namespace (keeps the cluster)
down:
    kubectl --context {{ctx}} delete namespace {{ns}} --ignore-not-found --wait

# Delete the kind cluster
clean:
    kind delete cluster --name {{cluster}}

# Tail logs: just logs connect | kafka | postgres | valkey
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
