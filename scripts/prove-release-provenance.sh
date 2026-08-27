#!/usr/bin/env bash
# FG-093. Offline proof that the provenance gate refuses every planted mismatch
# before the downstream marker can run.  The checker is copied into a scratch
# Git repository and committed there, so HEAD/tree/cleanliness checks are real.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT=$PWD
CHECKER="$ROOT/scripts/release-provenance-gate.py"
if [ ! -f "$CHECKER" ] || [ ! -x "$CHECKER" ] || [ -L "$CHECKER" ]; then
  echo 'FG-093 proof REFUSED: repository checker is not an executable, non-symlink regular file' >&2
  exit 1
fi
LAB=$(mktemp -d /tmp/fogell-provenance-proof.XXXXXX)
trap 'rm -rf "$LAB"' EXIT
REPO="$LAB/repo"
SUBMODULE_SOURCE="$LAB/submodule-source"
NESTED_SOURCE="$LAB/nested-source"
OUT="$LAB/output"
ARTIFACT="$OUT/fogell-release.bin"
GOOD="$OUT/good.json"
MARKER="$OUT/downstream-ran"
FAILED=0

mkdir -p "$REPO/scripts" "$REPO/corpus" "$SUBMODULE_SOURCE" "$NESTED_SOURCE" "$OUT"
git -C "$NESTED_SOURCE" init -q
git -C "$NESTED_SOURCE" config user.name fogell-proof
git -C "$NESTED_SOURCE" config user.email fogell-proof@example.invalid
printf 'nested clean\n' > "$NESTED_SOURCE/nested.txt"
git -C "$NESTED_SOURCE" add nested.txt
git -C "$NESTED_SOURCE" commit -q -m base

git -C "$SUBMODULE_SOURCE" init -q
git -C "$SUBMODULE_SOURCE" config user.name fogell-proof
git -C "$SUBMODULE_SOURCE" config user.email fogell-proof@example.invalid
printf 'submodule clean\n' > "$SUBMODULE_SOURCE/submodule.txt"
git -C "$SUBMODULE_SOURCE" -c protocol.file.allow=always submodule add -q "$NESTED_SOURCE" vendor/nested-fixture
git -C "$SUBMODULE_SOURCE" add submodule.txt .gitmodules vendor/nested-fixture
git -C "$SUBMODULE_SOURCE" commit -q -m base

cp -p "$CHECKER" "$REPO/scripts/release-provenance-gate.py"
printf 'pinned corpus rows\n' > "$REPO/corpus/CORPUS-SHA256SUMS"
printf 'base\n' > "$REPO/tracked.txt"
printf 'NUL-safe path bytes\n' > "$REPO/"$'tracked\nnewline.txt'
ln -s tracked.txt "$REPO/tracked-link"
git -C "$REPO" init -q
git -C "$REPO" config user.name fogell-proof
git -C "$REPO" config user.email fogell-proof@example.invalid
git -C "$REPO" -c protocol.file.allow=always submodule add -q "$SUBMODULE_SOURCE" vendor/provenance-fixture
git -C "$REPO" add scripts/release-provenance-gate.py corpus/CORPUS-SHA256SUMS tracked.txt \
  $'tracked\nnewline.txt' tracked-link .gitmodules vendor/provenance-fixture
git -C "$REPO" commit -q -m base
git -C "$REPO" -c protocol.file.allow=always submodule update --init --recursive -q
printf 'target\n' >> "$REPO/tracked.txt"
git -C "$REPO" add tracked.txt
git -C "$REPO" commit -q -m target
printf 'deterministic release artifact bytes\n' > "$ARTIFACT"
CLEAN_FILTER="$OUT/constant-clean-filter.sh"
printf '#!/bin/sh\nprintf "base\\ntarget\\n"\n' > "$CLEAN_FILTER"
chmod +x "$CLEAN_FILTER"

sha256() { sha256sum "$1" | awk '{print $1}'; }

write_manifest() {
  local path=$1 commit=$2 tree=$3 artifact_sha=$4 corpus_sha=$5 schema_version=${6:-1}
  printf '{"schema_version":%s,"commit":"%s","tree":"%s","artifact_sha256":"%s","corpus_manifest_sha256":"%s"}\n' \
    "$schema_version" "$commit" "$tree" "$artifact_sha" "$corpus_sha" > "$path"
}

identity() {
  local repo=$1
  COMMIT=$(git -C "$repo" rev-parse HEAD)
  TREE=$(git -C "$repo" rev-parse 'HEAD^{tree}')
  ARTIFACT_SHA=$(sha256 "$ARTIFACT")
  CORPUS_SHA=$(sha256 "$repo/corpus/CORPUS-SHA256SUMS")
}

clone_fixture() {
  local destination=$1
  git -c protocol.file.allow=always clone -q --recurse-submodules "$REPO" "$destination"
  git -C "$destination" config user.name fogell-proof
  git -C "$destination" config user.email fogell-proof@example.invalid
}

plant_clean_filter_attack() {
  local repo=$1
  git -C "$repo" config filter.constant.clean "$CLEAN_FILTER"
  git -C "$repo" config filter.constant.smudge cat
  printf '/tracked.txt filter=constant\n' > "$repo/.git/info/attributes"
  printf 'EVIL RAW BYTES\n' > "$repo/tracked.txt"
  git -C "$repo" add tracked.txt
  if [ -n "$(git -C "$repo" status --porcelain=v1 --untracked-files=all)" ] || \
     [ "$(git -C "$repo" ls-files -v tracked.txt)" != 'H tracked.txt' ] || \
     [ "$(git -C "$repo" show :tracked.txt)" != $'base\ntarget' ] || \
     ! grep -Fqx 'EVIL RAW BYTES' "$repo/tracked.txt"; then
    echo '  FAIL: clean-filter attack preconditions were not established'
    FAILED=1
    return 1
  fi
}

clear_clean_filter_attack() {
  local repo=$1
  rm -f "$repo/tracked.txt"
  git -C "$repo" checkout-index --force -- tracked.txt
  rm -f "$repo/.git/info/attributes"
  git -C "$repo" config --remove-section filter.constant
  if [ "$(git -C "$repo" hash-object tracked.txt)" != "$(git -C "$repo" rev-parse :tracked.txt)" ]; then
    echo '  FAIL: clean-filter attack cleanup did not restore tracked bytes'
    FAILED=1
    return 1
  fi
}

run_gate() {
  local checker=$1 manifest=$2 artifact=$3 marker=$4 log=$5
  rm -f "$marker"
  set +e
  "$checker" --manifest "$manifest" --artifact "$artifact" -- /usr/bin/touch "$marker" > "$log" 2>&1
  RC=$?
  set -e
}

expect_accept() {
  local label=$1 checker=$2 manifest=$3 artifact=$4
  run_gate "$checker" "$manifest" "$artifact" "$MARKER" "$OUT/run.log"
  if [ "$RC" -ne 0 ] || [ ! -f "$MARKER" ] || ! grep -Fq 'PROVENANCE VERIFIED:' "$OUT/run.log"; then
    echo "  FAIL: $label did not verify and execute exactly once"
    sed 's/^/    | /' "$OUT/run.log"
    FAILED=1
  else
    echo "  accepted $label; downstream marker ran"
  fi
}

expect_reject() {
  local label=$1 checker=$2 manifest=$3 artifact=$4 wanted=$5
  run_gate "$checker" "$manifest" "$artifact" "$MARKER" "$OUT/run.log"
  if [ "$RC" -eq 0 ]; then
    echo "  FAIL: $label was accepted"
    FAILED=1
  elif [ -f "$MARKER" ]; then
    echo "  FAIL: $label ran the downstream command after refusal"
    FAILED=1
  elif ! grep -Fq "$wanted" "$OUT/run.log"; then
    echo "  FAIL: $label refused for the wrong reason; wanted: $wanted"
    sed 's/^/    | /' "$OUT/run.log"
    FAILED=1
  else
    echo "  rejected $label before downstream execution"
  fi
}

