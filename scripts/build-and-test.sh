#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
: "${FOGELL_TEST_DATABASE_URL:?Set this to an explicitly disposable PostgreSQL database}"
python3 scripts/check-native-tree.py
dotnet restore Fogell.slnx --locked-mode --disable-parallel -m:1
dotnet build Fogell.slnx -c Release --no-restore -m:1
# A dedicated session avoids inheriting init's process group in containers.
timeout --kill-after=5s 300s setsid --wait ./scripts/run-project-tests.sh
if [[ -n "${FOGELL_PG_CONTAINER:-}" ]]; then
  python3 scripts/prove-native.py --container "$FOGELL_PG_CONTAINER" \
    --port "${FOGELL_PG_PORT:?Set the selected disposable container loopback port}" \
    --runtime "${FOGELL_CONTAINER_RUNTIME:-podman}"
else
  python3 scripts/prove-native.py
fi
printf 'OK native pipeline gate\n'
