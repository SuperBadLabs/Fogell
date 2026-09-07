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
# does not verify; a non-HEAD or unbuildable differential CLI closure; a Jenkins-side fence that
# cannot be applied or proven; a Fogell-side fence that cannot be proven. The
# The disposable controller is destroyed and its access fences are removed on
# exit only after their absence checks succeed. A failed cleanup stays closed
# and the runbook says how to recover it.
#
# Environment (the same names run-differential.sh uses):
#   FOGELL_CORPUS            pinned corpus root   (default /sn8100/work/exchange/crucible-gate/corpus)
#   FOGELL_JENKINS_URL       oracle               (default http://luigi:18086)
#   FOGELL_JENKINS_CORE      pinned core          (default 2.568.1)
#   FOGELL_JENKINS_HOST      ssh host             (default luigi)
#   FOGELL_JENKINS_CONTAINER container            (set internally to the fresh full ID)
#   FOGELL_RECEIPT_DIR       where receipts land  (default differential/receipts)
set -Eeuo pipefail
cd "$(dirname "$0")/.."

: "${FOGELL_CORPUS:=/sn8100/work/exchange/crucible-gate/corpus}"
: "${FOGELL_JENKINS_URL:=http://luigi:18086}"
: "${FOGELL_JENKINS_CORE:=2.568.1}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=fogell-corpus-jenkins-unprovisioned}"
: "${FOGELL_RECEIPT_DIR:=differential/receipts}"
export FOGELL_CORPUS FOGELL_JENKINS_URL FOGELL_JENKINS_CORE FOGELL_JENKINS_HOST FOGELL_JENKINS_CONTAINER

