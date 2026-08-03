#!/usr/bin/env bash
# FG-104b. The proof for `audit-stale-refs.bb` — one planted fixture per binding
# form it claims to support, plus the two ways it can be wrong about its own job.
#
# The operating contract says a checker must be PROVEN TO FAIL. That rule has now
# been under-applied twice on this one script: the first proof covered a deleted
# `let name` and nothing else, so an unresolvable base ref silently reported a
# clean tree (a BLOCKING gate turned green by a typo), and the regex allowed only
# `private`, so `let mutable X` captured "mutable" and `let rec` was missed
# entirely. Both were found in review, both are the checker being right about the
# thing it watched and blind at its own edges.
#
# It then happened a THIRD time, on the form this codebase writes most: members
# carry a receiver, so `member _.name` matched nothing and `member this.name`
# captured "this". The reviewer found it by reproducing it. Every form the audit
# claims is now planted here — that claim is what keeps costing, so it is the
# claim the fixtures below have to earn.
#
# So the proof is per-form, and it runs in a scratch repository rather than this
# one — a proof that mutates the tree it audits is its own confounder.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
AUDIT="$(pwd)/scripts/audit-stale-refs.bb"

FAIL=0
note() { printf '  %-34s %s\n' "$1" "$2"; }

# one fixture: plant a binding plus a comment naming it, delete the binding, and
# require the audit to name the surviving comment
prove_form() {
  local label=$1 binding=$2 id=$3
  local d
  d=$(mktemp -d /tmp/stale-proof.XXXXXX)
  (
    cd "$d" || exit 1
    git init -q . && git config user.email p@p && git config user.name p
    mkdir -p src
    # the identifier is passed in, not derived: a greedy sed here pulled `int`
    # out of `type X = { A: int }` and the fixture then proved nothing
    printf '%s\n// %s is explained here\n' "$binding" "$id" > src/F.fs
    git add -A && git commit -qm base
    # delete the binding, keep the comment — the known-bad state
    sed -i '1d' src/F.fs
  ) >/dev/null 2>&1

  local out
  out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 )
  local rc=$?
  # THE EXPECTED IDENTIFIER, not merely "some report happened". These cases
  # checked only the generic phrase, so a run that reported the wrong thing —
  # the audit briefly collected the word `member` as a deleted identifier —
  # passed for the wrong reason while missing the real one.
  if [ "$rc" -ne 0 ] && grep -q "name an identifier this diff deleted" <<<"$out" \
     && grep -qF -- "$id" <<<"$out"; then
    note "$label" "reported (exit $rc) — OK"
  else
    note "$label" "MISSED — exit $rc"
    printf '%s\n' "$out" | sed 's/^/      /'
    FAIL=1
  fi
}

echo "=== planted fixtures, one per binding form ==="
prove_form "let name"                 "let staleGateValue = 1" staleGateValue
prove_form "let private name"         "let private staleGateValue = 1" staleGateValue
prove_form "let mutable name"         "let mutable staleGateValue = 1" staleGateValue
prove_form "let rec name"             "let rec staleGateValue x = x" staleGateValue
prove_form "let inline name"          "let inline staleGateValue x = x" staleGateValue
prove_form "let private mutable name" "let private mutable staleGateValue = 1" staleGateValue
prove_form "type name"                "type StaleGateValue = { A: int }" StaleGateValue
prove_form "member _.name"            "member _.staleGateValue = 1" staleGateValue
prove_form "member this.name"         "member this.staleGateValue = 1" staleGateValue
prove_form "member val name"          "member val StaleGateValue = 1 with get, set" StaleGateValue
prove_form "static member name"       "static member StaleGateValue = 1" StaleGateValue
prove_form "override this.name"       "override this.StaleGateValue = 1" StaleGateValue
prove_form "abstract member name"     "abstract member StaleGateValue : int" StaleGateValue
prove_form "default this.name"        "default this.StaleGateValue = 1" StaleGateValue
# F# identifiers may end in `'`, which is not a word character — so the `\b` the
# comment search used could never close against one. Extracted fine, reported
# never.
prove_form "let name' (apostrophe)"   "let staleGateValue' = 1" "staleGateValue'"
prove_form "let name'' (two)"         "let staleGateValue'' = 1" "staleGateValue''"
prove_form "and Name (recursive type)" "and StaleGateValue = { A: int }" StaleGateValue
prove_form "and name (recursive fn)"  "and staleGateValue x = x" staleGateValue
prove_form "and private name"         "and private staleGateValue x = x" staleGateValue
prove_form "let! (computation expr)"  "let! staleGateValue = fetch ()" staleGateValue
prove_form "use binding"              "use staleGateValue = open ()" staleGateValue
prove_form "use! binding"             "use! staleGateValue = openAsync ()" staleGateValue

