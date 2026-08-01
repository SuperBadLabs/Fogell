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
#      the other instead of the human being asked again;
#   H. two prompts inside one wrapper are two GATES: they share a durability key
#      and must not share an answer;
#   I. a HARD LINK to the journal is the same build — no path resolution can
#      canonicalise one, so the build's identity is carried by the journal;
#   J. a gate whose own machinery breaks FAILS the build; it does not open. An
#      unwritable inbox inside a parallel branch used to fault the branch task,
#      be swallowed by the waiter, and finish SUCCESS unapproved;
#   K. a marker published by an attempt that DIED is swept when the build
#      finishes — cleanup identifies markers by what they say, not by what the
#      cleaning process remembers publishing;
#   L. `retry` does not re-ask a human who said no: a rejection is an
#      interruption, not a retryable failure;
#   M. and it escapes NESTED retries — the inner scope's fresh per-attempt
#      signal must still reach the outer one;
#   N. a prompt killed by its timeout is WITHDRAWN before the retry publishes the
#      next one, so the inbox never advertises a gate nothing is listening to.
# D, E and F exist because a pre-push review found all three as live defects:
# a reusable answer file that auto-approved every later build, a silent
# unbounded hang, and `approve alice` read as a complete approval by "unknown"
# after only `approve` had landed. A lane that cannot see a defect does not
# cover it.
# Everything is asserted; the transcript is the lane's evidence.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

LANE=$(mktemp -d /tmp/fogell-approval-lane.XXXXXX)
cleanup() {
  # a lane that dies mid-scenario must not leave a host waiting on a prompt
  # nobody will answer — it would outlive this run and fail the NEXT one
  # `|| true` is load-bearing: pkill exits 1 when nothing matches, and as the
  # LAST command of an && list that failure is NOT exempt from `set -e` — the
  # trap died there, taking the script's exit code to 1 with every assertion
  # passed. A cleanup step must never be able to fail the thing it cleans up.
  [ -n "${HOST_RE_BIN:-}" ] && pkill -9 -f "^${HOST_RE_BIN} .*${LANE_RE}" 2>/dev/null || true
  [ "${LANE_OK:-0}" = 1 ] && rm -rf "$LANE" || echo "lane FAILED — evidence kept at $LANE" >&2
}
trap cleanup EXIT

