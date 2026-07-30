#!/usr/bin/env bash
# FG-005 — evidence convention. Seals a ticket's receipt so it verifies standalone.
#   usage: scripts/seal-evidence.sh FG-010 [extra-file ...]
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
TICKET="${1:?usage: seal-evidence.sh <TICKET-ID> [files...]}"; shift || true
STAMP="$(git log -1 --format=%cd --date=format:%Y%m%dT%H%M%SZ)"
DIR="evidence/${STAMP}-${TICKET,,}"
mkdir -p "$DIR"

# FG-104 review finding: a seal run before `git add` records a diff that OMITS every
# untracked file — the FG-104 bundle validated an intermediate patch and left out the audit
# script itself, which was the entire deliverable. Evidence that silently excludes the work
# is worse than no evidence, because it carries a checksum.
UNTRACKED="$(git ls-files --others --exclude-standard)"
if [ -n "$UNTRACKED" ]; then
  echo "REFUSING TO SEAL: untracked files would be omitted from the evidence:" >&2
  printf '  %s\n' $UNTRACKED >&2
  echo "Stage them (git add) so the sealed diff covers the actual change." >&2
  exit 1
fi

git diff HEAD --stat > "$DIR/diffstat.txt" 2>/dev/null
git diff HEAD          > "$DIR/candidate.diff" 2>/dev/null
git status --short     > "$DIR/status-before-commit.txt"
git rev-parse HEAD     > "$DIR/base-commit.txt"
git ls-files           > "$DIR/tree.txt"
./scripts/verify-corpus.sh > "$DIR/corpus-gate.log" 2>&1 || echo "CORPUS GATE FAILED" >> "$DIR/corpus-gate.log"
{ dotnet build -c Release --nologo 2>&1 | tail -20; } > "$DIR/build.log"
for t in tests/*/; do
  [ -d "$t" ] || continue
  n="$(basename "$t")"
  dotnet run --project "$t" -c Release --no-build 2>&1 | rg -o "EXPECTO!.*" > "$DIR/tests-$n.log" || true
done
for f in "$@"; do [ -f "$f" ] && cp "$f" "$DIR/"; done

( cd "$DIR" && sha256sum $(ls | grep -v '^SHA256SUMS$') > SHA256SUMS )
echo "sealed $DIR"
echo "  manifest: $(sha256sum "$DIR/SHA256SUMS" | cut -c1-16)"
