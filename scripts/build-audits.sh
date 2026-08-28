#!/usr/bin/env bash
# FG-226. Compile the audit tools from `scripts/fsx/*.fsx` to native binaries in
# `scripts/bin/`.
#
# WHY THE BINARIES ARE BUILT AND NEVER COMMITTED. An fflat build is NOT
# reproducible — the same source compiled twice differs by six bytes of embedded
# module GUID (measured 2026-08-27), so "rebuild and compare hashes" cannot prove
# a committed binary matches its source. A committed binary would therefore be an
# unauditable assertion sitting in the blocking gate: edit an .fsx, forget to
# rebuild, and the gate runs the OLD logic and passes green, which is the FG-158
# shape exactly. Building here, in the same run that uses them, makes the
# source-to-binary link true by construction instead of by discipline.
#
# `--check` verifies every tool is present and newer than its source, without
# building. That is the STALENESS guard for a developer who edited an .fsx and
# did not rebuild; the gate calls the plain form and rebuilds unconditionally.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
SRC=scripts/fsx
BIN=scripts/bin

TOOLS=(
  audit-board-numbers
  audit-claims
  audit-stale-refs
  count-options
  generate-scorecard
  probe-input
  review-rounds
  sync-scm-cases
)

if ! command -v fflat >/dev/null 2>&1; then
  echo "FAIL: fflat not on PATH — install with: dotnet tool install -g fflat" >&2
  exit 1
fi

if [ "${1:-}" = "--check" ]; then
  missing=0
  for t in "${TOOLS[@]}"; do
    if [ ! -x "$BIN/$t" ]; then
      echo "  MISSING  $BIN/$t"
      missing=1
    elif [ "$SRC/$t.fsx" -nt "$BIN/$t" ] || [ "$SRC/prelude.fsx" -nt "$BIN/$t" ]; then
      # The prelude is a dependency of every tool, so a prelude edit staleness-
      # marks all of them. Missing that is how one tool keeps old shared
      # semantics while its siblings get the new ones.
      echo "  STALE    $BIN/$t (source is newer)"
      missing=1
    fi
  done
  if [ "$missing" -ne 0 ]; then
    echo "FAIL: audit binaries are missing or stale — run scripts/build-audits.sh" >&2
    exit 1
  fi
  echo "audit binaries current: ${#TOOLS[@]} tools"
  exit 0
fi

mkdir -p "$BIN"

# COMPILED IN PARALLEL. Each fflat invocation saturates roughly three cores, and
# there are eight independent tools; run sequentially they cost ~58s of wall time
# on a 32-core host that is idle for most of it. The jobs are capped rather than
# unbounded so this does not thrash a small runner — GitHub's are 4-core, where
# the cap is what keeps it from being SLOWER than sequential.
LOGS=$(mktemp -d /tmp/fogell-build-audits.XXXXXX)
trap 'rm -rf "$LOGS"' EXIT

jobs_max=$(nproc 2>/dev/null || echo 4)
jobs_max=$(( jobs_max / 3 )); [ "$jobs_max" -lt 1 ] && jobs_max=1
[ "$jobs_max" -gt "${#TOOLS[@]}" ] && jobs_max=${#TOOLS[@]}

for t in "${TOOLS[@]}"; do
  while [ "$(jobs -rp | wc -l)" -ge "$jobs_max" ]; do wait -n; done
  {
    # bflat emits a wall of IL2xxx/IL3050 trim-analysis warnings out of FSharp.Core
    # on every build. They are not actionable here and they bury a real error, so
    # only genuine compiler diagnostics are surfaced.
    if fflat "$SRC/$t.fsx" -o "$BIN/$t" >"$LOGS/$t.log" 2>&1; then
      : > "$LOGS/$t.ok"
    fi
  } &
done
wait

built=0
for t in "${TOOLS[@]}"; do
  if [ -e "$LOGS/$t.ok" ]; then
    built=$((built + 1))
  else
    echo "FAIL: $t did not compile" >&2
    grep -E 'error|FS[0-9]{4}' "$LOGS/$t.log" | head -20 >&2
    exit 1
  fi
done
echo "built $built audit tools into $BIN/"
