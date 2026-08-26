#!/usr/bin/env bash
# FG-223. Black-box proof that evidence sealing fails closed and publishes atomically.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="$ROOT/scripts/seal-evidence.sh"
LAB="$(mktemp -d /tmp/fogell-fg223-seal-proof.XXXXXX)"
trap 'rm -rf -- "$LAB"' EXIT

make_case () {
  local label="$1"
  local repo="$LAB/$label"
  mkdir -p "$repo/scripts" "$repo/tests/Sample.Tests" "$repo/fakebin"
  cp "$TARGET" "$repo/scripts/seal-evidence.sh"
  chmod +x "$repo/scripts/seal-evidence.sh"
  printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "$repo/tests/Sample.Tests/Sample.Tests.fsproj"
  # shellcheck disable=SC2016 # These are literal lines in the generated fixture.
  printf '%s\n' '#!/usr/bin/env bash' 'echo "corpus fixture"' 'exit "${FAKE_CORPUS_RC:-0}"' > "$repo/scripts/verify-corpus.sh"
  chmod +x "$repo/scripts/verify-corpus.sh"
  # shellcheck disable=SC2016 # Expansion belongs to the generated fake, not this proof.
  printf '%s\n' \
    '#!/usr/bin/env bash' \
    'case "${1:-}" in' \
    '  build) sleep "${FAKE_BUILD_DELAY:-0}"; echo "Build succeeded."; exit "${FAKE_BUILD_RC:-0}" ;;' \
    '  run)' \
    '    if [ "${FAKE_TEST_MODE:-summary}" = summary ]; then' \
    '      echo "EXPECTO! 1 tests run - 1 passed, 0 failed. Success!"' \
    '    else' \
    '      echo "test process returned without a summary"' \
    '    fi' \
    '    exit "${FAKE_TEST_RC:-0}" ;;' \
    '  *) echo "unexpected fake dotnet invocation: $*" >&2; exit 97 ;;' \
    'esac' > "$repo/fakebin/dotnet"
  chmod +x "$repo/fakebin/dotnet"
  (
    cd "$repo"
    git init -q
    git config user.name FG223-Proof
    git config user.email fg223-proof@example.invalid
    git add .
    git commit -qm baseline
  )
  printf '%s\n' "$repo"
}

expect_refusal_at () {
  local repo="$1"
  local label="$2"
  local diagnostic="$3"
  shift 3
  local log="$LAB/$label.log"
  local rc=0
  (
    cd "$repo"
    env PATH="$repo/fakebin:$PATH" "$@" ./scripts/seal-evidence.sh FG-223-PROOF
  ) > "$log" 2>&1 || rc=$?

  [ "$rc" -ne 0 ] || { echo "FAIL: $label accepted a failed prerequisite"; return 1; }
  rg -q "$diagnostic" "$log" || { echo "FAIL: $label did not name '$diagnostic'"; cat "$log"; return 1; }
  if find "$repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
    echo "FAIL: $label published a manifest after refusal"
    return 1
  fi
  if find "$repo/evidence" -maxdepth 1 -type d -name '*.partial.*' -print -quit 2>/dev/null | rg -q .; then
    echo "FAIL: $label left a partial bundle"
    return 1
  fi
}

expect_refusal () {
  local label="$1"
  local diagnostic="$2"
  shift 2
  local repo
  repo="$(make_case "$label")"
  expect_refusal_at "$repo" "$label" "$diagnostic" "$@"
}

# Prove the proof's exit-code oracle is live: an always-green fake sealer must be
# rejected by the same expectation helper used for every planted prerequisite.
permissive_repo="$(make_case permissive-control)"
printf '%s\n' '#!/usr/bin/env bash' 'mkdir -p evidence/forged' 'touch evidence/forged/SHA256SUMS' 'exit 0' > "$permissive_repo/scripts/seal-evidence.sh"
chmod +x "$permissive_repo/scripts/seal-evidence.sh"
if expect_refusal_at "$permissive_repo" permissive-control-check "corpus verification failed" env FAKE_CORPUS_RC=7 \
  > "$LAB/permissive-oracle.log" 2>&1; then
  echo "FAIL: proof oracle accepted the permissive control"
  exit 1
fi
echo "planted permissive sealer: rejected by proof oracle"

expect_refusal corpus-failure "corpus verification failed" env FAKE_CORPUS_RC=7
expect_refusal build-failure "Release build failed" env FAKE_BUILD_RC=8
expect_refusal test-failure "test project failed: Sample.Tests" env FAKE_TEST_RC=9
expect_refusal missing-summary "test project produced no Expecto summary: Sample.Tests" env FAKE_TEST_MODE=no-summary

no_tests_repo="$(make_case no-tests)"
rm "$no_tests_repo/tests/Sample.Tests/Sample.Tests.fsproj"
(
  cd "$no_tests_repo"
  git add -u
  git commit -qm no-tests
)
no_tests_log="$LAB/no-tests.log"
no_tests_rc=0
(
  cd "$no_tests_repo"
  env PATH="$no_tests_repo/fakebin:$PATH" ./scripts/seal-evidence.sh FG-223-PROOF
) > "$no_tests_log" 2>&1 || no_tests_rc=$?
[ "$no_tests_rc" -ne 0 ] || { echo "FAIL: missing test inventory was accepted"; exit 1; }
rg -q "no test projects were discovered" "$no_tests_log" || { cat "$no_tests_log"; exit 1; }

