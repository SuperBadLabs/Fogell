#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root="$repo/evidence/20260818-fg-177-measurement"
output="$bundle/run"
case_names=(
  fg218-junit-repeated-separators-corpus
)

cleanup_workspaces() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG218_JENKINS_HOST" \
      "podman exec $FG218_JENKINS_CONTAINER rm -rf /var/jenkins_home/workspace/$job_name /var/jenkins_home/workspace/$job_name@tmp" \
      >/dev/null 2>&1 || true
  done
}

verify_workspaces_absent() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG218_JENKINS_HOST" \
      "podman exec $FG218_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name"
    ssh "$FG218_JENKINS_HOST" \
      "podman exec $FG218_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name@tmp"
    printf 'workspace cleanup: %s and @tmp absent\n' "$job_name"
  done
}

: "${FG218_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FG218_JENKINS_CORE:=2.568.1}"
: "${FG218_JENKINS_HOST:=luigi}"
: "${FG218_JENKINS_CONTAINER:=jenkins-lab}"

for target_var in FG218_JENKINS_HOST FG218_JENKINS_CONTAINER; do
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
    "$FG218_JENKINS_URL" "$FG218_JENKINS_CORE" \
    "$FG218_JENKINS_HOST" "$FG218_JENKINS_CONTAINER" \
    "$pin_root" "$stage/oracle-metadata"
}

verify_after() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG218_JENKINS_URL" "$FG218_JENKINS_CORE" \
    "$FG218_JENKINS_HOST" "$FG218_JENKINS_CONTAINER" \
    "$stage/oracle-metadata"
}

capture_junit_jar() {
  ssh "$FG218_JENKINS_HOST" \
    "podman exec $FG218_JENKINS_CONTAINER sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar"
}

capture_junit_private() {
  ssh "$FG218_JENKINS_HOST" \
    "podman exec $FG218_JENKINS_CONTAINER javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar -c -p 'hudson.tasks.junit.JUnitParser\$ParseResultCallable' hudson.tasks.junit.JUnitResultArchiver"
}

capture_core_ant_jars() {
  ssh "$FG218_JENKINS_HOST" \
    "podman exec $FG218_JENKINS_CONTAINER sha256sum /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar"
}

capture_core_private() {
  ssh "$FG218_JENKINS_HOST" \
    "podman exec $FG218_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar -c -p hudson.Util"
}

capture_ant_private() {
  ssh "$FG218_JENKINS_HOST" \
    "podman exec $FG218_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar -c -p org.apache.tools.ant.types.AbstractFileSet org.apache.tools.ant.DirectoryScanner"
}

capture_tokenized_pattern_private() {
  ssh "$FG218_JENKINS_HOST" \
    "podman exec $FG218_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar -p -c -s org.apache.tools.ant.types.selectors.TokenizedPattern"
}

capture_selector_utils_private() {
  ssh "$FG218_JENKINS_HOST" \
    "podman exec $FG218_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar -p -c -s org.apache.tools.ant.types.selectors.SelectorUtils"
}

capture_ant_match_matrix() {
  {
    ssh "$FG218_JENKINS_HOST" \
    "podman exec -i $FG218_JENKINS_CONTAINER jshell --feedback concise --class-path /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar" <<'EOF'
import org.apache.tools.ant.types.selectors.SelectorUtils;
System.out.println("single=" + SelectorUtils.matchPath("**/*target/surefire-reports/TEST-*.xml", "module/target/surefire-reports/TEST-one.xml", true));
System.out.println("doubled=" + SelectorUtils.matchPath("**//*target/surefire-reports/TEST-*.xml", "module/target/surefire-reports/TEST-one.xml", true));
System.out.println("many=" + SelectorUtils.matchPath("**////*target//surefire-reports///TEST-*.xml", "module/target/surefire-reports/TEST-one.xml", true));
System.out.println("case-decoy=" + SelectorUtils.matchPath("**//*target/surefire-reports/TEST-*.xml", "module/target/surefire-reports/test-one.xml", true));
System.out.println("rooted=" + SelectorUtils.matchPath("//module/target/surefire-reports/TEST-one.xml", "module/target/surefire-reports/TEST-one.xml", true));
/exit
EOF
  } | tr -d '\b' | grep -oE 'single=(true|false)|doubled=(true|false)|many=(true|false)|case-decoy=(true|false)|rooted=(true|false)'
}

