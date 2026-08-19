#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

test_tmp=$(mktemp -d)
server_pid=''
cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$test_tmp"
}
trap cleanup EXIT
mkdir -p "$test_tmp/bin"
evidence='evidence/20260818-fg-177-measurement'
oracle_metadata="$test_tmp/oracle-metadata"
oracle_ready="$test_tmp/oracle-url"
python3 "$evidence/jenkins-oracle-fixture.py" metadata "$oracle_metadata"
python3 "$evidence/jenkins-oracle-fixture.py" serve "$oracle_ready" &
server_pid=$!
for _ in {1..100}; do
  [[ -s "$oracle_ready" ]] && break
  sleep 0.01
done
[[ -s "$oracle_ready" ]] || {
  echo 'ERROR: local Jenkins oracle fixture did not start' >&2
  exit 1
}
jenkins_url=$(<"$oracle_ready")
cat > "$test_tmp/bin/ssh" <<'EOF'
#!/usr/bin/env bash
printf 'oracle image inspect\n' >> "$FOGELL_STUB_ORDER"
printf '%s\n' 'fixture/jenkins:2.568.1|1111111111111111111111111111111111111111111111111111111111111111|sha256:2222222222222222222222222222222222222222222222222222222222222222'
EOF
chmod +x "$test_tmp/bin/ssh"
stub="$test_tmp/bin/dotnet"
cat > "$stub" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$FOGELL_STUB_CALLS"
case "$1" in
  build)
    printf 'dotnet build\n' >> "$FOGELL_STUB_ORDER"
    printf 'stub Release build\n'
    exit "${FOGELL_STUB_BUILD_RC:-0}"
    ;;
  run)
    printf 'dotnet run\n' >> "$FOGELL_STUB_ORDER"
    while [[ $# -gt 0 && $1 != -- ]]; do shift; done
    shift
    shift 2
    receipt_dir=$1
    shift
    mkdir -p "$receipt_dir"
    for case_file in "$@"; do
      receipt_name=$(basename "${case_file%.Jenkinsfile}.receipt.txt")
      printf 'fixture receipt for %s\n' "$(basename "$case_file")" \
        > "$receipt_dir/$receipt_name"
    done
    printf 'intentional archive schema CLI failure\n'
    exit 23
    ;;
  *)
    printf 'ERROR: unexpected dotnet command: %s\n' "$1" >&2
    exit 97
    ;;
esac
EOF
chmod +x "$stub"

out="$test_tmp/fresh-evidence-output"
calls="$test_tmp/dotnet-calls"
order="$test_tmp/runner-order"
set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_STUB_CALLS="$calls" \
FOGELL_STUB_ORDER="$order" \
  bash "$evidence/run-archive-schema.sh"
rc=$?
set -e

if [[ $rc -ne 23 ]]; then
  echo "ERROR: expected standalone archive runner rc 23, got $rc" >&2
  exit 1
fi
if [[ ! -d "$out/raw-receipts" ]]; then
  echo "ERROR: standalone archive runner did not create raw-receipts" >&2
  exit 1
fi
expected_build='build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --nologo'
expected_run_prefix='run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --no-build -- '
if [[ $(sed -n '1p' "$calls") != "$expected_build" || \
      $(sed -n '2p' "$calls") != "$expected_run_prefix"* || \
      $(wc -l < "$calls") -ne 2 ]]; then
  printf 'ERROR: archive runner did not build once before CLI; calls=%s\n' \
    "$(tr '\n' '|' < "$calls")" >&2
  exit 1
fi
if [[ $(<"$order") != $'oracle image inspect\ndotnet build\ndotnet run' ]]; then
  printf 'ERROR: archive verifier did not finish before build/run; order=%s\n' \
    "$(tr '\n' '|' < "$order")" >&2
  exit 1
fi
grep -Fx 'intentional archive schema CLI failure' "$out/archive-schema-run.log"
grep -Fx 'archive-schema-cli-exit=23' "$out/archive-schema-exit.txt"
grep -Fx $'cli-exit\t23' "$out/archive-schema-run-manifest.tsv"
grep -Fx $'jenkins-core\t2.568.1' "$out/archive-schema-run-manifest.tsv"
grep -Fx $'case-count\t1' "$out/archive-schema-run-manifest.tsv"

build_failure_out="$test_tmp/build-failure-output"
build_failure_calls="$test_tmp/build-failure-calls"
set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$build_failure_out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_STUB_CALLS="$build_failure_calls" \
FOGELL_STUB_ORDER="$test_tmp/build-failure-order" \
FOGELL_STUB_BUILD_RC=37 \
  bash "$evidence/run-archive-schema.sh" \
  > "$test_tmp/build-failure.log" 2>&1
build_failure_rc=$?
set -e
if [[ $build_failure_rc -ne 37 ]]; then
  printf 'ERROR: expected archive build rc 37, got %s\n' "$build_failure_rc" >&2
  exit 1
fi
if [[ $(sed -n '1p' "$build_failure_calls") != "$expected_build" || \
      $(wc -l < "$build_failure_calls") -ne 1 ]]; then
  printf 'ERROR: failed archive build did not stop before CLI; calls=%s\n' \
    "$(tr '\n' '|' < "$build_failure_calls")" >&2
  exit 1
fi
if [[ -e "$build_failure_out/archive-schema-exit.txt" ]]; then
  echo 'ERROR: failed archive build wrote a misleading CLI exit marker' >&2
  exit 1
fi

printf 'standalone archive runner created raw-receipts and propagated rc=%s\n' "$rc"
