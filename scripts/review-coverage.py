#!/usr/bin/env python3.12
"""FG-199. Did every expected reviewer cover the commit being judged?

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
  - historical audit mode skips a PR closed without merge: nothing landed
  - single-PR ``--pr N`` mode refuses a closed-unmerged PR with exit 2: it is
    neither an open candidate nor immutable evidence of something that landed
  - it cannot see reviews GitHub has garbage-collected or that were dismissed

The no-argument audit retains its historical meaning.  ``--pr N`` is the
HeMan-local post-publication, pre-merge guard: it checks the current head twice
around the review reads and refuses if the head moved while it was deciding.

usage: scripts/review-coverage.py [--repo OWNER/NAME] [--reviewers a,b]
                                  [--pr N] [--json]
       exit 1 for missing coverage, 2 when no verdict can be made.
"""

import argparse
import json
import re
import subprocess
import sys
from typing import Any

DEFAULT_REVIEWERS = ["copilot-pull-request-reviewer[bot]", "chatgpt-codex-connector[bot]"]
CODEX_REVIEWER = "chatgpt-codex-connector[bot]"
CODEX_CLEAN_PREFIX = "Codex Review: Didn't find any major issues."
CODEX_COMMIT_RE = re.compile(
    r"^\*\*Reviewed commit:\*\* `(?P<sha>[0-9a-fA-F]{10,40})`\s*$",
    re.MULTILINE,
)


class CoverageError(Exception):
    """The checker could not make a trustworthy coverage decision."""


def parse_json_values(body: str, path: str) -> list | dict:
    """Parse one JSON value or gh's whitespace-separated paginated values."""
    decoder = json.JSONDecoder()
    values: list[Any] = []
    at = 0
    while at < len(body):
        while at < len(body) and body[at].isspace():
            at += 1
        if at == len(body):
            break
        try:
            value, at = decoder.raw_decode(body, at)
        except json.JSONDecodeError as exc:
            raise CoverageError(f"invalid JSON from gh api {path}: {exc}") from exc
        values.append(value)
    if not values:
        return []
    if len(values) == 1:
        return values[0]
    if not all(isinstance(value, list) for value in values):
        raise CoverageError(f"mixed paginated JSON shapes from gh api {path}")
    return [item for value in values for item in value]


def gh(path: str) -> list | dict:
    """One `gh api` call. Fails loudly: a checker that swallows a transport error
    and reports 'no problems' is the FG-158 defect this file exists to avoid."""
    out = subprocess.run(
        ["gh", "api", "--paginate", path],
        capture_output=True, text=True,
    )
    if out.returncode != 0:
        detail = out.stderr.strip()
        suffix = f"\n{detail}" if detail else ""
        raise CoverageError(f"gh api {path}{suffix}")
    body = out.stdout.strip()
    return parse_json_values(body, path)


def positive_pr(value: str) -> int:
    try:
        number = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("PR number must be a positive integer") from exc
    if number <= 0:
        raise argparse.ArgumentTypeError("PR number must be a positive integer")
    return number


def expected_reviewers(raw: str) -> list[str]:
    result: list[str] = []
    seen: set[str] = set()
    for value in raw.split(","):
        reviewer = value.strip()
        key = reviewer.casefold()
        if reviewer and key not in seen:
            result.append(reviewer)
            seen.add(key)
    return result


def as_list(value: list | dict, what: str) -> list:
    if not isinstance(value, list):
        raise CoverageError(f"expected a JSON array for {what}")
    return value


