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

manifest_checker=scripts/check-fg037-manifest.py
manifest_case=$scratch/manifest-case
cp -a "$source_dir" "$manifest_case"
rm -f "$manifest_case/manifest.sha256"
manifest_case_tmp=$scratch/manifest-case.sha256
(
  cd "$manifest_case"
  find . -type f ! -name manifest.sha256 -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 sha256sum >"$manifest_case_tmp"
)
mv "$manifest_case_tmp" "$manifest_case/manifest.sha256"
python3 "$manifest_checker" "$manifest_case" >/dev/null
printf 'unlisted\n' >"$manifest_case/unlisted.txt"
if python3 "$manifest_checker" "$manifest_case" >/dev/null 2>&1; then
  echo "FAIL: manifest checker accepted an unlisted evidence file" >&2
  exit 1
fi
echo "  rejected an unlisted evidence file"

manifest_substitution=$scratch/manifest-substitution
cp -a "$source_dir" "$manifest_substitution"
rm -f "$manifest_substitution/manifest.sha256"
manifest_substitution_tmp=$scratch/manifest-substitution.sha256
(
  cd "$manifest_substitution"
  find . -type f ! -name manifest.sha256 -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 sha256sum >"$manifest_substitution_tmp"
)
mv "$manifest_substitution_tmp" "$manifest_substitution/manifest.sha256"
trusted_manifest_sha=$(sha256sum "$manifest_substitution/manifest.sha256" | awk '{print $1}')
printf '\nsubstituted payload\n' >>"$manifest_substitution/build.log"
(
  cd "$manifest_substitution"
  find . -type f ! -name manifest.sha256 -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 sha256sum >"$manifest_substitution_tmp"
)
mv "$manifest_substitution_tmp" "$manifest_substitution/manifest.sha256"
if python3 "$manifest_checker" \
  --expected-manifest-sha256 "$trusted_manifest_sha" \
  "$manifest_substitution" >/dev/null 2>&1; then
  echo "FAIL: manifest checker accepted a substituted payload and self-consistent manifest" >&2
  exit 1
fi
echo "  rejected a substituted payload and self-consistent manifest"

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

inspect_fake_bin=$scratch/inspect-fake-bin
mkdir -p "$inspect_fake_bin"
cat >"$inspect_fake_bin/ssh" <<'FAKE_SSH'
#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 3 ]
[ "$1" = -- ]
[ "$2" = "$FG037_EXPECTED_HOST" ]
shift 2
bash -c "$1"
FAKE_SSH
cat >"$inspect_fake_bin/podman" <<'FAKE_PODMAN'
#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 3 ]
[ "$1" = inspect ]
[ "$2" = '--format={{.Image}}' ]
[ "$3" = "$FG037_EXPECTED_CONTAINER" ]
printf '%s\n' pinned-image-id
FAKE_PODMAN
chmod +x "$inspect_fake_bin/ssh" "$inspect_fake_bin/podman"
inspect_injection_marker=$scratch/remote-inspect-injection
host_injection_marker=$scratch/ssh-host-injection
hostile_host="-oProxyCommand=touch $host_injection_marker"
hostile_container="jenkins-lab; touch $inspect_injection_marker"
inspect_result=$(
  PATH="$inspect_fake_bin:$PATH" \
    FG037_EXPECTED_HOST="$hostile_host" \
    FG037_EXPECTED_CONTAINER="$hostile_container" \
    bash -c \
      'source "$1"; fogell_jenkins_podman_inspect_v2 "$2" "$3" "{{.Image}}"' \
      _ scripts/jenkins-workspace-v2.sh "$hostile_host" "$hostile_container"
) || {
  echo "FAIL: shared collector rejected a safely quoted inspect override" >&2
  exit 1
}
if [ "$inspect_result" != pinned-image-id ] \
  || [ -e "$host_injection_marker" ] \
  || [ -e "$inspect_injection_marker" ]; then
  echo "FAIL: remote inspect did not contain hostile host/container overrides" >&2
  exit 1
fi
echo "  contained hostile remote-inspect host/container overrides"