capture_corpus() {
  local corpus=/sn8100/work/exchange/crucible-gate/corpus
  local file="$corpus/jenkinsfiles/Romeh_spring-boot-sample-app.Jenkinsfile"
  bash "$repo/scripts/verify-corpus.sh"
  sha256sum "$file"
  grep -nF "junit '**//*target/surefire-reports/TEST-*.xml'" "$file"
  printf 'exact-occurrences='
  grep -Fc "junit '**//*target/surefire-reports/TEST-*.xml'" "$file"
  printf 'exact-files='
  rg -l -F "junit '**//*target/surefire-reports/TEST-*.xml'" "$corpus/jenkinsfiles" | wc -l | tr -d ' '
  rg -nF 'Romeh_spring-boot-sample-app.Jenkinsfile' "$repo/corpus/BASELINE-DECLARATIVE-SCORE.tsv" "$repo/docs/COMPATIBILITY-LEDGER.tsv"
}

capture_container() {
  ssh "$FG218_JENKINS_HOST" \
    "podman inspect $FG218_JENKINS_CONTAINER --format '{{.Id}}|{{.ImageName}}|{{.ImageDigest}}'"
}

verify_before > "$stage/oracle-before.txt"
capture_junit_jar > "$stage/junit-jar-before.txt"
capture_junit_private > "$stage/junit-bytecode-before.txt"
capture_core_ant_jars > "$stage/core-ant-jars-before.txt"
capture_core_private > "$stage/core-bytecode-before.txt"
capture_ant_private > "$stage/ant-bytecode-before.txt"
capture_tokenized_pattern_private > "$stage/tokenized-pattern-bytecode-before.txt"
capture_selector_utils_private > "$stage/selector-utils-bytecode-before.txt"
capture_ant_match_matrix > "$stage/ant-match-matrix-before.txt"
capture_container > "$stage/container-before.txt"
capture_corpus > "$stage/corpus-proof.txt"

cd "$repo"
configure_differential_commands "$FG218_JENKINS_HOST" "$FG218_JENKINS_CONTAINER"
dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --nologo > "$stage/build.log"

case_inputs=()
for case_name in "${case_names[@]}"; do
  case_inputs+=("$stage/inputs/$case_name.Jenkinsfile")
done

dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  "$FG218_JENKINS_URL" "$FG218_JENKINS_CORE" "$stage/receipts" \
  "${case_inputs[@]}" > "$stage/run.log" 2>&1
printf 'differential-cli-exit=0\n' > "$stage/exit.txt"
cleanup_workspaces

verify_after > "$stage/oracle-after.txt"
capture_junit_jar > "$stage/junit-jar-after.txt"
capture_junit_private > "$stage/junit-bytecode-after.txt"
capture_core_ant_jars > "$stage/core-ant-jars-after.txt"
capture_core_private > "$stage/core-bytecode-after.txt"
capture_ant_private > "$stage/ant-bytecode-after.txt"
capture_tokenized_pattern_private > "$stage/tokenized-pattern-bytecode-after.txt"
capture_selector_utils_private > "$stage/selector-utils-bytecode-after.txt"
capture_ant_match_matrix > "$stage/ant-match-matrix-after.txt"
capture_container > "$stage/container-after.txt"