missing_repo="$(make_case missing-extra)"
missing_log="$LAB/missing-extra.log"
missing_rc=0
(
  cd "$missing_repo"
  env PATH="$missing_repo/fakebin:$PATH" ./scripts/seal-evidence.sh FG-223-PROOF missing.measurement
) > "$missing_log" 2>&1 || missing_rc=$?
[ "$missing_rc" -ne 0 ] || { echo "FAIL: missing extra evidence was accepted"; exit 1; }
rg -q "extra evidence file does not exist" "$missing_log" || { cat "$missing_log"; exit 1; }

collision_repo="$(make_case extra-collision)"
printf 'forged build log\n' > "$collision_repo/build.log"
collision_log="$LAB/extra-collision.log"
collision_rc=0
(
  cd "$collision_repo"
  env PATH="$collision_repo/fakebin:$PATH" ./scripts/seal-evidence.sh FG-223-PROOF build.log
) > "$collision_log" 2>&1 || collision_rc=$?
[ "$collision_rc" -ne 0 ] || { echo "FAIL: reserved extra basename was accepted"; exit 1; }
rg -q "extra evidence basename is reserved" "$collision_log" || { cat "$collision_log"; exit 1; }

unsafe_repo="$(make_case unsafe-ticket)"
unsafe_log="$LAB/unsafe-ticket.log"
unsafe_rc=0
(
  cd "$unsafe_repo"
  env PATH="$unsafe_repo/fakebin:$PATH" ./scripts/seal-evidence.sh ../escape
) > "$unsafe_log" 2>&1 || unsafe_rc=$?
[ "$unsafe_rc" -ne 0 ] || { echo "FAIL: unsafe ticket path was accepted"; exit 1; }
rg -q "ticket id contains unsafe path characters" "$unsafe_log" || { cat "$unsafe_log"; exit 1; }

existing_repo="$(make_case existing-destination)"
existing_stamp="$(git -C "$existing_repo" log -1 --format=%cd --date=format:%Y%m%dT%H%M%SZ)"
existing_dir="$existing_repo/evidence/${existing_stamp}-fg-223-proof"
mkdir -p "$existing_dir"
printf 'owner\n' > "$existing_dir/marker"
existing_log="$LAB/existing-destination.log"
existing_rc=0
(
  cd "$existing_repo"
  env PATH="$existing_repo/fakebin:$PATH" ./scripts/seal-evidence.sh FG-223-PROOF
) > "$existing_log" 2>&1 || existing_rc=$?
[ "$existing_rc" -ne 0 ] || { echo "FAIL: existing destination was overwritten"; exit 1; }
rg -q "evidence destination already exists" "$existing_log" || { cat "$existing_log"; exit 1; }
[ "$(cat "$existing_dir/marker")" = owner ] || { echo "FAIL: existing destination marker changed"; exit 1; }

concurrent_repo="$(make_case concurrent-publish)"
(
  cd "$concurrent_repo"
  env PATH="$concurrent_repo/fakebin:$PATH" FAKE_BUILD_DELAY=0.2 ./scripts/seal-evidence.sh FG-223-PROOF
) > "$LAB/concurrent-a.log" 2>&1 &
concurrent_a=$!
(
  cd "$concurrent_repo"
  env PATH="$concurrent_repo/fakebin:$PATH" FAKE_BUILD_DELAY=0.2 ./scripts/seal-evidence.sh FG-223-PROOF
) > "$LAB/concurrent-b.log" 2>&1 &
concurrent_b=$!
concurrent_a_rc=0
concurrent_b_rc=0
wait "$concurrent_a" || concurrent_a_rc=$?
wait "$concurrent_b" || concurrent_b_rc=$?
if { [ "$concurrent_a_rc" -eq 0 ] && [ "$concurrent_b_rc" -eq 0 ]; } \
  || { [ "$concurrent_a_rc" -ne 0 ] && [ "$concurrent_b_rc" -ne 0 ]; }; then
  echo "FAIL: concurrent publishers did not produce exactly one winner: $concurrent_a_rc/$concurrent_b_rc"
  exit 1
fi
concurrent_bundle="$(find "$concurrent_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
(
  cd "$concurrent_bundle"
  sha256sum -c SHA256SUMS >/dev/null
)
[ "$(find "$concurrent_bundle" -mindepth 1 -type d | wc -l)" -eq 0 ] \
  || { echo "FAIL: losing concurrent staging tree was nested under the winner"; exit 1; }

success_repo="$(make_case success)"
(
  cd "$success_repo"
  env PATH="$success_repo/fakebin:$PATH" ./scripts/seal-evidence.sh FG-223-PROOF > "$LAB/success.log"
)
bundle="$(find "$success_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
[ -n "$bundle" ] || { echo "FAIL: successful seal published no bundle"; exit 1; }
(
  cd "$bundle"
  sha256sum -c SHA256SUMS >/dev/null
)
[ "$(find "$bundle" -maxdepth 1 -type f -name 'tests-*.log' | wc -l)" -eq 1 ] \
  || { echo "FAIL: successful seal omitted the test summary"; exit 1; }

echo "FG-223 evidence sealer proof: PASS (permissive control rejected; prerequisite, inventory and input-boundary failures publish no manifest; one concurrent publisher wins; success verifies)"
