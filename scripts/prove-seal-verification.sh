#!/usr/bin/env bash
# FG-161. Proves `--verify-seals` REJECTS the tampering arms below, and ACCEPTS the
# regions the receipt declares outside its seal.
#
# NOT "every way a receipt can be tampered with" — it said that, and it was false. The
# seal covers an ENUMERATED subset of the rendered document, so the comparison contract,
# the printed workspace file listing and the engine notes are trusted, printed and
# UNBOUND: edit a contract line and every seal still matches. Found by the pre-push
# verifier after six earlier rounds of exactly this shape. FG-169 replaces the whole
# design with "hash the document minus delimited unsealed regions"; until then this lane
# proves the arms it lists and nothing wider.
#
# A checker never run against known-bad state is indistinguishable from a broken one.
# This one especially: it is the check that makes every other proven claim mean
# something, and it fails in the quiet direction — a verifier that always returns OK
# looks exactly like a tree with no tampering in it.
#
# Every arm mutates a scratch COPY. The committed receipts are never touched.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

CLI="tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj"
SRC=differential/receipts

LAB=$(mktemp -d /tmp/fogell-seal-proof.XXXXXX)
trap 'rm -rf "$LAB"' EXIT
FAILED=0

verify() { dotnet run --project "$CLI" -- --verify-seals "$1" 2>&1; }

# A fresh directory holding ONE receipt, ready to mutate.
lab_with() {
  local name=$1 dir="$LAB/$2"
  mkdir -p "$dir"
  cp "$SRC/$name" "$dir/r.receipt.txt"
  echo "$dir"
}

expect_reject() {
  local what=$1 dir=$2 want=$3
  local out; out=$(verify "$dir"); local rc=$?
  if [ "$rc" -eq 0 ]; then
    echo "  FAIL: $what — the verifier ACCEPTED a tampered receipt"
    FAILED=1
  elif ! grep -q "$want" <<<"$out"; then
    echo "  FAIL: $what — rejected, but not as $want"
    sed 's/^/    | /' <<<"$out" | head -3
    FAILED=1
  else
    echo "  rejected $what"
  fi
}

expect_accept() {
  local what=$1 dir=$2
  if verify "$dir" >/dev/null 2>&1; then
    echo "  accepted $what"
  else
    echo "  FAIL: $what — the verifier REJECTED a receipt it must accept"
    verify "$dir" | sed 's/^/    | /' | head -3
    FAILED=1
  fi
}

echo "=== seal verification: proven against known-bad receipts ==="

# PRECONDITIONS. Both arms below depend on picking receipts of the right KIND, and a
# rename would silently turn either into a test of nothing.
SEQ=when-conditions.receipt.txt
MULTI=parallel-siblings-finish.receipt.txt
for f in "$SEQ" "$MULTI"; do
  [ -f "$SRC/$f" ] || { echo "  FAIL: fixture $f is gone — this proof would test nothing"; exit 1; }
done
grep -q '^sealed-output: sequence' "$SRC/$SEQ" || {
  echo "  FAIL: $SEQ is not a SEQUENCE receipt — the order arm would prove nothing"; exit 1; }
grep -q '^sealed-output: multiset' "$SRC/$MULTI" || {
  echo "  FAIL: $MULTI is not a MULTISET receipt — the relaxation arm would prove nothing"; exit 1; }

# 0. the committed tree verifies
expect_accept "the committed receipts, unmodified" "$SRC"

# 1. THE ATTACK THIS TICKET IS ABOUT: flip the verdict line and nothing else.
# The scorecard classifies a receipt as proven by READING THIS LINE. Before FG-161 the
# seal did not bind it, and this arm passed — measured, not imagined.
d=$(lab_with "$SEQ" verdict)
sed -i 's/^VERDICT: PROVEN (tier 1).*/VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash/' "$d/r.receipt.txt"
sed -i '0,/^VERDICT:/s//VERDICT: DIVERGED (7)\nZZZ/' "$d/r.receipt.txt"
sed -i '/^ZZZ$/d' "$d/r.receipt.txt"
expect_reject "a FLIPPED VERDICT line" "$d" "SEAL MISMATCH"

