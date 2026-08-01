#!/usr/bin/env bash
# FG-046b. The approval lane: a REAL `input` answered through the host's
# approvals inbox, with the answer surviving a genuine SIGKILL.
#
# The behaviour being reproduced was MEASURED on the pinned lab (Jenkins
# 2.568.1, `scripts/probe-input.bb`): a pending `input` survives a
# controller restart with the SAME action id and is still approvable afterwards,
# approving prints NOTHING, and a human abort ends the build ABORTED with
# `Rejected`.
#
# Proves, in order:
#   A. an interrupted `input` that NOBODY answered still refuses (exit 3) — the
#      exemption is for an ANSWERED prompt, not for `input` as a step kind;
#      once answered, the resumed attempt proceeds silently and the following
#      step runs exactly once;
#   B. the answer is durable in the JOURNAL, not in the inbox: with the inbox
#      deleted, a rewound attempt re-runs the prompt and never asks again;
#   C. `reject` ends the build ABORTED with `Rejected`, and the step after the
#      prompt does not run;
#   D. an answer is CONSUMED — a second build sharing the inbox asks again
#      instead of inheriting the first build's approval;
#   E. with NO inbox there is no approver, and an un-timed prompt fails closed
#      by name instead of waiting forever for a human who cannot answer;
#   F. neither a half-written answer nor an ambiguous two-line one is an answer
#      (by either separator — a bare CR is a line break too);
#   G. the same physical journal reached through a symlink alias resolves to the
#      same action id, so an answer published under one spelling is found under
#      the other instead of the human being asked again.
# D, E and F exist because a pre-push review found all three as live defects:
# a reusable answer file that auto-approved every later build, a silent
# unbounded hang, and `approve alice` read as a complete approval by "unknown"
# after only `approve` had landed. A lane that cannot see a defect does not
# cover it.
# Everything is asserted; the transcript is the lane's evidence.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

LANE=$(mktemp -d /tmp/fogell-approval-lane.XXXXXX)
trap '[ "${LANE_OK:-0}" = 1 ] && rm -rf "$LANE" || echo "lane FAILED — evidence kept at $LANE" >&2' EXIT

dotnet build -c Release --nologo >/dev/null
HOST_BIN=$(find tools/Fogell.Run.Host/bin/Release -name Fogell.Run.Host -type f | head -1)
[ -x "$HOST_BIN" ] || { echo "FAIL: host binary not found"; exit 1; }

JF='pipeline {
    agent any
    stages {
        stage("Gate") {
            steps {
                sh "echo one >> markers.txt"
                input message: "Deploy?", ok: "Ship it"
                sh "echo two >> markers.txt"
            }
        }
    }
}'

# wait for a prompt to be published to the inbox, and echo its action id
await_pending() {
  local inbox=$1
  for _ in $(seq 1 240); do
    local f
    f=$(find "$inbox" -maxdepth 1 -name '*.pending' 2>/dev/null | head -1)
    if [ -n "$f" ]; then basename "$f" .pending; return 0; fi
    sleep 0.25
  done
  return 1
}

# ---------------------------------------------------------------- scenario A
echo "=== A1: SIGKILL while the prompt is UNANSWERED ==="
A="$LANE/a"; mkdir -p "$A"
printf '%s\n' "$JF" > "$A/Jenkinsfile"
AWS="$A/ws"; AINBOX="$A/approvals"; AJ="$A/build.journal"; AMARK="$AWS/gate/markers.txt"
mkdir -p "$AINBOX"

