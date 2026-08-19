#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

evidence='evidence/20260818-fg-177-measurement'
fixture_tmp=$(mktemp -d)
server_pid=''
custom_server_pid=''
cleanup() {
  for pid in "$server_pid" "$custom_server_pid"; do
    if [[ -n "$pid" ]]; then
      kill "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
    fi
  done
  rm -rf "$fixture_tmp"
}
trap cleanup EXIT
remote="$fixture_tmp/fixture.git"
seed="$fixture_tmp/seed"
rendered="$fixture_tmp/rendered"
oracle_metadata="$fixture_tmp/oracle-metadata"
oracle_ready="$fixture_tmp/oracle-url"
oracle_state="$fixture_tmp/oracle-state"
PYTHONDONTWRITEBYTECODE=1 python3 "$evidence/test-render-probe-cases.py"
mkdir -p "$fixture_tmp/bin"
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
cat > "$fixture_tmp/bin/ssh" <<'EOF'
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
chmod +x "$fixture_tmp/bin/ssh"

require_fixed_count() {
  local mode=$1
  local expected=$2
  local needle=$3
  local file=$4
  local diagnostic=$5
  local count rc

  if count=$(grep "$mode" -- "$needle" "$file"); then
    rc=0
  else
    rc=$?
  fi
  if [[ $rc -gt 1 ]]; then
    printf 'ERROR: %s (grep rc=%s)\n' "$diagnostic" "$rc" >&2
    return "$rc"
  fi
  [[ $rc -eq 1 ]] && count=0
  if [[ $count -ne $expected ]]; then
    printf 'ERROR: %s (found %s, expected %s)\n' \
      "$diagnostic" "$count" "$expected" >&2
    return 1
  fi
}

git init -q --bare "$remote"
git init -q "$seed"
printf 'fixture\n' > "$seed/README"
git -C "$seed" add README
git -C "$seed" \
  -c user.email=harness@fogell \
  -c user.name=fogell-harness \
  commit -qm 'fixture: initial main'
git -C "$seed" branch -M main
git -C "$seed" remote add origin "$remote"
git -C "$seed" push -q origin main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main

scm_url="file://$remote"
FOGELL_SCM_URL="$scm_url" \
FOGELL_RENDERED_CASES_DIR="$rendered" \
  python3 "$evidence/render-probe-cases.py"

# Known-bad proof: grep returns 1 for zero matches, but that status must reach
# our count assertion and its diagnostic rather than aborting under `set -e`.
planted_missing="$fixture_tmp/planted-missing-url"
printf 'no configured URL here\n' > "$planted_missing"
planted_diagnostic='planted rendered case lacks its configured URL'
if planted_output=$(
  require_fixed_count -Foc 1 "$scm_url" "$planted_missing" "$planted_diagnostic" 2>&1
); then
  echo 'ERROR: planted zero-match count unexpectedly passed' >&2
  exit 1
else
  planted_rc=$?
fi
expected_planted="ERROR: $planted_diagnostic (found 0, expected 1)"
if [[ $planted_rc -ne 1 || "$planted_output" != "$expected_planted" ]]; then
  printf 'ERROR: planted zero-match proof missed custom diagnostic; rc=%s output=%s\n' \
    "$planted_rc" "$planted_output" >&2
  exit 1
fi

missing_file="$fixture_tmp/does-not-exist"
missing_diagnostic='planted grep read failure propagates'
if missing_output=$(
  require_fixed_count -Foc 1 "$scm_url" "$missing_file" "$missing_diagnostic" 2>&1
); then
  echo 'ERROR: planted grep read failure unexpectedly passed' >&2
  exit 1
else
  missing_rc=$?
fi
expected_missing="ERROR: $missing_diagnostic (grep rc=2)"
if [[ $missing_rc -ne 2 || "$missing_output" != *"$expected_missing"* ]]; then
  printf 'ERROR: planted grep read failure lost rc/diagnostic; rc=%s output=%s\n' \
    "$missing_rc" "$missing_output" >&2
  exit 1
fi

