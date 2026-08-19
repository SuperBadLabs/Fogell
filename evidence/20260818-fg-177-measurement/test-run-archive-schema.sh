#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

test_tmp=$(mktemp -d)
server_pid=''
custom_server_pid=''
cleanup() {
  for pid in "$server_pid" "$custom_server_pid"; do
    if [[ -n "$pid" ]]; then
      kill "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
    fi
  done
  rm -rf "$test_tmp"
}
trap cleanup EXIT
mkdir -p "$test_tmp/bin"
evidence='evidence/20260818-fg-177-measurement'
oracle_metadata="$test_tmp/oracle-metadata"
oracle_ready="$test_tmp/oracle-url"
oracle_state="$test_tmp/oracle-state"
python3 "$evidence/jenkins-oracle-fixture.py" metadata "$oracle_metadata"
FOGELL_FIXTURE_STATE_FILE="$oracle_state" \
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
printf 'oracle core %s\n' "${FOGELL_JENKINS_CORE-unset}" >> "$FOGELL_STUB_ORDER"
printf 'oracle image inspect\n' >> "$FOGELL_STUB_ORDER"
if [[ -n ${FOGELL_STUB_ORACLE_STATE:-} && -f "$FOGELL_STUB_ORACLE_STATE" &&
      $(<"$FOGELL_STUB_ORACLE_STATE") == image ]]; then
  printf '%s\n' 'fixture/jenkins:2.568.1|3333333333333333333333333333333333333333333333333333333333333333|sha256:4444444444444444444444444444444444444444444444444444444444444444'
  exit 0
