#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root="$repo/evidence/20260818-fg-177-measurement"
output="$bundle/run"
cases=(
  "$repo/differential/cases/fg208-junit-malformed-xml.Jenkinsfile"
  "$repo/differential/cases/fg208-junit-malformed-mixed.Jenkinsfile"
)

: "${FG208_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FG208_JENKINS_CORE:=2.568.1}"
: "${FG208_JENKINS_HOST:=luigi}"
: "${FG208_JENKINS_CONTAINER:=jenkins-lab}"

if [[ -e "$output" ]]; then
  echo "ERROR: refusing to overwrite retained run: $output" >&2
  exit 1
fi

for case_file in "${cases[@]}"; do
  [[ -s "$case_file" ]] || { echo "ERROR: missing case: $case_file" >&2; exit 1; }
done

stage=$(mktemp -d "$bundle/.stage.XXXXXX")
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/inputs" "$stage/receipts" "$stage/tooling"
cp "$bundle/README.md" "$stage/tooling/README.md"
cp "$bundle/collect.sh" "$stage/tooling/collect.sh"
for case_file in "${cases[@]}"; do
  cp "$case_file" "$stage/inputs/$(basename "$case_file")"
done

verify_before() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG208_JENKINS_URL" "$FG208_JENKINS_CORE" \
    "$FG208_JENKINS_HOST" "$FG208_JENKINS_CONTAINER" \
    "$pin_root" "$stage/oracle-metadata"
}

verify_after() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG208_JENKINS_URL" "$FG208_JENKINS_CORE" \
    "$FG208_JENKINS_HOST" "$FG208_JENKINS_CONTAINER" \
    "$stage/oracle-metadata"
}

capture_junit() {
  ssh "$FG208_JENKINS_HOST" \
    "podman exec $FG208_JENKINS_CONTAINER sh -c 'sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar; javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar -c -p hudson.tasks.junit.TestResultSummary hudson.tasks.junit.TestResult hudson.tasks.junit.SuiteResult hudson.tasks.junit.CaseResult'"
}

capture_container() {
  ssh "$FG208_JENKINS_HOST" \
    "podman inspect $FG208_JENKINS_CONTAINER --format '{{.Id}}|{{.ImageName}}|{{.ImageDigest}}'"
}

verify_before > "$stage/oracle-before.txt"
capture_junit > "$stage/junit-bytecode-before.txt"
capture_container > "$stage/container-before.txt"

cd "$repo"
export FOGELL_JENKINS_WORKSPACE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\""'
export FOGELL_JENKINS_ENV_CMD='ssh luigi "podman exec jenkins-lab env"'
export FOGELL_JENKINS_GIT_VERSION_CMD='ssh luigi "podman exec jenkins-lab git --version"'
export FOGELL_JENKINS_WIPE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\""'

dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --nologo > "$stage/build.log"
dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  "$FG208_JENKINS_URL" "$FG208_JENKINS_CORE" "$stage/receipts" \
  "$stage/inputs/fg208-junit-malformed-xml.Jenkinsfile" \
  "$stage/inputs/fg208-junit-malformed-mixed.Jenkinsfile" \
  > "$stage/run.log" 2>&1
printf 'differential-cli-exit=0\n' > "$stage/exit.txt"

verify_after > "$stage/oracle-after.txt"
capture_junit > "$stage/junit-bytecode-after.txt"
capture_container > "$stage/container-after.txt"

{
  cmp "$stage/oracle-before.txt" "$stage/oracle-after.txt"
  cmp "$stage/junit-bytecode-before.txt" "$stage/junit-bytecode-after.txt"
  cmp "$stage/container-before.txt" "$stage/container-after.txt"
  cmp "$repo/differential/cases/fg208-junit-malformed-xml.Jenkinsfile" \
    "$stage/inputs/fg208-junit-malformed-xml.Jenkinsfile"
  cmp "$repo/differential/cases/fg208-junit-malformed-mixed.Jenkinsfile" \
    "$stage/inputs/fg208-junit-malformed-mixed.Jenkinsfile"
  dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
    -c Release --no-build -- --verify-seals "$stage/receipts"
  for case_name in fg208-junit-malformed-xml fg208-junit-malformed-mixed; do
    receipt="$stage/receipts/$case_name.receipt.txt"
    grep -Fx 'VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash' "$receipt"
    case_digest=$(sha256sum "$stage/inputs/$case_name.Jenkinsfile" | awk '{print $1}')
    receipt_digest=$(awk '/^case-digest:/ {print $2}' "$receipt")
    [[ "$case_digest" == "$receipt_digest" ]]
  done
} > "$stage/validation.txt"

printf 'COMPLETE\n' > "$stage/STATUS"
(
  cd "$stage"
  find . -type f ! -name MANIFEST.sha256 -print0 | sort -z | xargs -0 sha256sum > MANIFEST.sha256
)

mv "$stage" "$output"
trap - EXIT
printf 'EVIDENCE_DIR=%s\n' "$output"