git ls-remote --exit-code "$scm_url" refs/heads/main >/dev/null
(
  cd "$rendered"
  sha256sum -c SHA256SUMS
)

for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile \
  fg177-plan-git-history.Jenkinsfile
do
  expected_urls=1
  if [[ "$name" == fg177-plan-git-history.Jenkinsfile ]]; then
    expected_urls=2
  fi
  require_fixed_count -Foc "$expected_urls" "$scm_url" "$rendered/$name" \
    "rendered $name does not contain exactly $expected_urls configured URLs"
  if grep -Fq '@@FOGELL_SCM_URL@@' "$rendered/$name"; then
    echo "ERROR: rendered $name retains its SCM token" >&2
    exit 1
  fi
  if grep -Fq 'git://100.105.179.51/repo.git' "$rendered/$name"; then
    echo "ERROR: rendered $name retains the default fixture URL" >&2
    exit 1
  fi
done

for name in \
  fg177-probe-requiredness.Jenkinsfile \
  fg177-probe-checkout-scm.Jenkinsfile
do
  cmp "$evidence/cases/$name" "$rendered/$name"
done

require_fixed_count -Fxc 1 '//// NEXT BUILD ////' \
  "$rendered/fg177-plan-git-history.Jenkinsfile" \
  'retained-history plan is not exactly two builds'

# Exercise the actual runner from a fresh fixture. A fake CLI proves that all
# paths handed to it are exact rendered files and that its status is preserved.
stub="$fixture_tmp/bin/dotnet"
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
    printf '%s\n' "$@" > "$FOGELL_STUB_ARGS"
    while [[ $# -gt 0 && $1 != -- ]]; do shift; done
    shift
    shift 2
    receipt_dir=$1
    shift
    mkdir -p "$receipt_dir"
    mode=${FOGELL_STUB_MODE:-success}
    generation=${FOGELL_STUB_GENERATION:-one}
    case "$mode" in
      zero)
        printf 'completed without receipts\n'
        exit 1
        ;;
      partial|interrupt)
        case_file=$1
        receipt_name=$(basename "${case_file%.Jenkinsfile}.receipt.txt")
        printf 'fixture receipt %s for %s\n' "$generation" "$(basename "$case_file")" \
          > "$receipt_dir/$receipt_name"
        if [[ "$mode" == interrupt ]]; then
          printf '%s\n' "$$" > "$FOGELL_STUB_READY"
          trap 'exit 143' TERM
          while :; do sleep 0.02; done
        fi
        printf 'partial probe CLI failure\n'
        exit 44
        ;;
    esac
    for case_file in "$@"; do
      receipt_name=$(basename "${case_file%.Jenkinsfile}.receipt.txt")
      printf 'fixture receipt %s for %s\n' "$generation" "$(basename "$case_file")" \
        > "$receipt_dir/$receipt_name"
    done
    if [[ "$mode" == extra ]]; then
      printf 'unexpected\n' > "$receipt_dir/unexpected.receipt.txt"
    fi
    if [[ "$mode" == failure ]]; then
      printf 'probe CLI infrastructure failure\n'
      exit 45
    fi
    printf 'probe CLI complete\n'
    if [[ -n ${FOGELL_STUB_ORACLE_DRIFT:-} ]]; then
      if [[ $FOGELL_STUB_ORACLE_DRIFT == plugin-recapture ]]; then
        printf 'plugin\n' > "$FOGELL_STUB_ORACLE_STATE"
        sed 's/beta\t3.4/beta\t9.9/' \
          "$FOGELL_JENKINS_ORACLE_DIR/jenkins-plugins.tsv" \
          > "$FOGELL_JENKINS_ORACLE_DIR/jenkins-plugins.tsv.next"
        mv "$FOGELL_JENKINS_ORACLE_DIR/jenkins-plugins.tsv.next" \
          "$FOGELL_JENKINS_ORACLE_DIR/jenkins-plugins.tsv"
      else
        printf '%s\n' "$FOGELL_STUB_ORACLE_DRIFT" > "$FOGELL_STUB_ORACLE_STATE"
      fi
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
runner_out="$fixture_tmp/runner-output"
args="$fixture_tmp/dotnet-args"
calls="$fixture_tmp/dotnet-calls"
order="$fixture_tmp/runner-order"
sentinel_target="$runner_out/runs/probes"
mkdir -p "$sentinel_target/raw-receipts"
printf 'preexisting sentinel receipt\n' \
  > "$sentinel_target/raw-receipts/sentinel.receipt.txt"
