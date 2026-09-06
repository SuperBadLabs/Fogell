#!/usr/bin/env bash
# Full differential with EVERY canonicalisation hook the harness provides,
# configured the way scripts/run-differential.sh does. Receipts go to a scratch
# dir so the committed sealed evidence is never overwritten.
set -uo pipefail
cd "$(dirname "$0")"
export FOGELL_JENKINS_HOST=mario
export FOGELL_JENKINS_CONTAINER=jenkins-faceoff
export FOGELL_JENKINS_URL=http://100.127.170.90:18086
export FOGELL_JENKINS_CORE=2.568.1
export FOGELL_SCM_URL=git://100.105.179.51/repo.git

source scripts/jenkins-workspace-v2.sh || { echo "collector load failed"; exit 2; }
fogell_configure_jenkins_workspace_v2 "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER" \
  || { echo "collector configure failed"; exit 2; }
export FOGELL_JENKINS_ENV_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} env\""
export FOGELL_JENKINS_GIT_VERSION_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} git --version\""

echo "workspace_cmd set: ${FOGELL_JENKINS_WORKSPACE_CMD:+yes}"
echo "wipe_cmd set:      ${FOGELL_JENKINS_WIPE_CMD:+yes}"
echo "env_cmd set:       ${FOGELL_JENKINS_ENV_CMD:+yes}"
echo "gitver_cmd set:    ${FOGELL_JENKINS_GIT_VERSION_CMD:+yes}"
echo

OUT=$1; shift
mkdir -p "$OUT"
exec tools/Fogell.Differential.Cli/bin/Release/net10.0/fogell-diff \
  "$FOGELL_JENKINS_URL" "$FOGELL_JENKINS_CORE" "$OUT" "$@"
