#!/usr/bin/env bash
set -uo pipefail
campaign=/tmp/fogell-native-campaign-20260926.rrPLAz
export DOTNET_ROOT="$campaign/sdk" PATH="$campaign/sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
export FOGELL_PG_CONTAINER=fogell-native-campaign-rrPLAz FOGELL_CONTAINER_RUNTIME=podman
cd "$campaign/source"
run() {
 local label=$1; shift
 local started rc
 started=$(date +%s)
 timeout --kill-after=10s 300s setsid --wait "$@" < /dev/null > "$campaign/logs/$label.log" 2>&1
 rc=$?
 printf '%s\t%s\t%s\n' "$label" "$rc" "$(( $(date +%s)-started ))" >> "$campaign/logs/results.tsv"
 printf '%s exit=%s\n' "$label" "$rc"
 tail -2 "$campaign/logs/$label.log"
}
run backup-corrupt env FG085_TEST_CORRUPT_AFTER_HASH=1 ./scripts/backup-restore-drill.sh
run migration-rollback ./scripts/migration-rollback-drill.sh
run migration-corrupt-archive env FG081_TEST_CORRUPT_ARCHIVE_AFTER_HASH=1 ./scripts/migration-rollback-drill.sh
run migration-corrupt-data env FG081_TEST_CORRUPT_ROLLBACK_DATA=1 ./scripts/migration-rollback-drill.sh
run migration-skip-forward env FG081_TEST_SKIP_SECOND_FORWARD=1 ./scripts/migration-rollback-drill.sh
run migration-drop-fk env FG081_TEST_DROP_FORWARD_FK=1 ./scripts/migration-rollback-drill.sh
printf 'OPERATIONAL CAMPAIGN COMPLETE\n'
