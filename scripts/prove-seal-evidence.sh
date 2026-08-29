#!/usr/bin/env bash
# FG-223. Black-box proof that evidence sealing fails closed and publishes atomically.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="$ROOT/scripts/seal-evidence.sh"
REAL_CP="$(command -v cp)"
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
    '    [ -z "${FAKE_ASSERT_ABSENT:-}" ] || { [ ! -e "$FAKE_ASSERT_ABSENT" ] || { echo "unexpected candidate path: $FAKE_ASSERT_ABSENT"; exit 96; }; echo "ABSENT: $FAKE_ASSERT_ABSENT"; }' \
    '    [ -z "${FAKE_ASSERT_SYMLINK:-}" ] || { [ -L "$FAKE_ASSERT_SYMLINK" ] || { echo "missing candidate symlink: $FAKE_ASSERT_SYMLINK"; exit 95; }; echo "SYMLINK: $FAKE_ASSERT_SYMLINK"; }' \
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

late_untracked_repo="$(make_case late-untracked-source)"
printf 'caller measurement\n' > "$late_untracked_repo/measurement.log"
# shellcheck disable=SC2016 # Expansion belongs to the generated fake, not this proof.
{
  printf '%s\n' \
    '#!/usr/bin/env bash' \
    '[ -z "${FAKE_CP_MARKER:-}" ] || : > "$FAKE_CP_MARKER"' \
    'sleep "${FAKE_CP_DELAY:-0}"'
  printf 'exec %q "$@"\n' "$REAL_CP"
} > "$late_untracked_repo/fakebin/cp"
chmod +x "$late_untracked_repo/fakebin/cp"
(
  cd "$late_untracked_repo"
  git add fakebin/cp
  git commit -qm add-delayed-copy
)
late_untracked_marker="$LAB/late-untracked-copy-started"
late_untracked_log="$LAB/late-untracked-source.log"
late_untracked_rc=0
(
  cd "$late_untracked_repo"
  env PATH="$late_untracked_repo/fakebin:$PATH" \
    FAKE_CP_MARKER="$late_untracked_marker" FAKE_CP_DELAY=0.5 \
    ./scripts/seal-evidence.sh FG-223-PROOF measurement.log
) > "$late_untracked_log" 2>&1 &
late_untracked_pid=$!
for _ in $(seq 1 100); do
  [ -e "$late_untracked_marker" ] && break
  sleep 0.02
done
[ -e "$late_untracked_marker" ] \
  || { echo "FAIL: late-untracked capture window never opened"; kill "$late_untracked_pid" 2>/dev/null || true; exit 1; }
printf 'late source bytes\n' > "$late_untracked_repo/late-source.fs"
wait "$late_untracked_pid" || late_untracked_rc=$?
[ "$late_untracked_rc" -ne 0 ] \
  || { echo "FAIL: untracked source created after preflight was omitted from a published bundle"; exit 1; }
rg -q "untracked files would be omitted from the evidence" "$late_untracked_log" \
  || { echo "FAIL: late untracked source was not diagnosed"; cat "$late_untracked_log"; exit 1; }
rg -q "late-source.fs" "$late_untracked_log" \
  || { echo "FAIL: late untracked diagnostic did not name the source path"; cat "$late_untracked_log"; exit 1; }
if find "$late_untracked_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: late untracked source published a manifest"
  exit 1
fi
if find "$late_untracked_repo/evidence" -maxdepth 1 -type d -name '*.partial.*' -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: late untracked source left a partial bundle"
  exit 1
fi

hook_repo="$(make_case post-checkout-hook)"
mkdir -p "$hook_repo/.githooks"
# shellcheck disable=SC2016 # Expansion belongs to the generated hostile hook.
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'printf "hook-created ignored input\n" > "$PWD/.hook-input"' \
  > "$hook_repo/.githooks/post-checkout"
chmod +x "$hook_repo/.githooks/post-checkout"
printf '.hook-input\n' >> "$hook_repo/.gitignore"
(
  cd "$hook_repo"
  git add .githooks/post-checkout .gitignore
  git commit -qm add-hostile-post-checkout
  git config core.hooksPath .githooks
)
hook_control_parent="$LAB/post-checkout-control"
mkdir "$hook_control_parent"
git -C "$hook_repo" worktree add --detach "$hook_control_parent/source" HEAD \
  > "$LAB/post-checkout-control.log" 2>&1
[ -f "$hook_control_parent/source/.hook-input" ] \
  || { echo "FAIL: hostile post-checkout control did not create ignored input"; cat "$LAB/post-checkout-control.log"; exit 1; }
