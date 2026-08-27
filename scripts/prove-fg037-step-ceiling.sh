#!/usr/bin/env bash
# FG-037. Mutation proof for the retained-evidence semantic checker.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [ "$#" -ne 1 ] || [ ! -d "$1/cases" ] || [ ! -d "$1/receipts" ]; then
  echo "usage: $0 <fg037-evidence-directory>" >&2
  exit 2
fi

source_dir=$1
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT

check() {
  python3 scripts/check-fg037-step-ceiling.py \
    --cases "$1/cases" --receipts "$1/receipts" --jenkins-core 2.568.1
}

fresh() {
  rm -rf "$scratch/case"
  mkdir -p "$scratch/case"
  cp -a "$source_dir/cases" "$source_dir/receipts" "$scratch/case/"
}

expect_reject() {
  local label=$1
  if check "$scratch/case" >/dev/null 2>&1; then
    echo "FAIL: checker accepted $label" >&2
    exit 1
  fi
  echo "  rejected $label"
}

echo "=== FG-037 semantic checker mutation proof ==="
check "$source_dir" >/dev/null
echo "  accepted the unmodified retained evidence"

identity_checker=scripts/check-fg037-jenkins-identity.sh
good_identity=$'2.568.1\tpinned-session'
stale_identity=$'2.568.1\tstale-forward-session'
wrong_core_identity=$'9.99.9\tpinned-session'
restarted_identity=$'2.568.1\trestarted-session'
bash "$identity_checker" 2.568.1 2.568.1 \
  "$good_identity" "$good_identity" >/dev/null
echo "  accepted one pinned endpoint/container identity"

if bash "$identity_checker" 2.568.1 2.568.1 \
  "$stale_identity" "$good_identity" >/dev/null 2>&1; then
  echo "FAIL: identity checker accepted a same-core stale endpoint session" >&2
  exit 1
fi
echo "  rejected a same-core stale endpoint session"

if bash "$identity_checker" 2.568.1 2.568.1 \
  "$wrong_core_identity" "$good_identity" >/dev/null 2>&1; then
  echo "FAIL: identity checker accepted a wrong endpoint core" >&2
  exit 1
fi
echo "  rejected a wrong endpoint core"

if bash "$identity_checker" 2.568.1 2.568.1 \
  "$restarted_identity" "$restarted_identity" pinned-session >/dev/null 2>&1; then
  echo "FAIL: identity checker accepted a controller session change" >&2
  exit 1
fi
echo "  rejected a controller session change across the live run"

fake_bin=$scratch/fake-bin
mkdir -p "$fake_bin"
printf '#!/usr/bin/env bash\nexit 9\n' >"$fake_bin/base64"
chmod +x "$fake_bin/base64"
set +e
PATH="$fake_bin:$PATH" bash -c \
  'source "$1"; fogell_configure_jenkins_workspace_v2 luigi jenkins-lab' \
  _ scripts/jenkins-workspace-v2.sh >"$scratch/base64-failure.log" 2>&1
base64_rc=$?
set -e
if [ "$base64_rc" -ne 2 ]; then
  echo "FAIL: shared collector configuration accepted a Base64 encoder failure" >&2
  sed -n '1,80p' "$scratch/base64-failure.log" >&2
  exit 1
fi
echo "  rejected a shared-collector Base64 encoder failure"

fresh
sed -i 's/^jenkins-core: 2\.568\.1$/jenkins-core: 9.99.9/' \
  "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a substituted Jenkins core"

fresh
sed -i '/^## Jenkins$/,/^## Fogell$/s/^  result:         failure$/  result:         success/' \
  "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a 251-step Jenkins success substitution"

fresh
sed -i '0,/^    | FG037-400$/s//    | FG037-399/' \
  "$scratch/case/receipts/fg037-400-steps.receipt.txt"
expect_reject "a skipped final Fogell marker"

fresh
sed -i 's/^VERDICT: DIVERGED (/VERDICT: PROVEN (tier 1) — forged (/' \
  "$scratch/case/receipts/fg037-400-steps.receipt.txt"
expect_reject "a promoted intentional divergence"

fresh
sed -i 's/^VERDICT: DIVERGED (3)$/VERDICT: DIVERGED (1)/' \
  "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a weakened one-difference verdict"

fresh
sed -i 's/^VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash$/& — forged suffix/' \
  "$scratch/case/receipts/fg037-250-steps.receipt.txt"
expect_reject "a forged suffix on the exact control verdict"

fresh
sed -i '2s/$/ /' "$scratch/case/cases/fg037-250-steps.Jenkinsfile"
expect_reject "fixture drift even before its digest is updated"

fresh
cp "$scratch/case/receipts/fg037-250-steps.receipt.txt" \
  "$scratch/case/receipts/unexpected.receipt.txt"
expect_reject "an extra receipt outside the exact three-file inventory"

fresh
rm "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a missing boundary receipt"

probe_repo=$scratch/probe-repo
mkdir -p "$probe_repo/scripts" "$probe_repo/tools/Fogell.Differential.Cli" \
  "$probe_repo/src"
cp scripts/run-fg037-step-ceiling-probe.sh \
  scripts/check-fg037-jenkins-identity.sh \
  scripts/check-fg037-step-ceiling.py \
  scripts/jenkins-workspace-v2.sh \
  scripts/prove-fg037-step-ceiling.sh \
  "$probe_repo/scripts/"
touch "$probe_repo/Fogell.slnx" "$probe_repo/global.json" \
  "$probe_repo/Directory.Build.props" \
  "$probe_repo/tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj"
git -C "$probe_repo" init -q
git -C "$probe_repo" add .
git -C "$probe_repo" -c user.name=FG-037 -c user.email=fg037@example.invalid \
  -c commit.gpgsign=false commit -qm baseline
printf '\n# planted collector drift\n' >>"$probe_repo/scripts/jenkins-workspace-v2.sh"
probe_output=$probe_repo/collector-drift-evidence
set +e
FOGELL_JENKINS_URL=http://127.0.0.1:1 \
  "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output" \
  >"$scratch/collector-drift.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || ! grep -Fq "scripts/jenkins-workspace-v2.sh" "$scratch/collector-drift.log"; then
  echo "FAIL: probe did not refuse a dirty shared workspace collector before evidence creation" >&2
  sed -n '1,120p' "$scratch/collector-drift.log" >&2
  exit 1
fi
echo "  refused a dirty shared workspace collector before evidence creation"

echo "FG-037 proof PASS (9 semantic + 3 controller-identity + 2 collector/configuration rejection arms)"
