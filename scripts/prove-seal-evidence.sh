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
  printf '\0baseline binary candidate\377\n' > "$repo/candidate.bin"
  printf 'stable candidate\n' > "$repo/candidate.txt"
  printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "$repo/tests/Sample.Tests/Sample.Tests.fsproj"
  # shellcheck disable=SC2016 # These are literal lines in the generated fixture.
  printf '%s\n' '#!/usr/bin/env bash' 'echo "CORPUS_PWD=$PWD"' 'exit "${FAKE_CORPUS_RC:-0}"' > "$repo/scripts/verify-corpus.sh"
  chmod +x "$repo/scripts/verify-corpus.sh"
  # shellcheck disable=SC2016 # Expansion belongs to the generated fake, not this proof.
  printf '%s\n' \
    '#!/usr/bin/env bash' \
    'case "${1:-}" in' \
    '  build)' \
    '    [ -z "${FAKE_BUILD_MARKER:-}" ] || : > "$FAKE_BUILD_MARKER"' \
    '    sleep "${FAKE_MEASURE_DELAY:-0}"' \
    '    [ -z "${FAKE_MEASURE_FILE:-}" ] || { printf "MEASURED: "; cat "$FAKE_MEASURE_FILE"; }' \
    '    [ -z "${FAKE_MUTATE_FILE:-}" ] || printf "%s\n" "${FAKE_MUTATE_CONTENT:-mutated by prerequisite}" > "$FAKE_MUTATE_FILE"' \
    '    [ -z "${FAKE_MEASURED_MARKER:-}" ] || : > "$FAKE_MEASURED_MARKER"' \
    '    sleep "${FAKE_BUILD_DELAY:-0}"; echo "BUILD_PWD=$PWD"; echo "Build succeeded."; exit "${FAKE_BUILD_RC:-0}" ;;' \
    '  run)' \
    '    if [ "${FAKE_TEST_MODE:-summary}" = summary ]; then' \
    '      echo "EXPECTO! 1 tests run - 1 passed, 0 failed. Success! TEST_PWD=$PWD"' \
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
expect_refusal test-failure "test project failed: tests/Sample.Tests/Sample.Tests.fsproj" env FAKE_TEST_RC=9
expect_refusal missing-summary "test project produced no Expecto summary: tests/Sample.Tests/Sample.Tests.fsproj" env FAKE_TEST_MODE=no-summary
expect_refusal snapshot-mutation "materialized candidate changed while prerequisites ran" \
  env FAKE_MUTATE_FILE=candidate.txt

untracked_mutation_repo="$(make_case untracked-snapshot-mutation)"
git -C "$untracked_mutation_repo" config status.showUntrackedFiles no
expect_refusal_at "$untracked_mutation_repo" untracked-snapshot-mutation \
  "materialized candidate changed while prerequisites ran: status" \
  env FAKE_MUTATE_FILE=future-source.fs

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

partial_inventory_repo="$(make_case partial-test-inventory)"
mkdir -p "$partial_inventory_repo/tests/Hidden"
printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "$partial_inventory_repo/tests/Hidden/Other.fsproj"
printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "$partial_inventory_repo/tests/Root.fsproj"
(
  cd "$partial_inventory_repo"
  git add tests/Hidden/Other.fsproj tests/Root.fsproj
  git commit -qm add-nonconforming-test-project
  env PATH="$partial_inventory_repo/fakebin:$PATH" \
    ./scripts/seal-evidence.sh FG-223-PROOF > "$LAB/partial-test-inventory.log"
)
partial_inventory_bundle="$(find "$partial_inventory_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
[ "$(find "$partial_inventory_bundle" -maxdepth 1 -type f -name 'tests-*.log' | wc -l)" -eq 3 ] \
  || { echo "FAIL: sealer omitted a tracked nonconforming test project"; exit 1; }
rg -l -F 'project: tests/Hidden/Other.fsproj' "$partial_inventory_bundle"/tests-*.log >/dev/null \
  || { echo "FAIL: nonconforming test project identity was not sealed"; exit 1; }
rg -l -F 'project: tests/Root.fsproj' "$partial_inventory_bundle"/tests-*.log >/dev/null \
  || { echo "FAIL: root-level test project identity was not sealed"; exit 1; }

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

hidden_repo="$(make_case hidden-extra-collision)"
printf 'caller-owned hidden evidence\n' > "$hidden_repo/.materialization.log"
hidden_log="$LAB/hidden-extra-collision.log"
hidden_rc=0
(
  cd "$hidden_repo"
  env PATH="$hidden_repo/fakebin:$PATH" \
    ./scripts/seal-evidence.sh FG-223-PROOF .materialization.log
) > "$hidden_log" 2>&1 || hidden_rc=$?
[ "$hidden_rc" -ne 0 ] || { echo "FAIL: hidden internal extra basename was silently omitted"; exit 1; }
rg -q "extra evidence basename is reserved for internal staging" "$hidden_log" \
  || { cat "$hidden_log"; exit 1; }

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