git -C "$hook_repo" worktree remove --force "$hook_control_parent/source"
rmdir "$hook_control_parent"
(
  cd "$hook_repo"
  env PATH="$hook_repo/fakebin:$PATH" FAKE_ASSERT_ABSENT=.hook-input \
    ./scripts/seal-evidence.sh FG-223-PROOF > "$LAB/post-checkout-hook.log"
)
hook_bundle="$(find "$hook_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
[ -n "$hook_bundle" ] || { echo "FAIL: hook-hardened materialization published no bundle"; exit 1; }
rg -q '^ABSENT: .hook-input$' "$hook_bundle/build.log" \
  || { echo "FAIL: hostile post-checkout input reached the materialized build"; cat "$hook_bundle/build.log"; exit 1; }
[ ! -e "$hook_repo/.hook-input" ] \
  || { echo "FAIL: hostile post-checkout hook mutated the publishing checkout"; exit 1; }

filter_repo="$(make_case checkout-content-filter)"
printf 'candidate.txt filter=unspecified\n' > "$filter_repo/.gitattributes"
(
  cd "$filter_repo"
  git add .gitattributes
  git commit -qm add-hostile-filter-attribute
)
filter_clean="$LAB/filter-clean"
filter_smudge="$LAB/filter-smudge"
# shellcheck disable=SC2016 # Expansion belongs to the generated hostile filter.
printf '%s\n' \
  '#!/usr/bin/env bash' \
  '[ -z "${FG223_FILTER_CLEAN_MARKER:-}" ] || : > "$FG223_FILTER_CLEAN_MARKER"' \
  "exec sed 's/SMUDGED UNBOUND/stable candidate/'" > "$filter_clean"
# shellcheck disable=SC2016 # Expansion belongs to the generated hostile filter.
printf '%s\n' \
  '#!/usr/bin/env bash' \
  '[ -z "${FG223_FILTER_SMUDGE_MARKER:-}" ] || : > "$FG223_FILTER_SMUDGE_MARKER"' \
  "exec sed 's/stable candidate/SMUDGED UNBOUND/'" > "$filter_smudge"
chmod +x "$filter_clean" "$filter_smudge"
git -C "$filter_repo" config filter.unspecified.clean "$filter_clean"
git -C "$filter_repo" config filter.unspecified.smudge "$filter_smudge"
git -C "$filter_repo" config filter.unspecified.required true
filter_control_parent="$LAB/filter-control"
filter_clean_marker="$LAB/filter-clean-executed"
filter_smudge_marker="$LAB/filter-smudge-executed"
mkdir "$filter_control_parent"
FG223_FILTER_CLEAN_MARKER="$filter_clean_marker" \
FG223_FILTER_SMUDGE_MARKER="$filter_smudge_marker" \
  git -C "$filter_repo" worktree add --detach "$filter_control_parent/source" HEAD \
  > "$LAB/filter-control.log" 2>&1
[ -e "$filter_smudge_marker" ] \
  || { echo "FAIL: hostile checkout filter control did not execute"; cat "$LAB/filter-control.log"; exit 1; }
rg -qx 'SMUDGED UNBOUND' "$filter_control_parent/source/candidate.txt" \
  || { echo "FAIL: hostile checkout filter control did not alter physical bytes"; exit 1; }
filter_control_status="$(
  FG223_FILTER_CLEAN_MARKER="$filter_clean_marker" \
    FG223_FILTER_SMUDGE_MARKER="$filter_smudge_marker" \
    git -C "$filter_control_parent/source" status --short
)"
[ -z "$filter_control_status" ] \
  || { echo "FAIL: hostile checkout filter control was not Git-clean"; exit 1; }
[ -e "$filter_clean_marker" ] \
  || { echo "FAIL: hostile clean-filter control did not execute"; exit 1; }
