#!/usr/bin/env bash
# The no-egress fence the operating contract names for executing corpus files.
# FG-200 probed on 2026-08-17 that it did not exist: `jenkins-lab` resolved DNS
# and fetched an external HTTPS URL, and the Fogell side ran on a host with full
# egress. This script builds it on BOTH sides, per run, and PROVES it before any
# corpus file executes — a fence that is merely configured is not evidence.
#
#   scripts/no-egress-fence.sh jenkins apply|verify|quiesce|remove|status|present|started-at
#   scripts/no-egress-fence.sh fogell  run -- <command...>
#   scripts/no-egress-fence.sh fogell  status
#
# JENKINS SIDE. `jenkins-lab` is a rootless podman container (slirp4netns) on
# $FOGELL_JENKINS_HOST. Its network namespace is owned by our user namespace, so
# `nsenter -U -n` reaches it WITHOUT root and WITHOUT recreating the oracle, and
# an nftables ruleset loaded there filters exactly that container: loopback and
# already-established flows pass (Jenkins keeps answering HeMan on its published
# port), every new outbound TCP SYN, every UDP datagram (DNS) and everything else
# is REJECTED — refused in under a millisecond rather than dropped, so a step
# that tries the network fails fast and identically on every run. The ruleset
# lives in the namespace: it evaporates when the container restarts, which is
# why `verify` runs before every corpus execution and `status` never assumes.
#
# FOGELL SIDE. HeMan ignores user-level IPAddressDeny and forbids unprivileged
# user namespaces (both measured 2026-09-04), so the fence is a root nftables
# rule keyed to the cgroup of a transient systemd user scope that `run` places
# the command in. nft binds the rule to the cgroup's identity AT LOAD TIME, so
# the rule is loaded from INSIDE the live scope, proven there, and deleted on
# exit; a rule left behind for a dead scope (its cgroup directory gone) is
# swept on the next run, and a live one is never touched. Before the rule is
# deleted every process still in the scope is KILLED, so nothing a step
# backgrounded outlives the fence. Inside the scope the reachable set is the
# Jenkins host on the oracle port and ssh (the workspace collector), plus
# loopback minus systemd-resolved's stub listeners (127.0.0.53 and .54), so
# names do not resolve over DNS inside the scope; the oracle host resolves
# from /etc/hosts.
#
# WHAT THE FOGELL SIDE IS AND IS NOT. It is a network fence for the executed
# surface — the differential CLI, Fogell in-process, and every /bin/sh a step
# starts — that turns an accidental or ordinary network call into an
# immediate refusal. It is NOT a boundary against a hostile file: the fenced
# processes run as the operator's own UID on HeMan, and that UID can hop off
# the fence by the collector's ssh key (`ssh <host> curl …`), by asking the
# systemd user manager to spawn outside the cgroup (`systemd-run --user`), by
# passwordless sudo, or by any listener on loopback. Those are properties of
# running Fogell as the operator (FG-073: no hostile-workload containment),
# not of the fence, and the allowlist-by-reading rule is the control that
# addresses them. The JENKINS side is the network boundary: a namespace, with
# no same-UID hatch measured (see FG-244).
set -Eeuo pipefail

: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"
: "${FOGELL_JENKINS_URL:=http://luigi:18083}"
: "${FOGELL_FENCE_PROBE_HOST:=example.com}"
: "${FOGELL_FENCE_PROBE_IP:=1.1.1.1}"
: "${FOGELL_FENCE_PROBE_LAN_IP:=}"

log() { printf 'fence: %s\n' "$*"; }
die() { printf 'fence: REFUSED: %s\n' "$*" >&2; exit 2; }

# --- Jenkins side ------------------------------------------------------------

jenkins_ns() {
  # Prints the nsenter prefix for the container's user+net namespace, on the host.
  printf 'pid=$(podman inspect --format "{{.State.Pid}}" %q) && [ -n "$pid" ] && nsenter -t "$pid" -U --preserve-credentials -n --' "$FOGELL_JENKINS_CONTAINER"
}

