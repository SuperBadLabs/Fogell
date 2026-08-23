#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root="$repo/evidence/20260818-fg-177-measurement"
output="$bundle/run"
case_names=(
  fg213-junit-count-accessors-positive
  fg213-junit-count-accessors-empty
)

: "${FG213_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FG213_JENKINS_CORE:=2.568.1}"
: "${FG213_JENKINS_HOST:=luigi}"
: "${FG213_JENKINS_CONTAINER:=jenkins-lab}"

for target_var in FG213_JENKINS_HOST FG213_JENKINS_CONTAINER; do
  target_value=${!target_var}
  [[ "$target_value" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || {
    echo "ERROR: invalid $target_var: $target_value" >&2
    exit 1
  }
done

configure_differential_commands() {
  local host=$1
  local container=$2
  export FOGELL_JENKINS_WORKSPACE_CMD="ssh $host \"podman exec $container sh -c \\\"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\\\"\""
  export FOGELL_JENKINS_ENV_CMD="ssh $host \"podman exec $container env\""
  export FOGELL_JENKINS_GIT_VERSION_CMD="ssh $host \"podman exec $container git --version\""
  export FOGELL_JENKINS_WIPE_CMD="ssh $host \"podman exec $container sh -c \\\"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\\\"\""
}

[[ ! -e "$output" ]] || {
  echo "ERROR: refusing to overwrite retained run: $output" >&2
  exit 1
}

for case_name in "${case_names[@]}"; do
  [[ -s "$repo/differential/cases/$case_name.Jenkinsfile" ]] || {
    echo "ERROR: missing case: $case_name" >&2
    exit 1
  }
  [[ -s "$repo/differential/receipts/$case_name.receipt.txt" ]] || {
    echo "ERROR: missing promoted receipt: $case_name" >&2
    exit 1
  }
done

stage=$(mktemp -d "$bundle/.stage.XXXXXX")
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/inputs" "$stage/promoted-receipts" "$stage/receipts" "$stage/tooling"
cp "$bundle/README.md" "$stage/tooling/README.md"
{
  printf '%s\n' \
    '> **Collector-time scaffold snapshot**' \
    '>' \
    '> The embedded status below describes the bundle when collection started,' \
    '> before the retained `run/` was published. After publication, the enclosing' \
    '> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained' \
    '> provenance, not the live status of the enclosing run.' \
    ''
  cat "$stage/tooling/README.md"
} > "$stage/tooling/README.md.annotated"
mv "$stage/tooling/README.md.annotated" "$stage/tooling/README.md"
cp "$bundle/collect.sh" "$stage/tooling/collect.sh"
for case_name in "${case_names[@]}"; do
  cp "$repo/differential/cases/$case_name.Jenkinsfile" "$stage/inputs/"
  cp "$repo/differential/receipts/$case_name.receipt.txt" "$stage/promoted-receipts/"
done

verify_before() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG213_JENKINS_URL" "$FG213_JENKINS_CORE" \
    "$FG213_JENKINS_HOST" "$FG213_JENKINS_CONTAINER" \
    "$pin_root" "$stage/oracle-metadata"
}

verify_after() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG213_JENKINS_URL" "$FG213_JENKINS_CORE" \
    "$FG213_JENKINS_HOST" "$FG213_JENKINS_CONTAINER" \
    "$stage/oracle-metadata"
}

capture_junit_jar() {
  ssh "$FG213_JENKINS_HOST" \
    "podman exec $FG213_JENKINS_CONTAINER sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar"
}

capture_junit_public() {
  ssh "$FG213_JENKINS_HOST" \
    "podman exec $FG213_JENKINS_CONTAINER javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar -public hudson.tasks.junit.TestResultSummary"
}

capture_junit_private() {
  ssh "$FG213_JENKINS_HOST" \
    "podman exec $FG213_JENKINS_CONTAINER javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar -c -p hudson.tasks.junit.TestResultSummary"
}

capture_container() {
  ssh "$FG213_JENKINS_HOST" \
    "podman inspect $FG213_JENKINS_CONTAINER --format '{{.Id}}|{{.ImageName}}|{{.ImageDigest}}'"
}

