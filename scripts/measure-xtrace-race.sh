#!/usr/bin/env bash
# FG-119. Reproduces the measurement the retry logic in
# tools/Fogell.Differential.Cli/Program.fs rests on.
#
# `dash` writes an `sh -x` trace line in more than one write(), so two stages of
# a SHELL PIPELINE interleave character-by-character: `+ ls out` and `+ wc -l`
# arrive as `+ + lswc -l out`. This is not a receipt-provable claim — it is a
# property of the shell, below the level a differential case can observe — so
# the claim audit would otherwise have to take it on trust. This script is the
# evidence instead: run it and read the numbers.
#
# It answers two questions that were both got WRONG by reasoning first:
#   1. is Fogell's `2>&1`-into-a-pipe capture to blame?   (no — file is no better)
#   2. is Jenkins immune, or merely luckier?              (luckier; both corrupt)
#
# Usage: scripts/measure-xtrace-race.sh [iterations]     (default 200)
#        FOGELL_RACE_CONTAINER_HOST + FOGELL_RACE_CONTAINER opt into the
#        container arm (see the note above it). The names here were wrong —
#        they said FOGELL_JENKINS_HOST/CONTAINER, which the script does not
#        read, so anyone following this line silently got the local arms only.
set -uo pipefail

N="${1:-200}"

# The container arm is OPT-IN. It defaulted to luigi/jenkins-lab, which meant the
# documented command with no environment set would copy files into and execute
# inside the SHARED SINGLE-TENANT lab — the one the differential suite needs
# exclusively, and which this very ticket exists because runs interfere with each
# other. A measurement tool that perturbs the thing being measured is worse than
# no tool. Raised by the pre-push verifier's model review.
#
#   FOGELL_RACE_CONTAINER_HOST=luigi FOGELL_RACE_CONTAINER=jenkins-lab \
#     scripts/measure-xtrace-race.sh
#
# Run it only when the lab is idle. The local arms below need nothing and are
# sufficient for the capture-mechanism question on their own.
FOGELL_JENKINS_HOST="${FOGELL_RACE_CONTAINER_HOST:-}"
FOGELL_JENKINS_CONTAINER="${FOGELL_RACE_CONTAINER:-}"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/race.sh" <<'EOF'
printenv PATH | tr ":" "\n" | head -3 | wc -l > /dev/null
env | sort | grep -c "=" | cat > /dev/null
ls / | head -5 | wc -l | cat > /dev/null
seq 1 20 | tr "\n" " " | wc -c > /dev/null
EOF

# A corrupt row is an xtrace line carrying a SECOND `+ ` from another stage.
# Good enough for a rate measurement on THIS fixed script, whose commands
# contain no literal `+`; it is deliberately NOT the basis of any decision in
# the harness, where `+ echo a + b` is an ordinary trace and the same pattern
# would be a false positive. See the rejection note in Program.fs.
corrupt_rate() {
  local mode="$1" bad=0 i n
  for ((i = 0; i < N; i++)); do
    if [ "$mode" = pipe ]; then
      /bin/sh -xe "$WORK/race.sh" 2>&1 | cat > "$WORK/out"
    else
      /bin/sh -xe "$WORK/race.sh" > "$WORK/out" 2>&1
    fi
    n=$(grep -cE '^\+ .*\+ ' "$WORK/out" || true)
    [ "$n" != 0 ] && bad=$((bad + 1))
  done
  printf '%s' "$bad"
}

echo "=== local host, $N iterations each ==="
echo "  PIPE capture (what Fogell does): $(corrupt_rate pipe) corrupted runs"
echo "  FILE capture (what Jenkins does): $(corrupt_rate file) corrupted runs"
echo "  -> if these are close, the CAPTURE MECHANISM is not the cause."

if [ -z "$FOGELL_JENKINS_HOST" ] || [ -z "$FOGELL_JENKINS_CONTAINER" ]; then
  echo "=== container arm SKIPPED (opt-in) ==="
  echo "  Set FOGELL_RACE_CONTAINER_HOST and FOGELL_RACE_CONTAINER to run it, and only"
  echo "  while the differential lab is idle — it executes inside that container."
  echo "  Without it the local arms above still answer the capture-mechanism question,"
  echo "  but they say NOTHING about whether Jenkins is affected."
  exit 0
fi

echo "=== ${FOGELL_JENKINS_CONTAINER} on ${FOGELL_JENKINS_HOST}, $N iterations ==="
if ! ssh -o BatchMode=yes -o ConnectTimeout=10 "$FOGELL_JENKINS_HOST" true 2>/dev/null; then
  echo "  SKIPPED: ${FOGELL_JENKINS_HOST} unreachable — the local arms above still stand."
  exit 0
fi

# The counter goes in as a FILE. Threading it through ssh -> podman exec -> sh -c
# as a quoted string mangled the grep pattern into a syntax error, and the
# summary line below printed regardless — a broken arm that read like a result,
# which is the whole failure mode this ticket exists to remove.
cat > "$WORK/count.sh" <<COUNT
bad=0
i=0
while [ \$i -lt $N ]; do
  /bin/sh -xe /tmp/fg119-race.sh > /tmp/fg119.out 2>&1
  n=\$(grep -cE '^\+ .*\+ ' /tmp/fg119.out)
  [ "\$n" != 0 ] && bad=\$((bad + 1))
  i=\$((i + 1))
done
echo "  container dash, FILE capture: \$bad corrupted runs"
COUNT

if ! scp -q "$WORK/race.sh" "$FOGELL_JENKINS_HOST":/tmp/fg119-race.sh 2>/dev/null \
   || ! scp -q "$WORK/count.sh" "$FOGELL_JENKINS_HOST":/tmp/fg119-count.sh 2>/dev/null; then
  echo "  SKIPPED: could not stage the scripts on ${FOGELL_JENKINS_HOST}."
  exit 0
fi

if ! ssh "$FOGELL_JENKINS_HOST" \
     "podman cp /tmp/fg119-race.sh ${FOGELL_JENKINS_CONTAINER}:/tmp/fg119-race.sh \
      && podman cp /tmp/fg119-count.sh ${FOGELL_JENKINS_CONTAINER}:/tmp/fg119-count.sh \
      && podman exec ${FOGELL_JENKINS_CONTAINER} /bin/sh /tmp/fg119-count.sh"; then
  echo "  FAILED: the container arm did not run — do NOT read the local numbers as"
  echo "          evidence about Jenkins; that was the mistake this script exists to prevent."
  exit 1
fi

echo "  -> a NON-ZERO count here is the point: Jenkins is not immune either."