printf 'preexisting sentinel manifest\n' > "$sentinel_target/sentinel.manifest"
set +e
PATH="$fixture_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$runner_out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_SCM_URL="$scm_url" \
FOGELL_STUB_ARGS="$args" \
FOGELL_STUB_CALLS="$calls" \
FOGELL_STUB_ORDER="$order" \
FOGELL_STUB_ORACLE_STATE="$oracle_state" \
  bash "$evidence/run-probes.sh"
rc=$?
set -e
if [[ $rc -ne 1 ]]; then
  echo "ERROR: expected completed probe runner rc 1, got $rc" >&2
  exit 1
fi
published="$runner_out/runs/probes"
expected_build='build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --nologo'
expected_run_prefix='run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --no-build -- '
if [[ $(sed -n '1p' "$calls") != "$expected_build" ]]; then
  printf 'ERROR: probe runner did not build first; calls=%s\n' "$(tr '\n' '|' < "$calls")" >&2
  exit 1
fi
if [[ $(sed -n '2p' "$calls") != "$expected_run_prefix"* ]]; then
  printf 'ERROR: probe runner did not execute the prebuilt CLI second; calls=%s\n' \
    "$(tr '\n' '|' < "$calls")" >&2
  exit 1
fi
if [[ $(wc -l < "$calls") -ne 2 ]]; then
  printf 'ERROR: probe runner invoked dotnet more than build+run; calls=%s\n' \
    "$(tr '\n' '|' < "$calls")" >&2
  exit 1
fi
if [[ $(<"$order") != $'oracle core 2.568.1\noracle image inspect\ndotnet build\ndotnet run\noracle core 2.568.1\noracle image inspect' ]]; then
  printf 'ERROR: verifier did not bracket build/run; order=%s\n' \
    "$(tr '\n' '|' < "$order")" >&2
  exit 1
fi
grep -Fx 'probe CLI complete' "$published/probe-run.log"
grep -Fx 'probe-cli-exit=1' "$published/probe-exit.txt"
grep -Fx '2.568.1' "$args"
grep -E "^$runner_out/runs/\\.probes-stage\\.[^/]+/raw-receipts$" "$args"
for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-requiredness.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile \
  fg177-probe-checkout-scm.Jenkinsfile
do
  grep -E "^$runner_out/runs/\\.probes-stage\\.[^/]+/rendered-cases/$name$" "$args"
done
for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile
do
  require_fixed_count -Foc 1 "$scm_url" \
    "$published/rendered-cases/$name" \
    "runner-rendered $name does not contain exactly one configured URL"
done
cmp "$evidence/cases/fg177-probe-checkout-scm.Jenkinsfile" \
  "$published/rendered-cases/fg177-probe-checkout-scm.Jenkinsfile"
grep -Fx $'cli-exit\t1' "$published/probe-run-manifest.tsv"
grep -Fx $'jenkins-core\t2.568.1' "$published/probe-run-manifest.tsv"
grep -Fx $'format\tfogell-evidence-run-v2' "$published/probe-run-manifest.tsv"
grep -F $'oracle-before-verification\toracle-before-verification.txt\t' \
  "$published/probe-run-manifest.tsv"
grep -F $'oracle-after-verification\toracle-after-verification.txt\t' \
  "$published/probe-run-manifest.tsv"
cmp "$published/oracle-before-verification.txt" \
  "$published/oracle-after-verification.txt"
grep -Fx $'case-count\t4' "$published/probe-run-manifest.tsv"
if find "$published/raw-receipts" -mindepth 1 -maxdepth 1 -type f \
    -printf '%f\n' | sort | diff -u - <(printf '%s\n' \
      fg177-probe-checkout-scm.receipt.txt \
      fg177-probe-requiredness.receipt.txt \
      fg177-probe-return-semantics.receipt.txt \
      fg177-probe-unknown-policy.receipt.txt); then
  :
