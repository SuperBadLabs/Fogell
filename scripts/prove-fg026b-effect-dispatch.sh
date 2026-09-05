#!/usr/bin/env bash
# FG-026b. Process-kill proof of the effect ledger's runtime adoption.
#
# For each of the four ledger windows (prepare, invoke, apply, confirm) the
# REAL Controller.Host apphost is started on a fresh database with the
# file-drop receipt simulator enabled and FOGELL_EFFECT_KILL_AT armed at that
# window, a trivial pipeline is submitted through the API, and the controller
# is required to die by its own SIGKILL (status 137) inside the window with no
# survivor. The ledger row and the destination must then hold exactly that
# window's state and receipt count. The controller is relaunched WITHOUT the
# kill hook and, with no manual Store call anywhere in this proof, the
# production triggers must classify the stranded prepared/applied work as
# tenant-scoped uncertain (the startup pass for the prepare and invoke
# windows, whose lease is allowed to expire before the relaunch; the periodic
# lease-expiry pass for the apply window, relaunched under a live lease),
# publish exactly one effect.uncertain event and outbox row, list it on
# GET .../effects/uncertain, move the attempt and build to
# reconciliation_required, and never touch the destination again across two
# further lease periods. A confirmed row (the confirm window) must survive
# lease loss confirmed and unlisted.
#
# The judges are shown to fail first, on planted false-green inputs: a
# controller that exits 0 instead of dying, a destination holding two
# receipts, a surface missing its event, and a listing from a different
# organization.
#
# Every wait is bounded and named (FG-231); the helpers are the ones
# scripts/prove-runnable-controller.sh uses.
set -Eeuo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
container=${FOGELL_PG_CONTAINER:-}
port=${FOGELL_PG_PORT:-}
runtime=${FOGELL_CONTAINER_RUNTIME:-podman}
[[ "$runtime" = podman || "$runtime" = docker ]] \
  || { echo "FG-026b REFUSED: FOGELL_CONTAINER_RUNTIME must be exactly podman or docker" >&2; exit 2; }
command -v "$runtime" >/dev/null \
  || { echo "FG-026b REFUSED: $runtime is required for the scratch database" >&2; exit 2; }
command -v timeout >/dev/null \
  || { echo "FG-026b REFUSED: coreutils timeout is required to bound this proof" >&2; exit 2; }
