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
for _ in 1 2 3 4; do pgrep -f "^${HOST_BIN} " >/dev/null || break; sleep 0.5; done
pgrep -f "^${HOST_BIN} " >/dev/null && { echo "FAIL: host survived the SIGKILL"; pgrep -af "^${HOST_BIN} "; exit 1; }
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

LANE_OK=1
echo "RESTART LANE: ALL ASSERTIONS PASSED"
