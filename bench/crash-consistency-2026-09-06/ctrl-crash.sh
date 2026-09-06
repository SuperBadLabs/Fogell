#!/usr/bin/env bash
# Crash consistency against Fogell.Controller.Host -- the real server, with the
# resume machinery (effect_checkpoints, restart-discovered admission,
# attempt-keyed retry journals) that Run.Host does not have.
#
# Same shape as the three-engine test: stage 2 writes its marker THEN sleeps, so
# the SIGKILL lands after the effect and before the completion record.
set -uo pipefail
R=$HOME/fogell-ctrl
source "$R/env"
KILL_AFTER=${KILL_AFTER:-15}
SLEEP_IN_STEP=${SLEEP_IN_STEP:-45}

for run in 1 2; do
  TAG=ctrl-$(date +%s)-$run
  M=$HOME/crashlab/marker-$TAG.txt
  JF=$HOME/crashlab/$TAG.Jenkinsfile
  {
    echo 'pipeline {'
    echo '  agent any'
    echo '  stages {'
    echo "    stage('s1') { steps { sh \"echo s1 >> $M\" } }"
    echo "    stage('s2') { steps { sh \"echo s2 >> $M; sleep $SLEEP_IN_STEP\" } }"
    echo "    stage('s3') { steps { sh \"echo s3 >> $M\" } }"
    echo '  }'
    echo '}'
  } > "$JF"

  code=$(curl -s -o "$HOME/crashlab/$TAG.json" -w '%{http_code}' -X POST \
    -H "authorization: Bearer $FOGELL_TOKEN" -H "idempotency-key: $TAG" \
    -H 'content-type: application/x-jenkinsfile' --data-binary @"$JF" "$FOGELL_BUILDS_URL")
  B=$(sed -n 's/.*"build_id":"\([^"]*\)".*/\1/p' "$HOME/crashlab/$TAG.json")
  echo "=== run $run: submit=$code build=$B ==="
  [ "$code" = 201 ] || { echo "  submit failed"; continue; }

  sleep "$KILL_AFTER"
  BEFORE=$(sort "$M" 2>/dev/null | uniq -c | tr '\n' ' ')
  CPID=$(pgrep -f "fogell-ctrl/controller/Fogell.Controller.Host" | head -1)
  kill -9 "$CPID" 2>/dev/null
  echo "  killed controller pid=$CPID   markers at kill: ${BEFORE:-none}"
  sleep 3
  # did the controller take Run.Host down with it?
  echo "  orphaned Run.Host procs after kill: $(pgrep -cf 'fogell-ctrl/runhost/Fogell.Run.Host')"

  bash "$R/ctrl-start.sh" >/dev/null 2>&1
  sleep 2
  S=none
  for i in $(seq 1 90); do
    RESP=$(curl -s -H "authorization: Bearer $FOGELL_TOKEN" "$FOGELL_BUILDS_URL/$B" 2>/dev/null)
    S=$(sed -n 's/.*"status":"\([^"]*\)".*/\1/p' <<<"$RESP")
    case "$S" in success|failure|aborted|reconciliation_required) break;; esac
    sleep 2
  done
  AFTER=$(sort "$M" 2>/dev/null | uniq -c | tr '\n' ' ')
  S2=$(rg -Nc '^s2$' "$M" 2>/dev/null || echo 0)
  echo "  markers at end : ${AFTER:-none}"
  echo "  controller verdict: $S"
  if [ "$S2" -ge 2 ]; then echo "  -> DUPLICATE SIDE EFFECT: s2 ran ${S2}x (at-least-once)"
  elif [ "$S2" = 1 ]; then echo "  -> one effect across the crash"
  else echo "  -> effect never happened"; fi
  echo
done