git -C "$filter_repo" worktree remove --force "$filter_control_parent/source"
rmdir "$filter_control_parent"
rm "$filter_clean_marker" "$filter_smudge_marker"
# The dirty candidate removes both the attribute and its target. Worktree add
# still checks out HEAD first, so a current-state-only audit would execute the
# base smudge and then delete its raw-identity witness while applying the patch.
: > "$filter_repo/.gitattributes"
rm "$filter_repo/candidate.txt"
filter_log="$LAB/checkout-content-filter.log"
filter_rc=0
(
  cd "$filter_repo"
  env PATH="$filter_repo/fakebin:$PATH" \
    FG223_FILTER_CLEAN_MARKER="$filter_clean_marker" \
    FG223_FILTER_SMUDGE_MARKER="$filter_smudge_marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$filter_log" 2>&1 || filter_rc=$?
[ "$filter_rc" -ne 0 ] || { echo "FAIL: effective checkout filter was accepted"; exit 1; }
rg -q "publishing checkout has configured Git content filters" "$filter_log" \
  || { echo "FAIL: effective checkout filter was not diagnosed"; cat "$filter_log"; exit 1; }
if [ -e "$filter_clean_marker" ] || [ -e "$filter_smudge_marker" ]; then
  echo "FAIL: evidence sealer executed a hostile checkout filter before refusal"
  exit 1
fi
if find "$filter_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: effective checkout filter published a manifest"
  exit 1
fi

conditional_filter_repo="$(make_case conditional-checkout-filter)"
printf 'candidate.txt filter=unspecified\n' > "$conditional_filter_repo/.gitattributes"
git -C "$conditional_filter_repo" add .gitattributes
git -C "$conditional_filter_repo" commit -qm add-conditional-filter-attribute
conditional_filter_marker="$LAB/conditional-filter.marker"
conditional_filter_driver="$LAB/conditional-filter-driver"
conditional_filter_config="$LAB/conditional-filter.config"
# shellcheck disable=SC2016 # Marker expansion belongs to the hostile driver.
printf '%s\n' '#!/usr/bin/env bash' 'tee' ': > "$FG223_CONDITIONAL_FILTER_MARKER"' \
  > "$conditional_filter_driver"
chmod +x "$conditional_filter_driver"
git config --file "$conditional_filter_config" filter.unspecified.clean "$conditional_filter_driver"
git config --file "$conditional_filter_config" filter.unspecified.smudge "$conditional_filter_driver"
git config --file "$conditional_filter_config" filter.unspecified.required true
git -C "$conditional_filter_repo" config --local \
  'includeIf.gitdir:**/worktrees/source.path' "$conditional_filter_config"
if git -C "$conditional_filter_repo" config \
  --get-regexp '^filter\..*\.(clean|smudge|process)$' >/dev/null; then
  echo "FAIL: conditional filter unexpectedly appeared in the publishing checkout"
  exit 1
fi
conditional_control_parent="$(mktemp -d "$LAB/conditional-filter-control.XXXXXX")"
env FG223_CONDITIONAL_FILTER_MARKER="$conditional_filter_marker" \
  git -C "$conditional_filter_repo" worktree add --detach \
  "$conditional_control_parent/source" HEAD >/dev/null
[ -e "$conditional_filter_marker" ] \
  || { echo "FAIL: linked-worktree conditional filter control did not execute"; exit 1; }
git -C "$conditional_filter_repo" worktree remove --force "$conditional_control_parent/source"
rmdir "$conditional_control_parent"
rm "$conditional_filter_marker"
conditional_filter_rc=0
(
  cd "$conditional_filter_repo"
  env PATH="$conditional_filter_repo/fakebin:$PATH" \
    FG223_CONDITIONAL_FILTER_MARKER="$conditional_filter_marker" \
    FAKE_BUILD_MARKER="$LAB/conditional-filter-build.marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$LAB/conditional-checkout-filter.log" 2>&1 || conditional_filter_rc=$?
[ "$conditional_filter_rc" -ne 0 ] \
  || { echo "FAIL: linked-worktree conditional filter was accepted"; exit 1; }
rg -q 'materialized worktree has configured Git content filters' \
  "$LAB/conditional-checkout-filter.log" \
  || { echo "FAIL: linked-worktree conditional filter was not diagnosed"; cat "$LAB/conditional-checkout-filter.log"; exit 1; }
[ ! -e "$conditional_filter_marker" ] \
  || { echo "FAIL: conditional filter executed before linked-worktree refusal"; exit 1; }
[ ! -e "$LAB/conditional-filter-build.marker" ] \
  || { echo "FAIL: build ran before linked-worktree conditional-filter refusal"; exit 1; }
if find "$conditional_filter_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: linked-worktree conditional filter published a manifest"
  exit 1
fi

ident_repo="$(make_case checkout-ident-transform)"
# shellcheck disable=SC2016 # The fixture must contain the literal ident marker.
printf '$Id$\n' > "$ident_repo/ident.txt"
printf 'ident.txt ident\n' > "$ident_repo/.gitattributes"
(
  cd "$ident_repo"
  git add ident.txt .gitattributes
  git commit -qm add-ident-transform
)
ident_control_parent="$LAB/ident-control"
mkdir "$ident_control_parent"
git -C "$ident_repo" worktree add --detach "$ident_control_parent/source" HEAD \
  > "$LAB/ident-control.log" 2>&1
rg -q '^\$Id: [0-9a-f]+ \$$' "$ident_control_parent/source/ident.txt" \
  || { echo "FAIL: ident checkout control did not alter physical bytes"; cat "$ident_control_parent/source/ident.txt"; exit 1; }
[ -z "$(git -C "$ident_control_parent/source" status --short)" ] \
  || { echo "FAIL: ident checkout control was not Git-clean"; exit 1; }
git -C "$ident_repo" worktree remove --force "$ident_control_parent/source"
rmdir "$ident_control_parent"
ident_log="$LAB/checkout-ident-transform.log"
ident_build_marker="$LAB/ident-build-started"
ident_rc=0
(
  cd "$ident_repo"
  env PATH="$ident_repo/fakebin:$PATH" FAKE_BUILD_MARKER="$ident_build_marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$ident_log" 2>&1 || ident_rc=$?
[ "$ident_rc" -ne 0 ] || { echo "FAIL: Git-clean raw checkout transform was accepted"; exit 1; }
rg -q "materialized candidate raw tracked bytes do not match the candidate index: ident.txt" "$ident_log" \
  || { echo "FAIL: raw checkout transform was not diagnosed"; cat "$ident_log"; exit 1; }
[ ! -e "$ident_build_marker" ] \
  || { echo "FAIL: raw checkout transform reached the build before refusal"; exit 1; }
if find "$ident_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: raw checkout transform published a manifest"
  exit 1
fi

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

deleted_repo="$(make_case unstaged-tracked-deletion)"
rm "$deleted_repo/candidate.txt"
# An empty directory at the deleted path is invisible to Git. This kills a
# physical `-e` inventory filter while the private candidate index stays exact.
mkdir "$deleted_repo/candidate.txt"
(
  cd "$deleted_repo"
  env PATH="$deleted_repo/fakebin:$PATH" FAKE_ASSERT_ABSENT=candidate.txt \
    ./scripts/seal-evidence.sh FG-223-PROOF > "$LAB/unstaged-tracked-deletion.log"
)
deleted_bundle="$(find "$deleted_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
[ -n "$deleted_bundle" ] || { echo "FAIL: unstaged tracked deletion published no bundle"; exit 1; }
rg -q '^deleted file mode ' "$deleted_bundle/candidate.diff" \
  || { echo "FAIL: candidate diff did not bind the unstaged tracked deletion"; exit 1; }
if rg -qx 'candidate.txt' "$deleted_bundle/tree.txt"; then
  echo "FAIL: candidate inventory retained an unstaged tracked deletion"
  exit 1
fi
rg -q '^ABSENT: candidate.txt$' "$deleted_bundle/build.log" \
  || { echo "FAIL: materialized build did not observe the tracked deletion"; cat "$deleted_bundle/build.log"; exit 1; }
[ -d "$deleted_repo/candidate.txt" ] \
  || { echo "FAIL: sealing mutated the publishing checkout's empty replacement directory"; exit 1; }
(
  cd "$deleted_bundle"
  sha256sum -c SHA256SUMS >/dev/null
)

staged_repo="$(make_case staged-inventory-transitions)"
printf 'staged candidate\n' > "$staged_repo/staged.txt"
ln -s missing-target "$staged_repo/broken.link"
(
  cd "$staged_repo"
  git add staged.txt broken.link
  git rm -q candidate.txt
  env PATH="$staged_repo/fakebin:$PATH" \
    FAKE_MEASURE_FILE=staged.txt FAKE_ASSERT_ABSENT=candidate.txt \
    FAKE_ASSERT_SYMLINK=broken.link \
    ./scripts/seal-evidence.sh FG-223-PROOF > "$LAB/staged-inventory-transitions.log"
)
staged_bundle="$(find "$staged_repo/evidence" -mindepth 1 -maxdepth 1 -type d -name '*-fg-223-proof' -print -quit)"
[ -n "$staged_bundle" ] || { echo "FAIL: staged candidate transitions published no bundle"; exit 1; }
rg -qx 'staged.txt' "$staged_bundle/tree.txt" \
  || { echo "FAIL: candidate inventory omitted a staged addition"; exit 1; }
rg -qx 'broken.link' "$staged_bundle/tree.txt" \
  || { echo "FAIL: candidate inventory omitted a staged broken symlink"; exit 1; }
if rg -qx 'candidate.txt' "$staged_bundle/tree.txt"; then
  echo "FAIL: candidate inventory retained a staged deletion"
  exit 1
fi
rg -q '^new file mode 120000$' "$staged_bundle/candidate.diff" \
  || { echo "FAIL: candidate diff did not bind the staged broken symlink"; exit 1; }
rg -q '^MEASURED: staged candidate$' "$staged_bundle/build.log" \
  || { echo "FAIL: materialized build did not consume the staged addition"; cat "$staged_bundle/build.log"; exit 1; }
rg -q '^ABSENT: candidate.txt$' "$staged_bundle/build.log" \
  || { echo "FAIL: materialized build did not observe the staged deletion"; cat "$staged_bundle/build.log"; exit 1; }
rg -q '^SYMLINK: broken.link$' "$staged_bundle/build.log" \
  || { echo "FAIL: materialized build did not observe the staged broken symlink"; cat "$staged_bundle/build.log"; exit 1; }

external_symlink_repo="$(make_case external-symlink)"
external_target="$LAB/external-source.txt"
printf 'mutable external input\n' > "$external_target"
ln -s "$external_target" "$external_symlink_repo/escape.link"
(
  cd "$external_symlink_repo"
  git add escape.link
  set +e
  env PATH="$external_symlink_repo/fakebin:$PATH" \
    FAKE_BUILD_MARKER="$LAB/external-symlink-build.marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF \
    > "$LAB/external-symlink.log" 2>&1
  external_symlink_rc=$?
  set -e
  [ "$external_symlink_rc" -ne 0 ] \
    || { echo "FAIL: a tracked external symlink was accepted"; exit 1; }
)
rg -q 'tracked symlink has an absolute target: escape.link' "$LAB/external-symlink.log" \
  || { echo "FAIL: external symlink refusal diagnostic was not specific"; cat "$LAB/external-symlink.log"; exit 1; }
[ ! -e "$LAB/external-symlink-build.marker" ] \
  || { echo "FAIL: prerequisites ran before the external symlink refusal"; exit 1; }
if find "$external_symlink_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: external symlink refusal published a bundle"
  exit 1
fi

gitmeta_repo="$(make_case git-admin-symlink)"
ln -s .git "$gitmeta_repo/gitmeta.link"
git -C "$gitmeta_repo" add gitmeta.link
git -C "$gitmeta_repo" commit -qm add-git-admin-symlink
gitmeta_control_parent="$(mktemp -d "$LAB/git-admin-control.XXXXXX")"
git -C "$gitmeta_repo" worktree add --detach "$gitmeta_control_parent/source" HEAD >/dev/null
rg -q '^gitdir: ' "$gitmeta_control_parent/source/gitmeta.link" \
  || { echo "FAIL: Git-admin symlink control did not expose linked-worktree metadata"; exit 1; }
git -C "$gitmeta_repo" worktree remove --force "$gitmeta_control_parent/source"
rmdir "$gitmeta_control_parent"
gitmeta_rc=0
(
  cd "$gitmeta_repo"
  env PATH="$gitmeta_repo/fakebin:$PATH" \
    FAKE_BUILD_MARKER="$LAB/git-admin-symlink-build.marker" \
    ./scripts/seal-evidence.sh FG-223-PROOF
) > "$LAB/git-admin-symlink.log" 2>&1 || gitmeta_rc=$?
[ "$gitmeta_rc" -ne 0 ] \
  || { echo "FAIL: a tracked Git-admin symlink was accepted"; exit 1; }
rg -q 'tracked symlink enters the Git administrative namespace: gitmeta.link' \
  "$LAB/git-admin-symlink.log" \
  || { echo "FAIL: Git-admin symlink refusal diagnostic was not specific"; cat "$LAB/git-admin-symlink.log"; exit 1; }
[ ! -e "$LAB/git-admin-symlink-build.marker" ] \
  || { echo "FAIL: prerequisites ran before the Git-admin symlink refusal"; exit 1; }
if find "$gitmeta_repo/evidence" -type f -name SHA256SUMS -print -quit 2>/dev/null | rg -q .; then
  echo "FAIL: Git-admin symlink refusal published a bundle"
  exit 1
fi

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

echo "FG-223 evidence sealer proof: PASS (permissive control rejected; prerequisite, snapshot-mutation, empty-inventory, input-boundary, late-untracked, post-checkout-hook, executable-filter, raw-transform, external/Git-admin-symlink, tracked, staging-state, HEAD and atomic-move failures publish no manifest; every tracked tests/**/*.fsproj runs; dirty text, binary bytes, unstaged/staged deletions, staged additions, and a confined broken symlink reach/reconstruct from one hook-free raw-identity-audited isolated source; checkout ABA cannot contaminate it; empty and populated destinations are preserved; one concurrent publisher wins; success verifies)"
