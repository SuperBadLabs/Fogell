#!/usr/bin/env bash
# FG-104b. The proof for `audit-stale-refs` — one planted fixture per binding
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
AUDIT="$(pwd)/scripts/bin/audit-stale-refs"
# THE MUTATIONS COMPILE. A native binary cannot be edited with sed, so proving
# this checker fails on a known-bad variant means BUILDING that variant — which
# also makes the thing under test provably the source the mutation touched,
# rather than a binary someone hopes matches it. The prelude travels with it
# because the script `#load`s it by relative path.
AUDIT_SRC="$(pwd)/scripts/fsx/audit-stale-refs.fsx"
PRELUDE="$(pwd)/scripts/fsx/prelude.fsx"

# `--check`, not merely `-x`. Run standalone this would otherwise compare a
# possibly STALE base binary against freshly compiled mutants, and every verdict
# would be about two different versions of the checker. Named: this proof runs
# ONE tool, and the hosted stale-refs lane builds only that one, so asking
# after all nine would fail the lane on eight binaries it never touches.
if ! audit_check=$(./scripts/build-audits.sh --check audit-stale-refs 2>&1); then
  echo "STALE-REF PROOF FAILED: audit binaries missing or stale — run scripts/build-audits.sh" >&2
  printf '%s\n' "$audit_check" | tail -20 >&2
  exit 1
fi

FAIL=0
note() { printf '  %-34s %s\n' "$1" "$2"; }

