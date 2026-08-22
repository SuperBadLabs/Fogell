#!/usr/bin/env bash
# FG-152. A SECTION FOGELL ACTS ON, WHEN IT DOES NOT PARSE, IS REFUSED — NEVER
# CONSUMED OPAQUELY AND DROPPED.
#
# This exists because the same fix landed on one of two sibling functions three
# times running. FG-143 taught the STAGE fallback to refuse `steps`. FG-150 taught
# the TOP-LEVEL fallback to refuse `options`/`stages`. Neither carried `options`
# back to the stage fallback, so a stage's
# `options { timeout(...); bogus("x) y) }` was still swallowed whole and its
# TIMEOUT SILENTLY DROPPED — measured `completed: success` against a control that
# aborts. A dropped timeout is a build that runs past a bound Jenkins enforces.
#
# THE FIXTURES MOVED 2026-08-18: every bad arm used a SLASHY as its unparseable
# content, and FG-141 made slashy parse — two arms then "failed" by succeeding
# at the thing they existed to be refused for (top-level options accept-and-
# ignore an unknown name once it parses; a slashy input gate simply gates). An
# UNTERMINATED double-quoted literal is the replacement: no grammar change can
# close a quote the author never closed, so the refusal class stays testable.
#
# COVERAGE, enumerated: stage `options`, top-level `options`, stage `steps`, and
# `environment` at BOTH levels. The banner this file prints used to say "every
# acted-on section refuses" while it exercised three cases — and it passed green while
# a malformed `environment` block was dropped at both levels and the shell ran with the
# variable UNSET. THE SCRIPT WRITTEN TO STOP ME OVERCLAIMING CARRIED THE OVERCLAIM IN
# ITS OWN BANNER. The refusal set is now one shared `actedOnSections` in the parser, so
# what this script asserts and what the code enforces cannot drift apart silently.
#
# Every case here is PAIRED WITH A CONTROL that must still run. A parser that
# refused everything would satisfy the refusal half of this file and fail the
# controls — the lesson from approval-lane scenario Z4, where four mutation-proven
# refusal scenarios would all have passed an engine that refused every pipeline.
set -euo pipefail

HOST_BIN=${HOST_BIN:-$(find tools/Fogell.Run.Host/bin/Release -name Fogell.Run.Host -type f | head -1)}
[ -x "$HOST_BIN" ] || { echo "prove-section-refusals: no host binary"; exit 1; }

LAB=$(mktemp -d /tmp/fogell-section-refusals.XXXXXX)
trap 'rm -rf "$LAB"' EXIT
FAILED=0

# run <name> <jenkinsfile-text>  -> echoes the terminal line
run_case() {
  local name=$1 body=$2
  mkdir -p "$LAB/$name"
  printf '%s\n' "$body" > "$LAB/$name/Jenkinsfile"
  timeout 60 "$HOST_BIN" "$LAB/$name/Jenkinsfile" "$LAB/$name/ws" run \
    "$LAB/$name/build.journal" > "$LAB/$name/run.log" 2>&1 || true
  cat "$LAB/$name/run.log"
}

# A CONTROL must complete the way the directive says — not merely "not refuse".
expect_control() {
  local name=$1 want=$2 log
  log=$(run_case "$name" "$3")
  if grep -q "$want" <<<"$log"; then
    echo "  control $name: $want"
  else
    echo "  FAIL: control $name did not $want — the engine over-refuses"
    sed 's/^/    | /' <<<"$log" | head -5
    FAILED=1
  fi
}

expect_refusal() {
  local name=$1 log
  log=$(run_case "$name" "$2")
  if grep -qE 'malformed_syntax|no_stages|refus' <<<"$log"; then
    echo "  refused $name"
  elif grep -q 'completed: success' <<<"$log"; then
    echo "  FAIL: $name COMPLETED SUCCESSFULLY — an unparseable section was dropped, not refused"
    sed 's/^/    | /' <<<"$log" | head -5
    FAILED=1
  else
    echo "  FAIL: $name neither refused nor succeeded; no diagnostic"
    sed 's/^/    | /' <<<"$log" | head -5
    FAILED=1
  fi
}