# The surviving comment REPEATS the binding keyword. Every fixture above names
# the identifier bare, so none of them could catch a comment being mistaken for
# a surviving definition — which is how `member OldHook` deleted, plus
# `// member OldHook used to ...` left behind, exited clean. Caught in review,
# not by this proof, which is why it is now in this proof.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    member _.staleGateValue = 1\n// member staleGateValue used to publish the gate\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '2d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "staleGateValue" <<<"$out"; then
  note "comment repeats the keyword" "reported (exit $rc) — OK"
else
  note "comment repeats the keyword" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A record field written on the BRACE line, deleted on its own — not the
# enclosing type. `{ Root: string }` is how this codebase writes single-field
# records, and the extractor required `-` then whitespace then the name.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    { StaleGateValue: string\n      Keep: int }\n// StaleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'type T =\n    { Keep: int }\n// StaleGateValue is the gate hook\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "StaleGateValue" <<<"$out"; then
  note "record field on the brace line" "reported (exit $rc) — OK"
else
  note "record field on the brace line" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# An F# BLOCK comment. Its interior lines carry no marker, so the previous
# line-prefix scan could not see this class even in principle — and the checker
# exited 0 on the exact thing it advertises. Nested, because F# block comments
# nest and a depth counter that cannot count is not a depth counter.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let staleGateValue = 1\n(* the gate hook\n   (* nested aside *)\n   staleGateValue is explained here *)\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "staleGateValue" <<<"$out"; then
  note "nested (* block comment *)" "reported (exit $rc) — OK"
else
  note "nested (* block comment *)" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A surviving STRING that merely looks like a definition. Unanchored,
# `let keep = "let StaleGateValue"` convinced the audit the binding was still
# defined, so the stale comment went unreported and --strict exited 0. The
# whole-line comment filter cannot help here: the line IS code.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let StaleGateValue = 1\nlet keep = "let StaleGateValue"\n// StaleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "StaleGateValue" <<<"$out"; then
  note "string that looks like a def" "reported (exit $rc) — OK"
else
  note "string that looks like a def" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# THE FIRST FALSE-POSITIVE FIXTURE. Every case above asks whether the checker
# stays silent when it should speak; this asks whether it speaks when it should
# stay silent, which is the failure that makes people stop reading a gate.
# Deleting a COMMENT containing binding syntax removes no binding, so tidying
# stale documentation must not fail the build.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let keep = 1\n// let GhostGateValue = old docs only\n// GhostGateValue was once described here\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '2d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "deleting a comment is not a deletion" "silent (exit 0) — OK"
else
  note "deleting a comment is not a deletion" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A record field named on an INTERIOR block-comment line. That line carries no
# marker at all, so a prefix test read it as a surviving definition and the
# deleted field never reached the stale-comment check — the definition scan and
# the comment scan disagreeing at exactly the edge one of them was built for.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    { StaleGateValue: string\n      Keep: int }\n(* the old shape was\n   StaleGateValue: string\n*)\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'type T =\n    { Keep: int }\n(* the old shape was\n   StaleGateValue: string\n*)\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "StaleGateValue" <<<"$out"; then
  note "field named inside a block comment" "reported (exit $rc) — OK"