expect_clean() {
  local label=$1 repo=$2 removed_count=$3 forbidden_ids=$4
  local out rc id forbidden_ok=1
  out=$( cd "$repo" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
  for id in $forbidden_ids; do
    grep -qE "^  $id[[:space:]]" <<<"$out" && forbidden_ok=0
  done
  if [ "$rc" -eq 0 ] \
     && grep -qF "stale-reference audit: $removed_count identifier(s) removed vs HEAD, $removed_count fully gone" <<<"$out" \
     && grep -qF "no surviving comment names a deleted identifier" <<<"$out" \
     && [ "$forbidden_ok" -eq 1 ]; then
    note "$label" "silent with exact diagnostic (exit 0) — OK"
  else
    note "$label" "FALSE POSITIVE/VACUOUS — exit $rc"
    printf '%s\n' "$out" | sed 's/^/      /'
    FAIL=1
  fi
}

expect_reported() {
  local label=$1 repo=$2 removed_count=$3 expected_ids=$4 forbidden_ids=$5
  local out rc id expected_ok=1 forbidden_ok=1
  out=$( cd "$repo" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
  for id in $expected_ids; do
    grep -qE "^  $id[[:space:]]" <<<"$out" || expected_ok=0
  done
  for id in $forbidden_ids; do
    grep -qE "^  $id[[:space:]]" <<<"$out" && forbidden_ok=0
  done
  if [ "$rc" -eq 1 ] \
     && grep -qF "stale-reference audit: $removed_count identifier(s) removed vs HEAD, $removed_count fully gone" <<<"$out" \
     && grep -qF "comment(s) name an identifier this diff deleted" <<<"$out" \
     && [ "$expected_ok" -eq 1 ] \
     && [ "$forbidden_ok" -eq 1 ]; then
    note "$label" "reported exact binder (exit $rc) — OK"
  else
    note "$label" "MISSED/WRONG DIAGNOSTIC — exit $rc"
    printf '%s\n' "$out" | sed 's/^/      /'
    FAIL=1
  fi
}

replace_line_once() {
  local file=$1 old=$2 new=$3 count tmp line
  count=$(grep -Fxc -- "$old" "$file")
  [ "$count" -eq 1 ] || return 1
  tmp=$(mktemp /tmp/stale-mutant-line.XXXXXX)
  while IFS= read -r line || [ -n "$line" ]; do
    if [ "$line" = "$old" ]; then
      printf '%s\n' "$new"
    else
      printf '%s\n' "$line"
    fi
  done < "$file" > "$tmp"
  mv "$tmp" "$file"
}

# ---------------------------------------------------------------------------
# MUTANT BUILDS ARE BATCHED AND PARALLEL. Ten mutants at ~7s each cost 70s of
# wall time run one at a time, on a host with 32 cores idle. They are
# independent by construction — each is a separate source tree in its own
# directory — so the only ordering that matters is that every one is built
# before the first arm judges its result. Failures are recorded per mutant and
# reported by the arm that owns them, so a build error still fails the proof
# rather than silently reading as a killed mutation.
MUTANT_DIRS=()
mutant_prepare() {
  local key=$1 old=$2 new=$3
  local d="$MUTANT_ROOT/$key"
  mkdir -p "$d"
  cp "$AUDIT_SRC" "$d/audit-stale-refs.fsx"
  cp "$PRELUDE" "$d/prelude.fsx"
  if ! replace_line_once "$d/audit-stale-refs.fsx" "$old" "$new"; then
    echo "not-unique" > "$d/status"
    return
  fi
  sha256sum "$d/audit-stale-refs.fsx" | cut -d' ' -f1 > "$d/after"
  MUTANT_DIRS+=("$d")
}
mutant_build_all() {
  local jobs_max d
  # One compile per core: nproc/3 was 1 on the 4-core runner, and the ten
  # mutants took 180 s in series there (run 33595804856). Measured in
  # scripts/build-audits.sh (2026-09-02, 4-core mask): 69 s serial vs 33 s at
  # one job per core for nine compiles.
  jobs_max=$(nproc 2>/dev/null || echo 4)
  [ "$jobs_max" -lt 1 ] && jobs_max=1
  for d in "${MUTANT_DIRS[@]}"; do
    while [ "$(jobs -rp | wc -l)" -ge "$jobs_max" ]; do wait -n; done
    {
      if fflat "$d/audit-stale-refs.fsx" -o "$d/audit-stale-refs" >"$d/compile.log" 2>&1; then
        echo built > "$d/status"
      else
        echo compile-failed > "$d/status"
      fi
    } &
  done
  wait
}
BASE_SHA=$(sha256sum "$AUDIT_SRC" | cut -d' ' -f1)

prove_false_positive_mutation() {
  local label=$1 repo=$2 expected_id=$3 key=$4
  local original original_rc d before after out rc
  original=$( cd "$repo" && "$AUDIT" HEAD --strict 2>&1 ); original_rc=$?
  d="$MUTANT_ROOT/$key"
  before="$BASE_SHA"
  case "$(cat "$d/status" 2>/dev/null)" in
    built) after=$(cat "$d/after") ;;
    not-unique)
      note "$label mutation" "MUTATION TARGET NOT UNIQUE"
      FAIL=1
      return ;;
    *)
      # A mutant that does not COMPILE has not been tested. Without this the arm
      # would report a killed mutation on the strength of a build error, which is
      # the vacuous-pass shape this whole proof exists to refuse.
      note "$label mutation" "MUTANT DID NOT COMPILE"
      tail -5 "$d/compile.log" 2>/dev/null | sed 's/^/      /'
      FAIL=1
      return ;;
  esac
  out=$( cd "$repo" && "$d/audit-stale-refs" HEAD --strict 2>&1 ); rc=$?
  if [ "$original_rc" -eq 0 ] \
     && grep -qF "no surviving comment names a deleted identifier" <<<"$original" \
     && [ "$before" != "$after" ] \
     && [ "$rc" -eq 1 ] \
     && grep -qE "^  $expected_id[[:space:]]" <<<"$out"; then
    note "$label mutation" "changed bytes; false positive exposed — KILLED"
  else
    note "$label mutation" "SURVIVED/VACUOUS — original $original_rc mutant $rc"
    printf '%s\n' "$out" | sed 's/^/      /'
    FAIL=1
  fi
}

prove_coverage_mutation() {
  local label=$1 repo=$2 expected_id=$3 key=$4
  local original original_rc d before after out rc
  original=$( cd "$repo" && "$AUDIT" HEAD --strict 2>&1 ); original_rc=$?
  d="$MUTANT_ROOT/$key"
  before="$BASE_SHA"
  case "$(cat "$d/status" 2>/dev/null)" in
    built) after=$(cat "$d/after") ;;
    not-unique)
      note "$label mutation" "MUTATION TARGET NOT UNIQUE"
      FAIL=1
      return ;;
    *)
      # A mutant that does not COMPILE has not been tested. Without this the arm
      # would report a killed mutation on the strength of a build error, which is
      # the vacuous-pass shape this whole proof exists to refuse.
      note "$label mutation" "MUTANT DID NOT COMPILE"
      tail -5 "$d/compile.log" 2>/dev/null | sed 's/^/      /'
      FAIL=1
      return ;;
  esac
  out=$( cd "$repo" && "$d/audit-stale-refs" HEAD --strict 2>&1 ); rc=$?
  if [ "$original_rc" -eq 1 ] \
     && grep -qE "^  $expected_id[[:space:]]" <<<"$original" \
     && [ "$before" != "$after" ] \
     && [ "$rc" -eq 0 ] \
     && ! grep -qE "^  $expected_id[[:space:]]" <<<"$out" \
     && grep -qF "no surviving comment names a deleted identifier" <<<"$out"; then
    note "$label mutation" "changed bytes; missing binder exposed — KILLED"
  else
    note "$label mutation" "SURVIVED/VACUOUS — original $original_rc mutant $rc"
    printf '%s\n' "$out" | sed 's/^/      /'
    FAIL=1
  fi
}

