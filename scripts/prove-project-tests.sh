#!/usr/bin/env bash
# Prove the authoritative test runner fails closed at its inventory and summary
# boundaries. This proof is self-contained: fake dotnet processes make every
# planted state deterministic and no package restore or build is performed.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TARGET="$ROOT/scripts/run-project-tests.sh"
REAL_GIT="$(type -P git)"
LAB="$(mktemp -d /tmp/fogell-project-test-proof.XXXXXX)"
trap 'rm -rf -- "$LAB"' EXIT

make_case() {
  local label="$1"
  local repo="$LAB/$label"
  mkdir -p "$repo/scripts" "$repo/fakebin" \
    "$repo/tests/Canonical.Tests" "$repo/tests/Nested/Deep" "$repo/tests/Names Differ"
  cp "$TARGET" "$repo/scripts/run-project-tests.sh"
  chmod +x "$repo/scripts/run-project-tests.sh"
  printf '<Project />\n' >"$repo/tests/Canonical.Tests/Canonical.Tests.fsproj"
  printf '<Project />\n' >"$repo/tests/Nested/Deep/Regression.fsproj"
  printf '<Project />\n' >"$repo/tests/Names Differ/Unexpected.fsproj"
  printf '<Project />\n' >"$repo/tests/Root.fsproj"
  # shellcheck disable=SC2016 # Expansion belongs to the generated fake.
  printf '%s\n' \
    '#!/usr/bin/env bash' \
    '[ "$#" -eq 6 ] && [ "$1" = run ] && [ "$2" = --project ] && [ "$4" = -c ] && [ "$5" = Release ] && [ "$6" = --no-build ] || { echo "unexpected dotnet argv: $*" >&2; exit 97; }' \
    '[ "$PWD" = "$FAKE_EXPECTED_CWD" ] || { echo "unexpected dotnet cwd: $PWD" >&2; exit 96; }' \
    'printf "%s\n" "$3" >> "$FAKE_PROJECT_LOG"' \
    'case "${FAKE_TEST_MODE:-success}" in' \
    '  success) printf "\\033[37m[12:34:56 INF] EXPECTO! \\033[36m1\\033[37m test run in 00:00:00.0010000 - 1 passed, 0 ignored, 0 failed, 0 errored. \\033[36mSuccess!\\033[37m <Expecto>\\033[0m\\n" ;;' \
    '  no-summary) echo "test process returned without a summary" ;;' \
    '  zero) echo "EXPECTO! 0 tests run in 00:00:00 - 0 passed, 0 ignored, 0 failed, 0 errored. Success!" ;;' \
    '  failed) echo "EXPECTO! 1 test run in 00:00:00 - 0 passed, 0 ignored, 1 failed, 0 errored. Failure!" ;;' \
    '  errored) echo "EXPECTO! 1 test run in 00:00:00 - 0 passed, 0 ignored, 0 failed, 1 errored. Failure!" ;;' \
    '  multiple) echo "EXPECTO! 1 test run - 1 passed, 0 ignored, 0 failed, 0 errored. Success!"; echo "EXPECTO! 1 test run - 1 passed, 0 ignored, 0 failed, 0 errored. Success!" ;;' \
    '  same-line-multiple) echo "EXPECTO! 1 test run - 0 passed, 0 ignored, 1 failed, 0 errored. Failure! EXPECTO! 1 test run - 1 passed, 0 ignored, 0 failed, 0 errored. Success!" ;;' \
    '  malformed) echo "EXPECTO! definitely Success!" ;;' \
    '  inconsistent) echo "EXPECTO! 2 tests run in 00:00:00 - 1 passed, 0 ignored, 0 failed, 0 errored. Success!" ;;' \
    'esac' \
    '[ "${FAKE_TEST_RC:-0}" -eq 0 ] || exit "$FAKE_TEST_RC"' \
    'exit 0' >"$repo/fakebin/dotnet"
  chmod +x "$repo/fakebin/dotnet"
  (
    cd "$repo"
    git init -q
    git config user.name Fogell-Proof
    git config user.email fogell-proof@example.invalid
    git add .
    git commit -qm baseline
  )
  printf '%s\n' "$repo"
}

run_runner() {
  local repo="$1"
  shift
  (
    cd "$repo"
    env PATH="$repo/fakebin:$PATH" FAKE_PROJECT_LOG="$repo/projects.log" \
      FAKE_EXPECTED_CWD="$repo" "$@" \
      ./scripts/run-project-tests.sh
  )
}

expect_refusal() {
  local repo="$1"
  local label="$2"
  local diagnostic="$3"
  shift 3
  local log="$LAB/$label.log"
  local rc=0
  run_runner "$repo" "$@" >"$log" 2>&1 || rc=$?
  [ "$rc" -ne 0 ] || { echo "FAIL: $label was accepted"; return 1; }
  rg -q -F "$diagnostic" "$log" || {
    echo "FAIL: $label did not name '$diagnostic'"
    cat "$log"
    return 1
  }
}