fi
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
    printf '%s\n' "$@" > "${FOGELL_STUB_ARGS:-/dev/null}"
    while [[ $# -gt 0 && $1 != -- ]]; do shift; done
    shift
    shift 2
    receipt_dir=$1
    shift
    mkdir -p "$receipt_dir"
    mode=${FOGELL_STUB_MODE:-success}
    generation=${FOGELL_STUB_GENERATION:-one}
    if [[ "$mode" == zero ]]; then
      printf 'completed without receipts\n'
      exit 1
    fi
    case_file=$1
    receipt_name=$(basename "${case_file%.Jenkinsfile}.receipt.txt")
    printf 'fixture receipt %s for %s\n' "$generation" "$(basename "$case_file")" \
      > "$receipt_dir/$receipt_name"
    if [[ "$mode" == interrupt ]]; then
      printf '%s\n' "$$" > "$FOGELL_STUB_READY"
      trap 'exit 143' TERM
      while :; do sleep 0.02; done
    fi
    if [[ "$mode" == extra ]]; then
      printf 'unexpected\n' > "$receipt_dir/unexpected.receipt.txt"
    fi
    if [[ "$mode" == failure || "$mode" == partial ]]; then
      printf 'archive schema CLI infrastructure failure\n'
      exit 43
    fi
    printf 'archive schema CLI complete\n'
    if [[ -n ${FOGELL_STUB_ORACLE_DRIFT:-} ]]; then
      printf '%s\n' "$FOGELL_STUB_ORACLE_DRIFT" > "$FOGELL_STUB_ORACLE_STATE"
    fi
    exit 1
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
args="$test_tmp/dotnet-args"
sentinel_target="$out/runs/archive-schema"
mkdir -p "$sentinel_target/raw-receipts"
printf 'preexisting sentinel receipt\n' \
  > "$sentinel_target/raw-receipts/sentinel.receipt.txt"
printf 'preexisting sentinel manifest\n' > "$sentinel_target/sentinel.manifest"
set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_STUB_CALLS="$calls" \
FOGELL_STUB_ORDER="$order" \
FOGELL_STUB_ARGS="$args" \
FOGELL_STUB_ORACLE_STATE="$oracle_state" \
  bash "$evidence/run-archive-schema.sh"
rc=$?
set -e

if [[ $rc -ne 1 ]]; then
  echo "ERROR: expected completed archive runner rc 1, got $rc" >&2
  exit 1
fi
published="$out/runs/archive-schema"
if [[ ! -d "$published/raw-receipts" ]]; then
  echo "ERROR: standalone archive runner did not publish raw-receipts" >&2
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
if [[ $(<"$order") != $'oracle core 2.568.1\noracle image inspect\ndotnet build\ndotnet run\noracle core 2.568.1\noracle image inspect' ]]; then
  printf 'ERROR: archive verifier did not bracket build/run; order=%s\n' \
    "$(tr '\n' '|' < "$order")" >&2
  exit 1
fi
grep -Fx 'archive schema CLI complete' "$published/archive-schema-run.log"
grep -Fx 'archive-schema-cli-exit=1' "$published/archive-schema-exit.txt"
grep -Fx $'cli-exit\t1' "$published/archive-schema-run-manifest.tsv"
grep -Fx $'jenkins-core\t2.568.1' "$published/archive-schema-run-manifest.tsv"
grep -Fx $'format\tfogell-evidence-run-v2' "$published/archive-schema-run-manifest.tsv"
grep -F $'oracle-before-verification\toracle-before-verification.txt\t' \
  "$published/archive-schema-run-manifest.tsv"
grep -F $'oracle-after-verification\toracle-after-verification.txt\t' \
  "$published/archive-schema-run-manifest.tsv"
cmp "$published/oracle-before-verification.txt" \
  "$published/oracle-after-verification.txt"
grep -Fx $'case-count\t1' "$published/archive-schema-run-manifest.tsv"
grep -Fx '2.568.1' "$args"
if [[ $(find "$published/raw-receipts" -mindepth 1 -maxdepth 1 -type f \
    -printf '%f\n') != fg177-probe-archive-schema.receipt.txt ]]; then
  echo 'ERROR: successful archive publication has the wrong receipt set' >&2
  exit 1
fi
if find "$published" -name '*sentinel*' | grep -q .; then
  echo 'ERROR: successful archive publication retained sentinel evidence' >&2
  exit 1
fi

bundle_hash() {
  (
    cd "$1"
    find . -type f -print0 | sort -z | xargs -0 sha256sum
  ) | sha256sum | awk '{ print $1 }'
}
published_hash=$(bundle_hash "$published")

for drift in core plugin image transport; do
  rm -f "$oracle_state"
  set +e
  PATH="$test_tmp/bin:$PATH" \
  FOGELL_EVIDENCE_OUT="$out" \
  FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
  FOGELL_JENKINS_URL="$jenkins_url" \
  FOGELL_JENKINS_HOST=fixture-host \
  FOGELL_JENKINS_CONTAINER=fixture-controller \
  FOGELL_STUB_CALLS="$test_tmp/drift-$drift-calls" \
  FOGELL_STUB_ORDER="$test_tmp/drift-$drift-order" \
  FOGELL_STUB_ORACLE_STATE="$oracle_state" \
  FOGELL_STUB_ORACLE_DRIFT="$drift" \
    bash "$evidence/run-archive-schema.sh" > "$test_tmp/drift-$drift.log" 2>&1
  drift_rc=$?
  set -e
  if [[ $drift_rc -eq 0 || $(bundle_hash "$published") != "$published_hash" ]]; then
    printf 'ERROR: archive post-CLI %s drift rc=%s or prior bundle changed\n' \
      "$drift" "$drift_rc" >&2
    exit 1
  fi
done
rm -f "$oracle_state"

for mode in partial zero extra failure; do
  set +e
  PATH="$test_tmp/bin:$PATH" \
  FOGELL_EVIDENCE_OUT="$out" \
  FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
  FOGELL_JENKINS_URL="$jenkins_url" \
  FOGELL_JENKINS_HOST=fixture-host \
  FOGELL_JENKINS_CONTAINER=fixture-controller \
  FOGELL_STUB_CALLS="$test_tmp/$mode-calls" \
  FOGELL_STUB_ORDER="$test_tmp/$mode-order" \
  FOGELL_STUB_MODE="$mode" \
    bash "$evidence/run-archive-schema.sh" > "$test_tmp/$mode.log" 2>&1
  mode_rc=$?
  set -e
  if [[ "$mode" =~ ^(partial|failure)$ && $mode_rc -ne 43 ]] ||
     [[ "$mode" =~ ^(zero|extra)$ && $mode_rc -ne 1 ]]; then
    printf 'ERROR: archive mode %s returned unexpected rc %s\n' "$mode" "$mode_rc" >&2
    exit 1
  fi
  if [[ $(bundle_hash "$published") != "$published_hash" ]]; then
    printf 'ERROR: archive mode %s mutated prior publication\n' "$mode" >&2
    exit 1
  fi
done

interrupt_ready="$test_tmp/interrupt-ready"
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_STUB_CALLS="$test_tmp/interrupt-calls" \
FOGELL_STUB_ORDER="$test_tmp/interrupt-order" \
FOGELL_STUB_MODE=interrupt \
FOGELL_STUB_READY="$interrupt_ready" \
  bash "$evidence/run-archive-schema.sh" > "$test_tmp/interrupt.log" 2>&1 &
interrupt_pid=$!
for _ in {1..500}; do
  [[ -e "$interrupt_ready" ]] && break
  sleep 0.01
done
[[ -e "$interrupt_ready" ]] || {
  echo 'ERROR: interrupt archive run did not reach staged output' >&2
  exit 1
}
kill -TERM "$(<"$interrupt_ready")"
set +e
wait "$interrupt_pid"
interrupt_rc=$?
set -e
if [[ $interrupt_rc -ne 143 || $(bundle_hash "$published") != "$published_hash" ]]; then
  printf 'ERROR: interrupted archive rc=%s or prior publication changed\n' "$interrupt_rc" >&2
  exit 1
fi

set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_STUB_CALLS="$test_tmp/replacement-calls" \
FOGELL_STUB_ORDER="$test_tmp/replacement-order" \
FOGELL_STUB_GENERATION=two \
  bash "$evidence/run-archive-schema.sh" > "$test_tmp/replacement.log" 2>&1
replacement_rc=$?
set -e
if [[ $replacement_rc -ne 1 || $(bundle_hash "$published") == "$published_hash" ]]; then
  echo 'ERROR: complete replacement did not publish a new exact archive bundle' >&2
  exit 1
fi
grep -Fx 'fixture receipt two for fg177-probe-archive-schema.Jenkinsfile' \
  "$published/raw-receipts/fg177-probe-archive-schema.receipt.txt"

custom_core=2.777.3
custom_metadata="$test_tmp/custom-oracle-metadata"
custom_ready="$test_tmp/custom-oracle-url"
FOGELL_FIXTURE_JENKINS_CORE="$custom_core" \
  python3 "$evidence/jenkins-oracle-fixture.py" metadata "$custom_metadata"
FOGELL_FIXTURE_JENKINS_CORE="$custom_core" \
  python3 "$evidence/jenkins-oracle-fixture.py" serve "$custom_ready" &
custom_server_pid=$!
for _ in {1..100}; do
  [[ -s "$custom_ready" ]] && break
  sleep 0.01
done
[[ -s "$custom_ready" ]] || {
  echo 'ERROR: custom-core archive oracle fixture did not start' >&2
  exit 1
}
custom_url=$(<"$custom_ready")
custom_out="$test_tmp/custom-core-output"
custom_args="$test_tmp/custom-core-args"
custom_calls="$test_tmp/custom-core-calls"
custom_order="$test_tmp/custom-core-order"
set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$custom_out" \
FOGELL_JENKINS_ORACLE_DIR="$custom_metadata" \
FOGELL_JENKINS_URL="$custom_url" \
FOGELL_JENKINS_CORE="$custom_core" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_STUB_ARGS="$custom_args" \
FOGELL_STUB_CALLS="$custom_calls" \
FOGELL_STUB_ORDER="$custom_order" \
  bash "$evidence/run-archive-schema.sh" > "$test_tmp/custom-core.log" 2>&1
custom_rc=$?
set -e
if [[ $custom_rc -ne 1 ]]; then
  printf 'ERROR: custom matching archive core returned %s\n' "$custom_rc" >&2
  exit 1
fi
grep -Fx "oracle core $custom_core" "$custom_order"
grep -Fx "$custom_core" "$custom_args"
grep -Fx $'jenkins-core\t'"$custom_core" \
  "$custom_out/runs/archive-schema/archive-schema-run-manifest.tsv"

require_archive_core_refusal() {
  local label=$1
  local expected_rc=$2
  local core_mode=$3
  local core_value=${4-}
  local refused_out="$test_tmp/core-refused-$label"
  local refused_calls="$test_tmp/core-refused-$label-calls"
  local refused_order="$test_tmp/core-refused-$label-order"
  local core_command=()
  if [[ "$core_mode" == unset ]]; then
    core_command=(env -u FOGELL_JENKINS_CORE)
  else
    core_command=(env "FOGELL_JENKINS_CORE=$core_value")
  fi
  set +e
  "${core_command[@]}" \
    PATH="$test_tmp/bin:$PATH" \
    FOGELL_EVIDENCE_OUT="$refused_out" \
    FOGELL_JENKINS_ORACLE_DIR="$custom_metadata" \
    FOGELL_JENKINS_URL="$custom_url" \
    FOGELL_JENKINS_HOST=fixture-host \
    FOGELL_JENKINS_CONTAINER=fixture-controller \
    FOGELL_STUB_CALLS="$refused_calls" \
    FOGELL_STUB_ORDER="$refused_order" \
      bash "$evidence/run-archive-schema.sh" > "$test_tmp/core-refused-$label.log" 2>&1
  local refused_rc=$?
  set -e
  if [[ $refused_rc -ne $expected_rc || -e "$refused_out" ||
        -e "$refused_calls" || -e "$refused_order" ]]; then
    printf 'ERROR: archive core refusal %s rc=%s reached oracle image/output/dotnet\n' \
      "$label" "$refused_rc" >&2
    exit 1
  fi
}

require_archive_core_refusal unset-default-mismatch 1 unset
require_archive_core_refusal caller-mismatch 1 set 2.999
require_archive_core_refusal explicit-empty 2 set ''
require_archive_core_refusal malformed 2 set version-next

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
if [[ -e "$build_failure_out/runs/archive-schema" ]]; then
  echo 'ERROR: failed archive build published an evidence bundle' >&2
  exit 1
fi

printf 'archive transaction proof: partial/zero/extra/failure/interruption retain prior bundle; success atomically replaces exact set\n'