expect_reject_git_environment() {
  local label=$1 wanted=$2
  shift 2
  rm -f "$MARKER"
  set +e
  env "$@" "$REPO/scripts/release-provenance-gate.py" --manifest "$GOOD" --artifact "$ARTIFACT" -- \
    /usr/bin/touch "$MARKER" > "$OUT/git-environment.log" 2>&1
  RC=$?
  set -e
  if [ "$RC" -eq 0 ] || [ -f "$MARKER" ] || ! grep -Fq "$wanted" "$OUT/git-environment.log"; then
    echo "  FAIL: $label was not refused before downstream execution"
    sed 's/^/    | /' "$OUT/git-environment.log"
    FAILED=1
  else
    echo "  rejected $label; inherited Git environment could not hide dirt"
  fi
}

identity "$REPO"
write_manifest "$GOOD" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"

echo '=== FG-093 positive control ==='
expect_accept 'the exact clean tuple' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT"

MODE_ROOT="$LAB/non-executable-checker"
mkdir -p "$MODE_ROOT/scripts"
cp -p "$ROOT/scripts/prove-release-provenance.sh" "$MODE_ROOT/scripts/prove-release-provenance.sh"
cp -p "$CHECKER" "$MODE_ROOT/scripts/release-provenance-gate.py"
chmod -x "$MODE_ROOT/scripts/release-provenance-gate.py"
set +e
"$MODE_ROOT/scripts/prove-release-provenance.sh" > "$OUT/non-executable.log" 2>&1
MODE_RC=$?
set -e
if [ "$MODE_RC" -eq 0 ] || ! grep -Fq 'repository checker is not an executable, non-symlink regular file' "$OUT/non-executable.log"; then
  echo '  FAIL: proof accepted a non-executable repository checker'
  sed 's/^/    | /' "$OUT/non-executable.log"
  FAILED=1
else
  echo '  rejected non-executable repository checker before fixture setup'
fi

SYMLINK_ROOT="$LAB/symlink-checker"
mkdir -p "$SYMLINK_ROOT/scripts" "$SYMLINK_ROOT/target"
cp -p "$ROOT/scripts/prove-release-provenance.sh" "$SYMLINK_ROOT/scripts/prove-release-provenance.sh"
cp -p "$CHECKER" "$SYMLINK_ROOT/target/release-provenance-gate.py"
ln -s ../target/release-provenance-gate.py "$SYMLINK_ROOT/scripts/release-provenance-gate.py"
set +e
"$SYMLINK_ROOT/scripts/prove-release-provenance.sh" > "$OUT/symlink-checker.log" 2>&1
SYMLINK_RC=$?
set -e
if [ "$SYMLINK_RC" -eq 0 ] || ! grep -Fq 'repository checker is not an executable, non-symlink regular file' "$OUT/symlink-checker.log"; then
  echo '  FAIL: proof accepted a symlink repository checker'
  sed 's/^/    | /' "$OUT/symlink-checker.log"
  FAILED=1
else
  echo '  rejected symlink repository checker before fixture setup'
fi

ARGV_PROBE="$OUT/argv-probe.sh"
ARGV_CAPTURE="$OUT/argv-capture"
SHELL_MARKER="$OUT/shell-expanded"
printf '#!/bin/sh\nprintf "%%s" "$1" > "$2"\n' > "$ARGV_PROBE"
chmod +x "$ARGV_PROBE"
LITERAL_ARGUMENT="literal;\$(touch $SHELL_MARKER)"
rm -f "$ARGV_CAPTURE" "$SHELL_MARKER"
set +e
"$REPO/scripts/release-provenance-gate.py" --manifest "$GOOD" --artifact "$ARTIFACT" -- \
  "$ARGV_PROBE" "$LITERAL_ARGUMENT" "$ARGV_CAPTURE" > "$OUT/argv.log" 2>&1
RC=$?
set -e
if [ "$RC" -ne 0 ] || [ ! -f "$ARGV_CAPTURE" ] || [ "$(cat "$ARGV_CAPTURE")" != "$LITERAL_ARGUMENT" ] || [ -e "$SHELL_MARKER" ]; then
  echo '  FAIL: downstream arguments were not passed literally without a shell'
  sed 's/^/    | /' "$OUT/argv.log"
  FAILED=1
else
  echo '  accepted downstream argv literally; no shell expansion occurred'
fi

ENV_PROBE="$OUT/environment-probe.sh"
ENV_CAPTURE="$OUT/environment-capture"
ENV_EXPECTED="$OUT/environment-expected"
printf '%s\n' \
  '#!/bin/sh' \
  'case "${FOGELL_VERIFIED_ARTIFACT-}" in' \
  '  /proc/self/fd/[0-9]*) artifact_binding="<sealed-descriptor>" ;;' \
  '  *) artifact_binding="${FOGELL_VERIFIED_ARTIFACT-<unset>}" ;;' \
  'esac' \
  'artifact_bytes_sha=$(sha256sum "${FOGELL_VERIFIED_ARTIFACT-}" 2>/dev/null | awk '\''{print $1}'\'')' \
  'printf "%s\n" \' \
  '  "${FOGELL_VERIFIED_COMMIT-<unset>}" \' \
  '  "${FOGELL_VERIFIED_TREE-<unset>}" \' \
  '  "$artifact_binding" \' \
  '  "${FOGELL_VERIFIED_ARTIFACT_SHA256-<unset>}" \' \
  '  "${FOGELL_VERIFIED_CORPUS_MANIFEST_SHA256-<unset>}" \' \
  '  "$artifact_bytes_sha" \' \
  '  "${GIT_NO_REPLACE_OBJECTS-<unset>}" \' \
  '  "${GIT_DIR-<unset>}" \' \
  '  "${GIT_WORK_TREE-<unset>}" \' \
  '  "${GIT_INDEX_FILE-<unset>}" \' \
  '  "${GIT_CONFIG_COUNT-<unset>}" \' \
  '  "${GIT_CONFIG_KEY_0-<unset>}" \' \
  '  "${GIT_CONFIG_VALUE_0-<unset>}" > "$1"' > "$ENV_PROBE"
chmod +x "$ENV_PROBE"
printf '%s\n' "$COMMIT" "$TREE" '<sealed-descriptor>' "$ARTIFACT_SHA" "$CORPUS_SHA" "$ARTIFACT_SHA" \
  '1' '<unset>' '<unset>' '<unset>' '<unset>' '<unset>' '<unset>' > "$ENV_EXPECTED"
rm -f "$ENV_CAPTURE"
set +e
env FOGELL_VERIFIED_COMMIT=hostile FOGELL_VERIFIED_TREE=hostile \
  FOGELL_VERIFIED_ARTIFACT=/hostile FOGELL_VERIFIED_ARTIFACT_SHA256=hostile \
  FOGELL_VERIFIED_CORPUS_MANIFEST_SHA256=hostile GIT_DIR=/hostile/git-dir \
  GIT_WORK_TREE=/hostile/work-tree GIT_INDEX_FILE=/hostile/index \
  GIT_NO_REPLACE_OBJECTS=0 GIT_CONFIG_COUNT=1 \
  GIT_CONFIG_KEY_0=core.ignoreCase GIT_CONFIG_VALUE_0=true \
  "$REPO/scripts/release-provenance-gate.py" --manifest "$GOOD" --artifact "$ARTIFACT" -- \
  "$ENV_PROBE" "$ENV_CAPTURE" > "$OUT/environment.log" 2>&1
