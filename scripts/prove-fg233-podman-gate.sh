#!/usr/bin/env bash
# FG-233 — prove the hosted gate chooses Podman explicitly and never returns
# to a runner-global PostgreSQL port. This is a static workflow boundary; the
# hosted controller, hang, build, and database jobs are its live runtime proof.
set -euo pipefail

for required_command in bash basename chmod cp date dirname mkdir mktemp rm rg sed seq sleep tail timeout tr; do
  command -v "$required_command" >/dev/null \
    || { printf 'FG-233 REFUSED: %s is required\n' "$required_command" >&2; exit 2; }
done

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
workflow="$repo/.github/workflows/gate.yml"
postgres="$repo/scripts/ci-postgres.sh"
local_postgres="$repo/scripts/pg-test-db.sh"
controller="$repo/scripts/prove-runnable-controller.sh"
inotify="$repo/scripts/prove-fg232-controller-inotify.sh"
scratch=$(mktemp -d /tmp/fogell-fg233-proof.XXXXXX)
trap 'rm -rf -- "$scratch"' EXIT

refuse() {
  printf 'FG-233 REFUSED: %s\n' "$*" >&2
}

check_candidate() {
  local candidate_workflow=$1
  local candidate_postgres=$2
  local candidate_local_postgres=$3
  local candidate_controller=$4
  local candidate_inotify=$5
  local forbidden_runtime=dock
  forbidden_runtime+=er
  local starts stops guarded_stops selected

  if rg -q '^\s+services:' "$candidate_workflow"; then
    refuse "Actions service containers select the runner runtime implicitly"
    return 1
  fi
  if rg -q 'job\.services' "$candidate_workflow"; then
    refuse "the workflow still depends on an Actions service container id"
    return 1
  fi
  if rg -q "\\b${forbidden_runtime}\\b" "$candidate_workflow"; then
    refuse "the workflow invokes the unintended container runtime"
    return 1
  fi

  selected=$(rg -c '^  FOGELL_CONTAINER_RUNTIME: podman$' "$candidate_workflow" || true)
  [[ "$selected" = 1 ]] \
    || { refuse "the workflow must select Podman exactly once at global scope (found $selected)"; return 1; }
  starts=$(rg -c '^\s+run: \./scripts/ci-postgres\.sh start$' "$candidate_workflow" || true)
  stops=$(rg -c '^\s+run: \./scripts/ci-postgres\.sh stop$' "$candidate_workflow" || true)
  [[ "$starts" = 4 && "$stops" = 4 ]] \
    || { refuse "expected four PostgreSQL starts and stops, found $starts starts and $stops stops"; return 1; }
  guarded_stops=$(rg -U -c "if: always\\(\\)( && matrix\\.lane == 'build')? && env\\.FOGELL_PG_CONTAINER != ''\\n\\s+run: \\./scripts/ci-postgres\\.sh stop" "$candidate_workflow" || true)
  [[ "$guarded_stops" = 4 ]] \
    || { refuse "every PostgreSQL cleanup must require an exported container name (found $guarded_stops guarded stops)"; return 1; }

  rg -q -- '--publish 127\.0\.0\.1::5432' "$candidate_postgres" \
    || { refuse "PostgreSQL does not request a runtime-allocated host port"; return 1; }
  rg -q 'port \"\$container\" 5432/tcp' "$candidate_postgres" \
    || { refuse "the allocated PostgreSQL port is not read back from the runtime"; return 1; }
  rg -q 'FOGELL_TEST_DATABASE_URL=%s' "$candidate_postgres" \
    || { refuse "the dynamic port is not published to the test suites"; return 1; }

  rg -Fq 'runtime=${FOGELL_CONTAINER_RUNTIME:-podman}' "$candidate_local_postgres" \
    || { refuse "the local PostgreSQL helper does not default to Podman"; return 1; }
  rg -Fq 'PORT=${2:-}' "$candidate_local_postgres" \
    || { refuse "the local PostgreSQL helper has a fixed host-port fallback"; return 1; }
  rg -Fq 'publish="127.0.0.1::5432"' "$candidate_local_postgres" \
    || { refuse "the local PostgreSQL helper does not request a runtime-allocated host port"; return 1; }
  rg -Fq 'PORT=${BASH_REMATCH[2]}' "$candidate_local_postgres" \
    || { refuse "the local PostgreSQL helper does not consume the allocated host port"; return 1; }

  rg -Fq 'port=${FOGELL_PG_PORT:-}' "$candidate_controller" \
    || { refuse "the controller proof has a fixed PostgreSQL host-port fallback"; return 1; }
  rg -Fq 'Port=$port;' "$candidate_controller" \
    || { refuse "the controller proof does not consume the allocated PostgreSQL host port"; return 1; }
  rg -Fq 'port=${FOGELL_PG_PORT:-}' "$candidate_inotify" \
    || { refuse "the inotify proof has a fixed PostgreSQL host-port fallback"; return 1; }
  rg -Fq 'Port=$port;' "$candidate_inotify" \
    || { refuse "the inotify proof does not consume the allocated PostgreSQL host port"; return 1; }
}