dotnet build -c Release --nologo >/dev/null
HOST_BIN=$(find tools/Fogell.Run.Host/bin/Release -name Fogell.Run.Host -type f | head -1)
[ -x "$HOST_BIN" ] || { echo "FAIL: host binary not found"; exit 1; }
# `pgrep -f` takes a REGEX: both of these appear in patterns below and contain
# characters (dots, most of all) that would otherwise match anything
requote() { printf '%s' "$1" | sed 's/[.[\*^$()+?{}|\\]/\\&/g'; }
HOST_RE_BIN=$(requote "$HOST_BIN")
LANE_RE=$(requote "$LANE")

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
# SCOPED TO THIS LANE's directory as well as the binary: the check means "no
# host is still working on MY files", and matching the binary alone let an
# unrelated host elsewhere on the machine fail a lane that was perfectly fine.
HOST_RE="^${HOST_RE_BIN} .*${LANE_RE}"
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
grep -q $'^input-decision\tGate\t1\t1\tapproved\talice$' "$AJ" || { echo "FAIL: the answer was not journaled"; sed 's/^/  | /' "$AJ"; exit 1; }
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
grep -q $'^input-decision\tGate\t1\t1\trejected\tcarol$' "$CJ" || { echo "FAIL: the rejection was not journaled"; exit 1; }
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
grep -q $'^input-decision\tGate\t1\t1\tapproved\tfrank$' "$F/build.journal" || {
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
grep -q $'^input-decision\tGate\t1\t1\tapproved\tgrace$' "$G/build.journal" || {
  echo "FAIL: the answer was not journaled under the alias"; sed 's/^/  | /' "$G/build.journal"; exit 1; }
echo "the alias resolved to the same action id; nobody was asked twice"

echo "=== G2: a terminal journal sweeps a marker its build left behind ==="
# the state a kill between the terminal record's sync and the in-process cleanup
# leaves: a finished build with a prompt still advertised as outstanding
printf 'stage\tGate\nstep\t1\nprompt#\t1\nprompt\tDeploy?\n' > "$G/approvals/$GID.pending"
"$HOST_BIN" "$G/Jenkinsfile" "$G/ws" gate "$G/build.journal" "$G/approvals" > "$G/run3.log" 2>&1
grep -q 'already-terminal' "$G/run3.log" || { echo "FAIL: expected already-terminal"; cat "$G/run3.log"; exit 1; }
[ -f "$G/approvals/$GID.pending" ] && { echo "FAIL: a finished build still advertises a pending prompt"; exit 1; }
echo "swept on the already-terminal path"

# ---------------------------------------------------------------- scenario I
echo "=== I: a HARD LINK to the journal is the same build ==="
# a hard link has no distinguished original, so no amount of path resolution can
# canonicalise it — the identity has to be carried BY the journal
I="$LANE/i"; mkdir -p "$I/approvals"
printf '%s\n' "$JF" > "$I/Jenkinsfile"
"$HOST_BIN" "$I/Jenkinsfile" "$I/ws" gate "$I/build.journal" "$I/approvals" > "$I/run1.log" 2>&1 &
PID=$!
IID=$(await_pending "$I/approvals") || { echo "FAIL: no prompt published"; cat "$I/run1.log"; exit 1; }
kill -9 "$PID"; wait "$PID" 2>/dev/null || true
printf 'approve ivan\n' > "$I/approvals/$IID.decision"
ln "$I/build.journal" "$I/hard.journal"
[ "$(stat -c %i "$I/build.journal")" = "$(stat -c %i "$I/hard.journal")" ] || { echo "FAIL: not actually a hard link"; exit 1; }
timeout 120 "$HOST_BIN" "$I/Jenkinsfile" "$I/ws" gate "$I/hard.journal" "$I/approvals" > "$I/run2.log" 2>&1 || {
  echo "FAIL: the hard-linked resume did not complete — the answer was not found"; cat "$I/run2.log"; exit 1; }
grep -q 'needs-reconciliation' "$I/run2.log" && { echo "FAIL: an answered prompt was sent for reconciliation under a hard link"; exit 1; }
grep -q 'completed: success' "$I/run2.log" || { echo "FAIL: hard-linked resume not successful"; cat "$I/run2.log"; exit 1; }
grep -q $'^input-decision\tGate\t1\t1\tapproved\tivan$' "$I/build.journal" || {
  echo "FAIL: the answer was not journaled under the hard link"; sed 's/^/  | /' "$I/build.journal"; exit 1; }
[ "$(grep -c '^build-identity' "$I/build.journal")" -eq 1 ] || {
  echo "FAIL: the build identity was minted more than once"; sed 's/^/  | /' "$I/build.journal"; exit 1; }
echo "same inode, same identity, same action id; nobody was asked twice"

# ---------------------------------------------------------------- scenario H
echo "=== H: two prompts under one wrapper are two gates ==="
# both `input`s journal under the SAME durability key (the top-level `timeout`),
# so an approval cached by key alone answered the second gate with the first
# human's decision — nobody reviewed it
H="$LANE/h"; mkdir -p "$H/approvals"
cat > "$H/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage("Gate") {
            steps {
                timeout(time: 10, unit: 'MINUTES') {
                    input message: "Deploy the app?", ok: "Ship it"
                    sh "echo app >> markers.txt"
                    input message: "And the database?", ok: "Ship it"
                    sh "echo db >> markers.txt"
                }
            }
        }
    }
}
JF
timeout 200 "$HOST_BIN" "$H/Jenkinsfile" "$H/ws" gate "$H/build.journal" "$H/approvals" > "$H/run.log" 2>&1 &
PID=$!
H1=$(await_pending "$H/approvals") || { echo "FAIL: the first prompt was never published"; cat "$H/run.log"; exit 1; }
grep -q 'Deploy the app?' "$H/approvals/$H1.pending" || { echo "FAIL: wrong first prompt"; cat "$H/approvals/$H1.pending"; exit 1; }
printf 'approve heidi\n' > "$H/approvals/$H1.decision"