{
  cmp "$stage/oracle-before.txt" "$stage/oracle-after.txt"
  cmp "$stage/junit-jar-before.txt" "$stage/junit-jar-after.txt"
  cmp "$stage/junit-bytecode-before.txt" "$stage/junit-bytecode-after.txt"
  cmp "$stage/core-ant-jars-before.txt" "$stage/core-ant-jars-after.txt"
  cmp "$stage/core-bytecode-before.txt" "$stage/core-bytecode-after.txt"
  cmp "$stage/ant-bytecode-before.txt" "$stage/ant-bytecode-after.txt"
  cmp "$stage/tokenized-pattern-bytecode-before.txt" "$stage/tokenized-pattern-bytecode-after.txt"
  cmp "$stage/selector-utils-bytecode-before.txt" "$stage/selector-utils-bytecode-after.txt"
  cmp "$stage/ant-match-matrix-before.txt" "$stage/ant-match-matrix-after.txt"
  cmp "$stage/container-before.txt" "$stage/container-after.txt"
  grep -Fx '7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142  /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar' "$stage/junit-jar-before.txt"
  grep -Fx '07511327f8f69b4abdab17705f99c5de16bcb751b79e1403f4ac80ac151b3e6c  /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar' "$stage/core-ant-jars-before.txt"
  grep -Fx '8be692e02837f41a47a3d21cde6655792142fdf42fe23bcb16d7129cad9b2284  /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar' "$stage/core-ant-jars-before.txt"
  grep -F 'public static org.apache.tools.ant.types.FileSet createFileSet' "$stage/core-bytecode-before.txt"
  grep -F 'Method hudson/Util.createFileSet:' "$stage/junit-bytecode-before.txt"
  grep -F 'private static java.lang.String normalizePattern(java.lang.String);' "$stage/ant-bytecode-before.txt"
  grep -E 'SelectorUtils\.tokenizePathAsArray:\(Ljava/lang/String;\)\[Ljava/lang/String;' "$stage/tokenized-pattern-bytecode-before.txt"
  grep -F 'descriptor: (Lorg/apache/tools/ant/types/selectors/TokenizedPath;Z)Z' "$stage/tokenized-pattern-bytecode-before.txt"
  grep -E 'SelectorUtils\.matchPath:\(\[Ljava/lang/String;\[Ljava/lang/String;Z\)Z' "$stage/tokenized-pattern-bytecode-before.txt"
  grep -F 'descriptor: (Ljava/lang/String;)[Ljava/lang/String;' "$stage/selector-utils-bytecode-before.txt"
  grep -F 'Method org/apache/tools/ant/util/FileUtils.isAbsolutePath:' "$stage/selector-utils-bytecode-before.txt"
  grep -F 'Field java/io/File.separatorChar:C' "$stage/selector-utils-bytecode-before.txt"
  grep -E '^[[:space:]]+63: if_icmpeq[[:space:]]+69$' "$stage/selector-utils-bytecode-before.txt"
  grep -E '^[[:space:]]+66: iinc[[:space:]]+5, 1$' "$stage/selector-utils-bytecode-before.txt"
  grep -E '^[[:space:]]+149: if_icmpeq[[:space:]]+171$' "$stage/selector-utils-bytecode-before.txt"
  grep -E 'substring:\(II\)Ljava/lang/String;' "$stage/selector-utils-bytecode-before.txt"
  grep -Fx 'single=true' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'doubled=true' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'many=true' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'case-decoy=false' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'rooted=false' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'exact-occurrences=2' "$stage/corpus-proof.txt"
  grep -Fx 'exact-files=1' "$stage/corpus-proof.txt"
  grep -F $'Romeh_spring-boot-sample-app.Jenkinsfile\tok\t-\t9\t23' "$stage/corpus-proof.txt"
  grep -F $'Romeh_spring-boot-sample-app.Jenkinsfile\tadmitted' "$stage/corpus-proof.txt"
  printf 'Ant tokenizer: single, doubled, and wider internal runs match; case and rooted controls remain false\n'
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
    configure_differential_commands fg218-proof-host fg218-proof-container
    for command_var in FOGELL_JENKINS_WORKSPACE_CMD FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD FOGELL_JENKINS_WIPE_CMD; do
      command_value=${!command_var}
      [[ "$command_value" == "ssh fg218-proof-host "* ]]
      [[ "$command_value" == *"podman exec fg218-proof-container "* ]]
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
