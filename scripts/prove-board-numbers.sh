#!/usr/bin/env bash
# FG-162 plus the FG-224 accounting closure. Proves audit-board-numbers FAILS
# independently on compatibility drift and canonical board-accounting drift, and
# PASSES the compliant state.
#
# The board policy requires exactly this: "a cross-cutting invariant is not a ticket
# line, it is a sweep with a test." A checker proven only by hand is one the gate
# cannot re-run, which is the FG-158 defect — a fix indistinguishable from a no-op
# because nothing exercises it. The audit's optional [board ledger] path arguments
# exist for this lane; it mutates scratch COPIES and never touches the committed files.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
AUDIT=scripts/bin/audit-board-numbers
BOARD=docs/EXECUTION_BOARD.md
LEDGER=docs/COMPATIBILITY-LEDGER.tsv

if ! ./scripts/build-audits.sh --check >/dev/null 2>&1; then
  echo "BOARD-NUMBER PROOF FAILED: audit binaries missing or stale — run scripts/build-audits.sh" >&2
  exit 1
fi

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
    { ./"$AUDIT" "$b" "$l" 2>&1 || true; } | sed 's/^/    | /' | head -4
    FAILED=1
  fi
}

# Everything is DERIVED from the committed files — nothing hard-codes today's counts,
# or the proof silently stops planting drift the moment a real count changes (the very
# fragility this ticket kills).
T3=$(awk -F'\t' '!/^#/ && !/^file\t/ && $2=="3"' "$LEDGER" | wc -l | tr -d ' ')

# THE BOARD MUST ACTUALLY CARRY A LIVE TOKEN, or the audit checks nothing and passes.
# Its coverage depends on rows using the token form; rewrite the FG-090 row into prose
# and every drift check becomes vacuous while still reporting green. This asserts the
# coverage exists before proving the checker behaves — the two are different claims.
#
# (This comment previously said the live token "is found, not assumed" — describing
# code I had replaced with token CONSTRUCTION two commits earlier, and left standing.
# The assertion below now makes the sentence true rather than deleting it.)
if ! grep -oE '(^|[^"])tier3=\*{0,2}[0-9]+' "$BOARD" >/dev/null 2>&1; then
  echo "  FAIL: the board carries no LIVE tier3= token — the audit would pass vacuously"
  exit 1
fi

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

# 3. EACH audited count planted independently. The ledger-drift case alone moves BOTH
# tier3 and admitted, and the tier3 token is enough to make the audit fail — so a
# regression that broke only `admitted` handling would still have looked proven. One
# planted token per count, each with the others left correct.
T1=$(awk -F'\t' '!/^#/ && !/^file\t/ && $2=="1"' "$LEDGER" | wc -l | tr -d ' ')
ADM=$(awk -F'\t' '!/^#/ && !/^file\t/ && $2=="admitted"' "$LEDGER" | wc -l | tr -d ' ')

printf '%s\n> planted drift: admitted=%d\n' "$BOARD_TXT" "$((ADM + 1))" > "$LAB/adm.md"
expect_fail "admitted drift alone" "$LAB/adm.md" "$LAB/ledger.tsv"

printf '%s\n> planted drift: tier1=%d\n' "$BOARD_TXT" "$((T1 + 1))" > "$LAB/t1.md"
expect_fail "tier1 drift alone" "$LAB/t1.md" "$LAB/ledger.tsv"

# 4. ledger changes under a fixed board: one tier-3 row becomes admitted
awk -F'\t' 'BEGIN{OFS="\t"} $2=="3" && !done {$2="admitted"; done=1} {print}' \
  "$LAB/ledger.tsv" > "$LAB/ledger-drift.tsv"
expect_fail "ledger drift under fixed board" "$LAB/board.md" "$LAB/ledger-drift.tsv"

# 4. a quoted RETRACTION must still pass — flagging quoted history punishes honesty
printf '\n> a row once said "tier3=%d" and was corrected.\n' "$((T3 - 5))" >> "$LAB/board.md"
expect_pass "quoted retraction (exempt)" "$LAB/board.md" "$LAB/ledger.tsv"

# Ticket accounting is parsed from canonical Wave rows. Each construction below
# changes a scratch copy only and leaves all unrelated compatibility tokens intact.

# 5. A new legal row must move rows/open/P3. If the audit ever returns to trusting
# the prose summary, this is the exact manual-increment failure it would miss.
awk '
  /^## Standing risks/ && !inserted {
    print "| FG-9999 | P3 | TODO | planted row | planted acceptance |"
    inserted=1
  }
  { print }
' "$LAB/board.md" > "$LAB/row-added.md"
expect_fail "canonical row added without accounting update" "$LAB/row-added.md" "$LAB/ledger.tsv"

# 6. Legal status and priority changes keep the row structurally valid but must move
# the corresponding derived totals.
awk '
  /^\| FG-000 \|/ { sub(/\*\*DONE\*\*/, "**PARTIAL**") }
  { print }
' "$LAB/board.md" > "$LAB/status-drift.md"
expect_fail "legal status drift" "$LAB/status-drift.md" "$LAB/ledger.tsv"

awk '
  /^\| FG-004b \|/ { sub(/\| P1 \|/, "| P2 |") }
  { print }
' "$LAB/board.md" > "$LAB/priority-drift.md"
expect_fail "legal priority drift" "$LAB/priority-drift.md" "$LAB/ledger.tsv"