mv_failure_repo="$(make_case atomic-move-failure)"
# shellcheck disable=SC2016 # Expansion belongs to the generated fake, not this proof.
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'if [ "${1:-}" = --help ]; then echo "  -T, --no-target-directory"; exit 0; fi' \
  '[ "$#" -eq 4 ] && [ "$1" = -T ] && [ "$2" = -n ] || exit 97' \
  '[ -d "$3" ] && [ ! -e "$4" ] || exit 98' \
  'echo "planted atomic move failure" >&2' \
  'exit 74' > "$mv_failure_repo/fakebin/mv"
chmod +x "$mv_failure_repo/fakebin/mv"
(
  cd "$mv_failure_repo"
  git add fakebin/mv
  git commit -qm add-failing-mv
)
mv_failure_log="$LAB/atomic-move-failure.log"
mv_failure_rc=0
(
  cd "$mv_failure_repo"
  env PATH="$mv_failure_repo/fakebin:$PATH" ./scripts/seal-evidence.sh FG-223-PROOF
) > "$mv_failure_log" 2>&1 || mv_failure_rc=$?
[ "$mv_failure_rc" -ne 0 ] || { echo "FAIL: failed atomic move was accepted"; exit 1; }
rg -q "planted atomic move failure" "$mv_failure_log" \
  || { echo "FAIL: atomic move failure control did not reach the intended branch"; cat "$mv_failure_log"; exit 1; }
rg -q "atomic evidence publication failed" "$mv_failure_log" \
  || { echo "FAIL: failed atomic move was misdiagnosed"; cat "$mv_failure_log"; exit 1; }
if rg -q "evidence destination appeared during sealing" "$mv_failure_log"; then
  echo "FAIL: ordinary atomic move failure was diagnosed as a destination race"
  cat "$mv_failure_log"
  exit 1
fi
if find "$mv_failure_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: failed atomic move published a manifest"
  exit 1
fi

