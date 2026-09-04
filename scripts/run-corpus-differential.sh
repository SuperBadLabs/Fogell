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

# THE ALLOWLIST. Membership in the pinned corpus is not permission to execute:
# the corpus is untrusted, the Fogell fence does not contain a hostile file,
# and the operating contract executes only what has been allowlisted BY ITS
# EXECUTED SURFACE. differential/corpus-allowlist.tsv ties a file's sha256 and
# stem to the surface a person read; a file whose digest is not there is
# refused before the lease, before either fence, before anything runs
# (Codex on PR #392).
allowlist=differential/corpus-allowlist.tsv
[ -f "$allowlist" ] || die "$allowlist is missing — nothing is allowlisted"
for r in "${files[@]}"; do
  digest=$(sha256sum "$r" | cut -d' ' -f1); stem=$(basename "$r" .Jenkinsfile)
  awk -F'\t' -v d="$digest" -v s="$stem" '$1==d && $2==s {f=1} END {exit !f}' "$allowlist" \
    || die "$(basename "$r") (sha256 $digest) is not on the executed-surface allowlist — read it, record its surface in $allowlist, then run"
done
echo "corpus lane: every file's digest and stem are on the executed-surface allowlist"

# THE BYTES THAT WERE CHECKED ARE THE BYTES THAT RUN. The corpus lives on a
# shared mount; a refresh between the hash check and the CLI's own read would
# execute bytes nobody allowlisted (Codex on PR #394). Each approved file is
# copied into a private snapshot, the copy is re-hashed against the allowlist,
# and the CLI is given the copies — same basename, so the receipt stem and the
# sealed case-digest are unchanged.
snap_dir=$(mktemp -d); snaps=()
trap 'rm -rf "$snap_dir"' EXIT   # until the full cleanup replaces it
for r in "${files[@]}"; do
  b=$(basename "$r"); cp -- "$r" "$snap_dir/$b"; chmod 0400 "$snap_dir/$b"
  d=$(sha256sum "$snap_dir/$b" | cut -d' ' -f1)
  awk -F'\t' -v d="$d" -v s="$(basename "$r" .Jenkinsfile)" '$1==d && $2==s {f=1} END {exit !f}' "$allowlist" || { rm -rf "$snap_dir"; die "$b changed between the allowlist check and the snapshot (now sha256 $d) — refusing"; }
  snaps+=("$snap_dir/$b")
done

cli=tools/Fogell.Differential.Cli/bin/Release/net10.0/fogell-diff.dll
[ -f "$cli" ] || die "$cli is missing — build it first: dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release"

# shellcheck source=scripts/jenkins-workspace-v2.sh disable=SC1091
source scripts/jenkins-workspace-v2.sh || die "workspace collector could not be loaded"
fogell_configure_jenkins_workspace_v2 "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER" || die "workspace collector could not be configured"
container_q=$(fogell_quote_posix_shell_v2 "$FOGELL_JENKINS_CONTAINER")
FOGELL_JENKINS_ENV_CMD=$(fogell_jenkins_ssh_command_v2 "$FOGELL_JENKINS_HOST" "podman exec $container_q env")
FOGELL_JENKINS_GIT_VERSION_CMD=$(fogell_jenkins_ssh_command_v2 "$FOGELL_JENKINS_HOST" "podman exec $container_q git --version")
export FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD

# One lane per user on this host: a second lane's exit trap would remove this
# lane's Jenkins fence (measured, fixed). The lock lives in the user's runtime
# dir; the oracle's busy check below is the only cross-user, cross-host guard.
lock_dir=${XDG_RUNTIME_DIR:-/tmp}; [ -d "$lock_dir" ] && [ -w "$lock_dir" ] || lock_dir=/tmp
[ -d "$lock_dir" ] && [ -w "$lock_dir" ] || die "no writable directory for the lane lock ($lock_dir)"
exec 9>"$lock_dir/fogell-corpus-lane.lock" || die "could not open the lane lock in $lock_dir"
flock -n 9 || die "another corpus lane of this user holds $lock_dir/fogell-corpus-lane.lock"

if ! busy_json=$(curl -sS -m 10 "$FOGELL_JENKINS_URL/computer/api/json?tree=busyExecutors" 2>&1); then
  die "the oracle at $FOGELL_JENKINS_URL did not answer the busy check: ${busy_json:-no output}"
fi
busy=$(printf '%s' "$busy_json" | sed -n 's/.*"busyExecutors":\([0-9]*\).*/\1/p')
[ "${busy:-x}" = "0" ] || die "oracle reports busyExecutors=${busy:-unknown}; the lane is single-tenant"