"$HOST_BIN" "$A/Jenkinsfile" "$AWS" gate "$AJ" "$AINBOX" > "$A/run1.log" 2>&1 &
PID=$!
ID=$(await_pending "$AINBOX") || { echo "FAIL: the prompt was never published to the inbox"; cat "$A/run1.log"; exit 1; }
echo "prompt published as $ID:"
sed 's/^/  | /' "$AINBOX/$ID.pending"
kill -9 "$PID"; wait "$PID" 2>/dev/null || true
# BOTH checks, because they answer different questions. `kill -0` is exact and
# says this process is gone; the pattern scan says no OTHER host survived it —
# the case the restart lane exists for, where a driver process is SIGKILLed and
# the real walker lives on underneath it. The pattern is REGEX-QUOTED: the
# binary name contains dots, and an unescaped `.` matches any character, so the
# strict check was quietly a loose one.
HOST_RE="^$(printf '%s' "$HOST_BIN" | sed 's/[.[\*^$()+?{}|\\]/\\&/g') "
kill -0 "$PID" 2>/dev/null && { echo "FAIL: the killed host is still alive"; exit 1; }
for _ in 1 2 3 4; do pgrep -f "$HOST_RE" >/dev/null || break; sleep 0.5; done
pgrep -f "$HOST_RE" >/dev/null && { echo "FAIL: a host process survived the SIGKILL"; pgrep -af "$HOST_RE"; exit 1; }
grep -q '^completed:' "$A/run1.log" && { echo "FAIL: run 1 completed despite the kill"; exit 1; }
grep -q '^input-decision' "$AJ" && { echo "FAIL: an answer was journaled that nobody gave"; exit 1; }

echo "=== A2: an UNANSWERED interrupted input must still refuse (exit 3) ==="
set +e
"$HOST_BIN" "$A/Jenkinsfile" "$AWS" gate "$AJ" "$AINBOX" > "$A/run2.log" 2>&1
RC=$?
set -e
[ "$RC" -eq 3 ] || { echo "FAIL: expected refusal exit 3, got $RC"; cat "$A/run2.log"; exit 1; }
grep -q 'needs-reconciliation: Gate#1' "$A/run2.log" || { echo "FAIL: refusal did not name Gate#1"; cat "$A/run2.log"; exit 1; }
echo "refused: $(grep -o 'needs-reconciliation.*' "$A/run2.log")"

echo "=== A3: the human answers; the resumed attempt proceeds SILENTLY ==="
printf 'approve alice\n' > "$AINBOX/$ID.decision"
timeout 120 "$HOST_BIN" "$A/Jenkinsfile" "$AWS" gate "$AJ" "$AINBOX" > "$A/run3.log" 2>&1 || {
  echo "FAIL: the answered resume did not complete"; cat "$A/run3.log"; exit 1; }
grep -q 'completed: success' "$A/run3.log" || { echo "FAIL: resume not successful"; cat "$A/run3.log"; exit 1; }
grep -q 'skip (durably finished): Gate#0' "$A/run3.log" || { echo "FAIL: step 0 not skipped"; exit 1; }
# the prompt itself is re-narrated (this attempt has its own console; Jenkins'
# is one continuous log across a restart and cannot be compared line-for-line)
grep -q '| Deploy?' "$A/run3.log" || { echo "FAIL: the prompt was not narrated"; exit 1; }
grep -q '| Ship it or Abort' "$A/run3.log" || { echo "FAIL: the ok label was not narrated"; exit 1; }
# MEASURED: approving narrates NOTHING. Anything approval-shaped is a divergence.
grep -qiE '\| .*(approved|approval|alice)' "$A/run3.log" && { echo "FAIL: approval narrated something Jenkins does not"; exit 1; }
grep -q $'^input-decision\tGate\t1\tapproved\talice$' "$AJ" || { echo "FAIL: the answer was not journaled"; sed 's/^/  | /' "$AJ"; exit 1; }
[ -f "$AINBOX/$ID.pending" ] && { echo "FAIL: an answered prompt is still listed as pending"; exit 1; }
[ "$(grep -c '^two$' "$AMARK")" -eq 1 ] || { echo "FAIL: the step after the prompt did not run exactly once"; exit 1; }
[ "$(grep -c '^one$' "$AMARK")" -eq 1 ] || { echo "FAIL: the durable step before the prompt re-ran"; exit 1; }
echo "answered, proceeded silently, journal:"
sed 's/^/  | /' "$AJ"

# ---------------------------------------------------------------- scenario B
echo "=== B: the answer lives in the JOURNAL — inbox deleted, no second ask ==="
# The real crash window (answer journaled, step not yet finished) is microseconds
# wide, so the state is CONSTRUCTED from A's journal rather than raced for: drop
# the records that came after the answer. Everything else is a real run.
grep -vP '^(build-finished|stage-committed)\t' "$AJ" \
  | grep -vP '^step-(started|finished)\tGate\t2\t' \
  | grep -vP '^step-finished\tGate\t1\t' > "$A/rewound.journal"
