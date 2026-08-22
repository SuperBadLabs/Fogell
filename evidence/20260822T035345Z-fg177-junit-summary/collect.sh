#!/usr/bin/env bash
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$bundle/../.." && pwd)
pin_root=$repo/evidence/20260818-fg-177-measurement
case_file=$repo/differential/cases/fg177-junit-summary-counts.Jenkinsfile
stage=$(mktemp -d /tmp/fg177-junit-summary-evidence.XXXXXX)
mkdir -p "$stage/receipts"

verify_before() {
  bash "$pin_root/verify-run-oracle.sh" \
    http://127.0.0.1:18099 2.568.1 luigi jenkins-lab "$pin_root" "$1"
}

verify_after() {
  bash "$pin_root/verify-run-oracle.sh" \
    http://127.0.0.1:18099 2.568.1 luigi jenkins-lab "$1"
}

verify_before "$stage/oracle-metadata" | tee "$stage/oracle-before.txt"
ssh luigi 'podman exec jenkins-lab sh -c "sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar; javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar hudson.tasks.junit.TestResultSummary"' \
  > "$stage/junit-surface-before.txt"
ssh luigi 'podman inspect jenkins-lab --format "{{.Id}}|{{.ImageName}}|{{.ImageDigest}}"' \
  > "$stage/container-before.txt"
cp "$case_file" "$stage/fg177-junit-summary-counts.Jenkinsfile"

cd "$repo"
export FOGELL_JENKINS_WORKSPACE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\""'
export FOGELL_JENKINS_ENV_CMD='ssh luigi "podman exec jenkins-lab env"'
export FOGELL_JENKINS_GIT_VERSION_CMD='ssh luigi "podman exec jenkins-lab git --version"'
export FOGELL_JENKINS_WIPE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\""'

dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --nologo \
  > "$stage/build.log"
dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --no-build -- \
  http://127.0.0.1:18099 2.568.1 "$stage/receipts" "$stage/fg177-junit-summary-counts.Jenkinsfile" \
  > "$stage/run.log" 2>&1
printf 'probe-cli-exit=0\n' > "$stage/exit.txt"
cmp "$case_file" "$stage/fg177-junit-summary-counts.Jenkinsfile"

verify_after "$stage/oracle-metadata" | tee "$stage/oracle-after.txt"
ssh luigi 'podman exec jenkins-lab sh -c "sha256sum /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar; javap -classpath /var/jenkins_home/plugins/junit/WEB-INF/lib/junit.jar hudson.tasks.junit.TestResultSummary"' \
  > "$stage/junit-surface-after.txt"
ssh luigi 'podman inspect jenkins-lab --format "{{.Id}}|{{.ImageName}}|{{.ImageDigest}}"' \
  > "$stage/container-after.txt"

cmp "$stage/oracle-before.txt" "$stage/oracle-after.txt"
cmp "$stage/junit-surface-before.txt" "$stage/junit-surface-after.txt"
cmp "$stage/container-before.txt" "$stage/container-after.txt"

(
  cd "$stage"
  find . -type f ! -name MANIFEST.sha256 -print0 | sort -z | xargs -0 sha256sum > MANIFEST.sha256
)

printf 'EVIDENCE_DIR=%s\n' "$stage"
