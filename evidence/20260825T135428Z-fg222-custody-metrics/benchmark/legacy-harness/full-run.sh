#!/usr/bin/env bash
# full-run.sh — the complete trifecta matrix on a quieted luigi.
set -uo pipefail
cd "$(dirname "$0")"

QUIETED="chengis-jp065 chengis-war jenkins-lab ctrl ag1"
echo "== quieting host: stopping $QUIETED"
for c in $QUIETED; do podman stop -t 10 "$c" 2>/dev/null && echo "  stopped $c"; done

restore() {
  echo "== restoring containers"
  for c in $QUIETED; do podman start "$c" 2>/dev/null && echo "  restarted $c"; done
}
trap restore EXIT

ENGINES=jenkins,jenkins-perfopt,fogell,mcloving

echo "== pass 1: startup,idle-rss,correctness (iters 3)"
bb bench.bb "$ENGINES" startup,idle-rss,correctness --iters 3

echo "== pass 2: echo-e2e,parallel (iters 15)"
bb bench.bb "$ENGINES" echo-e2e,parallel --iters 15

echo "== pass 3: per-step (iters 8)"
bb bench.bb "$ENGINES" per-step --iters 8

echo "== pass 4: step-ladder"
bb bench.bb "$ENGINES" step-ladder --iters 1

echo "== ALL PASSES COMPLETE"
