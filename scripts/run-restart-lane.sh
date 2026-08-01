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
trap 'rm -rf "$LANE"' EXIT
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
    }
}
JF

dotnet build -c Release --nologo >/dev/null
HOST=(dotnet run --project tools/Fogell.Run.Host -c Release --no-build --)

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
echo "killed host mid-step; journal now:"
sed 's/^/  | /' "$JOURNAL"

echo "=== attempt 2: resume must REFUSE (interrupted step, exit 3) ==="
set +e
"${HOST[@]}" "$LANE/Jenkinsfile" "$WSROOT" "$JOB" "$JOURNAL" > "$LANE/run2.log" 2>&1
RC=$?
set -e
[ "$RC" -eq 3 ] || { echo "FAIL: expected refusal exit 3, got $RC"; cat "$LANE/run2.log"; exit 1; }
grep -q 'needs-reconciliation: Build#1' "$LANE/run2.log" || { echo "FAIL: refusal did not name Build#1"; exit 1; }
echo "refused, step named: $(rg -o 'needs-reconciliation.*' "$LANE/run2.log")"

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

echo "=== integrity: each effect exactly once ==="
sort "$MARKERS" | uniq -c | sed 's/^/  /'
for m in one two three; do
  [ "$(grep -c "^$m\$" "$MARKERS")" -eq 1 ] || { echo "FAIL: marker '$m' not exactly-once"; exit 1; }
done

echo "=== attempt 4: terminal journal is a no-op ==="
"${HOST[@]}" "$LANE/Jenkinsfile" "$WSROOT" "$JOB" "$JOURNAL" > "$LANE/run4.log" 2>&1
grep -q 'already-terminal: success' "$LANE/run4.log" || { echo "FAIL: not already-terminal"; exit 1; }

echo "RESTART LANE: ALL ASSERTIONS PASSED"
