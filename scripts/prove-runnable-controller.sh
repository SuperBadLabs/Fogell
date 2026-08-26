#!/usr/bin/env bash
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
container=${FOGELL_PG_CONTAINER:-fogell-fg060a}
port=${FOGELL_PG_PORT:-55445}
database="fogell_fg224_$$_$(date +%s)"
role="fogell_fg224_runtime_$$_$(date +%s)"
listen_port=${FOGELL_FG224_PORT:-18083}
configuration=${FOGELL_BUILD_CONFIGURATION:-Release}
base_url="http://127.0.0.1:${listen_port}"
scratch=$(mktemp -d /tmp/fogell-fg224-proof.XXXXXX)
state_root="$scratch/state"
token_file="$scratch/token"
weak_token_file="$scratch/weak-token"
host_log="$scratch/controller.log"
host_pid=""

admin() {
  docker exec "$container" psql -U fogell -d "$1" -v ON_ERROR_STOP=1 "${@:2}"
}

cleanup() {
  if [[ -n "$host_pid" ]] && kill -0 "$host_pid" 2>/dev/null; then
    kill -TERM "$host_pid" 2>/dev/null || true
    wait "$host_pid" 2>/dev/null || true
  fi
  docker exec "$container" psql -U fogell -d postgres -v ON_ERROR_STOP=1 \
    -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$database' AND pid <> pg_backend_pid()" \
    -c "DROP DATABASE IF EXISTS $database" >/dev/null 2>&1 || true
  docker exec "$container" psql -U fogell -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP OWNED BY $role" -c "DROP ROLE IF EXISTS $role" >/dev/null 2>&1 || true
  case "$scratch" in
    /tmp/fogell-fg224-proof.*) rm -rf -- "$scratch" ;;
    *) echo "FG-224 REFUSED: unsafe cleanup path" >&2 ;;
  esac
}
trap cleanup EXIT

controller="$repo/src/Fogell.Controller.Host/bin/$configuration/net10.0/Fogell.Controller.Host"
run_host="$repo/tools/Fogell.Run.Host/bin/$configuration/net10.0/Fogell.Run.Host"
[[ -x "$controller" ]] || { echo "FG-224 REFUSED: controller host is not built" >&2; exit 2; }
[[ -x "$run_host" ]] || { echo "FG-224 REFUSED: run host is not built" >&2; exit 2; }

printf '%s' 'fg224-proof-token-0123456789abcdef' >"$token_file"
printf '%s' 'weak' >"$weak_token_file"
chmod 400 "$token_file" "$weak_token_file"

admin postgres -c "CREATE DATABASE $database" >/dev/null
admin postgres -c "CREATE ROLE $role NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS" >/dev/null

maintenance_url="Host=127.0.0.1;Port=$port;Username=fogell;Password=fogell;Database=$database"
runtime_url="$maintenance_url;Options=-c role=$role;No Reset On Close=true;Maximum Pool Size=8"

common_env=(
  "FOGELL_DATABASE_URL=$runtime_url"
  "FOGELL_MAINTENANCE_DATABASE_URL=$maintenance_url"
  "FOGELL_LISTEN_URL=$base_url"
  "FOGELL_STATE_ROOT=$state_root"
  "FOGELL_RUN_HOST_PATH=$run_host"
  "FOGELL_LOCAL_TRUST_POOL=trusted-linux"
  "FOGELL_MAX_PIPELINE_BYTES=1024"
  "FOGELL_MAX_LOG_CHUNKS=100"
  "FOGELL_WORKER_POLL_MS=50"
  "FOGELL_WORKER_LEASE_SECONDS=60"
)

set +e
env "${common_env[@]}" "FOGELL_API_TOKEN_FILE=$weak_token_file" "$controller" \
  >"$scratch/weak.stdout" 2>"$scratch/weak.stderr"
weak_rc=$?
set -e
[[ $weak_rc -eq 2 ]] || { echo "FG-224 REFUSED: weak token did not fail startup before bind" >&2; exit 1; }
! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
  || { echo "FG-224 REFUSED: weak startup bound a socket" >&2; exit 1; }

set +e
env "${common_env[@]}" "FOGELL_API_TOKEN_FILE=$token_file" \
  "FOGELL_WORKER_POLL_MS=3334" "FOGELL_WORKER_LEASE_SECONDS=10" "$controller" \
  >"$scratch/unsafe-timing.stdout" 2>"$scratch/unsafe-timing.stderr"
unsafe_timing_rc=$?
set -e
[[ $unsafe_timing_rc -eq 2 ]] \
  || { echo "FG-224 REFUSED: unsafe worker timing did not fail startup" >&2; exit 1; }
grep -Fq 'FOGELL_WORKER_POLL_MS must be no more than one third of FOGELL_WORKER_LEASE_SECONDS (3333 ms for 10 s)' \
  "$scratch/unsafe-timing.stderr" \
  || { echo "FG-224 REFUSED: unsafe worker timing refusal was not named precisely" >&2; exit 1; }
! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
  || { echo "FG-224 REFUSED: unsafe worker timing startup bound a socket" >&2; exit 1; }

set +e
env "${common_env[@]}" "FOGELL_API_TOKEN_FILE=$token_file" "$controller" \
  >"$scratch/capability.stdout" 2>"$scratch/capability.stderr"
capability_rc=$?
set -e
[[ $capability_rc -eq 3 ]] \
  || { echo "FG-224 REFUSED: incomplete runtime capability did not fail startup" >&2; exit 1; }
grep -Fq 'runtime database capability is incomplete' "$scratch/capability.stderr" \
  || { echo "FG-224 REFUSED: runtime capability refusal was not named" >&2; exit 1; }
! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
  || { echo "FG-224 REFUSED: incomplete runtime capability bound a socket" >&2; exit 1; }

