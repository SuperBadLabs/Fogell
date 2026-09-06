#!/usr/bin/env bash
# Start (or restart) the controller against the already-provisioned database.
set -uo pipefail
R=$HOME/fogell-ctrl
source "$R/env"
setsid "$R/controller/Fogell.Controller.Host" >> "$R/controller.log" 2>&1 < /dev/null &
for i in $(seq 1 60); do
  curl -sf -o /dev/null "$FOGELL_LISTEN_URL/health" 2>/dev/null && { echo "controller up (health)"; exit 0; }
  curl -sf -o /dev/null -H "authorization: Bearer $FOGELL_TOKEN" "$FOGELL_BUILDS_URL" 2>/dev/null && { echo "controller up (builds)"; exit 0; }
  sleep 1
done
echo "controller did NOT come up; log tail:"; tail -5 "$R/controller.log"; exit 1