grep -q '^input-decision' "$A/rewound.journal" || { echo "FAIL: the rewind dropped the answer"; exit 1; }
# the inbox is DELETED but still passed: a host that needed to ask would
# recreate it and publish a pending marker, and this attempt would hang
rm -rf "$AINBOX"
timeout 120 "$HOST_BIN" "$A/Jenkinsfile" "$AWS" gate "$A/rewound.journal" "$AINBOX" > "$A/run4.log" 2>&1 || {
  echo "FAIL: the rewound attempt did not complete"; cat "$A/run4.log"; exit 1; }
grep -q 'completed: success' "$A/run4.log" || { echo "FAIL: rewound attempt not successful"; cat "$A/run4.log"; exit 1; }
grep -q 'needs-reconciliation' "$A/run4.log" && { echo "FAIL: an answered input was sent for reconciliation"; exit 1; }
[ ! -d "$AINBOX" ] || { echo "FAIL: the inbox was recreated — the answer was not read from the journal"; exit 1; }
[ "$(grep -c '^input-decision' "$A/rewound.journal")" -eq 1 ] || {
  echo "FAIL: the answer was re-recorded, so it was asked again"; exit 1; }
echo "resumed with no inbox at all; the prompt was answered from the journal"

# ---------------------------------------------------------------- scenario C
echo "=== C: reject — the build is ABORTED and the next step never runs ==="
C="$LANE/c"; mkdir -p "$C"
printf '%s\n' "$JF" > "$C/Jenkinsfile"
CWS="$C/ws"; CINBOX="$C/approvals"; CJ="$C/build.journal"; CMARK="$CWS/gate/markers.txt"
mkdir -p "$CINBOX"

timeout 180 "$HOST_BIN" "$C/Jenkinsfile" "$CWS" gate "$CJ" "$CINBOX" > "$C/run.log" 2>&1 &
PID=$!
CID=$(await_pending "$CINBOX") || { echo "FAIL: the prompt was never published"; cat "$C/run.log"; exit 1; }
printf 'reject carol\n' > "$CINBOX/$CID.decision"
set +e
wait "$PID"
RC=$?
set -e
[ "$RC" -eq 1 ] || { echo "FAIL: a rejected build must not exit 0 (got $RC)"; cat "$C/run.log"; exit 1; }
grep -q 'completed: aborted' "$C/run.log" || { echo "FAIL: rejection did not abort the build"; cat "$C/run.log"; exit 1; }
grep -q '| Rejected' "$C/run.log" || { echo "FAIL: the measured 'Rejected' line is missing"; cat "$C/run.log"; exit 1; }
grep -q '^two$' "$CMARK" 2>/dev/null && { echo "FAIL: the step after a rejected prompt ran"; exit 1; }
grep -q $'^input-decision\tGate\t1\trejected\tcarol$' "$CJ" || { echo "FAIL: the rejection was not journaled"; exit 1; }
echo "rejected, aborted, nothing after the prompt ran"

# ---------------------------------------------------------------- scenario D
echo "=== D: an answer is consumed — a second build must ask again ==="
# scenario A's inbox is reused verbatim, with a fresh journal and workspace:
# the shape an operator gets by pointing every build at one approvals directory
D="$LANE/d"; mkdir -p "$D/approvals"
printf '%s\n' "$JF" > "$D/Jenkinsfile"
printf 'approve dave\n' > "$D/approvals/seed"   # placed via the published id below
"$HOST_BIN" "$D/Jenkinsfile" "$D/ws1" gate "$D/b1.journal" "$D/approvals" > "$D/b1.log" 2>&1 &
PID=$!
DID=$(await_pending "$D/approvals") || { echo "FAIL: build 1 never published a prompt"; cat "$D/b1.log"; exit 1; }
cp "$D/approvals/seed" "$D/approvals/$DID.decision"
set +e; wait "$PID"; set -e
grep -q 'completed: success' "$D/b1.log" || { echo "FAIL: build 1 did not complete"; cat "$D/b1.log"; exit 1; }
[ -f "$D/approvals/$DID.decision" ] && { echo "FAIL: the answer was left in the inbox for the next build to reuse"; exit 1; }
find "$D/approvals" -maxdepth 1 -name '*.pending' | grep -q . && { echo "FAIL: a stale pending marker outlived build 1"; exit 1; }