admin "$database" \
  -c "GRANT USAGE ON SCHEMA public TO $role" \
  -c "GRANT SELECT, UPDATE(singleton) ON controller_metadata TO $role" \
  -c "GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes, attempts, events, outbox, log_chunks, effect_checkpoints, retry_decisions, build_definitions TO $role" \
  -c "GRANT SELECT ON organization_work_roots TO $role" \
  -c "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $role" >/dev/null

organization=$(tr -d '-' </proc/sys/kernel/random/uuid)
project=$(tr -d '-' </proc/sys/kernel/random/uuid)
organization="${organization:0:8}-${organization:8:4}-${organization:12:4}-${organization:16:4}-${organization:20:12}"
project="${project:0:8}-${project:8:4}-${project:12:4}-${project:16:4}-${project:20:12}"
admin "$database" \
  -c "INSERT INTO organizations (id, slug) VALUES ('$organization', 'fg224-org')" \
  -c "INSERT INTO projects (id, organization_id, slug) VALUES ('$project', '$organization', 'fg224-project')" >/dev/null

# Hold the first worker scan long enough to admit and stop deterministically.
# The second process uses the production poll setting and must discover the
# queued build from durable state rather than process memory.
env "${common_env[@]}" "FOGELL_API_TOKEN_FILE=$token_file" "FOGELL_WORKER_POLL_MS=10000" \
  "$controller" >"$host_log" 2>&1 &
host_pid=$!

ready=0
for _ in $(seq 1 200); do
  if curl -fsS "$base_url/health/ready" >/dev/null 2>&1; then
    ready=1
    break
  fi
  kill -0 "$host_pid" 2>/dev/null || { echo "FG-224 REFUSED: controller exited during startup" >&2; exit 1; }
  sleep 0.05
done
[[ $ready -eq 1 ]] || { echo "FG-224 REFUSED: readiness never became healthy" >&2; exit 1; }

builds_url="$base_url/api/v1/organizations/$organization/projects/$project/builds"
auth="authorization: Bearer fg224-proof-token-0123456789abcdef"

# No Content-Length: Router's decoded-byte reader is the sole size authority.
# First prove the exact decoded boundary through the production Kestrel host —
# this is the EOF-probe case a transport-level cap previously misclassified.
exact_prefix=$'pipeline {\n  agent any\n  stages {\n    stage(\'Exact\') {\n      steps {\n        echo \'exact-boundary\'\n      }\n    }\n  }\n}'
printf '%s' "$exact_prefix" >"$scratch/chunked-exact"
exact_size=$(wc -c <"$scratch/chunked-exact" | tr -d '[:space:]')
(( exact_size < 1024 )) || { echo "FG-224 REFUSED: exact-limit fixture prefix is not below the limit" >&2; exit 1; }
head -c $((1024 - exact_size)) /dev/zero | tr '\0' ' ' >>"$scratch/chunked-exact"
[[ $(wc -c <"$scratch/chunked-exact" | tr -d '[:space:]') = 1024 ]] \
  || { echo "FG-224 REFUSED: exact-limit fixture is not 1024 bytes" >&2; exit 1; }
exact_code=$(curl --http1.1 -sS -o "$scratch/chunked-exact.json" -w '%{http_code}' -X POST \
  -H "$auth" -H 'idempotency-key: fg224-chunked-exact' \
  -H 'content-type: application/x-jenkinsfile' -H 'transfer-encoding: chunked' \
  --data-binary @"$scratch/chunked-exact" "$builds_url")
[[ "$exact_code" = 201 ]] \
  || { echo "FG-224 REFUSED: exact-limit chunked pipeline returned $exact_code instead of 201" >&2; exit 1; }
exact_build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <"$scratch/chunked-exact.json")
[[ -n "$exact_build_id" ]] || { echo "FG-224 REFUSED: exact-limit admission returned no build id" >&2; exit 1; }
exact_cancel_code=$(curl -sS -o "$scratch/chunked-exact-cancel.json" -w '%{http_code}' -X POST \
  -H "$auth" "$builds_url/$exact_build_id/cancel")
[[ "$exact_cancel_code" = 202 ]] \
  || { echo "FG-224 REFUSED: exact-limit queued build was not cancelled before worker start" >&2; exit 1; }

# The one forbidden decoded byte must return the Router's stable JSON 413,
# independently of chunk framing and segmentation.
head -c 1025 /dev/zero | tr '\0' x >"$scratch/chunked-overflow"
chunked_code=$(curl --http1.1 -sS -o "$scratch/chunked-overflow.json" -w '%{http_code}' -X POST \
  -H "$auth" -H 'idempotency-key: fg224-chunked-overflow' \
  -H 'content-type: application/x-jenkinsfile' -H 'transfer-encoding: chunked' \
  --data-binary @"$scratch/chunked-overflow" "$builds_url")
[[ "$chunked_code" = 413 ]] \
  || { echo "FG-224 REFUSED: chunked overflow returned $chunked_code instead of 413" >&2; exit 1; }
grep -q 'pipeline_too_large' "$scratch/chunked-overflow.json" \
  || { echo "FG-224 REFUSED: chunked overflow lost its stable error code" >&2; exit 1; }

# A parseable pipeline can still be outside Fogell's execution capability.
# Admission must run the exact Run.Host preflight before allocating a build or
# binding the idempotency key, so a retry is a stable refusal and the same key
# remains available for a supported source.
unsupported_pipeline=$'pipeline {\n  agent any\n  tools { maven \'m3\' }\n  stages { stage(\'Unsupported\') { steps { echo \'never-admitted\' } } }\n}'
builds_before_unsupported=$(admin "$database" -Atc \
  "SELECT count(*) FROM builds WHERE organization_id='$organization' AND project_id='$project'")