# 2. an edited output line
d=$(lab_with "$SEQ" output)
sed -i '0,/^    | /s/^    | .*/    | tampered/' "$d/r.receipt.txt"
expect_reject "an edited OUTPUT line" "$d" "SEAL MISMATCH"

# 3. a DELETED output line
d=$(lab_with "$SEQ" dropped)
sed -i '0,/^    | /{/^    | /d}' "$d/r.receipt.txt"
# Caught by the count check first (the header still declares the old number), which is
# the more specific rejection. The payload is sealed too, so both guards cover it.
expect_reject "a DELETED output line" "$d" "UNREADABLE"

# 4. an edited terminal result
d=$(lab_with "$SEQ" result)
sed -i '0,/^  result:/s/^  result:.*/  result:         failure/' "$d/r.receipt.txt"
expect_reject "an edited RESULT" "$d" "SEAL MISMATCH"

# 5. an edited workspace hash — the load-bearing claim for tier 1
d=$(lab_with "$SEQ" workspace)
sed -i '0,/^  workspace-hash:/s/^  workspace-hash:.*/  workspace-hash: 0000000000000000000000000000000000000000000000000000000000000000/' "$d/r.receipt.txt"
expect_reject "an edited WORKSPACE HASH" "$d" "SEAL MISMATCH"

# 6. an edited case-digest — swapping which case the receipt claims to prove
d=$(lab_with "$SEQ" casedigest)
sed -i 's/^case-digest:.*/case-digest:  1111111111111111111111111111111111111111111111111111111111111111/' "$d/r.receipt.txt"
expect_reject "an edited CASE DIGEST" "$d" "SEAL MISMATCH"

# 7. an edited jenkins-core — a receipt reassigned to a different pinned Jenkins
d=$(lab_with "$SEQ" core)
sed -i 's/^jenkins-core:.*/jenkins-core: 9.99.9/' "$d/r.receipt.txt"
expect_reject "an edited JENKINS CORE" "$d" "SEAL MISMATCH"

# 8. a REORDERED output block in a SEQUENCE receipt. Order IS compared for these, so it
# must be sealed — this is the arm that would fail if FG-167's sort were applied to
# every receipt instead of only multiset ones.
d=$(lab_with "$SEQ" reorder-seq)
python3 - "$d/r.receipt.txt" <<'PY'
import sys
p=sys.argv[1]; ls=open(p).read().split("\n")
idx=[i for i,l in enumerate(ls) if l.startswith("    | ")]
if len(idx) < 2: sys.exit("fixture has fewer than 2 output lines — the reorder arm proves nothing")
ls[idx[0]], ls[idx[1]] = ls[idx[1]], ls[idx[0]]
open(p,"w").write("\n".join(ls))
PY
[ $? -eq 0 ] || { echo "  FAIL: could not plant the sequence reorder"; FAILED=1; }
expect_reject "a REORDERED output block in a SEQUENCE receipt" "$d" "SEAL MISMATCH"

# 9. the same reorder in a MULTISET receipt must be ACCEPTED. FG-167 seals these sorted
# because branch interleaving is decided by OS scheduling, and the receipt says so in
# its own contract block. If this arm ever starts failing, the verifier has begun
# reporting every re-run as tampering — the false positive that blocked this ticket.
d=$(lab_with "$MULTI" reorder-multi)
python3 - "$d/r.receipt.txt" <<'PY'
import sys
p=sys.argv[1]; ls=open(p).read().split("\n")
idx=[i for i,l in enumerate(ls) if l.startswith("    | ")]
if len(idx) < 2: sys.exit("fixture has fewer than 2 output lines")
ls[idx[0]], ls[idx[1]] = ls[idx[1]], ls[idx[0]]
open(p,"w").write("\n".join(ls))
PY
expect_accept "a REORDERED output block in a MULTISET receipt (declared unsealed)" "$d"

# 10. ...but changing the CONTENT of a multiset receipt must still be rejected. Without
# this arm, arm 9 alone is satisfied by a verifier that skips multiset receipts entirely.
d=$(lab_with "$MULTI" content-multi)
sed -i '0,/^    | /s/^    | .*/    | tampered/' "$d/r.receipt.txt"
expect_reject "an edited output line in a MULTISET receipt" "$d" "SEAL MISMATCH"