RC=$?
set -e
if [ "$RC" -ne 0 ] || [ ! -f "$ENV_CAPTURE" ] || ! cmp -s "$ENV_EXPECTED" "$ENV_CAPTURE"; then
  echo '  FAIL: downstream did not receive five exact bindings, immutable artifact bytes, and scrubbed Git state'
  sed 's/^/    | /' "$OUT/environment.log"
  [ ! -f "$ENV_CAPTURE" ] || sed 's/^/    | actual: /' "$ENV_CAPTURE"
  FAILED=1
else
  echo '  exported five exact bindings to immutable artifact bytes and removed inherited Git state downstream'
fi

ARTIFACT_SWAP_PROBE="$OUT/artifact-swap-probe.sh"
ARTIFACT_SWAP_CAPTURE="$OUT/artifact-swap.capture"
printf '%s\n' \
  '#!/bin/sh' \
  'if printf "in-place hostile bytes\n" > "$FOGELL_VERIFIED_ARTIFACT" 2>/dev/null; then exit 97; fi' \
  'printf "replacement artifact bytes\n" > "$1.replacement"' \
  'mv "$1.replacement" "$1"' \
  'sha256sum "$FOGELL_VERIFIED_ARTIFACT" | awk '\''{print $1}'\'' > "$2"' > "$ARTIFACT_SWAP_PROBE"
chmod +x "$ARTIFACT_SWAP_PROBE"
rm -f "$ARTIFACT_SWAP_CAPTURE"
set +e
"$REPO/scripts/release-provenance-gate.py" --manifest "$GOOD" --artifact "$ARTIFACT" -- \
  "$ARTIFACT_SWAP_PROBE" "$ARTIFACT" "$ARTIFACT_SWAP_CAPTURE" > "$OUT/artifact-swap.log" 2>&1
RC=$?
set -e
if [ "$RC" -ne 0 ] || [ ! -f "$ARTIFACT_SWAP_CAPTURE" ] || \
   [ "$(cat "$ARTIFACT_SWAP_CAPTURE")" != "$ARTIFACT_SHA" ] || \
   [ "$(sha256 "$ARTIFACT")" = "$ARTIFACT_SHA" ]; then
  echo '  FAIL: downstream did not consume the immutable verified bytes after an artifact path swap'
  sed 's/^/    | /' "$OUT/artifact-swap.log"
  FAILED=1
else
  echo '  bound downstream consumption to immutable verified bytes across an artifact path swap'
fi
printf 'deterministic release artifact bytes\n' > "$ARTIFACT"

echo '=== FG-093 four tuple mismatches ==='
write_manifest "$OUT/bad-commit.json" 0000000000000000000000000000000000000000 "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
expect_reject 'commit mismatch' "$REPO/scripts/release-provenance-gate.py" "$OUT/bad-commit.json" "$ARTIFACT" 'commit mismatch'

write_manifest "$OUT/bad-tree.json" "$COMMIT" 0000000000000000000000000000000000000000 "$ARTIFACT_SHA" "$CORPUS_SHA"
expect_reject 'tree mismatch' "$REPO/scripts/release-provenance-gate.py" "$OUT/bad-tree.json" "$ARTIFACT" 'tree mismatch'

write_manifest "$OUT/bad-artifact.json" "$COMMIT" "$TREE" "$(printf '0%.0s' {1..64})" "$CORPUS_SHA"
expect_reject 'artifact digest mismatch' "$REPO/scripts/release-provenance-gate.py" "$OUT/bad-artifact.json" "$ARTIFACT" 'artifact_sha256 mismatch'

write_manifest "$OUT/bad-corpus.json" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$(printf '0%.0s' {1..64})"
expect_reject 'corpus-manifest digest mismatch' "$REPO/scripts/release-provenance-gate.py" "$OUT/bad-corpus.json" "$ARTIFACT" 'corpus_manifest_sha256 mismatch'

echo '=== FG-093 strict manifest grammar ==='
write_manifest "$OUT/wrong-schema.json" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA" 2
expect_reject 'unknown schema version' "$REPO/scripts/release-provenance-gate.py" "$OUT/wrong-schema.json" "$ARTIFACT" 'schema_version must be exactly 1'

write_manifest "$OUT/bool-schema.json" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA" true
expect_reject 'boolean is not integer schema version' "$REPO/scripts/release-provenance-gate.py" "$OUT/bool-schema.json" "$ARTIFACT" 'schema_version must be the JSON integer 1'

write_manifest "$OUT/short.json" "${COMMIT:0:12}" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
expect_reject 'short commit' "$REPO/scripts/release-provenance-gate.py" "$OUT/short.json" "$ARTIFACT" 'commit must be exactly 40 lowercase'

write_manifest "$OUT/mixed-case.json" "${COMMIT^^}" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
expect_reject 'mixed-case commit guessed as lowercase' "$REPO/scripts/release-provenance-gate.py" "$OUT/mixed-case.json" "$ARTIFACT" 'commit must be exactly 40 lowercase'

write_manifest "$OUT/short-digest.json" "$COMMIT" "$TREE" "${ARTIFACT_SHA:0:12}" "$CORPUS_SHA"
expect_reject 'short artifact digest' "$REPO/scripts/release-provenance-gate.py" "$OUT/short-digest.json" "$ARTIFACT" 'artifact_sha256 must be exactly 64 lowercase'

write_manifest "$OUT/mixed-digest.json" "$COMMIT" "$TREE" "${ARTIFACT_SHA^^}" "$CORPUS_SHA"
expect_reject 'mixed-case digest guessed as lowercase' "$REPO/scripts/release-provenance-gate.py" "$OUT/mixed-digest.json" "$ARTIFACT" 'artifact_sha256 must be exactly 64 lowercase'

printf '{broken\n' > "$OUT/malformed.json"
expect_reject 'malformed JSON' "$REPO/scripts/release-provenance-gate.py" "$OUT/malformed.json" "$ARTIFACT" 'manifest is not strict UTF-8 JSON'

printf '{"schema_version":1,"commit":"%s","commit":"%s","tree":"%s","artifact_sha256":"%s","corpus_manifest_sha256":"%s"}\n' \
  "$COMMIT" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA" > "$OUT/duplicate.json"
expect_reject 'duplicate key' "$REPO/scripts/release-provenance-gate.py" "$OUT/duplicate.json" "$ARTIFACT" 'duplicate JSON key: commit'

printf '{"schema_version":1,"commit":"%s","tree":"%s","artifact_sha256":"%s"}\n' \
  "$COMMIT" "$TREE" "$ARTIFACT_SHA" > "$OUT/missing.json"
expect_reject 'missing key' "$REPO/scripts/release-provenance-gate.py" "$OUT/missing.json" "$ARTIFACT" 'manifest missing key(s): corpus_manifest_sha256'

printf '{"schema_version":1,"commit":"%s","tree":"%s","artifact_sha256":"%s","corpus_manifest_sha256":"%s","surprise":"accepted"}\n' \
  "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA" > "$OUT/unknown.json"
expect_reject 'unknown key' "$REPO/scripts/release-provenance-gate.py" "$OUT/unknown.json" "$ARTIFACT" 'manifest has unknown key(s): surprise'

printf '{"schema_version":1,"commit":7,"tree":"%s","artifact_sha256":"%s","corpus_manifest_sha256":"%s"}\n' \
  "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA" > "$OUT/non-string.json"
expect_reject 'non-string value' "$REPO/scripts/release-provenance-gate.py" "$OUT/non-string.json" "$ARTIFACT" 'commit, tree, and digest values must be JSON strings'

echo '=== FG-093 checkout and file boundaries ==='
cp "$GOOD" "$REPO/internal-manifest.json"
expect_reject 'manifest inside checkout' "$REPO/scripts/release-provenance-gate.py" "$REPO/internal-manifest.json" "$ARTIFACT" 'manifest must be external'
rm -f "$REPO/internal-manifest.json"

expect_reject 'tracked artifact inside checkout' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$REPO/tracked.txt" 'artifact must be external to the checkout being judged'