else
  note "field named inside a block comment" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A field that SURVIVES by moving onto the brace line. The extractor had learned
# that shape and the surviving-definition check had not, so the field read as
# deleted and an honest comment about it failed the build. False positives are
# how a gate earns the reputation that gets it switched off.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    { Keep: int\n      StaleGateValue: string }\n// StaleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'type T =\n    { StaleGateValue: string }\n// StaleGateValue is the gate hook\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "field moved onto the brace line" "silent (exit 0) — OK"
else
  note "field moved onto the brace line" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# UNRELATED KEYWORD PROSE. The words `member`, `let` and `type` occur in
# ordinary English, and for one commit the extractor collected them as deleted
# identifiers — failing the build on a comment about team rotation. A checker
# that blocks pushes over prose is worse than no checker.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    member _.staleGateValue = 1\n// team member rotation notes\n// let the record show, this type of thing\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '2d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if grep -qE "^  (member|let|type)\\b" <<<"$out"; then
  note "keyword prose is not an identifier" "FALSE POSITIVE on a keyword"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
else
  note "keyword prose is not an identifier" "not reported — OK"
fi

# AN INERT `(*` IN A STRING, aimed at the half of the bug that actually bites.
# Counting delimiter tokens rather than scanning characters let `let syntax =
# "(*"` open a block comment that never closed, so every LATER line in the file
# read as comment text. The damage is on the DEFINITION side: a binding that
# merely MOVED within the file is masked as comment text, `still-defined?`
# concludes it is gone, and the surviving comment naming it fails the blocking
# gate. A false positive on code that is entirely correct.
#
# (The first two fixtures written for this finding asserted the comment-side
# behaviour instead, and passed identically with the bug present and absent —
# decoration, not proof. Kept only after watching this one flip.)
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let syntax = "(*"\nlet staleGateValue = 1\nlet keeper = 2\n// staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'let syntax = "(*"\nlet keeper = 2\nlet staleGateValue = 1\n// staleGateValue is the gate hook\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "inert (* does not mask a real def" "silent (exit 0) — OK"
else
  note "inert (* does not mask a real def" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# an ATTRIBUTED record field (Contracts.fs writes these)
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    { [<JsonPropertyName "gate">] StaleGateValue: string\n      Keep: int }\n// StaleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'type T =\n    { Keep: int }\n// StaleGateValue is the gate hook\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "StaleGateValue" <<<"$out"; then
  note "attributed record field" "reported (exit $rc) — OK"
else
  note "attributed record field" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# a MUTABLE record field (Interpreter.fs writes these)
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    { mutable StaleGateValue: int\n      Keep: int }\n// StaleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'type T =\n    { Keep: int }\n// StaleGateValue is the gate hook\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "StaleGateValue" <<<"$out"; then
  note "mutable record field" "reported (exit $rc) — OK"
else
  note "mutable record field" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A NON-F# FILE IS OUT OF SCOPE FOR EXTRACTION, and was not: the field arm
# matched `StageName: old script step` in a shell script and failed the blocking
# gate over a `# StageName is documented here` comment. The scope sentence
# claimed otherwise, which made it a wish rather than a rule.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src scripts \
  && printf 'let keeper = 1\n' > src/F.fs \
  && printf 'StageName: old script step\n# StageName is documented here\n' > scripts/lane.sh \
  && git add -A && git commit -qm base && sed -i '1d' scripts/lane.sh ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "non-F# deletion (out of scope)" "not extracted (exit 0) — OK"
else
  note "non-F# deletion (out of scope)" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A MULTI-LINE STRING whose CONTINUATION line carries `(*`. This is
