#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

export FOGELL_JENKINS_WORKSPACE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\""'
export FOGELL_JENKINS_ENV_CMD='ssh luigi "podman exec jenkins-lab env"'
export FOGELL_JENKINS_GIT_VERSION_CMD='ssh luigi "podman exec jenkins-lab git --version"'
export FOGELL_JENKINS_WIPE_CMD='ssh luigi "podman exec jenkins-lab sh -c \"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\""'
export FOGELL_SCM_URL='git://100.105.179.51/repo.git'

dotnet run --project tools/Fogell.Differential.Cli -c Release --no-build -- \
  http://127.0.0.1:18099 \
  2.568.1 \
  evidence/20260818-fg-177-measurement/raw-receipts \
  evidence/20260818-fg-177-measurement/cases/fg177-probe-archive-schema.Jenkinsfile
