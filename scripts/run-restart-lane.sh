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

echo "=== FG-135: a SIGKILL mid-attempt of a RETRIED stage — attempts have identity ==="
# The defect this proves fixed: with records keyed (stage, index) only, attempt
# 1's `step-finished failure` read as durably finished, so a resumed build
# printed Retrying for each remaining attempt while NEVER re-running the shell,
# and completed failure. The retry-attempt marker supersedes a failed attempt's
# records, the resume refuses on the LIVE attempt's interrupted step, and after
# reconciliation the loop CONTINUES from the journaled attempt — each attempt's
# step running exactly once across the crash. `input` inside a journaled
# retried stage stays refused (approval identity has no attempt dimension yet).
R_LANE="$LANE/fg135"
mkdir -p "$R_LANE/ws"
R_JOURNAL="$R_LANE/build.journal"
R_TRIES="$R_LANE/ws/fg135/tries.txt"

cat > "$R_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Flaky') {
            options { retry(3) }
            steps {
                sh 'echo try >> tries.txt; n=$(wc -l < tries.txt); if [ "$n" -lt 3 ]; then if [ "$n" -eq 2 ]; then sleep 15; fi; exit 1; fi'
            }
        }
    }
}
JF

"${HOST[@]}" "$R_LANE/Jenkinsfile" "$R_LANE/ws" fg135 "$R_JOURNAL" > "$R_LANE/run1.log" 2>&1 &
R_PID=$!
# attempt 1 fails fast; attempt 2 writes its try THEN sleeps — kill in the sleep
for _ in $(seq 1 120); do
  [ -f "$R_TRIES" ] && [ "$(wc -l < "$R_TRIES")" -ge 2 ] && break
  sleep 0.25
done
[ "$(wc -l < "$R_TRIES")" -ge 2 ] || { echo "FAIL: attempt 2 never started"; exit 1; }
kill -9 "$R_PID"
wait "$R_PID" 2>/dev/null || true
R_HOST_RE="^$(printf '%s' "$HOST_BIN" | sed 's/[.[\*^$()+?{}|\\]/\\&/g') .*$(printf '%s' "$R_LANE" | sed 's/[.[\*^$()+?{}|\\]/\\&/g')"
for _ in 1 2 3 4; do pgrep -f "$R_HOST_RE" >/dev/null || break; sleep 0.5; done
pgrep -f "$R_HOST_RE" >/dev/null && { echo "FAIL: a host survived the FG-135 SIGKILL"; pgrep -af "$R_HOST_RE"; exit 1; }
grep -q '^completed:' "$R_LANE/run1.log" && { echo "FAIL: run 1 completed despite the kill"; exit 1; }

# the journal must show: attempt 1 finished-failure, the attempt-2 marker, and
# attempt 2 started — the marker BETWEEN them is what gives attempts identity
grep -q $'^retry-attempt\tFlaky\t2$' "$R_JOURNAL" || { echo "FAIL: no attempt-2 marker"; sed 's/^/  | /' "$R_JOURNAL"; exit 1; }

set +e
"${HOST[@]}" "$R_LANE/Jenkinsfile" "$R_LANE/ws" fg135 "$R_JOURNAL" > "$R_LANE/run2.log" 2>&1
R_RC=$?
set -e
# THE DEFECT'S OWN ASSERTION: pre-fix this resume SKIPPED Flaky#0 as durably
# finished (attempt 1's failure) and completed failure without re-running
# anything. With attempt identity it refuses on the LIVE attempt's interrupted
# step instead.
[ "$R_RC" -eq 3 ] || { echo "FAIL: expected refusal exit 3, got $R_RC"; cat "$R_LANE/run2.log"; exit 1; }
grep -q 'needs-reconciliation: Flaky#0' "$R_LANE/run2.log" || { echo "FAIL: refusal did not name the live attempt's step"; exit 1; }
[ "$(wc -l < "$R_TRIES")" -eq 2 ] || { echo "FAIL: the refused resume ran something"; exit 1; }
echo "refused on the LIVE attempt's step — the superseded failure no longer reads as finished"