prove_coverage_pair_mutation() {
  local label=$1 repo=$2 expected_one=$3 expected_two=$4 key=$5
  local original original_rc d before after out rc
  original=$( cd "$repo" && "$AUDIT" HEAD --strict 2>&1 ); original_rc=$?
  d="$MUTANT_ROOT/$key"
  before="$BASE_SHA"
  case "$(cat "$d/status" 2>/dev/null)" in
    built) after=$(cat "$d/after") ;;
    not-unique)
      note "$label mutation" "MUTATION TARGET NOT UNIQUE"
      FAIL=1
      return ;;
    *)
      # A mutant that does not COMPILE has not been tested. Without this the arm
      # would report a killed mutation on the strength of a build error, which is
      # the vacuous-pass shape this whole proof exists to refuse.
      note "$label mutation" "MUTANT DID NOT COMPILE"
      tail -5 "$d/compile.log" 2>/dev/null | sed 's/^/      /'
      FAIL=1
      return ;;
  esac
  out=$( cd "$repo" && "$d/audit-stale-refs" HEAD --strict 2>&1 ); rc=$?
  if [ "$original_rc" -eq 1 ] \
     && grep -qE "^  $expected_one[[:space:]]" <<<"$original" \
     && grep -qE "^  $expected_two[[:space:]]" <<<"$original" \
     && [ "$before" != "$after" ] \
     && [ "$rc" -eq 0 ] \
     && ! grep -qE "^  ($expected_one|$expected_two)[[:space:]]" <<<"$out" \
     && grep -qF "no surviving comment names a deleted identifier" <<<"$out"; then
    note "$label mutation" "changed bytes; both missing binders exposed — KILLED"
  else
    note "$label mutation" "SURVIVED/VACUOUS — original $original_rc mutant $rc"
    printf '%s\n' "$out" | sed 's/^/      /'
    FAIL=1
  fi
}

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
  out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 )
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "multi-line string with (*" "silent (exit 0) — OK"
else
  note "multi-line string with (*" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A CHARACTER LITERAL HOLDING A DOUBLE QUOTE — `'"'`, live at
# src/Fogell.Admission/Limits.fs:80 and src/Fogell.Pipeline.Parser/Lexeme.fs:175.
# Once lexical mode is carried across lines, mistaking that quote for a string
# delimiter leaves the scanner in :str for the rest of the file, so every later
# comment drops out of the index and a deleted binding named by one passes
# silently. The fix for carrying state is what created this class.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let isQuote c = (c = %s)\nlet staleGateValue = 1\n// staleGateValue is the gate hook\n' "'\"'" > src/F.fs \
  && git add -A && git commit -qm base && sed -i '2d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -q "staleGateValue" <<<"$out"; then
  note "char literal holding a quote" "reported (exit $rc) — OK"
else
  note "char literal holding a quote" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A DESTRUCTURING LET. `let (verdict, folds), j, f =` is live at
# src/Fogell.Differential/Compare.fs:194, and the name-after-keyword arms saw
# nothing in it.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (staleGateValue, foldedGate), staleOuterGate = (1, 2), 3\nlet keeper = 1\n// staleGateValue and foldedGate and staleOuterGate are the gate hooks\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
# BOTH names, because a regression that extracted only the FIRST tuple binder
# would have passed a single-name assertion while missing the `folds` case the
# audit's own comment cites from Compare.fs:194.
#
# ANCHORED ON THE IDENTIFIER COLUMN. The report prints `  <id>  <comment line>`,
# and the planted comment names both identifiers — so an unanchored grep matched
# the ECHOED COMMENT TEXT and passed with the second binder never extracted at
# all. Caught by running it against a copy that keeps only the first binder.
# THREE names, and the trailing one is OUTSIDE the first parenthesised group and
# four characters long. It was `j` — one character, below the floor — so the
# whole-pattern miss was invisible to this fixture by construction.
if [ "$rc" -ne 0 ] \
   && grep -qE "^  staleGateValue[[:space:]]" <<<"$out" \
   && grep -qE "^  foldedGate[[:space:]]" <<<"$out" \
   && grep -qE "^  staleOuterGate[[:space:]]" <<<"$out"; then
  note "destructuring let (all three)" "reported (exit $rc) — OK"