expect_refusal() {
  local name=$1 expected=$2
  if check_candidate "$scratch/$name-gate.yml" "$scratch/$name-postgres.sh" \
      "$scratch/$name-local-postgres.sh" "$scratch/$name-controller.sh" \
      "$scratch/$name-inotify.sh" >"$scratch/$name.log" 2>&1; then
    echo "FG-233 REFUSED: checker accepted planted $name defect" >&2
    exit 1
  fi
  rg -q -- "$expected" "$scratch/$name.log" \
    || { echo "FG-233 REFUSED: $name failed for the wrong reason: $(tr '\n' ' ' <"$scratch/$name.log")" >&2; exit 1; }
  printf '  killed: %s\n' "$name"
}

for name in service-container fixed-port missing-job-runtime missing-cleanup-guard local-fixed-port controller-fixed-port inotify-fixed-port local-readback controller-consumer inotify-consumer; do
  cp "$workflow" "$scratch/$name-gate.yml"
  cp "$postgres" "$scratch/$name-postgres.sh"
  cp "$local_postgres" "$scratch/$name-local-postgres.sh"
  cp "$controller" "$scratch/$name-controller.sh"
  cp "$inotify" "$scratch/$name-inotify.sh"
done

sed -i '0,/^  lane:$/s//  lane:\n    services:\n      planted:\n        image: docker.io\/library\/postgres:16/' "$scratch/service-container-gate.yml"
expect_refusal service-container 'service containers select the runner runtime implicitly'

sed -i 's/127\.0\.0\.1::5432/127.0.0.1:55440:5432/' "$scratch/fixed-port-postgres.sh"
expect_refusal fixed-port 'does not request a runtime-allocated host port'

sed -i '0,/^  FOGELL_CONTAINER_RUNTIME: podman$/{/^  FOGELL_CONTAINER_RUNTIME: podman$/d;}' "$scratch/missing-job-runtime-gate.yml"
expect_refusal missing-job-runtime 'must select Podman exactly once'

sed -i "0,/if: always() && env.FOGELL_PG_CONTAINER != ''/s//if: always()/" "$scratch/missing-cleanup-guard-gate.yml"
expect_refusal missing-cleanup-guard 'every PostgreSQL cleanup must require an exported container name'

sed -i 's/PORT=${2:-}/PORT=${2:-55440}/' "$scratch/local-fixed-port-local-postgres.sh"
expect_refusal local-fixed-port 'local PostgreSQL helper has a fixed host-port fallback'

sed -i 's/port=${FOGELL_PG_PORT:-}/port=${FOGELL_PG_PORT:-55445}/' "$scratch/controller-fixed-port-controller.sh"
expect_refusal controller-fixed-port 'controller proof has a fixed PostgreSQL host-port fallback'

