#!/usr/bin/env bash
# FG-233 — prove the hosted gate chooses Podman explicitly and never returns
# to a runner-global PostgreSQL port. This is a static workflow boundary; the
# hosted controller, hang, build, and database jobs are its live runtime proof.
#
# EVERY WORKFLOW UNDER .github/workflows, DISCOVERED. This named gate.yml alone
# until FG-256 added gate-mutants.yml, which owns a PostgreSQL lifecycle of its
# own. A second file would have escaped every check here — free to declare an
# Actions `services:` block, name the unintended runtime, or leave a cleanup
# unguarded — while this proof still passed and said it had checked the hosted
# gate. Discovering the directory covers the next workflow by construction.
#
# The `\bdocker\b` scan therefore now applies to every workflow. That is
# fail-closed and intended; a future workflow with a legitimate reason to name
# Docker will have to argue with this proof rather than slip past it.
#
# SO DOES THE PODMAN DECLARATION, and that is the sharper edge: EVERY workflow
# under this directory must carry `FOGELL_CONTAINER_RUNTIME: podman` exactly
# once at global scope, including one that starts no container at all. A
# docs-only workflow added later will fail this proof until it declares a
# runtime it never uses. That is deliberate — the alternative, requiring the
# declaration only where a PostgreSQL lifecycle is detected, would exempt
# precisely the workflow that reaches for a container by some other means,
# which is the hole FG-233 exists to close. Relax it only with a check that
# still covers that case.
set -euo pipefail

for required_command in bash basename chmod cp date dirname mkdir mktemp rm rg sed seq sleep tail timeout tr; do
  command -v "$required_command" >/dev/null \
    || { printf 'FG-233 REFUSED: %s is required\n' "$required_command" >&2; exit 2; }
done

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
workflows_dir="$repo/.github/workflows"
postgres="$repo/scripts/ci-postgres.sh"
local_postgres="$repo/scripts/pg-test-db.sh"
controller="$repo/scripts/prove-runnable-controller.sh"
inotify="$repo/scripts/prove-fg232-controller-inotify.sh"
scratch_root=$(cd -- "${TMPDIR:-/tmp}" && pwd -P) \
  || { printf 'FG-233 REFUSED: temporary directory root is unavailable\n' >&2; exit 2; }
scratch=$(mktemp -d "${scratch_root%/}/fogell-fg233-proof.XXXXXX")
scratch=$(cd -- "$scratch" && pwd -P) \
  || { printf 'FG-233 REFUSED: temporary proof directory is unavailable\n' >&2; exit 2; }
cleanup_scratch() {
  case "$scratch" in
    "${scratch_root%/}"/fogell-fg233-proof.*) rm -rf -- "$scratch" ;;
    *) printf 'FG-233 REFUSED: unsafe cleanup path: %s\n' "$scratch" >&2 ;;
  esac
}
trap cleanup_scratch EXIT

refuse() {
  printf 'FG-233 REFUSED: %s\n' "$*" >&2
}