else
  note "destructuring let (all three)" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

echo "=== the checker's own failure modes ==="
out=$("$AUDIT" definitely-not-a-ref --strict 2>&1); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && PATH="$d/bin:$PATH" "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
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
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "lowercase field (by design)" "not tracked (exit 0) — OK"
else
  note "lowercase field (by design)" "UNEXPECTEDLY TRACKED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A TYPED destructuring pattern is out of scope, pinned. Collecting from
# `let (a: string, b) = ...` would take `string` as a deleted identifier, fail to
# find it as a definition, and then match every comment in the tree that says
# "string" — a false positive that blocks pushes, which this audit has been
# taught four separate times to fear more than a miss.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (staleGateValue: string, other: string) = ("a", "b")\nlet keeper = 1\n// staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "typed destructuring (by design)" "not tracked (exit 0) — OK"
else
  note "typed destructuring (by design)" "UNEXPECTEDLY TRACKED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A CONSTRUCTOR inside a pattern is not a binder. In F# an uppercase identifier
# in a pattern position is a union case or literal — `let (Some staleGateValue,
# other) = ...` binds staleGateValue and MATCHES on Some. Collecting `Some`
# reported it as a deleted identifier against every comment in the tree that
# mentions it.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (Some staleGateValue, other) = (Some 1, 2)\nlet keeper = 1\n// Some wraps staleGateValue in the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
# BOTH halves. Asserting only that `Some` is absent would pass if the audit
# stopped extracting the binder beside it too — silence for the wrong reason.
# The binder must be REPORTED and the constructor must NOT be.
if [ "$rc" -ne 0 ] \
   && grep -qE "^  staleGateValue[[:space:]]" <<<"$out" \
   && ! grep -qE "^  Some[[:space:]]" <<<"$out"; then
  note "constructor skipped, binder kept" "reported (exit $rc) — OK"
else
  note "constructor skipped, binder kept" "WRONG — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# NESTED patterns are uncovered, and must be uncovered CONSISTENTLY. The tuple
# regex truncates at the first inner paren, so `((a), b)` used to yield `(a` and
# then report `a` — coverage by accident, which makes the stated boundary false.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let ((staleGateValue), other) = ((1), 2)\nlet keeper = 1\n// staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "nested pattern (by design)" "not tracked (exit 0) — OK"
else
  note "nested pattern (by design)" "INCONSISTENTLY TRACKED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A DESTRUCTURED BINDING THAT SURVIVES. Editing the values, or moving the line,
# keeps the binders alive — but the survivor check knew only the
# name-after-keyword and record-field grammars, so it read them as deleted and
# failed the gate over an honest comment. The same asymmetry as the brace-line
# field arm, and the fourth on this branch: extraction learns a form, the
# survivor check does not.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (staleGateValue, foldedGate) = (1, 2)\n// staleGateValue and foldedGate are the gate hooks\n' > src/F.fs \
  && git add -A && git commit -qm base \
  && printf 'let (staleGateValue, foldedGate) = (3, 4)\n// staleGateValue and foldedGate are the gate hooks\n' > src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "destructured binding survives" "silent (exit 0) — OK"
else
  note "destructured binding survives" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A DESTRUCTURED IDENTIFIER ENDING IN `'`. The literal guard below must not
# treat an identifier apostrophe as a literal marker — doing so dropped the whole
# pattern and contradicted the coverage claimed for untyped tuples, while the
# same name shape is proven elsewhere in this file.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf "let (staleGateValue', foldedGate) = (1, 2)\nlet keeper = 1\n// staleGateValue' is the gate hook\n" > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -ne 0 ] && grep -qE "^  staleGateValue'[[:space:]]" <<<"$out"; then
  note "destructured name ending in '" "reported (exit $rc) — OK"
else
  note "destructured name ending in '" "MISSED — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

# A STRING LITERAL inside a pattern is data, not a binder. F# patterns can match
# on literals, and a token scan cannot tell `let ("staleGateValue", other) =`
# from a binding — it reported the literal's text as a deleted identifier and
# failed the gate over a comment that legitimately mentions it.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let ("staleGateValue", other) = ("x", 2)\nlet keeper = 1\n// staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
out=$( cd "$d" && "$AUDIT" HEAD --strict 2>&1 ); rc=$?
if [ "$rc" -eq 0 ]; then
  note "literal in a pattern (by design)" "not tracked (exit 0) — OK"
else
  note "literal in a pattern (by design)" "FALSE POSITIVE — exit $rc"; printf '%s\n' "$out" | sed 's/^/      /'; FAIL=1
