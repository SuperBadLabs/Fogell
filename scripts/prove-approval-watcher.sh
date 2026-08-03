#!/usr/bin/env bash
# FG-046e. The proof for the inbox watcher, because scenario N is only worth
# more than the sampler it replaced if this thing is PROVEN to report a breach.
#
# Five of the eight cases below are about the WATCHER rather than the inbox —
# its two blind spots, and the two count cases that stop "no overlap" being
# accepted as "both prompts happened". That ratio is deliberate: on this gate's
# other checker nearly every defect lived at the checker's own edges, and each
# was found by planting the case rather than by reading the code again.
#
# (This header said "three of the five" until the count cases landed. A stale
# claim inside the proof of the stale-claim checker is not a joke worth keeping.)
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
# LIVENESS BEFORE THE INTENTIONAL KILL — the same check scenario N gained, and
# this proof did not, which is the checker-blind-at-its-own-edges pattern landing
# on the proof of the checker. A watcher that exits after recording only the
# first CREATE/DELETE pair leaves a log whose peak is 1 and whose events are
# well-formed, so `withdraw-then-publish` reports exit 0 and a partial watcher
# implementation passes its own planted proof. After the kill the two states are
# indistinguishable, so it has to be asked before.
WATCHER_DIED=0
stop_watch() {
  sleep 0.5
  kill -0 "$WPID" 2>/dev/null || WATCHER_DIED=1
  kill "$WPID" 2>/dev/null
  wait "$WPID" 2>/dev/null
  WPID=""
}

run_case() {
  local label=$1 want=$2 body=$3 expect=${4:-}
  local d log
  d=$(mktemp -d /tmp/approval-watch.XXXXXX); log="$d/events"; : > "$log"
  if ! start_watch "$d" "$log"; then note "$label" "WATCHER NEVER REGISTERED"; FAIL=1; return; fi
  ( cd "$d" && eval "$body" )
  WATCHER_DIED=0
  stop_watch
  if [ "$WATCHER_DIED" -eq 1 ]; then
    note "$label" "WATCHER EXITED BEFORE TEARDOWN — its log is a partial capture"
    sed 's/^/      /' "$log"; FAIL=1; return
  fi
  "$WATCH" report "$log" $expect >"$d/out" 2>&1; local rc=$?
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

# The expected-publish COUNT. Without it, "no overlap" was accepted as proof
# that both retry occurrences advertised a prompt — two different claims, and a
# regression that logged the second prompt without publishing a marker cleared
# the scenario.
run_case "two published when two expected" 0 '
  touch a.pending; rm -f a.pending
  touch b.pending; rm -f b.pending' 2

run_case "only one published, two expected" 1 '
  touch a.pending
  rm -f a.pending' 2

# The SAME prompt published twice is one prompt. Counting raw events let this
# satisfy "expected 2" while only one marker was ever advertised — and scenario
# N exists precisely to check that the retry occurrence published its OWN.
run_case "same prompt twice, two expected" 1 '
  touch a.pending
  rm -f a.pending
  touch a.pending
  rm -f a.pending' 2

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

[ "$FAIL" -eq 0 ] && echo "APPROVAL-WATCH PROOF: it catches the breach and the miscount, clears the compliant case, and refuses both blind spots" \
                  || { echo "APPROVAL-WATCH PROOF FAILED"; exit 1; }
