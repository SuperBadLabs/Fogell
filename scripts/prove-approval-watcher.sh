#!/usr/bin/env bash
# FG-046e. The proof for the inbox watcher, because scenario N is only worth
# more than the sampler it replaced if this thing is PROVEN to report a breach.
#
# Three of the five cases below are failure modes of the WATCHER, not of the
# inbox. That ratio is deliberate: on this gate's other checker every defect but
# one lived at the checker's own edges, and each was found by planting the case
# rather than by reading the code again.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
WATCH="tools/Fogell.Watch.Inbox/bin/Release/net10.0/Fogell.Watch.Inbox"
[ -x "$WATCH" ] || WATCH="$(find tools/Fogell.Watch.Inbox/bin -name 'Fogell.Watch.Inbox' -type f 2>/dev/null | head -1)"
[ -n "$WATCH" ] && [ -x "$WATCH" ] || { echo "APPROVAL-WATCH PROOF: watcher not built"; exit 1; }
WATCH="$(realpath "$WATCH")"

FAIL=0
WPID=""
note() { printf '  %-38s %s\n' "$1" "$2"; }
cleanup() { [ -n "$WPID" ] && kill "$WPID" 2>/dev/null; }
trap cleanup EXIT

# Block until the watcher has REGISTERED, not until it has started. A process
# that is merely running has not necessarily subscribed, and the publish it
# misses is the publish that matters.
start_watch() {
  local dir=$1 log=$2
  "$WATCH" watch "$dir" "$log" >/dev/null 2>&1 &
  WPID=$!
  local i=0
  until grep -q READY "$log" 2>/dev/null; do
    i=$((i+1)); [ "$i" -gt 300 ] && return 1
    sleep 0.05
  done
}
stop_watch() { sleep 0.5; kill "$WPID" 2>/dev/null; wait "$WPID" 2>/dev/null; WPID=""; }

run_case() {
  local label=$1 want=$2 body=$3
  local d log
  d=$(mktemp -d /tmp/approval-watch.XXXXXX); log="$d/events"; : > "$log"
  if ! start_watch "$d" "$log"; then note "$label" "WATCHER NEVER REGISTERED"; FAIL=1; return; fi
  ( cd "$d" && eval "$body" )
  stop_watch
  "$WATCH" report "$log" >"$d/out" 2>&1; local rc=$?
  if [ "$rc" -eq "$want" ]; then
    note "$label" "exit $rc — OK"
  else
    note "$label" "WRONG — exit $rc, wanted $want"; sed 's/^/      /' "$d/out"; FAIL=1
  fi
}

echo "=== does it see a breach the sampler could not? ==="
# The exact shape scenario N is named against: the successor is published BEFORE
# the expired prompt is withdrawn, and the overlap closes almost immediately. A
# 0.2s sampler misses this. The watcher must not.
run_case "overlapping publish (the breach)" 1 '
  touch a.pending
  touch b.pending
  rm -f a.pending
  rm -f b.pending'

# The same breach, published the way a careful implementation writes files —
# atomic rename. If renames were not counted, an atomic inbox would look like an
# inbox that never published anything at all.
run_case "overlap published by atomic rename" 1 '
  touch a.tmp && mv -f a.tmp a.pending
  touch b.tmp && mv -f b.tmp b.pending
  rm -f a.pending b.pending'

# The compliant ordering: withdraw, THEN publish. Never two live at once.
run_case "withdraw-then-publish (compliant)" 0 '
  touch a.pending
  rm -f a.pending
  touch b.pending
  rm -f b.pending'

echo "=== the watcher's own failure modes ==="
# A watcher that died, or subscribed too late, leaves an empty log — whose peak
# is 0, which sails straight through a `<= 1` assertion.
d=$(mktemp -d /tmp/approval-watch.XXXXXX); printf 'READY\n' > "$d/events"
"$WATCH" report "$d/events" >"$d/out" 2>&1; rc=$?
if [ "$rc" -eq 3 ] && grep -q "not the same as seeing nothing wrong" "$d/out"; then
  note "watcher saw nothing at all" "refused (exit 3) — OK"
else
  note "watcher saw nothing at all" "DID NOT REFUSE — exit $rc"; sed 's/^/      /' "$d/out"; FAIL=1
fi

# A dropped-event log supports no conclusion in either direction.
d=$(mktemp -d /tmp/approval-watch.XXXXXX)
printf 'READY\nCREATE a.pending\nOVERFLOW\nDELETE a.pending\n' > "$d/events"
"$WATCH" report "$d/events" >"$d/out" 2>&1; rc=$?
if [ "$rc" -eq 3 ] && grep -q "dropped events" "$d/out"; then
  note "runtime dropped events" "refused (exit 3) — OK"
else
  note "runtime dropped events" "DID NOT REFUSE — exit $rc"; sed 's/^/      /' "$d/out"; FAIL=1
fi

[ "$FAIL" -eq 0 ] && echo "APPROVAL-WATCH PROOF: it catches the breach, clears the compliant case, and refuses both blind spots" \
                  || { echo "APPROVAL-WATCH PROOF FAILED"; exit 1; }