fi

echo "=== FG-117 false-positive tranche ==="

# (b) BOOLEAN AND NULL LITERALS are values matched by a pattern, not names
# introduced by it. First the pure false-positive shape: there is deliberately
# no eligible binder, and the exact zero-count diagnostic prevents a broad
# "ignore the whole diff" change from passing silently.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (true, x) = true, 1\nlet (false, y) = false, 2\nlet (null, z) = null, 3\nlet keeper = 1\n// true false and null are literal patterns\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1,3d' src/F.fs ) >/dev/null 2>&1
expect_clean "literal patterns only" "$d" 0 'true false null'
fg117_b_fixture=$d

# The neighboring real binders must still be extracted. Each literal sits in
# the same deleted tuple as one supported binder, so filtering the whole pattern
# instead of only the literal fails this arm.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (true, staleGateValue) = true, 1\nlet (false, foldedGate) = false, 2\nlet (null, otherGate) = null, 3\nlet keeper = 1\n// true guards staleGateValue; false guards foldedGate; null guards otherGate\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1,3d' src/F.fs ) >/dev/null 2>&1
expect_reported "literal neighbor binder" "$d" 3 'staleGateValue foldedGate otherGate' 'true false null'

# (c) ACTIVE-PATTERN AND OPERATOR HEADERS are definitions. The identifiers
# after the closing parenthesis are parameters, not top-level destructured
# binders.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (|LongCase|_|) inputValue = Some inputValue\nlet (+.) leftValue rightValue = leftValue + rightValue\nlet keeper = 1\n// inputValue and leftValue are definition parameters\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1,2d' src/F.fs ) >/dev/null 2>&1
expect_clean "definition paren forms" "$d" 0 'inputValue leftValue rightValue'
fg117_c_fixture=$d

# A conventional tuple deleted beside those two definitions remains covered.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (|LongCase|_|) inputValue = Some inputValue\nlet (+.) leftValue rightValue = leftValue + rightValue\nlet (staleGateValue, foldedGate) = 1, 2\nlet keeper = 1\n// inputValue leftValue staleGateValue and foldedGate are discussed\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1,3d' src/F.fs ) >/dev/null 2>&1
expect_reported "definition neighbor tuple" "$d" 2 'staleGateValue foldedGate' 'inputValue leftValue rightValue'

# A leading operator CHARACTER does not make a tuple an operator definition.
# The pure control carries no eligible binder; the paired tuple's lowercase
# neighbor must still be extracted. Requiring a real parameter after an exact
# operator/active-pattern head is what separates both from the definitions
# above.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (-1, x) = -1, 2\nlet keeper = 1\n// -1 is a numeric literal pattern, not a definition header\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "numeric tuple literal only" "$d" 0 'staleGateValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (-1, staleGateValue) = -1, 2\nlet keeper = 1\n// staleGateValue is the numeric tuple neighbor\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "numeric tuple neighbor binder" "$d" 1 'staleGateValue' ''
fg117_c_numeric_fixture=$d

# (e) A LEADING UNDERSCORE belongs to the identifier. A comment containing
# only the old, invented suffix must not fire, while the summary proves the
# actual underscore-prefixed binder was still collected.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (_staleGateValue, x) = 1, 2\nlet keeper = 1\n// staleGateValue is not the name that was bound\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "underscore suffix not invented" "$d" 1 'staleGateValue'
fg117_e_fixture=$d

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (_staleGateValue, x) = 1, 2\nlet keeper = 1\n// _staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "underscore binder retained" "$d" 1 '_staleGateValue' ''

# One-or-more leading underscores are the binder, not a prefix to preserve once
# and not a reason to suppress the token. The suffix-only control must stay
# silent while the exact double-underscore name remains reportable.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (__staleGateValue, x) = 1, 2\nlet keeper = 1\n// staleGateValue is not the name that was bound\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "multi-underscore suffix not invented" "$d" 1 'staleGateValue _staleGateValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (__staleGateValue, x) = 1, 2\nlet keeper = 1\n// __staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "multi-underscore binder retained" "$d" 1 '__staleGateValue' '_staleGateValue staleGateValue'
fg117_e_multi_fixture=$d