command -v curl >/dev/null || { echo "FG-026b REFUSED: curl is required" >&2; exit 2; }
command -v rg >/dev/null || { echo "FG-026b REFUSED: rg (ripgrep) is required" >&2; exit 2; }
[[ -n "$container" && "$container" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] \
  || { echo "FG-026b REFUSED: FOGELL_PG_CONTAINER must name the PostgreSQL container" >&2; exit 2; }
[[ -n "$port" && "$port" =~ ^[0-9]{1,5}$ ]] \
  || { echo "FG-026b REFUSED: FOGELL_PG_PORT must be set to the PostgreSQL host port" >&2; exit 2; }
port_number=$((10#$port))
(( port_number >= 1 && port_number <= 65535 )) \
  || { echo "FG-026b REFUSED: FOGELL_PG_PORT must be set to the PostgreSQL host port" >&2; exit 2; }

listen_port=${FOGELL_FG026B_PORT:-18086}
configuration=${FOGELL_BUILD_CONFIGURATION:-Release}
base_url="http://127.0.0.1:${listen_port}"
lease_seconds=10
scratch=$(mktemp -d /tmp/fogell-fg026b-proof.XXXXXX)
host_pid=""
database=""
role=""
host_log=""

http_max_time=10
runtime_budget=30
process_budget=30
reap_budget_ms=15000

now_ms() {
  local t=${EPOCHREALTIME/,/.}
  printf '%s\n' "$(( ${t%.*} * 1000 + 10#${t#*.} / 1000 ))"
}
deadline_after() { printf '%s\n' "$(( $(now_ms) + $1 ))"; }
before_deadline() { (( $(now_ms) < $1 )); }
bounded() { timeout -k 5 "$1" "${@:2}"; }
budget_expired() { (( $1 == 124 || $1 == 137 )); }

# Reap a background child within a budget; 124 names a hang, any other value
# is the child's own exit status (137 is the SIGKILL this proof expects).
wait_bounded() {
  local pid="$1" budget_ms="$2" label="$3" deadline
  deadline=$(deadline_after "$budget_ms")
  while kill -0 "$pid" 2>/dev/null; do
    if ! before_deadline "$deadline"; then
      kill -KILL "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
      echo "FG-026b REFUSED: $label (pid $pid) did not exit within ${budget_ms} ms and was killed" >&2
      return 124
    fi
    sleep 0.05
  done
  wait "$pid" 2>/dev/null
}

exec {diagnostic_fd}>&2
on_err() {
  local rc="$1" line="$2" command="$3" note="" i
  [[ $- == *e* ]] || return 0
  (( BASH_SUBSHELL == 0 )) || return 0
  budget_expired "$rc" && note=" (budget expired)"
  for (( i = 1; i < ${#FUNCNAME[@]} - 1; i++ )); do
    note+=" in ${FUNCNAME[i]} called from line ${BASH_LINENO[i]}"
  done
  echo "FG-026b REFUSED: line $line: \`$command\` exited $rc$note" >&"$diagnostic_fd"
  exit 1
}
trap 'on_err $? $LINENO "$BASH_COMMAND"' ERR

admin() {
  bounded "$runtime_budget" "$runtime" exec "$container" psql -U fogell -d "$1" -v ON_ERROR_STOP=1 "${@:2}"
}

release_controller() {
  [[ -n "$host_pid" ]] || return 0
  kill -TERM "$host_pid" 2>/dev/null || true
  wait_bounded "$host_pid" "$reap_budget_ms" "native controller" >/dev/null 2>&1 || true
  host_pid=""
}

drop_database() {
  [[ -n "$database" ]] || return 0
  bounded "$runtime_budget" "$runtime" exec "$container" psql -U fogell -d postgres -v ON_ERROR_STOP=1 \
    -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$database' AND pid <> pg_backend_pid()" \
    -c "DROP DATABASE IF EXISTS $database" >/dev/null 2>&1 || true
  bounded "$runtime_budget" "$runtime" exec "$container" psql -U fogell -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP OWNED BY $role" -c "DROP ROLE IF EXISTS $role" >/dev/null 2>&1 || true
  database=""
  role=""
}

cleanup() {
  release_controller
  drop_database
  if [[ ${FOGELL_KEEP_FG026B_PROOF:-0} = 1 ]]; then
    echo "FG-026b proof scratch retained: $scratch" >&2
  else
    case "$scratch" in
      /tmp/fogell-fg026b-proof.*) rm -rf -- "$scratch" ;;
      *) echo "FG-026b REFUSED: unsafe cleanup path" >&2 ;;
    esac
  fi
}
trap cleanup EXIT

# ---- the judges, shown to fail first ------------------------------------------
judge_exit_status() {
  [[ "$1" = 137 ]] || { echo "controller exited $1, not SIGKILL 137" >&2; return 1; }
}

judge_receipts() {
  local directory="$1" expected="$2" observed=0
  if [[ -d "$directory" ]]; then
    observed=$(find "$directory" -maxdepth 1 -name '*.receipt' -type f | wc -l | tr -d ' ')
  fi
  [[ "$observed" = "$expected" ]] \
    || { echo "destination holds $observed receipt(s), expected $expected" >&2; return 1; }
}

judge_surface() {
  local events="$1" outbox="$2" expected="$3"
  [[ "$events" = "$expected" && "$outbox" = "$expected" ]] \
    || { echo "surface has $events event(s) and $outbox outbox row(s), expected $expected of each" >&2; return 1; }
}

# The listing must name the organization it was asked for, and must (or must
# not) carry the attempt.
judge_listing() {
  local json="$1" organization="$2" attempt="$3" listed="$4"
  printf '%s' "$json" | rg -q -F "\"organization_id\":\"$organization\"" \
    || { echo "listing does not name organization $organization" >&2; return 1; }
  if [[ "$listed" = 1 ]]; then
    printf '%s' "$json" | rg -q -F "\"attempt_id\":\"$attempt\"" \
      || { echo "listing does not carry attempt $attempt" >&2; return 1; }
  else
    ! printf '%s' "$json" | rg -q -F "\"attempt_id\":\"$attempt\"" \
      || { echo "listing carries attempt $attempt, which is confirmed" >&2; return 1; }
  fi
}

control_dir="$scratch/controls"
mkdir -p "$control_dir/two"
: >"$control_dir/two/a.receipt"
: >"$control_dir/two/b.receipt"
org_a=11111111-1111-1111-1111-111111111111
org_b=22222222-2222-2222-2222-222222222222
attempt_x=33333333-3333-3333-3333-333333333333
listing_a="{\"organization_id\":\"$org_a\",\"effects\":[{\"attempt_id\":\"$attempt_x\",\"effect_key\":\"file-drop-receipt:x\"}]}"
listing_b="{\"organization_id\":\"$org_b\",\"effects\":[{\"attempt_id\":\"$attempt_x\",\"effect_key\":\"file-drop-receipt:x\"}]}"

judge_exit_status 137 2>/dev/null || { echo "FG-026b REFUSED: positive exit-status control was rejected" >&2; exit 1; }
judge_receipts "$control_dir/two" 2 2>/dev/null || { echo "FG-026b REFUSED: positive receipt control was rejected" >&2; exit 1; }
judge_surface 1 1 1 2>/dev/null || { echo "FG-026b REFUSED: positive surface control was rejected" >&2; exit 1; }
judge_listing "$listing_a" "$org_a" "$attempt_x" 1 2>/dev/null || { echo "FG-026b REFUSED: positive listing control was rejected" >&2; exit 1; }

expect_rejected() {
  local name="$1"
  shift
  if "$@" 2>/dev/null; then
    echo "FG-026b REFUSED: judge accepted planted $name" >&2
    exit 1
  fi
  echo "  rejected planted $name"
}
echo "=== FG-026b judges refuse planted false-green inputs ==="
expect_rejected "stub controller that exits 0 instead of dying" judge_exit_status 0
expect_rejected "controller that exits 1 on its own" judge_exit_status 1
expect_rejected "destination holding two receipts" judge_receipts "$control_dir/two" 1
expect_rejected "surface missing its event row" judge_surface 0 1 1
expect_rejected "surface with a duplicated outbox row" judge_surface 1 2 1
expect_rejected "listing from a different organization" judge_listing "$listing_b" "$org_a" "$attempt_x" 1
expect_rejected "listing carrying a confirmed attempt" judge_listing "$listing_a" "$org_a" "$attempt_x" 0

# ---- the real controller -----------------------------------------------------
mapping_rc=0
mapping=$(bounded "$runtime_budget" "$runtime" port "$container" 5432/tcp) || mapping_rc=$?
(( mapping_rc == 0 )) || { echo "FG-026b REFUSED: $runtime could not report the PostgreSQL port" >&2; exit 2; }
mapping_pattern='^(5432/tcp[[:space:]]+->[[:space:]]+)?127\.0\.0\.1:([0-9]+)$'
[[ "$mapping" =~ $mapping_pattern ]] \
  || { echo "FG-026b REFUSED: PostgreSQL has an unexpected port mapping: $mapping" >&2; exit 2; }
(( $((10#${BASH_REMATCH[2]})) == port_number )) \
  || { echo "FG-026b REFUSED: FOGELL_PG_PORT does not match the selected PostgreSQL container" >&2; exit 2; }

controller="$repo/src/Fogell.Controller.Host/bin/$configuration/net10.0/Fogell.Controller.Host"
run_host="$repo/tools/Fogell.Run.Host/bin/$configuration/net10.0/Fogell.Run.Host"
[[ -x "$controller" ]] || { echo "FG-026b REFUSED: controller host is not built" >&2; exit 2; }
[[ -x "$run_host" ]] || { echo "FG-026b REFUSED: run host is not built" >&2; exit 2; }

token_file="$scratch/token"
printf '%s' 'fg026b-proof-token-0123456789abcdef' >"$token_file"
chmod 400 "$token_file"
auth="authorization: Bearer fg026b-proof-token-0123456789abcdef"
pipeline=$'pipeline {\n  agent any\n  stages {\n    stage(\'Effect\') {\n      steps {\n        echo \'fg026b-effect\'\n      }\n    }\n  }\n}'

# No process launched from a scratch root of this proof may outlive the
# controller. Detection only, anchored to the unique scratch path; nothing is
# killed by pattern.
survivors_of() {
  local marker="$1" pid cmdline text
  for cmdline in /proc/[0-9]*/cmdline; do
    pid=${cmdline#/proc/}
    pid=${pid%/cmdline}
    [[ "$pid" != "$$" ]] || continue
    # A process can vanish between the glob and the read; that is not a survivor.
    [[ -r "$cmdline" ]] || continue
    text=$( { tr '\0' ' ' <"$cmdline"; } 2>/dev/null || true)
    if [[ "$text" == *"$marker"* ]]; then
      printf '%s\n' "$pid"
    fi
  done
}

wait_ready() {
  local label="$1" ready=0 poll_deadline
  poll_deadline=$(deadline_after 15000)
  while before_deadline "$poll_deadline"; do
    if curl --max-time "$http_max_time" -fsS "$base_url/health/ready" >/dev/null 2>&1; then
      ready=1
      break
    fi
    kill -0 "$host_pid" 2>/dev/null \
      || { echo "FG-026b REFUSED: $label exited during startup: $(tail -n 3 "$host_log" | tr '\n' ' ')" >&2; exit 1; }
    sleep 0.05
  done
  [[ $ready -eq 1 ]] || { echo "FG-026b REFUSED: $label never became ready" >&2; exit 1; }
}

# The controller runs under a small shell parent that forwards SIGTERM to it
# and reports the controller's status as its own exit code (137 for SIGKILL).
# Bash would otherwise print a job-control "Killed" line for a signalled
# background job on every reap; the status is what the judge reads, not the
# message. The proof's stop and survivor checks after every relaunch are what
# hold this wrapper to forwarding correctly.
launch_controller() {
  local kill_window="$1"
  local env_args=("${common_env[@]}")
  [[ -z "$kill_window" ]] || env_args+=("FOGELL_EFFECT_KILL_AT=$kill_window")
  bash -c '
    "$@" &
    child=$!
    trap "kill -TERM $child 2>/dev/null" TERM
    wait "$child"
    rc=$?
    while kill -0 "$child" 2>/dev/null; do
      wait "$child"
      rc=$?
    done
    exit "$rc"' fogell-fg026b-controller env "${env_args[@]}" "$controller" >>"$host_log" 2>&1 &
  host_pid=$!
}

ledger_row() {
  admin "$database" -Atc \
    "SELECT state || '|' || COALESCE(uncertain_from, '-') FROM effect_checkpoints
      WHERE organization_id = '$organization' AND attempt_id = '$1'"
}

surface_counts() {
  admin "$database" -Atc \
    "SELECT (SELECT count(*) FROM events WHERE organization_id = '$organization' AND attempt_id = '$1' AND kind = 'effect.uncertain')
         || '|' ||
            (SELECT count(*) FROM outbox WHERE organization_id = '$organization' AND topic = 'effect.uncertain' AND body->>'attempt' = '$1')
         || '|' ||
            COALESCE((SELECT string_agg(payload->>'reason', ',') FROM events WHERE organization_id = '$organization' AND attempt_id = '$1' AND kind = 'effect.uncertain'), '-')"
}

# The windows and what each must leave behind at the kill.
window_state() {
  case "$1" in
    prepare) printf 'prepared|-\n' ;;
    invoke) printf 'prepared|-\n' ;;
    apply) printf 'applied|-\n' ;;
    confirm) printf 'confirmed|-\n' ;;
  esac
}
window_receipts() {
  case "$1" in
    prepare) printf '0\n' ;;
    *) printf '1\n' ;;
  esac
}

for window in prepare invoke apply confirm; do
  echo "=== FG-026b window: kill after $window ==="
  stamp="$$_$(date +%s)_$window"
  database="fogell_fg026b_$stamp"
  role="fogell_fg026b_runtime_$stamp"
  state_root="$scratch/state-$window"
  drop_root="$scratch/drop-$window"
  host_log="$scratch/controller-$window.log"
  mkdir -p "$state_root" "$drop_root"
  # The operator-created marker that pins the destination (FG-026b review fold).
  : >"$drop_root/.fogell-drop-root"
  : >"$host_log"

  admin postgres -c "CREATE DATABASE $database" >/dev/null
  admin postgres -c "CREATE ROLE $role NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS" >/dev/null
  maintenance_url="Host=127.0.0.1;Port=$port;Username=fogell;Password=fogell;Database=$database"
  runtime_url="$maintenance_url;Options=-c role=$role;No Reset On Close=true;Maximum Pool Size=8"
  common_env=(
    "FOGELL_DATABASE_URL=$runtime_url"
    "FOGELL_MAINTENANCE_DATABASE_URL=$maintenance_url"
    "FOGELL_API_TOKEN_FILE=$token_file"
    "FOGELL_LISTEN_URL=$base_url"
    "FOGELL_STATE_ROOT=$state_root"
    "FOGELL_RUN_HOST_PATH=$run_host"
    "FOGELL_LOCAL_TRUST_POOL=trusted-linux"
    "FOGELL_MAX_PIPELINE_BYTES=1024"
    "FOGELL_MAX_LOG_CHUNKS=100"
    "FOGELL_WORKER_POLL_MS=50"
    "FOGELL_WORKER_LEASE_SECONDS=$lease_seconds"
    "FOGELL_EFFECT_FILE_DROP_ROOT=$drop_root"
  )

  # A kill hook without a destination must refuse startup before binding.
  if [[ "$window" = prepare ]]; then
    set +e
    bounded "$process_budget" env "${common_env[@]/FOGELL_EFFECT_FILE_DROP_ROOT=*/FOGELL_EFFECT_FILE_DROP_ROOT=}" \
      "FOGELL_EFFECT_KILL_AT=prepare" "$controller" >"$scratch/orphan-kill.stdout" 2>"$scratch/orphan-kill.stderr"
    orphan_rc=$?
    set -e
    [[ $orphan_rc -eq 2 ]] \
      || { echo "FG-026b REFUSED: a kill hook without a destination did not refuse startup (rc $orphan_rc)" >&2; exit 1; }
    rg -q -F 'FOGELL_EFFECT_KILL_AT requires FOGELL_EFFECT_FILE_DROP_ROOT' "$scratch/orphan-kill.stderr" \
      || { echo "FG-026b REFUSED: the orphan kill-hook refusal was not named" >&2; exit 1; }
    ! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
      || { echo "FG-026b REFUSED: the orphan kill-hook startup bound a socket" >&2; exit 1; }
  fi

  # First start migrates and then refuses the incomplete runtime capability
  # (exit 3); the grants below need the migrated tables.
  set +e
  bounded "$process_budget" env "${common_env[@]}" "$controller" \
    >"$scratch/capability-$window.stdout" 2>"$scratch/capability-$window.stderr"
  capability_rc=$?
  set -e
  [[ $capability_rc -eq 3 ]] \
    || { echo "FG-026b REFUSED: migrating startup did not refuse the incomplete capability (rc $capability_rc): $(tail -n 2 "$scratch/capability-$window.stderr" | tr '\n' ' ')" >&2; exit 1; }
  admin "$database" \
    -c "GRANT USAGE ON SCHEMA public TO $role" \
    -c "GRANT SELECT, UPDATE(singleton) ON controller_metadata TO $role" \
    -c "GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes, attempts, events, outbox, log_chunks, effect_checkpoints, retry_decisions, build_definitions TO $role" \
    -c "GRANT SELECT ON organization_work_roots TO $role" \
    -c "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $role" >/dev/null

  organization=$(tr -d '-' </proc/sys/kernel/random/uuid)
  organization="${organization:0:8}-${organization:8:4}-${organization:12:4}-${organization:16:4}-${organization:20:12}"
  project=$(tr -d '-' </proc/sys/kernel/random/uuid)
  project="${project:0:8}-${project:8:4}-${project:12:4}-${project:16:4}-${project:20:12}"
  admin "$database" \
    -c "INSERT INTO organizations (id, slug) VALUES ('$organization', 'fg026b-org')" \
    -c "INSERT INTO projects (id, organization_id, slug) VALUES ('$project', '$organization', 'fg026b-project')" >/dev/null
  organization_key=${organization//-/}
  builds_url="$base_url/api/v1/organizations/$organization/projects/$project/builds"
  listing_url="$base_url/api/v1/organizations/$organization/effects/uncertain"

  launch_controller "$window"
  wait_ready "armed controller ($window)"

  response=$(curl --max-time "$http_max_time" -fsS -X POST -H "$auth" -H "idempotency-key: fg026b-$window" \
    -H 'content-type: application/x-jenkinsfile' --data-binary "$pipeline" "$builds_url")
  build_id=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' <<<"$response")
  attempt_id=$(sed -n 's/.*"attempt_id":"\([^"]*\)".*/\1/p' <<<"$response")
  [[ -n "$build_id" && -n "$attempt_id" ]] \
    || { echo "FG-026b REFUSED: admission returned no build/attempt id: $response" >&2; exit 1; }

  # The controller must die inside the window: SIGKILL from its own hook.
  set +e
  wait_bounded "$host_pid" 60000 "armed controller ($window) reaching its kill window"
  death_rc=$?
  set -e
  host_pid=""
  [[ $death_rc -ne 124 ]] || { echo "FG-026b REFUSED: armed controller ($window) never reached its kill window" >&2; exit 1; }
  judge_exit_status "$death_rc" \
    || { echo "FG-026b REFUSED: armed controller ($window) did not die by SIGKILL: $(tail -n 3 "$host_log" | tr '\n' ' ')" >&2; exit 1; }
  survivors=$(survivors_of "$state_root" || true)
  [[ -z "$survivors" ]] \
    || { echo "FG-026b REFUSED: processes survived the controller's death ($window): $survivors" >&2; exit 1; }
  ! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
    || { echo "FG-026b REFUSED: something still answers on the controller port after the kill ($window)" >&2; exit 1; }

  expected_state=$(window_state "$window")
  expected_receipts=$(window_receipts "$window")
  observed_state=$(ledger_row "$attempt_id")
  [[ "$observed_state" = "$expected_state" ]] \
    || { echo "FG-026b REFUSED: ledger after kill in $window is '$observed_state', expected '$expected_state'" >&2; exit 1; }
  judge_receipts "$drop_root/$organization_key" "$expected_receipts" \
    || { echo "FG-026b REFUSED: destination after kill in $window" >&2; exit 1; }
  if [[ "$expected_receipts" = 1 ]]; then
    receipt="$drop_root/$organization_key/${attempt_id//-/}.receipt"
    [[ -f "$receipt" ]] || { echo "FG-026b REFUSED: receipt is not keyed by the attempt: $receipt" >&2; exit 1; }
    rg -q -F "\"attempt\":\"$attempt_id\"" "$receipt" \
      || { echo "FG-026b REFUSED: receipt does not name its attempt" >&2; exit 1; }
    rg -q -F '"journal_terminal":"Success"' "$receipt" \
      || { echo "FG-026b REFUSED: receipt does not carry the journal terminal" >&2; exit 1; }
    receipt_digest_before=$(sha256sum "$receipt" | cut -d' ' -f1)
  fi
  attempt_after_kill=$(admin "$database" -Atc "SELECT state FROM attempts WHERE organization_id = '$organization' AND id = '$attempt_id'")
  [[ "$attempt_after_kill" = running ]] \
    || { echo "FG-026b REFUSED: attempt after kill in $window is '$attempt_after_kill', expected a still-leased running attempt" >&2; exit 1; }
  observed_surface=$(surface_counts "$attempt_id")
  [[ "$observed_surface" = "0|0|-" ]] \
    || { echo "FG-026b REFUSED: a surface was published before any trigger ran ($window): $observed_surface" >&2; exit 1; }

  # The trigger. prepare/invoke: let the dead controller's lease expire first so
  # the STARTUP pass is what classifies. apply: relaunch under the live lease so
  # the PERIODIC pass classifies once the lease expires. confirm: nothing to
  # classify; the lease loss alone must move the attempt to reconciliation.
  expected_reason=lease_expired
  if [[ "$window" = prepare || "$window" = invoke ]]; then
    expected_reason=controller_startup
    poll_deadline=$(deadline_after $(( (lease_seconds + 5) * 1000 )))
    expired=0
    while before_deadline "$poll_deadline"; do
      remaining=$(admin "$database" -Atc "SELECT (lease_expires_at <= clock_timestamp())::text FROM attempts WHERE organization_id = '$organization' AND id = '$attempt_id'")
      [[ "$remaining" = true ]] && { expired=1; break; }
      sleep 0.25
    done
    [[ $expired -eq 1 ]] || { echo "FG-026b REFUSED: the dead controller's lease did not expire within $((lease_seconds + 5)) s" >&2; exit 1; }
  elif [[ "$window" = apply ]]; then
    # Time only: the dead controller's lease is set to one full lease from now,
    # so the startup pass provably sees live authority and the classification
    # can only come from the periodic pass once that lease expires.
    admin "$database" -c "UPDATE attempts SET lease_expires_at = clock_timestamp() + interval '$lease_seconds seconds'
                           WHERE organization_id = '$organization' AND id = '$attempt_id' AND state = 'running'" >/dev/null
  fi

  launch_controller ""
  wait_ready "relaunched controller ($window)"

  if [[ "$window" = confirm ]]; then
    poll_deadline=$(deadline_after $(( (lease_seconds + 20) * 1000 )))
    reconciled=0
    while before_deadline "$poll_deadline"; do
      status_json=$(curl --max-time "$http_max_time" -fsS -H "$auth" "$builds_url/$build_id" || true)
      rg -q -F '"status":"reconciliation_required"' <<<"$status_json" && { reconciled=1; break; }
      if rg -q '"status":"(success|failure|unstable|aborted)"' <<<"$status_json"; then
        echo "FG-026b REFUSED: confirm window: a build whose terminal publication was lost reached terminal truth: $status_json" >&2
        exit 1
      fi
      sleep 0.25
    done
    [[ $reconciled -eq 1 ]] || { echo "FG-026b REFUSED: confirm window: lease loss did not move the build to reconciliation_required" >&2; exit 1; }
    listing=$(curl --max-time "$http_max_time" -fsS -H "$auth" "$listing_url")
    judge_listing "$listing" "$organization" "$attempt_id" 0 \
      || { echo "FG-026b REFUSED: confirm window listing: $listing" >&2; exit 1; }
    observed_state=$(ledger_row "$attempt_id")
    [[ "$observed_state" = "confirmed|-" ]] \
      || { echo "FG-026b REFUSED: confirm window: ledger became '$observed_state' after lease loss" >&2; exit 1; }
    observed_surface=$(surface_counts "$attempt_id")
    judge_surface "$(cut -d'|' -f1 <<<"$observed_surface")" "$(cut -d'|' -f2 <<<"$observed_surface")" 0 \
      || { echo "FG-026b REFUSED: confirm window published an uncertainty surface: $observed_surface" >&2; exit 1; }
  else
    poll_deadline=$(deadline_after $(( (lease_seconds + 20) * 1000 )))
    listed=0
    listing=""
    while before_deadline "$poll_deadline"; do
      listing=$(curl --max-time "$http_max_time" -fsS -H "$auth" "$listing_url" || true)
      if judge_listing "$listing" "$organization" "$attempt_id" 1 2>/dev/null; then
        listed=1
        break
      fi
      sleep 0.25
    done
    [[ $listed -eq 1 ]] \
      || { echo "FG-026b REFUSED: $window window: the production trigger never listed the stranded effect: ${listing:-no listing}: $(tail -n 5 "$host_log" | tr '\n' ' ')" >&2; exit 1; }
    rg -q -F "\"uncertain_from\":\"${expected_state%%|*}\"" <<<"$listing" \
      || { echo "FG-026b REFUSED: $window window listing does not carry origin '${expected_state%%|*}': $listing" >&2; exit 1; }
    rg -q -F "\"effect_key\":\"file-drop-receipt:${attempt_id//-/}\"" <<<"$listing" \
      || { echo "FG-026b REFUSED: $window window listing does not carry the registry key: $listing" >&2; exit 1; }

    observed_state=$(ledger_row "$attempt_id")
    [[ "$observed_state" = "uncertain|${expected_state%%|*}" ]] \
      || { echo "FG-026b REFUSED: $window window: ledger is '$observed_state' after the trigger" >&2; exit 1; }
    observed_surface=$(surface_counts "$attempt_id")
    judge_surface "$(cut -d'|' -f1 <<<"$observed_surface")" "$(cut -d'|' -f2 <<<"$observed_surface")" 1 \
      || { echo "FG-026b REFUSED: $window window surface: $observed_surface" >&2; exit 1; }
    [[ "$(cut -d'|' -f3 <<<"$observed_surface")" = "$expected_reason" ]] \
      || { echo "FG-026b REFUSED: $window window: surface reason is '$(cut -d'|' -f3 <<<"$observed_surface")', expected '$expected_reason'" >&2; exit 1; }
    startup_line="FG-026b startup reconciliation: effect file-drop-receipt:${attempt_id//-/} for attempt $attempt_id"
    if [[ "$expected_reason" = controller_startup ]]; then
      rg -q -F "$startup_line" "$host_log" \
        || { echo "FG-026b REFUSED: $window window: the startup pass did not report the classification" >&2; exit 1; }
    else
      ! rg -q -F "$startup_line" "$host_log" \
        || { echo "FG-026b REFUSED: $window window: the startup pass classified a live-leased row" >&2; exit 1; }
    fi

    # The attempt and build follow the lease loss into reconciliation.
    poll_deadline=$(deadline_after $(( (lease_seconds + 20) * 1000 )))
    reconciled=0
    while before_deadline "$poll_deadline"; do
      status_json=$(curl --max-time "$http_max_time" -fsS -H "$auth" "$builds_url/$build_id" || true)
      rg -q -F '"status":"reconciliation_required"' <<<"$status_json" && { reconciled=1; break; }
      sleep 0.25
    done
    [[ $reconciled -eq 1 ]] || { echo "FG-026b REFUSED: $window window: build did not reach reconciliation_required: $status_json" >&2; exit 1; }
    attempt_truth=$(admin "$database" -Atc \
      "SELECT a.state || '|' || COALESCE((SELECT string_agg(e.payload->>'reason', ',') FROM events e WHERE e.organization_id = a.organization_id AND e.attempt_id = a.id AND e.kind = 'attempt.reconciliation_required'), '-')
         FROM attempts a WHERE a.organization_id = '$organization' AND a.id = '$attempt_id'")
    [[ "$attempt_truth" = "reconciliation_required|lease_expired" ]] \
      || { echo "FG-026b REFUSED: $window window: attempt truth is '$attempt_truth'" >&2; exit 1; }
  fi

  # Two more lease periods (a fixed, named $((lease_seconds * 2)) s wait):
  # nothing re-invokes, nothing rewrites, nothing publishes a second surface.
  sleep $(( lease_seconds * 2 ))
  judge_receipts "$drop_root/$organization_key" "$expected_receipts" \
    || { echo "FG-026b REFUSED: $window window: destination changed after the trigger" >&2; exit 1; }
  if [[ "$expected_receipts" = 1 ]]; then
    [[ "$(sha256sum "$receipt" | cut -d' ' -f1)" = "$receipt_digest_before" ]] \
      || { echo "FG-026b REFUSED: $window window: receipt bytes changed after the trigger" >&2; exit 1; }
  fi
  final_surface=$(surface_counts "$attempt_id")
  if [[ "$window" = confirm ]]; then
    [[ "$final_surface" = "0|0|-" ]] || { echo "FG-026b REFUSED: confirm window published a late surface: $final_surface" >&2; exit 1; }
  else
    judge_surface "$(cut -d'|' -f1 <<<"$final_surface")" "$(cut -d'|' -f2 <<<"$final_surface")" 1 \
      || { echo "FG-026b REFUSED: $window window: surface was published again: $final_surface" >&2; exit 1; }
    final_state=$(ledger_row "$attempt_id")
    [[ "$final_state" = "$observed_state" ]] \
      || { echo "FG-026b REFUSED: $window window: ledger moved after classification: $final_state" >&2; exit 1; }
  fi

  # Tenant scope: a second organization sees nothing of this one.
  foreign=$(tr -d '-' </proc/sys/kernel/random/uuid)
  foreign="${foreign:0:8}-${foreign:8:4}-${foreign:12:4}-${foreign:16:4}-${foreign:20:12}"
  admin "$database" -c "INSERT INTO organizations (id, slug) VALUES ('$foreign', 'fg026b-foreign')" >/dev/null
  foreign_listing=$(curl --max-time "$http_max_time" -fsS -H "$auth" "$base_url/api/v1/organizations/$foreign/effects/uncertain")
  judge_listing "$foreign_listing" "$foreign" "$attempt_id" 0 \
    || { echo "FG-026b REFUSED: $window window: another organization can see this tenant's uncertainty: $foreign_listing" >&2; exit 1; }

  release_controller
  ! curl -fsS --max-time 1 "$base_url/health/live" >/dev/null 2>&1 \
    || { echo "FG-026b REFUSED: stopped controller still served requests ($window)" >&2; exit 1; }
  survivors=$(survivors_of "$state_root" || true)
  [[ -z "$survivors" ]] \
    || { echo "FG-026b REFUSED: processes survived the relaunched controller's stop ($window): $survivors" >&2; exit 1; }
  drop_database
  if [[ "$window" = confirm ]]; then
    echo "  $window: killed by SIGKILL inside the window; ledger '$expected_state' with $expected_receipts receipt(s); lease loss reconciled the attempt and the confirmed row stayed confirmed, unlisted, without re-invocation"
  else
    echo "  $window: killed by SIGKILL inside the window; ledger '$expected_state' with $expected_receipts receipt(s); trigger ($expected_reason) classified and surfaced without re-invocation"
  fi
done

echo "FG-026b EFFECT-DISPATCH PROOF: judges refuse planted false-green inputs; the real controller died by SIGKILL in each of the four windows; prepared/applied work was classified uncertain by the startup and lease-expiry triggers with exactly one event/outbox pair, listed per tenant, never re-invoked; confirmed work survived lease loss unlisted"
