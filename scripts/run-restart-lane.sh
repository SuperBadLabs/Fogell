#!/usr/bin/env bash
# FG-112. The restart lane: a REAL Jenkinsfile through the REAL walker with the
# FG-025 journal, killed with a genuine SIGKILL and resumed. Proves, in order:
#  1. a mid-step SIGKILL leaves the step Interrupted, and the resume REFUSES by
#     name (exit 3) — the engine does not guess whether the effect landed;
#  2. operator reconciliation is a text append (the journal is cat-able by
#     design): the marker shows the effect landed, so the operator records
#     step-finished, and the resume then skips every durable step and runs the
#     rest — each effect exactly once;
#  3. a rerun over a terminal journal is a no-op ("already-terminal").
# Everything is asserted; the transcript is the lane's evidence.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

LANE=$(mktemp -d /tmp/fogell-restart-lane.XXXXXX)
# evidence survives failure: the transcript IS the lane's proof
trap '[ "${LANE_OK:-0}" = 1 ] && rm -rf "$LANE" || echo "lane FAILED — evidence kept at $LANE" >&2' EXIT
JOURNAL="$LANE/build.journal"
WSROOT="$LANE/ws"
JOB="restart-lane"
MARKERS="$WSROOT/$JOB/markers.txt"

cat > "$LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Build') {
            steps {
                sh 'echo one >> markers.txt'
                sh 'echo two >> markers.txt && sleep 15'
                sh 'echo three >> markers.txt'
            }
        }
        stage('Recovery') {
            when { isRestartedRun() }
            steps {
                sh 'echo recovered >> markers.txt'
            }
        }
    }
}
JF

dotnet build -c Release --nologo >/dev/null
# the BUILT apphost, invoked directly: `dotnet run` interposes a driver
# process, and SIGKILLing the driver leaves the actual walker alive —
# every assertion could then pass without the walker ever dying
HOST_BIN=$(find tools/Fogell.Run.Host/bin/Release -name Fogell.Run.Host -type f | head -1)
[ -x "$HOST_BIN" ] || { echo "FAIL: host binary not found"; exit 1; }
HOST=("$HOST_BIN")

echo "=== attempt 1: SIGKILL mid-step ==="
"${HOST[@]}" "$LANE/Jenkinsfile" "$WSROOT" "$JOB" "$JOURNAL" > "$LANE/run1.log" 2>&1 &
PID=$!
# wait until step 2's effect is visible, then kill DURING its sleep
for _ in $(seq 1 120); do
  [ -f "$MARKERS" ] && grep -q '^two$' "$MARKERS" && break
  sleep 0.25
done
grep -q '^two$' "$MARKERS" || { echo "FAIL: step 2 never started"; exit 1; }
kill -9 "$PID"
wait "$PID" 2>/dev/null || true
# the WALKER must actually be dead: no lingering host process (anchored to
# the binary path — an unanchored two-substring pattern once false-matched a
# transient unrelated cmdline), and the first run's log must never complete
# REGEX-QUOTED (FG-046b review): the binary name contains dots and `pgrep -f`
# takes a regex, so an unescaped `.` matches any character — the anchored,
# strict-looking check was quietly a loose one. `kill -0` joins it because the
# two answer different questions: this process is gone, and no OTHER host
# survived (the interposed-driver case this anchoring exists for).
HOST_RE="^$(printf '%s' "$HOST_BIN" | sed 's/[.[\*^$()+?{}|\\]/\\&/g') .*$(printf '%s' "$LANE" | sed 's/[.[\*^$()+?{}|\\]/\\&/g')"
kill -0 "$PID" 2>/dev/null && { echo "FAIL: the killed host is still alive"; exit 1; }
for _ in 1 2 3 4; do pgrep -f "$HOST_RE" >/dev/null || break; sleep 0.5; done
pgrep -f "$HOST_RE" >/dev/null && { echo "FAIL: a host process survived the SIGKILL"; pgrep -af "$HOST_RE"; exit 1; }
grep -q '^completed:' "$LANE/run1.log" && { echo "FAIL: run 1 completed despite the kill"; exit 1; }
echo "killed host mid-step (verified dead); journal now:"
sed 's/^/  | /' "$JOURNAL"