empty_race_repo="$(make_case empty-destination-race)"
empty_race_stamp="$(git -C "$empty_race_repo" log -1 --format=%cd --date=format:%Y%m%dT%H%M%SZ)"
empty_race_dir="$empty_race_repo/evidence/${empty_race_stamp}-fg-223-proof"
empty_race_marker="$LAB/empty-destination-build-started"
empty_race_log="$LAB/empty-destination-race.log"
empty_race_rc=0
(
  cd "$empty_race_repo"
  env PATH="$empty_race_repo/fakebin:$PATH" \
    FAKE_BUILD_DELAY=0.5 FAKE_BUILD_MARKER="$empty_race_marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$empty_race_log" 2>&1 &
empty_race_pid=$!
for _ in $(seq 1 100); do
  [ -e "$empty_race_marker" ] && break
  sleep 0.02
done
[ -e "$empty_race_marker" ] || { echo "FAIL: empty-destination race build never started"; kill "$empty_race_pid" 2>/dev/null || true; exit 1; }
mkdir "$empty_race_dir"
wait "$empty_race_pid" || empty_race_rc=$?
[ "$empty_race_rc" -ne 0 ] || { echo "FAIL: empty race-created destination was replaced"; exit 1; }
rg -q "evidence destination appeared during sealing" "$empty_race_log" \
  || { echo "FAIL: empty destination race was not diagnosed"; cat "$empty_race_log"; exit 1; }
if rg -q "atomic evidence publication failed" "$empty_race_log"; then
  echo "FAIL: empty destination race was diagnosed as an ordinary atomic move failure"
  cat "$empty_race_log"
  exit 1
fi
if [ ! -d "$empty_race_dir" ] \
  || [ -n "$(find "$empty_race_dir" -mindepth 1 -print -quit)" ]; then
  echo "FAIL: empty race-created destination was not preserved"
  exit 1
fi

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

drift_repo="$(make_case candidate-drift)"
drift_marker="$LAB/candidate-drift-build-started"
drift_log="$LAB/candidate-drift.log"
drift_rc=0
(
  cd "$drift_repo"
  env PATH="$drift_repo/fakebin:$PATH" \
    FAKE_BUILD_DELAY=0.5 FAKE_BUILD_MARKER="$drift_marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$drift_log" 2>&1 &
drift_pid=$!
for _ in $(seq 1 100); do
  [ -e "$drift_marker" ] && break
  sleep 0.02
done
[ -e "$drift_marker" ] || { echo "FAIL: candidate-drift build never started"; kill "$drift_pid" 2>/dev/null || true; exit 1; }
printf 'concurrent edit\n' >> "$drift_repo/candidate.txt"
wait "$drift_pid" || drift_rc=$?
[ "$drift_rc" -ne 0 ] || { echo "FAIL: source drift published an evidence bundle"; exit 1; }
rg -q "candidate source changed while prerequisites ran" "$drift_log" \
  || { echo "FAIL: source drift did not name the changed candidate"; cat "$drift_log"; exit 1; }
if find "$drift_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: source drift published a manifest"
  exit 1
fi
if find "$drift_repo/evidence" -maxdepth 1 -type d -name '*.partial.*' -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: source drift left a partial bundle"
  exit 1
fi

base_drift_repo="$(make_case base-commit-drift)"
base_drift_marker="$LAB/base-commit-drift-build-started"
base_drift_log="$LAB/base-commit-drift.log"
base_drift_rc=0
(
  cd "$base_drift_repo"
  env PATH="$base_drift_repo/fakebin:$PATH" \
    FAKE_BUILD_DELAY=0.5 FAKE_BUILD_MARKER="$base_drift_marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$base_drift_log" 2>&1 &
base_drift_pid=$!
for _ in $(seq 1 100); do
  [ -e "$base_drift_marker" ] && break
  sleep 0.02
done
[ -e "$base_drift_marker" ] || { echo "FAIL: base-drift build never started"; kill "$base_drift_pid" 2>/dev/null || true; exit 1; }
git -C "$base_drift_repo" commit --allow-empty -qm concurrent-empty-commit
wait "$base_drift_pid" || base_drift_rc=$?
[ "$base_drift_rc" -ne 0 ] || { echo "FAIL: base-commit drift published an evidence bundle"; exit 1; }
rg -q "candidate source changed while prerequisites ran: base-commit.txt" "$base_drift_log" \
  || { echo "FAIL: base-commit drift was not isolated"; cat "$base_drift_log"; exit 1; }

index_drift_repo="$(make_case index-status-drift)"
printf 'dirty captured candidate\n' > "$index_drift_repo/candidate.txt"
index_drift_marker="$LAB/index-status-drift-build-started"
index_drift_log="$LAB/index-status-drift.log"
index_drift_rc=0
(
  cd "$index_drift_repo"
  env PATH="$index_drift_repo/fakebin:$PATH" \
    FAKE_BUILD_DELAY=0.5 FAKE_BUILD_MARKER="$index_drift_marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$index_drift_log" 2>&1 &
index_drift_pid=$!
for _ in $(seq 1 100); do
  [ -e "$index_drift_marker" ] && break
  sleep 0.02
done
[ -e "$index_drift_marker" ] || { echo "FAIL: index-drift build never started"; kill "$index_drift_pid" 2>/dev/null || true; exit 1; }
git -C "$index_drift_repo" add candidate.txt
wait "$index_drift_pid" || index_drift_rc=$?
[ "$index_drift_rc" -ne 0 ] || { echo "FAIL: index/status drift published an evidence bundle"; exit 1; }
rg -q "candidate source changed while prerequisites ran: status-before-commit.txt" "$index_drift_log" \
  || { echo "FAIL: index/status drift was not isolated"; cat "$index_drift_log"; exit 1; }

aba_repo="$(make_case checkout-aba)"
aba_build_marker="$LAB/checkout-aba-build-started"
aba_measured_marker="$LAB/checkout-aba-measured"
aba_log="$LAB/checkout-aba.log"
(
  cd "$aba_repo"
  env PATH="$aba_repo/fakebin:$PATH" \
    FAKE_BUILD_MARKER="$aba_build_marker" FAKE_MEASURE_DELAY=0.2 \
    FAKE_MEASURE_FILE=candidate.txt FAKE_MEASURED_MARKER="$aba_measured_marker" \
    FAKE_BUILD_DELAY=0.3 ./scripts/seal-evidence.sh FG-223-PROOF
) > "$aba_log" 2>&1 &
aba_pid=$!
for _ in $(seq 1 100); do
  [ -e "$aba_build_marker" ] && break
  sleep 0.02
done
[ -e "$aba_build_marker" ] || { echo "FAIL: checkout-ABA build never started"; kill "$aba_pid" 2>/dev/null || true; exit 1; }
printf 'transient checkout bytes\n' > "$aba_repo/candidate.txt"
for _ in $(seq 1 100); do
  [ -e "$aba_measured_marker" ] && break
  sleep 0.02
done
[ -e "$aba_measured_marker" ] || { echo "FAIL: checkout-ABA build never measured"; kill "$aba_pid" 2>/dev/null || true; exit 1; }
printf 'stable candidate\n' > "$aba_repo/candidate.txt"
wait "$aba_pid"
aba_bundle="$(find "$aba_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
[ -n "$aba_bundle" ] || { echo "FAIL: checkout-ABA control published no bundle"; exit 1; }
rg -q '^MEASURED: stable candidate$' "$aba_bundle/build.log" \
  || { echo "FAIL: transient publishing-checkout bytes reached the measured snapshot"; cat "$aba_bundle/build.log"; exit 1; }
if rg -q 'transient checkout bytes' "$aba_bundle/build.log"; then
  echo "FAIL: checkout-ABA bytes contaminated the measured snapshot"
  exit 1
fi
(
  cd "$aba_bundle"
  sha256sum -c SHA256SUMS >/dev/null
)

binary_repo="$(make_case binary-candidate)"
printf '\0changed binary candidate\376\n' > "$binary_repo/candidate.bin"
binary_expected="$(sha256sum "$binary_repo/candidate.bin" | cut -d' ' -f1)"
(
  cd "$binary_repo"
  env PATH="$binary_repo/fakebin:$PATH" ./scripts/seal-evidence.sh FG-223-PROOF > "$LAB/binary-candidate.log"
)
binary_bundle="$(find "$binary_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
[ -n "$binary_bundle" ] || { echo "FAIL: binary candidate published no bundle"; exit 1; }
rg -q '^GIT binary patch$' "$binary_bundle/candidate.diff" \
  || { echo "FAIL: candidate diff did not bind dirty binary bytes"; exit 1; }
rg -q '^index [0-9a-f]{40}\.\.[0-9a-f]{40}( |$)' "$binary_bundle/candidate.diff" \
  || { echo "FAIL: candidate diff did not bind full binary object identities"; exit 1; }
binary_replay="$LAB/binary-candidate-replay"
git clone -q "$binary_repo" "$binary_replay"
git -C "$binary_replay" checkout -q "$(cat "$binary_bundle/base-commit.txt")"
git -C "$binary_replay" apply --binary "$binary_bundle/candidate.diff"
binary_actual="$(sha256sum "$binary_replay/candidate.bin" | cut -d' ' -f1)"
[ "$binary_actual" = "$binary_expected" ] \
  || { echo "FAIL: candidate diff did not reconstruct the measured binary bytes"; exit 1; }
(
  cd "$binary_bundle"
  sha256sum -c SHA256SUMS >/dev/null
)

dirty_text_repo="$(make_case dirty-text-candidate)"
printf 'dirty measured candidate\n' > "$dirty_text_repo/candidate.txt"
(
  cd "$dirty_text_repo"
  env PATH="$dirty_text_repo/fakebin:$PATH" FAKE_MEASURE_FILE=candidate.txt \
    ./scripts/seal-evidence.sh FG-223-PROOF > "$LAB/dirty-text-candidate.log"
)
dirty_text_bundle="$(find "$dirty_text_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
rg -q '^MEASURED: dirty measured candidate$' "$dirty_text_bundle/build.log" \
  || { echo "FAIL: materialized build did not consume dirty candidate bytes"; cat "$dirty_text_bundle/build.log"; exit 1; }

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
corpus_pwd="$(sed -n 's/^CORPUS_PWD=//p' "$bundle/corpus-gate.log")"
build_pwd="$(sed -n 's/^BUILD_PWD=//p' "$bundle/build.log")"
test_pwd="$(sed -n 's/^.*TEST_PWD=//p' "$bundle"/tests-*.log)"
if [ -z "$corpus_pwd" ] || [ "$corpus_pwd" != "$build_pwd" ] || [ "$build_pwd" != "$test_pwd" ]; then
  echo "FAIL: corpus/build/test did not share one materialized candidate: $corpus_pwd | $build_pwd | $test_pwd"
  exit 1
fi
[ "$build_pwd" != "$success_repo" ] \
  || { echo "FAIL: prerequisites executed from the publishing checkout"; exit 1; }

echo "FG-223 evidence sealer proof: PASS (permissive control rejected; prerequisite, snapshot-mutation, empty-inventory, input-boundary, tracked, staging-state, HEAD and atomic-move failures publish no manifest; every tracked tests/**/*.fsproj runs; dirty text and binary bytes reach/reconstruct from one isolated source; checkout ABA cannot contaminate it; empty and populated destinations are preserved; one concurrent publisher wins; success verifies)"
