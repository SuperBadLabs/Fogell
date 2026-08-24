#!/usr/bin/env bash
# FG-072. Prove the interpreter sandbox refuses the complete named escape
# inventory before a successor step can run, while sanctioned calls still work.
#
# This lane is intentionally two-sided. The live matrix proves denials, and the
# allowed control proves that an implementation which refuses every script does
# not pass. The final eight arms mutate one accepted denial result into two bad
# execution states (timeout and signal) and six bad record/workspace states
# (non-failure terminal, extra terminal, generic rather than typed, unnamed,
# missing its boundary reason, and not halted).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

RUN_HOST="$PWD/tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host"
[ -x "$RUN_HOST" ] \
  || { echo "prove-sandbox-denials: exact net10.0 Release Fogell.Run.Host is missing; build it first"; exit 1; }

LAB=$(mktemp -d /tmp/fogell-sandbox-denials.XXXXXX)
trap 'rm -rf "$LAB"' EXIT

# id|expected attempted name|script expression. Every name in
# Sandbox.knownEscapes occurs at least once; the inventory check below refuses
# drift in either direction. Constructors, free calls, member calls, and the
# null-safe short-circuit path all traverse the parser and interpreter rather
# than calling Sandbox.admit* here.
CASES=(
  "file|new File|new File('/etc/passwd')"
  "file-input|new FileInputStream|new FileInputStream('/etc/passwd')"
  "file-output|new FileOutputStream|new FileOutputStream('/tmp/fg072')"
  "random-access|new RandomAccessFile|new RandomAccessFile('/etc/passwd', 'r')"
  "process-builder|new ProcessBuilder|new ProcessBuilder('id')"
  "runtime|Runtime|Runtime()"
  "system|System|System()"
  "class|Class|Class()"
  "class-loader|ClassLoader|ClassLoader()"
  "groovy-shell|new GroovyShell|new GroovyShell()"
  "groovy-loader|new GroovyClassLoader|new GroovyClassLoader()"
  "eval|Eval|Eval('1 + 1')"
  "evaluate|evaluate|evaluate('1 + 1')"
  "url|new URL|new URL('https://example.invalid')"
  "url-connection|URLConnection|URLConnection()"
  "socket|new Socket|new Socket('127.0.0.1', 9)"
  "server-socket|new ServerSocket|new ServerSocket(9)"
  "http-url-connection|HttpURLConnection|HttpURLConnection()"
  "thread|new Thread|new Thread()"
  "unsafe|Unsafe|Unsafe()"
  "method-handles|MethodHandles|MethodHandles()"
  "get-class|getClass|'value'.getClass()"
  "safe-null-get-class|getClass|def target = null; target?.getClass()"
  "for-name|forName|'value'.forName('java.lang.System')"
  "new-instance|newInstance|'value'.newInstance()"
  "declared-method|getDeclaredMethod|'value'.getDeclaredMethod('x')"
  "declared-field|getDeclaredField|'value'.getDeclaredField('x')"
  "set-accessible|setAccessible|'value'.setAccessible(true)"
  "invoke|invoke|'value'.invoke()"
  "execute|execute|'id'.execute()"
  "exec|exec|'id'.exec()"
)

inventory_from_source() {
  sed -n '/let knownEscapes/,/Decide whether/p' src/Fogell.Groovy.Interpreter/Sandbox.fs \
    | rg -o '"[^"]+"' \
    | tr -d '"' \
    | sort
}

inventory_from_cases() {
  local row expected
  for row in "${CASES[@]}"; do
    IFS='|' read -r _ expected _ <<<"$row"
    printf '%s\n' "${expected#new }"
  done | sort -u
}

if ! diff -u <(inventory_from_source) <(inventory_from_cases); then
  echo "prove-sandbox-denials: CASES do not exactly cover Sandbox.knownEscapes"
  exit 1
fi

write_pipeline() {
  local target=$1 expression=$2
  mkdir -p "$target"
  {
    printf '%s\n' 'pipeline {'
    printf '%s\n' '    agent any'
    printf '%s\n' '    stages {'
    printf '%s\n' '        stage("sandbox") {'
    printf '%s\n' '            steps {'
    printf '%s\n' '                script {'
    printf '                    %s\n' "$expression"
    printf '%s\n' '                    sh "printf escaped > escaped.txt"'
    printf '%s\n' '                }'
    printf '%s\n' '            }'
    printf '%s\n' '        }'
    printf '%s\n' '    }'
    printf '%s\n' '}'
  } > "$target/Jenkinsfile"
}

judge_host_status() {
  local run_status=$1 label=$2

  case "$run_status" in
    ''|*[!0-9]*)
      echo "  FAIL $label: invalid host exit status [$run_status]"
      return 1
      ;;
    124)
      echo "  FAIL $label: host timed out before completing the proof"
      return 1
      ;;
    *)
      if [ "$run_status" -gt 128 ]; then
        echo "  FAIL $label: host terminated by signal $((run_status - 128))"
        return 1
      fi
      ;;
  esac
}

