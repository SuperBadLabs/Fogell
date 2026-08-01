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
# FG-104. BLOCKING. Every MEASURED claim must cite a receipt or admit UNPROVEN. The
# backlog it was introduced against (30) is zero, so the check now fails the build instead
# of printing at it — an advisory check nobody must act on decays into noise.
if [ -x scripts/audit-claims.bb ]; then
  echo "=== claim audit (FG-104, blocking) ==="
  # No `| head`: piping into head can SIGPIPE babashka and mask its exit status, which
  # would make an advisory check silently become a broken one.
  # Status captured explicitly: without it a babashka that cannot start is indistinguishable
  # from a clean audit, and the planned `--strict` flip would never have failed a build.
  if ! audit_out="$(./scripts/audit-claims.bb --strict 2>&1)"; then
    echo "CLAIM AUDIT FAILED"; printf '%s\n' "$audit_out"; exit 1
  fi
  printf '%s\n' "$audit_out" | sed -n '1,3p'

fi

# FG-112: the restart lane is self-contained (dotnet + bash + a SIGKILL) and
# is the ONLY automated coverage of PersistenceHooks/resume — it runs in the
# gate so the headline durability semantics cannot silently regress.
echo "=== restart lane (FG-112, blocking) ==="
./scripts/run-restart-lane.sh || { echo "RESTART LANE FAILED"; exit 1; }

echo "OK"
