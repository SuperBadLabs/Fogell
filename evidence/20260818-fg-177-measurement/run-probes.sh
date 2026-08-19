#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

requested_jenkins_url=${FOGELL_JENKINS_URL:-http://127.0.0.1:18099}
requested_jenkins_host=${FOGELL_JENKINS_HOST:-luigi}
requested_jenkins_container=${FOGELL_JENKINS_CONTAINER:-jenkins-lab}
if [[ ${FOGELL_JENKINS_CORE+x} ]]; then
  requested_jenkins_core=$FOGELL_JENKINS_CORE
else
  requested_jenkins_core=2.568.1
fi
if [[ ! "$requested_jenkins_core" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
  printf 'ERROR: FOGELL_JENKINS_CORE must be one canonical non-empty version, got %q\n' \
    "$requested_jenkins_core" >&2
  exit 2
fi
: "${FOGELL_SCM_URL:=git://100.105.179.51/repo.git}"
export FOGELL_SCM_URL

export FOGELL_JENKINS_WORKSPACE_CMD="ssh ${requested_jenkins_host} \"podman exec ${requested_jenkins_container} sh -c \\\"cd /var/jenkins_home/workspace/{job} 2>/dev/null && find . -type f | sort | xargs -r sha256sum\\\"\""
export FOGELL_JENKINS_ENV_CMD="ssh ${requested_jenkins_host} \"podman exec ${requested_jenkins_container} env\""
export FOGELL_JENKINS_GIT_VERSION_CMD="ssh ${requested_jenkins_host} \"podman exec ${requested_jenkins_container} git --version\""
export FOGELL_JENKINS_WIPE_CMD="ssh ${requested_jenkins_host} \"podman exec ${requested_jenkins_container} sh -c \\\"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp\\\"\""

evidence_root='evidence/20260818-fg-177-measurement'
: "${FOGELL_EVIDENCE_OUT:=$evidence_root}"
: "${FOGELL_JENKINS_ORACLE_DIR:=$evidence_root}"
out="$FOGELL_EVIDENCE_OUT"
stage=''
oracle_snapshot_parent=''
# Invoked indirectly by EXIT/signal traps.
# shellcheck disable=SC2317
cleanup() {
  if [[ -n "$stage" && -e "$stage" ]]; then
    case "$stage" in
      "$publication_parent"/.probes-stage.*) rm -rf -- "$stage" ;;
      *) printf 'ERROR: refusing unsafe stage cleanup: %s\n' "$stage" >&2 ;;
    esac
  fi
  if [[ -n "$oracle_snapshot_parent" && -e "$oracle_snapshot_parent" ]]; then
    case "$oracle_snapshot_parent" in
      /tmp/tmp.*) rm -rf -- "$oracle_snapshot_parent" ;;
      *) printf 'ERROR: refusing unsafe oracle snapshot cleanup: %s\n' \
           "$oracle_snapshot_parent" >&2 ;;
    esac
  fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
run_started_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
oracle_snapshot_parent=$(mktemp -d)
oracle_snapshot="$oracle_snapshot_parent/oracle-metadata"
oracle_verification_before=$(
  bash "$evidence_root/verify-run-oracle.sh" \
    "$requested_jenkins_url" "$requested_jenkins_core" \
    "$requested_jenkins_host" "$requested_jenkins_container" \
    "$FOGELL_JENKINS_ORACLE_DIR" "$oracle_snapshot"
)
oracle_verified_before_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
printf '%s\n' "$oracle_verification_before"
publication_parent="$out/runs"
publication_target="$publication_parent/probes"
mkdir -p "$publication_parent"
exec {publication_lock_fd}> "$publication_parent/probes.lock"
if ! flock -n "$publication_lock_fd"; then
  echo "ERROR: another probe evidence run owns $publication_target" >&2
  exit 1
