#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""FG-199. Does the commit that was MERGED carry a review from every expected reviewer?

WHY THIS EXISTS RATHER THAN A HABIT OF CHECKING. Eight verifier rounds on the
FG-199 board row were spent correcting claims about GitHub state that I had
described instead of queried -- including one claim that a reviewer had stopped
emitting check-runs (it had not; I was querying the merge head, which it never
reviewed) and one WITHDRAWAL of a true claim because I said an audit log was
empty without reading it. Prose about API state does not converge. Output does.

WHAT "COVERED" MEANS HERE, stated because the first draft of the ticket got it
wrong: a review attaches to the SHA it was submitted against, not to the PR. A
PR whose reviewers all approved commit A and which then merged commit B has NO
review of the code that landed. That is the common case in this repo, not the
exception, and it is invisible to any check that asks "does this PR have
reviews".

WHAT IT DOES NOT CHECK, so a pass is not misread:
  - review QUALITY, or whether findings were addressed -- only presence
  - reviews by humans are counted the same as bots; --reviewers decides who counts
  - a PR closed without merging is skipped: nothing landed, so nothing is at risk
  - it cannot see reviews GitHub has garbage-collected or that were dismissed

usage: scripts/review-coverage.py [--repo OWNER/NAME] [--reviewers a,b] [--json]
       exit 1 if any merged PR lacks full coverage of its merged commit.
"""

import argparse
import json
import subprocess
import sys

DEFAULT_REVIEWERS = ["copilot-pull-request-reviewer[bot]", "chatgpt-codex-connector[bot]"]


def gh(path: str) -> list | dict:
    """One `gh api` call. Fails loudly: a checker that swallows a transport error
    and reports 'no problems' is the FG-158 defect this file exists to avoid."""
    out = subprocess.run(
        ["gh", "api", "--paginate", path],
        capture_output=True, text=True,
    )
    if out.returncode != 0:
        sys.exit(f"FAIL: gh api {path}\n{out.stderr.strip()}")
    body = out.stdout.strip()
    if not body:
        return []
    # --paginate concatenates JSON arrays; stitch them back together.
    try:
        return json.loads(body)
    except json.JSONDecodeError:
        merged: list = []
        for chunk in body.replace("][", "]\x00[").split("\x00"):
            merged.extend(json.loads(chunk))
        return merged


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="SuperBadLabs/Fogell")
    ap.add_argument("--reviewers", default=",".join(DEFAULT_REVIEWERS))
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()
    expected = [r.strip() for r in args.reviewers.split(",") if r.strip()]

    prs = gh(f"repos/{args.repo}/pulls?state=closed&per_page=100")
    rows = []
    for pr in sorted(prs, key=lambda p: p["number"]):
        if not pr.get("merged_at"):
            continue  # closed unmerged: nothing landed
        n = pr["number"]
        head = pr["head"]["sha"]
        reviews = gh(f"repos/{args.repo}/pulls/{n}/reviews")
        # A reviewer covers this PR only if it reviewed the SHA that merged.
        on_head = {r["user"]["login"] for r in reviews if r.get("commit_id") == head}
        anywhere = {r["user"]["login"] for r in reviews}
        missing = [r for r in expected if r not in on_head]
        rows.append({
            "pr": n,
            "merged_head": head[:9],
            "covered": not missing,
            "missing_on_head": missing,
            "absent_entirely": [r for r in expected if r not in anywhere],
            "title": pr["title"][:58],
        })

    bad = [r for r in rows if not r["covered"]]
    if args.json:
        print(json.dumps({"rows": rows, "uncovered": len(bad)}, indent=2))
    else:
        print(f"review coverage of the MERGED commit — {args.repo}")
        print(f"expected reviewers: {', '.join(expected)}\n")
        for r in rows:
            mark = "ok  " if r["covered"] else "MISS"
            print(f"  [{mark}] PR#{r['pr']:<4} {r['merged_head']}  {r['title']}")
            if r["missing_on_head"]:
                never = set(r["absent_entirely"])
                for m in r["missing_on_head"]:
                    why = "never reviewed the PR at all" if m in never else "reviewed an earlier commit only"
                    print(f"           missing: {m} — {why}")
        print(f"\n{len(rows)} merged PRs; {len(bad)} without full coverage of the commit that landed")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