check_candidate() {
  local candidate_workflows=$1
  local candidate_postgres=$2
  local candidate_local_postgres=$3
  local candidate_controller=$4
  local candidate_inotify=$5
  local forbidden_runtime=dock
  forbidden_runtime+=er
  local starts stops guarded_stops selected
  local workflow_file total_starts=0 workflow_count=0

  # EVERY WORKFLOW, DISCOVERED — not one hardcoded path. This audit named
  # .github/workflows/gate.yml alone until FG-256 added a second workflow that
  # also owns a PostgreSQL lifecycle. A second file would have escaped every
  # check below: it could have declared an Actions `services:` block, named the
  # unintended runtime, or left a cleanup unguarded, and this proof would have
  # passed while saying it had checked the hosted gate. Discovering the
  # directory makes the next workflow covered by construction rather than by
  # somebody remembering this file.
  for workflow_file in "$candidate_workflows"/*.yml; do
    [ -e "$workflow_file" ] || { refuse "no workflows found under $candidate_workflows"; return 1; }
    workflow_count=$((workflow_count + 1))

    if rg -q '^\s+services:' "$workflow_file"; then
      refuse "Actions service containers select the runner runtime implicitly ($(basename "$workflow_file"))"
      return 1
    fi
    if rg -q 'job\.services' "$workflow_file"; then
      refuse "the workflow still depends on an Actions service container id ($(basename "$workflow_file"))"
      return 1
    fi
    if rg -q "\\b${forbidden_runtime}\\b" "$workflow_file"; then
      refuse "the workflow invokes the unintended container runtime ($(basename "$workflow_file"))"
      return 1
    fi

    selected=$(rg -c '^  FOGELL_CONTAINER_RUNTIME: podman$' "$workflow_file" || true)
    selected=${selected:-0}
    [[ "$selected" = 1 ]] \
      || { refuse "the workflow must select Podman exactly once at global scope (found $selected in $(basename "$workflow_file"))"; return 1; }

    # PER FILE, every start is matched by a guarded stop. A file-local balance
    # is what makes a leaked container impossible; the cross-file total below is
    # what makes a silently DELETED lifecycle impossible.
    # `rg -c` prints NOTHING and exits 1 when there is no match, so each count
    # is defaulted to 0 rather than left empty: an empty string compares unequal
    # just the same, but reports "and  guarded stops" instead of naming the zero.
    starts=$(rg -c '^\s+run: \./scripts/ci-postgres\.sh start$' "$workflow_file" || true)
    stops=$(rg -c '^\s+run: \./scripts/ci-postgres\.sh stop$' "$workflow_file" || true)
    guarded_stops=$(rg -U -c "if: always\\(\\)( && matrix\\.lane == 'build')? && env\\.FOGELL_PG_CONTAINER != ''\\n\\s+run: \\./scripts/ci-postgres\\.sh stop" "$workflow_file" || true)
    starts=${starts:-0}; stops=${stops:-0}; guarded_stops=${guarded_stops:-0}
    [[ "$starts" = "$stops" && "$stops" = "$guarded_stops" ]] \
      || { refuse "$(basename "$workflow_file") has $starts PostgreSQL starts, $stops stops and $guarded_stops guarded stops; each must match"; return 1; }
    total_starts=$((total_starts + starts))
  done

  [[ "$workflow_count" -ge 2 ]] \
    || { refuse "expected at least two workflows under $candidate_workflows, found $workflow_count"; return 1; }
  # THE EXPECTED TOTAL IS NAMED, not derived. Per-file balance alone would
  # accept a workflow whose PostgreSQL lifecycle was deleted outright — zero
  # starts and zero stops balance perfectly. Four in gate.yml (build lane,
  # controller, hang-proof, database) and one in gate-mutants.yml (FG-251).
  [[ "$total_starts" = 5 ]] \
    || { refuse "expected five PostgreSQL lifecycles across all workflows, found $total_starts"; return 1; }

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
  if check_candidate "$scratch/$name-workflows" "$scratch/$name-postgres.sh" \
      "$scratch/$name-local-postgres.sh" "$scratch/$name-controller.sh" \
      "$scratch/$name-inotify.sh" >"$scratch/$name.log" 2>&1; then
    echo "FG-233 REFUSED: checker accepted planted $name defect" >&2
    exit 1
  fi
  rg -q -- "$expected" "$scratch/$name.log" \
    || { echo "FG-233 REFUSED: $name failed for the wrong reason: $(tr '\n' ' ' <"$scratch/$name.log")" >&2; exit 1; }
  printf '  killed: %s\n' "$name"
}

for name in service-container fixed-port missing-job-runtime missing-cleanup-guard local-fixed-port controller-fixed-port inotify-fixed-port local-readback controller-consumer inotify-consumer mutants-service-container mutants-unguarded-stop mutants-missing-runtime deleted-lifecycle; do
  mkdir -p "$scratch/$name-workflows"
  cp "$workflows_dir"/*.yml "$scratch/$name-workflows/"
  cp "$postgres" "$scratch/$name-postgres.sh"
  cp "$local_postgres" "$scratch/$name-local-postgres.sh"
  cp "$controller" "$scratch/$name-controller.sh"
  cp "$inotify" "$scratch/$name-inotify.sh"
done

sed -i '0,/^  lane:$/s//  lane:\n    services:\n      planted:\n        image: docker.io\/library\/postgres:16/' "$scratch/service-container-workflows/gate.yml"
expect_refusal service-container 'service containers select the runner runtime implicitly'

sed -i 's/127\.0\.0\.1::5432/127.0.0.1:55440:5432/' "$scratch/fixed-port-postgres.sh"
expect_refusal fixed-port 'does not request a runtime-allocated host port'

sed -i '0,/^  FOGELL_CONTAINER_RUNTIME: podman$/{/^  FOGELL_CONTAINER_RUNTIME: podman$/d;}' "$scratch/missing-job-runtime-workflows/gate.yml"
expect_refusal missing-job-runtime 'must select Podman exactly once'

sed -i "0,/if: always() && env.FOGELL_PG_CONTAINER != ''/s//if: always()/" "$scratch/missing-cleanup-guard-workflows/gate.yml"
expect_refusal missing-cleanup-guard 'gate.yml has 4 PostgreSQL starts, 4 stops and 3 guarded stops'

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

# THE SECOND WORKFLOW, PLANTED ON DIRECTLY. The three arms above mutate gate.yml
# and would all still pass if the discovery loop silently skipped every other
# file, so they do not prove the new coverage. These do: each defect is planted
# in gate-mutants.yml alone.
sed -i '0,/^  mutants:$/s//  mutants:\n    services:\n      planted:\n        image: index.io\/library\/postgres:16/' \
  "$scratch/mutants-service-container-workflows/gate-mutants.yml"
expect_refusal mutants-service-container 'service containers select the runner runtime implicitly \(gate-mutants.yml\)'

sed -i "0,/if: always() && env.FOGELL_PG_CONTAINER != ''/s//if: always()/" \
  "$scratch/mutants-unguarded-stop-workflows/gate-mutants.yml"
expect_refusal mutants-unguarded-stop 'gate-mutants.yml has 1 PostgreSQL starts, 1 stops and 0 guarded stops'

sed -i '0,/^  FOGELL_CONTAINER_RUNTIME: podman$/{/^  FOGELL_CONTAINER_RUNTIME: podman$/d;}' \
  "$scratch/mutants-missing-runtime-workflows/gate-mutants.yml"
expect_refusal mutants-missing-runtime 'must select Podman exactly once at global scope \(found 0 in gate-mutants.yml\)'

# A DELETED LIFECYCLE BALANCES PER FILE. Removing both the start and its guarded
# stop leaves gate-mutants.yml internally consistent at zero, and only the named
# cross-file total refuses it. Without this arm the total would be untested.
sed -i '/run: \.\/scripts\/ci-postgres\.sh start$/d; /run: \.\/scripts\/ci-postgres\.sh stop$/d' \
  "$scratch/deleted-lifecycle-workflows/gate-mutants.yml"
expect_refusal deleted-lifecycle 'expected five PostgreSQL lifecycles across all workflows, found 4'

check_candidate "$workflows_dir" "$postgres" "$local_postgres" "$controller" "$inotify"

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

echo "FG-233 PROOF PASS: across every workflow under .github/workflows, Podman is explicit, Actions services are absent, five jobs own guarded disposable PostgreSQL lifecycles, and hosted plus local host ports are runtime-allocated"