judge_terminal() {
  local journal=$1 expected=$2 label=$3
  local terminal_count terminal_value failed=0

  terminal_count=$(awk -F '\t' '$1 == "build-finished" { count++ } END { print count + 0 }' "$journal")
  terminal_value=$(awk -F '\t' '$1 == "build-finished" { print $2; exit }' "$journal")

  if [ "$terminal_count" -ne 1 ]; then
    echo "  FAIL $label: expected exactly one build-finished record, found $terminal_count"
    failed=1
  fi
  if [ "$terminal_value" != "$expected" ]; then
    echo "  FAIL $label: terminal value [$terminal_value] is not [$expected]"
    failed=1
  fi

  [ "$failed" -eq 0 ]
}

run_denial() {
  local id=$1 expression=$2 target="$LAB/$id" run_status
  write_pipeline "$target" "$expression"
  set +e
  timeout --kill-after=5 60 "$RUN_HOST" "$target/Jenkinsfile" "$target/ws" run "$target/build.journal" \
    > "$target/run.log" 2>&1
  run_status=$?
  set -e
  printf '%s\n' "$run_status" > "$target/run.status"
}

judge_denial() {
  local target=$1 expected=$2 label=$3 failed=0
  local journal="$target/build.journal"
  local run_status=

  if [ ! -s "$target/run.status" ]; then
    echo "  FAIL $label: missing host exit status"
    failed=1
  else
    run_status=$(tr -d '[:space:]' < "$target/run.status")
    judge_host_status "$run_status" "$label" || failed=1
  fi

  if [ ! -s "$journal" ]; then
    echo "  FAIL $label: no journal; the case may have been rejected by the parser instead of the sandbox"
    return 1
  fi

  judge_terminal "$journal" failure "$label" || failed=1
  grep -q $'step-reason\tsandbox\t0\tscript block: Denied' "$journal" \
    || { echo "  FAIL $label: no typed Denied step reason"; failed=1; }
  grep -Fq "Attempted = \"$expected\"" "$journal" \
    || { echo "  FAIL $label: denial did not name attempted capability [$expected]"; failed=1; }
  grep -q 'not reachable\|not a pure builtin' "$journal" \
    || { echo "  FAIL $label: denial did not state the capability boundary"; failed=1; }
  if find "$target/ws" -name escaped.txt -type f -print -quit 2>/dev/null | grep -q .; then
    echo "  FAIL $label: successor step ran after denial"
    failed=1
  fi

  [ "$failed" -eq 0 ]
}

INVENTORY_SIZE=$(inventory_from_source | wc -l | tr -d ' ')
echo "=== FG-072 live sandbox denial matrix (${#CASES[@]} vectors / $INVENTORY_SIZE names) ==="
REFERENCE_TARGET=
REFERENCE_EXPECTED=
for row in "${CASES[@]}"; do
  IFS='|' read -r id expected expression <<<"$row"
  run_denial "$id" "$expression"
  judge_denial "$LAB/$id" "$expected" "$id"
  printf '  denied %s as %s\n' "$id" "$expected"
  if [ -z "$REFERENCE_TARGET" ]; then
    REFERENCE_TARGET="$LAB/$id"
    REFERENCE_EXPECTED=$expected
  fi
done

echo "=== sanctioned-call control ==="
CONTROL="$LAB/allowed-control"
mkdir -p "$CONTROL"
cat > "$CONTROL/Jenkinsfile" <<'EOF'
def clean(value) { return value.trim() }
pipeline {
    agent any
    stages {
        stage("allowed") {
            steps {
                script {
                    def value = clean("  ok  ")
                    echo value
                    sh "printf ${value} > allowed.txt"
                }
            }
        }
    }
}
EOF
set +e
timeout --kill-after=5 60 "$RUN_HOST" "$CONTROL/Jenkinsfile" "$CONTROL/ws" run "$CONTROL/build.journal" \
  > "$CONTROL/run.log" 2>&1
CONTROL_STATUS=$?
set -e
judge_host_status "$CONTROL_STATUS" "allowed control" || exit 1
judge_terminal "$CONTROL/build.journal" success "allowed control" || exit 1
CONTROL_FILE=$(find "$CONTROL/ws" -name allowed.txt -type f -print -quit 2>/dev/null || true)
[ -n "$CONTROL_FILE" ] && [ "$(cat "$CONTROL_FILE")" = "ok" ] \
  || { echo "  FAIL: allowed control did not produce the expected workspace effect"; exit 1; }
echo "  allowed control: registered steps + script function + trim builtin"

