#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

export FOGELL_JENKINS_WORKSPACE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\""'
export FOGELL_JENKINS_ENV_CMD='ssh luigi "podman exec jenkins-lab env"'
export FOGELL_JENKINS_GIT_VERSION_CMD='ssh luigi "podman exec jenkins-lab git --version"'
export FOGELL_JENKINS_WIPE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\""'
export FOGELL_SCM_URL='git://100.105.179.51/repo.git'

out='evidence/20260818-fg-177-measurement'
mkdir -p "$out/raw-receipts"

set +e
dotnet run --project tools/Fogell.Differential.Cli -c Release --no-build -- \
  http://127.0.0.1:18099 \
  2.568.1 \
  "$out/raw-receipts" \
  "$out/cases/fg177-probe-unknown-policy.Jenkinsfile" \
  "$out/cases/fg177-probe-requiredness.Jenkinsfile" \
  "$out/cases/fg177-probe-return-semantics.Jenkinsfile" \
  "$out/cases/fg177-probe-checkout-scm.Jenkinsfile" \
  2>&1 | tee "$out/probe-run.log"
rc=${PIPESTATUS[0]}
set -e

printf 'probe-cli-exit=%s\n' "$rc" | tee "$out/probe-exit.txt"
exit 0