sed -i 's/port=${FOGELL_PG_PORT:-}/port=${FOGELL_PG_PORT:-55445}/' "$scratch/inotify-fixed-port-inotify.sh"
expect_refusal inotify-fixed-port 'inotify proof has a fixed PostgreSQL host-port fallback'

sed -i 's/PORT=${BASH_REMATCH\[2\]}/PORT=55440/' "$scratch/local-readback-local-postgres.sh"
expect_refusal local-readback 'local PostgreSQL helper does not consume the allocated host port'

sed -i 's/Port=\$port;/Port=55445;/' "$scratch/controller-consumer-controller.sh"
expect_refusal controller-consumer 'controller proof does not consume the allocated PostgreSQL host port'

sed -i 's/Port=\$port;/Port=55445;/' "$scratch/inotify-consumer-inotify.sh"
expect_refusal inotify-consumer 'inotify proof does not consume the allocated PostgreSQL host port'

check_candidate "$workflow" "$postgres" "$local_postgres" "$controller" "$inotify"

runtime_shim="$scratch/not-a-runtime"
runtime_calls="$scratch/runtime-calls"
printf '%s\n' '#!/usr/bin/env bash' 'printf "%s\\n" "$*" >>"$FG233_RUNTIME_CALLS"' >"$runtime_shim"
chmod +x "$runtime_shim"
for proof in "$controller" "$inotify"; do
  rm -f "$runtime_calls"
  proof_name=$(basename "$proof")
  proof_rc=0
  FG233_RUNTIME_CALLS="$runtime_calls" FOGELL_CONTAINER_RUNTIME="$runtime_shim" \
    FOGELL_PG_CONTAINER=fogell-fg233-refusal FOGELL_PG_PORT=1 \
    bash "$proof" >"$scratch/$proof_name-invalid-runtime.log" 2>&1 || proof_rc=$?
  [[ "$proof_rc" = 2 ]] \
    || { refuse "$proof_name did not refuse an invalid runtime with exit 2 (got $proof_rc)"; exit 1; }
  rg -q 'FOGELL_CONTAINER_RUNTIME must be exactly podman or docker' "$scratch/$proof_name-invalid-runtime.log" \
    || { refuse "$proof_name did not name its invalid-runtime refusal"; exit 1; }
  [[ ! -e "$runtime_calls" ]] \
    || { refuse "$proof_name invoked an untrusted runtime during refusal"; exit 1; }
done

mkdir -p "$scratch/target-bin"
target_calls="$scratch/target-calls"
printf '%s\n' '#!/usr/bin/env bash' 'printf "%s\\n" "$*" >>"$FG233_TARGET_CALLS"' >"$scratch/target-bin/podman"
chmod +x "$scratch/target-bin/podman"
for proof in "$controller" "$inotify"; do
  proof_name=$(basename "$proof")
  target_rc=0
  PATH="$scratch/target-bin:$PATH" FG233_TARGET_CALLS="$target_calls" \
    FOGELL_CONTAINER_RUNTIME=podman FOGELL_PG_CONTAINER=--latest FOGELL_PG_PORT=1 \
    bash "$proof" >"$scratch/$proof_name-invalid-target.log" 2>&1 || target_rc=$?
  [[ "$target_rc" = 2 ]] \
    || { refuse "$proof_name did not refuse an option-like container name with exit 2 (got $target_rc)"; exit 1; }
  rg -q 'FOGELL_PG_CONTAINER must be a literal container name' "$scratch/$proof_name-invalid-target.log" \
    || { refuse "$proof_name did not name its invalid-container refusal"; exit 1; }
done
[[ ! -e "$target_calls" ]] \
  || { refuse "a controller proof invoked the runtime with an option-like container target"; exit 1; }

mkdir -p "$scratch/mismatch-bin"
mismatch_calls="$scratch/mismatch-calls"
printf '%s\n' '#!/usr/bin/env bash' \
  'printf "%s\\n" "$*" >>"$FG233_MISMATCH_CALLS"' \
  'if [[ "${1:-}" = port ]]; then printf "%s\\n" "127.0.0.1:2"; fi' >"$scratch/mismatch-bin/podman"
