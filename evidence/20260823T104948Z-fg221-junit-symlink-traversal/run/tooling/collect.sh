#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root="$repo/evidence/20260818-fg-177-measurement"
output="$bundle/run"
case_names=(
  fg221-junit-symlink-loop
  fg221-junit-dangling-links
)

cleanup_workspaces() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG221_JENKINS_HOST" \
      "podman exec $FG221_JENKINS_CONTAINER rm -rf /var/jenkins_home/workspace/$job_name /var/jenkins_home/workspace/$job_name@tmp" \
      >/dev/null 2>&1 || true
  done
}

verify_workspaces_absent() {
  local case_name job_name
  for case_name in "${case_names[@]}"; do
    job_name="diff-$case_name"
    ssh "$FG221_JENKINS_HOST" \
      "podman exec $FG221_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name"
    ssh "$FG221_JENKINS_HOST" \
      "podman exec $FG221_JENKINS_CONTAINER test ! -e /var/jenkins_home/workspace/$job_name@tmp"
    printf 'workspace cleanup: %s and @tmp absent\n' "$job_name"
  done
}

: "${FG221_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FG221_JENKINS_CORE:=2.568.1}"
: "${FG221_JENKINS_HOST:=luigi}"
: "${FG221_JENKINS_CONTAINER:=jenkins-lab}"

for target_var in FG221_JENKINS_HOST FG221_JENKINS_CONTAINER; do
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
    "$FG221_JENKINS_URL" "$FG221_JENKINS_CORE" \
    "$FG221_JENKINS_HOST" "$FG221_JENKINS_CONTAINER" \
    "$pin_root" "$stage/oracle-metadata"
}

verify_after() {
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG221_JENKINS_URL" "$FG221_JENKINS_CORE" \
    "$FG221_JENKINS_HOST" "$FG221_JENKINS_CONTAINER" \
    "$stage/oracle-metadata"
}

capture_junit_jar() {
  ssh "$FG221_JENKINS_HOST" \
    "podman exec $FG221_JENKINS_CONTAINER sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar"
}

capture_junit_private() {
  ssh "$FG221_JENKINS_HOST" \
    "podman exec $FG221_JENKINS_CONTAINER javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar -c -p 'hudson.tasks.junit.JUnitParser\$ParseResultCallable' hudson.tasks.junit.JUnitResultArchiver hudson.tasks.junit.TestResult"
}

capture_core_ant_jars() {
  ssh "$FG221_JENKINS_HOST" \
    "podman exec $FG221_JENKINS_CONTAINER sha256sum /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar"
}

capture_core_private() {
  ssh "$FG221_JENKINS_HOST" \
    "podman exec $FG221_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/jenkins-core-2.568.1.jar -c -p hudson.Util"
}

capture_ant_private() {
  ssh "$FG221_JENKINS_HOST" \
    "podman exec $FG221_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar -p -c -s -v org.apache.tools.ant.types.AbstractFileSet org.apache.tools.ant.DirectoryScanner"
}

capture_tokenized_pattern_private() {
  ssh "$FG221_JENKINS_HOST" \
    "podman exec $FG221_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar -p -c -s org.apache.tools.ant.types.selectors.TokenizedPattern"
}

capture_selector_utils_private() {
  ssh "$FG221_JENKINS_HOST" \
    "podman exec $FG221_JENKINS_CONTAINER javap -classpath /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar -p -c -s org.apache.tools.ant.types.selectors.SelectorUtils"
}