# operator: the try count shows attempt 2's step ran and its shell then died — a failure
printf 'step-finished\tFlaky\t0\tfailure\n' >> "$R_JOURNAL"
"${HOST[@]}" "$R_LANE/Jenkinsfile" "$R_LANE/ws" fg135 "$R_JOURNAL" > "$R_LANE/run3.log" 2>&1
grep -q 'skip (durably finished): Flaky#0' "$R_LANE/run3.log" || { echo "FAIL: reconciled attempt-2 not replayed"; exit 1; }
grep -q 'completed: success' "$R_LANE/run3.log" || { echo "FAIL: resume did not complete"; cat "$R_LANE/run3.log"; exit 1; }
[ "$(wc -l < "$R_TRIES")" -eq 3 ] || { echo "FAIL: attempt 3 did not run exactly once (tries=$(wc -l < "$R_TRIES"))"; exit 1; }
grep -q $'^retry-attempt\tFlaky\t3$' "$R_JOURNAL" || { echo "FAIL: the live attempt 3 journaled no marker"; exit 1; }
[ "$(grep -c $'^retry-attempt\tFlaky\t2$' "$R_JOURNAL")" -eq 1 ] || { echo "FAIL: attempt-2 marker not exactly once"; exit 1; }
echo "reconciled resume CONTINUED the loop: attempt 3 ran once, build success, every attempt's step exactly once"

echo "=== FG-135: input inside a journaled retried stage stays REFUSED ==="
I_LANE="$LANE/fg135-input"
mkdir -p "$I_LANE/ws"
cat > "$I_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Gate') {
            options { retry(2) }
            steps {
                timeout(time: 5, unit: 'SECONDS') { input message: 'go?' }
            }
        }
    }
}
JF
set +e
"${HOST[@]}" "$I_LANE/Jenkinsfile" "$I_LANE/ws" fg135i "$I_LANE/build.journal" > "$I_LANE/run.log" 2>&1
I_RC=$?
set -e
[ "$I_RC" -eq 1 ] || { echo "FAIL: expected failure exit 1, got $I_RC"; exit 1; }
grep -q 'completed: failure' "$I_LANE/run.log" || { echo "FAIL: not a failed build"; exit 1; }
# the journal proves WHICH path failed it: the refusal fires before any step, so
# a step-started here means the timeout path ran instead. FG-114's step-reason
# does NOT cover this refusal — it journals beside a hooked step's finish, and
# this fires before any step exists — so record ABSENCE is the discriminating
# evidence, and the reason text stays non-durable here (a true FG-114 residual)
grep -q $'^step-started' "$I_LANE/build.journal" && { echo "FAIL: the gate step RAN — the approval refusal did not fire"; exit 1; }
echo "refused before any step — a prior attempt's approval can never be replayed"

echo "=== FG-135: NESTED stages under a journaled retry stay REFUSED (leaf-only) ==="
N_LANE="$LANE/fg135-nested"
mkdir -p "$N_LANE/ws"
cat > "$N_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Parent') {
            options { retry(2) }
            stages {
                stage('Child') {
                    steps { sh 'echo ran >> nested.txt' }
                }
            }
        }
    }
}
JF
set +e
"${HOST[@]}" "$N_LANE/Jenkinsfile" "$N_LANE/ws" fg135n "$N_LANE/build.journal" > "$N_LANE/run.log" 2>&1
N_RC=$?
set -e
[ "$N_RC" -eq 1 ] || { echo "FAIL: expected failure exit 1, got $N_RC"; cat "$N_LANE/run.log"; exit 1; }
grep -q 'completed: failure' "$N_LANE/run.log" || { echo "FAIL: not a failed build"; exit 1; }
# the refusal fires before any step: nested steps journal under the CHILD's name,
# which the parent's marker cannot supersede, so running them would rebuild the
# exact defect one level down
grep -q $'^step-started' "$N_LANE/build.journal" && { echo "FAIL: a nested step RAN under a journaled retry"; exit 1; }
[ -f "$N_LANE/ws/fg135n/nested.txt" ] && { echo "FAIL: the nested step's effect landed"; exit 1; }
echo "refused before any step — nested records cannot masquerade as the live attempt's"