# Once an identifier begins with an underscore, an uppercase or digit next
# character is part of a binder rather than an unprefixed constructor. Keep the
# full prefixed spelling in both domains: suffix-only prose stays silent, while
# the exact binder remains reportable.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (_StaleGateValue, x) = 1, 2\nlet keeper = 1\n// StaleGateValue is not the name that was bound\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "uppercase underscore suffix not invented" "$d" 1 'StaleGateValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (_StaleGateValue, x) = 1, 2\nlet keeper = 1\n// _StaleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "uppercase underscore binder retained" "$d" 1 '_StaleGateValue' 'StaleGateValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (_123GateValue, x) = 1, 2\nlet keeper = 1\n// 123GateValue is not the name that was bound\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "digit underscore suffix not invented" "$d" 1 '123GateValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (_123GateValue, x) = 1, 2\nlet keeper = 1\n// _123GateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "digit underscore binder retained" "$d" 1 '_123GateValue' '123GateValue'

# One fixture makes the domain broadening jointly load-bearing: reverting the
# underscore arm to lowercase-only must lose both supported binders.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (_StaleGateValue, x) = 1, 2\nlet (_123GateValue, y) = 3, 4\nlet keeper = 1\n// _StaleGateValue and _123GateValue are gate hooks\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1,2d' src/F.fs ) >/dev/null 2>&1
expect_reported "underscore uppercase and digit binders" "$d" 2 '_StaleGateValue _123GateValue' 'StaleGateValue 123GateValue'
fg117_e_domain_fixture=$d

# The length floor applies to the COMPLETE captured identifier. These three are
# exactly four characters, including their leading underscore run. First prove
# that suffix-only prose does not invent a shorter name while the exact removed
# count proves all three full tokens were collected.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (__Ab, _Abc, _123, x) = 1, 2, 3, 4\nlet keeper = 1\n// Ab Abc and 123 are suffixes, not the names that were bound\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "underscore boundary suffixes" "$d" 3 'Ab Abc 123'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (__Ab, _Abc, _123, x) = 1, 2, 3, 4\nlet keeper = 1\n// __Ab _Abc and _123 are exact four-character binders\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "underscore boundary binders" "$d" 3 '__Ab _Abc _123' 'Ab Abc 123'

# Keep a single-hit __Ab fixture for the suffix-floor mutation below. The other
# two supported binders remain unmentioned, so restoring the old suffix-based
# floor removes the only hit and must make the mutant incorrectly exit clean.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (__Ab, _Abc, _123, x) = 1, 2, 3, 4\nlet keeper = 1\n// __Ab is the load-bearing four-character binder\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "double-underscore length floor" "$d" 3 '__Ab' 'Ab Abc 123'
fg117_e_total_floor_fixture=$d

# Three total characters stay below the deliberate floor even when two of them
# are underscores. Exact zero is what makes removal of the full-token filter a
# non-vacuous false-positive mutation.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (__A, _Ab, x) = 1, 2, 3\nlet keeper = 1\n// __A and _Ab are deliberately below the four-character floor\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "underscore below length floor" "$d" 0 '__A _Ab'
fg117_e_short_floor_fixture=$d

# (f) REMOVED-LINE COMMENT STATE comes from the full base blob, not from a
# zero-context hunk. The deleted binding-shaped text is several lines after the
# opener, so resetting state at the hunk reproduces the false positive.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf '(*\nblock comment preface\nanother comment line\nlet staleDirectValue = 1\nStaleFieldValue: int\nlet (staleGateValue, foldedGate) = 1, 2\ncomment tail\n*)\nlet keeper = 1\n// staleDirectValue StaleFieldValue staleGateValue and foldedGate are documentation examples\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '4,6d' src/F.fs ) >/dev/null 2>&1
expect_clean "removed block-comment text" "$d" 0 'staleDirectValue StaleFieldValue staleGateValue foldedGate'
fg117_f_fixture=$d

# In the paired arm a real tuple is deleted in a separate hunk. The fake tuple
# inside the old block comment must be ignored while the real binders report.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf '(*\nblock comment preface\nanother comment line\nlet staleDirectValue = 1\nStaleFieldValue: int\nlet (staleGateValue, foldedGate) = 1, 2\ncomment tail\n*)\nlet keeperOne = 1\nlet keeperTwo = 2\nlet keeperThree = 3\nlet (actualGateValue, actualFoldedGate) = 4, 5\n// staleDirectValue StaleFieldValue staleGateValue foldedGate actualGateValue and actualFoldedGate are discussed\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '4,6d;12d' src/F.fs ) >/dev/null 2>&1
expect_reported "block-comment neighbor code" "$d" 2 'actualGateValue actualFoldedGate' 'staleDirectValue StaleFieldValue staleGateValue foldedGate'