capture_ant_match_matrix() {
  {
    ssh "$FG221_JENKINS_HOST" \
    "podman exec -i $FG221_JENKINS_CONTAINER jshell --feedback concise --class-path /var/jenkins_home/war/WEB-INF/lib/ant-1.10.17.jar" <<'EOF'
import org.apache.tools.ant.Project;
import org.apache.tools.ant.types.FileSet;
import java.nio.file.*;
import java.util.Comparator;
import java.util.Arrays;
void clean(Path path) throws Exception { if (Files.exists(path, LinkOption.NOFOLLOW_LINKS)) try (var paths = Files.walk(path)) { paths.sorted(Comparator.reverseOrder()).forEach(p -> { try { Files.deleteIfExists(p); } catch (Exception e) { throw new RuntimeException(e); } }); } }
String[] scan(Path base, String include) { var fs = new FileSet(); fs.setDir(base.toFile()); fs.setIncludes(include); return fs.getDirectoryScanner(new Project()).getIncludedFiles(); }
var root = Path.of("/tmp/fg221-ant-symlink-matrix");
var external = Path.of("/tmp/fg221-ant-symlink-external");
clean(root); clean(external); Files.createDirectories(root); Files.createDirectories(external);
var healthy = Files.createDirectories(root.resolve("healthy")); Files.writeString(healthy.resolve("real.xml"), "x"); Files.createSymbolicLink(healthy.resolve("file-link.xml"), Path.of("real.xml"));
var target = Files.createDirectories(healthy.resolve("target")); Files.writeString(target.resolve("result.xml"), "x"); Files.createSymbolicLink(healthy.resolve("dir-link"), Path.of("target"));
Files.writeString(external.resolve("outside.xml"), "x"); Files.createSymbolicLink(healthy.resolve("external-link"), external);
var broken = Files.createDirectories(root.resolve("broken")); Files.createSymbolicLink(broken.resolve("broken.xml"), Path.of("missing.xml")); Files.createSymbolicLink(broken.resolve("broken-dir"), Path.of("missing-dir")); Files.createSymbolicLink(broken.resolve("self.xml"), Path.of("self.xml"));
var reports = Files.createDirectories(root.resolve("reports")); Files.writeString(reports.resolve("result.xml"), "x"); Files.createSymbolicLink(reports.resolve("loop"), Path.of("."));
System.out.println("healthy-file=" + Arrays.toString(scan(root, "healthy/file-link.xml")));
System.out.println("healthy-dir=" + Arrays.toString(scan(root, "healthy/dir-link/*.xml")));
System.out.println("external-dir=" + Arrays.toString(scan(root, "healthy/external-link/*.xml")));
System.out.println("broken-literal=" + Arrays.toString(scan(root, "broken/broken.xml")));
System.out.println("broken-wildcard=" + Arrays.toString(scan(root, "broken/broken*.xml")));
System.out.println("broken-dir=" + Arrays.toString(scan(root, "broken/broken-dir/**/*.xml")));
System.out.println("self-file=" + Arrays.toString(scan(root, "broken/self*.xml")));
System.out.println("loop-count=" + scan(root, "reports/**/*.xml").length);
clean(root); clean(external);
/exit
EOF
  } | tr -d '\b' | grep -oE 'healthy-file=\[[^]]*\]|healthy-dir=\[[^]]*\]|external-dir=\[[^]]*\]|broken-literal=\[[^]]*\]|broken-wildcard=\[[^]]*\]|broken-dir=\[[^]]*\]|self-file=\[[^]]*\]|loop-count=[0-9]+'
}

capture_corpus() {
  local corpus=/sn8100/work/exchange/crucible-gate/corpus
  local junit_calls
  bash "$repo/scripts/verify-corpus.sh"
  junit_calls="$(rg -n --glob '*.Jenkinsfile' '^[[:space:]]*junit([ (]|$)' "$corpus/jenkinsfiles")"
  printf 'direct-junit-calls='
  printf '%s\n' "$junit_calls" | wc -l | tr -d ' '
  printf 'recursive-junit-calls='
  printf '%s\n' "$junit_calls" | grep -c '\*\*'
  printf 'symlink-indicators-on-junit-call-lines='
  printf '%s\n' "$junit_calls" | grep -Eic 'symlink|readlink|ln[[:space:]]+-s' || true
}

capture_container() {
  ssh "$FG221_JENKINS_HOST" \
    "podman inspect $FG221_JENKINS_CONTAINER --format '{{.Id}}|{{.ImageName}}|{{.ImageDigest}}'"
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
configure_differential_commands "$FG221_JENKINS_HOST" "$FG221_JENKINS_CONTAINER"
dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --nologo > "$stage/build.log"

case_inputs=()
for case_name in "${case_names[@]}"; do
  case_inputs+=("$stage/inputs/$case_name.Jenkinsfile")
done

dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  "$FG221_JENKINS_URL" "$FG221_JENKINS_CORE" "$stage/receipts" \
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
  grep -F 'Method java/io/File.length:()J' "$stage/junit-bytecode-before.txt"
  grep -F 'public static final int MAX_LEVELS_OF_SYMLINKS;' "$stage/ant-bytecode-before.txt"
  grep -F 'private boolean followSymlinks;' "$stage/ant-bytecode-before.txt"
  grep -F 'private int maxLevelsOfSymlinks;' "$stage/ant-bytecode-before.txt"
  grep -F 'causesIllegalSymlinkLoop' "$stage/ant-bytecode-before.txt"
  grep -F ' -- too many levels of symbolic links.' "$stage/ant-bytecode-before.txt"
  grep -Fx 'healthy-file=[healthy/file-link.xml]' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'healthy-dir=[healthy/dir-link/result.xml]' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'external-dir=[healthy/external-link/outside.xml]' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'broken-literal=[]' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'broken-wildcard=[broken/broken.xml]' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'broken-dir=[]' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'self-file=[broken/self.xml]' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'loop-count=6' "$stage/ant-match-matrix-before.txt"
  grep -Fx 'direct-junit-calls=36' "$stage/corpus-proof.txt"
  grep -Fx 'recursive-junit-calls=25' "$stage/corpus-proof.txt"
  grep -Fx 'symlink-indicators-on-junit-call-lines=0' "$stage/corpus-proof.txt"
  printf 'Ant symlink matrix: healthy targets followed, dangling fast/wildcard split pinned, self-loop bounded to six\n'
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
    configure_differential_commands fg221-proof-host fg221-proof-container
    for command_var in FOGELL_JENKINS_WORKSPACE_CMD FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD FOGELL_JENKINS_WIPE_CMD; do
      command_value=${!command_var}
      [[ "$command_value" == "ssh fg221-proof-host "* ]]
      [[ "$command_value" == *"podman exec fg221-proof-container "* ]]
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
  sha256sum -c MANIFEST.sha256
)

mv "$stage" "$output"
trap - EXIT
printf 'EVIDENCE_DIR=%s\n' "$output"
