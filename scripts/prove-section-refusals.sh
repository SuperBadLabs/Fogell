#!/usr/bin/env bash
# FG-152. A SECTION FOGELL ACTS ON, WHEN IT DOES NOT PARSE, IS REFUSED — NEVER
# CONSUMED OPAQUELY AND DROPPED.
#
# This exists because the same fix landed on one of two sibling functions three
# times running. FG-143 taught the STAGE fallback to refuse `steps`. FG-150 taught
# the TOP-LEVEL fallback to refuse `options`/`stages`. Neither carried `options`
# back to the stage fallback, so a stage's
# `options { timeout(...); bogus(/x) y/) }` was still swallowed whole and its
# TIMEOUT SILENTLY DROPPED — measured `completed: success` against a control that
# aborts. A dropped timeout is a build that runs past a bound Jenkins enforces.
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
                bogus(/x) y/)
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
        bogus(/x) y/)
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
        bogus(/x) y/)
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
                bogus(/x) y/)
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
                input(/Deploy { / + env.TARGET, ok: "Ship it")
            }
        }
    }
}'

if [ "$FAILED" -eq 0 ]; then
  echo "SECTION-REFUSAL PROOF: options/steps/environment refuse at both levels, the input DIRECTIVE refuses, and every control still runs"
else
  echo "SECTION-REFUSAL PROOF FAILED"
  exit 1
fi
