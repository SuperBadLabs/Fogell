#!/usr/bin/env bash
# FG-081 hostile proof. Requires the same disposable PostgreSQL container as the
# live drill. Candidate faults must refuse; byte-changing comparison mutants must
# expose the corresponding false PASS.
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
candidate="$script_dir/migration-rollback-drill.sh"
[[ -x "$candidate" ]] || { echo "FG-081 proof: candidate is missing or non-executable" >&2; exit 1; }
[[ -n "${FOGELL_PG_CONTAINER:-}" ]] || { echo "FG-081 proof: FOGELL_PG_CONTAINER is required" >&2; exit 1; }

scratch="$(mktemp -d "${TMPDIR:-/tmp}/fogell-fg081-proof.XXXXXX")"
case "$scratch" in
  "${TMPDIR:-/tmp}"/fogell-fg081-proof.*) ;;
  *) echo "FG-081 proof: unsafe scratch path" >&2; exit 1 ;;
esac
cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  case "$scratch" in
    "${TMPDIR:-/tmp}"/fogell-fg081-proof.*) rm -rf -- "$scratch" ;;
    *) rc=1 ;;
  esac
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'
  fi
}
candidate_sha="$(sha256_file "$candidate")"

run_case() {
  local script=$1 label=$2 fault=${3:-}
  local rc=0
  if [[ -n "$fault" ]]; then
    env FG081_REPO_ROOT="$repo_root" "$fault"=1 "$script" > "$scratch/$label.log" 2>&1 || rc=$?
  else
    env FG081_REPO_ROOT="$repo_root" "$script" > "$scratch/$label.log" 2>&1 || rc=$?
  fi
  printf '%s\n' "$rc" > "$scratch/$label.exit"
  return "$rc"
}

expect_pass() {
  local script=$1 label=$2 fault=${3:-}
  run_case "$script" "$label" "$fault" \
    || { echo "FG-081 proof expected PASS: $label" >&2; tail -40 "$scratch/$label.log" >&2; exit 1; }
  grep -q '^FG-081 PASS ' "$scratch/$label.log" \
    || { echo "FG-081 proof got zero exit without PASS: $label" >&2; exit 1; }
}

expect_refusal() {
  local script=$1 label=$2 fault=$3 expected=$4
  if run_case "$script" "$label" "$fault"; then
    echo "FG-081 proof expected refusal: $label" >&2
    tail -40 "$scratch/$label.log" >&2
    exit 1
  fi
  grep -Fq "$expected" "$scratch/$label.log" \
    || { echo "FG-081 proof refusal had the wrong reason: $label" >&2; tail -40 "$scratch/$label.log" >&2; exit 1; }
  ! grep -q '^FG-081 PASS ' "$scratch/$label.log" \
    || { echo "FG-081 proof refusal emitted a false PASS: $label" >&2; exit 1; }
  printf '  refused %-28s %s\n' "$label" "$expected"
}

make_mutant() {
  local name=$1 output="$scratch/$1.sh"
  cp "$candidate" "$output"
  chmod +x "$output"
  printf '%s\n' "$output"
}

assert_mutant() {
  local mutant=$1
  bash -n "$mutant"
  [[ "$(sha256_file "$mutant")" != "$candidate_sha" ]] \
    || { echo "FG-081 proof mutation did not change bytes: $mutant" >&2; exit 1; }
  [[ "$(sha256_file "$candidate")" == "$candidate_sha" ]] \
    || { echo "FG-081 proof changed the candidate" >&2; exit 1; }
}

echo '=== FG-081 positive live control ==='
expect_pass "$candidate" positive

echo '=== FG-081 candidate fault refusals ==='
expect_refusal "$candidate" archive-tamper FG081_TEST_CORRUPT_ARCHIVE_AFTER_HASH \
  'rollback archive changed after its hash was recorded'
expect_refusal "$candidate" rollback-drift FG081_TEST_CORRUPT_ROLLBACK_DATA \
  'rollback logical hash differs from the pre-upgrade state'
expect_refusal "$candidate" missing-forward-fk FG081_TEST_DROP_FORWARD_FK \
  'latest migration did not install both tenant-composite attempt keys'
expect_refusal "$candidate" skipped-second-forward FG081_TEST_SKIP_SECOND_FORWARD \
  'second forward logical hash differs from the first forward state'

echo '=== FG-081 direct comparison mutants ==='
m1="$(make_mutant m1-ignore-archive-rehash)"
perl -0pi -e 's/\[\[ "\$\(sha256_file "\$rollback_archive"\)" == "\$rollback_archive_sha" \]\] \|\| die "rollback archive changed after its hash was recorded"/: # archive rehash removed/' "$m1"
assert_mutant "$m1"
expect_pass "$m1" m1-exposed FG081_TEST_CORRUPT_ARCHIVE_AFTER_HASH
echo '  killed archive-rehash mutant'

m2="$(make_mutant m2-ignore-rollback-hash)"
perl -0pi -e 's/\[\[ "\$rollback_hash" == "\$pre_hash" \]\] \|\| die "rollback logical hash differs from the pre-upgrade state"/: # rollback hash comparison removed/' "$m2"
assert_mutant "$m2"
expect_pass "$m2" m2-exposed FG081_TEST_CORRUPT_ROLLBACK_DATA
echo '  killed rollback-hash mutant'

m3="$(make_mutant m3-ignore-forward-hash)"
perl -0pi -e 's/\[\[ "\$forward2_hash" == "\$forward1_hash" \]\] \|\| die "second forward logical hash differs from the first forward state"/: # forward hash comparison removed/' "$m3"
assert_mutant "$m3"
expect_pass "$m3" m3-exposed FG081_TEST_SKIP_SECOND_FORWARD
echo '  killed forward-hash mutant'

m4="$(make_mutant m4-broaden-database-prefix)"
perl -0pi -e 's/\^fogell_fg081_/^fogell_/' "$m4"
assert_mutant "$m4"
if "$candidate" --validate-db-name fogell_123_primary >/dev/null 2>&1; then
  echo 'FG-081 proof: candidate accepted a foreign database name' >&2; exit 1
fi
"$m4" --validate-db-name fogell_123_primary >/dev/null \
  || { echo 'FG-081 proof: namespace mutant did not expose the foreign name' >&2; exit 1; }
echo '  killed database-namespace mutant'

[[ "$(sha256_file "$candidate")" == "$candidate_sha" ]] || { echo 'FG-081 proof: candidate hash drifted' >&2; exit 1; }
printf 'FG-081 PROOF PASS: positive forward/back/forward rehearsal; 4 fault controls refused; 4 byte-changing mutants killed\n'