jenkins_apply() {
  local ns; ns=$(jenkins_ns)
  ssh "$FOGELL_JENKINS_HOST" "set -e; $ns nft list table inet fogell_fence >/dev/null 2>&1 && $ns nft delete table inet fogell_fence; $ns nft -f - <<'NFT'
table inet fogell_fence {
  chain output {
    type filter hook output priority 0; policy drop;
    oif \"lo\" accept
    tcp flags ack accept
    counter reject
  }
}
NFT" || die "jenkins fence could not be applied on $FOGELL_JENKINS_HOST"
  log "jenkins fence applied in the $FOGELL_JENKINS_CONTAINER network namespace"
}

jenkins_remove() {
  # Absence is fine; a FAILED deletion is not, and is reported as such.
  local ns; ns=$(jenkins_ns)
  # A failed namespace lookup is NOT absence: list the tables (must succeed),
  # then delete if present, then list again (Codex on PR #392).
  ssh "$FOGELL_JENKINS_HOST" "set -e; t=\$($ns nft list tables) || { echo LOOKUP-FAILED; exit 3; }; if printf '%s\n' \"\$t\" | grep -q 'inet fogell_fence\$'; then $ns nft delete table inet fogell_fence; fi; t=\$($ns nft list tables) || { echo LOOKUP-FAILED; exit 3; }; if printf '%s\n' \"\$t\" | grep -q 'inet fogell_fence\$'; then echo STILL-PRESENT; exit 1; fi" \
    || die "jenkins fence could NOT be confirmed removed on $FOGELL_JENKINS_HOST (lookup failed or table still present) — remove it by hand before running the other lane"
  log "jenkins fence removed"
}