good_repo="$(make_case good)"
run_runner "$good_repo" >"$LAB/good.log"
[ "$(wc -l <"$good_repo/projects.log")" -eq 4 ] || {
  echo "FAIL: runner did not execute every tracked project"
  exit 1
}
rg -q -F 'tests/Nested/Deep/Regression.fsproj' "$good_repo/projects.log" || {
  echo "FAIL: nested project was skipped"
  exit 1
}
rg -q -F 'tests/Names Differ/Unexpected.fsproj' "$good_repo/projects.log" || {
  echo "FAIL: differently named project was skipped"
  exit 1
}
rg -q -F 'tests/Root.fsproj' "$good_repo/projects.log" || {
  echo "FAIL: root-level project was skipped"
  exit 1
}

log_repo="$(make_case sealed-logs)"
(
  cd "$log_repo"
  env PATH="$log_repo/fakebin:$PATH" FAKE_PROJECT_LOG="$log_repo/projects.log" \
    FAKE_EXPECTED_CWD="$log_repo" \
    ./scripts/run-project-tests.sh --log-dir "$log_repo/logs" >"$LAB/sealed-logs.log"
)
[ "$(find "$log_repo/logs" -maxdepth 1 -type f -name 'tests-*.log' | wc -l)" -eq 4 ] || {
  echo "FAIL: per-project evidence logs were incomplete"
  exit 1
}
rg -l -F 'project: tests/Nested/Deep/Regression.fsproj' "$log_repo/logs"/tests-*.log >/dev/null || {
  echo "FAIL: nested project identity was not logged"
  exit 1
}

failure_repo="$(make_case failed-exit)"
expect_refusal "$failure_repo" failed-exit "test project failed:" env FAKE_TEST_RC=9
no_summary_repo="$(make_case no-summary)"
expect_refusal "$no_summary_repo" no-summary "produced no Expecto summary" env FAKE_TEST_MODE=no-summary
zero_repo="$(make_case zero-tests)"
expect_refusal "$zero_repo" zero-tests "produced a non-success Expecto summary" env FAKE_TEST_MODE=zero
failed_repo="$(make_case failed-summary)"
expect_refusal "$failed_repo" failed-summary "produced a non-success Expecto summary" env FAKE_TEST_MODE=failed
errored_repo="$(make_case errored-summary)"
expect_refusal "$errored_repo" errored-summary "produced a non-success Expecto summary" env FAKE_TEST_MODE=errored
multiple_repo="$(make_case multiple-summary)"
expect_refusal "$multiple_repo" multiple-summary "produced multiple Expecto summaries" env FAKE_TEST_MODE=multiple
same_line_repo="$(make_case same-line-multiple-summary)"
expect_refusal "$same_line_repo" same-line-multiple-summary "produced multiple Expecto summaries" env FAKE_TEST_MODE=same-line-multiple
malformed_repo="$(make_case malformed-summary)"
expect_refusal "$malformed_repo" malformed-summary "produced a non-success Expecto summary" env FAKE_TEST_MODE=malformed
inconsistent_repo="$(make_case inconsistent-summary)"
expect_refusal "$inconsistent_repo" inconsistent-summary "produced a non-success Expecto summary" env FAKE_TEST_MODE=inconsistent

empty_repo="$(make_case empty-inventory)"
(
  cd "$empty_repo"
  git rm -qr tests
  git commit -qm empty-inventory
)
expect_refusal "$empty_repo" empty-inventory "no test projects were discovered"

inventory_repo="$(make_case inventory-failure)"
# Emit one plausible path and then fail. mapfile alone would flatten this into a
# partial success; the runner must wait for the inventory producer explicitly.
# shellcheck disable=SC2016 # Expansion belongs to the generated fake.
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'for arg in "$@"; do' \
  '  if [ "$arg" = ":(glob)tests/**/*.fsproj" ]; then' \
  '    printf "tests/Canonical.Tests/Canonical.Tests.fsproj\\0"' \
  '    echo "planted inventory failure" >&2' \
  '    exit 66' \
  '  fi' \
  'done' \
  "exec $(printf '%q' "$REAL_GIT") \"\$@\"" >"$inventory_repo/fakebin/git"
chmod +x "$inventory_repo/fakebin/git"
expect_refusal "$inventory_repo" inventory-failure "tracked test project inventory could not be read"

argv_mutant_repo="$(make_case argv-mutation)"
sed -i 's/ -c Release --no-build/ -c Release/' "$argv_mutant_repo/scripts/run-project-tests.sh"
expect_refusal "$argv_mutant_repo" argv-mutation "unexpected dotnet argv:"

# Prove the refusal oracle itself notices an always-green mutant.
permissive_repo="$(make_case permissive-control)"
printf '%s\n' '#!/usr/bin/env bash' 'exit 0' >"$permissive_repo/scripts/run-project-tests.sh"
chmod +x "$permissive_repo/scripts/run-project-tests.sh"
if expect_refusal "$permissive_repo" permissive-control-check "test project failed:" env FAKE_TEST_RC=9 \
  >"$LAB/permissive-control.log" 2>&1; then
  echo "FAIL: proof oracle accepted the permissive runner"
  exit 1
fi

echo "PROJECT TEST INVENTORY PROOF PASS: nested/mismatched projects executed; inventory and summaries fail closed"
