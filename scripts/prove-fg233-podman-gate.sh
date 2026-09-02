#!/usr/bin/env bash
# FG-233 — prove the hosted gate chooses Podman explicitly and never returns
# to a runner-global PostgreSQL port. This is a static workflow boundary; the
# hosted controller, hang, build, and database jobs are its live runtime proof.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
workflow="$repo/.github/workflows/gate.yml"
postgres="$repo/scripts/ci-postgres.sh"
scratch=$(mktemp -d /tmp/fogell-fg233-proof.XXXXXX)
trap 'rm -rf -- "$scratch"' EXIT

refuse() {
  printf 'FG-233 REFUSED: %s\n' "$*" >&2
  return 1
}

check_candidate() {
  local candidate_workflow=$1
  local candidate_postgres=$2
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
}

expect_refusal() {
  local name=$1 expected=$2
  if check_candidate "$scratch/$name-gate.yml" "$scratch/$name-postgres.sh" >"$scratch/$name.log" 2>&1; then
    echo "FG-233 REFUSED: checker accepted planted $name defect" >&2
    exit 1
  fi
  rg -q -- "$expected" "$scratch/$name.log" \
    || { echo "FG-233 REFUSED: $name failed for the wrong reason: $(tr '\n' ' ' <"$scratch/$name.log")" >&2; exit 1; }
  printf '  killed: %s\n' "$name"
}

for name in service-container fixed-port missing-job-runtime missing-cleanup-guard; do
  cp "$workflow" "$scratch/$name-gate.yml"
  cp "$postgres" "$scratch/$name-postgres.sh"
done

sed -i '0,/^jobs:$/s//  services:\n    planted:\njobs:/' "$scratch/service-container-gate.yml"
expect_refusal service-container 'service containers select the runner runtime implicitly'

sed -i 's/127\.0\.0\.1::5432/127.0.0.1:55440:5432/' "$scratch/fixed-port-postgres.sh"
expect_refusal fixed-port 'does not request a runtime-allocated host port'

sed -i '0,/^  FOGELL_CONTAINER_RUNTIME: podman$/d' "$scratch/missing-job-runtime-gate.yml"
expect_refusal missing-job-runtime 'must select Podman exactly once'

sed -i "0,/if: always() && env.FOGELL_PG_CONTAINER != ''/s//if: always()/" "$scratch/missing-cleanup-guard-gate.yml"
expect_refusal missing-cleanup-guard 'every PostgreSQL cleanup must require an exported container name'

check_candidate "$workflow" "$postgres"
echo "FG-233 PROOF PASS: Podman is explicit, Actions services are absent, four jobs own guarded disposable PostgreSQL lifecycles, and every host port is runtime-allocated"