printf 'dirty\n' >> "$REPO/tracked.txt"
expect_reject 'tracked dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'tracked regular-file raw bytes do not match index blob'
git -C "$REPO" restore -- tracked.txt

plant_clean_filter_attack "$REPO"
expect_reject 'clean-filter hidden raw tracked-byte mismatch' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'tracked regular-file raw bytes do not match index blob'
clear_clean_filter_attack "$REPO"

printf 'untracked\n' > "$REPO/untracked.txt"
expect_reject 'untracked dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout contains physical untracked paths, including ignored paths'
rm -f "$REPO/untracked.txt"

printf '/hidden-by-info-exclude\n' >> "$REPO/.git/info/exclude"
printf 'physically present but excluded\n' > "$REPO/hidden-by-info-exclude"
expect_reject '.git/info/exclude hidden physical untracked file' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout contains physical untracked paths, including ignored paths'
rm -f "$REPO/hidden-by-info-exclude"

git -C "$REPO" update-index --assume-unchanged tracked.txt
printf 'masked tracked dirt\n' >> "$REPO/tracked.txt"
expect_reject 'assume-unchanged masked tracked dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout index contains assume-unchanged entries'
git -C "$REPO" update-index --no-assume-unchanged tracked.txt
git -C "$REPO" restore -- tracked.txt

git -C "$REPO" update-index --skip-worktree tracked.txt
printf 'masked tracked dirt\n' >> "$REPO/tracked.txt"
expect_reject 'skip-worktree masked tracked dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout index contains skip-worktree entries'
git -C "$REPO" update-index --no-skip-worktree tracked.txt
git -C "$REPO" restore -- tracked.txt

git -C "$REPO" config core.fileMode false
chmod +x "$REPO/tracked.txt"
expect_reject 'core.fileMode=false masked executable-bit dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout disables tracked executable-bit detection'
chmod -x "$REPO/tracked.txt"
git -C "$REPO" config core.fileMode true

git -C "$REPO" config core.ignoreCase true
printf 'case-collision dirt\n' > "$REPO/TRACKED.TXT"
expect_reject 'core.ignoreCase case-collision dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout contains physical untracked paths, including ignored paths'
rm -f "$REPO/TRACKED.TXT"
git -C "$REPO" config core.ignoreCase false

printf 'submodule dirty\n' >> "$REPO/vendor/provenance-fixture/submodule.txt"
expect_reject 'submodule dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout has tracked or untracked changes'
git -C "$REPO/vendor/provenance-fixture" restore -- submodule.txt

NESTED_REPO="$REPO/vendor/provenance-fixture/vendor/nested-fixture"
git -C "$NESTED_REPO" update-index --assume-unchanged nested.txt
printf 'nested masked dirt\n' >> "$NESTED_REPO/nested.txt"
expect_reject 'nested submodule assume-unchanged dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout index contains assume-unchanged entries'
git -C "$NESTED_REPO" update-index --no-assume-unchanged nested.txt
git -C "$NESTED_REPO" restore -- nested.txt

git -C "$REPO" submodule deinit -f -q vendor/provenance-fixture
mkdir -p "$REPO/vendor/provenance-fixture"
printf 'evil bytes in a plain gitlink directory\n' > "$REPO/vendor/provenance-fixture/evil.txt"
expect_reject 'plain directory at gitlink path' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'gitlink is not an initialized Git worktree'
rm -f "$REPO/vendor/provenance-fixture/evil.txt"
rmdir "$REPO/vendor/provenance-fixture"
git -C "$REPO" -c protocol.file.allow=always submodule update --init --recursive -q

EXPECTED_SUBMODULE_HEAD=$(git -C "$REPO" rev-parse HEAD:vendor/provenance-fixture)
git -C "$REPO/vendor/provenance-fixture" -c user.name=fogell-proof \
  -c user.email=fogell-proof@example.invalid commit --allow-empty -q -m 'wrong gitlink head'
expect_reject 'initialized submodule at wrong HEAD' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'initialized submodule HEAD does not match its index gitlink'
git -C "$REPO/vendor/provenance-fixture" checkout -q "$EXPECTED_SUBMODULE_HEAD"

git -C "$NESTED_REPO" update-index --skip-worktree nested.txt
printf 'nested masked dirt\n' >> "$NESTED_REPO/nested.txt"
expect_reject 'nested submodule skip-worktree dirt' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$ARTIFACT" 'checkout index contains skip-worktree entries'
git -C "$NESTED_REPO" update-index --no-skip-worktree nested.txt
git -C "$NESTED_REPO" restore -- nested.txt

echo '=== FG-093 inherited Git-environment attacks ==='
printf 'case-collision ambient dirt\n' > "$REPO/TRACKED.TXT"
expect_reject_git_environment 'GIT_CONFIG_COUNT core.ignoreCase collision' 'checkout contains physical untracked paths, including ignored paths' \
  GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=core.ignoreCase GIT_CONFIG_VALUE_0=true
rm -f "$REPO/TRACKED.TXT"

chmod +x "$REPO/tracked.txt"
expect_reject_git_environment 'GIT_CONFIG_COUNT core.fileMode masking' 'tracked regular-file executable mode does not match index' \
  GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=core.fileMode GIT_CONFIG_VALUE_0=false
chmod -x "$REPO/tracked.txt"

ALT_INDEX="$OUT/alternate-index"
cp "$REPO/.git/index" "$ALT_INDEX"
GIT_INDEX_FILE="$ALT_INDEX" git -C "$REPO" update-index --assume-unchanged tracked.txt
printf 'alternate-index masked dirt\n' >> "$REPO/tracked.txt"
expect_reject_git_environment 'GIT_INDEX_FILE alternate index' 'tracked regular-file raw bytes do not match index blob' \
  GIT_INDEX_FILE="$ALT_INDEX"
git -C "$REPO" restore -- tracked.txt

rm -f "$MARKER"
set +e
GIT_DIR="$SUBMODULE_SOURCE/.git" GIT_WORK_TREE="$REPO" \
  "$REPO/scripts/release-provenance-gate.py" --manifest "$GOOD" --artifact "$ARTIFACT" -- \
  /usr/bin/touch "$MARKER" > "$OUT/git-dir-work-tree.log" 2>&1
RC=$?
set -e
if [ "$RC" -ne 0 ] || [ ! -f "$MARKER" ] || ! grep -Fq 'PROVENANCE VERIFIED:' "$OUT/git-dir-work-tree.log"; then
  echo '  FAIL: inherited GIT_DIR/GIT_WORK_TREE changed the repository under judgment'
  sed 's/^/    | /' "$OUT/git-dir-work-tree.log"
  FAILED=1
else
  echo '  ignored inherited GIT_DIR/GIT_WORK_TREE; exact repository still executed'
fi

REPLACE_ATTACK="$LAB/replacement-object-attack"
clone_fixture "$REPLACE_ATTACK"
identity "$REPLACE_ATTACK"
REPLACE_TRUSTED_COMMIT=$COMMIT
REPLACE_TRUSTED_TREE=$TREE
REPLACE_MANIFEST="$OUT/replacement-object-attack.json"
write_manifest "$REPLACE_MANIFEST" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
printf 'replacement-object malicious bytes\n' > "$REPLACE_ATTACK/tracked.txt"
git -C "$REPLACE_ATTACK" add tracked.txt
git -C "$REPLACE_ATTACK" commit -q -m 'build malicious replacement tree'
REPLACE_MALICIOUS_TREE=$(git -C "$REPLACE_ATTACK" rev-parse 'HEAD^{tree}')
git -C "$REPLACE_ATTACK" replace "$REPLACE_TRUSTED_TREE" "$REPLACE_MALICIOUS_TREE"
git -C "$REPLACE_ATTACK" reset --hard -q "$REPLACE_TRUSTED_COMMIT"
if [ -n "$(git -C "$REPLACE_ATTACK" status --porcelain=v1 --untracked-files=all)" ] || \
   [ "$(git -C "$REPLACE_ATTACK" rev-parse 'HEAD^{tree}')" != "$REPLACE_TRUSTED_TREE" ] || \
   ! grep -Fqx 'replacement-object malicious bytes' "$REPLACE_ATTACK/tracked.txt"; then
  echo '  FAIL: replacement-object attack preconditions were not established'
  FAILED=1