chmod +x "$scratch/mismatch-bin/podman"
for proof in "$controller" "$inotify"; do
  proof_name=$(basename "$proof")
  mismatch_rc=0
  PATH="$scratch/mismatch-bin:$PATH" FG233_MISMATCH_CALLS="$mismatch_calls" \
    FOGELL_CONTAINER_RUNTIME=podman FOGELL_PG_CONTAINER=fogell-fg233-mismatch \
    FOGELL_PG_PORT=1 bash "$proof" >"$scratch/$proof_name-mismatch.log" 2>&1 || mismatch_rc=$?
  [[ "$mismatch_rc" = 2 ]] \
    || { refuse "$proof_name did not refuse a container/port mismatch with exit 2 (got $mismatch_rc)"; exit 1; }
  rg -q 'FOGELL_PG_PORT does not match the selected PostgreSQL container' "$scratch/$proof_name-mismatch.log" \
    || { refuse "$proof_name did not name its container/port mismatch"; exit 1; }
done
[[ $(rg -c '^port fogell-fg233-mismatch 5432/tcp$' "$mismatch_calls" || true) = 2 ]] \
  || { refuse "the controller proofs did not query the selected PostgreSQL container mapping"; exit 1; }
mismatch_execs=$(rg -c '^exec ' "$mismatch_calls" || true)
[[ ${mismatch_execs:-0} = 0 ]] \
  || { refuse "a controller proof touched PostgreSQL after a container/port mismatch"; exit 1; }

mkdir -p "$scratch/bin"
failed_start_calls="$scratch/failed-start-calls"
printf '%s\n' '#!/usr/bin/env bash' \
  'printf "%s\\n" "$*" >>"$FG233_FAILED_START_CALLS"' \
  '[[ "${1:-}" != run ]]' >"$scratch/bin/podman"
chmod +x "$scratch/bin/podman"
failed_start_rc=0
PATH="$scratch/bin:$PATH" FG233_FAILED_START_CALLS="$failed_start_calls" \
  FOGELL_CONTAINER_RUNTIME=podman bash "$local_postgres" fogell-fg233-failed-start \
  >"$scratch/failed-start.log" 2>&1 || failed_start_rc=$?
[[ "$failed_start_rc" != 0 ]] \
  || { refuse "the local PostgreSQL helper accepted a failed container start"; exit 1; }
failed_start_removals=$(rg -c '^rm -f fogell-fg233-failed-start$' "$failed_start_calls" || true)
[[ "$failed_start_removals" = 2 ]] \
  || { refuse "a failed local PostgreSQL start was not cleaned up (found $failed_start_removals removals)"; exit 1; }

rm -f "$failed_start_calls"
failed_ci_start_rc=0
PATH="$scratch/bin:$PATH" FG233_FAILED_START_CALLS="$failed_start_calls" \
  FOGELL_CONTAINER_RUNTIME=podman GITHUB_RUN_ID=fg233 GITHUB_JOB=failed-start \
  GITHUB_RUN_ATTEMPT=1 bash "$postgres" start \
  >"$scratch/failed-ci-start.log" 2>&1 || failed_ci_start_rc=$?
[[ "$failed_ci_start_rc" != 0 ]] \
  || { refuse "the hosted PostgreSQL helper accepted a failed container start"; exit 1; }
failed_ci_runs=$(rg -c '^run --detach --rm --name fogell-gate-postgres-' "$failed_start_calls" || true)
failed_ci_removals=$(rg -c '^rm -f fogell-gate-postgres-' "$failed_start_calls" || true)
[[ "$failed_ci_runs" = 1 && "$failed_ci_removals" = 1 ]] \
  || { refuse "a failed hosted PostgreSQL start was not cleaned up (found $failed_ci_runs runs and $failed_ci_removals removals)"; exit 1; }

echo "FG-233 PROOF PASS: Podman is explicit, Actions services are absent, four jobs own guarded disposable PostgreSQL lifecycles, and hosted plus local host ports are runtime-allocated"