else
  echo 'ERROR: successful probe publication has the wrong receipt set' >&2
  exit 1
fi
if find "$published" -name '*sentinel*' | grep -q .; then
  echo 'ERROR: successful probe publication retained sentinel evidence' >&2
  exit 1
fi

bundle_hash() {
  (
    cd "$1"
    find . -type f -print0 | sort -z | xargs -0 sha256sum
  ) | sha256sum | awk '{ print $1 }'
}

published_hash=$(bundle_hash "$published")
for drift in core plugin image transport plugin-recapture; do
  python3 "$evidence/jenkins-oracle-fixture.py" metadata "$oracle_metadata"
  rm -f "$oracle_state"
  set +e
  PATH="$fixture_tmp/bin:$PATH" \
  FOGELL_EVIDENCE_OUT="$runner_out" \
  FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
  FOGELL_JENKINS_URL="$jenkins_url" \
  FOGELL_JENKINS_HOST=fixture-host \
  FOGELL_JENKINS_CONTAINER=fixture-controller \
  FOGELL_SCM_URL="$scm_url" \
  FOGELL_STUB_ARGS="$fixture_tmp/drift-$drift-args" \
  FOGELL_STUB_CALLS="$fixture_tmp/drift-$drift-calls" \
  FOGELL_STUB_ORDER="$fixture_tmp/drift-$drift-order" \
  FOGELL_STUB_ORACLE_STATE="$oracle_state" \
  FOGELL_STUB_ORACLE_DRIFT="$drift" \
    bash "$evidence/run-probes.sh" > "$fixture_tmp/drift-$drift.log" 2>&1
  drift_rc=$?
  set -e
  if [[ $drift_rc -eq 0 || $(bundle_hash "$published") != "$published_hash" ]]; then
    printf 'ERROR: probe post-CLI %s drift rc=%s or prior bundle changed\n' \
      "$drift" "$drift_rc" >&2
    exit 1
  fi
  if [[ $drift == plugin-recapture ]] &&
     ! grep -Fq 'Jenkins oracle identity changed during probe CLI' \
       "$fixture_tmp/drift-$drift.log"; then
    echo 'ERROR: same-count plugin metadata recapture missed receipt comparison' >&2
    exit 1
  fi
done
python3 "$evidence/jenkins-oracle-fixture.py" metadata "$oracle_metadata"
rm -f "$oracle_state"
for mode in partial zero extra failure; do
  mode_calls="$fixture_tmp/$mode-calls"
  mode_order="$fixture_tmp/$mode-order"
  set +e
  PATH="$fixture_tmp/bin:$PATH" \
  FOGELL_EVIDENCE_OUT="$runner_out" \
  FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
  FOGELL_JENKINS_URL="$jenkins_url" \
  FOGELL_JENKINS_HOST=fixture-host \
  FOGELL_JENKINS_CONTAINER=fixture-controller \
  FOGELL_SCM_URL="$scm_url" \
  FOGELL_STUB_ARGS="$fixture_tmp/$mode-args" \
  FOGELL_STUB_CALLS="$mode_calls" \
  FOGELL_STUB_ORDER="$mode_order" \
  FOGELL_STUB_MODE="$mode" \
    bash "$evidence/run-probes.sh" > "$fixture_tmp/$mode.log" 2>&1
  mode_rc=$?
  set -e
  if [[ "$mode" == partial && $mode_rc -ne 44 ]] ||
     [[ "$mode" == failure && $mode_rc -ne 45 ]] ||
     [[ "$mode" =~ ^(zero|extra)$ && $mode_rc -ne 1 ]]; then
    printf 'ERROR: probe mode %s returned unexpected rc %s\n' "$mode" "$mode_rc" >&2
    exit 1
  fi
  if [[ $(bundle_hash "$published") != "$published_hash" ]]; then
    printf 'ERROR: probe mode %s mutated prior publication\n' "$mode" >&2
    exit 1
  fi
