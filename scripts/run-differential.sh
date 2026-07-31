#!/usr/bin/env bash
# FG-002/002b. Runs every differential case through both engines and seals
# receipts. Exits non-zero unless every case is FULLY proven.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

: "${FOGELL_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FOGELL_JENKINS_CORE:=2.568.1}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"

# The five `withCredentials` receipts need the same fake values on BOTH sides.
# Without this a fresh shell simply has no store, those cases fail CLOSED, and the
# run reads as a code regression — which cost a full debugging cycle once. The path
# is exactly what provision-credentials.sh writes and prints an `export` line for.
: "${FOGELL_CREDENTIALS_FILE:=$PWD/.fogell-credentials.tsv}"
if [[ -f "$FOGELL_CREDENTIALS_FILE" ]]; then
  export FOGELL_CREDENTIALS_FILE
else
  echo "note: no credential store at $FOGELL_CREDENTIALS_FILE — withCredentials cases" \
       "will fail closed. Run scripts/provision-credentials.sh first." >&2
fi

# Jenkins does not share a filesystem with us, so the workspace is hashed WHERE
# IT LIVES using the same manifest form as a local hash (see Trace.collectRemote).
export FOGELL_JENKINS_WORKSPACE_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} sh -c \\\"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\\\"\""
export FOGELL_JENKINS_WIPE_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} sh -c \\\"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\\\"\""

dotnet build -c Release --nologo >/dev/null 2>&1 || { echo "build failed"; exit 1; }
exec dotnet run --project tools/Fogell.Differential.Cli -c Release --no-build -- \
  "$FOGELL_JENKINS_URL" "$FOGELL_JENKINS_CORE" differential/receipts differential/cases/*.Jenkinsfile