fi
stage=$(mktemp -d "$publication_parent/.probes-stage.XXXXXX")
mv -- "$oracle_snapshot" "$stage/oracle-metadata"
rmdir "$oracle_snapshot_parent"
oracle_snapshot_parent=''
printf '%s\n' "$oracle_verification_before" > "$stage/oracle-before-verification.txt"
rendered="$stage/rendered-cases"
mkdir -p "$stage/raw-receipts"
FOGELL_SCM_PIN_OUTPUT="$stage/scm-pin.tsv" \
  bash "$evidence_root/sync-checkout-scm-fixture.sh"
scm_pinned_branch=$(awk -F '\t' '$1 == "scm-pinned-branch" {print $2}' "$stage/scm-pin.tsv")
scm_pinned_revision=$(awk -F '\t' '$1 == "scm-pinned-revision" {print $2}' "$stage/scm-pin.tsv")
git_pinned_branch=$(awk -F '\t' '$1 == "git-pinned-branch" {print $2}' "$stage/scm-pin.tsv")
if [[ ! "$scm_pinned_revision" =~ ^[0-9a-f]{40}$ ||
      "$scm_pinned_branch" != "fogell-pins/$scm_pinned_revision" ||
      ! "$git_pinned_branch" =~ ^fogell-pins/[0-9a-f]{40}$ ]]; then
  echo 'ERROR: SCM helper emitted a malformed content-addressed pin' >&2
  exit 1
fi
export FOGELL_SCM_PINNED_BRANCH="$scm_pinned_branch"
export FOGELL_SCM_PINNED_REVISION="$scm_pinned_revision"
export FOGELL_GIT_PINNED_BRANCH="$git_pinned_branch"
FOGELL_RENDERED_CASES_DIR="$rendered" \
  python3 "$evidence_root/render-probe-cases.py"

cli_project='tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj'
dotnet build "$cli_project" -c Release --nologo

cases=(
  "$rendered/fg177-probe-unknown-policy.Jenkinsfile"
  "$rendered/fg177-probe-requiredness.Jenkinsfile"
  "$rendered/fg177-probe-return-semantics.Jenkinsfile"
  "$rendered/fg177-probe-checkout-scm.Jenkinsfile"
)

set +e
FOGELL_SCM_ATTESTATION=fg177-probes-v1 \
dotnet run --project "$cli_project" -c Release --no-build -- \
  "$requested_jenkins_url" \
  "$requested_jenkins_core" \
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

PYTHONDONTWRITEBYTECODE=1 python3 "$evidence_root/validate-scm-execution.py" \
  "$stage/scm-pin.tsv" "$stage/raw-receipts" "$stage/scm-execution.tsv"

oracle_verification_after=$(
  bash "$evidence_root/verify-run-oracle.sh" \
    "$requested_jenkins_url" "$requested_jenkins_core" \
    "$requested_jenkins_host" "$requested_jenkins_container" \
    "$stage/oracle-metadata"
)
oracle_verified_after_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
printf '%s\n' "$oracle_verification_after"
printf '%s\n' "$oracle_verification_after" > "$stage/oracle-after-verification.txt"
if ! cmp -s "$stage/oracle-before-verification.txt" \
    "$stage/oracle-after-verification.txt"; then
  echo 'ERROR: Jenkins oracle identity changed during probe CLI; prior evidence retained' >&2
  exit 1
fi

printf 'probe-cli-exit=%s\n' "$rc" | tee "$stage/probe-exit.txt"
run_finished_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
bash "$evidence_root/write-run-manifest.sh" \
  "$stage/probe-run-manifest.tsv" probes \
  "$run_started_at" "$oracle_verified_before_at" \
  "$oracle_verified_after_at" "$run_finished_at" \
  "$rc" probe-cli-exit "$stage/probe-run.log" "$stage/probe-exit.txt" \
  "$requested_jenkins_core" "$stage/oracle-metadata" \
  "$stage/oracle-before-verification.txt" \
  "$stage/oracle-after-verification.txt" "${cases[@]}"
python3 "$evidence_root/publish-run-bundle.py" "$stage" "$publication_target"
exit "$rc"
