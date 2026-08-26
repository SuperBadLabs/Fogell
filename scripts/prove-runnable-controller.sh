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
  "FOGELL_WORKER_LEASE_SECONDS=15"
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
  -c "GRANT SELECT ON controller_metadata TO $role" \
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

pipeline=$'pipeline {\n  agent any\n  stages {\n    stage(\'Build\') {\n      steps {\n        echo \'hello-controller-before\'\n        sh \'head -c 20000 /dev/zero | base64 -w0\'\n        sh \'sleep 5\'\n        echo \'hello-controller-after\'\n      }\n    }\n  }\n}'

response=$(curl -fsS -X POST -H "$auth" -H 'idempotency-key: fg224-e2e' \
  -H 'content-type: application/x-jenkinsfile' --data-binary "$pipeline" "$builds_url")
build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <<<"$response")
[[ -n "$build_id" ]] || { echo "FG-224 REFUSED: admission returned no build id" >&2; exit 1; }

queued_state=$(admin "$database" -Atc \
  "SELECT a.state FROM attempts a JOIN nodes n ON n.id=a.node_id AND n.organization_id=a.organization_id WHERE n.build_id='$build_id'")
[[ "$queued_state" = queued ]] || { echo "FG-224 REFUSED: deterministic restart fixture was not queued" >&2; exit 1; }

kill -TERM "$host_pid"
wait "$host_pid"
host_pid=""
! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
  || { echo "FG-224 REFUSED: stopped controller still served requests" >&2; exit 1; }

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
large_run=$(head -c 1000 /dev/zero | tr '\0' A)
grep -Fq "$large_run" <<<"$logs" \
  || { echo "FG-224 REFUSED: split newline-free output was not reconstructed through the API" >&2; exit 1; }
grep -Fq 'AAA=' <<<"$logs" \
  || { echo "FG-224 REFUSED: split newline-free output lost its terminal padding" >&2; exit 1; }
IFS='|' read -r large_chunks large_a_count max_log_chunk <<<"$(admin "$database" -Atc \
  "SELECT count(*) FILTER (WHERE length(regexp_replace(body, '[^A]', '', 'g')) >= 1000), COALESCE(sum(length(regexp_replace(body, '[^A]', '', 'g'))), 0), COALESCE(max(octet_length(body)), 0) FROM log_chunks WHERE build_id='$build_id'")"
(( large_chunks >= 2 )) \
  || { echo "FG-224 REFUSED: newline-free output did not cross multiple persisted chunks ($large_chunks)" >&2; exit 1; }
(( large_a_count >= 26660 )) \
  || { echo "FG-224 REFUSED: newline-free output was truncated ($large_a_count base64 A bytes)" >&2; exit 1; }
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

set +e
admin "$database" -c "UPDATE build_definitions SET source_bytes = decode('00','hex') WHERE build_id='$build_id'" >/dev/null 2>&1
mutate_rc=$?
set -e
[[ $mutate_rc -ne 0 ]] || { echo "FG-224 REFUSED: durable definition was mutable" >&2; exit 1; }

if grep -Fq 'fg224-proof-token-0123456789abcdef' "$host_log" || grep -Fq 'Password=fogell' "$host_log"; then
  echo "FG-224 REFUSED: controller log disclosed a configured secret" >&2
  exit 1
fi

echo "FG-224 PROOF PASS: restart-discovered durable admission; exact chunked byte bounds; fixed placement; supervised whole-pipeline execution; progressive bounded fenced logs; atomic terminal roll-up"