done

# An interruption after a partial receipt is staged must not expose it.
interrupt_ready="$fixture_tmp/interrupt-ready"
PATH="$fixture_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$runner_out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_SCM_URL="$scm_url" \
FOGELL_STUB_ARGS="$fixture_tmp/interrupt-args" \
FOGELL_STUB_CALLS="$fixture_tmp/interrupt-calls" \
FOGELL_STUB_ORDER="$fixture_tmp/interrupt-order" \
FOGELL_STUB_MODE=interrupt \
FOGELL_STUB_READY="$interrupt_ready" \
  bash "$evidence/run-probes.sh" > "$fixture_tmp/interrupt.log" 2>&1 &
interrupt_pid=$!
for _ in {1..500}; do
  [[ -e "$interrupt_ready" ]] && break
  sleep 0.01
done
[[ -e "$interrupt_ready" ]] || {
  echo 'ERROR: interrupt probe did not reach staged partial output' >&2
  exit 1
}
kill -TERM "$(<"$interrupt_ready")"
set +e
wait "$interrupt_pid"
interrupt_rc=$?
set -e
if [[ $interrupt_rc -ne 143 || $(bundle_hash "$published") != "$published_hash" ]]; then
  printf 'ERROR: interrupted probe rc=%s or prior publication changed\n' "$interrupt_rc" >&2
  exit 1
fi

# A second complete run replaces the exact bundle rather than mixing receipts.
set +e
PATH="$fixture_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$runner_out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_SCM_URL="$scm_url" \
FOGELL_STUB_ARGS="$fixture_tmp/replacement-args" \
FOGELL_STUB_CALLS="$fixture_tmp/replacement-calls" \
FOGELL_STUB_ORDER="$fixture_tmp/replacement-order" \
FOGELL_STUB_GENERATION=two \
  bash "$evidence/run-probes.sh" > "$fixture_tmp/replacement.log" 2>&1
replacement_rc=$?
set -e
if [[ $replacement_rc -ne 1 || $(bundle_hash "$published") == "$published_hash" ]]; then
  echo 'ERROR: complete replacement did not publish a new exact probe bundle' >&2
  exit 1