else
  expect_reject 'Git replacement-object attack' \
    "$REPLACE_ATTACK/scripts/release-provenance-gate.py" "$REPLACE_MANIFEST" "$ARTIFACT" \
    'checkout contains Git replacement-object refs'
fi

ln -s "$ARTIFACT" "$OUT/artifact-link"
expect_reject 'symlink artifact' "$REPO/scripts/release-provenance-gate.py" "$GOOD" "$OUT/artifact-link" 'artifact is not a regular non-symlink file'

if [ -n "$(git -C "$REPO" status --porcelain=v1 --untracked-files=all)" ]; then
  echo '  FAIL: proof fixture was not restored to a clean checkout'
  FAILED=1
fi

echo '=== FG-093 comparison-removal mutations ==='
mutation_exposes_arm() {
  local field=$1 zeros=$2
  local mutant="$LAB/mutant-$field" checker manifest marker log
  clone_fixture "$mutant"
  checker="$mutant/scripts/release-provenance-gate.py"
  manifest="$OUT/mutant-$field.json"
  marker="$OUT/mutant-$field-ran"
  log="$OUT/mutant-$field.log"

  local before after
  before=$(sha256 "$checker")
  sed -i "/require_match(\"$field\"/s/^/        # FG-093 planted comparison-removal mutation: /" "$checker"
  after=$(sha256 "$checker")
  if [ "$before" = "$after" ]; then
    echo "  FAIL: $field comparison mutation changed nothing"
    FAILED=1
    return
  fi

  git -C "$mutant" add scripts/release-provenance-gate.py
  git -C "$mutant" commit -q -m "mutate $field comparison"
  identity "$mutant"

  local manifest_commit=$COMMIT manifest_tree=$TREE manifest_artifact=$ARTIFACT_SHA manifest_corpus=$CORPUS_SHA
  case "$field" in
    commit) manifest_commit=$zeros ;;
    tree) manifest_tree=$zeros ;;
    artifact_sha256) manifest_artifact=$zeros ;;
    corpus_manifest_sha256) manifest_corpus=$zeros ;;
    *) echo "  FAIL: unknown mutation field $field"; FAILED=1; return ;;
  esac
  write_manifest "$manifest" "$manifest_commit" "$manifest_tree" "$manifest_artifact" "$manifest_corpus"
  run_gate "$checker" "$manifest" "$ARTIFACT" "$marker" "$log"
  if [ "$RC" -eq 0 ] && [ -f "$marker" ] && grep -Fq 'PROVENANCE VERIFIED:' "$log"; then
    echo "  killed $field comparison-removal mutation: its known-bad arm became executable"
  else
    echo "  FAIL: $field mutation did not expose its known-bad arm"
    sed 's/^/    | /' "$log"
    FAILED=1
  fi
}

ZEROS40=$(printf '0%.0s' {1..40})
ZEROS64=$(printf '0%.0s' {1..64})
mutation_exposes_arm commit "$ZEROS40"
mutation_exposes_arm tree "$ZEROS40"
mutation_exposes_arm artifact_sha256 "$ZEROS64"
mutation_exposes_arm corpus_manifest_sha256 "$ZEROS64"

ARTIFACT_BOUNDARY_MUTANT="$LAB/mutant-artifact-external-boundary"
clone_fixture "$ARTIFACT_BOUNDARY_MUTANT"
ARTIFACT_BOUNDARY_CHECKER="$ARTIFACT_BOUNDARY_MUTANT/scripts/release-provenance-gate.py"
ARTIFACT_BOUNDARY_BEFORE=$(sha256 "$ARTIFACT_BOUNDARY_CHECKER")
sed -i '/require_external_path(artifact, repository, "artifact")/s/^/    # FG-093 planted artifact-boundary mutation: /' "$ARTIFACT_BOUNDARY_CHECKER"
ARTIFACT_BOUNDARY_AFTER=$(sha256 "$ARTIFACT_BOUNDARY_CHECKER")
if [ "$ARTIFACT_BOUNDARY_BEFORE" = "$ARTIFACT_BOUNDARY_AFTER" ]; then
  echo '  FAIL: artifact external-boundary mutation changed nothing'
  FAILED=1
else
  git -C "$ARTIFACT_BOUNDARY_MUTANT" add scripts/release-provenance-gate.py
  git -C "$ARTIFACT_BOUNDARY_MUTANT" commit -q -m 'mutate artifact external boundary'
  identity "$ARTIFACT_BOUNDARY_MUTANT"
  INTERNAL_ARTIFACT="$ARTIFACT_BOUNDARY_MUTANT/tracked.txt"
  INTERNAL_ARTIFACT_SHA=$(sha256 "$INTERNAL_ARTIFACT")
  ARTIFACT_BOUNDARY_MANIFEST="$OUT/mutant-artifact-external-boundary.json"
  ARTIFACT_BOUNDARY_MARKER="$OUT/mutant-artifact-external-boundary-ran"
  write_manifest "$ARTIFACT_BOUNDARY_MANIFEST" "$COMMIT" "$TREE" "$INTERNAL_ARTIFACT_SHA" "$CORPUS_SHA"
  run_gate "$ARTIFACT_BOUNDARY_CHECKER" "$ARTIFACT_BOUNDARY_MANIFEST" "$INTERNAL_ARTIFACT" \
    "$ARTIFACT_BOUNDARY_MARKER" "$OUT/mutant-artifact-external-boundary.log"
  if [ "$RC" -eq 0 ] && [ -f "$ARTIFACT_BOUNDARY_MARKER" ] && \
     grep -Fq 'PROVENANCE VERIFIED:' "$OUT/mutant-artifact-external-boundary.log"; then
    echo '  killed artifact-boundary mutation: tracked in-checkout artifact became executable'
  else
    echo '  FAIL: artifact-boundary mutation did not expose the tracked-artifact arm'
    sed 's/^/    | /' "$OUT/mutant-artifact-external-boundary.log"
    FAILED=1
  fi
fi

echo '=== FG-093 downstream environment-binding mutations ==='
mutation_exposes_environment_binding() {
  local binding=$1 line_number=$2
  local mutant="$LAB/mutant-env-$binding" checker manifest capture expected detected log
  clone_fixture "$mutant"
  checker="$mutant/scripts/release-provenance-gate.py"
  manifest="$OUT/mutant-env-$binding.json"
  capture="$OUT/mutant-env-$binding.capture"
  expected="$OUT/mutant-env-$binding.expected"
  detected="$OUT/mutant-env-$binding.detected"
  log="$OUT/mutant-env-$binding.log"

  local before after
  before=$(sha256 "$checker")
  sed -i "/\"$binding\":/s/: .*/: \"FG-093-planted-wrong-binding\",/" "$checker"
  after=$(sha256 "$checker")
  if [ "$before" = "$after" ] || [ "$(grep -Fc "\"$binding\": \"FG-093-planted-wrong-binding\"," "$checker")" -ne 1 ]; then
    echo "  FAIL: $binding environment mutation was not planted exactly once"
    FAILED=1
    return
  fi

  git -C "$mutant" add scripts/release-provenance-gate.py
  git -C "$mutant" commit -q -m "mutate $binding environment binding"
  identity "$mutant"
  write_manifest "$manifest" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
  printf '%s\n' "$COMMIT" "$TREE" '<sealed-descriptor>' "$ARTIFACT_SHA" "$CORPUS_SHA" "$ARTIFACT_SHA" \
    '1' '<unset>' '<unset>' '<unset>' '<unset>' '<unset>' '<unset>' > "$expected"
  sed "${line_number}s/.*/FG-093-planted-wrong-binding/" "$expected" > "$detected"
  if [ "$binding" = FOGELL_VERIFIED_ARTIFACT ]; then
    sed -i '6s/.*//' "$detected"
  fi

  rm -f "$capture"
  set +e
  "$checker" --manifest "$manifest" --artifact "$ARTIFACT" -- "$ENV_PROBE" "$capture" > "$log" 2>&1
  RC=$?
  set -e
  if [ "$RC" -eq 0 ] && [ -f "$capture" ] && cmp -s "$detected" "$capture" && ! cmp -s "$expected" "$capture" && grep -Fq 'PROVENANCE VERIFIED:' "$log"; then
    echo "  killed $binding mutation: exact downstream binding assertion detected it"
  else
    echo "  FAIL: $binding mutation was not detected exactly"
    sed 's/^/    | /' "$log"
    [ ! -f "$capture" ] || sed 's/^/    | actual: /' "$capture"
    FAILED=1
  fi
}

