#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

: "${FOGELL_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FOGELL_JENKINS_CORE:=2.568.1}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"
: "${FOGELL_SCM_URL:=git://100.105.179.51/repo.git}"
export FOGELL_SCM_URL

export FOGELL_JENKINS_WORKSPACE_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} sh -c \\\"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\\\"\""
export FOGELL_JENKINS_ENV_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} env\""
export FOGELL_JENKINS_GIT_VERSION_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} git --version\""
export FOGELL_JENKINS_WIPE_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} sh -c \\\"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\\\"\""

evidence_root='evidence/20260818-fg-177-measurement'
: "${FOGELL_EVIDENCE_OUT:=$evidence_root}"
out="$FOGELL_EVIDENCE_OUT"
rendered="$out/rendered-cases"
mkdir -p "$out/raw-receipts"
FOGELL_RENDERED_CASES_DIR="$rendered" \
  python3 "$evidence_root/render-probe-cases.py"
bash "$evidence_root/sync-checkout-scm-fixture.sh"

cli_project='tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj'
dotnet build "$cli_project" -c Release --nologo

set +e
dotnet run --project "$cli_project" -c Release --no-build -- \
  "$FOGELL_JENKINS_URL" \
  "$FOGELL_JENKINS_CORE" \
  "$out/raw-receipts" \
  "$rendered/fg177-probe-unknown-policy.Jenkinsfile" \
  "$rendered/fg177-probe-requiredness.Jenkinsfile" \
  "$rendered/fg177-probe-return-semantics.Jenkinsfile" \
  "$rendered/fg177-probe-checkout-scm.Jenkinsfile" \
  2>&1 | tee "$out/probe-run.log"
rc=${PIPESTATUS[0]}
set -e

printf 'probe-cli-exit=%s\n' "$rc" | tee "$out/probe-exit.txt"
exit "$rc"