def pr_snapshot(repo: str, number: int) -> dict:
    value = gh(f"repos/{repo}/pulls/{number}")
    if not isinstance(value, dict):
        raise CoverageError(f"expected a PR object for {repo}#{number}")
    state = value.get("state")
    head = value.get("head")
    sha = head.get("sha") if isinstance(head, dict) else None
    if state not in {"open", "closed"} or not isinstance(sha, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", sha):
        raise CoverageError(f"PR {repo}#{number} has no trustworthy state and full head SHA")
    title = value.get("title")
    if not isinstance(title, str):
        raise CoverageError(f"PR {repo}#{number} has no trustworthy title")
    merged_at = value.get("merged_at")
    if state == "closed" and not merged_at:
        raise CoverageError(f"PR {repo}#{number} is closed without merge; no candidate may pass")
    return {
        "number": number,
        "state": "merged" if merged_at else "open",
        "head": sha.lower(),
        "merged_at": merged_at,
        "title": title,
    }


def login_of(value: dict, what: str) -> str:
    user = value.get("user")
    login = user.get("login") if isinstance(user, dict) else None
    if not isinstance(login, str) or not login:
        raise CoverageError(f"{what} has no reviewer login")
    return login


def formal_evidence(reviews: list, reviewer: str) -> list[dict]:
    result = []
    for review in reviews:
        if not isinstance(review, dict):
            raise CoverageError("PR reviews contained a non-object")
        login = login_of(review, "PR review")
        # Validate every submitted record before filtering by expected login. A
        # malformed unrelated entry means this API page is not trustworthy
        # evidence; silently ignoring it would turn corrupt input into a pass.
        submitted_at = review.get("submitted_at")
        if submitted_at is None:
            continue
        if not isinstance(submitted_at, str) or not submitted_at:
            raise CoverageError("submitted PR review has no trustworthy submitted_at")
        commit = review.get("commit_id")
        if not isinstance(commit, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", commit):
            raise CoverageError("submitted PR review has no trustworthy full commit_id")
        state = review.get("state")
        if not isinstance(state, str):
            raise CoverageError("submitted PR review has no trustworthy state")
        if login.casefold() != reviewer.casefold():
            continue
        result.append({
            "source": "pull_request_review",
            "commit": commit.lower(),
            "submitted_at": submitted_at,
            "state": state,
        })
    return result


def codex_clean_evidence(comments: list, reviewer: str) -> list[dict]:
    if reviewer.casefold() != CODEX_REVIEWER.casefold():
        return []
    result = []
    for comment in comments:
        if not isinstance(comment, dict):
            raise CoverageError("issue comments contained a non-object")
        login = login_of(comment, "issue comment")
        if login.casefold() != CODEX_REVIEWER.casefold():
            continue
        body = comment.get("body")
        if not isinstance(body, str) or not body.startswith(CODEX_CLEAN_PREFIX):
            continue
        matches = list(CODEX_COMMIT_RE.finditer(body))
        # An unrecognised or ambiguous bot message is not evidence. It remains a
        # loud missing-review result rather than being guessed into a pass.
        if len(matches) != 1:
            continue
        created_at = comment.get("created_at")
        if not isinstance(created_at, str) or not created_at:
            raise CoverageError("Codex clean issue comment has no trustworthy created_at")
        result.append({
            "source": "codex_clean_issue_comment",
            "commit": matches[0].group("sha").lower(),
            "submitted_at": created_at,
            "state": "COMMENTED",
        })
    return result


def evidence_covers_head(evidence: dict, head: str) -> bool:
    commit = evidence["commit"]
    if evidence["source"] == "pull_request_review":
        return commit == head
    return len(commit) >= 10 and head.startswith(commit)


def reviewer_row(reviewer: str, head: str, reviews: list, comments: list) -> dict:
    evidence = formal_evidence(reviews, reviewer) + codex_clean_evidence(comments, reviewer)
    evidence.sort(key=lambda item: (item["submitted_at"], item["source"], item["commit"]))
    exact = [item for item in evidence if evidence_covers_head(item, head)]
    if exact:
        reason = None
    elif evidence:
        reason = "reviewed an earlier commit only"
    else:
        reason = "never reviewed the PR at all"
    return {
        "reviewer": reviewer,
        "covered": bool(exact),
        "reason": reason,
        "evidence": exact,
        "reviewed_commits": sorted({item["commit"] for item in evidence}),
    }


def pr_coverage(repo: str, number: int, expected: list[str]) -> dict:
    before = pr_snapshot(repo, number)
    reviews: list = []
    comments: list = []
    if expected:
        reviews = as_list(
            gh(f"repos/{repo}/pulls/{number}/reviews?per_page=100"),
            f"reviews for {repo}#{number}",
        )
        comments = as_list(
            gh(f"repos/{repo}/issues/{number}/comments?per_page=100"),
            f"issue comments for {repo}#{number}",
        )
    after = pr_snapshot(repo, number)
    if (before["head"], before["state"], before["merged_at"]) != (
        after["head"], after["state"], after["merged_at"]
    ):
        raise CoverageError(
            f"PR {repo}#{number} changed while coverage was checked "
            f"({before['head']} -> {after['head']}); run again"
        )
    rows = [reviewer_row(reviewer, before["head"], reviews, comments) for reviewer in expected]
    return {
        "mode": "pr",
        "repo": repo,
        "pr": number,
        "state": before["state"],
        "head": before["head"],
        "expected_reviewers": expected,
        "reviewers": rows,
        "covered": all(row["covered"] for row in rows),
    }


def print_pr(result: dict) -> None:
    print(f"review coverage of the CURRENT head — {result['repo']} PR#{result['pr']}")
    print(f"current head: {result['head']}")
    print(f"state: {result['state']}")
    expected = ", ".join(result["expected_reviewers"]) or "(none)"
    print(f"expected reviewers: {expected}")
    print("COVERAGE ONLY: this does not clear findings, checks, or merge state.\n")
    for row in result["reviewers"]:
        if row["covered"]:
            sources = ", ".join(sorted({item["source"] for item in row["evidence"]}))
            print(f"  [ok  ] {row['reviewer']} — current head via {sources}")
        else:
            print(f"  [MISS] {row['reviewer']} — {row['reason']}")
    print(f"\nRESULT: {'covered' if result['covered'] else 'uncovered'} — {result['head']}")


def audit_pr(value: Any, repo: str) -> dict | None:
    """Validate a closed-list entry; return None only for closed-unmerged."""
    if not isinstance(value, dict):
        raise CoverageError(f"closed PR list for {repo} contained a non-object")
    number = value.get("number")
    if isinstance(number, bool) or not isinstance(number, int) or number <= 0:
        raise CoverageError(f"closed PR list for {repo} contained an invalid PR number")
    if value.get("state") != "closed":
        raise CoverageError(f"PR {repo}#{number} from the closed list is not closed")
    merged_at = value.get("merged_at")
    if merged_at is None:
        return None
    if not isinstance(merged_at, str) or not merged_at:
        raise CoverageError(f"merged PR {repo}#{number} has no trustworthy merged_at")
    head = value.get("head")
    sha = head.get("sha") if isinstance(head, dict) else None
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", sha):
        raise CoverageError(f"merged PR {repo}#{number} has no trustworthy full head SHA")
    title = value.get("title")
    if not isinstance(title, str):
        raise CoverageError(f"merged PR {repo}#{number} has no trustworthy title")
    return {"number": number, "head": sha.lower(), "title": title}


def audit_merged(repo: str, expected: list[str]) -> tuple[list[dict], list[dict]]:
    """The original repository-wide audit, kept output-compatible."""
    values = as_list(gh(f"repos/{repo}/pulls?state=closed&per_page=100"), "closed PRs")
    prs = []
    for value in values:
        pr = audit_pr(value, repo)
        if pr is not None:
            prs.append(pr)
    rows = []
    for pr in sorted(prs, key=lambda p: p["number"]):
        n = pr["number"]
        head = pr["head"]
        reviews: list = []
        comments: list = []
        if expected:
            reviews = as_list(
                gh(f"repos/{repo}/pulls/{n}/reviews?per_page=100"),
                f"reviews for {repo}#{n}",
            )
            comments = as_list(
                gh(f"repos/{repo}/issues/{n}/comments?per_page=100"),
                f"issue comments for {repo}#{n}",
            )
        reviewer_rows = [reviewer_row(reviewer, head, reviews, comments) for reviewer in expected]
        missing = [row["reviewer"] for row in reviewer_rows if not row["covered"]]
        rows.append({
            "pr": n,
            "merged_head": head[:9],
            "covered": not missing,
            "missing_on_head": missing,
            "absent_entirely": [
                row["reviewer"] for row in reviewer_rows
                if row["reason"] == "never reviewed the PR at all"
            ],
            "title": pr["title"][:58],
        })
    return rows, [row for row in rows if not row["covered"]]


def print_audit(repo: str, expected: list[str], rows: list[dict], bad: list[dict]) -> None:
    print(f"review coverage of the MERGED commit — {repo}")
    print(f"expected reviewers: {', '.join(expected)}\n")
    for row in rows:
        mark = "ok  " if row["covered"] else "MISS"
        print(f"  [{mark}] PR#{row['pr']:<4} {row['merged_head']}  {row['title']}")
        if row["missing_on_head"]:
            never = set(row["absent_entirely"])
            for missing in row["missing_on_head"]:
                why = "never reviewed the PR at all" if missing in never else "reviewed an earlier commit only"
                print(f"           missing: {missing} — {why}")
    print(f"\n{len(rows)} merged PRs; {len(bad)} without full coverage of the commit that landed")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="SuperBadLabs/Fogell")
    ap.add_argument("--reviewers", default=",".join(DEFAULT_REVIEWERS))
    ap.add_argument("--pr", type=positive_pr)
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()
    try:
        expected = expected_reviewers(args.reviewers)
        if args.pr is not None:
            result = pr_coverage(args.repo, args.pr, expected)
            if args.json:
                print(json.dumps(result, indent=2, sort_keys=True))
            else:
                print_pr(result)
            return 0 if result["covered"] else 1
        rows, bad = audit_merged(args.repo, expected)
        if args.json:
            print(json.dumps({"rows": rows, "uncovered": len(bad)}, indent=2))
        else:
            print_audit(args.repo, expected, rows, bad)
        return 1 if bad else 0
    except CoverageError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 2
    except Exception as exc:
        print(f"FAIL: unexpected {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
