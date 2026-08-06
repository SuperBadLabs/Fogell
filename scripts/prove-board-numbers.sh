#!/usr/bin/env bash
# FG-162. Proves scripts/audit-board-numbers.bb FAILS on each known-bad state and
# PASSES the compliant one — in the gate, so a regression that makes the checker stop
# matching cannot pass CI while the checker is silently broken.
#
# The board policy requires exactly this: "a cross-cutting invariant is not a ticket
# line, it is a sweep with a test." A checker proven only by hand is one the gate
# cannot re-run, which is the FG-158 defect — a fix indistinguishable from a no-op
# because nothing exercises it. The audit's optional [board ledger] path arguments
# exist for this lane; it mutates scratch COPIES and never touches the committed files.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
AUDIT=scripts/audit-board-numbers.bb
BOARD=docs/EXECUTION_BOARD.md
LEDGER=docs/COMPATIBILITY-LEDGER.tsv

LAB=$(mktemp -d /tmp/fogell-board-proof.XXXXXX)
trap 'rm -rf "$LAB"' EXIT
FAILED=0

# expect_fail <name> <board> <ledger> : the audit MUST exit non-zero
expect_fail() {
  local name=$1 b=$2 l=$3
  if ./"$AUDIT" "$b" "$l" >/dev/null 2>&1; then
    echo "  FAIL: $name — the audit PASSED a known-bad state"
    FAILED=1
  else
    echo "  rejected $name"
  fi
}
expect_pass() {
  local name=$1 b=$2 l=$3
  if ./"$AUDIT" "$b" "$l" >/dev/null 2>&1; then
    echo "  passed $name"
  else
    echo "  FAIL: $name — the audit REJECTED a compliant state"
    ./"$AUDIT" "$b" "$l" 2>&1 | sed 's/^/    | /' | head -4
    FAILED=1
  fi
}

# Everything is DERIVED from the committed files — nothing hard-codes today's counts,
# or the proof silently stops planting drift the moment a real count changes (the very
# fragility this ticket kills). The live tier3 token in the board is found, not assumed.
T3=$(awk -F'\t' '!/^#/ && !/^file\t/ && $2=="3"' "$LEDGER" | wc -l | tr -d ' ')

# The planted token is CONSTRUCTED from the derived count, not edited out of the board.
# Mutating the live token with `sed -E 's/[0-9]+/…/'` replaced the FIRST digit run — the
# 3 in `tier3` — planting `tier78=79`, which is not a tier3 token at all, so the audit
# correctly ignored it and the proof reported a false failure. Building the token says
# exactly what is being planted.
WRONG_T3="tier3=$((T3 - 1))"

cp "$BOARD" "$LAB/board.md"
cp "$LEDGER" "$LAB/ledger.tsv"

echo "=== board-number audit: proven against known-bad states ==="
expect_pass "compliant board" "$LAB/board.md" "$LAB/ledger.tsv"

# 1. board drift: a live tier3= token that disagrees with the ledger
BOARD_TXT=$(cat "$LAB/board.md")
printf '%s\n> planted drift: %s\n' "$BOARD_TXT" "$WRONG_T3" > "$LAB/drift.md"
expect_fail "board drift (tier3 off by one)" "$LAB/drift.md" "$LAB/ledger.tsv"

# 2. a LIVE tier2= claim — forbidden outright, ADR tier 2 is NOT ASSESSED
printf '%s\n> planted claim: tier2=%d admitted files\n' "$BOARD_TXT" "$T3" > "$LAB/tier2.md"
expect_fail "live tier2= claim" "$LAB/tier2.md" "$LAB/ledger.tsv"

# 3. ledger changes under a fixed board: one tier-3 row becomes admitted
awk -F'\t' 'BEGIN{OFS="\t"} $2=="3" && !done {$2="admitted"; done=1} {print}' \
  "$LAB/ledger.tsv" > "$LAB/ledger-drift.tsv"
expect_fail "ledger drift under fixed board" "$LAB/board.md" "$LAB/ledger-drift.tsv"

# 4. a quoted RETRACTION must still pass — flagging quoted history punishes honesty
printf '\n> a row once said "tier3=%d" and was corrected.\n' "$((T3 - 5))" >> "$LAB/board.md"
expect_pass "quoted retraction (exempt)" "$LAB/board.md" "$LAB/ledger.tsv"

if [ "$FAILED" -eq 0 ]; then
  echo "BOARD-NUMBER PROOF: the audit fails every planted drift and passes the compliant and quoted-retraction cases"
else
  echo "BOARD-NUMBER PROOF FAILED"
  exit 1
fi