mutation_exposes_environment_binding FOGELL_VERIFIED_COMMIT 1
mutation_exposes_environment_binding FOGELL_VERIFIED_TREE 2
mutation_exposes_environment_binding FOGELL_VERIFIED_ARTIFACT 3
mutation_exposes_environment_binding FOGELL_VERIFIED_ARTIFACT_SHA256 4
mutation_exposes_environment_binding FOGELL_VERIFIED_CORPUS_MANIFEST_SHA256 5

DOWNSTREAM_SCRUB_MUTANT="$LAB/mutant-downstream-git-environment-scrub"
clone_fixture "$DOWNSTREAM_SCRUB_MUTANT"
DOWNSTREAM_SCRUB_CHECKER="$DOWNSTREAM_SCRUB_MUTANT/scripts/release-provenance-gate.py"
DOWNSTREAM_SCRUB_BEFORE=$(sha256 "$DOWNSTREAM_SCRUB_CHECKER")
DOWNSTREAM_SCRUB_LINE=$(grep -nF 'environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}' "$DOWNSTREAM_SCRUB_CHECKER" | tail -1 | cut -d: -f1)
sed -i "${DOWNSTREAM_SCRUB_LINE}s/environment = {key: value for key, value in os.environ.items() if not key.startswith(\"GIT_\")}/environment = os.environ.copy()/" "$DOWNSTREAM_SCRUB_CHECKER"
DOWNSTREAM_SCRUB_AFTER=$(sha256 "$DOWNSTREAM_SCRUB_CHECKER")
if [ "$DOWNSTREAM_SCRUB_BEFORE" = "$DOWNSTREAM_SCRUB_AFTER" ] || \
   [ "$(grep -Fc 'environment = os.environ.copy()' "$DOWNSTREAM_SCRUB_CHECKER")" -ne 1 ] || \
   [ "$(grep -Fc 'environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}' "$DOWNSTREAM_SCRUB_CHECKER")" -ne 1 ]; then
  echo '  FAIL: downstream inherited-Git-environment scrub mutation was not planted'
  FAILED=1
else
  git -C "$DOWNSTREAM_SCRUB_MUTANT" add scripts/release-provenance-gate.py
  git -C "$DOWNSTREAM_SCRUB_MUTANT" commit -q -m 'mutate downstream Git environment scrub'
  identity "$DOWNSTREAM_SCRUB_MUTANT"
  DOWNSTREAM_SCRUB_MANIFEST="$OUT/mutant-downstream-git-environment-scrub.json"
  DOWNSTREAM_SCRUB_CAPTURE="$OUT/mutant-downstream-git-environment-scrub.capture"
  DOWNSTREAM_SCRUB_EXPECTED="$OUT/mutant-downstream-git-environment-scrub.expected"
  write_manifest "$DOWNSTREAM_SCRUB_MANIFEST" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
  printf '%s\n' "$COMMIT" "$TREE" '<sealed-descriptor>' "$ARTIFACT_SHA" "$CORPUS_SHA" "$ARTIFACT_SHA" \
    1 /hostile/git-dir /hostile/work-tree /hostile/index 1 core.ignoreCase true > "$DOWNSTREAM_SCRUB_EXPECTED"
  rm -f "$DOWNSTREAM_SCRUB_CAPTURE"
  set +e
  env GIT_DIR=/hostile/git-dir GIT_WORK_TREE=/hostile/work-tree GIT_INDEX_FILE=/hostile/index \
    GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=core.ignoreCase GIT_CONFIG_VALUE_0=true \
    "$DOWNSTREAM_SCRUB_CHECKER" --manifest "$DOWNSTREAM_SCRUB_MANIFEST" --artifact "$ARTIFACT" -- \
    "$ENV_PROBE" "$DOWNSTREAM_SCRUB_CAPTURE" > "$OUT/mutant-downstream-git-environment-scrub.log" 2>&1
  RC=$?
  set -e
  if [ "$RC" -eq 0 ] && [ -f "$DOWNSTREAM_SCRUB_CAPTURE" ] && \
     cmp -s "$DOWNSTREAM_SCRUB_EXPECTED" "$DOWNSTREAM_SCRUB_CAPTURE"; then
    echo '  killed downstream Git-environment scrub mutation: hostile values reached the probe'
  else
    echo '  FAIL: downstream Git-environment scrub mutation was not exposed exactly'
    sed 's/^/    | /' "$OUT/mutant-downstream-git-environment-scrub.log"
    FAILED=1
  fi
fi

echo '=== FG-093 Git-cleanliness guard mutations ==='
mutation_exposes_clean_guard() {
  local label=$1 pattern=$2 setup=$3
  local mutant="$LAB/mutant-clean-$label" checker manifest marker log
  clone_fixture "$mutant"
  checker="$mutant/scripts/release-provenance-gate.py"
  manifest="$OUT/mutant-clean-$label.json"
  marker="$OUT/mutant-clean-$label-ran"
  log="$OUT/mutant-clean-$label.log"

  local before after
  before=$(sha256 "$checker")
  sed -i "/$pattern/s/^/        # FG-093 planted cleanliness-guard mutation: /" "$checker"
  after=$(sha256 "$checker")
  if [ "$before" = "$after" ]; then
    echo "  FAIL: $label cleanliness mutation changed nothing"
    FAILED=1
    return
  fi
  git -C "$mutant" add scripts/release-provenance-gate.py
  git -C "$mutant" commit -q -m "mutate $label cleanliness guard"
  identity "$mutant"
  write_manifest "$manifest" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"

  case "$setup" in
    assume)
      git -C "$mutant" update-index --assume-unchanged tracked.txt
      ;;
    skip)
      git -C "$mutant" update-index --skip-worktree tracked.txt
      ;;
    filemode)
      git -C "$mutant" config core.fileMode false
      ;;
    nested)
      local nested="$mutant/vendor/provenance-fixture/vendor/nested-fixture"
      git -C "$nested" update-index --assume-unchanged nested.txt
      printf 'nested masked mutation dirt\n' >> "$nested/nested.txt"
      ;;
    ignorecase)
      git -C "$mutant" config core.ignoreCase true
      printf 'case-collision mutation dirt\n' > "$mutant/TRACKED.TXT"
      ;;
    hidden-exclude)
      printf '/hidden-by-info-exclude\n' >> "$mutant/.git/info/exclude"
      printf 'hidden physical mutation dirt\n' > "$mutant/hidden-by-info-exclude"
      ;;
    clean-filter)
      plant_clean_filter_attack "$mutant"
      ;;
    *) echo "  FAIL: unknown cleanliness setup $setup"; FAILED=1; return ;;
  esac

  run_gate "$checker" "$manifest" "$ARTIFACT" "$marker" "$log"
  if [ "$RC" -eq 0 ] && [ -f "$marker" ] && grep -Fq 'PROVENANCE VERIFIED:' "$log"; then
    echo "  killed $label cleanliness mutation: its masked bad arm became executable"
  else
    echo "  FAIL: $label cleanliness mutation did not expose its masked bad arm"
    sed 's/^/    | /' "$log"
    FAILED=1
  fi
}

