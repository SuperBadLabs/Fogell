#!/usr/bin/env bash
# FG-198. Proves scripts/audit-queue-rows.py FAILS on each planted overclaim shape,
# PASSES the committed board, stays SILENT on a quoted retraction carrying a bare
# apostrophe, and REFUSES a board whose queue tables it cannot find — in the gate,
# per the operating contract: a checker must be proven to fail before it is trusted.
#
# The apostrophe fixture is not decoration: the first version of this checker
# flagged a retraction as a live claim because `FG-183's` defeated naive
# quote-pairing, and that is the failure mode this checker is most exposed to.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
AUDIT=scripts/audit-queue-rows.py
BOARD=docs/EXECUTION_BOARD.md

LAB=$(mktemp -d /tmp/fogell-queue-proof.XXXXXX)
trap 'rm -rf "$LAB"' EXIT
FAILED=0

expect_fail() {
  local name=$1 b=$2
  if ./"$AUDIT" "$b" >/dev/null 2>&1; then
    echo "  FAIL: $name — the audit PASSED a known-bad state"
    FAILED=1
  else
    echo "  rejected $name"
  fi
}
expect_pass() {
  local name=$1 b=$2
  if ./"$AUDIT" "$b" >/dev/null 2>&1; then
    echo "  passed $name"
  else
    echo "  FAIL: $name — the audit REJECTED a compliant state"
    { ./"$AUDIT" "$b" 2>&1 || true; } | sed 's/^/    | /' | head -6
    FAILED=1
  fi
}

# Plant a row INSIDE the Track 1 table, not appended at EOF — the checker scans
# only the queue region, and a fixture outside it would prove nothing while
# reporting rejected (the vacuous-coverage mistake prove-board-numbers.sh names).
# Anchored on the TABLE HEADER SEPARATOR, not on any ticket row: the first anchor
# was the FG-195 row, and the proof died loudly in the gate the day that row was
# marked DONE — a ticket row is a moving target, the table's own shape is not.
plant_row() {
  local cell=$1 out=$2
  awk -v cell="$cell" '
    { print }
    /^### Track 1 /       { in1 = 1 }
    in1 && /^\|---/ && !done {
      print "| 99 | — | FG-999 | TODO | " cell " |"
      done = 1
    }
  ' "$BOARD" > "$out"
  # the fixture must actually have landed in the table, or the proof is vacuous
  grep -q "FG-999" "$out" || { echo "  FAIL: plant did not land — anchor row moved"; exit 1; }
}

echo "=== queue-row audit: proven against planted overclaims ==="
expect_pass "committed board" "$BOARD"

# 1. the CAUSE/FIX shape — the FG-183 wording that cost an AST node to disprove
plant_row "Measured once; the fix is a placement walk at admission, self-contained, no interpreter change" "$LAB/fix.md"
expect_fail "planted cause/fix claim" "$LAB/fix.md"

# 2. the SCOPE shape — the sub-shape three sweeps read past (FG-193/FG-196 wording)
plant_row "An unrun construction which if real is class A and fires on any step longer than two minutes" "$LAB/scope.md"
expect_fail "planted scope claim" "$LAB/scope.md"

# 3. the REORDER-FRAGILE shape — positional plus universal in one row
plant_row "Follows FG-195 at the head of the B block; every ticket above it is measured" "$LAB/positional.md"
expect_fail "planted positional claim" "$LAB/positional.md"

# 4. a quoted retraction with a bare apostrophe OUTSIDE the quotes must pass:
#    every tell sits inside double quotes, and FG-183's apostrophe must not
#    open a span that swallows the closing quote's mate
plant_row "THIS SAID \"the fix is trivial, self-contained, fires on every ticket\" UNTIL 2026-08-16, which repeated FG-183's error" "$LAB/retraction.md"
expect_pass "quoted retraction with bare apostrophe (exempt)" "$LAB/retraction.md"

# 5. a board whose queue tables cannot be found is a refusal, not a clean pass —
#    absence of rows must never read as absence of violations
sed 's/^### Track 1 — Correctness$/### Renamed Away/' "$BOARD" > "$LAB/no-queue.md"
expect_fail "queue tables missing (refuses vacuous pass)" "$LAB/no-queue.md"

# 6. an ESCAPED pipe is literal cell text, not a cell boundary — splitting on it
#    truncated the scanned cell one character before the tell, a confirmed evasion
#    from the FG-201-cycle verifier
plant_row "Measured once; the fix is a placement walk \\\\| and the rest of the cell" "$LAB/escaped-pipe.md"
expect_fail "tell hidden behind an escaped pipe" "$LAB/escaped-pipe.md"

# 7. two ADJACENT quoted spans must not have their flanking words merged into a
#    tell — substituting a bare space for each span produced `the … fix … is`
#    from a compliant retraction, the verifier's confirmed false positive
plant_row "This row once said the \"wrong thing about a\" fix \"and the claim\" is withdrawn with its receipt" "$LAB/adjacent-quotes.md"
expect_pass "adjacent quoted spans do not merge into a tell" "$LAB/adjacent-quotes.md"

if [ "$FAILED" -eq 0 ]; then
  echo "QUEUE-ROW PROOF: the audit fails every planted shape, passes the committed board and the quoted retraction, and refuses a board it cannot parse"
else
  echo "QUEUE-ROW PROOF FAILED"
  exit 1
fi
