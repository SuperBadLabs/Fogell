#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

evidence='evidence/20260818-fg-177-measurement'
fixture_tmp=$(mktemp -d)
server_pid=''
cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$fixture_tmp"
}
trap cleanup EXIT
remote="$fixture_tmp/fixture.git"
seed="$fixture_tmp/seed"
rendered="$fixture_tmp/rendered"
oracle_metadata="$fixture_tmp/oracle-metadata"
oracle_ready="$fixture_tmp/oracle-url"
mkdir -p "$fixture_tmp/bin"
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
cat > "$fixture_tmp/bin/ssh" <<'EOF'
#!/usr/bin/env bash
printf 'oracle image inspect\n' >> "$FOGELL_STUB_ORDER"
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
    for case_file in "$@"; do
      receipt_name=$(basename "${case_file%.Jenkinsfile}.receipt.txt")
      printf 'fixture receipt for %s\n' "$(basename "$case_file")" \
        > "$receipt_dir/$receipt_name"
    done
    printf 'intentional probe CLI failure\n'
    exit 29
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
  bash "$evidence/run-probes.sh"
rc=$?
set -e
if [[ $rc -ne 29 ]]; then
  echo "ERROR: expected probe runner rc 29, got $rc" >&2
  exit 1
fi
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
if [[ $(<"$order") != $'oracle image inspect\ndotnet build\ndotnet run' ]]; then
  printf 'ERROR: verifier did not finish before build/run; order=%s\n' \
    "$(tr '\n' '|' < "$order")" >&2
  exit 1
fi
grep -Fx 'intentional probe CLI failure' "$runner_out/probe-run.log"
grep -Fx 'probe-cli-exit=29' "$runner_out/probe-exit.txt"
grep -Fx "$runner_out/raw-receipts" "$args"
for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-requiredness.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile \
  fg177-probe-checkout-scm.Jenkinsfile
do
  path="$runner_out/rendered-cases/$name"
  grep -Fx "$path" "$args"
done
for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile
do
  require_fixed_count -Foc 1 "$scm_url" \
    "$runner_out/rendered-cases/$name" \
    "runner-rendered $name does not contain exactly one configured URL"
done
cmp "$evidence/cases/fg177-probe-checkout-scm.Jenkinsfile" \
  "$runner_out/rendered-cases/fg177-probe-checkout-scm.Jenkinsfile"
grep -Fx $'cli-exit\t29' "$runner_out/probe-run-manifest.tsv"
grep -Fx $'jenkins-core\t2.568.1' "$runner_out/probe-run-manifest.tsv"
grep -Fx $'case-count\t4' "$runner_out/probe-run-manifest.tsv"

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
if [[ -e "$build_failure_out/probe-exit.txt" ]]; then
  echo 'ERROR: failed probe build wrote a misleading CLI exit marker' >&2
  exit 1
fi

printf 'rendered probe cases verified with fresh local fixture %s\n' "$scm_url"
