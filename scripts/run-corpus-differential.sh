#!/usr/bin/env bash
# The corpus lane: execute PINNED CORPUS FILES on both engines, under the
# no-egress fence the operating contract requires, and only after the fence
# is PROVEN on both sides. FG-200 walked the tier-1 path once for an inert
# surface; this lane is what lets a file with an executed surface (`sh`) walk
# it under the rule rather than around it.
#
#   scripts/run-corpus-differential.sh <corpus-file.Jenkinsfile>...
#
# Refuses, in order: a file outside the pinned corpus; a corpus whose manifest
# does not verify; a missing differential CLI build; a Jenkins-side fence that
# cannot be applied or proven; a Fogell-side fence that cannot be proven. The
# Jenkins fence is removed on exit whatever happens short of ssh itself
# failing, so the hand-written lane (whose git-step cases reach the SCM
# daemon) finds the lab as it was; the runbook says how to remove a leftover.
#
# Environment (the same names run-differential.sh uses):
#   FOGELL_CORPUS            pinned corpus root   (default /sn8100/work/exchange/crucible-gate/corpus)
#   FOGELL_JENKINS_URL       oracle               (default http://luigi:18083)
#   FOGELL_JENKINS_CORE      pinned core          (default 2.568.1)
#   FOGELL_JENKINS_HOST      ssh host             (default luigi)
#   FOGELL_JENKINS_CONTAINER container            (default jenkins-lab)
#   FOGELL_RECEIPT_DIR       where receipts land  (default differential/receipts)
set -Eeuo pipefail
cd "$(dirname "$0")/.."

: "${FOGELL_CORPUS:=/sn8100/work/exchange/crucible-gate/corpus}"
: "${FOGELL_JENKINS_URL:=http://luigi:18083}"
: "${FOGELL_JENKINS_CORE:=2.568.1}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"
: "${FOGELL_RECEIPT_DIR:=differential/receipts}"
export FOGELL_CORPUS FOGELL_JENKINS_URL FOGELL_JENKINS_CORE FOGELL_JENKINS_HOST FOGELL_JENKINS_CONTAINER

die() { printf 'corpus lane: REFUSED: %s\n' "$*" >&2; exit 2; }
[ $# -gt 0 ] || die "name at least one corpus file"

corpus_dir=$(realpath -e "$FOGELL_CORPUS/jenkinsfiles" 2>/dev/null) || die "corpus not found at $FOGELL_CORPUS/jenkinsfiles"
files=()
for f in "$@"; do
  r=$(realpath -e "$f" 2>/dev/null) || die "$f does not exist"
  case "$r" in "$corpus_dir"/*.Jenkinsfile) files+=("$r") ;; *) die "$f is not a pinned corpus file under $corpus_dir — this lane executes corpus files only" ;; esac
done

echo "corpus lane: verifying the pinned manifest"
./scripts/verify-corpus.sh >/dev/null || die "corpus manifest did not verify — nothing executes against a drifted corpus"

cli=tools/Fogell.Differential.Cli/bin/Release/net10.0/fogell-diff.dll
[ -f "$cli" ] || die "$cli is missing — build it first: dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release"

# shellcheck source=scripts/jenkins-workspace-v2.sh disable=SC1091
source scripts/jenkins-workspace-v2.sh || die "workspace collector could not be loaded"
fogell_configure_jenkins_workspace_v2 "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER" || die "workspace collector could not be configured"
export FOGELL_JENKINS_ENV_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} env\""
export FOGELL_JENKINS_GIT_VERSION_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} git --version\""

# One lane per user on this host: a second lane's exit trap would remove this
# lane's Jenkins fence (measured, fixed). The lock lives in the user's runtime
# dir; the oracle's busy check below is the only cross-user, cross-host guard.
exec 9>"${XDG_RUNTIME_DIR:-/tmp}/fogell-corpus-lane.lock"
flock -n 9 || die "another corpus lane of this user holds ${XDG_RUNTIME_DIR:-/tmp}/fogell-corpus-lane.lock"

busy=$(curl -sS -m 10 "$FOGELL_JENKINS_URL/computer/api/json?tree=busyExecutors" 2>/dev/null | sed -n 's/.*"busyExecutors":\([0-9]*\).*/\1/p')
[ "${busy:-x}" = "0" ] || die "oracle reports busyExecutors=${busy:-unknown}; the lane is single-tenant"

started=$(date -u +%FT%TZ)
echo "corpus lane: applying and proving the Jenkins-side fence"
./scripts/no-egress-fence.sh jenkins apply
# `|| true`: errexit is live inside an EXIT trap, so a failed quiesce must not
# skip the removal — a lingering Jenkins fence is what breaks the other lane.
trap './scripts/no-egress-fence.sh jenkins quiesce || true; ./scripts/no-egress-fence.sh jenkins remove' EXIT
./scripts/no-egress-fence.sh jenkins verify

echo "corpus lane: proving the Fogell-side fence, then running ${#files[@]} corpus file(s)"
./scripts/no-egress-fence.sh fogell run -- \
  dotnet "$cli" "$FOGELL_JENKINS_URL" "$FOGELL_JENKINS_CORE" "$FOGELL_RECEIPT_DIR" "${files[@]}" && rc=0 || rc=$?
echo "corpus lane: finished at $(date -u +%FT%TZ) (started $started), differential exit $rc"
exit "$rc"