# The Jenkins fence is ONE table in ONE namespace, shared by every corpus lane
# from every user and host. The busy check above reserves nothing, so two
# lanes could both pass it and the first to exit would unfence the second
# (Codex on PR #389). The lease is a `flock` held ON THE JENKINS HOST by a
# background ssh session for the life of this lane: atomic across users and
# hosts, and bound to THIS PROCESS's life: the remote holder is `cat` reading
# ssh's stdin, and that stdin is fed by `tail --pid=$$`, which exits when this
# lane's pid is gone however it went — normal exit, die, or SIGKILL — so the
# remote cat gets EOF and flock releases. (A first version held with
# `sleep infinity`, which ignores stdin and outlived the client: the verifier
# measured the lease stuck after the lane died.) The hand-written lane does
# not take it — it does not fence — and stays single-tenant by rule.
lease_dir=$(mktemp -d); lease_fifo="$lease_dir/lease"; mkfifo "$lease_fifo"
# `9>&-` on every background job: they must not inherit the local lock fd, or
# the lock outlives the lane by however long they do (measured: the next lane
# was refused by a dead lane's ssh child).
tail --pid=$$ -f /dev/null 9>&- | ssh -o BatchMode=yes "$FOGELL_JENKINS_HOST" \
  'flock -n "$HOME/.fogell-corpus-lane.lock" -c "echo leased; exec cat" || echo held' > "$lease_fifo" 2>/dev/null 9>&- &
lease_pid=$!; disown "$lease_pid"   # disowned: bash must not report these jobs when they are killed at teardown.
                                    # NEVER `wait` a disowned pid: it returns at once without waiting (measured).
if ! IFS= read -r -t 20 lease_word < "$lease_fifo" || [ "$lease_word" != leased ]; then
  kill "$lease_pid" 2>/dev/null; rm -rf "$lease_dir"
  die "could not take the corpus-lane lease on $FOGELL_JENKINS_HOST (${lease_word:-no answer: ssh exited or 20 s elapsed}) — another lane, user or host holds it, or the host is unreachable"
fi
rm -rf "$lease_dir"; lease_dir=""   # ssh holds its own fd; nothing else needs the path, and a SIGKILLed lane must not leak it
echo "corpus lane: holding the lane lease on $FOGELL_JENKINS_HOST (released when this pid $$ is gone)"
# The cleanup trap is installed BEFORE any watcher is spawned, so a refusal
# between here and the fence cannot leave a watcher alive to signal a reused
# pid later (Copilot on PR #398). Every variable the trap reads is
# initialised first.
fence_applied=; poller=; run_pid=; completed=; lease_watch=; run_receipts=
started=$(date -u +%FT%TZ)
# The trap goes in BEFORE apply so a partially applied fence is never left
# behind. Teardown policy: quiesce first; if the quiesce FAILS the fence is
# LEFT UP and reported — an open namespace with a survivor in it is the one
# state this lane exists to prevent, and the runbook says how to recover
# (Codex on PR #394). A pre-apply failure quiesces nothing and removes
# nothing: `apply` is one atomic nft load, and a fence that exists without
# this lane having applied it is somebody else's to recover.
# The run writes its receipts into a PRIVATE directory and they are PROMOTED
# into $FOGELL_RECEIPT_DIR only after the post-run check passes. A run that
# lost its fence or its lease simply discards that directory: nothing that
# was in the receipt directory before the run — committed, unstaged, or
# produced by another run — is ever touched (Codex on PR #397; a first
# version reverted the real directory with `git checkout`/`rm`).
cleanup() {
  # errexit is OFF and signals are ignored from here on: the teardown must
  # run to its end whatever fails inside it. Measured twice: a `kill` of a
  # watcher that had already exited aborted the trap at its first line under
  # errexit, and a second TERM re-entered `exit` — in both cases the revert,
  # the quiesce and the removal never ran.
  set +e
  trap '' TERM INT HUP
  [ -n "$poller" ] && kill -KILL "$poller" 2>/dev/null
  [ -n "$lease_watch" ] && kill -KILL "$lease_watch" 2>/dev/null
  # Stop the fenced run FIRST, and wait for its own teardown, so nothing of
  # ours is still executing when the namespace is quiesced and unfenced.
  if [ -n "$run_pid" ] && kill -0 "$run_pid" 2>/dev/null; then kill -TERM "$run_pid" 2>/dev/null; wait "$run_pid" 2>/dev/null || true; fi
  if [ -n "$fence_applied" ] && [ -z "$completed" ]; then
    echo "corpus lane: the run did not complete under a standing fence — its receipts are discarded, the receipt directory is untouched" >&2
  fi
  [ -n "$run_receipts" ] && rm -rf "$run_receipts"
  rm -f -- "$FOGELL_RECEIPT_DIR"/.*.tmp.$$ 2>/dev/null   # a promotion temp orphaned by a signal between cp and mv
  # Only a fence THIS lane applied is ever removed: a fence found already in
  # place belongs to a previous lane's recovery and stays (measured: a first
  # version removed the leftover it had just refused to replace).
  if [ -n "$fence_applied" ]; then
    if ./scripts/no-egress-fence.sh jenkins quiesce; then
      ./scripts/no-egress-fence.sh jenkins remove || true
    else
      echo "corpus lane: QUIESCE FAILED — the Jenkins fence is LEFT UP on purpose; recover per docs/runbooks/no-egress-fence.md" >&2
    fi
  fi
  kill "$lease_pid" 2>/dev/null || true
  [ -n "$lease_dir" ] && rm -rf "$lease_dir"
  [ -n "$snap_dir" ] && rm -rf "$snap_dir"
  return 0
}
trap cleanup EXIT
trap 'exit 143' TERM INT HUP
# The holder is WATCHED: if the ssh session or its remote cat exits during the
# run, the flock is free and another lane could unfence the shared namespace
# underneath this one, so the lane terminates and tears down (Codex on PR #394).
( tail --pid="$lease_pid" -f /dev/null; echo "corpus lane: LEASE LOST — the remote holder exited; aborting" >&2; kill -TERM $$ 2>/dev/null ) 9>&- &
lease_watch=$!; disown "$lease_watch"
started_at=$(./scripts/no-egress-fence.sh jenkins started-at) || die "could not read the container's start instant"
run_receipts=$(mktemp -d)
# QUIESCE BEFORE APPLY: a leftover of an earlier build holding a connection it
# opened before the fence would otherwise keep it (the rule now rejects the
# original direction of every flow, measured, but the process is killed too).
echo "corpus lane: quiescing the container, then applying and proving the Jenkins-side fence"
./scripts/no-egress-fence.sh jenkins quiesce
./scripts/no-egress-fence.sh jenkins apply
fence_applied=1
./scripts/no-egress-fence.sh jenkins verify

