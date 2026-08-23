#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root="$repo/evidence/20260818-fg-177-measurement"
output="$bundle/run"
case_names=(
  fg220-junit-skip-old-mixed
  fg220-junit-skip-old-all-allow-empty
)

cleanup_workspaces() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG220_JENKINS_HOST" \
      "podman exec $FG220_JENKINS_CONTAINER rm -rf /var/jenkins_home/workspace/$job_name /var/jenkins_home/workspace/$job_name@tmp" \
      >/dev/null 2>&1 || true
  done
}

verify_workspaces_absent() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG220_JENKINS_HOST" \
      "podman exec $FG220_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name"
    ssh "$FG220_JENKINS_HOST" \
      "podman exec $FG220_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name@tmp"
    printf 'workspace cleanup: %s and @tmp absent\n' "$job_name"
  done
}

: "${FG220_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FG220_JENKINS_CORE:=2.568.1}"
: "${FG220_JENKINS_HOST:=luigi}"
: "${FG220_JENKINS_CONTAINER:=jenkins-lab}"

for target_var in FG220_JENKINS_HOST FG220_JENKINS_CONTAINER; do
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
    '> `run/STATUS` is authoritative: `COMPLETE` means this snapshot is retained' \
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
    "$FG220_JENKINS_URL" "$FG220_JENKINS_CORE" \
    "$FG220_JENKINS_HOST" "$FG220_JENKINS_CONTAINER" \
    "$pin_root" "$stage/oracle-metadata"
}

verify_after() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG220_JENKINS_URL" "$FG220_JENKINS_CORE" \
    "$FG220_JENKINS_HOST" "$FG220_JENKINS_CONTAINER" \
    "$stage/oracle-metadata"
}

capture_junit_jar() {
  ssh "$FG220_JENKINS_HOST" \
    "podman exec $FG220_JENKINS_CONTAINER sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar"
}

capture_junit_private() {
  ssh "$FG220_JENKINS_HOST" \
    "podman exec $FG220_JENKINS_CONTAINER javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar -c -p -s -v 'hudson.tasks.junit.JUnitParser\$ParseResultCallable' hudson.tasks.junit.TestResult hudson.tasks.junit.JUnitResultArchiver"
}

capture_core_jar() {
  ssh "$FG220_JENKINS_HOST" \
    "podman exec $FG220_JENKINS_CONTAINER sha256sum /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar"
}

capture_run_private() {
  ssh "$FG220_JENKINS_HOST" \
    "podman exec $FG220_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar -c -p -s hudson.model.Run"
}

capture_margin_runtime() {
  ssh "$FG220_JENKINS_HOST" \
    "podman exec $FG220_JENKINS_CONTAINER sh -c 'java_pid=\$(pgrep -o -f java); property=\$(jcmd \"\$java_pid\" VM.system_properties | grep -F hudson.tasks.junit.TestResultfiletime.precision.margin || true); environment=\$(tr \"\\000\" \"\\n\" < /proc/\"\$java_pid\"/environ | grep -F hudson.tasks.junit.TestResultfiletime.precision.margin || true); printf \"system-property=%s\\nenvironment-override=%s\\n\" \"\${property:-<absent>}\" \"\${environment:-<absent>}\"'"
}