# build 2: new journal, new workspace, SAME inbox, nobody answers.
# Waiting for the prompt is the POINT here, so the timeout firing (124) is the
# passing outcome and must not trip `set -e` before it can be asserted on.
set +e
timeout 25 "$HOST_BIN" "$D/Jenkinsfile" "$D/ws2" gate "$D/b2.journal" "$D/approvals" > "$D/b2.log" 2>&1
RC=$?
set -e
[ "$RC" -eq 124 ] || { echo "FAIL: build 2 finished (rc=$RC) without a human answering it"; cat "$D/b2.log"; exit 1; }
grep -q '^input-decision' "$D/b2.journal" && { echo "FAIL: build 2 recorded an answer nobody gave it"; exit 1; }
find "$D/approvals" -maxdepth 1 -name '*.pending' | grep -q . || { echo "FAIL: build 2 did not ask"; exit 1; }
echo "build 2 asked again and waited; the first build's answer did not carry over"

# ---------------------------------------------------------------- scenario E
echo "=== E: no inbox means no approver — fail closed, do not hang ==="
E="$LANE/e"; mkdir -p "$E"
printf '%s\n' "$JF" > "$E/Jenkinsfile"
set +e
timeout 60 "$HOST_BIN" "$E/Jenkinsfile" "$E/ws" gate "$E/build.journal" > "$E/run.log" 2>&1
RC=$?
set -e
[ "$RC" -ne 124 ] || { echo "FAIL: an un-timed prompt with no approver HUNG (silently, since output is buffered)"; exit 1; }
[ "$RC" -eq 1 ] || { echo "FAIL: expected a failed build (exit 1), got $RC"; cat "$E/run.log"; exit 1; }
grep -q 'completed: failure' "$E/run.log" || { echo "FAIL: did not fail closed"; cat "$E/run.log"; exit 1; }
# the REASON is not asserted from this log by design: `ERROR:`-shaped engine
# diagnostics are captured as the run's reported failure reason and excluded
# from compared output (Trace.isDiagnosticLine), so the console shows the
# prompt and then stops. What matters here is that it STOPS.
[ "$(tail -1 "$E/run.log")" = "| Ship it or Abort" ] || {
  echo "FAIL: something ran after the unanswerable prompt"; cat "$E/run.log"; exit 1; }
echo "failed closed at the prompt, exit $RC — no hang"

# ---------------------------------------------------------------- scenario F
echo "=== F: neither a fragment nor an ambiguous two-line answer counts ==="
F="$LANE/f"; mkdir -p "$F/approvals"
printf '%s\n' "$JF" > "$F/Jenkinsfile"
"$HOST_BIN" "$F/Jenkinsfile" "$F/ws" gate "$F/build.journal" "$F/approvals" > "$F/run.log" 2>&1 &
PID=$!
FID=$(await_pending "$F/approvals") || { echo "FAIL: no prompt published"; cat "$F/run.log"; exit 1; }
printf 'approve' > "$F/approvals/$FID.decision"     # verdict only, no submitter, no newline
sleep 3
grep -q '^input-decision' "$F/build.journal" && { echo "FAIL: a fragment was accepted as an answer"; exit 1; }
kill -0 "$PID" 2>/dev/null || { echo "FAIL: the build acted on a fragment"; cat "$F/run.log"; exit 1; }
# two complete lines — two approvers, or automation appending. Ambiguous is not
# an answer: read with a two-field split it approved as "alice reject bob".
printf 'approve alice\nreject bob\n' > "$F/approvals/$FID.decision"
sleep 3
grep -q '^input-decision' "$F/build.journal" && { echo "FAIL: an ambiguous two-line answer was accepted"; sed 's/^/  | /' "$F/build.journal"; exit 1; }
kill -0 "$PID" 2>/dev/null || { echo "FAIL: the build acted on an ambiguous answer"; cat "$F/run.log"; exit 1; }
# the same ambiguity separated by a bare CR: newline-terminated, one `\n`, and
# the journal's own sanitiser would have flattened the CR to a space and
# recorded it as a decision
printf 'approve alice\rreject bob\n' > "$F/approvals/$FID.decision"
sleep 3
grep -q '^input-decision' "$F/build.journal" && { echo "FAIL: a CR-separated ambiguous answer was accepted"; sed 's/^/  | /' "$F/build.journal"; exit 1; }
kill -0 "$PID" 2>/dev/null || { echo "FAIL: the build acted on a CR-separated answer"; cat "$F/run.log"; exit 1; }
# a blank second line is ambiguous too — and trimming EVERY trailing terminator
# quietly turned it back into a clean one-liner
printf 'approve alice\n\n' > "$F/approvals/$FID.decision"
sleep 3
grep -q '^input-decision' "$F/build.journal" && { echo "FAIL: a trailing blank line was trimmed into an answer"; sed 's/^/  | /' "$F/build.journal"; exit 1; }
kill -0 "$PID" 2>/dev/null || { echo "FAIL: the build acted on a trailing-blank-line answer"; cat "$F/run.log"; exit 1; }
printf 'approve frank\n' > "$F/approvals/$FID.decision"   # the completed write
set +e; wait "$PID"; RC=$?; set -e
[ "$RC" -eq 0 ] || { echo "FAIL: the completed answer was not honoured (rc=$RC)"; cat "$F/run.log"; exit 1; }
grep -q $'^input-decision\tGate\t1\tapproved\tfrank$' "$F/build.journal" || {
  echo "FAIL: the submitter was not recorded from the completed write"; sed 's/^/  | /' "$F/build.journal"; exit 1; }