# The namespace fence evaporates if the container restarts; a run is proven
# only while it stands. A poller re-checks presence every few seconds and
# aborts the run if it is gone (Codex on PR #394); the post-run check below
# refuses the run's receipts if the container restarted at any point.
( while sleep 5; do ./scripts/no-egress-fence.sh jenkins present >/dev/null 2>&1 || { echo "corpus lane: FENCE LOST in the Jenkins namespace — aborting" >&2; kill -TERM $$ 2>/dev/null; break; }; done ) 9>&- &
poller=$!; disown "$poller"

echo "corpus lane: proving the Fogell-side fence, then running ${#files[@]} corpus file(s) from the snapshot"
# The fenced run is told whose lease it runs under: if this lane's pid
# vanishes (SIGKILL), the run tears itself down instead of outliving the
# lease as an orphan another lane could unfence (Codex on PR #392). It runs
# in the background under `wait` so a TERM from the watchers is not deferred
# behind it.
export FOGELL_FENCE_OWNER_PID=$$
./scripts/no-egress-fence.sh fogell run -- \
  dotnet "$cli" "$FOGELL_JENKINS_URL" "$FOGELL_JENKINS_CORE" "$run_receipts" "${snaps[@]}" 9>&- & run_pid=$!
wait "$run_pid" && rc=0 || rc=$?
kill -KILL "$poller" 2>/dev/null || true; poller=

# Post-run: the fence must still stand and the container must not have
# restarted; otherwise every receipt this run wrote is reverted, because a
# receipt from a run that lost its fence is not evidence under the contract.
ended_at=$(./scripts/no-egress-fence.sh jenkins started-at 2>/dev/null || echo unknown)
if [ "$ended_at" != "$started_at" ] || ! ./scripts/no-egress-fence.sh jenkins present >/dev/null 2>&1; then
  echo "corpus lane: the Jenkins fence did not stand for the whole run (container start $started_at -> $ended_at) — this run's receipts are discarded" >&2
  rc=2
elif [ "$rc" = 0 ]; then
  completed=1
  mkdir -p "$FOGELL_RECEIPT_DIR"
  # Only receipts for the stems this run was asked for are promoted, and a
  # copy failure is reported rather than aborting mid-promotion (verifier).
  for r in "${files[@]}"; do
    p="$run_receipts/$(basename "$r" .Jenkinsfile).receipt.txt"
    if [ -f "$p" ]; then
      # Atomic: copy beside the destination, then rename, so an interrupted or
      # failed copy never truncates an existing receipt (Codex on PR #398).
      tmp="$FOGELL_RECEIPT_DIR/.$(basename "$p").tmp.$$"
      if cp -- "$p" "$tmp" && mv -f -- "$tmp" "$FOGELL_RECEIPT_DIR/$(basename "$p")"; then echo "corpus lane: promoted $(basename "$p") into $FOGELL_RECEIPT_DIR"; else rm -f -- "$tmp"; echo "corpus lane: could NOT promote $(basename "$p")" >&2; rc=2; fi
    else
      echo "corpus lane: no receipt was produced for $(basename "$r")" >&2; rc=2
    fi
  done
else
  echo "corpus lane: the differential exited $rc — its receipts are not promoted" >&2
fi
echo "corpus lane: finished at $(date -u +%FT%TZ) (started $started), differential exit $rc"
exit "$rc"
