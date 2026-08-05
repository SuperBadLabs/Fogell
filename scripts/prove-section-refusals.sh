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
  echo "SECTION-REFUSAL PROOF: every acted-on section refuses when unparseable, and every control still runs"
else
  echo "SECTION-REFUSAL PROOF FAILED"
  exit 1
fi