capture_corpus() {
  local corpus=/sn8100/work/exchange/crucible-gate/corpus
  local junit_calls direct_count option_count global_hits global_count
  bash "$repo/scripts/verify-corpus.sh"
  # Some corpus inputs are CRLF. Retain the matched source text while removing
  # only the transport carriage return so the evidence itself passes the
  # repository whitespace gate.
  junit_calls="$(rg -n --glob '*.Jenkinsfile' '^[[:space:]]*junit([ (]|$)' "$corpus/jenkinsfiles" | tr -d '\r')"
  direct_count=$(printf '%s\n' "$junit_calls" | wc -l | tr -d ' ')
  option_count=$(printf '%s\n' "$junit_calls" | awk '
    { line = $0; while (match(line, /skipOldReports/)) { count++; line = substr(line, RSTART + RLENGTH) } }
    END { print count + 0 }
  ')
  global_hits="$(rg -n --glob '*.Jenkinsfile' 'skipOldReports' "$corpus/jenkinsfiles" | tr -d '\r' || true)"

  if [[ -n "$global_hits" ]]; then
    global_count=$(printf '%s\n' "$global_hits" | wc -l | tr -d ' ')
  else
    global_count=0
  fi

  printf 'direct-junit-calls=%s\n' "$direct_count"
  printf 'direct-junit-skip-old-reports-occurrences=%s\n' "$option_count"
  printf 'corpus-wide-skip-old-reports-lines=%s\n' "$global_count"
  printf '%s\n' "$junit_calls"

  if [[ -n "$global_hits" ]]; then
    printf '%s\n' "$global_hits"
  fi
}

capture_container() {
  ssh "$FG220_JENKINS_HOST" \
    "podman inspect $FG220_JENKINS_CONTAINER --format '{{.Id}}|{{.ImageName}}|{{.ImageDigest}}'"
}

verify_before > "$stage/oracle-before.txt"
capture_junit_jar > "$stage/junit-jar-before.txt"
capture_junit_private > "$stage/junit-bytecode-before.txt"
capture_core_jar > "$stage/core-jar-before.txt"
capture_run_private > "$stage/run-bytecode-before.txt"
capture_margin_runtime > "$stage/margin-runtime-before.txt"
capture_container > "$stage/container-before.txt"
capture_corpus > "$stage/corpus-proof.txt"

cd "$repo"
configure_differential_commands "$FG220_JENKINS_HOST" "$FG220_JENKINS_CONTAINER"
dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --nologo > "$stage/build.log"

case_inputs=()
for case_name in "${case_names[@]}"; do
  case_inputs+=("$stage/inputs/$case_name.Jenkinsfile")
done

dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  "$FG220_JENKINS_URL" "$FG220_JENKINS_CORE" "$stage/receipts" \
  "${case_inputs[@]}" > "$stage/run.log" 2>&1
printf 'differential-cli-exit=0\n' > "$stage/exit.txt"
cleanup_workspaces

verify_after > "$stage/oracle-after.txt"
capture_junit_jar > "$stage/junit-jar-after.txt"
capture_junit_private > "$stage/junit-bytecode-after.txt"
capture_core_jar > "$stage/core-jar-after.txt"
capture_run_private > "$stage/run-bytecode-after.txt"
capture_margin_runtime > "$stage/margin-runtime-after.txt"
capture_container > "$stage/container-after.txt"

{
  cmp "$stage/oracle-before.txt" "$stage/oracle-after.txt"
  cmp "$stage/junit-jar-before.txt" "$stage/junit-jar-after.txt"
  cmp "$stage/junit-bytecode-before.txt" "$stage/junit-bytecode-after.txt"
  cmp "$stage/core-jar-before.txt" "$stage/core-jar-after.txt"
  cmp "$stage/run-bytecode-before.txt" "$stage/run-bytecode-after.txt"
  cmp "$stage/margin-runtime-before.txt" "$stage/margin-runtime-after.txt"
  cmp "$stage/container-before.txt" "$stage/container-after.txt"
  grep -Fx '7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142  /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar' "$stage/junit-jar-before.txt"
  grep -Fx '07511327f8f69b4abdab17705f99c5de16bcb751b79e1403f4ac80ac151b3e6c  /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar' "$stage/core-jar-before.txt"
  grep -F 'Method hudson/model/Run.getStartTimeInMillis:()J' "$stage/junit-bytecode-before.txt"
  grep -F 'Method hudson/model/Run.getTimeInMillis:()J' "$stage/junit-bytecode-before.txt"
  grep -F 'Method java/lang/Math.min:(JJ)J' "$stage/junit-bytecode-before.txt"
  grep -F 'Method java/nio/file/Files.getLastModifiedTime:' "$stage/junit-bytecode-before.txt"
  grep -F 'Field FILE_TIME_PRECISION_MARGIN:J' "$stage/junit-bytecode-before.txt"
  grep -F 'filetime.precision.margin' "$stage/junit-bytecode-before.txt"
  grep -F 'long 3000l' "$stage/junit-bytecode-before.txt"
  grep -E '^[[:space:]]+57: lsub$' "$stage/junit-bytecode-before.txt"
  grep -E '^[[:space:]]+58: lcmp$' "$stage/junit-bytecode-before.txt"
  grep -E '^[[:space:]]+59: ifge[[:space:]]+113$' "$stage/junit-bytecode-before.txt"
  grep -F 'public final long getStartTimeInMillis();' "$stage/run-bytecode-before.txt"
  grep -F 'public final long getTimeInMillis();' "$stage/run-bytecode-before.txt"
  grep -Fx 'system-property=<absent>' "$stage/margin-runtime-before.txt"
  grep -Fx 'environment-override=<absent>' "$stage/margin-runtime-before.txt"
  grep -Fx 'direct-junit-calls=36' "$stage/corpus-proof.txt"
  grep -Fx 'direct-junit-skip-old-reports-occurrences=0' "$stage/corpus-proof.txt"
  grep -Fx 'corpus-wide-skip-old-reports-lines=0' "$stage/corpus-proof.txt"
  printf 'strict skip predicate, 3000 ms default, absent overrides, and corpus-wide 0/36 boundary verified\n'
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
    configure_differential_commands fg220-proof-host fg220-proof-container
    for command_var in FOGELL_JENKINS_WORKSPACE_CMD FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD FOGELL_JENKINS_WIPE_CMD; do
      command_value=${!command_var}
      [[ "$command_value" == "ssh fg220-proof-host "* ]]
      [[ "$command_value" == *"podman exec fg220-proof-container "* ]]
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
  sha256sum -c MANIFEST.sha256 >/dev/null
)

mv "$stage" "$output"
trap - EXIT
printf 'EVIDENCE_DIR=%s\n' "$output"
