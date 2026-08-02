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
  if [ "$rc" -ne 0 ] && grep -q "name an identifier this diff deleted" <<<"$out"; then
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

[ "$FAIL" -eq 0 ] && echo "STALE-REF PROOF: every supported form fails when it should" \
                  || { echo "STALE-REF PROOF FAILED"; exit 1; }
