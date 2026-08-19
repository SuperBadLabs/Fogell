#!/usr/bin/env bash
# Verify one explicit Jenkins identity snapshot for an evidence runner.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

if [[ $# -lt 5 || $# -gt 6 ]]; then
  echo "usage: $0 JENKINS_URL JENKINS_CORE JENKINS_HOST JENKINS_CONTAINER METADATA_DIR [SNAPSHOT_DESTINATION]" >&2
  exit 2
fi

jenkins_url=$1
jenkins_core=$2
jenkins_host=$3
jenkins_container=$4
metadata=$5
snapshot_destination=${6:-}
evidence_root='evidence/20260818-fg-177-measurement'

FOGELL_JENKINS_URL="$jenkins_url" \
FOGELL_JENKINS_CORE="$jenkins_core" \
FOGELL_JENKINS_HOST="$jenkins_host" \
FOGELL_JENKINS_CONTAINER="$jenkins_container" \
  bash "$evidence_root/jenkins-oracle.sh" verify "$metadata" "$snapshot_destination"
