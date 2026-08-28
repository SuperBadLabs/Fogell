#!/usr/bin/env bash
# FG-174. Proves the CITATION check in `audit-claims` — every receipt a comment
# names must exist.
#
# WHY THIS EXISTS. That check was added after the SEVENTH documentation overclaim on
# one branch: a comment citing `script-sh-returnstdout`, a receipt nobody ever wrote.
# Six earlier ones were each answered with a more careful sentence, and the seventh
# arrived anyway — the answer was never a sentence. But a checker nobody has seen FAIL
# is itself a claim, and this repo's rule is that a checker must be proven to fail.
#
# It runs against a SYNTHETIC root, not this repo: the audit derives its root from its
# own path, so a copy of the script beside a planted `differential/receipts` and a
# planted source file exercises it end to end in milliseconds — and the arms stay
# readable, which they would not be if each one had to mutate the real tree and put it
# back. The failure mode that costs a real tree a bad restore is designed out.
#
# BOTH DIRECTIONS ARE ARMS. A check that rejects everything is not a check, and the
# false positive this one nearly shipped was prose — "left a re-run receipt
# byte-identical to a first-attempt pass" read as a citation of `byte-identical`. The
# ACCEPT arms below are what keep the rule narrow enough to stay switched on.
set -uo pipefail

cd "$(dirname "$0")/.."
AUDIT="$PWD/scripts/bin/audit-claims"

if ! ./scripts/build-audits.sh --check >/dev/null 2>&1; then
  echo "CLAIM-CITATION PROOF FAILED: audit binaries missing or stale — run scripts/build-audits.sh" >&2
  exit 1
fi

fails=0
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# One synthetic root per arm, so no arm can be contaminated by the last one's plant.
new_root() {
  local root="$tmp/case-$1"
  # `src/Planted/`, NOT `src/` — the audit globs `src/**/*.fs`, which needs a directory
  # level. A first version planted `src/Planted.fs`, scanned ZERO files, and every
  # ACCEPT arm passed for that reason. Only the REJECT arms exposed it, which is the
  # argument for having both.
  mkdir -p "$root/scripts/bin" "$root/differential/receipts" "$root/src/Planted"
  # `scripts/bin/`, NOT `scripts/`: the compiled audit derives the repository
  # root as THREE parents from its own location, one deeper than the .bb it
  # replaced. Planting it at the old depth makes it audit the fixture's
  # PARENT and every arm passes vacuously.
  cp "$AUDIT" "$root/scripts/bin/audit-claims"
  # The citable set for every arm. `multi-case` is a MULTI-BUILD case, stored per
  # build; `fam-one`/`fam-two` are a family cited by glob.
  for r in real-case multi-case.b1 multi-case.b2 fam-one fam-two; do
    : > "$root/differential/receipts/$r.receipt.txt"
  done
  echo "$root"
}

# expect <reject|accept> <label> <F# comment body on stdin>
expect() {
  local want="$1" label="$2"
  local root; root="$(new_root "$(echo "$label" | tr -c 'a-zA-Z0-9' '-')")"
  cat > "$root/src/Planted/Planted.fs"
  local out rc
  out="$("$root/scripts/bin/audit-claims" --strict 2>&1)"; rc=$?

  case "$want" in
    reject)
      # NOT merely "exited non-zero": the CITATION check must be what failed. The
      # MEASURED check shares the exit code, so an arm that planted an uncited claim
      # by accident would otherwise pass while proving nothing.
      if [ "$rc" -ne 0 ] && echo "$out" | grep -q "citation(s) name something that does not exist"; then
        echo "  ok      rejects: $label"
      else
        echo "  FAILED  should reject: $label (rc=$rc)"
        echo "$out" | sed 's/^/            /'
        fails=$((fails + 1))
      fi
      ;;
    accept)
      if [ "$rc" -eq 0 ]; then
        echo "  ok      accepts: $label"
      else
        echo "  FAILED  should accept: $label (rc=$rc)"
        echo "$out" | sed 's/^/            /'
        fails=$((fails + 1))
      fi
      ;;
  esac
}

echo "=== citations that must be REJECTED ==="