expect_planted_failure() {
  local label=$1 target=$2 expected=$3
  if judge_denial "$target" "$expected" "planted-$label" >/dev/null 2>&1; then
    echo "  FAIL: checker accepted planted $label state"
    exit 1
  fi
  echo "  checker rejects planted $label state"
}

echo "=== checker self-proof ==="
TIMEOUT_STATUS="$LAB/planted-timeout-status"
cp -a "$REFERENCE_TARGET" "$TIMEOUT_STATUS"
printf '%s\n' 124 > "$TIMEOUT_STATUS/run.status"
expect_planted_failure timeout-status "$TIMEOUT_STATUS" "$REFERENCE_EXPECTED"

SIGNAL_STATUS="$LAB/planted-signal-status"
cp -a "$REFERENCE_TARGET" "$SIGNAL_STATUS"
printf '%s\n' 143 > "$SIGNAL_STATUS/run.status"
expect_planted_failure signal-status "$SIGNAL_STATUS" "$REFERENCE_EXPECTED"

NON_FAILURE_TERMINAL="$LAB/planted-non-failure-terminal"
cp -a "$REFERENCE_TARGET" "$NON_FAILURE_TERMINAL"
sed -i $'s/build-finished\tfailure/build-finished\tsuccess/' "$NON_FAILURE_TERMINAL/build.journal"
if [ "$(awk -F '\t' '$1 == "build-finished" { count++ } END { print count + 0 }' "$NON_FAILURE_TERMINAL/build.journal")" -ne 1 ] \
  || ! grep -qx $'build-finished\tsuccess' "$NON_FAILURE_TERMINAL/build.journal"; then
  echo "  FAIL: could not plant unique non-failure terminal state"
  exit 1
fi
expect_planted_failure non-failure-terminal "$NON_FAILURE_TERMINAL" "$REFERENCE_EXPECTED"

EXTRA_TERMINAL="$LAB/planted-extra-terminal"
cp -a "$REFERENCE_TARGET" "$EXTRA_TERMINAL"
printf '%s\n' $'build-finished\tsuccess' >> "$EXTRA_TERMINAL/build.journal"
if [ "$(awk -F '\t' '$1 == "build-finished" { count++ } END { print count + 0 }' "$EXTRA_TERMINAL/build.journal")" -ne 2 ] \
  || ! grep -qx $'build-finished\tfailure' "$EXTRA_TERMINAL/build.journal" \
  || ! grep -qx $'build-finished\tsuccess' "$EXTRA_TERMINAL/build.journal"; then
  echo "  FAIL: could not plant extra-terminal state"
  exit 1
fi
expect_planted_failure extra-terminal "$EXTRA_TERMINAL" "$REFERENCE_EXPECTED"

GENERIC_FAILURE="$LAB/planted-generic-failure"
cp -a "$REFERENCE_TARGET" "$GENERIC_FAILURE"
sed -i 's/script block: Denied/script block: Failure/' "$GENERIC_FAILURE/build.journal"
if grep -q 'script block: Denied' "$GENERIC_FAILURE/build.journal" \
  || ! grep -q 'script block: Failure' "$GENERIC_FAILURE/build.journal"; then
  echo "  FAIL: could not plant generic-failure state"
  exit 1
fi
expect_planted_failure generic-failure "$GENERIC_FAILURE" "$REFERENCE_EXPECTED"

UNNAMED="$LAB/planted-unnamed"
cp -a "$REFERENCE_TARGET" "$UNNAMED"
sed -i "s/Attempted = \"$REFERENCE_EXPECTED\"/Attempted = \"redacted\"/" "$UNNAMED/build.journal"
expect_planted_failure unnamed "$UNNAMED" "$REFERENCE_EXPECTED"

NO_BOUNDARY_REASON="$LAB/planted-no-boundary-reason"
cp -a "$REFERENCE_TARGET" "$NO_BOUNDARY_REASON"
sed -i -e 's/not reachable/capability denied/g' \
  -e 's/not a pure builtin/capability denied/g' "$NO_BOUNDARY_REASON/build.journal"
if grep -q 'not reachable\|not a pure builtin' "$NO_BOUNDARY_REASON/build.journal" \
  || ! grep -q 'capability denied' "$NO_BOUNDARY_REASON/build.journal"; then
  echo "  FAIL: could not plant missing-boundary-reason state"
  exit 1
fi
expect_planted_failure no-boundary-reason "$NO_BOUNDARY_REASON" "$REFERENCE_EXPECTED"

NO_HALT="$LAB/planted-no-halt"
cp -a "$REFERENCE_TARGET" "$NO_HALT"
mkdir -p "$NO_HALT/ws/planted"
printf '%s' escaped > "$NO_HALT/ws/planted/escaped.txt"
expect_planted_failure no-halt "$NO_HALT" "$REFERENCE_EXPECTED"

echo "FG-072 sandbox denial proof: PASS"