# The same full-base scanner must exclude binding-shaped continuation lines in
# each F# string mode. Each clean control is paired with a real deleted binder
# after the string, which must remain visible.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let payload =\n    "begin\nlet staleOrdinaryValue = 1\nend"\nlet keeper = 1\n// staleOrdinaryValue is string data\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '3d' src/F.fs ) >/dev/null 2>&1
expect_clean "ordinary string continuation" "$d" 0 'staleOrdinaryValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let payload =\n    "begin\nlet staleOrdinaryValue = 1\nend"\nlet actualOrdinaryGate = 2\n// staleOrdinaryValue is data; actualOrdinaryGate is a binder\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '3d;5d' src/F.fs ) >/dev/null 2>&1
expect_reported "ordinary string neighbor" "$d" 1 'actualOrdinaryGate' 'staleOrdinaryValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let payload =\n    @"begin\nlet staleVerbatimValue = 1\nend"\nlet keeper = 1\n// staleVerbatimValue is string data\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '3d' src/F.fs ) >/dev/null 2>&1
expect_clean "verbatim string continuation" "$d" 0 'staleVerbatimValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let payload =\n    @"begin\nlet staleVerbatimValue = 1\nend"\nlet actualVerbatimGate = 2\n// staleVerbatimValue is data; actualVerbatimGate is a binder\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '3d;5d' src/F.fs ) >/dev/null 2>&1
expect_reported "verbatim string neighbor" "$d" 1 'actualVerbatimGate' 'staleVerbatimValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let payload =\n    """begin\nlet staleTripleValue = 1\nend"""\nlet keeper = 1\n// staleTripleValue is string data\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '3d' src/F.fs ) >/dev/null 2>&1
expect_clean "triple string continuation" "$d" 0 'staleTripleValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let payload =\n    """begin\nlet staleTripleValue = 1\nend"""\nlet actualTripleGate = 2\n// staleTripleValue is data; actualTripleGate is a binder\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '3d;5d' src/F.fs ) >/dev/null 2>&1
expect_reported "triple string neighbor" "$d" 1 'actualTripleGate' 'staleTripleValue'

# Nested F# block comments carry depth across the whole base blob too.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf '(*\nouter docs\n(* nested docs *)\nlet staleNestedValue = 1\n*)\nlet keeper = 1\n// staleNestedValue is documentation\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '4d' src/F.fs ) >/dev/null 2>&1
expect_clean "nested removed comment" "$d" 0 'staleNestedValue'

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf '(*\nouter docs\n(* nested docs *)\nlet staleNestedValue = 1\n*)\nlet actualNestedGate = 2\n// staleNestedValue is docs; actualNestedGate is a binder\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '4d;6d' src/F.fs ) >/dev/null 2>&1
expect_reported "nested comment neighbor" "$d" 1 'actualNestedGate' 'staleNestedValue'

# A character literal holding a quote must not enter string mode and hide the
# real binding on the following line.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let quote = %s\nlet actualCharGate = 2\n// actualCharGate follows a quote character literal\n' "'\"'" > src/F.fs \
  && git add -A && git commit -qm base && sed -i '2d' src/F.fs ) >/dev/null 2>&1
expect_reported "char literal adjacency" "$d" 1 'actualCharGate' ''

# A line that starts in a comment but closes into code is projected from its
# first real token, rather than discarded with the comment prefix.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf '(* old docs *) let afterCommentGate = 1\n// afterCommentGate is the live binder after the close\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "code after comment close" "$d" 1 'afterCommentGate' ''

# (g) A LOWERCASE match must begin at an identifier boundary. Entering Choice
# at its second character invents hoice.
d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (Choice x, y) = Choice 1, 2\nlet keeper = 1\n// hoice is merely a fragment of the constructor name\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_clean "constructor fragment" "$d" 0 'hoice'
fg117_g_fixture=$d

d=$(mktemp -d /tmp/stale-proof.XXXXXX)
( cd "$d" && git init -q . && git config user.email p@p && git config user.name p \
  && mkdir -p src \
  && printf 'let (Choice staleGateValue, x) = Choice 1, 2\nlet keeper = 1\n// hoice is not a binder, but staleGateValue is the gate hook\n' > src/F.fs \
  && git add -A && git commit -qm base && sed -i '1d' src/F.fs ) >/dev/null 2>&1
expect_reported "constructor neighbor binder" "$d" 1 'staleGateValue' 'hoice'

echo "=== FG-117 direct mutation kills ==="
MUTANT_ROOT=$(mktemp -d /tmp/stale-mutants.XXXXXX)
trap 'rm -rf "$MUTANT_ROOT"' EXIT