echo "=== FG-206: an ABSORBED failure's reason does not explain a later refusal ==="
# A script-level catch absorbs a captured failure but nothing cleared the
# shared reason ref, so a later child failing through a NON-capturing site
# (here a dir traversal refusal) journaled the absorbed diagnostic as the
# WHY of a disposition it does not explain — `script returned exit code 3`
# durable, the real cause nowhere. Found by the fleet session's review of
# PR #97; the per-hosted-child freshening is the third site of the FG-114
# invariant. Honest ABSENCE is the fixed behaviour: the terminal site does
# not capture, so no reason may journal.
S_LANE="$LANE/fg206-stale"
mkdir -p "$S_LANE/ws"
cat > "$S_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    try { sh "exit 3" } catch (e) { echo "absorbed" }
                    dir("../outside") { sh "echo x" }
                }
            }
        }
    }
}
JF
set +e
"${HOST[@]}" "$S_LANE/Jenkinsfile" "$S_LANE/ws" fg206 "$S_LANE/build.journal" > "$S_LANE/run.log" 2>&1
set -e
grep -q 'completed: failure' "$S_LANE/run.log" || { echo "FAIL: the refused build did not fail"; cat "$S_LANE/run.log"; exit 1; }
grep -q "dir refused" "$S_LANE/run.log" || { echo "FAIL: the terminal cause is not the dir refusal"; cat "$S_LANE/run.log"; exit 1; }
grep -q $'^step-reason' "$S_LANE/build.journal" && {
  echo "FAIL: an absorbed failure's reason journaled as the explanation of a later refusal"
  cat "$S_LANE/build.journal"; exit 1; }
grep -q $'^step-finished\tGate\t0\tfailure' "$S_LANE/build.journal" || { echo "FAIL: the failure itself is not journaled"; exit 1; }

# CONTROL: an UNCAUGHT captured failure still journals its own reason — the
# freshening must not suppress the legitimate explanation.
C_LANE="$LANE/fg206-control"
mkdir -p "$C_LANE/ws"
cat > "$C_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    sh "exit 3"
                }
            }
        }
    }
}
JF
set +e
"${HOST[@]}" "$C_LANE/Jenkinsfile" "$C_LANE/ws" fg206c "$C_LANE/build.journal" > "$C_LANE/run.log" 2>&1
set -e
grep -q $'^step-reason\tGate\t0\tscript returned exit code 3' "$C_LANE/build.journal" || {
  echo "FAIL: the uncaught failure's own reason did not journal — the freshening over-suppresses"
  cat "$C_LANE/build.journal"; exit 1; }
echo "an absorbed reason cannot explain a later refusal, and an uncaught one still explains itself"

# THIRD DIRECTION, from the verifier's refutation of the first fix: after a
# refusal HALTS the branch, later calls still enter the host to be skipped —
# and the unguarded freshening wiped the reason the refusal had just written.
# A refused call WITH A SUCCESSOR must still journal its own reason.
H_LANE="$LANE/fg206-halted"
mkdir -p "$H_LANE/ws"
cat > "$H_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    def unreachableArg = {
                        sh 'printf arg > arg.txt'
                        return 'ignored'
                    }
                    sh('invalid', 'extra')
                    sh(script: MISSING)
                    sh(script: unreachableArg())
                    sh(script: 'printf warned > warned.txt', fogellProbeUnknown: true)
                    echo(message: 'must-not-print', fogellProbeUnknown: true)
                    sh 'printf effect > effect.txt'
                }
            }
        }
    }
}
JF
set +e
"${HOST[@]}" "$H_LANE/Jenkinsfile" "$H_LANE/ws" fg206h "$H_LANE/build.journal" > "$H_LANE/run.log" 2>&1
set -e
grep -q 'completed: failure' "$H_LANE/run.log" || { echo "FAIL: the refused build did not fail"; cat "$H_LANE/run.log"; exit 1; }
grep -q $'^step-reason\tGate\t0\t' "$H_LANE/build.journal" || {
  echo "FAIL: a refusal with a successor call lost its durable reason — the freshening fired on a halted entry"
  cat "$H_LANE/build.journal"; exit 1; }
grep -q "positional argument" "$H_LANE/build.journal" || {
  echo "FAIL: the journaled reason is not the refusal's own"; cat "$H_LANE/build.journal"; exit 1; }
grep -q 'WARNING: Unknown parameter' "$H_LANE/run.log" && {
  echo "FAIL: an unreachable warning-class call emitted after halt"; cat "$H_LANE/run.log"; exit 1; }