echo "=== attempt 2: resume must REFUSE (interrupted step, exit 3) ==="
set +e
"${HOST[@]}" "$LANE/Jenkinsfile" "$WSROOT" "$JOB" "$JOURNAL" > "$LANE/run2.log" 2>&1
RC=$?
set -e
[ "$RC" -eq 3 ] || { echo "FAIL: expected refusal exit 3, got $RC"; cat "$LANE/run2.log"; exit 1; }
grep -q 'needs-reconciliation: Build#1' "$LANE/run2.log" || { echo "FAIL: refusal did not name Build#1"; exit 1; }
echo "refused, step named: $(grep -o 'needs-reconciliation.*' "$LANE/run2.log")"

echo "=== attempt 2b: a CHANGED definition must refuse (exit 4) ==="
cp "$LANE/Jenkinsfile" "$LANE/Jenkinsfile.orig"
printf '\n// drifted\n' >> "$LANE/Jenkinsfile"
set +e
"${HOST[@]}" "$LANE/Jenkinsfile" "$WSROOT" "$JOB" "$JOURNAL" > "$LANE/run2b.log" 2>&1
RC=$?
set -e
[ "$RC" -eq 4 ] || { echo "FAIL: expected definition-changed exit 4, got $RC"; cat "$LANE/run2b.log"; exit 1; }
grep -q 'definition-changed' "$LANE/run2b.log" || { echo "FAIL: refusal not named"; exit 1; }
mv "$LANE/Jenkinsfile.orig" "$LANE/Jenkinsfile"
echo "refused the drifted definition; original restored"

echo "=== operator reconciliation: the marker shows the effect landed ==="
grep -q '^two$' "$MARKERS"
printf 'step-finished\tBuild\t1\tsuccess\n' >> "$JOURNAL"
echo "appended step-finished for Build#1 (evidence: marker line 'two' exists)"

echo "=== attempt 3: clean resume — durable steps skip, the rest run ==="
"${HOST[@]}" "$LANE/Jenkinsfile" "$WSROOT" "$JOB" "$JOURNAL" > "$LANE/run3.log" 2>&1
grep -q 'resuming: one recovery event' "$LANE/run3.log" || { echo "FAIL: no recovery event"; exit 1; }
grep -q 'skip (durably finished): Build#0' "$LANE/run3.log" || { echo "FAIL: step 0 not skipped"; exit 1; }
grep -q 'skip (durably finished): Build#1' "$LANE/run3.log" || { echo "FAIL: step 1 not skipped"; exit 1; }
grep -q 'completed: success' "$LANE/run3.log" || { echo "FAIL: resume did not complete"; cat "$LANE/run3.log"; exit 1; }

echo "=== isRestartedRun: the Recovery stage ran on the RESUMED attempt only ==="
grep -q '^recovered$' "$MARKERS" || { echo "FAIL: recovery stage never ran on resume"; exit 1; }

echo "=== integrity: each effect exactly once ==="
sort "$MARKERS" | uniq -c | sed 's/^/  /'
for m in one two three recovered; do
  [ "$(grep -c "^$m\$" "$MARKERS")" -eq 1 ] || { echo "FAIL: marker '$m' not exactly-once"; exit 1; }
done

echo "=== attempt 4: terminal journal is a no-op ==="
"${HOST[@]}" "$LANE/Jenkinsfile" "$WSROOT" "$JOB" "$JOURNAL" > "$LANE/run4.log" 2>&1
grep -q 'already-terminal: success' "$LANE/run4.log" || { echo "FAIL: not already-terminal"; exit 1; }

echo "=== FG-171: a SIGKILL between two script{} children — the BLOCK is the unit ==="
# The FG-171 row claimed a crash inside script{} RE-RUNS the whole block on
# resume, duplicating effects under a clean report — class A. MEASURED HERE:
# it does not. The block is ONE durability unit (step-started, no finish), the
# resume REFUSES by name exactly as it does for any interrupted step, and no
# child effect ever duplicates. What the unit-granularity DOES cost is stated
# and asserted below: operator reconciliation attests the WHOLE block, so a
# child the crash never reached does not run on resume — the wrapper limit
# Resume.fs documents, applying to script{} too. Fine-grained resume inside a
# block shares the journal redesign FG-135 carries.
S_LANE="$LANE/fg171"
mkdir -p "$S_LANE/ws"
S_JOURNAL="$S_LANE/build.journal"
S_MARKERS="$S_LANE/ws/fg171/markers.txt"