mutation_exposes_clean_guard assume-unchanged 'require_no_assume_unchanged(worktree)' assume
mutation_exposes_clean_guard skip-worktree 'require_no_skip_worktree(worktree)' skip
mutation_exposes_clean_guard filemode-config 'require_filemode_tracking(worktree)' filemode
mutation_exposes_clean_guard recursive-submodule 'queue.extend(submodules)' nested
mutation_exposes_clean_guard ignorecase-override 'core.ignoreCase=false' ignorecase
mutation_exposes_clean_guard physical-untracked 'require_no_physical_untracked(worktree)' hidden-exclude
mutation_exposes_clean_guard raw-tracked-identity 'require_raw_tracked_identity(worktree)' clean-filter

GITLINK_MUTANT="$LAB/mutant-required-gitlink"
clone_fixture "$GITLINK_MUTANT"
GITLINK_CHECKER="$GITLINK_MUTANT/scripts/release-provenance-gate.py"
GITLINK_BEFORE=$(sha256 "$GITLINK_CHECKER")
sed -i 's/submodules = required_submodules(worktree)/submodules = []  # FG-093 planted required-gitlink mutation/' "$GITLINK_CHECKER"
GITLINK_AFTER=$(sha256 "$GITLINK_CHECKER")
if [ "$GITLINK_BEFORE" = "$GITLINK_AFTER" ] || \
   ! grep -Fq 'submodules = []  # FG-093 planted required-gitlink mutation' "$GITLINK_CHECKER"; then
  echo '  FAIL: required-gitlink mutation was not planted'
  FAILED=1
else
  git -C "$GITLINK_MUTANT" add scripts/release-provenance-gate.py
  git -C "$GITLINK_MUTANT" commit -q -m 'mutate required gitlink guard'
  identity "$GITLINK_MUTANT"
  GITLINK_MANIFEST="$OUT/mutant-required-gitlink.json"
  GITLINK_MARKER="$OUT/mutant-required-gitlink-ran"
  write_manifest "$GITLINK_MANIFEST" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
  git -C "$GITLINK_MUTANT" submodule deinit -f -q vendor/provenance-fixture
  mkdir -p "$GITLINK_MUTANT/vendor/provenance-fixture"
  printf 'evil bytes in mutant plain gitlink directory\n' > "$GITLINK_MUTANT/vendor/provenance-fixture/evil.txt"
  run_gate "$GITLINK_CHECKER" "$GITLINK_MANIFEST" "$ARTIFACT" "$GITLINK_MARKER" "$OUT/mutant-required-gitlink.log"
  if [ "$RC" -eq 0 ] && [ -f "$GITLINK_MARKER" ] && grep -Fq 'PROVENANCE VERIFIED:' "$OUT/mutant-required-gitlink.log"; then
    echo '  killed required-gitlink mutation: evil plain-directory bytes became executable'
  else
    echo '  FAIL: required-gitlink mutation did not expose the plain-directory arm'
    sed 's/^/    | /' "$OUT/mutant-required-gitlink.log"
    FAILED=1
  fi
fi

SCRUB_MUTANT="$LAB/mutant-git-environment-scrub"
clone_fixture "$SCRUB_MUTANT"
SCRUB_CHECKER="$SCRUB_MUTANT/scripts/release-provenance-gate.py"
SCRUB_BEFORE=$(sha256 "$SCRUB_CHECKER")
sed -i '/        env=controlled_git_environment(),/d' "$SCRUB_CHECKER"
SCRUB_AFTER=$(sha256 "$SCRUB_CHECKER")
if [ "$SCRUB_BEFORE" = "$SCRUB_AFTER" ] || [ "$(grep -Fc 'env=controlled_git_environment(),' "$SCRUB_CHECKER")" -ne 0 ]; then
  echo '  FAIL: inherited-Git-environment scrub mutation was not planted'
  FAILED=1
else
  git -C "$SCRUB_MUTANT" add scripts/release-provenance-gate.py
  git -C "$SCRUB_MUTANT" commit -q -m 'mutate inherited Git environment scrub'
  identity "$SCRUB_MUTANT"
  SCRUB_MANIFEST="$OUT/mutant-git-environment-scrub.json"
  SCRUB_MARKER="$OUT/mutant-git-environment-scrub-ran"
  write_manifest "$SCRUB_MANIFEST" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
  rm -f "$SCRUB_MARKER"
  set +e
  GIT_DIR="$SUBMODULE_SOURCE/.git" GIT_WORK_TREE="$SCRUB_MUTANT" \
    "$SCRUB_CHECKER" --manifest "$SCRUB_MANIFEST" --artifact "$ARTIFACT" -- \
    /usr/bin/touch "$SCRUB_MARKER" > "$OUT/mutant-git-environment-scrub.log" 2>&1
  RC=$?
  set -e
  if [ "$RC" -ne 0 ] && [ ! -f "$SCRUB_MARKER" ]; then
    echo '  killed inherited-Git-environment scrub mutation: ambient Git state changed the judgment'
  else
    echo '  FAIL: inherited-Git-environment scrub mutation was not detected'
    sed 's/^/    | /' "$OUT/mutant-git-environment-scrub.log"
    FAILED=1
  fi
fi

echo '=== FG-093 replacement-object and artifact-handoff mutations ==='
REPLACE_GUARD_MUTANT="$LAB/mutant-replacement-object-guards"
clone_fixture "$REPLACE_GUARD_MUTANT"
REPLACE_GUARD_CHECKER="$REPLACE_GUARD_MUTANT/scripts/release-provenance-gate.py"
REPLACE_GUARD_BEFORE=$(sha256 "$REPLACE_GUARD_CHECKER")
sed -i '/environment\["GIT_NO_REPLACE_OBJECTS"\] = "1"/d; /require_no_replacement_refs(worktree)/s/^/        # FG-093 planted replacement-object mutation: /' "$REPLACE_GUARD_CHECKER"
REPLACE_GUARD_AFTER=$(sha256 "$REPLACE_GUARD_CHECKER")
if [ "$REPLACE_GUARD_BEFORE" = "$REPLACE_GUARD_AFTER" ] || \
   [ "$(grep -Fc 'environment["GIT_NO_REPLACE_OBJECTS"] = "1"' "$REPLACE_GUARD_CHECKER")" -ne 0 ] || \
   [ "$(grep -Fc '# FG-093 planted replacement-object mutation:         require_no_replacement_refs(worktree)' "$REPLACE_GUARD_CHECKER")" -ne 1 ]; then
  echo '  FAIL: replacement-object guard mutation was not planted exactly'
  FAILED=1
