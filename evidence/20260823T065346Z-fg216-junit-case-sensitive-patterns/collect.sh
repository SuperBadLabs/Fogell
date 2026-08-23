#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root="$repo/evidence/20260818-fg-177-measurement"
output="$bundle/run"
case_names=(
  fg216-junit-pattern-case-sensitive-selection
  fg216-junit-pattern-case-only-miss
)

cleanup_workspaces() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG216_JENKINS_HOST" \
      "podman exec $FG216_JENKINS_CONTAINER rm -rf /var/jenkins_home/workspace/$job_name /var/jenkins_home/workspace/$job_name@tmp" \
      >/dev/null 2>&1 || true
  done
}

verify_workspaces_absent() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG216_JENKINS_HOST" \
      "podman exec $FG216_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name"
    ssh "$FG216_JENKINS_HOST" \
      "podman exec $FG216_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name@tmp"
    printf 'workspace cleanup: %s and @tmp absent\n' "$job_name"
  done
}

: "${FG216_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FG216_JENKINS_CORE:=2.568.1}"
: "${FG216_JENKINS_HOST:=luigi}"
: "${FG216_JENKINS_CONTAINER:=jenkins-lab}"

for target_var in FG216_JENKINS_HOST FG216_JENKINS_CONTAINER; do
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
trap 'cleanup_workspaces; rm -rf "$stage"' EXIT
mkdir -p "$stage/inputs" "$stage/promoted-receipts" "$stage/receipts" "$stage/tooling"
{
  printf '%s\n' \
    '> **Collector-time scaffold snapshot**' \
    '>' \
    '> The embedded status below describes the bundle when collection started,' \
    '> before the retained `run/` was published. After publication, the enclosing' \
    '> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained' \
    '> provenance, not the live status of the enclosing run.' \
    ''
  awk '
    /^Status: \*\*COMPLETE\*\*/ {
      skip = 1
      print "Status: **COLLECTOR-TIME SNAPSHOT**. Live completion state and the"
      print "manifest digest are intentionally omitted from this embedded provenance copy."
      next
    }
    skip && /^$/ { skip = 0; print ""; next }
    !skip { print }
  ' "$bundle/README.md"
} > "$stage/tooling/README.md"
cp "$bundle/collect.sh" "$stage/tooling/collect.sh"
for case_name in "${case_names[@]}"; do
  cp "$repo/differential/cases/$case_name.Jenkinsfile" "$stage/inputs/"
  cp "$repo/differential/receipts/$case_name.receipt.txt" "$stage/promoted-receipts/"
done

verify_before() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG216_JENKINS_URL" "$FG216_JENKINS_CORE" \
    "$FG216_JENKINS_HOST" "$FG216_JENKINS_CONTAINER" \
    "$pin_root" "$stage/oracle-metadata"
}

verify_after() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG216_JENKINS_URL" "$FG216_JENKINS_CORE" \
    "$FG216_JENKINS_HOST" "$FG216_JENKINS_CONTAINER" \
    "$stage/oracle-metadata"
}

capture_junit_jar() {
  ssh "$FG216_JENKINS_HOST" \
    "podman exec $FG216_JENKINS_CONTAINER sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar"
}

capture_junit_private() {
  ssh "$FG216_JENKINS_HOST" \
    "podman exec $FG216_JENKINS_CONTAINER javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar -c -p 'hudson.tasks.junit.JUnitParser\$ParseResultCallable' hudson.tasks.junit.JUnitResultArchiver"
}

capture_core_ant_jars() {
  ssh "$FG216_JENKINS_HOST" \
    "podman exec $FG216_JENKINS_CONTAINER sha256sum /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar"
}

capture_core_private() {
  ssh "$FG216_JENKINS_HOST" \
    "podman exec $FG216_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar -c -p hudson.Util"
}

capture_ant_private() {
  ssh "$FG216_JENKINS_HOST" \
    "podman exec $FG216_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar -c -p org.apache.tools.ant.types.AbstractFileSet org.apache.tools.ant.DirectoryScanner"
}

capture_container() {
  ssh "$FG216_JENKINS_HOST" \
    "podman inspect $FG216_JENKINS_CONTAINER --format '{{.Id}}|{{.ImageName}}|{{.ImageDigest}}'"
}

verify_before > "$stage/oracle-before.txt"
capture_junit_jar > "$stage/junit-jar-before.txt"
capture_junit_private > "$stage/junit-bytecode-before.txt"
capture_core_ant_jars > "$stage/core-ant-jars-before.txt"
capture_core_private > "$stage/core-bytecode-before.txt"
capture_ant_private > "$stage/ant-bytecode-before.txt"
capture_container > "$stage/container-before.txt"

cd "$repo"
configure_differential_commands "$FG216_JENKINS_HOST" "$FG216_JENKINS_CONTAINER"
dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --nologo > "$stage/build.log"

case_inputs=()
for case_name in "${case_names[@]}"; do
  case_inputs+=("$stage/inputs/$case_name.Jenkinsfile")
done

dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  "$FG216_JENKINS_URL" "$FG216_JENKINS_CORE" "$stage/receipts" \
  "${case_inputs[@]}" > "$stage/run.log" 2>&1
printf 'differential-cli-exit=0\n' > "$stage/exit.txt"
cleanup_workspaces

verify_after > "$stage/oracle-after.txt"
capture_junit_jar > "$stage/junit-jar-after.txt"
capture_junit_private > "$stage/junit-bytecode-after.txt"
capture_core_ant_jars > "$stage/core-ant-jars-after.txt"
capture_core_private > "$stage/core-bytecode-after.txt"
capture_ant_private > "$stage/ant-bytecode-after.txt"
capture_container > "$stage/container-after.txt"

{
  cmp "$stage/oracle-before.txt" "$stage/oracle-after.txt"
  cmp "$stage/junit-jar-before.txt" "$stage/junit-jar-after.txt"
  cmp "$stage/junit-bytecode-before.txt" "$stage/junit-bytecode-after.txt"
  cmp "$stage/core-ant-jars-before.txt" "$stage/core-ant-jars-after.txt"
  cmp "$stage/core-bytecode-before.txt" "$stage/core-bytecode-after.txt"
  cmp "$stage/ant-bytecode-before.txt" "$stage/ant-bytecode-after.txt"
  cmp "$stage/container-before.txt" "$stage/container-after.txt"
  grep -Fx '7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142  /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar' "$stage/junit-jar-before.txt"
  grep -Fx '07511327f8f69b4abdab17705f99c5de16bcb751b79e1403f4ac80ac151b3e6c  /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar' "$stage/core-ant-jars-before.txt"
  grep -Fx '8be692e02837f41a47a3d21cde6655792142fdf42fe23bcb16d7129cad9b2284  /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar' "$stage/core-ant-jars-before.txt"
  grep -F 'public static org.apache.tools.ant.types.FileSet createFileSet' "$stage/core-bytecode-before.txt"
  grep -F 'private boolean caseSensitive;' "$stage/ant-bytecode-before.txt"
  grep -F 'protected boolean isCaseSensitive;' "$stage/ant-bytecode-before.txt"
  verify_workspaces_absent
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
    configure_differential_commands fg216-proof-host fg216-proof-container
    for command_var in FOGELL_JENKINS_WORKSPACE_CMD FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD FOGELL_JENKINS_WIPE_CMD; do
      command_value=${!command_var}
      [[ "$command_value" == "ssh fg216-proof-host "* ]]
      [[ "$command_value" == *"podman exec fg216-proof-container "* ]]
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
