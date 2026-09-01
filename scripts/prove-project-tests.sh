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
    '  binary-envelope) printf "\\377\\033[37m[12:34:56 INF] EXPECTO! \\033[36m1\\033[37m test run in 00:00:00.0010000 - 1 passed, 0 ignored, 0 failed, 0 errored. \\033[36mSuccess!\\033[37m <Expecto>\\033[0m\\n" ;;' \
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
rg -q -F 'tests/Nested/Deep/Regression.fsproj: EXPECTO!' "$LAB/good.log" || {
  echo "FAIL: successful gate output did not identify its project"
  exit 1
}

# `git -C` alone does not defeat ambient repository selectors. Point every
# selector at a foreign repository whose index contains only one plausible
# project path; the runner must still execute all four projects in its own repo.
ambient_repo="$(make_case ambient-git-selectors)"
foreign_repo="$(make_case foreign-git-selectors)"
(
  cd "$foreign_repo"
  git rm -qr tests/Nested tests/'Names Differ' tests/Root.fsproj
  git commit -qm partial-inventory
)
rm -f "$ambient_repo/projects.log"
foreign_index="$(git -C "$foreign_repo" rev-parse --git-path index)"
run_runner "$ambient_repo" env \
  GIT_DIR="$foreign_repo/.git" GIT_WORK_TREE="$foreign_repo" \
  GIT_INDEX_FILE="$foreign_index" GIT_CONFIG_COUNT=1 \
  GIT_CONFIG_KEY_0=core.worktree GIT_CONFIG_VALUE_0="$foreign_repo" \
  >"$LAB/ambient-git-selectors.log"
[ "$(wc -l <"$ambient_repo/projects.log")" -eq 4 ] || {
  echo "FAIL: ambient Git selectors substituted a partial project inventory"
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

# The summary boundary must not inherit the ambient locale (FG-230). Under a
# collating UTF-8 locale, GNU sed's collation-dependent bracket ranges in the
# ANSI strip matched nothing, escapes survived normalization, and the runner
# refused every genuine success. Run the runner under explicitly named locales
# on both sides of that defect: a UTF-8 locale must accept a genuine success
# AND still refuse bad summaries (acceptance alone would pass a mutant that
# skips validation under UTF-8), and the C locale must accept the same success.
# A machine with no UTF-8 locale cannot execute this arm and must say so, not
# pass silently.
#
# Locale names do not identify the hazard: glibc's C.UTF-8 collates by
# codepoint, so bracket ranges behave exactly as in C there and an arm run
# under it proves nothing about this defect class. Probe each available UTF-8
# locale directly — under a divergently collating one, the range [@-~] fails
# to match plain ASCII — and prefer a divergent locale over a merely UTF-8 one.
utf8_locale=""
utf8_fallback=""
while IFS= read -r locale_candidate; do
  case "$locale_candidate" in
    *.UTF-8 | *.UTF-8@* | *.utf8 | *.utf8@*) ;;
    *) continue ;;
  esac
  [ -n "$utf8_fallback" ] || utf8_fallback="$locale_candidate"
  if [ -n "$(printf 'A' | LC_ALL="$locale_candidate" sed 's/[@-~]//' 2>/dev/null)" ]; then
    utf8_locale="$locale_candidate"
    break
  fi
done < <(locale -a 2>/dev/null)
if [ -z "$utf8_locale" ]; then
  utf8_locale="$utf8_fallback"
  [ -z "$utf8_locale" ] || echo \
    "NOTE: no divergently collating UTF-8 locale on this host; locale arm runs under $utf8_locale, which cannot exercise the range-collation regression"
fi
[ -n "$utf8_locale" ] || {
  echo "FAIL: no UTF-8 locale available to prove locale independence"
  exit 1
}
utf8_accept_repo="$(make_case utf8-locale-accept)"
utf8_accept_rc=0
run_runner "$utf8_accept_repo" env LC_ALL="$utf8_locale" LANG="$utf8_locale" \
  >"$LAB/utf8-locale-accept.log" 2>&1 || utf8_accept_rc=$?
if [ "$utf8_accept_rc" -ne 0 ] || ! rg -q -F \
  'tests/Canonical.Tests/Canonical.Tests.fsproj: EXPECTO!' \
  "$LAB/utf8-locale-accept.log"; then
  echo "FAIL: genuine success was refused under $utf8_locale"
  cat "$LAB/utf8-locale-accept.log"
  exit 1
fi
# A genuine success whose summary line also carries a byte that is not valid
# UTF-8 (test output is arbitrary bytes). Under a UTF-8 locale an unpinned
# extraction regex cannot match across that byte and would refuse the success;
# the byte-oriented C-pinned boundary must accept it.
utf8_binary_repo="$(make_case utf8-locale-binary-envelope)"
utf8_binary_rc=0
run_runner "$utf8_binary_repo" env LC_ALL="$utf8_locale" LANG="$utf8_locale" \
  FAKE_TEST_MODE=binary-envelope \
  >"$LAB/utf8-locale-binary-envelope.log" 2>&1 || utf8_binary_rc=$?
if [ "$utf8_binary_rc" -ne 0 ] || ! rg -q -F \
  'tests/Canonical.Tests/Canonical.Tests.fsproj: EXPECTO!' \
  "$LAB/utf8-locale-binary-envelope.log"; then
  echo "FAIL: success with a non-UTF-8 byte on the summary line was refused under $utf8_locale"
  cat "$LAB/utf8-locale-binary-envelope.log"
  exit 1
fi
utf8_failed_repo="$(make_case utf8-locale-failed)"
expect_refusal "$utf8_failed_repo" utf8-locale-failed \
  "produced a non-success Expecto summary" \
  env LC_ALL="$utf8_locale" LANG="$utf8_locale" FAKE_TEST_MODE=failed
utf8_same_line_repo="$(make_case utf8-locale-same-line-multiple)"
expect_refusal "$utf8_same_line_repo" utf8-locale-same-line-multiple \
  "produced multiple Expecto summaries" \
  env LC_ALL="$utf8_locale" LANG="$utf8_locale" FAKE_TEST_MODE=same-line-multiple
c_accept_repo="$(make_case c-locale-accept)"
c_accept_rc=0
run_runner "$c_accept_repo" env LC_ALL=C LANG=C \
  >"$LAB/c-locale-accept.log" 2>&1 || c_accept_rc=$?
if [ "$c_accept_rc" -ne 0 ] || ! rg -q -F \
  'tests/Canonical.Tests/Canonical.Tests.fsproj: EXPECTO!' \
  "$LAB/c-locale-accept.log"; then
  echo "FAIL: genuine success was refused under LC_ALL=C"
  cat "$LAB/c-locale-accept.log"
  exit 1
fi

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

echo "PROJECT TEST INVENTORY PROOF PASS: nested/mismatched projects identified; ambient Git selectors contained; inventory and summaries fail closed; summary boundary holds under named UTF-8 and C locales"