echo "waited through the fragment AND the ambiguous pair, then took the completed answer with its submitter intact"

# ---------------------------------------------------------------- scenario G
echo "=== G: the same journal through a symlink alias finds the same answer ==="
G="$LANE/g"; mkdir -p "$G/approvals"
printf '%s\n' "$JF" > "$G/Jenkinsfile"
"$HOST_BIN" "$G/Jenkinsfile" "$G/ws" gate "$G/build.journal" "$G/approvals" > "$G/run1.log" 2>&1 &
PID=$!
GID=$(await_pending "$G/approvals") || { echo "FAIL: no prompt published"; cat "$G/run1.log"; exit 1; }
kill -9 "$PID"; wait "$PID" 2>/dev/null || true
printf 'approve grace\n' > "$G/approvals/$GID.decision"
# the human answered the id published for the REAL path; the resume reaches the
# same physical journal by another name, which every other identity check here
# already tolerates
ln -s "$G/build.journal" "$G/alias.journal"
timeout 120 "$HOST_BIN" "$G/Jenkinsfile" "$G/ws" gate "$G/alias.journal" "$G/approvals" > "$G/run2.log" 2>&1 || {
  echo "FAIL: the aliased resume did not complete — the answer was not found"; cat "$G/run2.log"; exit 1; }
grep -q 'needs-reconciliation' "$G/run2.log" && { echo "FAIL: an answered prompt was sent for reconciliation under an alias"; exit 1; }
grep -q 'completed: success' "$G/run2.log" || { echo "FAIL: aliased resume not successful"; cat "$G/run2.log"; exit 1; }
grep -q $'^input-decision\tGate\t1\tapproved\tgrace$' "$G/build.journal" || {
  echo "FAIL: the answer was not journaled under the alias"; sed 's/^/  | /' "$G/build.journal"; exit 1; }
echo "the alias resolved to the same action id; nobody was asked twice"

echo "=== G2: a terminal journal sweeps a marker its build left behind ==="
# the state a kill between the terminal record's sync and the in-process cleanup
# leaves: a finished build with a prompt still advertised as outstanding
touch "$G/approvals/$GID.pending"
"$HOST_BIN" "$G/Jenkinsfile" "$G/ws" gate "$G/build.journal" "$G/approvals" > "$G/run3.log" 2>&1
grep -q 'already-terminal' "$G/run3.log" || { echo "FAIL: expected already-terminal"; cat "$G/run3.log"; exit 1; }
[ -f "$G/approvals/$GID.pending" ] && { echo "FAIL: a finished build still advertises a pending prompt"; exit 1; }
echo "swept on the already-terminal path"

LANE_OK=1
echo "APPROVAL LANE: ALL ASSERTIONS PASSED"
