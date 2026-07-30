#!/usr/bin/env bash
# FG-000/FG-001 — the gate every ticket must pass before its PR.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
echo "=== sdk ==="; dotnet --version
echo "=== build (warnings are errors for FS0025/FS0026) ==="
dotnet build -c Release --nologo 2>&1 | tail -5
rc=${PIPESTATUS[0]}
[ "$rc" -ne 0 ] && { echo "BUILD FAILED"; exit 1; }
echo "=== tests ==="
fail=0
for p in tests/*/; do
  [ -f "$p/$(basename "$p").fsproj" ] || continue
  echo "--- $(basename "$p") ---"
  dotnet run --project "$p" -c Release --no-build 2>&1 | rg -o "EXPECTO! .*" | tail -1
  [ "${PIPESTATUS[0]}" -ne 0 ] && fail=1
done
[ "$fail" -ne 0 ] && { echo "TESTS FAILED"; exit 1; }
# FG-104. Reports MEASURED claims that name no receipt. NOT blocking yet: 26 pre-date the
# rule and are being annotated. Flip to --strict once that backlog is zero, so the check
# fails a build rather than printing at it.
if [ -x scripts/audit-claims.bb ]; then
  echo "=== claim audit (FG-104, advisory) ==="
  # No `| head`: piping into head can SIGPIPE babashka and mask its exit status, which
  # would make an advisory check silently become a broken one.
  # Status captured explicitly: without it a babashka that cannot start is indistinguishable
  # from a clean audit, and the planned `--strict` flip would never have failed a build.
  if ! audit_out="$(./scripts/audit-claims.bb 2>&1)"; then
    echo "CLAIM AUDIT FAILED TO RUN"; printf '%s\n' "$audit_out"; exit 1
  fi
  printf '%s\n' "$audit_out" | sed -n '1,3p'
  printf '%s\n' "$audit_out" | rg -c "name NO receipt" >/dev/null && \
    echo "  (advisory: run scripts/audit-claims.bb for the full list)"
fi

echo "OK"