cat > "$S_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Build') {
            steps {
                script {
                    sh 'echo s1 >> markers.txt'
                    sh 'echo s2 >> markers.txt && sleep 15'
                    sh 'echo s3 >> markers.txt'
                }
            }
        }
    }
}
JF

"${HOST[@]}" "$S_LANE/Jenkinsfile" "$S_LANE/ws" fg171 "$S_JOURNAL" > "$S_LANE/run1.log" 2>&1 &
S_PID=$!
for _ in $(seq 1 120); do
  [ -f "$S_MARKERS" ] && grep -q '^s2$' "$S_MARKERS" && break
  sleep 0.25
done
grep -q '^s2$' "$S_MARKERS" || { echo "FAIL: script child s2 never started"; exit 1; }
kill -9 "$S_PID"
wait "$S_PID" 2>/dev/null || true
# the same anchored liveness sweep as scenario 1 — after `wait` reaps, `kill -0`
# is a tautology, and an interposed driver would let the walker survive to write
# s3 after the assertions run (verifier's construction; not exploitable while
# HOST_BIN is the direct apphost, but one paste buys the insurance)
S_HOST_RE="^$(printf '%s' "$HOST_BIN" | sed 's/[.[\*^$()+?{}|\\]/\\&/g') .*$(printf '%s' "$S_LANE" | sed 's/[.[\*^$()+?{}|\\]/\\&/g')"
for _ in 1 2 3 4; do pgrep -f "$S_HOST_RE" >/dev/null || break; sleep 0.5; done
pgrep -f "$S_HOST_RE" >/dev/null && { echo "FAIL: a host process survived the FG-171 SIGKILL"; pgrep -af "$S_HOST_RE"; exit 1; }
grep -q '^completed:' "$S_LANE/run1.log" && { echo "FAIL: run 1 completed despite the kill"; exit 1; }

# the whole block is one journal unit: exactly one step-started, no finish
[ "$(grep -c $'^step-started\tBuild\t0\tscript$' "$S_JOURNAL")" -eq 1 ] \
  || { echo "FAIL: expected exactly one step-started for the script block"; sed 's/^/  | /' "$S_JOURNAL"; exit 1; }
grep -q $'^step-finished\tBuild\t0' "$S_JOURNAL" && { echo "FAIL: the interrupted block reads finished"; exit 1; }

set +e
"${HOST[@]}" "$S_LANE/Jenkinsfile" "$S_LANE/ws" fg171 "$S_JOURNAL" > "$S_LANE/run2.log" 2>&1
S_RC=$?
set -e
[ "$S_RC" -eq 3 ] || { echo "FAIL: expected refusal exit 3, got $S_RC"; cat "$S_LANE/run2.log"; exit 1; }
grep -q 'needs-reconciliation: Build#0' "$S_LANE/run2.log" || { echo "FAIL: refusal did not name the block"; exit 1; }
for m in s1 s2; do
  [ "$(grep -c "^$m\$" "$S_MARKERS")" -eq 1 ] || { echo "FAIL: '$m' not exactly-once after refused resume"; exit 1; }
done
echo "refused by name; s1/s2 exactly-once — the claimed double-run does NOT happen"

printf 'step-finished\tBuild\t0\tsuccess\n' >> "$S_JOURNAL"
"${HOST[@]}" "$S_LANE/Jenkinsfile" "$S_LANE/ws" fg171 "$S_JOURNAL" > "$S_LANE/run3.log" 2>&1
grep -q 'skip (durably finished): Build#0' "$S_LANE/run3.log" || { echo "FAIL: reconciled block not skipped"; exit 1; }
grep -q 'completed: success' "$S_LANE/run3.log" || { echo "FAIL: reconciled resume did not complete"; exit 1; }
# THE STATED LIMIT, asserted so it cannot drift silently: attestation covers the
# WHOLE block, so the child the crash never reached does not run on resume
grep -q '^s3$' "$S_MARKERS" && { echo "FAIL: s3 ran — block-unit attestation semantics changed; update this lane AND the FG-171 row together"; exit 1; }
for m in s1 s2; do
  [ "$(grep -c "^$m\$" "$S_MARKERS")" -eq 1 ] || { echo "FAIL: '$m' not exactly-once after reconciled resume"; exit 1; }
done
echo "reconciled resume: block skipped WHOLE (s3 deliberately absent — the stated limit), every effect exactly once"

LANE_OK=1
echo "RESTART LANE: ALL ASSERTIONS PASSED"