mutant_prepare literal-allowlist \
  'let patternLiterals = set [ "true"; "false"; "null" ]' \
  'let patternLiterals = Set.empty<string>'

mutant_prepare definition-form \
  '        |> List.filter (fun t -> not (definitionParenForm t))' \
  '        |> List.filter (fun t -> true)'

mutant_prepare numeric-tuple \
  '    javaRx "^(?:\\(\\s*[!%&*+\\-./<=>?@^|~:]+\\s*\\)|\\(\\s*\\|(?:[A-Z][A-Za-z0-9_'"'"']*\\|)+(?:_\\|)?\\s*\\))\\s+[^,=\\s]"' \
  '    javaRx "^\\(\\s*[!%&*+\\-./<=>?@^|~:]"'

mutant_prepare underscore-preservation \
  '        |> List.collect (fun t -> [ for m in destructuredToken.Matches t -> m.Groups.[1].Value ])' \
  '        |> List.collect (fun t -> [ for m in destructuredToken.Matches t -> (let v = m.Groups.[1].Value in (if v.StartsWith "_" then v.Substring 1 else v)) ])'

mutant_prepare multi-underscore \
  'let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'"'"'])(_+[A-Za-z0-9][A-Za-z0-9_'"'"']*|[a-z][A-Za-z0-9_'"'"']{3,})"' \
  'let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'"'"'])(_[A-Za-z0-9][A-Za-z0-9_'"'"']*|[a-z][A-Za-z0-9_'"'"']{3,})"'

mutant_prepare underscore-domain \
  'let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'"'"'])(_+[A-Za-z0-9][A-Za-z0-9_'"'"']*|[a-z][A-Za-z0-9_'"'"']{3,})"' \
  'let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'"'"'])(_+[a-z][A-Za-z0-9_'"'"']*|[a-z][A-Za-z0-9_'"'"']{3,})"'

mutant_prepare length-floor \
  '        |> List.filter (fun s -> s.Length >= 4)' \
  '        |> List.filter (fun s -> true)'

mutant_prepare suffix-floor \
  'let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'"'"'])(_+[A-Za-z0-9][A-Za-z0-9_'"'"']*|[a-z][A-Za-z0-9_'"'"']{3,})"' \
  'let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'"'"'])(_+[A-Za-z0-9][A-Za-z0-9_'"'"']{2,}|[a-z][A-Za-z0-9_'"'"']{3,})"'

mutant_prepare base-projection \
  '            let projected = match proj.TryGetValue n with | true, code -> Some code | _ -> None' \
  '            let projected = match proj.TryGetValue n with | true, code -> Some code | _ -> Some (raw.Substring 1)'

mutant_prepare left-boundary \
  'let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'"'"'])(_+[A-Za-z0-9][A-Za-z0-9_'"'"']*|[a-z][A-Za-z0-9_'"'"']{3,})"' \
  'let destructuredToken = javaRx "(?:^|.)(_+[A-Za-z0-9][A-Za-z0-9_'"'"']*|[a-z][A-Za-z0-9_'"'"']{3,})"'

# Every mutant is built before any arm judges one.
mutant_build_all

prove_false_positive_mutation \
  "literal allowlist" "$fg117_b_fixture" true literal-allowlist

prove_false_positive_mutation \
  "definition-form filter" "$fg117_c_fixture" inputValue definition-form

prove_coverage_mutation \
  "numeric tuple discrimination" "$fg117_c_numeric_fixture" staleGateValue numeric-tuple

prove_false_positive_mutation \
  "underscore preservation" "$fg117_e_fixture" staleGateValue underscore-preservation

prove_coverage_mutation \
  "multi-underscore binder" "$fg117_e_multi_fixture" __staleGateValue multi-underscore

prove_coverage_pair_mutation \
  "underscore uppercase/digit domain" "$fg117_e_domain_fixture" _StaleGateValue _123GateValue underscore-domain

prove_false_positive_mutation \
  "underscore complete-token length floor" "$fg117_e_short_floor_fixture" __A length-floor

prove_coverage_mutation \
  "underscore suffix-floor regression" "$fg117_e_total_floor_fixture" __Ab suffix-floor

prove_false_positive_mutation \
  "base lexical projection" "$fg117_f_fixture" staleDirectValue base-projection

prove_false_positive_mutation \
  "constructor left boundary" "$fg117_g_fixture" hoice left-boundary

[ "$FAIL" -eq 0 ] && echo "STALE-REF PROOF: supported forms report, pinned boundaries stay silent, and FG-117 false positives are excluded (10 direct mutations killed)" \
                  || { echo "STALE-REF PROOF FAILED"; exit 1; }