echo "=== stage options: a dropped timeout is a build past its bound ==="
expect_control stage-opts-ok 'completed: aborted' 'pipeline {
    agent any
    stages {
        stage("one") {
            options {
                timeout(time: 1, unit: "SECONDS")
            }
            steps {
                sh "sleep 3; echo done > marker.txt"
            }
        }
    }
}'
expect_refusal stage-opts-bad 'pipeline {
    agent any
    stages {
        stage("one") {
            options {
                timeout(time: 1, unit: "SECONDS")
                bogus("x) y)
            }
            steps {
                sh "sleep 3; echo done > marker.txt"
            }
        }
    }
}'

echo "=== top-level options: same section, other fallback ==="
expect_control top-opts-ok 'completed: aborted' 'pipeline {
    agent any
    options {
        timeout(time: 1, unit: "SECONDS")
    }
    stages {
        stage("one") {
            steps {
                sh "sleep 3; echo done > marker.txt"
            }
        }
    }
}'
expect_refusal top-opts-bad 'pipeline {
    agent any
    options {
        timeout(time: 1, unit: "SECONDS")
        bogus("x) y)
    }
    stages {
        stage("one") {
            steps {
                sh "sleep 3; echo done > marker.txt"
            }
        }
    }
}'

# A dropped environment variable is only visible in the WORKSPACE: the build still
# reports success, the shell just runs without the value. Asserting the terminal line
# alone would have missed the very defect these cases exist for.
#
# The `sh` body must be SINGLE-quoted so the SHELL expands `${FOO:-dropped}`. Written
# double-quoted first, it was a Groovy interpolation of a variable that does not exist
# and both controls failed — my test bug reported as an engine defect for one round.
expect_env_ok() {
  local name=$1 log
  log=$(run_case "$name" "$2")
  if ! grep -q 'completed: success' <<<"$log"; then
    echo "  FAIL: control $name did not complete — the engine over-refuses"
    sed 's/^/    | /' <<<"$log" | head -5
    FAILED=1
    return
  fi
  local marker
  marker=$(cat "$LAB/$name/ws"/*/marker.txt 2>/dev/null || echo "<no marker>")
  if [ "$marker" = "ok" ]; then
    echo "  control $name: FOO reached the shell"
  else
    echo "  FAIL: control $name ran with FOO=[$marker] — the variable was dropped"
    FAILED=1
  fi
}

echo "=== environment, both levels: a dropped var runs the shell without it ==="
expect_env_ok top-env-ok "pipeline {
    agent any
    environment {
        FOO = \"ok\"
    }
    stages {
        stage(\"one\") {
            steps {
                sh 'echo \${FOO:-dropped} > marker.txt'
            }
        }
    }
}"
expect_refusal top-env-bad "pipeline {
    agent any
    environment {
        FOO = \"ok\"
        bogus(\"x) y)
    }
    stages {
        stage(\"one\") {
            steps {
                sh 'echo \${FOO:-dropped} > marker.txt'
            }
        }
    }
}"

expect_env_ok stage-env-ok "pipeline {
    agent any
    stages {
        stage(\"one\") {
            environment {
                FOO = \"ok\"
            }
            steps {
                sh 'echo \${FOO:-dropped} > marker.txt'
            }
        }
    }
}"
expect_refusal stage-env-bad "pipeline {
    agent any
    stages {
        stage(\"one\") {
            environment {
                FOO = \"ok\"
                bogus(\"x) y)
            }
            steps {
                sh 'echo \${FOO:-dropped} > marker.txt'
            }
        }
    }
}"

echo "=== stage-level input DIRECTIVE: an unsupported human gate must stop the build ==="
# FG-155, a P0 and the only approval bypass on this branch that was not mine: mapped to
# an opaque section in the FIRST parser commit and ignored by stage construction ever
# since. MEASURED before the fix: `completed: success`, prompts=0, `shipped.txt` written
# — the gate skipped and its guarded work done.
#
# THE APPROVAL LANE COULD NOT SEE IT. Every one of its 30+ fixtures uses the STEP form
# `steps { input ... }`; this is the DIRECTIVE form, a different syntax reaching a
# different parser path to the same human gate. Guards accumulate against the spelling
# you already thought of.
#
# There is no control case here on purpose: Fogell does not implement this form, so
# there is no valid version of it to keep running. When it is implemented, this case
# must be replaced by a control that publishes a prompt and waits.
expect_refusal stage-input-directive 'pipeline {
    agent any
    stages {
        stage("Gate") {
            input {
                message "Deploy?"
                ok "Ship it"
            }
            steps {
                sh "echo shipped >> markers.txt"
            }
        }
    }
}'

echo "=== stage steps: the original silent fallback (FG-143) ==="
expect_control stage-steps-ok 'completed: success' 'pipeline {
    agent any
    stages {
        stage("one") {
            steps {
                sh "echo ran > marker.txt"
            }
        }
    }
}'
expect_refusal stage-steps-bad 'pipeline {
    agent any
    stages {
        stage("one") {
            steps {
                sh "echo ran > marker.txt"
                input(message: "Deploy, ok: "Ship it")
            }
        }
    }
}'

# FG-183. REFUSED BEFORE ANY STAGE RAN — which `expect_refusal` above cannot check, and
# the difference is the whole ticket. A refusal at ADMISSION and a fault at RUNTIME both
# leave a non-success log with a diagnostic in it, so that helper passes either way. The
# defect this arm exists for ran an earlier stage to completion and THEN faulted, and the
# obvious fix (catch the signal at the top) would still run it. Only the absence of the
# marker distinguishes them.
expect_refusal_before_effects() {
  local name=$1 marker=$2 log
  log=$(run_case "$name" "$3")
  if ! grep -qE 'malformed_syntax|no_stages|refus' <<<"$log"; then
    echo "  FAIL: $name was not refused"
    sed 's/^/    | /' <<<"$log" | head -5
    FAILED=1
  elif [ -e "$LAB/$name/ws/run/$marker" ]; then
    echo "  FAIL: $name refused, but an EARLIER STAGE had already run ($marker exists)"
    FAILED=1
  else
    echo "  refused $name, and no stage ran"
  fi
}

echo "=== break/continue outside a loop: Jenkins refuses at compile time, before any stage ==="
# The control comes FIRST here on purpose: this arm adds a new refusal, and a check that
# refuses every `break` would satisfy the refusal case while breaking every real loop.
expect_control break-in-loop-ok 'completed: success' 'pipeline {
    agent any
    stages {
        stage("one") {
            steps {
                script {
                    for (i in [1, 2]) {
                        break
                    }
                    sh "echo ran > marker.txt"
                }
            }
        }
    }
}'
expect_refusal_before_effects break-outside-loop early.txt 'pipeline {
    agent any
    stages {
        stage("early") {
            steps {
                sh "touch early.txt"
            }
        }
        stage("two") {
            steps {
                script {
                    dir("d") {
                        break
                    }
                }
            }
        }
    }
}'
expect_refusal_before_effects continue-outside-loop early.txt 'pipeline {
    agent any
    stages {
        stage("early") {
            steps {
                sh "touch early.txt"
            }
        }
        stage("two") {
            steps {
                script {
                    continue
                }
            }
        }
    }
}'

# FG-175. A MALFORMED `when` EXPRESSION IS REFUSED BEFORE ANY STAGE RUNS. Jenkins compiles
# the whole file first, so a bad condition means NO stage starts; Fogell used to admit the
# pipeline, run the earlier stages, and merely skip the gated one. Measured against the
# pinned lab: Jenkins' workspace is EMPTY, Fogell's held `early.txt`.
#
# The marker is what separates "refused" from "ran the earlier stage and skipped the gated
# one" — the second is what the defect did, and it leaves a diagnostic in the log too.
expect_control when-valid-ok 'completed: success' 'pipeline {
    agent any
    stages {
        stage("one") {
            when {
                expression { return true }
            }
            steps {
                sh "echo ran > marker.txt"
            }
        }
    }
}'
expect_refusal_before_effects when-malformed-expression early.txt 'pipeline {
    agent any
    stages {
        stage("early") {
            steps {
                sh "touch early.txt"
            }
        }
        stage("gated") {
            when {
                expression { return 10 / }
            }
            steps {
                sh "touch gated.txt"
            }
        }
    }
}'

# FG-175, remaining measured class. Duplicate map keys are conclusive Groovy
# compile errors, but `attempt` used to erase the refusal and let the opaque
# `when` fallback turn it into a runtime gate. Exercise every parser mechanism:
# an existing duplicate guard, a single-key parser that formerly left bytes
# behind, the changeRequest implicit-sibling route, and recursive composition.
expect_refusal_before_effects when-duplicate-equals early.txt 'pipeline {
    agent any
    stages {
        stage("early") { steps { sh "touch early.txt" } }
        stage("gated") {
            when { equals expected: 1, actual: 1, actual: 2 }
            steps { sh "touch gated.txt" }
        }
    }
}'
expect_refusal_before_effects when-duplicate-tag early.txt 'pipeline {
    agent any
    stages {
        stage("early") { steps { sh "touch early.txt" } }
        stage("gated") {
            when { tag pattern: "v1", pattern: "v2" }
            steps { sh "touch gated.txt" }
        }
    }
}'
expect_refusal_before_effects when-duplicate-change-request early.txt 'pipeline {
    agent any
    stages {
        stage("early") { steps { sh "touch early.txt" } }
        stage("gated") {
            when { changeRequest target: "main", target: "release" }
            steps { sh "touch gated.txt" }
        }
    }
}'
expect_refusal_before_effects when-duplicate-nested early.txt 'pipeline {
    agent any
    stages {
        stage("early") { steps { sh "touch early.txt" } }
        stage("gated") {
            when { allOf { branch "main"; environment name: "T", value: "a", value: "b" } }
            steps { sh "touch gated.txt" }
        }
    }
}'
expect_refusal_before_effects when-duplicate-parenthesised-opaque early.txt 'pipeline {
    agent any
    stages {
        stage("early") { steps { sh "touch early.txt" } }
        stage("gated") {
            when { changeRequest(target: targetFactory(1, 2), target: [name: "release", labels: ["a", "b"]]) }
            steps { sh "touch gated.txt" }
        }
    }
}'
expect_refusal_before_effects when-invalid-changeset-glob early.txt 'pipeline {
    agent any
    stages {
        stage("early") { steps { sh "touch early.txt" } }
        stage("gated") {
            when { changeset glob: "**/*.java" }
            steps { sh "touch gated.txt" }
        }
    }
}'
expect_refusal_before_effects when-invalid-directive-value early.txt 'pipeline {
    agent any
    stages {
        stage("early") { steps { sh "touch early.txt" } }
        stage("gated") {
            when { beforeAgent maybe; branch "main" }
            steps { sh "touch gated.txt" }
        }
    }
}'

