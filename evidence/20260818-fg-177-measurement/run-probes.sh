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
: "${FOGELL_JENKINS_ORACLE_DIR:=$evidence_root}"
out="$FOGELL_EVIDENCE_OUT"
run_started_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
oracle_verification=$(
  bash "$evidence_root/jenkins-oracle.sh" verify "$FOGELL_JENKINS_ORACLE_DIR"
)
oracle_verified_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
printf '%s\n' "$oracle_verification"
publication_parent="$out/runs"
publication_target="$publication_parent/probes"
mkdir -p "$publication_parent"
exec {publication_lock_fd}> "$publication_parent/probes.lock"
if ! flock -n "$publication_lock_fd"; then
  echo "ERROR: another probe evidence run owns $publication_target" >&2
  exit 1
fi
stage=''
# Invoked indirectly by EXIT/signal traps.
# shellcheck disable=SC2317
cleanup() {
  if [[ -n "$stage" && -e "$stage" ]]; then
    case "$stage" in
      "$publication_parent"/.probes-stage.*) rm -rf -- "$stage" ;;
      *) printf 'ERROR: refusing unsafe stage cleanup: %s\n' "$stage" >&2 ;;
    esac
  fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
stage=$(mktemp -d "$publication_parent/.probes-stage.XXXXXX")
rendered="$stage/rendered-cases"
mkdir -p "$stage/raw-receipts"
FOGELL_RENDERED_CASES_DIR="$rendered" \
  python3 "$evidence_root/render-probe-cases.py"
bash "$evidence_root/sync-checkout-scm-fixture.sh"

cli_project='tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj'
dotnet build "$cli_project" -c Release --nologo

cases=(
  "$rendered/fg177-probe-unknown-policy.Jenkinsfile"
  "$rendered/fg177-probe-requiredness.Jenkinsfile"
  "$rendered/fg177-probe-return-semantics.Jenkinsfile"
  "$rendered/fg177-probe-checkout-scm.Jenkinsfile"
)

set +e
dotnet run --project "$cli_project" -c Release --no-build -- \
  "$FOGELL_JENKINS_URL" \
  "$FOGELL_JENKINS_CORE" \
  "$stage/raw-receipts" \
  "${cases[@]}" \
  2>&1 | tee "$stage/probe-run.log"
rc=${PIPESTATUS[0]}
set -e

if [[ $rc -ne 0 && $rc -ne 1 ]]; then
  printf 'ERROR: probe CLI did not complete its comparison contract (rc=%s); prior evidence retained\n' \
    "$rc" >&2
  exit "$rc"
fi

printf 'probe-cli-exit=%s\n' "$rc" | tee "$stage/probe-exit.txt"
run_finished_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
bash "$evidence_root/write-run-manifest.sh" \
  "$stage/probe-run-manifest.tsv" probes \
  "$run_started_at" "$oracle_verified_at" "$run_finished_at" \
  "$rc" probe-cli-exit "$stage/probe-run.log" "$stage/probe-exit.txt" \
  "$FOGELL_JENKINS_ORACLE_DIR" "${cases[@]}"
python3 "$evidence_root/publish-run-bundle.py" "$stage" "$publication_target"
exit "$rc"
