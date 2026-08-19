#!/usr/bin/env bash
# FG-199. Offline proof for review-coverage.py --pr. The fake `gh` holds the
# recorded #58/#59/#60 facts and planted good/bad API shapes. No network or
# GitHub credentials are involved, so this proof is safe to run in every gate.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
CHECKER="$PWD/scripts/review-coverage.py"
LAB=$(mktemp -d /tmp/fogell-review-coverage-proof.XXXXXX)
trap 'rm -rf "$LAB"' EXIT
mkdir -p "$LAB/bin"

cat > "$LAB/bin/gh" <<'PY'
#!/usr/bin/env python3.12
import json
import os
import sys

case = os.environ["FAKE_GH_CASE"]
path = sys.argv[-1]
state_file = os.environ["FAKE_GH_STATE"]
COPILOT = "copilot-pull-request-reviewer[bot]"
CODEX = "chatgpt-codex-connector[bot]"
HEAD = "1111111111111111111111111111111111111111"
OTHER = "2222222222222222222222222222222222222222"
RACE = "3333333333333333333333333333333333333333"

def review(login, commit):
    return {"user": {"login": login}, "commit_id": commit,
            "submitted_at": "2026-08-18T01:00:00Z", "state": "COMMENTED"}

def clean(login, commit):
    return {"user": {"login": login}, "created_at": "2026-08-18T01:01:00Z",
            "body": "Codex Review: Didn't find any major issues. Clear!\n\n"
                    f"**Reviewed commit:** `{commit}`\n"}

fixtures = {
    "pr58": {
        "number": 58,
        "head": "ca6a0ea153124bf44cded3501e25db70343defb3",
        "reviews": [
            review(COPILOT, "552c99f660a852913b3511711ec5c5d77488932f"),
            review(CODEX, "552c99f660a852913b3511711ec5c5d77488932f"),
            review(CODEX, "ca6a0ea153124bf44cded3501e25db70343defb3"),
        ], "comments": []},
    "pr59": {
        "number": 59,
        "head": "021fcb5d94d485c3f12b88af58553a5c3e78d90c",
        "reviews": [
            review(COPILOT, "4e48809241b416a4286c1a6d8554f5baef393fdb"),
            review(CODEX, "4e48809241b416a4286c1a6d8554f5baef393fdb"),
        ], "comments": []},
    "pr60": {
        "number": 60,
        "head": "c481649fba816642093c6d472fc7ad720e21f1c8",
        "reviews": [review(COPILOT, "2fce430439068e371ce47f767ef9dd304a9db6e6")],
        "comments": []},
    "pass": {
        "number": 999, "head": HEAD,
        "reviews": [review(COPILOT, OTHER), review(COPILOT, HEAD), review(CODEX, OTHER)],
        "comments": [clean("a-human", HEAD[:10]), clean(CODEX, HEAD[:10])]},
    "spoof": {
        "number": 999, "head": HEAD, "reviews": [review(COPILOT, HEAD)],
        "comments": [clean("a-human", HEAD[:10])]},
    "stale": {
        "number": 999, "head": HEAD, "reviews": [review(COPILOT, HEAD)],
        "comments": [clean(CODEX, OTHER[:10])]},
    "pagination": {
        "number": 999, "head": HEAD,
        "reviews": [review(COPILOT, OTHER), review(CODEX, OTHER),
                    review(COPILOT, HEAD), review(CODEX, HEAD)],
        "comments": []},
    "race": {
        "number": 999, "head": HEAD,
        "reviews": [review(COPILOT, HEAD), review(CODEX, HEAD)], "comments": []},
    "closed": {
        "number": 999, "head": HEAD, "closed_unmerged": True,
        "reviews": [], "comments": []},
    "malformed": {
        "number": 999, "head": HEAD,
        "reviews": [], "comments": []},
    "api-fail": {
        "number": 999, "head": HEAD,
        "reviews": [], "comments": []},
}

if case == "audit":
    if path == "repos/SuperBadLabs/Fogell/pulls?state=closed&per_page=100":
        print(json.dumps([
            {"number": 999, "state": "closed", "merged_at": "2026-08-18T02:00:00Z",
             "head": {"sha": HEAD}, "title": "covered fixture"},
            {"number": 998, "state": "closed", "merged_at": None,
             "head": {"sha": OTHER}, "title": "closed without merge"},
        ]))
        sys.exit(0)
    if path == "repos/SuperBadLabs/Fogell/pulls/999/reviews":
        print(json.dumps([review(COPILOT, HEAD), review(CODEX, HEAD)]))
        sys.exit(0)
    print(f"unexpected audit endpoint: {path}", file=sys.stderr)
    sys.exit(64)

fixture = fixtures[case]
number = fixture["number"]
base = f"repos/SuperBadLabs/Fogell/pulls/{number}"
if path == base:
    head = fixture["head"]
    if case == "race":
        calls = 0
        if os.path.exists(state_file):
            calls = int(open(state_file, encoding="utf-8").read())
        with open(state_file, "w", encoding="utf-8") as handle:
            handle.write(str(calls + 1))
        if calls:
            head = RACE
    closed = fixture.get("closed_unmerged", False)
    print(json.dumps({
        "number": number,
        "state": "closed" if closed or number in {58, 59, 60} else "open",
        "merged_at": None if not (closed or number in {58, 59, 60}) else
                     (None if closed else "2026-08-15T12:00:00Z"),
        "head": {"sha": head}, "title": f"fixture {case}",
    }))
    sys.exit(0)
