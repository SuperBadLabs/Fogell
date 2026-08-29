#!/usr/bin/env bash
# FG-026. Fail-closed live-PostgreSQL proof for the ten-test Store ledger slice.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

die() {
  echo "FG-026 effect-ledger proof failed: $*" >&2
  exit 1
}

judge() {
  local output=$1
  local status=$2
  local marker_count summary_count normalized

  if [ "$status" -ne 0 ]; then
    echo "test process exited $status" >&2
    return 1
  fi

  if rg -q 'skipped: no PostgreSQL' "$output"; then
    echo "live PostgreSQL suite skipped" >&2
    return 1
  fi

  normalized="${output}.normalized"
  LC_ALL=C sed $'s/\033\\[[0-9;]*[mK]//g' "$output" > "$normalized"

  marker_count="$(rg -cxF 'FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16' "$normalized" || true)"
  if [ "$marker_count" != "1" ]; then
    echo "expected one exact full-line live schema marker, observed ${marker_count:-0}" >&2
    return 1
  fi

  summary_count="$(rg -c '^(\[[0-9]{2}:[0-9]{2}:[0-9]{2} INF\] )?EXPECTO! 10 tests run in [^ ]+ for Fogell\.Store\.FG-026 effect checkpoint ledger (-|–) 10 passed, 0 ignored, 0 failed, 0 errored\. Success!( <Expecto>)?$' "$normalized" || true)"
  if [ "$summary_count" != "1" ]; then
    echo "expected one exact full-line 10/10 FG-026 summary, observed ${summary_count:-0}" >&2
    return 1
  fi
}

proof_dir="$(mktemp -d /tmp/fogell-fg026-proof.XXXXXX)" || die "cannot create proof directory"
trap 'rm -rf -- "$proof_dir"' EXIT

good="$proof_dir/good.log"
printf '%s\n' \
  'FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16' \
  'EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 10 passed, 0 ignored, 0 failed, 0 errored. Success!' \
  > "$good"
judge "$good" 0 >/dev/null 2>&1 || die "positive parser control was rejected"

expect_rejected() {
  local name=$1
  local output=$2
  local status=$3

  if judge "$output" "$status" >/dev/null 2>&1; then
    die "parser accepted planted $name output"
  fi

  echo "  rejected planted $name output"
}

skip="$proof_dir/skip.log"
printf '%s\n' \
  'skipped: no PostgreSQL at unavailable' \
  'FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16' \
  'EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 10 passed, 0 ignored, 0 failed, 0 errored. Success!' \
  > "$skip"
expect_rejected skip "$skip" 0

missing_marker="$proof_dir/missing-marker.log"
printf '%s\n' \
  'EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 10 passed, 0 ignored, 0 failed, 0 errored. Success!' \
  > "$missing_marker"
expect_rejected missing-marker "$missing_marker" 0

wrong_count="$proof_dir/wrong-count.log"
printf '%s\n' \
  'FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16' \
  'EXPECTO! 9 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 9 passed, 0 ignored, 0 failed, 0 errored. Success!' \
  > "$wrong_count"
expect_rejected wrong-count "$wrong_count" 0

failed="$proof_dir/failed.log"
printf '%s\n' \
  'FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16' \
  'EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 9 passed, 0 ignored, 1 failed, 0 errored.' \
  > "$failed"
expect_rejected failed-summary-with-zero-exit "$failed" 0

decorated_marker="$proof_dir/decorated-marker.log"
printf '%s\n' \
  'prefix FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16 suffix' \
  'EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 10 passed, 0 ignored, 0 failed, 0 errored. Success!' \
  > "$decorated_marker"
expect_rejected decorated-marker "$decorated_marker" 0

decorated_summary="$proof_dir/decorated-summary.log"
printf '%s\n' \
  'FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16' \
  'prefix EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 10 passed, 0 ignored, 0 failed, 0 errored. Success! suffix' \
  > "$decorated_summary"
expect_rejected decorated-summary "$decorated_summary" 0

duplicate_summary="$proof_dir/duplicate-summary.log"
printf '%s\n' \
  'FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16' \
  'EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 10 passed, 0 ignored, 0 failed, 0 errored. Success! EXPECTO! 10 tests run in 00:00:01 for Fogell.Store.FG-026 effect checkpoint ledger - 10 passed, 0 ignored, 0 failed, 0 errored. Success!' \
  > "$duplicate_summary"
expect_rejected duplicate-summary "$duplicate_summary" 0

expect_rejected nonzero-exit "$good" 7

if [ -z "${FOGELL_TEST_DATABASE_URL:-}" ]; then
  die "FOGELL_TEST_DATABASE_URL is required; the live proof never guesses a database"
fi

store_tests="tests/Fogell.Store.Tests/bin/Release/net10.0/Fogell.Store.Tests.dll"
[ -f "$store_tests" ] || die "missing built Store test binary: $store_tests"

live="$proof_dir/live.log"
dotnet "$store_tests" \
  --filter-test-list 'FG-026 effect checkpoint ledger' \
  --sequenced --colours 0 --no-spinner \
  > "$live" 2>&1
live_status=$?
cat "$live"
judge "$live" "$live_status" || exit 1

echo "FG-026 EFFECT-LEDGER PROOF: parser rejects every planted false-green shape; live PostgreSQL schema 0003/0005/0007 and the exact ten-test ledger slice pass"