for attempt in 1 2; do
  unsupported_code=$(curl -sS -o "$scratch/unsupported-$attempt.json" -w '%{http_code}' -X POST \
    -H "$auth" -H 'idempotency-key: fg224-unsupported-retry' \
    -H 'content-type: application/x-jenkinsfile' --data-binary "$unsupported_pipeline" "$builds_url")
  [[ "$unsupported_code" = 422 ]] \
    || { echo "FG-224 REFUSED: unsupported admission attempt $attempt returned $unsupported_code" >&2; exit 1; }
  grep -Fq '"code":"execution_unsupported"' "$scratch/unsupported-$attempt.json" \
    || { echo "FG-224 REFUSED: unsupported admission attempt $attempt lost its stable API code" >&2; exit 1; }
  grep -Fq 'unsupported_tools' "$scratch/unsupported-$attempt.json" \
    || { echo "FG-224 REFUSED: unsupported admission attempt $attempt lost the shared preflight reason" >&2; exit 1; }
  builds_after_unsupported=$(admin "$database" -Atc \
    "SELECT count(*) FROM builds WHERE organization_id='$organization' AND project_id='$project'")
  [[ "$builds_after_unsupported" = "$builds_before_unsupported" ]] \
    || { echo "FG-224 REFUSED: unsupported admission attempt $attempt created durable build state" >&2; exit 1; }
done

supported_after_refusal=$(curl -sS -o "$scratch/supported-after-refusal.json" -w '%{http_code}' -X POST \
  -H "$auth" -H 'idempotency-key: fg224-unsupported-retry' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$exact_prefix" "$builds_url")
[[ "$supported_after_refusal" = 201 ]] \
  || { echo "FG-224 REFUSED: refused idempotency key was not available to supported admission" >&2; exit 1; }
supported_after_refusal_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <"$scratch/supported-after-refusal.json")
[[ -n "$supported_after_refusal_id" ]] \
  || { echo "FG-224 REFUSED: supported admission after refusal returned no build id" >&2; exit 1; }
