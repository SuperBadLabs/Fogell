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

# FG-104b: a comment naming a mechanism the code no longer has. Three of those
# landed in one day and every one was caught by a reviewer rather than a check —
# `audit-claims.bb` asks a different question (does a MEASURED claim name a
# receipt) that a stale identifier passes trivially. Proven to fail before being
# trusted: with a definition deleted and its comment left behind it reports the
# line; on a clean tree it is silent.
echo "=== stale-reference audit + its own proof (FG-104b, blocking) ==="
# the proof runs FIRST and in scratch repositories: a checker nobody has watched
# fail is a claim, and this one has twice been wrong about its own job
./scripts/prove-stale-refs.sh || { echo "STALE-REF PROOF FAILED"; exit 1; }
./scripts/audit-stale-refs.bb "${FOGELL_STALE_REF_BASE:-origin/main}" --strict \
  || { echo "STALE REFERENCE AUDIT FAILED"; exit 1; }

# FG-112: the restart lane is self-contained (dotnet + bash + a SIGKILL) and
# is the ONLY automated coverage of PersistenceHooks/resume — it runs in the
# gate so the headline durability semantics cannot silently regress.
echo "=== restart lane (FG-112, blocking) ==="
./scripts/run-restart-lane.sh || { echo "RESTART LANE FAILED"; exit 1; }

# FG-046b: same argument for durable APPROVAL — a human's answer surviving a
# kill is the one guarantee no receipt can cover (the differential harness has
# no approver on either side), so its only proof is this lane.
# FG-046e: scenario N's watcher earns its place only if it is proven to REPORT
# a breach. Runs first, on planted overlaps, in scratch directories.
echo "=== inbox-watcher proof (FG-046e, blocking) ==="
./scripts/prove-approval-watcher.sh || { echo "APPROVAL-WATCH PROOF FAILED"; exit 1; }

echo "=== approval lane (FG-046b, blocking) ==="
./scripts/run-approval-lane.sh || { echo "APPROVAL LANE FAILED"; exit 1; }

echo "OK"