# the SECOND gate must publish its own prompt and wait for its own human
# explicitly NOT the first gate's marker: it is consumed asynchronously once the
# answer is durable, so a plain "any pending file" search can still see it
H2=""
for _ in $(seq 1 240); do
  H2=$(find "$H/approvals" -maxdepth 1 -name '*.pending' ! -name "$H1.pending" | head -1)
  [ -n "$H2" ] && break
  sleep 0.25
done
[ -n "$H2" ] || { echo "FAIL: the second gate never asked — it inherited the first answer"; cat "$H/run.log"; sed 's/^/  | /' "$H/build.journal"; exit 1; }
H2=$(basename "$H2" .pending)
grep -q 'And the database?' "$H/approvals/$H2.pending" || { echo "FAIL: the second marker is not the second prompt"; cat "$H/approvals/$H2.pending"; exit 1; }
grep -q '^db$' "$H/ws/gate/markers.txt" 2>/dev/null && { echo "FAIL: the step past the SECOND gate ran before anyone answered it"; exit 1; }
printf 'reject heidi\n' > "$H/approvals/$H2.decision"
set +e; wait "$PID"; RC=$?; set -e
[ "$RC" -eq 1 ] || { echo "FAIL: rejecting the second gate should not exit 0 (got $RC)"; cat "$H/run.log"; exit 1; }
grep -q 'completed: aborted' "$H/run.log" || { echo "FAIL: the second gate's rejection did not abort"; cat "$H/run.log"; exit 1; }
grep -q '^app$' "$H/ws/gate/markers.txt" || { echo "FAIL: the step past the FIRST gate did not run"; exit 1; }
grep -q '^db$' "$H/ws/gate/markers.txt" && { echo "FAIL: the step past a REJECTED gate ran"; exit 1; }
echo "two prompts, two ids, two answers; journal:"
rg '^input-decision' "$H/build.journal" | sed 's/^/  | /'

# ---------------------------------------------------------------- scenario J
echo "=== J: an unwritable inbox fails the build, it does not ship unapproved ==="
# the prompt runs inside a PARALLEL branch and the approvals path is an ordinary
# FILE, so publishing throws. Before this fix the exception faulted the branch
# task, the waiter swallowed it, and the build finished SUCCESS having never been
# approved — a gate that opens when its own machinery breaks is not a gate.
J="$LANE/j"; mkdir -p "$J"
printf 'not a directory\n' > "$J/approvals"
cat > "$J/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage("Gate") {
            parallel {
                stage("ask") {
                    steps {
                        input message: "Deploy?", ok: "Ship it"
                    }
                }
                stage("other") {
                    steps {
                        sh "echo other >> markers.txt"
                    }
                }
            }
        }
    }
}
JF
set +e
timeout 120 "$HOST_BIN" "$J/Jenkinsfile" "$J/ws" gate "$J/build.journal" "$J/approvals" > "$J/run.log" 2>&1
RC=$?
set -e
[ "$RC" -ne 124 ] || { echo "FAIL: an unpublishable prompt HUNG"; exit 1; }
[ "$RC" -ne 0 ] || { echo "FAIL: the build SUCCEEDED without an approval"; cat "$J/run.log"; exit 1; }
grep -q 'completed: success' "$J/run.log" && { echo "FAIL: reported success with an unapproved gate"; cat "$J/run.log"; exit 1; }
grep -q 'cannot publish a prompt' "$J/run.log" || { echo "FAIL: the unwritable inbox was not named"; cat "$J/run.log"; exit 1; }
echo "failed closed, inbox named, exit $RC"