supported_replay_code=$(curl -sS -o "$scratch/supported-after-refusal-replay.json" -w '%{http_code}' -X POST \
  -H "$auth" -H 'idempotency-key: fg224-unsupported-retry' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$exact_prefix" "$builds_url")
[[ "$supported_replay_code" = 200 ]] \
  || { echo "FG-224 REFUSED: supported admission after refusal did not replay idempotently" >&2; exit 1; }
supported_cancel_code=$(curl -sS -o "$scratch/supported-after-refusal-cancel.json" -w '%{http_code}' -X POST \
  -H "$auth" "$builds_url/$supported_after_refusal_id/cancel")
[[ "$supported_cancel_code" = 202 ]] \
  || { echo "FG-224 REFUSED: supported admission after refusal was not cancelled before worker start" >&2; exit 1; }

poison_pipeline=$'pipeline {\n  agent any\n  stages {\n    stage(\'Poison\') {\n      steps {\n        echo \'this-source-must-never-run\'\n      }\n    }\n  }\n}'
poison_response=$(curl -fsS -X POST -H "$auth" -H 'idempotency-key: fg224-materialization-poison' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$poison_pipeline" "$builds_url")
poison_build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <<<"$poison_response")
[[ -n "$poison_build_id" ]] \
  || { echo "FG-224 REFUSED: materialization-poison admission returned no build id" >&2; exit 1; }

pipeline=$'pipeline {\n  agent any\n  stages {\n    stage(\'Build\') {\n      steps {\n        echo \'hello-controller-before\'\n        echo "FG224-METADATA:${env.BUILD_NUMBER}:${env.BUILD_ID}:${env.BUILD_DISPLAY_NAME}"\n        sh \'printf QkVHSU4tMjI0 | base64 -d; head -c 20000 /dev/zero | base64 -w0; printf RU5ELTEtMjI0 | base64 -d\'\n        sh \'sleep 5\'\n        echo \'hello-controller-after\'\n      }\n    }\n  }\n}'

response=$(curl -fsS -X POST -H "$auth" -H 'idempotency-key: fg224-e2e' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$pipeline" "$builds_url")
build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <<<"$response")
[[ -n "$build_id" ]] || { echo "FG-224 REFUSED: admission returned no build id" >&2; exit 1; }
build_number=$(sed -n 's/.*"number":\([0-9][0-9]*\).*/\1/p' <<<"$response")
[[ "$build_number" = 4 ]] \
  || { echo "FG-224 REFUSED: fourth project admission returned build number ${build_number:-missing}" >&2; exit 1; }

queued_state=$(admin "$database" -Atc \
  "SELECT a.state FROM attempts a JOIN nodes n ON n.id=a.node_id AND n.organization_id=a.organization_id WHERE n.build_id='$build_id'")
[[ "$queued_state" = queued ]] || { echo "FG-224 REFUSED: deterministic restart fixture was not queued" >&2; exit 1; }

kill -TERM "$host_pid"
wait "$host_pid"
host_pid=""
! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
  || { echo "FG-224 REFUSED: stopped controller still served requests" >&2; exit 1; }

organization_key=${organization//-/}
poison_build_key=${poison_build_id//-/}
poison_definition="$state_root/definitions/$organization_key/$poison_build_key/Jenkinsfile"
mkdir -p "$(dirname "$poison_definition")"
printf '%s' 'pipeline { agent none }' >"$poison_definition"

env "${common_env[@]}" "FOGELL_API_TOKEN_FILE=$token_file" "$controller" >>"$host_log" 2>&1 &
host_pid=$!

ready=0
for _ in $(seq 1 200); do
  if curl -fsS "$base_url/health/ready" >/dev/null 2>&1; then
    ready=1
    break
  fi
  kill -0 "$host_pid" 2>/dev/null || { echo "FG-224 REFUSED: restarted controller exited" >&2; exit 1; }
  sleep 0.05
done
[[ $ready -eq 1 ]] || { echo "FG-224 REFUSED: restarted controller never became ready" >&2; exit 1; }

# The oldest offered build has a materialized definition that conflicts with
# immutable admission truth. It must become reconciliation_required rather than
# escaping before BeginExecution and returning to queued on lease expiry. The
# later build's progressive and terminal assertions below prove FIFO progress.
poison_terminal=""
for _ in $(seq 1 200); do
  poison_terminal=$(curl -fsS -H "$auth" "$builds_url/$poison_build_id")
  grep -q '"status":"reconciliation_required"' <<<"$poison_terminal" && break
  grep -q '"status":"\(success\|failure\|aborted\)"' <<<"$poison_terminal" && {
    echo "FG-224 REFUSED: materialization poison reached false terminal truth $poison_terminal" >&2
    exit 1
  }
  sleep 0.05
done
grep -q '"status":"reconciliation_required"' <<<"$poison_terminal" \
  || { echo "FG-224 REFUSED: materialization poison was not quarantined" >&2; exit 1; }
poison_attempt=$(admin "$database" -Atc \
  "SELECT a.state || '|' || (a.lease_owner IS NULL)::text || '|' || (a.lease_expires_at IS NULL)::text
     FROM attempts a
     JOIN nodes n ON n.organization_id=a.organization_id AND n.id=a.node_id
    WHERE a.organization_id='$organization' AND n.build_id='$poison_build_id'")
[[ "$poison_attempt" = "reconciliation_required|true|true" ]] \
  || { echo "FG-224 REFUSED: materialization poison retained requeue authority: $poison_attempt" >&2; exit 1; }
poison_logs=$(curl -fsS -H "$auth" "$builds_url/$poison_build_id/logs")
! grep -Fq 'this-source-must-never-run' <<<"$poison_logs" \
  || { echo "FG-224 REFUSED: materialization poison executed its durable source" >&2; exit 1; }
grep -Fq '"from_sequence":0' <<<"$poison_logs" \
  || { echo "FG-224 REFUSED: materialization poison log response lost its initial cursor" >&2; exit 1; }
grep -Fq '"next_sequence":0' <<<"$poison_logs" \
  || { echo "FG-224 REFUSED: materialization poison advanced its log cursor" >&2; exit 1; }
grep -Fq '"chunks":[]' <<<"$poison_logs" \
  || { echo "FG-224 REFUSED: materialization poison published log chunks: $poison_logs" >&2; exit 1; }
poison_chunk_count=$(admin "$database" -Atc \
  "SELECT count(*) FROM log_chunks
    WHERE organization_id='$organization' AND build_id='$poison_build_id'")
[[ "$poison_chunk_count" = 0 ]] \
  || { echo "FG-224 REFUSED: materialization poison persisted $poison_chunk_count log chunks" >&2; exit 1; }

# A progressive log must cross the complete child -> event file -> fenced store
# -> HTTP path before the build reaches terminal state. A terminal replay could
# satisfy a post-build grep but cannot satisfy this assertion.
progressive=0
for _ in $(seq 1 200); do
  live_logs=$(curl -fsS -H "$auth" "$builds_url/$build_id/logs")
  if grep -q 'hello-controller-before' <<<"$live_logs"; then
    live_state=$(curl -fsS -H "$auth" "$builds_url/$build_id")
    grep -q '"status":"running"' <<<"$live_state" \
      || { echo "FG-224 REFUSED: first console line arrived only after terminal state" >&2; exit 1; }
    ! grep -q 'hello-controller-after' <<<"$live_logs" \
      || { echo "FG-224 REFUSED: terminal console was replayed into the progressive snapshot" >&2; exit 1; }
    progressive=1
    break
  fi
  sleep 0.05
done
[[ $progressive -eq 1 ]] || { echo "FG-224 REFUSED: console did not stream before terminal state" >&2; exit 1; }

terminal=""
for _ in $(seq 1 300); do
  terminal=$(curl -fsS -H "$auth" "$builds_url/$build_id")
  grep -q '"status":"success"' <<<"$terminal" && break
  grep -q '"status":"\(failure\|aborted\|reconciliation_required\)"' <<<"$terminal" && {
    echo "FG-224 REFUSED: build reached $terminal" >&2
    exit 1
  }
  sleep 0.05
done
grep -q '"status":"success"' <<<"$terminal" || { echo "FG-224 REFUSED: build did not finish" >&2; exit 1; }

logs=$(curl -fsS -H "$auth" "$builds_url/$build_id/logs")
[[ $(grep -o 'hello-controller-before' <<<"$logs" | wc -l | tr -d '[:space:]') = 1 ]] \
  || { echo "FG-224 REFUSED: first console line was lost or duplicated" >&2; exit 1; }
[[ $(grep -o 'hello-controller-after' <<<"$logs" | wc -l | tr -d '[:space:]') = 1 ]] \
  || { echo "FG-224 REFUSED: final console line was lost or duplicated" >&2; exit 1; }
grep -Fq "FG224-METADATA:$build_number:$build_number:#$build_number" <<<"$logs" \
  || { echo "FG-224 REFUSED: admitted project build number did not reach Jenkins metadata" >&2; exit 1; }
large_run=$(head -c 1000 /dev/zero | tr '\0' A)
grep -Fq "$large_run" <<<"$logs" \
  || { echo "FG-224 REFUSED: split newline-free output was not reconstructed through the API" >&2; exit 1; }
[[ $(grep -o 'BEGIN-224' <<<"$logs" | wc -l | tr -d '[:space:]') = 1 ]] \
  || { echo "FG-224 REFUSED: split newline-free output lost or duplicated its head" >&2; exit 1; }
[[ $(grep -o 'END-1-224' <<<"$logs" | wc -l | tr -d '[:space:]') = 1 ]] \
  || { echo "FG-224 REFUSED: split newline-free output lost or duplicated its tail" >&2; exit 1; }
case "$logs" in
  *BEGIN-224*END-1-224*) ;;
  *) echo "FG-224 REFUSED: split newline-free output reordered its unique boundaries" >&2; exit 1 ;;
esac
IFS='|' read -r large_chunks large_a_count max_log_chunk <<<"$(admin "$database" -Atc \
  "WITH bounds AS (
     SELECT min(sequence) FILTER (WHERE body='step-started: Build#2 sh') AS started,
            min(sequence) FILTER (WHERE body='step-finished: Build#2 success') AS finished
     FROM log_chunks WHERE build_id='$build_id'
   ), frame AS (
     SELECT body FROM log_chunks, bounds
     WHERE build_id='$build_id' AND sequence > started AND sequence < finished
   )
   SELECT count(*) FILTER (WHERE length(regexp_replace(body, '[^A]', '', 'g')) >= 1000),
          COALESCE(sum(length(regexp_replace(body, '[^A]', '', 'g'))), 0),
          COALESCE(max(octet_length(body)), 0)
   FROM frame")"
(( large_chunks >= 2 )) \
  || { echo "FG-224 REFUSED: newline-free output did not cross multiple persisted chunks ($large_chunks)" >&2; exit 1; }
(( large_a_count == 26667 )) \
  || { echo "FG-224 REFUSED: newline-free output was lost or duplicated ($large_a_count of 26667 data bytes)" >&2; exit 1; }
(( max_log_chunk <= 49152 )) \
  || { echo "FG-224 REFUSED: producer frame exceeded its decoded-byte contract ($max_log_chunk)" >&2; exit 1; }

replay_code=$(curl -sS -o "$scratch/replay.json" -w '%{http_code}' -X POST -H "$auth" \
  -H 'idempotency-key: fg224-e2e' -H 'content-type: application/x-jenkinsfile' \
  --data-binary "$pipeline" "$builds_url")
[[ "$replay_code" = 200 ]] || { echo "FG-224 REFUSED: exact replay was not idempotent" >&2; exit 1; }

conflict_code=$(curl -sS -o "$scratch/conflict.json" -w '%{http_code}' -X POST -H "$auth" \
  -H 'idempotency-key: fg224-e2e' -H 'content-type: application/x-jenkinsfile' \
  --data-binary "${pipeline/hello-controller/substituted}" "$builds_url")
[[ "$conflict_code" = 409 ]] || { echo "FG-224 REFUSED: source substitution did not conflict" >&2; exit 1; }

placement_code=$(curl -sS -o "$scratch/placement.json" -w '%{http_code}' -X POST -H "$auth" \
  -H 'idempotency-key: fg224-placement' -H 'fogell-trust-pool: privileged' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$pipeline" "$builds_url")
[[ "$placement_code" = 400 ]] || { echo "FG-224 REFUSED: caller selected a trust pool" >&2; exit 1; }

cancel_code=$(curl -sS -o "$scratch/cancel.json" -w '%{http_code}' -X POST -H "$auth" "$builds_url/$build_id/cancel")
[[ "$cancel_code" = 409 ]] || { echo "FG-224 REFUSED: terminal cancellation did not conflict" >&2; exit 1; }

facts=$(admin "$database" -Atc \
  "SELECT (SELECT count(*) FROM builds WHERE id='$build_id'),
          (SELECT count(*) FROM build_definitions WHERE build_id='$build_id'),
          (SELECT count(*) FROM nodes WHERE build_id='$build_id'),
          (SELECT count(*) FROM events WHERE build_id='$build_id' AND kind='attempt.terminal'),
          (SELECT count(*) FROM outbox WHERE topic='build.terminal' AND body->>'build'='$build_id')")
[[ "$facts" = "1|1|1|1|1" ]] || { echo "FG-224 REFUSED: durable vertical slice counts were $facts" >&2; exit 1; }

# A retry is a new execution attempt over the same build and source. Its
# journal must therefore be keyed by attempt identity: a build-keyed journal
# would make the child replay the failed parent's terminal record without ever
# entering the shell. The source leaves one workspace marker so its second
# execution (the retry child) is observable and successful.
retry_pipeline=$'pipeline {\n  agent any\n  stages {\n    stage(\'RetryJournal\') {\n      steps {\n        sh \'if [ -f "$HOME/.fg224-allow-child" ]; then echo FG224-RETRY-CHILD-EXECUTED; else exit 7; fi\'\n        echo \'FG224-RETRY-CHILD-COMPLETED\'\n      }\n    }\n  }\n}'
retry_response=$(curl -fsS -X POST -H "$auth" -H 'idempotency-key: fg224-attempt-journal' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$retry_pipeline" "$builds_url")
retry_build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <<<"$retry_response")
retry_parent_attempt=$(sed -n 's/.*"attempt_id":"\([^"]*\)".*/\1/p' <<<"$retry_response")
[[ -n "$retry_build_id" && -n "$retry_parent_attempt" ]] \
  || { echo "FG-224 REFUSED: retry journal fixture admission lost build or attempt identity" >&2; exit 1; }
retry_build_key=${retry_build_id//-/}

retry_parent_terminal=""
for _ in $(seq 1 300); do
  retry_parent_terminal=$(admin "$database" -Atc \
    "SELECT state || '|' || COALESCE(result, '') FROM attempts WHERE organization_id='$organization' AND id='$retry_parent_attempt'")
  [[ "$retry_parent_terminal" = "terminal|failure" ]] && break
  [[ "$retry_parent_terminal" = reconciliation_required* ]] && {
    echo "FG-224 REFUSED: retry parent required reconciliation: $retry_parent_terminal" >&2
    exit 1
  }
  sleep 0.05
done
[[ "$retry_parent_terminal" = "terminal|failure" ]] \
  || { echo "FG-224 REFUSED: retry parent did not fail durably: $retry_parent_terminal" >&2; exit 1; }

# Parent and retry share the restart-stable build HOME. Make the already-
# admitted source's child branch eligible only after the parent's failure is
# durable, so the child execution cannot be a replayed parent result. The
# worker-created private HOME already exists; refusing an empty match keeps the
# proof honest if its derivation ever changes.
retry_home_root="$state_root/workspaces/$organization_key/_agent_home"
retry_home_count=0
for retry_home in "$retry_home_root"/*; do
  [[ -d "$retry_home" ]] || continue
  touch "$retry_home/.fg224-allow-child"
  retry_home_count=$((retry_home_count + 1))
done
(( retry_home_count > 0 )) \
  || { echo "FG-224 REFUSED: retry parent created no private build HOME" >&2; exit 1; }

retry_child_attempt=$(tr -d '-' </proc/sys/kernel/random/uuid)
retry_child_attempt="${retry_child_attempt:0:8}-${retry_child_attempt:8:4}-${retry_child_attempt:12:4}-${retry_child_attempt:16:4}-${retry_child_attempt:20:12}"
admin "$database" -c "BEGIN;
  INSERT INTO attempts
      (id, organization_id, node_id, ordinal, retry_of, state, fence,
       restore_epoch, lease_owner, lease_expires_at, result)
    SELECT '$retry_child_attempt', organization_id, node_id, ordinal + 1, id,
           'queued', 0, restore_epoch, NULL, NULL, NULL
      FROM attempts
     WHERE organization_id='$organization' AND id='$retry_parent_attempt';
  INSERT INTO retry_decisions
      (organization_id, parent_attempt_id, parent_node_id, parent_ordinal,
       parent_retry_of, parent_restore_epoch, attempt_limit, outcome,
       child_attempt_id, dead_letter_reason)
    SELECT organization_id, id, node_id, ordinal, retry_of, restore_epoch,
           2, 'child_created', '$retry_child_attempt', NULL
      FROM attempts
     WHERE organization_id='$organization' AND id='$retry_parent_attempt';
  COMMIT;" >/dev/null

retry_child_terminal=""
for _ in $(seq 1 300); do
  retry_child_terminal=$(admin "$database" -Atc \
    "SELECT state || '|' || COALESCE(result, '') FROM attempts WHERE organization_id='$organization' AND id='$retry_child_attempt'")
  [[ "$retry_child_terminal" = "terminal|success" ]] && break
  [[ "$retry_child_terminal" = reconciliation_required* ]] && {
    echo "FG-224 REFUSED: retry child required reconciliation: $retry_child_terminal" >&2
    exit 1
  }
  sleep 0.05
done
[[ "$retry_child_terminal" = "terminal|success" ]] \
  || { echo "FG-224 REFUSED: retry child did not execute successfully: $retry_child_terminal" >&2; exit 1; }

retry_logs=$(curl -fsS -H "$auth" "$builds_url/$retry_build_id/logs")
retry_child_marker_count=$(admin "$database" -Atc \
  "SELECT count(*) FROM log_chunks
    WHERE organization_id='$organization' AND build_id='$retry_build_id'
      AND body='FG224-RETRY-CHILD-EXECUTED'")
[[ "$retry_child_marker_count" = 1 ]] \
  || { echo "FG-224 REFUSED: retry child shell did not execute exactly once" >&2; exit 1; }
grep -Fq 'FG224-RETRY-CHILD-COMPLETED' <<<"$retry_logs" \
  || { echo "FG-224 REFUSED: retry child did not reach its continuation" >&2; exit 1; }

retry_parent_key=${retry_parent_attempt//-/}
retry_child_key=${retry_child_attempt//-/}
retry_journal_root="$state_root/journals/$organization_key/attempts"
retry_parent_journal="$retry_journal_root/$retry_parent_key.journal"
retry_child_journal="$retry_journal_root/$retry_child_key.journal"
retry_legacy_journal="$state_root/journals/$organization_key/$retry_build_key.journal"
[[ -f "$retry_parent_journal" && -f "$retry_child_journal" ]] \
  || { echo "FG-224 REFUSED: parent and child did not receive separate attempt journals" >&2; exit 1; }
[[ "$retry_parent_journal" != "$retry_child_journal" && ! -e "$retry_legacy_journal" ]] \
  || { echo "FG-224 REFUSED: retry journal identity collided with parent or legacy build path" >&2; exit 1; }
grep -qx $'build-finished\tfailure' "$retry_parent_journal" \
  || { echo "FG-224 REFUSED: parent journal lost its failure terminal" >&2; exit 1; }
grep -qx $'build-finished\tsuccess' "$retry_child_journal" \
  || { echo "FG-224 REFUSED: child journal lost its success terminal" >&2; exit 1; }

# Restarting the exact child against its deterministic path must resume that
# journal as a terminal no-op, not execute the shell again or append bytes.
retry_child_sum_before=$(sha256sum "$retry_child_journal" | cut -d' ' -f1)
"$run_host" \
  "$state_root/definitions/$organization_key/$retry_build_key/Jenkinsfile" \
  "$state_root/workspaces/$organization_key" "$retry_build_key" "$retry_child_journal" \
  >"$scratch/retry-child-restart.log" 2>&1
retry_child_sum_after=$(sha256sum "$retry_child_journal" | cut -d' ' -f1)
[[ "$retry_child_sum_after" = "$retry_child_sum_before" ]] \
  || { echo "FG-224 REFUSED: same-child restart mutated its terminal journal" >&2; exit 1; }
grep -Fq 'already-terminal: success' "$scratch/retry-child-restart.log" \
  || { echo "FG-224 REFUSED: same-child restart did not resume its own terminal journal" >&2; exit 1; }
! grep -Fq 'FG224-RETRY-CHILD-EXECUTED' "$scratch/retry-child-restart.log" \
  || { echo "FG-224 REFUSED: same-child restart re-executed the shell" >&2; exit 1; }

# Force a finite post-exit event tail beyond the former 16 MiB aggregate drain
# ceiling. With a 10-second poll and the guarded sub-80-second runtime, bounded
# running slices can consume less than 3 MiB of the >24 MiB encoded event file;
# terminal success therefore requires draining a post-exit remainder above the
# old ceiling. Uniform payload cardinality and unique sentinels detect either
# loss or replay across every bounded slice.
kill -TERM "$host_pid"
wait "$host_pid"
host_pid=""

env "${common_env[@]}" "FOGELL_API_TOKEN_FILE=$token_file" "FOGELL_WORKER_POLL_MS=10000" \
  "$controller" >>"$host_log" 2>&1 &
host_pid=$!

ready=0
for _ in $(seq 1 200); do
  if curl -fsS "$base_url/health/ready" >/dev/null 2>&1; then
    ready=1
    break
  fi
  kill -0 "$host_pid" 2>/dev/null || { echo "FG-224 REFUSED: tail-proof controller exited" >&2; exit 1; }
  sleep 0.05
done
[[ $ready -eq 1 ]] || { echo "FG-224 REFUSED: tail-proof controller never became ready" >&2; exit 1; }

tail_pipeline=$'pipeline {\n  agent any\n  stages {\n    stage(\'Tail\') {\n      steps {\n        sh \'printf RFJBSU4tQkVHSU4tMjI0 | base64 -d; head -c 18000000 /dev/zero | tr "\\\\0" "\\\\132"; printf RFJBSU4tRU5ELTIyNA== | base64 -d\'\n      }\n    }\n  }\n}'
tail_response=$(curl -fsS -X POST -H "$auth" -H 'idempotency-key: fg224-post-exit-tail' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$tail_pipeline" "$builds_url")
tail_build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <<<"$tail_response")
[[ -n "$tail_build_id" ]] || { echo "FG-224 REFUSED: tail proof admission returned no build id" >&2; exit 1; }

tail_started=$(date +%s)
tail_terminal=""
for _ in $(seq 1 800); do
  tail_terminal=$(curl -fsS -H "$auth" "$builds_url/$tail_build_id")
  grep -q '"status":"success"' <<<"$tail_terminal" && break
  grep -q '"status":"\(failure\|aborted\|reconciliation_required\)"' <<<"$tail_terminal" && {
    echo "FG-224 REFUSED: tail proof reached $tail_terminal" >&2
    exit 1
  }
  sleep 0.1
done
grep -q '"status":"success"' <<<"$tail_terminal" \
  || { echo "FG-224 REFUSED: tail proof did not finish" >&2; exit 1; }
tail_elapsed=$(( $(date +%s) - tail_started ))
(( tail_elapsed < 80 )) \
  || { echo "FG-224 REFUSED: tail proof timing no longer establishes a >16 MiB post-exit remainder" >&2; exit 1; }

IFS='|' read -r tail_begin tail_end tail_z tail_chunks <<<"$(admin "$database" -Atc \
  "WITH bounds AS (
     SELECT min(sequence) FILTER (WHERE body LIKE 'step-started: Tail% sh') AS started,
            min(sequence) FILTER (WHERE body LIKE 'step-finished: Tail% success') AS finished
     FROM log_chunks WHERE build_id='$tail_build_id'
   ), frame AS (
     SELECT body FROM log_chunks, bounds
     WHERE build_id='$tail_build_id' AND sequence > started AND sequence < finished
   )
   SELECT count(*) FILTER (WHERE body LIKE '%DRAIN-BEGIN-224%'),
          count(*) FILTER (WHERE body LIKE '%DRAIN-END-224%'),
          COALESCE(sum(length(body) - length(replace(body, 'Z', ''))), 0),
          count(*)
   FROM frame")"
[[ "$tail_begin" = 1 && "$tail_end" = 1 ]] \
  || { echo "FG-224 REFUSED: tail sentinels were lost or replayed ($tail_begin|$tail_end)" >&2; exit 1; }
[[ "$tail_z" = 18000000 ]] \
  || { echo "FG-224 REFUSED: post-exit tail payload was lost or replayed ($tail_z of 18000000 bytes)" >&2; exit 1; }
(( tail_chunks > 256 )) \
  || { echo "FG-224 REFUSED: tail proof did not cross enough bounded event frames ($tail_chunks)" >&2; exit 1; }

# Terminal publication owns event-file cleanup. The successful tail attempt
# must leave no fence file behind.
IFS='|' read -r tail_attempt tail_fence <<<"$(admin "$database" -Atc \
  "SELECT a.id, a.fence
     FROM attempts a JOIN nodes n
       ON n.organization_id=a.organization_id AND n.id=a.node_id
    WHERE n.build_id='$tail_build_id'")"
tail_event_file="$state_root/events/$organization_key/${tail_attempt//-/}-$tail_fence.events"
[[ ! -e "$tail_event_file" ]] \
  || { echo "FG-224 REFUSED: terminal publication retained its event file" >&2; exit 1; }

# A graceful controller stop is not terminal truth. Start a build that has
# already emitted a frame, stop the controller, and bind all three recovery
# records: reason event, outbox, and byte-exact retained fence file.
kill -TERM "$host_pid"
wait "$host_pid"
host_pid=""

env "${common_env[@]}" "FOGELL_API_TOKEN_FILE=$token_file" "FOGELL_WORKER_POLL_MS=50" \
  "$controller" >>"$host_log" 2>&1 &
host_pid=$!

ready=0
for _ in $(seq 1 200); do
  if curl -fsS "$base_url/health/ready" >/dev/null 2>&1; then
    ready=1
    break
  fi
  kill -0 "$host_pid" 2>/dev/null || { echo "FG-224 REFUSED: shutdown-proof controller exited" >&2; exit 1; }
  sleep 0.05
done
[[ $ready -eq 1 ]] || { echo "FG-224 REFUSED: shutdown-proof controller never became ready" >&2; exit 1; }

shutdown_pipeline=$'pipeline {\n  agent any\n  stages {\n    stage(\'Shutdown\') {\n      steps {\n        sh \'echo FG224-SHUTDOWN-FRAME; sleep 30\'\n      }\n    }\n  }\n}'
shutdown_response=$(curl -fsS -X POST -H "$auth" -H 'idempotency-key: fg224-shutdown-recovery' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$shutdown_pipeline" "$builds_url")
shutdown_build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <<<"$shutdown_response")
[[ -n "$shutdown_build_id" ]] || { echo "FG-224 REFUSED: shutdown proof admission returned no build id" >&2; exit 1; }

shutdown_attempt=""
shutdown_fence=""
shutdown_event_file=""
# The first frame is step-started, before the shell has necessarily emitted
# any output. Hash only after the exact final pre-sleep frame is durable, so
# subsequent equality tests reconciliation retention rather than a live writer.
shutdown_quiescent_frame=$(printf '%s' '+ sleep 30' | base64 -w0)
for _ in $(seq 1 400); do
  IFS='|' read -r shutdown_attempt shutdown_fence shutdown_state <<<"$(admin "$database" -Atc \
    "SELECT a.id, a.fence, a.state
       FROM attempts a JOIN nodes n
         ON n.organization_id=a.organization_id AND n.id=a.node_id
      WHERE n.build_id='$shutdown_build_id'")"
  if [[ "$shutdown_state" = running ]]; then
    shutdown_event_file="$state_root/events/$organization_key/${shutdown_attempt//-/}-$shutdown_fence.events"
    [[ -s "$shutdown_event_file" ]] \
      && grep -Fxq -- "$shutdown_quiescent_frame" "$shutdown_event_file" \
      && break
  fi
  sleep 0.05
done
[[ -n "$shutdown_event_file" && -s "$shutdown_event_file" ]] \
  && grep -Fxq -- "$shutdown_quiescent_frame" "$shutdown_event_file" \
  || { echo "FG-224 REFUSED: shutdown proof never reached its running pre-sleep event frame" >&2; exit 1; }
shutdown_sum_before=$(sha256sum "$shutdown_event_file" | cut -d' ' -f1)

kill -TERM "$host_pid"
wait "$host_pid"
host_pid=""

shutdown_truth=$(admin "$database" -Atc \
  "SELECT a.state,
          count(DISTINCT e.id) FILTER (WHERE e.payload->>'reason'='controller_shutdown'),
          count(DISTINCT o.id) FILTER (WHERE o.topic='build.reconciliation_required'
                                        AND o.body->>'attempt'=a.id::text
                                        AND o.body->>'reason'='controller_shutdown'),
          (a.lease_owner IS NULL AND a.lease_expires_at IS NULL)
     FROM attempts a
     JOIN nodes n ON n.organization_id=a.organization_id AND n.id=a.node_id
     LEFT JOIN events e ON e.organization_id=a.organization_id
                       AND e.attempt_id=a.id
                       AND e.kind='attempt.reconciliation_required'
     LEFT JOIN outbox o ON o.organization_id=a.organization_id
    WHERE n.build_id='$shutdown_build_id'
    GROUP BY a.state, a.id, a.lease_owner, a.lease_expires_at")
[[ "$shutdown_truth" = "reconciliation_required|1|1|t" ]] \
  || { echo "FG-224 REFUSED: shutdown recovery truth was $shutdown_truth" >&2; exit 1; }
[[ -s "$shutdown_event_file" ]] \
  || { echo "FG-224 REFUSED: reconciliation deleted the recovery event file" >&2; exit 1; }
shutdown_sum_after=$(sha256sum "$shutdown_event_file" | cut -d' ' -f1)
[[ "$shutdown_sum_after" = "$shutdown_sum_before" ]] \
  || { echo "FG-224 REFUSED: reconciliation changed the retained recovery event bytes" >&2; exit 1; }
sleep 0.2
shutdown_sum_stable=$(sha256sum "$shutdown_event_file" | cut -d' ' -f1)
[[ "$shutdown_sum_stable" = "$shutdown_sum_after" ]] \
  || { echo "FG-224 REFUSED: retained event bytes changed after producer extinction" >&2; exit 1; }

set +e
admin "$database" -c "UPDATE build_definitions SET source_bytes = decode('00','hex') WHERE build_id='$build_id'" >/dev/null 2>&1
mutate_rc=$?
set -e
[[ $mutate_rc -ne 0 ]] || { echo "FG-224 REFUSED: durable definition was mutable" >&2; exit 1; }

if grep -Fq 'fg224-proof-token-0123456789abcdef' "$host_log" || grep -Fq 'Password=fogell' "$host_log"; then
  echo "FG-224 REFUSED: controller log disclosed a configured secret" >&2
  exit 1
fi

echo "FG-224 PROOF PASS: safe timing refusal; exact execution-preflight admission and idempotency boundary; restart-discovered durable admission; poisoned-definition quarantine with FIFO progress; exact chunked byte bounds; build-number metadata; supervised execution; attempt-keyed retry journals with same-child deterministic resume; progressive bounded fenced logs; >16 MiB finite post-exit tail drained exactly once; terminal event-file cleanup; graceful-shutdown reason event, outbox, and byte-exact recovery file; atomic terminal roll-up"