# 11. an edited comparison NOTE — the disclosure of which relaxations were used
d=$(lab_with "$MULTI" notes)
sed -i '0,/^  multiset mode:/s/^  multiset mode:.*/  multiset mode: nothing to see here/' "$d/r.receipt.txt"
expect_reject "an edited COMPARISON NOTE" "$d" "SEAL MISMATCH"

# 12. a missing sealed-output field is REFUSED, not a pass and not a mismatch. Guessing
# the mode would fail every parallel receipt or stop binding order for every ordinary
# one; "I cannot tell" is the honest third answer.
#
# TWO REJECTION KINDS, and they mean different things: REFUSED is structural — a required
# field missing, or a field appearing twice — and UNREADABLE is a field present but not
# parseable (an unknown sealed-output mode, a malformed timestamps line, a side block
# with no result). Both fail the gate; only one is a forgery signal.
d=$(lab_with "$SEQ" nomode)
sed -i '/^sealed-output:/d' "$d/r.receipt.txt"
expect_reject "a receipt with no sealed-output field" "$d" "REFUSED"

# 13. a truncated file is REFUSED (its required fields are gone) rather than silently OK
d=$(lab_with "$SEQ" truncated)
head -3 "$SRC/$SEQ" > "$d/r.receipt.txt"
expect_reject "a truncated receipt" "$d" "REFUSED"

# 14. the RECOVERED provenance block is declared outside the seal (FG-128), so adding
# one must NOT break verification. Stated as a test because it is a real hole and the
# receipt says so in the file — an accepted, named limit, not an oversight.
d=$(lab_with "$SEQ" recovered)
python3 - "$d/r.receipt.txt" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("VERDICT:", "RECOVERED: this case DIVERGED on an earlier attempt and did not reproduce.\n  fabricated\n\nVERDICT:", 1)
open(p,"w").write(s)
PY
expect_accept "an ADDED RECOVERED block (FG-128, declared unsealed)" "$d"

# 14b. A FLIPPED sealed-output MODE. That field decides HOW the verifier hashes, so
# leaving it unsealed put a key under the mat: a `sequence` receipt whose output happens
# to be sorted could be relabelled `multiset` without breaking the seal, and from then on
# its output could be reordered freely and still verify. Raised by the pre-push verifier,
# which substantiated it on archive-empty-fails.receipt.txt — original, mode-flipped, and
# mode-flipped-plus-reordered all recomputed the same stored seal.
d=$(lab_with "$SEQ" modeflip)
sed -i 's/^sealed-output: sequence/sealed-output: multiset/' "$d/r.receipt.txt"
expect_reject "a FLIPPED sealed-output mode" "$d" "SEAL MISMATCH"

# 14c. A BLANK OUTPUT LINE inserted into a ZERO-output receipt. `String.concat "\n"`
# serialises [] and [""] identically, so before the line count was sealed this receipt
# could gain an output line for free — while the contract in the same file promised that
# adding or dropping a line breaks the seal. Raised by the pre-push verifier.
ZERO=stash-not-carried.b2.receipt.txt
if ! grep -q '^  output (0 lines):' "$SRC/$ZERO"; then
  echo "  FAIL: $ZERO no longer has a zero-output side — the empty-line arm proves nothing"
  FAILED=1
else
  d=$(lab_with "$ZERO" blankline)
  # insert one blank output line directly under the first `output (0 lines):`
  python3 - "$d/r.receipt.txt" <<'PY2'
import sys
p=sys.argv[1]; ls=open(p).read().split("\n")
i=next(k for k,l in enumerate(ls) if l.startswith("  output (0 lines):"))
ls.insert(i+1, "    | ")
open(p,"w").write("\n".join(ls))
PY2
  # Caught by the count check (declares 0, carries 1) BEFORE the hash is recomputed —
  # an earlier and more specific rejection than a mismatch. The seal binds the count too,
  # so removing the count check would still reject this; both guards cover it.
  expect_reject "a BLANK OUTPUT LINE added to a zero-output side" "$d" "UNREADABLE"
fi

# 14d. A DOCTORED OUTPUT COUNT. `output (N lines):` is a printed fact the seal does not
# bind — the seal binds the payload — so changing it to 999 left verification passing
# while the receipt read as something it was not. Raised by the pre-push verifier.
# CHECKED rather than sealed: the count is derived from the payload, so the honest test
# is that the document agrees with itself.
d=$(lab_with "$SEQ" badcount)
sed -i '0,/^  output (/s/^  output (.*/  output (999 lines):/' "$d/r.receipt.txt"
expect_reject "a DOCTORED output line count" "$d" "UNREADABLE"