# ---------------------------------------------------------------- scenario K
echo "=== K: a marker from a DEAD attempt is swept when the build finishes ==="
# publish, kill, reconcile the interrupted step by hand, resume. The resumed run
# never re-executes that prompt, so its marker is not in this process's published
# set — completion has to sweep by what the marker SAYS it is, not by what this
# process remembers publishing.
K="$LANE/k"; mkdir -p "$K/approvals"
printf '%s\n' "$JF" > "$K/Jenkinsfile"
KMARK="$K/ws/gate/markers.txt"
"$HOST_BIN" "$K/Jenkinsfile" "$K/ws" gate "$K/build.journal" "$K/approvals" > "$K/run1.log" 2>&1 &
PID=$!
KID=$(await_pending "$K/approvals") || { echo "FAIL: no prompt published"; cat "$K/run1.log"; exit 1; }
kill -9 "$PID"; wait "$PID" 2>/dev/null || true
[ -f "$K/approvals/$KID.pending" ] || { echo "FAIL: the marker did not survive the kill"; exit 1; }
# the operator decides the gate was passed and records it, exactly as the
# restart lane's reconciliation does
printf 'step-finished\tGate\t1\tsuccess\n' >> "$K/build.journal"
timeout 120 "$HOST_BIN" "$K/Jenkinsfile" "$K/ws" gate "$K/build.journal" "$K/approvals" > "$K/run2.log" 2>&1 || {
  echo "FAIL: the reconciled resume did not complete"; cat "$K/run2.log"; exit 1; }
grep -q 'completed: success' "$K/run2.log" || { echo "FAIL: reconciled resume not successful"; cat "$K/run2.log"; exit 1; }
grep -q 'skip (durably finished): Gate#1' "$K/run2.log" || { echo "FAIL: the reconciled prompt was re-run"; exit 1; }
[ -f "$K/approvals/$KID.pending" ] && { echo "FAIL: a finished build still advertises a prompt from a dead attempt"; exit 1; }
echo "swept a marker this process never published"

# ---------------------------------------------------------------- scenario L
echo "=== L: retry does not re-ask a human who said no ==="
# a rejection is an INTERRUPTION, not a retryable failure. The retry dispatcher
# used to re-run the body, republish the prompt, and let a later approval carry
# the build to success — asking someone who already declined until they agree.
L="$LANE/l"; mkdir -p "$L/approvals"
cat > "$L/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage("Gate") {
            steps {
                retry(3) {
                    input message: "Deploy?", ok: "Ship it"
                }
                sh "echo shipped >> markers.txt"
            }
        }
    }
}
JF
timeout 180 "$HOST_BIN" "$L/Jenkinsfile" "$L/ws" gate "$L/build.journal" "$L/approvals" > "$L/run.log" 2>&1 &
PID=$!
LID=$(await_pending "$L/approvals") || { echo "FAIL: no prompt published"; cat "$L/run.log"; exit 1; }
printf 'reject laura\n' > "$L/approvals/$LID.decision"
set +e; wait "$PID"; RC=$?; set -e
[ "$RC" -ne 124 ] || { echo "FAIL: the rejected retry hung"; exit 1; }
[ "$RC" -eq 1 ] || { echo "FAIL: a rejected build must not exit 0 (got $RC)"; cat "$L/run.log"; exit 1; }
grep -q 'completed: aborted' "$L/run.log" || { echo "FAIL: rejection did not abort"; cat "$L/run.log"; exit 1; }
[ "$(grep -c '| Deploy?' "$L/run.log")" -eq 1 ] || {
  echo "FAIL: the prompt was published more than once — the human was re-asked"; cat "$L/run.log"; exit 1; }
grep -q '| Retrying' "$L/run.log" && { echo "FAIL: an abort was treated as a retryable failure"; cat "$L/run.log"; exit 1; }
[ "$(grep -c '^input-decision' "$L/build.journal")" -eq 1 ] || {
  echo "FAIL: more than one decision was recorded"; sed 's/^/  | /' "$L/build.journal"; exit 1; }
grep -q '^shipped$' "$L/ws/gate/markers.txt" 2>/dev/null && { echo "FAIL: the step after a rejected gate ran"; exit 1; }
echo "asked once, declined once, aborted"

