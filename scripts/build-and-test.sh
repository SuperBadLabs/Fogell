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
echo "OK"
