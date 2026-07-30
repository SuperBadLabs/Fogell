#!/usr/bin/env bash
# FG-003 — corpus gate. Any scoring run must call this FIRST.
#
# A corpus that drifts silently invalidates every number in
# docs/architecture/BASELINE.md. This refuses to let that happen quietly:
# it fails non-zero and names the offending files.
set -uo pipefail

CORPUS="${FOGELL_CORPUS:-/sn8100/work/exchange/crucible-gate/corpus}"
MANIFEST="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/corpus/CORPUS-SHA256SUMS"
EXPECTED_FILES=228

if [ ! -d "$CORPUS" ]; then
  echo "FAIL: corpus not found at $CORPUS (set FOGELL_CORPUS)" >&2
  exit 2
fi
if [ ! -f "$MANIFEST" ]; then
  echo "FAIL: pinned manifest missing at $MANIFEST" >&2
  exit 2
fi

actual=$(find "$CORPUS/jenkinsfiles" -name '*.Jenkinsfile' 2>/dev/null | wc -l)
if [ "$actual" -ne "$EXPECTED_FILES" ]; then
  echo "FAIL: corpus has $actual files, manifest pins $EXPECTED_FILES" >&2
  exit 1
fi

# The manifest is self-contained: bare filenames, relative to jenkinsfiles/.
cd "$CORPUS/jenkinsfiles" || exit 2
bad=$(sha256sum -c "$MANIFEST" 2>&1 | rg -v ': OK$' || true)

if [ -n "$bad" ]; then
  echo "FAIL: corpus drift detected" >&2
  echo "$bad" | head -10 >&2
  exit 1
fi

ok=$(sha256sum -c "$MANIFEST" 2>/dev/null | rg -c ': OK$')
echo "corpus verified: $ok/$EXPECTED_FILES files match the pinned manifest"
[ "$ok" -eq "$EXPECTED_FILES" ] || { echo "FAIL: only $ok of $EXPECTED_FILES verified" >&2; exit 1; }
exit 0