# 14e. A DUPLICATE FIELD INSIDE A SIDE BLOCK. The top-level duplicate check (arm 16)
# covered receipt fields and stopped there, so a second visible `result:` line inside a
# side block still verified — the reader takes the first match and hashes that, while the
# document shows two conflicting engine results. Raised by the pre-push verifier; sixth
# instance of one class in this ticket, and the second where the previous fix was scoped
# to the level I happened to be looking at instead of to the rule.
d=$(lab_with "$SEQ" dup-side)
sed -i '0,/^  result:/s/^  result:.*/&\n  result:         failure/' "$d/r.receipt.txt"
expect_reject "a DUPLICATED result field inside a side block" "$d" "UNREADABLE"

d=$(lab_with "$SEQ" dup-ws)
sed -i '0,/^  workspace-hash:/s/^  workspace-hash:.*/&\n  workspace-hash: dead/' "$d/r.receipt.txt"
expect_reject "a DUPLICATED workspace-hash inside a side block" "$d" "UNREADABLE"

# 14f. A SECOND, CONFLICTING timestamps() line. It renders a SEALED fact, and extraction
# takes the first match, so a receipt could carry two incompatible coverage claims under a
# valid seal. It was missing from the duplicate-field list while that list was described as
# closing the class. Raised by the pre-push verifier.
TS=$(rg -l '^timestamps\(\): ' "$SRC"/*.receipt.txt | head -1)
if [ -z "$TS" ]; then
  echo "  FAIL: no receipt carries a timestamps() line — this arm proves nothing"
  FAILED=1
else
  d=$(lab_with "$(basename "$TS")" duptimestamps)
  printf 'timestamps(): jenkins=none fogell=none (prefix text excluded, coverage compared and sealed)\n' >> "$d/r.receipt.txt"
  expect_reject "a SECOND conflicting timestamps() line" "$d" "REFUSED"
fi

# 15. A FORGED SECOND VERDICT. The seal binds the FIRST verdict block, and
# `generate-scorecard.bb` classifies tier 1 with a regex matching ANY line — so appending
# one line to a diverged receipt verified AND promoted it. Measured: this arm passed
# before the duplicate refusal existed, on the commit that added seal verification.
# The check that closes a hole shipped with a green tick in front of it.
d=$(lab_with "$SEQ" second-verdict)
printf '\nVERDICT: PROVEN (tier 1) — same result, same output, same workspace hash\n' >> "$d/r.receipt.txt"
expect_reject "a FORGED SECOND VERDICT line" "$d" "REFUSED"

# 16. the same class, on a header field: every reader takes the FIRST match, so a second
# copy of any field is unsealed text a later reader can be steered to. Arm 15 alone would
# be satisfied by a fix that special-cased the verdict.
d=$(lab_with "$SEQ" second-core)
printf '\njenkins-core: 9.99.9\n' >> "$d/r.receipt.txt"
expect_reject "a DUPLICATED jenkins-core field" "$d" "REFUSED"

# 17. a receipt with NO verdict line at all is refused rather than hashed as if fine
d=$(lab_with "$SEQ" no-verdict)
sed -i '/^VERDICT: /d' "$d/r.receipt.txt"
expect_reject "a receipt with NO verdict line" "$d" "REFUSED"

# 18. an EMPTY directory must fail, not report a vacuous pass. The check is loudest
# about success exactly when it is checking nothing.
mkdir -p "$LAB/empty"
if verify "$LAB/empty" >/dev/null 2>&1; then
  echo "  FAIL: an empty receipt directory reported a PASS"
  FAILED=1
else
  echo "  rejected an empty receipt directory"
fi

if [ "$FAILED" -eq 0 ]; then
  echo "SEAL VERIFICATION PROOF: every arm above is rejected, the declared-unsealed regions are accepted, and an empty directory refuses (this is NOT a claim that every possible tamper is caught — see the header and FG-169)"
else
  echo "SEAL VERIFICATION PROOF FAILED"
  exit 1
fi