# src/Fogell.Store/Store.fs:430-443 exactly: an ordinary F# string opened on one
# line, with `count(*)` — SQL, not a comment — on the lines below it. The token
# must be on a CONTINUATION line to exercise anything: the first version of this
# fixture put it beside the opening quote, where the scanner is already inside a
# string within that same line, and so it passed with cross-line carrying on OR
# off. Decoration, caught by running it against a copy with carrying disabled.
#
# With the state not carried, that `(*` opens a block comment, every later
# declaration reads as comment text, and a binding that merely MOVED below it
# fails the blocking gate.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let sql =\n    "SELECT n\n     FROM t WHERE count(*) > 0"\nlet staleGateValue = 1\nlet keeper = 2\n// staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'let sql =\n    "SELECT n\n     FROM t WHERE count(*) > 0"\nlet keeper = 2\nlet staleGateValue = 1\n// staleGateValue is the gate hook\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "multi-line string with (*" "silent (exit 0) — OK"
else
  note "multi-line string with (*" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

echo "=== the checker's own failure modes ==="
out=$(bb "$AUDIT" definitely-not-a-ref --strict 2>&1); rc=$?
if [ "$rc" -eq 2 ] && grep -q "does not resolve" <<<"$out"; then
  note "unresolvable base" "refused (exit 2) — OK"
else
  note "unresolvable base" "DID NOT REFUSE — exit $rc"; FAIL=1
fi

# HERMETIC, and it was not: this ran the audit against THIS repository with the
# default base, so it inherited whatever remotes the host happened to have. It
# passed on a developer box with `origin/main` and failed in CI's shallow
# single-ref checkout, where that ref does not exist — the audit refusing
# correctly, the proof reading it as a false positive. A proof that depends on
# the ambient environment is testing the environment.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src && printf 'let staleGateValue = 1\n// staleGateValue is explained here\n' > src/F.fs \
  && git add -A && git commit -qm base ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ] && grep -q "no surviving comment" <<<"$out"; then
  note "clean tree" "silent (exit 0) — OK"
else
  note "clean tree" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A search tool that ERRORS must not read as "found nothing". Forced with a stub
# `rg` on PATH that exits 2 — the same shape as an unreadable tree or a missing
# binary, and previously indistinguishable from a clean result.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src bin && printf 'let staleGateValue = 1\n// staleGateValue is explained here\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs \
  && printf '#!/bin/sh\necho "stub rg failure" >&2\nexit 2\n' > bin/rg && chmod +x bin/rg ) >/dev/null 2>&1
out=$( cd "$d" && PATH="$d/bin:$PATH" bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 2 ] && grep -q "rg failed while" <<<"$out"; then
  note "search tool errors" "refused (exit 2) — OK"
else
  note "search tool errors" "DID NOT REFUSE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# THE LENGTH FLOOR, pinned as a deliberate boundary rather than left implicit.
# Identifiers under four characters are not tracked: `x`, `i`, `id`, `ctx` occur
# inside ordinary English, and a checker that fires on prose is one nobody
# leaves switched on. If someone widens the extractor later, this case fails and
# they have to decide rather than discover.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src && printf 'let foo = 1\n// foo is still documented\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "short identifier (by design)" "not tracked (exit 0) — OK"
else
  note "short identifier (by design)" "UNEXPECTEDLY TRACKED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# LOWERCASE RECORD LABELS are out of scope, pinned like the length floor. Every
# label in this codebase is PascalCase (measured: zero lowercase across src/ and
# tools/), and widening the field arm would collect `mutable cache:` and friends
# as field names — false positives to cover a form nothing here writes.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'type T =\n    { staleGateValue: string\n      Keep: int }\n// staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'type T =\n    { Keep: int }\n// staleGateValue is the gate hook\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && bb "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "lowercase field (by design)" "not tracked (exit 0) — OK"
else
  note "lowercase field (by design)" "UNEXPECTEDLY TRACKED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

[ "$FAIL" -eq 0 ] && echo "STALE-REF PROOF: every supported form fails when it should, and both scope boundaries hold" \
                  || { echo "STALE-REF PROOF FAILED"; exit 1; }