# Overshoot control: a valid filter Fogell does not fully model stays admitted
# and fail-closed on a plain job. The filtered stage must not run, while the
# following stage proves the pipeline was not refused wholesale.
when_filter_log=$(run_case when-valid-unmodelled-filter 'pipeline {
    agent any
    stages {
        stage("filtered") {
            when { changeRequest target: "main" }
            steps { sh "touch must-not-run.txt" }
        }
        stage("control") { steps { sh "touch control.txt" } }
    }
}')
if grep -q 'completed: success' <<<"$when_filter_log" \
   && [ ! -e "$LAB/when-valid-unmodelled-filter/ws/run/must-not-run.txt" ] \
   && [ -e "$LAB/when-valid-unmodelled-filter/ws/run/control.txt" ]; then
  echo "  control when-valid-unmodelled-filter: admitted, filtered, and continued"
else
  echo "  FAIL: valid unsupported when filter was refused or executed"
  sed 's/^/    | /' <<<"$when_filter_log" | head -5
  FAILED=1
fi

# A broader scanner is used only to find duplicates. It must not silently widen
# Fogell's equals evaluator to collection/call/closure values it cannot model.
when_equals_log=$(run_case when-valid-unmodelled-equals 'pipeline {
    agent any
    stages {
        stage("filtered") {
            when { equals expected: [1], actual: [2] }
            steps { sh "touch must-not-run.txt" }
        }
    }
}')
if grep -q 'completed: failure' <<<"$when_equals_log" \
   && [ ! -e "$LAB/when-valid-unmodelled-equals/ws/run/must-not-run.txt" ]; then
  echo "  control when-valid-unmodelled-equals: admitted and failed closed without running the gated stage"
else
  echo "  FAIL: unsupported equals values did not fail closed before the gated effect"
  sed 's/^/    | /' <<<"$when_equals_log" | head -5
  FAILED=1
fi

if [ "$FAILED" -eq 0 ]; then
  echo "SECTION-REFUSAL PROOF: acted-on sections and every measured compile-invalid when route refuse BEFORE any stage runs, while valid controls still run"
else
  echo "SECTION-REFUSAL PROOF FAILED"
  exit 1
fi