# ---------------------------------------------------------------- scenario M
echo "=== M: a rejection escapes NESTED retries ==="
# the inner retry mints a fresh rejection ref per attempt (so one attempt cannot
# poison the next); that also severs it from the OUTER retry, which then saw an
# ordinary failed attempt and put the prompt back in front of someone who had
# already declined it
M="$LANE/m"; mkdir -p "$M/approvals"
cat > "$M/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage("Gate") {
            steps {
                retry(3) {
                    retry(2) {
                        input message: "Deploy?", ok: "Ship it"
                    }
                }
                sh "echo shipped >> markers.txt"
            }
        }
    }
}
JF
timeout 180 "$HOST_BIN" "$M/Jenkinsfile" "$M/ws" gate "$M/build.journal" "$M/approvals" > "$M/run.log" 2>&1 &
PID=$!
MID=$(await_pending "$M/approvals") || { echo "FAIL: no prompt published"; cat "$M/run.log"; exit 1; }
printf 'reject mallory\n' > "$M/approvals/$MID.decision"
set +e; wait "$PID"; RC=$?; set -e
[ "$RC" -ne 124 ] || { echo "FAIL: the nested rejected retry hung"; exit 1; }
[ "$RC" -eq 1 ] || { echo "FAIL: a rejected build must not exit 0 (got $RC)"; cat "$M/run.log"; exit 1; }
grep -q 'completed: aborted' "$M/run.log" || { echo "FAIL: rejection did not abort"; cat "$M/run.log"; exit 1; }
[ "$(grep -c '| Deploy?' "$M/run.log")" -eq 1 ] || {
  echo "FAIL: the prompt was republished — the rejection did not escape the inner retry"; cat "$M/run.log"; exit 1; }
grep -q '| Retrying' "$M/run.log" && { echo "FAIL: a rejection was retried at some level"; cat "$M/run.log"; exit 1; }
[ "$(grep -c '^input-decision' "$M/build.journal")" -eq 1 ] || {
  echo "FAIL: more than one decision was recorded"; sed 's/^/  | /' "$M/build.journal"; exit 1; }
grep -q '^shipped$' "$M/ws/gate/markers.txt" 2>/dev/null && { echo "FAIL: the step after a rejected gate ran"; exit 1; }
echo "declined once at depth two, aborted, never re-asked"

# ---------------------------------------------------------------- scenario N
echo "=== N: a timed-out prompt is withdrawn before the retry publishes ==="
# retry starts the next occurrence the instant the timeout aborts the last one.
# If the dead marker lingers, the inbox advertises two prompts and the operator
# will reach for the one that appeared first — whose answer nothing reads.
N="$LANE/n"; mkdir -p "$N/approvals"
cat > "$N/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage("Gate") {
            steps {
                retry(2) {
                    timeout(time: 4, unit: 'SECONDS') {
                        input message: "Deploy?", ok: "Ship it"
                    }
                }
            }
        }
    }
}
JF
timeout 120 "$HOST_BIN" "$N/Jenkinsfile" "$N/ws" gate "$N/build.journal" "$N/approvals" > "$N/run.log" 2>&1 &
PID=$!
N1=$(await_pending "$N/approvals") || { echo "FAIL: no prompt published"; cat "$N/run.log"; exit 1; }
# sample the inbox across the whole run: it must NEVER show two prompts at once
MAXSEEN=0
for _ in $(seq 1 100); do
  C=$(find "$N/approvals" -maxdepth 1 -name '*.pending' | wc -l)
  [ "$C" -gt "$MAXSEEN" ] && MAXSEEN=$C
  kill -0 "$PID" 2>/dev/null || break
  sleep 0.2
done
set +e; wait "$PID"; RC=$?; set -e
[ "$MAXSEEN" -le 1 ] || { echo "FAIL: the inbox advertised $MAXSEEN prompts at once — a dead one was still listed"; exit 1; }
grep -q 'completed: aborted' "$N/run.log" || { echo "FAIL: the exhausted retry did not abort"; cat "$N/run.log"; exit 1; }
# both occurrences ran (the timeout is retried — receipt retry-timeout-retries)
[ "$(grep -c '| Deploy?' "$N/run.log")" -eq 2 ] || {
  echo "FAIL: expected two prompts across two attempts"; cat "$N/run.log"; exit 1; }
[ "$(find "$N/approvals" -maxdepth 1 -name '*.pending' | wc -l)" -eq 0 ] || {
  echo "FAIL: a prompt is still advertised against a finished build"; exit 1; }
echo "never more than one live prompt; both attempts asked, neither lingered"

LANE_OK=1
echo "APPROVAL LANE: ALL ASSERTIONS PASSED"