nested_fake_bin=$scratch/nested-fake-bin
mkdir -p "$nested_fake_bin"
cat >"$nested_fake_bin/ssh" <<'FAKE_SSH'
#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 3 ]
[ "$1" = -- ]
[ "$2" = "$FG037_EXPECTED_HOST" ]
shift 2
bash -c "$1"
FAKE_SSH
cat >"$nested_fake_bin/podman" <<'FAKE_PODMAN'
#!/usr/bin/env bash
set -euo pipefail
[ "$1" = exec ]
[ "$2" = "$FG037_EXPECTED_CONTAINER" ]
printf '%s\n' "$2" >>"$FG037_CONTAINER_LOG"
shift 2
case "$1 ${2-}" in
  'bash -c')
    printf 'FOGELL-WORKSPACE-MANIFEST\t2\nEND\t0\n'
    ;;
  'sh -c')
    ;;
  'env ')
    printf 'PATH=/usr/bin\n'
    ;;
  'git --version')
    printf 'git version 2.0\n'
    ;;
  *)
    printf 'unexpected fake podman argv: %q ' "$@" >&2
    printf '\n' >&2
    exit 9
    ;;
esac
FAKE_PODMAN
chmod +x "$nested_fake_bin/ssh" "$nested_fake_bin/podman"
nested_host_marker=$scratch/nested-host-injection
nested_container_marker=$scratch/nested-container-injection
nested_backtick_marker=$scratch/nested-backtick-injection
nested_host="-oProxyCommand=touch $nested_host_marker"
nested_container="jenkins'\"; touch $nested_container_marker; \`touch $nested_backtick_marker\`"
nested_container_log=$scratch/nested-containers.log
PATH="$nested_fake_bin:$PATH" \
  FG037_EXPECTED_HOST="$nested_host" \
  FG037_EXPECTED_CONTAINER="$nested_container" \
  FG037_CONTAINER_LOG="$nested_container_log" \
  bash -c '
    set -euo pipefail
    source "$1"
    fogell_configure_jenkins_workspace_v2 "$2" "$3"
    container_q=$(fogell_quote_posix_shell_v2 "$3")
    env_cmd=$(fogell_jenkins_ssh_command_v2 "$2" "podman exec ${container_q} env")
    git_cmd=$(fogell_jenkins_ssh_command_v2 "$2" "podman exec ${container_q} git --version")
    workspace_cmd=${FOGELL_JENKINS_WORKSPACE_CMD//\{job\}/proof-job}
    wipe_cmd=${FOGELL_JENKINS_WIPE_CMD//\{job\}/proof-job}
    /bin/sh -c "$workspace_cmd" >/dev/null
    /bin/sh -c "$wipe_cmd" >/dev/null
    /bin/sh -c "$env_cmd" >/dev/null
    /bin/sh -c "$git_cmd" >/dev/null
  ' _ scripts/jenkins-workspace-v2.sh "$nested_host" "$nested_container"
if [ "$(wc -l <"$nested_container_log")" -ne 4 ] \
  || [ -e "$nested_host_marker" ] \
  || [ -e "$nested_container_marker" ] \
  || [ -e "$nested_backtick_marker" ] \
  || [ "$(sort -u "$nested_container_log")" != "$nested_container" ]; then
  echo "FAIL: nested local/remote shell commands did not contain hostile overrides" >&2
  exit 1
fi
echo "  contained hostile overrides across nested workspace/wipe/env/git commands"

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
sed -i 's/The max number of supported arguments is 255, but found 400/compiler diagnostic removed/' \
  "$scratch/case/receipts/fg037-400-steps.receipt.txt"
expect_reject "a missing exact 400/255 compiler diagnostic"

fresh
printf 'Started by user unknown or anonymous\nAgent provisioning failed\nFinished: FAILURE\n' \
  >"$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "an unrelated 251-step infrastructure failure"

fresh
sed -i '0,/java\.lang\.Object, /s///' \
  "$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "a 250-object substitution in the 251-step causal signature"

fresh
sed -i '/^Finished: FAILURE$/i [Pipeline] Start of Pipeline' \
  "$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "a 251-step console claiming Pipeline execution began"

fresh
sed -i '/CpsFlowExecution\.parseScript/d' \
  "$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "a missing 251-step CPS parse frame"

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
  scripts/check-fg037-manifest.py \
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

echo "FG-037 proof PASS (14 semantic + 3 controller-identity + 4 collector/configuration + 2 manifest rejection arms)"