else
  git -C "$REPLACE_GUARD_MUTANT" add scripts/release-provenance-gate.py
  git -C "$REPLACE_GUARD_MUTANT" commit -q -m 'mutate replacement-object guards'
  identity "$REPLACE_GUARD_MUTANT"
  REPLACE_GUARD_TRUSTED_COMMIT=$COMMIT
  REPLACE_GUARD_TRUSTED_TREE=$TREE
  REPLACE_GUARD_MANIFEST="$OUT/mutant-replacement-object-guards.json"
  REPLACE_GUARD_MARKER="$OUT/mutant-replacement-object-guards-ran"
  write_manifest "$REPLACE_GUARD_MANIFEST" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
  printf 'mutation replacement-object malicious bytes\n' > "$REPLACE_GUARD_MUTANT/tracked.txt"
  git -C "$REPLACE_GUARD_MUTANT" add tracked.txt
  git -C "$REPLACE_GUARD_MUTANT" commit -q -m 'build mutation replacement tree'
  REPLACE_GUARD_MALICIOUS_TREE=$(git -C "$REPLACE_GUARD_MUTANT" rev-parse 'HEAD^{tree}')
  git -C "$REPLACE_GUARD_MUTANT" replace "$REPLACE_GUARD_TRUSTED_TREE" "$REPLACE_GUARD_MALICIOUS_TREE"
  git -C "$REPLACE_GUARD_MUTANT" reset --hard -q "$REPLACE_GUARD_TRUSTED_COMMIT"
  run_gate "$REPLACE_GUARD_CHECKER" "$REPLACE_GUARD_MANIFEST" "$ARTIFACT" \
    "$REPLACE_GUARD_MARKER" "$OUT/mutant-replacement-object-guards.log"
  if [ "$RC" -eq 0 ] && [ -f "$REPLACE_GUARD_MARKER" ] && \
     grep -Fq 'PROVENANCE VERIFIED:' "$OUT/mutant-replacement-object-guards.log"; then
    echo '  killed replacement-object guard mutation: malicious clean bytes became executable'
  else
    echo '  FAIL: replacement-object guard mutation did not expose its hostile arm'
    sed 's/^/    | /' "$OUT/mutant-replacement-object-guards.log"
    FAILED=1
  fi
fi

ARTIFACT_HANDOFF_MUTANT="$LAB/mutant-artifact-descriptor-handoff"
clone_fixture "$ARTIFACT_HANDOFF_MUTANT"
ARTIFACT_HANDOFF_CHECKER="$ARTIFACT_HANDOFF_MUTANT/scripts/release-provenance-gate.py"
ARTIFACT_HANDOFF_BEFORE=$(sha256 "$ARTIFACT_HANDOFF_CHECKER")
sed -i 's/"FOGELL_VERIFIED_ARTIFACT": artifact_descriptor_path/"FOGELL_VERIFIED_ARTIFACT": str(parsed.artifact.resolve())/' "$ARTIFACT_HANDOFF_CHECKER"
ARTIFACT_HANDOFF_AFTER=$(sha256 "$ARTIFACT_HANDOFF_CHECKER")
if [ "$ARTIFACT_HANDOFF_BEFORE" = "$ARTIFACT_HANDOFF_AFTER" ] || \
   [ "$(grep -Fc '"FOGELL_VERIFIED_ARTIFACT": str(parsed.artifact.resolve())' "$ARTIFACT_HANDOFF_CHECKER")" -ne 1 ]; then
  echo '  FAIL: artifact descriptor-handoff mutation was not planted exactly'
  FAILED=1
else
  git -C "$ARTIFACT_HANDOFF_MUTANT" add scripts/release-provenance-gate.py
  git -C "$ARTIFACT_HANDOFF_MUTANT" commit -q -m 'mutate artifact descriptor handoff'
  identity "$ARTIFACT_HANDOFF_MUTANT"
  ARTIFACT_HANDOFF_MANIFEST="$OUT/mutant-artifact-descriptor-handoff.json"
  ARTIFACT_HANDOFF_CAPTURE="$OUT/mutant-artifact-descriptor-handoff.capture"
  write_manifest "$ARTIFACT_HANDOFF_MANIFEST" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
  printf 'deterministic release artifact bytes\n' > "$ARTIFACT"
  rm -f "$ARTIFACT_HANDOFF_CAPTURE"
  set +e
  "$ARTIFACT_HANDOFF_CHECKER" --manifest "$ARTIFACT_HANDOFF_MANIFEST" --artifact "$ARTIFACT" -- \
    "$ARTIFACT_SWAP_PROBE" "$ARTIFACT" "$ARTIFACT_HANDOFF_CAPTURE" > "$OUT/mutant-artifact-descriptor-handoff.log" 2>&1
  RC=$?
  set -e
  if [ "$RC" -eq 97 ] && [ ! -f "$ARTIFACT_HANDOFF_CAPTURE" ] && \
     [ "$(sha256 "$ARTIFACT")" != "$ARTIFACT_SHA" ]; then
    echo '  killed artifact descriptor-handoff mutation: downstream modified mutable source bytes'
  else
    echo '  FAIL: artifact descriptor-handoff mutation did not expose swapped bytes'
    sed 's/^/    | /' "$OUT/mutant-artifact-descriptor-handoff.log"
    FAILED=1
  fi
  printf 'deterministic release artifact bytes\n' > "$ARTIFACT"
fi

ARTIFACT_SEAL_MUTANT="$LAB/mutant-artifact-seals"
clone_fixture "$ARTIFACT_SEAL_MUTANT"
ARTIFACT_SEAL_CHECKER="$ARTIFACT_SEAL_MUTANT/scripts/release-provenance-gate.py"
ARTIFACT_SEAL_BEFORE=$(sha256 "$ARTIFACT_SEAL_CHECKER")
sed -i \
  -e 's/fcntl\.fcntl(sealed, fcntl\.F_ADD_SEALS, seals)/# FG-093 planted artifact-seal mutation: add omitted/' \
  -e 's/applied = fcntl\.fcntl(sealed, fcntl\.F_GET_SEALS)/applied = seals  # FG-093 planted artifact-seal mutation: lie/' \
  "$ARTIFACT_SEAL_CHECKER"
ARTIFACT_SEAL_AFTER=$(sha256 "$ARTIFACT_SEAL_CHECKER")
if [ "$ARTIFACT_SEAL_BEFORE" = "$ARTIFACT_SEAL_AFTER" ] || \
   [ "$(grep -Fc 'FG-093 planted artifact-seal mutation:' "$ARTIFACT_SEAL_CHECKER")" -ne 2 ]; then
  echo '  FAIL: artifact-seal mutation was not planted exactly'
  FAILED=1
else
  git -C "$ARTIFACT_SEAL_MUTANT" add scripts/release-provenance-gate.py
  git -C "$ARTIFACT_SEAL_MUTANT" commit -q -m 'mutate artifact seals'
  identity "$ARTIFACT_SEAL_MUTANT"
  ARTIFACT_SEAL_MANIFEST="$OUT/mutant-artifact-seals.json"
  ARTIFACT_SEAL_CAPTURE="$OUT/mutant-artifact-seals.capture"
  write_manifest "$ARTIFACT_SEAL_MANIFEST" "$COMMIT" "$TREE" "$ARTIFACT_SHA" "$CORPUS_SHA"
  printf 'deterministic release artifact bytes\n' > "$ARTIFACT"
  rm -f "$ARTIFACT_SEAL_CAPTURE"
  set +e
  "$ARTIFACT_SEAL_CHECKER" --manifest "$ARTIFACT_SEAL_MANIFEST" --artifact "$ARTIFACT" -- \
    "$ARTIFACT_SWAP_PROBE" "$ARTIFACT" "$ARTIFACT_SEAL_CAPTURE" > "$OUT/mutant-artifact-seals.log" 2>&1
  RC=$?
  set -e
  if [ "$RC" -eq 97 ] && [ ! -f "$ARTIFACT_SEAL_CAPTURE" ] && \
     [ "$(sha256 "$ARTIFACT")" = "$ARTIFACT_SHA" ]; then
    echo '  killed artifact-seal mutation: downstream modified the unsealed snapshot'
  else
    echo '  FAIL: artifact-seal mutation did not expose writable snapshot bytes'
    sed 's/^/    | /' "$OUT/mutant-artifact-seals.log"
    FAILED=1
  fi
fi

if [ "$FAILED" -ne 0 ]; then
  echo 'FG-093 provenance proof FAILED'
  exit 1
fi
echo 'FG-093 provenance proof PASSED'