grep -q 'must-not-print' "$H_LANE/run.log" && {
  echo "FAIL: an unreachable constructor-map call emitted after halt"; cat "$H_LANE/run.log"; exit 1; }
grep -q 'StepBindingFailed\|fogellProbeUnknown' "$H_LANE/build.journal" && {
  echo "FAIL: an unreachable binding fault replaced the original refusal"; cat "$H_LANE/build.journal"; exit 1; }
grep -q 'UnknownProperty\|MISSING' "$H_LANE/build.journal" && {
  echo "FAIL: an unreachable argument fault replaced the original refusal"; cat "$H_LANE/build.journal"; exit 1; }
[ -f "$H_LANE/ws/fg206h/arg.txt" ] && { echo "FAIL: unreachable argument side effect landed"; exit 1; }
[ -f "$H_LANE/ws/fg206h/warned.txt" ] && { echo "FAIL: unreachable warning-class effect landed"; exit 1; }
[ -f "$H_LANE/ws/fg206h/effect.txt" ] && { echo "FAIL: unreachable plain effect landed"; exit 1; }
echo "post-halt warning and constructor calls stay silent, effectless, and cannot replace the original reason"

echo "=== FG-177: binding failures preserve their measured exception class ==="
B_LANE="$LANE/fg177-binding-class"
mkdir -p "$B_LANE/ws"
cat > "$B_LANE/Jenkinsfile" <<'JF'
pipeline {
    agent any
    stages {
        stage('schema') {
            steps {
                script {
                    try {
                        junit()
                    } catch (NullPointerException expected) {
                        echo "junit-bound:${expected}"
                        sh 'printf junit > junit-caught.txt'
                    }
                    try {
                        dir() { sh 'printf wrong > wrong.txt' }
                    } catch (NullPointerException expected) {
                        echo "dir-bound:${expected}"
                        sh 'printf dir > dir-caught.txt'
                    }
                    try {
                        try {
                            junit()
                        } catch (IllegalArgumentException wrong) {
                            sh 'printf wrong > wrong-junit-class.txt'
                        }
                    } catch (NullPointerException expected) {
                        sh 'printf junit-class > junit-class.txt'
                    }
                    try {
                        try {
                            dir() { sh 'printf wrong > wrong.txt' }
                        } catch (IllegalArgumentException wrong) {
                            sh 'printf wrong > wrong-dir-class.txt'
                        }
                    } catch (NullPointerException expected) {
                        sh 'printf dir-class > dir-class.txt'
                    }
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
JF
"${HOST[@]}" "$B_LANE/Jenkinsfile" "$B_LANE/ws" fg177class "$B_LANE/build.journal" > "$B_LANE/run.log" 2>&1
grep -q 'completed: success' "$B_LANE/run.log" || {
  echo "FAIL: narrow NullPointerException catches did not recover"; cat "$B_LANE/run.log"; exit 1; }
grep -q 'junit-bound:java.lang.NullPointerException:' "$B_LANE/run.log" || {
  echo "FAIL: junit catch variable did not carry the measured exception class"; cat "$B_LANE/run.log"; exit 1; }
grep -q 'dir-bound:java.lang.NullPointerException:' "$B_LANE/run.log" || {
  echo "FAIL: dir catch variable did not carry the measured exception class"; cat "$B_LANE/run.log"; exit 1; }
for f in junit-caught.txt dir-caught.txt junit-class.txt dir-class.txt continued.txt; do
  [ -f "$B_LANE/ws/fg177class/$f" ] || { echo "FAIL: $f is absent after its narrow catch"; exit 1; }
done
[ -f "$B_LANE/ws/fg177class/wrong.txt" ] && { echo "FAIL: missing dir path ran its body"; exit 1; }
[ -f "$B_LANE/ws/fg177class/wrong-junit-class.txt" ] && { echo "FAIL: IllegalArgumentException caught junit's NullPointerException"; exit 1; }
[ -f "$B_LANE/ws/fg177class/wrong-dir-class.txt" ] && { echo "FAIL: IllegalArgumentException caught dir's NullPointerException"; exit 1; }
echo "junit and dir bind failures are caught by their measured NullPointerException class"

LANE_OK=1
echo "RESTART LANE: ALL ASSERTIONS PASSED"