verify_before > "$stage/oracle-before.txt"
capture_junit_jar > "$stage/junit-jar-before.txt"
capture_junit_public > "$stage/junit-public-before.txt"
capture_junit_private > "$stage/junit-bytecode-before.txt"
capture_container > "$stage/container-before.txt"

cd "$repo"
configure_differential_commands "$FG213_JENKINS_HOST" "$FG213_JENKINS_CONTAINER"
dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --nologo > "$stage/build.log"

case_inputs=()
for case_name in "${case_names[@]}"; do
  case_inputs+=("$stage/inputs/$case_name.Jenkinsfile")
done

dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  "$FG213_JENKINS_URL" "$FG213_JENKINS_CORE" "$stage/receipts" \
  "${case_inputs[@]}" > "$stage/run.log" 2>&1
printf 'differential-cli-exit=0\n' > "$stage/exit.txt"

verify_after > "$stage/oracle-after.txt"
capture_junit_jar > "$stage/junit-jar-after.txt"
capture_junit_public > "$stage/junit-public-after.txt"
capture_junit_private > "$stage/junit-bytecode-after.txt"
capture_container > "$stage/container-after.txt"

{
  cmp "$stage/oracle-before.txt" "$stage/oracle-after.txt"
  cmp "$stage/junit-jar-before.txt" "$stage/junit-jar-after.txt"
  cmp "$stage/junit-public-before.txt" "$stage/junit-public-after.txt"
  cmp "$stage/junit-bytecode-before.txt" "$stage/junit-bytecode-after.txt"
  cmp "$stage/container-before.txt" "$stage/container-after.txt"
  grep -Fx '7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142  /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar' "$stage/junit-jar-before.txt"
  grep -F 'public int getTotalCount();' "$stage/junit-public-before.txt"
  grep -F 'public int getFailCount();' "$stage/junit-public-before.txt"
  grep -F 'public int getSkipCount();' "$stage/junit-public-before.txt"
  grep -F 'public int getPassCount();' "$stage/junit-public-before.txt"
  dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
    -c Release --no-build -- --verify-seals "$stage/receipts"
  for case_name in "${case_names[@]}"; do
    cmp "$repo/differential/cases/$case_name.Jenkinsfile" "$stage/inputs/$case_name.Jenkinsfile"
    cmp "$repo/differential/receipts/$case_name.receipt.txt" "$stage/promoted-receipts/$case_name.receipt.txt"
    cmp "$stage/promoted-receipts/$case_name.receipt.txt" "$stage/receipts/$case_name.receipt.txt"
    receipt="$stage/receipts/$case_name.receipt.txt"
    grep -Fx 'VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash' "$receipt"
    case_digest=$(sha256sum "$stage/inputs/$case_name.Jenkinsfile" | awk '{print $1}')
    receipt_digest=$(awk '/^case-digest:/ {print $2}' "$receipt")
    [[ "$case_digest" == "$receipt_digest" ]]
  done
  (
    configure_differential_commands fg213-proof-host fg213-proof-container
    for command_var in FOGELL_JENKINS_WORKSPACE_CMD FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD FOGELL_JENKINS_WIPE_CMD; do
      command_value=${!command_var}
      [[ "$command_value" == "ssh fg213-proof-host "* ]]
      [[ "$command_value" == *"podman exec fg213-proof-container "* ]]
      [[ "$command_value" != *luigi* ]]
      [[ "$command_value" != *jenkins-lab* ]]
    done
    [[ "$FOGELL_JENKINS_WORKSPACE_CMD" == *'{job}'* ]]
    [[ "$FOGELL_JENKINS_WIPE_CMD" == *'{job}'* ]]
    printf 'configured-target override proof: 4 commands, literal job placeholders preserved\n'
  )
} > "$stage/validation.txt"

printf 'COMPLETE\n' > "$stage/STATUS"
(
  cd "$stage"
  find . -type f ! -name MANIFEST.sha256 -print0 | sort -z | xargs -0 sha256sum > MANIFEST.sha256
)

mv "$stage" "$output"
trap - EXIT
printf 'EVIDENCE_DIR=%s\n' "$output"