if path == f"{base}/reviews?per_page=100":
    if case == "api-fail":
        print("planted transport failure", file=sys.stderr)
        sys.exit(1)
    if case == "malformed":
        print("{not-json")
        sys.exit(0)
    if case == "pagination":
        print(json.dumps(fixture["reviews"][:2]))
        print(json.dumps(fixture["reviews"][2:]))
    else:
        print(json.dumps(fixture["reviews"]))
    sys.exit(0)
if path == f"repos/SuperBadLabs/Fogell/issues/{number}/comments?per_page=100":
    print(json.dumps(fixture["comments"]))
    sys.exit(0)
print(f"unexpected endpoint for {case}: {path}", file=sys.stderr)
sys.exit(64)
PY
chmod +x "$LAB/bin/gh"
COPILOT="copilot-pull-request-reviewer[bot]"
CODEX="chatgpt-codex-connector[bot]"

RC=0
OUT="$LAB/out"
FAILED=0
run_case() {
  local case=$1; shift
  rm -f "$LAB/state"
  set +e
  PATH="$LAB/bin:$PATH" FAKE_GH_CASE="$case" FAKE_GH_STATE="$LAB/state" \
    "$CHECKER" "$@" >"$OUT" 2>&1
  RC=$?
  set -e
}
expect_rc() {
  local label=$1 expected=$2
  if [ "$RC" -eq "$expected" ]; then
    printf '  %-48s rc=%s — OK\n' "$label" "$RC"
  else
    printf '  FAIL: %-42s wanted rc=%s, got %s\n' "$label" "$expected" "$RC"
    sed 's/^/    | /' "$OUT"
    FAILED=1
  fi
}
expect_text() {
  local text=$1
  if ! grep -Fq -- "$text" "$OUT"; then
    echo "  FAIL: output did not contain: $text"
    sed 's/^/    | /' "$OUT"
    FAILED=1
  fi
}

echo "=== review coverage: recorded known-bad heads ==="
run_case pr58 --pr 58
expect_rc "PR #58 rejects Copilot's earlier-head review" 1
expect_text "current head: ca6a0ea153124bf44cded3501e25db70343defb3"
expect_text "[MISS] $COPILOT — reviewed an earlier commit only"
expect_text "[ok  ] $CODEX — current head"

run_case pr59 --pr 59
expect_rc "PR #59 rejects both earlier-head reviews" 1
expect_text "[MISS] $COPILOT — reviewed an earlier commit only"
expect_text "[MISS] $CODEX — reviewed an earlier commit only"

run_case pr60 --pr 60
expect_rc "PR #60 separates earlier from absent" 1
expect_text "[MISS] $COPILOT — reviewed an earlier commit only"
expect_text "[MISS] $CODEX — never reviewed the PR at all"

echo "=== review coverage: positive and identity arms ==="
run_case pass --pr 999
expect_rc "formal Copilot + bot-authored clean Codex pass" 0
expect_text "[ok  ] $COPILOT — current head via pull_request_review"
expect_text "[ok  ] $CODEX — current head via codex_clean_issue_comment"

run_case spoof --pr 999
expect_rc "human copy of a Codex result cannot cover Codex" 1
expect_text "[MISS] $CODEX — never reviewed the PR at all"

run_case stale --pr 999
expect_rc "Codex clean result on an older head is stale" 1
expect_text "[MISS] $CODEX — reviewed an earlier commit only"

run_case pass --pr 999 --reviewers definitely-not-a-reviewer
expect_rc "impossible reviewer is absent, never approval" 1
expect_text "[MISS] definitely-not-a-reviewer — never reviewed the PR at all"

run_case pass --pr 999 --reviewers ""
expect_rc "empty required-reviewer set passes explicitly" 0
expect_text "expected reviewers: (none)"

run_case pagination --pr 999
expect_rc "exact reviews on pagination page two count" 0

echo "=== review coverage: refusal arms ==="
run_case race --pr 999
expect_rc "head moving during the check refuses a verdict" 2
expect_text "changed while coverage was checked"

run_case closed --pr 999
expect_rc "closed-unmerged PR refuses a verdict" 2
expect_text "closed without merge"

run_case malformed --pr 999
expect_rc "malformed GitHub JSON fails closed" 2
expect_text "invalid JSON from gh api"

run_case api-fail --pr 999
expect_rc "GitHub transport failure fails closed" 2
expect_text "planted transport failure"

echo "=== review coverage: JSON and historical audit compatibility ==="
run_case pass --pr 999 --json
expect_rc "single-PR JSON reports a covered full head" 0
python3.12 - "$OUT" <<'PY'
import json, sys
value = json.load(open(sys.argv[1], encoding="utf-8"))
assert value["mode"] == "pr"
assert value["head"] == "1111111111111111111111111111111111111111"
assert value["covered"] is True
assert [r["reviewer"] for r in value["reviewers"]] == [
    "copilot-pull-request-reviewer[bot]", "chatgpt-codex-connector[bot]"]
PY

run_case audit
expect_rc "no-argument merged-PR audit remains compatible" 0
expect_text "1 merged PRs; 0 without full coverage"

if [ "$FAILED" -ne 0 ]; then
  echo "REVIEW-COVERAGE PROOF FAILED"
  exit 1
fi
echo "REVIEW-COVERAGE PROOF: known-bad heads reject for the named reason; exact formal and Codex-clean evidence pass; identity, pagination, race, lifecycle, transport, JSON and historical-audit arms hold"
