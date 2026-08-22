#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root="$repo/evidence/20260818-fg-177-measurement"
output="$bundle/run"

: "${FG177_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FG177_JENKINS_CORE:=2.568.1}"
: "${FG177_ORACLE_SSH_HOST:=luigi}"
: "${FG177_JENKINS_CONTAINER:=jenkins-lab}"
export FG177_JENKINS_URL FG177_JENKINS_CORE FG177_ORACLE_SSH_HOST FG177_JENKINS_CONTAINER

if [[ -e "$output" ]]; then
  echo "ERROR: refusing to overwrite retained run: $output" >&2
  exit 1
fi

stage=$(mktemp -d "$bundle/.stage.XXXXXX")
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/inputs" "$stage/runs" "$stage/tooling"

cp "$bundle/expected.tsv" "$stage/expected.tsv"
for case in b0s0 b0s1 b1s0 b1s1; do
  source_case="$repo/differential/cases/fg177-junit-stage-matrix-$case.Jenkinsfile"
  [[ -s "$source_case" ]] || { echo "ERROR: missing matrix case $source_case" >&2; exit 1; }
  cp "$source_case" "$stage/inputs/$case.Jenkinsfile"
done

cp "$bundle/README.md" "$stage/tooling/README.md"
cp "$bundle/collect.sh" "$stage/tooling/collect.sh"
cp "$bundle/jenkins-driver.py" "$stage/tooling/jenkins-driver.py"
cp "$bundle/capture-surface.py" "$stage/tooling/capture-surface.py"
cp "$bundle/validate-stage-run.py" "$stage/tooling/validate-stage-run.py"
cp "$bundle/expected.tsv" "$stage/tooling/expected.tsv"
cp -R "$bundle/tests" "$stage/tooling/tests"

verify_oracle() {
  local destination=$1
  bash "$pin_root/verify-run-oracle.sh" \
    "$FG177_JENKINS_URL" "$FG177_JENKINS_CORE" \
    "$FG177_ORACLE_SSH_HOST" "$FG177_JENKINS_CONTAINER" \
    "$pin_root" "$destination"
}

verify_oracle "$stage/oracle-snapshot-before" > "$stage/oracle-before-verification.txt"
python3 "$bundle/capture-surface.py" "$stage/surface-before"

run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
printf 'run-id\t%s\n' "$run_id" > "$stage/attribution.tsv"
for case in b0s0 b0s1 b1s0 b1s1; do
  job="fg177-stage-${run_id}-${case}"
  python3 "$bundle/jenkins-driver.py" run \
    "$stage/inputs/$case.Jenkinsfile" "$job" "$stage/runs/$case"
done

if [[ "${FG177_RUN_DIFFERENTIAL:-0}" == 1 ]]; then
  mkdir -p "$stage/receipts"
  export FOGELL_JENKINS_WORKSPACE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\""'
  export FOGELL_JENKINS_ENV_CMD='ssh luigi "podman exec jenkins-lab env"'
  export FOGELL_JENKINS_GIT_VERSION_CMD='ssh luigi "podman exec jenkins-lab git --version"'
  export FOGELL_JENKINS_WIPE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\""'
  dotnet build "$repo/tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj" -c Release --nologo \
    > "$stage/differential-build.log"
  dotnet run --project "$repo/tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj" \
    -c Release --no-build -- "$FG177_JENKINS_URL" "$FG177_JENKINS_CORE" "$stage/receipts" \
    "$stage/inputs/b0s0.Jenkinsfile" "$stage/inputs/b0s1.Jenkinsfile" \
    "$stage/inputs/b1s0.Jenkinsfile" "$stage/inputs/b1s1.Jenkinsfile" \
    > "$stage/differential-run.log" 2>&1
  printf 'differential-cli-exit=0\n' > "$stage/differential-exit.txt"
else
  printf 'differential-cli=not-run; stage status is outside the current receipt contract\n' \
    > "$stage/differential-hook.txt"
fi

verify_oracle "$stage/oracle-snapshot-after" > "$stage/oracle-after-verification.txt"
python3 "$bundle/capture-surface.py" "$stage/surface-after"

printf 'COMPLETE\n' > "$stage/STATUS"
python3 "$bundle/validate-stage-run.py" "$stage" > "$stage/validation.txt"

(
  cd "$stage"
  find . -type f ! -name MANIFEST.sha256 -print0 | sort -z | xargs -0 sha256sum > MANIFEST.sha256
)

mv "$stage" "$output"
trap - EXIT
printf 'EVIDENCE_DIR=%s\n' "$output"
