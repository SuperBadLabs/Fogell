#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

evidence='evidence/20260818-fg-177-measurement'
fixture_tmp=$(mktemp -d)
trap 'rm -rf "$fixture_tmp"' EXIT
remote="$fixture_tmp/fixture.git"
seed="$fixture_tmp/seed"
rendered="$fixture_tmp/rendered"

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
mkdir -p "$fixture_tmp/bin"
stub="$fixture_tmp/bin/dotnet"
cat > "$stub" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$FOGELL_STUB_ARGS"
printf 'intentional probe CLI failure\n'
exit 29
EOF
chmod +x "$stub"
runner_out="$fixture_tmp/runner-output"
args="$fixture_tmp/dotnet-args"
set +e
PATH="$fixture_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$runner_out" \
FOGELL_SCM_URL="$scm_url" \
FOGELL_STUB_ARGS="$args" \
  bash "$evidence/run-probes.sh"
rc=$?
set -e
if [[ $rc -ne 29 ]]; then
  echo "ERROR: expected probe runner rc 29, got $rc" >&2
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

printf 'rendered probe cases verified with fresh local fixture %s\n' "$scm_url"