fi
for receipt in "$published"/raw-receipts/*.receipt.txt; do
  if ! grep -Fq 'fixture receipt two for ' "$receipt"; then
    printf 'ERROR: replacement probe receipt is not from generation two: %s\n' \
      "$receipt" >&2
    exit 1
  fi
done

# A caller-selected canonical core must reach the verifier child, CLI and
# manifest unchanged. Unset defaults to 2.568.1; against this custom pin that
# is a mismatch. Empty/malformed values refuse even before oracle I/O.
custom_core=2.777.3
custom_metadata="$fixture_tmp/custom-oracle-metadata"
custom_ready="$fixture_tmp/custom-oracle-url"
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
  echo 'ERROR: custom-core Jenkins oracle fixture did not start' >&2
  exit 1
}
custom_url=$(<"$custom_ready")
custom_out="$fixture_tmp/custom-core-output"
custom_args="$fixture_tmp/custom-core-args"
custom_calls="$fixture_tmp/custom-core-calls"
custom_order="$fixture_tmp/custom-core-order"
set +e
PATH="$fixture_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$custom_out" \
FOGELL_JENKINS_ORACLE_DIR="$custom_metadata" \
FOGELL_JENKINS_URL="$custom_url" \
FOGELL_JENKINS_CORE="$custom_core" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_SCM_URL="$scm_url" \
FOGELL_STUB_ARGS="$custom_args" \
FOGELL_STUB_CALLS="$custom_calls" \
FOGELL_STUB_ORDER="$custom_order" \
  bash "$evidence/run-probes.sh" > "$fixture_tmp/custom-core.log" 2>&1
custom_rc=$?
set -e
if [[ $custom_rc -ne 1 ]]; then
  printf 'ERROR: custom matching probe core returned %s\n' "$custom_rc" >&2
  exit 1
fi
grep -Fx "oracle core $custom_core" "$custom_order"
grep -Fx "$custom_core" "$custom_args"
grep -Fx $'jenkins-core\t'"$custom_core" \
  "$custom_out/runs/probes/probe-run-manifest.tsv"

require_probe_core_refusal() {
  local label=$1
  local expected_rc=$2
  local core_mode=$3
  local core_value=${4-}
  local refused_out="$fixture_tmp/core-refused-$label"
  local refused_calls="$fixture_tmp/core-refused-$label-calls"
  local refused_order="$fixture_tmp/core-refused-$label-order"
  local core_command=()
  if [[ "$core_mode" == unset ]]; then
    core_command=(env -u FOGELL_JENKINS_CORE)
  else
    core_command=(env "FOGELL_JENKINS_CORE=$core_value")
  fi
  set +e
  "${core_command[@]}" \
    PATH="$fixture_tmp/bin:$PATH" \
    FOGELL_EVIDENCE_OUT="$refused_out" \
    FOGELL_JENKINS_ORACLE_DIR="$custom_metadata" \
    FOGELL_JENKINS_URL="$custom_url" \
    FOGELL_JENKINS_HOST=fixture-host \
    FOGELL_JENKINS_CONTAINER=fixture-controller \
    FOGELL_SCM_URL="$scm_url" \
    FOGELL_STUB_ARGS="$fixture_tmp/core-refused-$label-args" \
    FOGELL_STUB_CALLS="$refused_calls" \
    FOGELL_STUB_ORDER="$refused_order" \
      bash "$evidence/run-probes.sh" > "$fixture_tmp/core-refused-$label.log" 2>&1
  local refused_rc=$?
  set -e
  if [[ $refused_rc -ne $expected_rc || -e "$refused_out" ||
        -e "$refused_calls" || -e "$refused_order" ]]; then
    printf 'ERROR: core refusal %s rc=%s reached oracle image/output/dotnet\n' \
      "$label" "$refused_rc" >&2
    exit 1
  fi
}

require_probe_core_refusal unset-default-mismatch 1 unset
require_probe_core_refusal caller-mismatch 1 set 2.999
require_probe_core_refusal explicit-empty 2 set ''
require_probe_core_refusal malformed 2 set version-next

# A fresh-checkout build failure must propagate before the CLI is attempted.
build_failure_out="$fixture_tmp/build-failure-output"
build_failure_calls="$fixture_tmp/build-failure-calls"
set +e
PATH="$fixture_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$build_failure_out" \
FOGELL_JENKINS_ORACLE_DIR="$oracle_metadata" \
FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_HOST=fixture-host \
FOGELL_JENKINS_CONTAINER=fixture-controller \
FOGELL_SCM_URL="$scm_url" \
FOGELL_STUB_ARGS="$fixture_tmp/build-failure-args" \
FOGELL_STUB_CALLS="$build_failure_calls" \
FOGELL_STUB_ORDER="$fixture_tmp/build-failure-order" \
FOGELL_STUB_BUILD_RC=31 \
  bash "$evidence/run-probes.sh" > "$fixture_tmp/build-failure.log" 2>&1
build_failure_rc=$?
set -e
if [[ $build_failure_rc -ne 31 ]]; then
  printf 'ERROR: expected probe build rc 31, got %s\n' "$build_failure_rc" >&2
  exit 1
fi
if [[ $(sed -n '1p' "$build_failure_calls") != "$expected_build" || \
      $(wc -l < "$build_failure_calls") -ne 1 ]]; then
  printf 'ERROR: failed probe build did not stop before CLI; calls=%s\n' \
    "$(tr '\n' '|' < "$build_failure_calls")" >&2
  exit 1
fi
if [[ -e "$build_failure_out/runs/probes" ]]; then
  echo 'ERROR: failed probe build published an evidence bundle' >&2
  exit 1
fi

printf 'probe transaction proof: partial/zero/extra/failure/interruption retain prior bundle; success atomically replaces exact set\n'
printf 'rendered probe cases verified with fresh local fixture %s\n' "$scm_url"