expect reject "colon form naming a receipt that does not exist" <<'EOF'
module Planted
// Receipt: nope-not-real
let x = 1
EOF

expect reject "backticked citation naming a receipt that does not exist" <<'EOF'
module Planted
// Behaviour held by receipt `also-not-real`.
let x = 1
EOF

# THE WRAP CASE, and the reason the matcher works on whole comment blocks. These
# comments wrap constantly, so "receipts" routinely ends one line and the name begins
# the next; a per-line matcher misses exactly those, which makes its coverage a
# function of where the text happened to wrap.
expect reject "citation split across two comment lines" <<'EOF'
module Planted
// The behaviour below is proven by receipts
// `wrapped-missing`, which is where the numbers come from.
let x = 1
EOF

expect reject "one bad name in a list of good ones" <<'EOF'
module Planted
// Proven by receipts `real-case`, `fam-one` and `sneaked-in-missing`.
let x = 1
EOF

expect reject "a real receipt name with a typo'd build suffix" <<'EOF'
module Planted
// Receipt: multi-case.b9
let x = 1
EOF

# THE FIXTURE THAT COULD NOT FAIL. The accept arm below cites `multi-case.b1/.b2`
# and BOTH exist, so it passed whether the checker validated one suffix or both — and
# it validated one. Raised in review on PR #53, and it is the same weakness the ACCEPT
# arms had when they were passing on zero scanned files: a fixture that cannot
# distinguish the right answer from the wrong one is not evidence.
expect reject "a compact citation whose SECOND build does not exist" <<'EOF'
module Planted
// Behaviour held by receipts `multi-case.b1/.b9`.
let x = 1
EOF

# THE FIXTURE GAP THAT HID IT. Conjunctions were exercised only in the BACKTICKED
# form, so the colon form's comma-only separator list went unchecked and a second
# citation after `and` was never resolved. Both forms now have an arm.
expect reject "colon form, second name after 'and' does not exist" <<'EOF'
module Planted
// Receipts: real-case and missing-after-and
let x = 1
EOF

expect reject "a glob matching nothing" <<'EOF'
module Planted
// Proven by receipts `nosuchfamily-*`.
let x = 1
EOF

echo
echo "=== spellings that must be ACCEPTED ==="

expect accept "plain backticked citation of a receipt that exists" <<'EOF'
module Planted
// Behaviour held by receipt `real-case`.
let x = 1
EOF

expect accept "colon form" <<'EOF'
module Planted
// Receipt: real-case
let x = 1
EOF

# A comment cites the CASE; the receipts are stored per build. Demanding the comment
# name a build number would make it worse for the reader, not better.
expect accept "multi-build case cited by its base name" <<'EOF'
module Planted
// Receipt: multi-case
let x = 1
EOF

expect accept "multi-build case cited as b1/.b2" <<'EOF'
module Planted
// Behaviour held by receipts `multi-case.b1/.b2`.
let x = 1
EOF

expect accept "a family cited by glob" <<'EOF'
module Planted
// Behaviour held by receipts `fam-*`.
let x = 1
EOF

expect accept "the file spelling" <<'EOF'
module Planted
// Behaviour held by receipt `real-case.receipt.txt`.
let x = 1
EOF

# THE FALSE POSITIVE THIS CHECK NEARLY SHIPPED. Verbatim from Compare.fs.
expect accept "prose that merely contains the word receipt" <<'EOF'
module Planted
// Keeping recovery in the console alone left a re-run receipt
// byte-identical to a first-attempt pass — which is precisely what the
// field exists to prevent.
let x = 1
EOF

# A name in CODE is not a citation. The audit's scanner already distinguishes the two
# for MEASURED claims; this asserts the citation check inherited that rather than
# re-deriving it and getting a different answer.
expect accept "a missing name that appears in a STRING, not a comment" <<'EOF'
module Planted
let name = "receipt: definitely-not-a-real-receipt"
EOF

echo
if [ "$fails" -eq 0 ]; then
  echo "CLAIM-CITATION PROOF: every planted dangling citation is rejected, and every real spelling — backticked, colon, multi-build, glob, file — is accepted"
  echo "OK"
else
  echo "CLAIM-CITATION PROOF: $fails arm(s) failed"
  exit 1
fi