die() { printf 'corpus lane: REFUSED: %s\n' "$*" >&2; exit 2; }
[ $# -gt 0 ] || die "name at least one corpus file"

corpus_dir=$(realpath -e "$FOGELL_CORPUS/jenkinsfiles" 2>/dev/null) || die "corpus not found at $FOGELL_CORPUS/jenkinsfiles"
files=(); file_digests=()
for f in "$@"; do
  r=$(realpath -e "$f" 2>/dev/null) || die "$f does not exist"
  case "$r" in "$corpus_dir"/*.Jenkinsfile) files+=("$r") ;; *) die "$f is not a pinned corpus file under $corpus_dir — this lane executes corpus files only" ;; esac
done

# Materialize every evidence-control byte from the signed commit before any
# repository helper or policy input is consumed. The currently executing
# runner must itself match that archive; every later helper, manifest,
# allowlist, runtime pin and compiled CLI is read only from the private HEAD
# tree, so untracked/ignored working-tree files cannot enter the receipt lane.
snap_dir=$(mktemp -d); cli_private=$(mktemp -d); cli_source="$cli_private/source"; cli_build="$cli_private/output"; snaps=()
mkdir -p "$cli_source" "$cli_build"
trap 'rm -rf "$snap_dir" "$cli_private"' EXIT   # until the full cleanup replaces it
source_head=$(git rev-parse --verify 'HEAD^{commit}') \
  || die "could not resolve the evidence-control commit"
[[ "$source_head" =~ ^[0-9a-f]{40,64}$ ]] \
  || die "git returned a malformed evidence-control commit identity"
git archive --format=tar "$source_head" | tar -xf - -C "$cli_source" \
  || die "could not materialize the exact HEAD evidence-control snapshot"
echo "corpus lane: evidence-control commit $source_head"
runner_path=$(realpath -e "$0") || die "could not resolve the executing corpus runner"
cmp -s -- "$runner_path" "$cli_source/scripts/run-corpus-differential.sh" \
  || die "the executing corpus runner differs from HEAD — commit it before collecting evidence"
fence_script="$cli_source/scripts/no-egress-fence.sh"
runtime_pin_checker="$cli_source/scripts/check-corpus-runtime-pins.sh"
workspace_helper="$cli_source/scripts/jenkins-workspace-v2.sh"
verify_corpus="$cli_source/scripts/verify-corpus.sh"
allowlist="$cli_source/differential/corpus-allowlist.tsv"
runtime_pins="$cli_source/differential/corpus-runtime-pins.tsv"

echo "corpus lane: verifying the pinned manifest"
"$verify_corpus" >/dev/null || die "corpus manifest did not verify — nothing executes against a drifted corpus"

# THE ALLOWLIST. Membership in the pinned corpus is not permission to execute:
# the corpus is untrusted, the Fogell fence does not contain a hostile file,
# and the operating contract executes only what has been allowlisted BY ITS
# EXECUTED SURFACE. differential/corpus-allowlist.tsv ties a file's sha256 and
# stem to the surface a person read; a file whose digest is not there is
# refused before the lease, before either fence, before anything runs
# (Codex on PR #392).
[ -f "$allowlist" ] || die "$allowlist is missing — nothing is allowlisted"
pin_ids=()
for r in "${files[@]}"; do
  digest=$(sha256sum "$r" | cut -d' ' -f1); stem=$(basename "$r" .Jenkinsfile)
  file_digests+=("$digest")
  if ! row=$(awk -F'\t' -v d="$digest" -v s="$stem" \
      '$1==d && $2==s {n++; row=$0} END {if(n==1) print row; else exit 1}' "$allowlist"); then
    die "$(basename "$r") (sha256 $digest) is not uniquely present on the executed-surface allowlist — read it, record its surface in $allowlist, then run"
  fi
  pin_id=$(printf '%s\n' "$row" | awk -F'\t' '{print $4}')
  if [ -n "$pin_id" ]; then
    case " ${pin_ids[*]} " in *" $pin_id "*) ;; *) pin_ids+=("$pin_id") ;; esac
  fi
done
echo "corpus lane: every file's digest and stem are on the executed-surface allowlist"

# Runtime-backed rows execute under the SAME fixed command-resolution PATH on
# Jenkins that ProcessGroup.fs gives Fogell. This is set here, not inherited:
# an ambient caller value cannot select a different build environment. Jenkins
# installs it as an explicit PATH build parameter on the disposable job, so a
# global/node PATH prefix cannot steer the real corpus `sh` to shadow bytes.
if [ "${#pin_ids[@]}" -gt 0 ]; then
  FOGELL_JENKINS_BUILD_PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
  export FOGELL_JENKINS_BUILD_PATH
else
  unset FOGELL_JENKINS_BUILD_PATH
fi
unset FOGELL_RUNTIME_GUARD_CASE_SHA FOGELL_RUNTIME_GUARD_NODE \
  FOGELL_RUNTIME_GUARD_COMMAND FOGELL_RUNTIME_GUARD_EXPECTATION \
  FOGELL_RUNTIME_GUARD_TOOL_PATH

# THE BYTES THAT WERE CHECKED ARE THE BYTES THAT RUN. The corpus lives on a
# shared mount; a refresh between the hash check and the CLI's own read would
# execute bytes nobody allowlisted (Codex on PR #394). Each approved file is
# copied into a private snapshot, the copy is re-hashed against the allowlist,
# and the CLI is given the copies — same basename, so the receipt stem and the
# sealed case-digest are unchanged.
for r in "${files[@]}"; do
  b=$(basename "$r"); cp -- "$r" "$snap_dir/$b"; chmod 0400 "$snap_dir/$b"
  d=$(sha256sum "$snap_dir/$b" | cut -d' ' -f1)
  awk -F'\t' -v d="$d" -v s="$(basename "$r" .Jenkinsfile)" '$1==d && $2==s {f=1} END {exit !f}' "$allowlist" || { rm -rf "$snap_dir"; die "$b changed between the allowlist check and the snapshot (now sha256 $d) — refusing"; }
  snaps+=("$snap_dir/$b")
done

# Compile the archived committed source closure into fresh private obj and
# output directories on every lane. A gitignored DLL or project.assets.json
# from the checkout can therefore never survive a revision/SDK switch into the
# evidence binary.
cli_project="$cli_source/tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj"
dotnet restore "$cli_project" --locked-mode --nologo >/dev/null \
  || die "the exact HEAD differential CLI source closure did not restore in its private workspace"
dotnet build "$cli_project" -c Release --no-restore --no-incremental --nologo -o "$cli_build" >/dev/null \
  || die "the exact HEAD differential CLI source closure did not build privately"
cli="$cli_build/fogell-diff.dll"
[ -f "$cli" ] || die "the fresh differential CLI build did not produce $cli"
cli_closure_sha=$(find "$cli_build" -type f -printf '%P\0' | sort -z | while IFS= read -r -d '' p; do sha256sum "$cli_build/$p" | sed "s#  $cli_build/#  #"; done | sha256sum | cut -d' ' -f1)
echo "corpus lane: fresh exact-HEAD differential CLI closure sha256 $cli_closure_sha"
capability=$(dotnet "$cli" --runtime-guard-capability 2>/dev/null) \
  || die "the fresh differential CLI did not answer the runtime-guard capability probe"
[ "$capability" = fogell-runtime-guard-v4 ] \
  || die "the fresh differential CLI reported an unexpected runtime-guard capability: ${capability:-no output}"

# One lane per user on this host: a second lane's exit trap would remove this
# lane's Jenkins fence (measured, fixed). The lock lives in the user's runtime
# dir; the oracle's busy check below is the only cross-user, cross-host guard.
# The lock lives in the user's runtime dir, or in a per-user 0700 directory
# under /tmp — never loose in world-writable /tmp (Copilot on PR #399).
# A caller-set runtime dir is held to the same test as the fallback: a
# directory, owned by this user, not a symlink, writable (Copilot on PR #401).
lock_dir=${XDG_RUNTIME_DIR:-}
if [ -z "$lock_dir" ] || [ ! -d "$lock_dir" ] || [ -L "$lock_dir" ] || [ ! -O "$lock_dir" ] || [ ! -w "$lock_dir" ]; then
  lock_dir="/tmp/fogell-corpus-lane-$(id -u)"
  [ -d "$lock_dir" ] || mkdir -m 0700 "$lock_dir" 2>/dev/null || true
  [ -d "$lock_dir" ] && [ -O "$lock_dir" ] && [ ! -L "$lock_dir" ] || die "lane lock directory $lock_dir is not a directory owned by this user"
  chmod 0700 "$lock_dir" 2>/dev/null || true
fi
[ -d "$lock_dir" ] && [ -w "$lock_dir" ] || die "no writable directory for the lane lock ($lock_dir)"
exec 9>"$lock_dir/fogell-corpus-lane.lock" || die "could not open the lane lock in $lock_dir"
flock -n 9 || die "another corpus lane of this user holds $lock_dir/fogell-corpus-lane.lock"

# The Jenkins fence is ONE table in ONE namespace, shared by every corpus lane
# from every user and host. A busy check by itself reserves nothing, so two
# lanes could otherwise both pass it and the first to exit would unfence the
# second (Codex on PR #389). The lease is a `flock` held ON THE JENKINS HOST by a
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
fence_applied=; access_applied=; local_access_applied=; poller=; run_pid=; completed=; lease_watch=; run_receipts=
tunnel_pid=; tunnel_watch=; tunnel_dir=
oracle_id=; oracle_name=; oracle_home_volume=; oracle_launch_attempted=; oracle_removed=; oracle_run_output=
started=$(date -u +%FT%TZ)
# The trap goes in BEFORE either access boundary or the controller. Teardown
# closes the tunnel, destroys the exact lane-labelled controller and its fresh
# anonymous home, proves both are absent, and only then reopens host access.
# A failed destruction leaves the host access fence up and reports recovery.
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
  [ -n "$tunnel_watch" ] && kill -KILL "$tunnel_watch" 2>/dev/null
  # Stop the fenced run FIRST, and wait for its own teardown, so nothing of
  # ours is still executing when the disposable namespace is destroyed.
  if [ -n "$run_pid" ] && kill -0 "$run_pid" 2>/dev/null; then kill -TERM "$run_pid" 2>/dev/null; wait "$run_pid" 2>/dev/null || true; fi
  if [ -n "$fence_applied" ] && [ -z "$completed" ]; then
    echo "corpus lane: the run did not complete under a standing fence — its receipts are discarded, the receipt directory is untouched" >&2
  fi
  [ -n "$run_receipts" ] && rm -rf "$run_receipts"
  rm -f -- "$FOGELL_RECEIPT_DIR"/.*.tmp.$$ 2>/dev/null   # a promotion temp orphaned by a signal between cp and mv
  # The SSH listener dies before either host access fence. A different local
  # principal can therefore never inherit a still-forwarding socket.
  tunnel_dead=1
  [ -n "$tunnel_pid" ] && kill "$tunnel_pid" 2>/dev/null
  for _ in 1 2 3 4 5; do
    if [ -z "$tunnel_pid" ] || ! kill -0 "$tunnel_pid" 2>/dev/null; then
      break
    fi
    sleep 1
  done
  [ -n "$tunnel_pid" ] && kill -KILL "$tunnel_pid" 2>/dev/null
  if [ -n "$tunnel_pid" ]; then
    sleep 1
    kill -0 "$tunnel_pid" 2>/dev/null && tunnel_dead=
  fi
  tunnel_pid=

  # The oracle has no inherited home: remove the exact returned container ID
  # and its exact anonymous JENKINS_HOME volume before reopening the port.
  # A label check prevents a stale/name-raced object from being destroyed.
  cleanup_oracle_id=$oracle_id
  if [ -n "$oracle_launch_attempted" ] && [ -z "$cleanup_oracle_id" ]; then
    cleanup_oracle_id=$(ssh -n "$FOGELL_JENKINS_HOST" \
      "podman inspect --format '{{.ID}}' $oracle_name" 2>/dev/null)
    if [ -z "$cleanup_oracle_id" ] && ssh -n "$FOGELL_JENKINS_HOST" \
      "podman container exists $oracle_name; rc=\$?; [ \$rc -eq 1 ]" 2>/dev/null; then
      oracle_removed=1
    fi
  fi
  if [ -n "$cleanup_oracle_id" ]; then
    if [[ ! "$cleanup_oracle_id" =~ ^[0-9a-f]{64}$ ]]; then
      echo "corpus lane: ORACLE ID RECOVERY FAILED — host access fence is LEFT UP; recover per docs/runbooks/no-egress-fence.md" >&2
      cleanup_oracle_id=
    elif [ -z "$oracle_home_volume" ]; then
      oracle_home_volume=$(ssh -n "$FOGELL_JENKINS_HOST" \
        "podman inspect $cleanup_oracle_id | jq -er '.[0] | (.Mounts | map(select(.Destination == \"/var/jenkins_home\"))) as \$h | if (\$h|length) == 1 and \$h[0].Type == \"volume\" and (\$h[0].Name|test(\"^[0-9a-f]{64}\$\")) then \$h[0].Name else error(\"JENKINS_HOME is not one fresh anonymous volume\") end'" 2>/dev/null)
      [ -n "$oracle_home_volume" ] || { echo "corpus lane: ORACLE HOME RECOVERY FAILED — host access fence is LEFT UP; recover per docs/runbooks/no-egress-fence.md" >&2; cleanup_oracle_id=; }
    fi
  fi
  if [ -n "$cleanup_oracle_id" ]; then
    owned=$(ssh -n "$FOGELL_JENKINS_HOST" \
      "podman inspect --format '{{index .Config.Labels \"fogell.lane-token\"}}' $cleanup_oracle_id" 2>/dev/null)
    if [ "$owned" = "${FOGELL_JENKINS_ACCESS_TOKEN:-}" ] \
       && ssh -n "$FOGELL_JENKINS_HOST" "podman rm -fv $cleanup_oracle_id" >/dev/null 2>&1 \
       && ssh -n "$FOGELL_JENKINS_HOST" "podman container exists $cleanup_oracle_id; rc=\$?; [ \$rc -eq 1 ]" \
       && { [ -z "$oracle_home_volume" ] || ssh -n "$FOGELL_JENKINS_HOST" "podman volume exists $oracle_home_volume; rc=\$?; [ \$rc -eq 1 ]"; }; then
      oracle_removed=1
      echo "corpus lane: disposable Jenkins controller and fresh home removed"
    else
      echo "corpus lane: ORACLE REMOVE FAILED — host access fence is LEFT UP; recover per docs/runbooks/no-egress-fence.md" >&2
    fi
  elif [ -z "$oracle_launch_attempted" ]; then
    oracle_removed=1
  fi

  if [ -n "$access_applied" ]; then
    if [ -n "$oracle_launch_attempted" ] && [ -z "$oracle_removed" ]; then
      echo "corpus lane: ACCESS FENCE LEFT UP because the disposable oracle may remain" >&2
    else
      "$fence_script" jenkins access-remove || \
        echo "corpus lane: ACCESS FENCE REMOVE FAILED — recover per docs/runbooks/no-egress-fence.md" >&2
    fi
  fi
  if [ -n "$local_access_applied" ]; then
    if [ -z "$tunnel_dead" ]; then
      echo "corpus lane: LOCAL ACCESS FENCE LEFT UP because the SSH tunnel may remain" >&2
    else
      "$fence_script" local access-remove || \
        echo "corpus lane: LOCAL ACCESS FENCE REMOVE FAILED — recover per docs/runbooks/no-egress-fence.md" >&2
    fi
  fi
  [ -n "$tunnel_dir" ] && rm -rf "$tunnel_dir"
  kill "$lease_pid" 2>/dev/null || true
  [ -n "$lease_dir" ] && rm -rf "$lease_dir"
  [ -n "$snap_dir" ] && rm -rf "$snap_dir"
  [ -n "$cli_private" ] && rm -rf "$cli_private"
  return 0
}
trap cleanup EXIT
trap 'exit 143' TERM INT HUP
# The holder is WATCHED: if the ssh session or its remote cat exits during the
# run, the flock is free and another lane could unfence the shared namespace
# underneath this one, so the lane terminates and tears down (Codex on PR #394).
( tail --pid="$lease_pid" -f /dev/null; echo "corpus lane: LEASE LOST — the remote holder exited; aborting" >&2; kill -TERM $$ 2>/dev/null ) 9>&- &
lease_watch=$!; disown "$lease_watch"
verify_runtime_pins() {
  [ "${#pin_ids[@]}" -eq 0 ] && return 0
  "$runtime_pin_checker" "$runtime_pins" "${pin_ids[@]}"
}

# A guarded runtime claim is deliberately one case / one pin per lane. The
# case digest binds the reviewed absence of a Pipeline PATH overlay; Jenkins.fs
# brackets that exact case with real `sh` builds on a dedicated guard job and
# requires both guards plus the history-pristine corpus job to report the node.
if [ "${#pin_ids[@]}" -gt 0 ]; then
  [ "${#files[@]}" -eq 1 ] && [ "${#pin_ids[@]}" -eq 1 ] \
    || die "runtime-pinned execution requires exactly one corpus file and one runtime pin"
  guard_row=$(awk -F'\t' -v p="${pin_ids[0]}" '$1==p {n++; row=$0} END {if(n==1) print row; else exit 1}' \
    "$runtime_pins") || die "could not recover the verified runtime pin row"
  IFS=$'\t' read -r guard_pin guard_command guard_expectation _ guard_tool_path _ guard_image guard_digest guard_container_port guard_host_binding guard_node guard_plugin_count guard_plugin_sha guard_extra <<< "$guard_row"
  [ -z "${guard_extra:-}" ] || die "verified runtime pin row changed shape"
  [ "$guard_pin" = "${pin_ids[0]}" ] || die "verified runtime pin identity changed"
  FOGELL_RUNTIME_GUARD_CASE_SHA=${file_digests[0]}
  FOGELL_RUNTIME_GUARD_NODE=$guard_node
  FOGELL_RUNTIME_GUARD_COMMAND=$guard_command
  FOGELL_RUNTIME_GUARD_EXPECTATION=$guard_expectation
  FOGELL_RUNTIME_GUARD_TOOL_PATH=$guard_tool_path
  export FOGELL_RUNTIME_GUARD_CASE_SHA FOGELL_RUNTIME_GUARD_NODE \
    FOGELL_RUNTIME_GUARD_COMMAND FOGELL_RUNTIME_GUARD_EXPECTATION \
    FOGELL_RUNTIME_GUARD_TOOL_PATH
fi

# A receipt never shares the long-lived lab JVM or its mutable home. The lane
# provisions a new controller from the exact pinned image only after both host
# access boundaries stand, and removes its anonymous JENKINS_HOME volume before
# either boundary comes down. The image seeds the same 154-plugin closure into
# that empty home; its canonical active inventory is checked below.
oracle_image=ddc4e247ca53c13baab8df6d13ab6646a4663ce2e843187661c74b8860a163f3
oracle_image_digest=sha256:dfdd9ae5effae9bc2484e25944437a23cf06949074de7c56cd5cd42981843990
oracle_container_port=8080
oracle_host_port=18086
oracle_plugin_count=154
oracle_plugin_sha=3b31a5bf08550cfa0155f99dd22dd61a17934ba49e474f11252ec40dd854783b
[ "$FOGELL_JENKINS_URL" = "http://$FOGELL_JENKINS_HOST:$oracle_host_port" ] \
  || die "disposable oracle URL must be exactly http://$FOGELL_JENKINS_HOST:$oracle_host_port"
if [ "${#pin_ids[@]}" -gt 0 ]; then
  [ "$guard_image" = "$oracle_image" ] && [ "$guard_digest" = "$oracle_image_digest" ] \
    && [ "$guard_container_port" = "$oracle_container_port/tcp" ] \
    && [ "$guard_host_binding" = "0.0.0.0:$oracle_host_port" ] \
    && [ "$guard_plugin_count" = "$oracle_plugin_count" ] \
    && [ "$guard_plugin_sha" = "$oracle_plugin_sha" ] \
    || die "runtime pin does not name the disposable oracle's exact image/digest/ports"
fi

# REST AUTHENTICATION. The configured HTTP endpoint above is still checked
# against the inspected host/container port mapping, but HTTP across the LAN
# has no server authentication. Every Jenkins REST byte used for this receipt
# therefore travels through an SSH local forward to loopback on THAT SAME
# inspected host. The forward is life-bound to this lane exactly like the
# remote lease: when this pid dies, tail closes ssh's stdin, the remote `cat`
# exits, and the listener disappears. ExitOnForwardFailure makes an occupied
# local port a refusal rather than an accidental connection to another service.
remote_port=${FOGELL_JENKINS_URL##*:}
tunnel_url=http://127.0.0.1:18084
tunnel_forward=127.0.0.1:18084:127.0.0.1:$remote_port
FOGELL_JENKINS_TUNNEL_PORT=18084
FOGELL_JENKINS_ACCESS_TOKEN=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
FOGELL_JENKINS_ACCESS_UID=$(ssh -n -o BatchMode=yes "$FOGELL_JENKINS_HOST" 'id -u') \
  || die "could not bind the Jenkins access fence to the authenticated remote uid"
export FOGELL_JENKINS_EVIDENCE_URL="$tunnel_url"
export FOGELL_JENKINS_TUNNEL_PORT FOGELL_JENKINS_ACCESS_TOKEN FOGELL_JENKINS_ACCESS_UID

# The TCP listener must never exist before its owner-uid rule: otherwise a
# different HeMan principal could pre-open a connection and make ssh relay its
# anonymous Jenkins calls as the trusted Luigi uid.
"$fence_script" local access-apply
local_access_applied=1

# Fence Luigi before the controller port exists. This closes both the startup
# window and the long-lived-controller problem: no pre-lane JVM, listener, job,
# timer or workspace is reused.
"$fence_script" jenkins access-apply
access_applied=1
oracle_name="fogell-corpus-jenkins-$FOGELL_JENKINS_ACCESS_TOKEN"
oracle_launch_attempted=1
oracle_run_output=$(ssh -n -o BatchMode=yes "$FOGELL_JENKINS_HOST" \
  "podman run --pull=never -d --name $oracle_name --label fogell.lane-token=$FOGELL_JENKINS_ACCESS_TOKEN -p $oracle_host_port:$oracle_container_port $oracle_image") \
  || die "could not start the fresh disposable Jenkins controller"
[[ "$oracle_run_output" =~ ^[0-9a-f]{64}$ ]] \
  || die "podman did not return one full container identity for the disposable oracle"
oracle_id=$oracle_run_output
FOGELL_JENKINS_CONTAINER=$oracle_id
export FOGELL_JENKINS_CONTAINER

oracle_home_volume=$(ssh -n -o BatchMode=yes "$FOGELL_JENKINS_HOST" \
  "podman inspect $oracle_id | jq -er '.[0] | (.Mounts | map(select(.Destination == \"/var/jenkins_home\"))) as \$h | if (\$h|length) == 1 and \$h[0].Type == \"volume\" and (\$h[0].Name|test(\"^[0-9a-f]{64}\$\")) then \$h[0].Name else error(\"JENKINS_HOME is not one fresh anonymous volume\") end'") \
  || die "disposable oracle did not receive exactly one fresh anonymous JENKINS_HOME volume"

# The namespace egress fence is loaded as soon as the fresh container has a
# PID, before readiness, plugins, tools or any corpus endpoint is queried.
"$fence_script" jenkins apply
fence_applied=1
started_at=$("$fence_script" jenkins started-at) \
  || die "could not read the disposable controller's start instant"

ready=
for _ in $(seq 1 120); do
  code=$(ssh -n -o BatchMode=yes "$FOGELL_JENKINS_HOST" \
    "curl -sS -m 2 -o /dev/null -w '%{http_code}' http://127.0.0.1:$oracle_host_port/api/json" 2>/dev/null || true)
  if [ "$code" = 200 ]; then ready=1; break; fi
  sleep 1
done
[ -n "$ready" ] || die "fresh disposable Jenkins did not become ready in 120 seconds"

observed_core=$(ssh -n -o BatchMode=yes "$FOGELL_JENKINS_HOST" \
  "curl -sS -D - -o /dev/null http://127.0.0.1:$oracle_host_port/api/json" \
  | sed -n 's/^[Xx]-Jenkins: *\([^[:space:]\r]*\).*/\1/p' | tr -d '\r')
[ "$observed_core" = "$FOGELL_JENKINS_CORE" ] \
  || die "fresh disposable Jenkins core is ${observed_core:-unknown}, expected $FOGELL_JENKINS_CORE"
plugin_json=$(ssh -n -o BatchMode=yes "$FOGELL_JENKINS_HOST" \
  "curl --globoff -sS 'http://127.0.0.1:$oracle_host_port/pluginManager/api/json?tree=plugins[shortName,version,active,enabled]'") \
  || die "could not read the fresh controller plugin inventory"
plugin_count=$(printf '%s' "$plugin_json" | jq -r '.plugins | length')
plugin_sha=$(printf '%s' "$plugin_json" | jq -cS '.plugins | sort_by(.shortName)' | sha256sum | cut -d' ' -f1)
[ "$plugin_count" = "$oracle_plugin_count" ] && [ "$plugin_sha" = "$oracle_plugin_sha" ] \
  || die "fresh controller plugin closure changed (count $plugin_count/$oracle_plugin_count, sha $plugin_sha/$oracle_plugin_sha)"
initial_json=$(ssh -n -o BatchMode=yes "$FOGELL_JENKINS_HOST" \
  "curl --globoff -sS 'http://127.0.0.1:$oracle_host_port/api/json?tree=jobs[name],quietingDown'; printf '\\n'; curl --globoff -sS 'http://127.0.0.1:$oracle_host_port/queue/api/json?tree=items[id]'; printf '\\n'; curl --globoff -sS 'http://127.0.0.1:$oracle_host_port/computer/api/json?tree=busyExecutors,computer[displayName,offline,numExecutors]'" ) \
  || die "could not attest the fresh controller's empty state"
printf '%s' "$initial_json" | jq -se \
  'length == 3 and (.[0].jobs|length)==0 and .[0].quietingDown==false and (.[1].items|length)==0 and .[2].busyExecutors==0 and (.[2].computer|length)==1 and .[2].computer[0].offline==false' >/dev/null \
  || die "fresh controller was not empty, idle, unqueued and one-node online"

# Configure every collector only after the random full container identity is
# known; no command may fall back to the persistent lab controller by name.
# shellcheck source=scripts/jenkins-workspace-v2.sh disable=SC1091
source "$workspace_helper" || die "workspace collector could not be loaded"
fogell_configure_jenkins_workspace_v2 "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER" \
  || die "workspace collector could not be configured"
container_q=$(fogell_quote_posix_shell_v2 "$FOGELL_JENKINS_CONTAINER")
FOGELL_JENKINS_ENV_CMD=$(fogell_jenkins_ssh_command_v2 "$FOGELL_JENKINS_HOST" "podman exec $container_q env")
FOGELL_JENKINS_GIT_VERSION_CMD=$(fogell_jenkins_ssh_command_v2 "$FOGELL_JENKINS_HOST" "podman exec $container_q git --version")
export FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD
verify_runtime_pins || die "a selected corpus runtime pin did not match the disposable oracle"
pinned_at=$("$fence_script" jenkins started-at) \
  || die "could not re-read the disposable controller's start instant"
[ "$pinned_at" = "$started_at" ] \
  || die "the disposable Jenkins controller restarted during attestation ($started_at -> $pinned_at)"

tunnel_dir=$(mktemp -d); tunnel_fifo="$tunnel_dir/ready"; mkfifo "$tunnel_fifo"
tail --pid=$$ -f /dev/null 9>&- | ssh -o BatchMode=yes -o ExitOnForwardFailure=yes \
  -o ServerAliveInterval=5 -o ServerAliveCountMax=1 -T -L "$tunnel_forward" \
  "$FOGELL_JENKINS_HOST" 'echo tunneled; exec cat >/dev/null' > "$tunnel_fifo" 2>/dev/null 9>&- &
tunnel_pid=$!; disown "$tunnel_pid"
if ! IFS= read -r -t 20 tunnel_word < "$tunnel_fifo" || [ "$tunnel_word" != tunneled ]; then
  kill "$tunnel_pid" 2>/dev/null; rm -rf "$tunnel_dir"; tunnel_dir=
  die "could not establish the authenticated Jenkins REST tunnel on $tunnel_url (${tunnel_word:-no answer: ssh exited or 20 s elapsed})"
fi
rm -rf "$tunnel_dir"; tunnel_dir=
echo "corpus lane: Jenkins REST is tunneled over SSH to $FOGELL_JENKINS_HOST loopback port $remote_port"
( tail --pid="$tunnel_pid" -f /dev/null; echo "corpus lane: REST TUNNEL LOST — aborting" >&2; kill -TERM $$ 2>/dev/null ) 9>&- &
tunnel_watch=$!; disown "$tunnel_watch"
"$fence_script" local access-verify

# The remote fence was present before this fresh listener existed; now prove
# the authenticated uid path, other-uid refusal, LAN refusal and exact rules.
"$fence_script" jenkins access-verify

if ! busy_json=$(curl -sS -m 10 "$tunnel_url/computer/api/json?tree=busyExecutors" 2>&1); then
  die "the authenticated oracle tunnel at $tunnel_url did not answer the busy check: ${busy_json:-no output}"
fi
busy=$(printf '%s' "$busy_json" | sed -n 's/.*"busyExecutors":\([0-9]*\).*/\1/p')
[ "${busy:-x}" = "0" ] || die "oracle reports busyExecutors=${busy:-unknown}; the lane is single-tenant"
if ! queue_json=$(curl --globoff -sS -m 10 "$tunnel_url/queue/api/json?tree=items[id]" 2>&1); then
  die "the authenticated oracle tunnel did not answer the queue check: ${queue_json:-no output}"
fi
printf '%s' "$queue_json" | jq -e '.items | type == "array" and length == 0' >/dev/null \
  || die "oracle queue was not empty after access isolation; the lane will not inherit pre-isolation work"
run_receipts=$(mktemp -d)
echo "corpus lane: proving the fresh controller's pre-applied Jenkins-side egress fence"
"$fence_script" jenkins verify

# The namespace fence evaporates if the container restarts; a run is proven
# only while it stands. A poller re-checks presence every few seconds and
# aborts the run if it is gone (Codex on PR #394); the post-run check below
# refuses the run's receipts if the container restarted at any point.
( while sleep 5; do
    "$fence_script" jenkins present >/dev/null 2>&1 \
      && "$fence_script" jenkins access-present >/dev/null 2>&1 \
      && "$fence_script" local access-present >/dev/null 2>&1 \
      && [ "$("$fence_script" jenkins started-at 2>/dev/null)" = "$started_at" ] \
      || { echo "corpus lane: NETWORK OR ACCESS FENCE LOST — aborting" >&2; kill -TERM $$ 2>/dev/null; break; }
  done ) 9>&- &
poller=$!; disown "$poller"

echo "corpus lane: proving the Fogell-side fence, then running ${#files[@]} corpus file(s) from the snapshot"
# The fenced run is told whose lease it runs under: if this lane's pid
# vanishes (SIGKILL), the run tears itself down instead of outliving the
# lease as an orphan another lane could unfence (Codex on PR #392). It runs
# in the background under `wait` so a TERM from the watchers is not deferred
# behind it.
export FOGELL_FENCE_OWNER_PID=$$
FOGELL_JENKINS_URL="$tunnel_url" "$fence_script" fogell run -- \
  dotnet "$cli" "$tunnel_url" "$FOGELL_JENKINS_CORE" "$run_receipts" "${snaps[@]}" 9>&- & run_pid=$!
wait "$run_pid" && rc=0 || rc=$?
kill -KILL "$poller" 2>/dev/null || true; poller=

# Post-run: the fence must still stand and the container must not have
# restarted; otherwise every receipt this run wrote is reverted, because a
# receipt from a run that lost its fence is not evidence under the contract.
ended_at=$("$fence_script" jenkins started-at 2>/dev/null || echo unknown)
if [ "$ended_at" != "$started_at" ] \
   || ! "$fence_script" jenkins present >/dev/null 2>&1 \
   || ! "$fence_script" jenkins access-present >/dev/null 2>&1 \
   || ! "$fence_script" local access-present >/dev/null 2>&1; then
  echo "corpus lane: the Jenkins network/access fences did not stand for the whole run (container start $started_at -> $ended_at) — this run's receipts are discarded" >&2
  rc=2
elif ! verify_runtime_pins; then
  echo "corpus lane: a runtime pin changed before promotion — this run's receipts are discarded" >&2
  rc=2
elif [ "$rc" = 0 ]; then
  completed=1
  mkdir -p "$FOGELL_RECEIPT_DIR"
  # A BATCH is promoted whole or not at all (Codex on PR #399): first every
  # requested receipt must exist, then every copy is staged beside its
  # destination, and only then is each renamed into place — so a missing
  # receipt or a failed copy leaves the receipt directory exactly as it was.
  staged=(); dests=(); batch_ok=1
  for r in "${files[@]}"; do
    p="$run_receipts/$(basename "$r" .Jenkinsfile).receipt.txt"
    [ -f "$p" ] || { echo "corpus lane: no receipt was produced for $(basename "$r") — nothing from this batch is promoted" >&2; batch_ok=; }
  done
  if [ -n "$batch_ok" ]; then
    for r in "${files[@]}"; do
      name="$(basename "$r" .Jenkinsfile).receipt.txt"; p="$run_receipts/$name"; tmp="$FOGELL_RECEIPT_DIR/.$name.tmp.$$"
      if cp -- "$p" "$tmp"; then staged+=("$tmp"); dests+=("$FOGELL_RECEIPT_DIR/$name"); else echo "corpus lane: could NOT stage $name — nothing from this batch is promoted" >&2; batch_ok=; break; fi
    done
  fi
  if [ -n "$batch_ok" ]; then
    # A rename that fails is reported and fails the lane; it cannot be silent
    # (Codex and Copilot on PR #401). A same-directory rename after a
    # successful copy is the one step here that has no realistic failure.
    for i in "${!staged[@]}"; do
      if mv -f -- "${staged[$i]}" "${dests[$i]}"; then
        echo "corpus lane: promoted $(basename "${dests[$i]}") into $FOGELL_RECEIPT_DIR"
      else
        echo "corpus lane: could NOT rename $(basename "${dests[$i]}") into place — receipts before it in this batch are promoted, this one and later ones are not" >&2
        batch_ok=; rc=2; break
      fi
    done
    [ -n "$batch_ok" ] || for tmp in "${staged[@]}"; do rm -f -- "$tmp"; done
  else
    for tmp in "${staged[@]}"; do rm -f -- "$tmp"; done
    rc=2
  fi
else
  echo "corpus lane: the differential exited $rc — its receipts are not promoted" >&2
fi
echo "corpus lane: finished at $(date -u +%FT%TZ) (started $started), differential exit $rc"
exit "$rc"