jenkins_quiesce() {
  # Kill every process a build left behind (anything that is not init, not the
  # Jenkins JVM, and not this exec itself), BEFORE the fence comes down, so a
  # step that backgrounded a process does not get egress after the run. The
  # script travels on stdin so no quoting layer can mangle it.
  local out c; c=$(printf '%q' "$FOGELL_JENKINS_CONTAINER")
  out=$(ssh "$FOGELL_JENKINS_HOST" "podman exec -i $c sh" <<'QUIESCE'
me=$$; killed=0
# The Jenkins JVM is identified by WHAT IT RUNS (jenkins.war) and by WHEN IT
# STARTED: of the processes whose cmdline names jenkins.war, the one with the
# EARLIEST start time is the oracle — it started with the container, before
# any build could spawn an impostor named `java` or carrying `#jenkins.war`
# in its arguments (Codex on PR #389, then the verifier's lexicographic-pid
# finding). /proc/<pid>/stat is parsed AFTER the closing parenthesis of the
# comm field, because comm may contain spaces and shift every whitespace-
# split field: the verifier planted `/tmp/a b` and beat a naive `$22`, and
# the unpatched test of that finding killed the oracle once. After the
# paren, ppid is field 2 and start time field 20. A non-numeric start is not
# a candidate; no match refuses.
after_paren() { sed 's/^.*) //' "$1" 2>/dev/null; }
jvm=""; jvm_start=""; matches=0
for d in /proc/[0-9]*; do
  { tr '\0' ' ' < "$d/cmdline"; } 2>/dev/null | grep -q 'jenkins\.war' || continue
  start=$(after_paren "$d/stat" | awk '{print $20}')
  case "$start" in ''|*[!0-9]*) continue ;; esac
  matches=$((matches+1))
  if [ -z "$jvm_start" ] || [ "$start" -lt "$jvm_start" ]; then jvm=${d#/proc/}; jvm_start=$start; fi
done
[ -n "$jvm" ] || { echo "killed=refused: no process runs jenkins.war"; exit 1; }
[ "$matches" = 1 ] || echo "note=$matches processes name jenkins.war; keeping the earliest-started, pid $jvm" >&2
for d in /proc/[0-9]*; do
  p=${d#/proc/}
  [ "$p" = 1 ] && continue
  [ "$p" = "$jvm" ] && continue
  [ "$p" = "$me" ] && continue
  ppid=$(after_paren "$d/stat" | awk '{print $2}')
  [ "$ppid" = "$me" ] && continue
  kill -KILL "$p" 2>/dev/null && killed=$((killed+1))
done
echo "killed=$killed"
QUIESCE
  ) || die "jenkins quiesce could not run or found no Jenkins JVM (${out:-no output})"
  log "jenkins quiesced: ${out#killed=} leftover process(es) killed before the fence comes down"
}

jenkins_present() {
  # 0 = table present, 1 = absent, 3 = the namespace could not be inspected.
  local ns t; ns=$(jenkins_ns)
  t=$(ssh -n -o ConnectTimeout=10 "$FOGELL_JENKINS_HOST" "$ns nft list tables 2>/dev/null") || return 3
  printf '%s\n' "$t" | grep -q 'inet fogell_fence$'
}

jenkins_started_at() {
  # The container's start instant: a change during a run means the namespace
  # (and the fence in it) was recreated underneath the run.
  ssh -n -o ConnectTimeout=10 "$FOGELL_JENKINS_HOST" "podman inspect --format '{{.State.StartedAt}}' $(printf '%q' "$FOGELL_JENKINS_CONTAINER")"
}

jenkins_status() {
  local ns c; ns=$(jenkins_ns); c=$(printf '%q' "$FOGELL_JENKINS_CONTAINER")
  if ssh "$FOGELL_JENKINS_HOST" "$ns nft list table inet fogell_fence 2>/dev/null"; then
    log "jenkins fence PRESENT"
  else
    log "jenkins fence ABSENT"
  fi
  ssh "$FOGELL_JENKINS_HOST" "podman exec $c sh -c 'curl -sS -m 8 -o /dev/null -w \"egress https://$FOGELL_FENCE_PROBE_HOST -> HTTP %{http_code}\n\" https://$FOGELL_FENCE_PROBE_HOST 2>&1 || echo \"egress https://$FOGELL_FENCE_PROBE_HOST -> refused (curl exit \$?)\"'"
}

jenkins_verify() {
  # Negative probes run INSIDE the container; the positive probe is the path the
  # differential lane uses. Every line is measured; any surprise refuses.
  local out failed=0 c; c=$(printf '%q' "$FOGELL_JENKINS_CONTAINER")
  out=$(ssh "$FOGELL_JENKINS_HOST" "podman exec $c sh -c '
    getent hosts $FOGELL_FENCE_PROBE_HOST >/dev/null 2>&1; echo dns=\$?
    curl -sS -m 8 -o /dev/null https://$FOGELL_FENCE_PROBE_HOST >/dev/null 2>&1; echo https=\$?
    s=\$(date +%s%N); curl -sS -m 8 -o /dev/null http://$FOGELL_FENCE_PROBE_IP/ >/dev/null 2>&1; r=\$?; e=\$(date +%s%N); echo ip=\$r; echo ipms=\$(( (e - s) / 1000000 ))
    ${FOGELL_FENCE_PROBE_LAN_IP:+curl -s -m 8 -o /dev/null http://$FOGELL_FENCE_PROBE_LAN_IP/ >/dev/null 2>/dev/null; echo lan=\$?}
    curl -sS -m 8 -o /dev/null -w loop=%{http_code} http://127.0.0.1:8080/api/json 2>/dev/null || echo loop=fail
  '") || die "jenkins verify probes could not run"
  local dns https ip ipms lan loop
  dns=$(printf '%s\n' "$out" | sed -n 's/^dns=//p'); https=$(printf '%s\n' "$out" | sed -n 's/^https=//p')
  ip=$(printf '%s\n' "$out" | sed -n 's/^ip=//p'); ipms=$(printf '%s\n' "$out" | sed -n 's/^ipms=//p')
  lan=$(printf '%s\n' "$out" | sed -n 's/^lan=//p'); loop=$(printf '%s\n' "$out" | sed -n 's/^loop=//p' | tr -d '\n')
  check() { if [ "$2" = "$3" ]; then log "jenkins PASS $1 ($4)"; else log "jenkins FAIL $1: got $2, want $3 ($4)"; failed=1; fi; }
  # A probe that did not run (empty) or whose tool is missing (127) is NOT a refusal.
  refused() { [ -n "$1" ] && [ "$1" != "0" ] && [ "$1" != "127" ]; }
  refused "$dns" && check "dns refused" ok ok "getent hosts $FOGELL_FENCE_PROBE_HOST exit $dns" || check "dns refused" "${dns:-no-output}" refused "getent hosts $FOGELL_FENCE_PROBE_HOST"
  refused "$https" && check "https refused" ok ok "curl https://$FOGELL_FENCE_PROBE_HOST exit $https" || check "https refused" "${https:-no-output}" refused "curl https://$FOGELL_FENCE_PROBE_HOST"
  if refused "$ip"; then
    check "direct-ip refused" ok ok "curl http://$FOGELL_FENCE_PROBE_IP/ exit $ip in ${ipms} ms"
    [ -n "$ipms" ] && [ "$ipms" -lt 2000 ] && check "refusal is fast" ok ok "${ipms} ms" || check "refusal is fast" slow fast "${ipms:-?} ms — a DROP would hang here; the fence REJECTS"
  else
    check "direct-ip refused" "${ip:-no-output}" refused "curl http://$FOGELL_FENCE_PROBE_IP/ in ${ipms:-?} ms"
  fi
  if [ -n "$FOGELL_FENCE_PROBE_LAN_IP" ]; then
    refused "$lan" && check "lan refused" ok ok "curl http://$FOGELL_FENCE_PROBE_LAN_IP/ exit $lan" || check "lan refused" "${lan:-no-output}" refused "curl http://$FOGELL_FENCE_PROBE_LAN_IP/"
  fi
  check "loopback open" "$loop" 200 "Jenkins answers itself on 127.0.0.1:8080"
  local inbound
  inbound=$(curl -sS -m 10 -o /dev/null -w '%{http_code}' "$FOGELL_JENKINS_URL/api/json" 2>/dev/null || echo fail)
  check "inbound open" "$inbound" 200 "$FOGELL_JENKINS_URL/api/json from this host"
  local rejected
  rejected=$( (ssh "$FOGELL_JENKINS_HOST" "$(jenkins_ns) nft list chain inet fogell_fence output 2>/dev/null" || true) | sed -n 's/.*counter packets \([0-9]*\).*/\1/p')
  [ -n "$rejected" ] && [ "$rejected" -gt 0 ] && check "rules live" ok ok "$rejected packets rejected so far" || check "rules live" "${rejected:-absent}" ">0" "reject counter"
  [ "$failed" -eq 0 ] || die "jenkins fence NOT proven — nothing may execute"
  log "jenkins fence PROVEN"
}

# --- Fogell side -------------------------------------------------------------

fogell_status() {
  local t; t=$(sudo -n nft list tables 2>/dev/null | grep -E 'fogell_fence' || true)
  if [ -n "$t" ]; then log "fogell fence tables PRESENT (a live run, or a stale rule for a dead scope):"; printf '  %s\n' "$t"; else log "fogell fence ABSENT (no run in progress)"; fi
}

fogell_run() {
  local cg
  cg=$(sed -n 's/^0:://p' /proc/self/cgroup); [ -n "$cg" ] || die "not in a cgroup v2 hierarchy"
  # The marker is not trusted: whatever the environment says, this process
  # fences (and at teardown KILLS) the cgroup it is actually in, so it must
  # be a fogell-fence scope whose name the marker carries — never a login
  # session or a service cgroup inherited from a caller (Codex on PR #392).
  local unit
  if [ "${FOGELL_FENCE_INNER:-}" = "" ] || [ "${cg##*/}" != "${FOGELL_FENCE_INNER}.scope" ]; then
    command -v systemd-run >/dev/null || die "systemd-run is required for the Fogell-side fence"
    sudo -n true 2>/dev/null || die "passwordless sudo is required to load the Fogell-side nft rule"
    unit="fogell-fence-$(date -u +%Y%m%dT%H%M%SZ)-$$"
    exec systemd-run --user --scope --quiet --slice=fogell-fence.slice --unit="$unit" \
      env FOGELL_FENCE_INNER="$unit" "$0" fogell run -- "$@"
  fi
  case "$cg" in */fogell.slice/fogell-fence.slice/fogell-fence-*.scope) ;; *) die "refusing to fence cgroup $cg: not a fogell-fence scope" ;; esac
  # Inside the scope. The cgroup path is this run's identity. FENCE_PATH and
  # FENCE_TABLE are globals on purpose: the EXIT trap runs after this function's
  # locals are gone (a first version used locals and never tore down — measured).
  local level
  FENCE_PATH=${cg#/}; level=$(printf '%s\n' "$FENCE_PATH" | awk -F/ '{print NF}')
  local path=$FENCE_PATH
  local scope uid; scope=${path##*/}; uid=$(id -u)     # fogell-fence-<ts>-<pid>.scope
  # The table carries the UID so the sweep below can tell whose scope to look
  # for; another user's live table lives under another user's cgroup root and
  # must never be classed stale from here (Codex on PR #389).
  FENCE_TABLE="fogell_fence_u${uid}_$(printf '%s' "${scope%.scope}" | sed 's/^fogell-fence-//; s/-/_/g')"   # fogell_fence_u<uid>_<ts>_<pid>
  local table=$FENCE_TABLE
  local cgroot="/sys/fs/cgroup/${path%/*}"
  local jenkins_ip ssh_ip jenkins_port url_host
  url_host=$(printf '%s' "$FOGELL_JENKINS_URL" | sed -E 's#^[a-z]+://##; s#[:/].*##')
  jenkins_port=$(printf '%s' "$FOGELL_JENKINS_URL" | sed -nE 's#^[a-z]+://[^:/]+:([0-9]+).*#\1#p')
  if [ -z "$jenkins_port" ]; then case "$FOGELL_JENKINS_URL" in https://*) jenkins_port=443 ;; *) jenkins_port=80 ;; esac; fi
  # Names are resolved from /etc/hosts ONLY (or taken numeric): a DNS lookup
  # here would be egress before the fence exists (Copilot on PR #389).
  files_ip() { case "$1" in [0-9]*.[0-9]*.[0-9]*.[0-9]*) printf '%s' "$1" ;; *) getent -s files ahostsv4 "$1" 2>/dev/null | awk 'NR==1{print $1}' ;; esac; }
  jenkins_ip=$(files_ip "$url_host"); [ -n "$jenkins_ip" ] || die "$url_host is not numeric and not in /etc/hosts — refusing to resolve it over the network before the fence exists"
  ssh_ip=$(ssh -G "$FOGELL_JENKINS_HOST" | awk '/^hostname /{print $2}'); ssh_ip=$(files_ip "$ssh_ip"); [ -n "$ssh_ip" ] || die "ssh host $FOGELL_JENKINS_HOST is not numeric and not in /etc/hosts — refusing to resolve it over the network before the fence exists"
  # Sweep rules left by DEAD scopes only: a table whose scope cgroup directory
  # still exists belongs to a live run and is never touched (a first version
  # swept every table and unfenced a concurrent run — caught by the verifier).
  local stale dir
  for stale in $(sudo -n nft list tables 2>/dev/null | awk '/fogell_fence_/{print $3}'); do
    case "$stale" in
      "fogell_fence_u${uid}_"*) ;;
      *) log "leaving $stale: not this user's table"; continue ;;
    esac
    dir="$cgroot/fogell-fence-$(printf '%s' "${stale#fogell_fence_u${uid}_}" | sed 's/_/-/g').scope"
    if [ -d "$dir" ]; then log "leaving $stale: its scope is live ($dir)"; else sudo -n nft delete table inet "$stale" 2>/dev/null && log "swept stale $stale" || true; fi
  done
  sudo -n nft -f - <<NFT || die "fogell fence rule could not be loaded"
table inet $table {
  chain output {
    type filter hook output priority 0; policy accept;
    socket cgroupv2 level $level "$path" jump fenced
  }
  chain fenced {
    ip daddr { 127.0.0.53, 127.0.0.54 } reject
    oif "lo" accept
    ip daddr $jenkins_ip tcp dport $jenkins_port accept
    ip daddr $ssh_ip tcp dport 22 accept
    counter reject
  }
}
NFT
  fence_down() {
    # Kill everything still in the scope except ourselves BEFORE the rule goes:
    # a backgrounded process must not outlive the fence (measured escape, fixed).
    local procs="/sys/fs/cgroup/$FENCE_PATH/cgroup.procs" killed=0 p
    for _ in 1 2 3; do
      for p in $(cat "$procs" 2>/dev/null); do
        [ "$p" = "$$" ] && continue; [ "$p" = "$BASHPID" ] && continue
        kill -KILL "$p" 2>/dev/null && killed=$((killed+1))
      done
      [ "$(grep -cvE "^($$|$BASHPID)$" "$procs" 2>/dev/null || true)" = 0 ] && break; sleep 0.2
    done
    sudo -n nft delete table inet "$FENCE_TABLE" 2>/dev/null || true
    log "fogell fence removed ($FENCE_TABLE); $killed leftover process(es) killed first"
  }
  trap fence_down EXIT
  trap 'exit 143' TERM INT HUP
  # If the lane that owns the lease dies, this run must not outlive it as an
  # orphan: a watchdog in the scope waits for the owner's pid to vanish and
  # then terminates this script, whose EXIT trap kills the scope and deletes
  # the rule (Codex on PR #392). The watchdog itself dies in fence_down.
  if [ -n "${FOGELL_FENCE_OWNER_PID:-}" ]; then
    ( tail --pid="$FOGELL_FENCE_OWNER_PID" -f /dev/null; kill -TERM $$ 2>/dev/null ) &
    log "watchdog armed: this run ends if owner pid $FOGELL_FENCE_OWNER_PID vanishes"
  fi
  log "fogell fence loaded for scope $path (table $table): reachable = $jenkins_ip:$jenkins_port, $ssh_ip:22 (collector), loopback minus DNS stubs"
  local failed=0 rc
  check() { if [ "$2" = "$3" ]; then log "fogell PASS $1 ($4)"; else log "fogell FAIL $1: got $2, want $3 ($4)"; failed=1; fi; }
  # A probe whose tool is missing (127) is NOT a refusal.
  refused() { [ -n "$1" ] && [ "$1" != "0" ] && [ "$1" != "127" ]; }
  curl -sS -m 8 -o /dev/null "https://$FOGELL_FENCE_PROBE_HOST" >/dev/null 2>&1 && rc=0 || rc=$?
  refused "$rc" && check "https refused" ok ok "curl https://$FOGELL_FENCE_PROBE_HOST exit $rc" || check "https refused" "$rc" refused "curl https://$FOGELL_FENCE_PROBE_HOST"
  local s e ms; s=$(date +%s%N); curl -sS -m 8 -o /dev/null "http://$FOGELL_FENCE_PROBE_IP/" >/dev/null 2>&1 && rc=0 || rc=$?; e=$(date +%s%N); ms=$(( (e - s) / 1000000 ))
  if refused "$rc"; then
    check "direct-ip refused" ok ok "curl http://$FOGELL_FENCE_PROBE_IP/ exit $rc in ${ms} ms"
    [ "$ms" -lt 2000 ] && check "refusal is fast" ok ok "${ms} ms" || check "refusal is fast" slow fast "${ms} ms"
  else
    check "direct-ip refused" "$rc" refused "curl http://$FOGELL_FENCE_PROBE_IP/ in ${ms} ms"
  fi
  if [ -n "$FOGELL_FENCE_PROBE_LAN_IP" ]; then
    curl -sS -m 8 -o /dev/null "http://$FOGELL_FENCE_PROBE_LAN_IP/" >/dev/null 2>&1 && rc=0 || rc=$?
    refused "$rc" && check "lan refused" ok ok "curl http://$FOGELL_FENCE_PROBE_LAN_IP/ exit $rc" || check "lan refused" "$rc" refused "curl http://$FOGELL_FENCE_PROBE_LAN_IP/"
  fi
  getent hosts "$FOGELL_FENCE_PROBE_HOST" >/dev/null 2>&1 && rc=0 || rc=$?
  refused "$rc" && check "dns refused" ok ok "getent hosts $FOGELL_FENCE_PROBE_HOST exit $rc" || check "dns refused" "$rc" refused "getent hosts $FOGELL_FENCE_PROBE_HOST"
  local code; code=$(curl -sS -m 10 -o /dev/null -w '%{http_code}' "$FOGELL_JENKINS_URL/api/json" 2>/dev/null || echo fail)
  check "oracle reachable" "$code" 200 "$FOGELL_JENKINS_URL/api/json"
  # -n: the probe must not drain the stdin the fenced command will inherit.
  ssh -n -o BatchMode=yes -o ConnectTimeout=10 "$FOGELL_JENKINS_HOST" true >/dev/null 2>&1 && rc=0 || rc=$?
  check "collector ssh reachable" "$rc" 0 "ssh $FOGELL_JENKINS_HOST true"
  local rejected; rejected=$(sudo -n nft list chain inet "$table" fenced 2>/dev/null | sed -n 's/.*counter packets \([0-9]*\).*/\1/p')
  [ -n "$rejected" ] && [ "$rejected" -gt 0 ] && check "rules live" ok ok "$rejected packets rejected so far" || check "rules live" "${rejected:-absent}" ">0" "reject counter"
  [ "$failed" -eq 0 ] || die "fogell fence NOT proven — nothing may execute"
  log "fogell fence PROVEN; running: $*"
  # The command runs in the background and is WAITED for: bash defers a
  # trapped signal until a foreground child exits, so a TERM from the owner
  # watchdog would otherwise wait out the whole differential (measured:
  # 117 s). `wait` is interrupted by the signal, the TERM trap exits, and the
  # EXIT trap kills the scope — the command included.
  # stdin is preserved; the fence markers are stripped so a NESTED `fogell run`
  # inside the command re-execs into its own scope instead of adopting this
  # one and killing it at teardown (verifier, round 7).
  # (bash points a background job's stdin at /dev/null BEFORE applying the
  # job's own redirections, so `<&0` would copy /dev/null; dup first.)
  # A closed stdin becomes /dev/null rather than an abort; the child gets fd 0
  # only (fd 3 closed); a subshell with `unset` rather than `env -u` so an
  # exported function still runs (verifier, round 8).
  { exec 3<&0; } 2>/dev/null || exec 3</dev/null
  ( unset FOGELL_FENCE_INNER FOGELL_FENCE_OWNER_PID; exec "$@" ) <&3 3<&- & local cmd_pid=$!
  exec 3<&-
  wait "$cmd_pid" && rc=0 || rc=$?
  rejected=$(sudo -n nft list chain inet "$table" fenced 2>/dev/null | sed -n 's/.*counter packets \([0-9]*\).*/\1/p')
  log "command exit $rc; ${rejected:-?} packets rejected over the whole fenced run (probes included)"
  return "$rc"
}

side=${1:-}; action=${2:-}
case "$side/$action" in
  jenkins/apply)  jenkins_apply ;;
  jenkins/verify) jenkins_verify ;;
  jenkins/remove) jenkins_remove ;;
  jenkins/status) jenkins_status ;;
  jenkins/quiesce) jenkins_quiesce ;;
  jenkins/present) jenkins_present ;;
  jenkins/started-at) jenkins_started_at ;;
  fogell/status)  fogell_status ;;
  fogell/run)     shift 2; [ "${1:-}" = "--" ] && shift; [ $# -gt 0 ] || die "fogell run -- <command...>"; fogell_run "$@" ;;
  *) sed -n '2,12p' "$0" >&2; exit 2 ;;
esac