# 7. Vocabulary is closed. These cases test legality independently of the summary
# comparison: replacing one legal cell cannot be accepted as a fifth state or tier.
awk '
  /^\| FG-004b \|/ { sub(/\| TODO \|/, "| OPEN |") }
  { print }
' "$LAB/board.md" > "$LAB/illegal-status.md"
expect_fail "illegal status vocabulary" "$LAB/illegal-status.md" "$LAB/ledger.tsv"

awk '
  /^\| FG-004b \|/ { sub(/\| P1 \|/, "| P9 |") }
  { print }
' "$LAB/board.md" > "$LAB/illegal-priority.md"
expect_fail "illegal priority vocabulary" "$LAB/illegal-priority.md" "$LAB/ledger.tsv"

# 8. Duplicate ids preserve every aggregate, so only the identity check can reject
# this shape. It directly pins the historical collision that made row and id counts
# disagree.
awk '
  /^\| FG-001 \|/ { sub(/FG-001/, "FG-000") }
  { print }
' "$LAB/board.md" > "$LAB/duplicate-id.md"
expect_fail "duplicate canonical id with unchanged totals" "$LAB/duplicate-id.md" "$LAB/ledger.tsv"

# 9. A ticket-looking row that cannot be parsed is a refusal, not an ignored line.
# Since an invalid id is excluded from the derived totals, this construction isolates
# the malformed-row check from summary drift.
awk '
  /^## Standing risks/ && !inserted {
    print "| FG-bad | P1 | TODO | planted malformed row | planted acceptance |"
    inserted=1
  }
  { print }
' "$LAB/board.md" > "$LAB/malformed-row.md"
expect_fail "malformed canonical row" "$LAB/malformed-row.md" "$LAB/ledger.tsv"

# 10. The summary is exactly one anchored line. A stale value, absence, or duplicate
# all fail; quoted history cannot accidentally satisfy this anchored shape.
awk '
  /^\*\*BOARD ACCOUNTING \(derived\):/ && !changed {
    sub(/rows=[0-9]+/, "rows=1")
    changed=1
  }
  { print }
' "$LAB/board.md" > "$LAB/summary-drift.md"
expect_fail "derived summary drift" "$LAB/summary-drift.md" "$LAB/ledger.tsv"

awk '!/^\*\*BOARD ACCOUNTING \(derived\):/' "$LAB/board.md" > "$LAB/summary-missing.md"
expect_fail "missing derived summary" "$LAB/summary-missing.md" "$LAB/ledger.tsv"

awk '
  { print }
  /^\*\*BOARD ACCOUNTING \(derived\):/ && !duplicated { print; duplicated=1 }
' "$LAB/board.md" > "$LAB/summary-duplicate.md"
expect_fail "duplicate derived summary" "$LAB/summary-duplicate.md" "$LAB/ledger.tsv"

# 11. Wave topology is part of the accounting contract. These three mutations keep
# every ticket row and aggregate unchanged, so only the exact-heading/nonempty checks
# can reject them.
awk '!/^## Wave 3\.5 /' "$LAB/board.md" > "$LAB/wave-missing.md"
expect_fail "missing expected Wave heading" "$LAB/wave-missing.md" "$LAB/ledger.tsv"

awk '
  { print }
  /^## Wave 3\.5 / && !duplicated { print; duplicated=1 }
' "$LAB/board.md" > "$LAB/wave-duplicate.md"
expect_fail "duplicate Wave heading" "$LAB/wave-duplicate.md" "$LAB/ledger.tsv"

awk '
  /^## Wave 3\.5 / && !inserted { print "## Wave 3.4 — planted unexpected Wave"; inserted=1 }
  { print }
' "$LAB/board.md" > "$LAB/wave-unexpected.md"
expect_fail "unexpected Wave heading" "$LAB/wave-unexpected.md" "$LAB/ledger.tsv"

awk '
  /^## Wave 3\.5 / { held=$0; next }
  /^## Wave 3\.6 / { print held }
  { print }
' "$LAB/board.md" > "$LAB/wave-empty.md"
expect_fail "expected Wave with no canonical rows" "$LAB/wave-empty.md" "$LAB/ledger.tsv"

# 12. Redirects carry their target's status. The mismatch construction compensates
# with an unrelated same-priority row, preserving every published aggregate; the
# missing-target construction changes only the redirect prose.
awk '
  /^\| FG-038b \|/ { sub(/\| TODO \|/, "| **DONE** |") }
  /^\| FG-111 \|/ { sub(/\| \*\*DONE\*\* \|/, "| TODO |") }
  { print }
' "$LAB/board.md" > "$LAB/redirect-mismatch.md"
expect_fail "redirect status disagrees with target" "$LAB/redirect-mismatch.md" "$LAB/ledger.tsv"

awk '
  /^\| FG-038b \|/ { sub(/MOVED to FG-113/, "MOVED to FG-9998") }
  { print }
' "$LAB/board.md" > "$LAB/redirect-missing.md"
expect_fail "redirect target missing" "$LAB/redirect-missing.md" "$LAB/ledger.tsv"

if [ "$FAILED" -eq 0 ]; then
  echo "BOARD-NUMBER PROOF: compatibility, Wave topology, redirect and canonical accounting audits fail every planted drift and pass the compliant and quoted-retraction cases"
else
  echo "BOARD-NUMBER PROOF FAILED"
  exit 1
fi
