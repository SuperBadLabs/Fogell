#!/usr/bin/env python3
"""Fail-closed semantic checker for retained FG-037 probe evidence."""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
from pathlib import Path


COUNTS = (250, 251, 400)
EMPTY_WORKSPACE = hashlib.sha256(b"").hexdigest()


def fail(message: str) -> None:
    raise ValueError(message)


def expected_case(count: int) -> str:
    steps = ["        sh 'printf reached > reached-agent.txt'"]
    steps.extend(f"        echo 'FG037-{i:03d}'" for i in range(2, count + 1))
    return (
        "pipeline {\n"
        "  agent any\n"
        "  stages {\n"
        "    stage('boundary') {\n"
        "      steps {\n"
        + "\n".join(steps)
        + "\n      }\n"
        "    }\n"
        "  }\n"
        "}\n"
    )


def one_line(text: str, label: str, pattern: str) -> str:
    matches = re.findall(pattern, text, flags=re.MULTILINE)
    if len(matches) != 1:
        fail(f"{label}: expected exactly one match, found {len(matches)}")
    return matches[0]


def side_block(receipt: str, side: str, following: str | None) -> str:
    start_marker = f"\n## {side}\n"
    if receipt.count(start_marker) != 1:
        fail(f"receipt has zero or duplicate {side} sections")
    start = receipt.index(start_marker) + len(start_marker)
    if following is None:
        end = len(receipt)
    else:
        end_marker = f"\n## {following}\n"
        if receipt.count(end_marker) != 1:
            fail(f"receipt has zero or duplicate {following} sections")
        end = receipt.index(end_marker, start)
    return receipt[start:end]


def parse_side(receipt: str, side: str, following: str | None) -> tuple[str, str, list[str]]:
    block = side_block(receipt, side, following)
    result = one_line(block, f"{side} result", r"^  result:\s+(\S+)\s*$")
    workspace = one_line(block, f"{side} workspace hash", r"^  workspace-hash:\s+([0-9a-f]{64})\s*$")
    count_text = one_line(block, f"{side} output header", r"^  output \(([0-9]+) lines\):\s*$")
    output = re.findall(r"^    \| (.*)$", block, flags=re.MULTILINE)
    if int(count_text) != len(output):
        fail(f"{side} output header says {count_text}, block contains {len(output)} lines")
    return result, workspace, output


def expected_workspace_hash() -> str:
    content_hash = hashlib.sha256(b"reached").hexdigest()
    manifest = f"reached-agent.txt\t{content_hash}".encode()
    return hashlib.sha256(manifest).hexdigest()


def check_case(path: Path, count: int) -> bytes:
    raw = path.read_bytes()
    expected = expected_case(count).encode()
    if raw != expected:
        fail(f"{path.name}: case bytes are not the exact deterministic {count}-step fixture")
    return raw


def check_receipt(path: Path, case_bytes: bytes, count: int, core: str) -> None:
    receipt = path.read_text(encoding="utf-8")
    recorded_core = one_line(receipt, f"{path.name} core", r"^jenkins-core:\s+(\S+)\s*$")
    if recorded_core != core:
        fail(f"{path.name}: jenkins-core={recorded_core}, expected {core}")

    digest = one_line(receipt, f"{path.name} case digest", r"^case-digest:\s+([0-9a-f]{64})\s*$")
    actual_digest = hashlib.sha256(case_bytes).hexdigest()
    if digest != actual_digest:
        fail(f"{path.name}: case digest does not bind the retained fixture")

    verdict = one_line(receipt, f"{path.name} verdict", r"^(VERDICT: .+)$")
    j_result, j_workspace, j_output = parse_side(receipt, "Jenkins", "Fogell")
    f_result, f_workspace, f_output = parse_side(receipt, "Fogell", None)

    markers = [f"FG037-{i:03d}" for i in range(2, count + 1)]
    fogell_markers = [line for line in f_output if line.startswith("FG037-")]
    if fogell_markers != markers:
        fail(f"{path.name}: Fogell did not execute markers 002..{count:03d} exactly once and in order")
    if len(f_output) != count or f_output.count("+ printf reached") != 1:
        fail(f"{path.name}: Fogell output is not one sentinel step plus {count - 1} markers")
    if f_result != "success" or f_workspace != expected_workspace_hash():
        fail(f"{path.name}: Fogell did not succeed with the exact sentinel workspace")

    if count == 250:
        expected_verdict = "VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash"
        if verdict != expected_verdict:
            fail(f"{path.name}: 250-step control is not tier-1 PROVEN")
        if j_result != "success" or j_workspace != expected_workspace_hash():
            fail(f"{path.name}: Jenkins 250-step control did not succeed with the sentinel")
        if j_output != f_output:
            fail(f"{path.name}: 250-step control output differs between engines")
    else:
        if verdict != "VERDICT: DIVERGED (3)":
            fail(f"{path.name}: intended {count}-step boundary is not recorded as DIVERGED")
        # A Declarative compilation failure occurs before `[Pipeline] Start of
        # Pipeline`, so the existing normaliser retains controller narration
        # such as `Started by user unknown or anonymous`. That is not a build
        # effect. The sealed facts that establish the boundary are the empty
        # workspace and absence of both our first-step xtrace and every marker.
        reached_output = any(line == "+ printf reached" or line.startswith("FG037-") for line in j_output)
        if j_result != "failure" or j_workspace != EMPTY_WORKSPACE or reached_output:
            fail(f"{path.name}: Jenkins must fail before any sentinel or marker effect")
        if count == 400:
            compiler_limit = (
                "General error during class generation: The max number of supported "
                "arguments is 255, but found 400"
            )
            if j_output.count(compiler_limit) != 1:
                fail(f"{path.name}: Jenkins output does not prove the exact 400/255 argument limit")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", required=True, type=Path)
    parser.add_argument("--receipts", required=True, type=Path)
    parser.add_argument("--jenkins-core", default="2.568.1")
    args = parser.parse_args()

    expected_cases = {f"fg037-{n}-steps.Jenkinsfile" for n in COUNTS}
    expected_receipts = {f"fg037-{n}-steps.receipt.txt" for n in COUNTS}
    actual_cases = {p.name for p in args.cases.glob("*.Jenkinsfile")}
    actual_receipts = {p.name for p in args.receipts.glob("*.receipt.txt")}
    if actual_cases != expected_cases:
        fail(f"case inventory mismatch: got {sorted(actual_cases)}")
    if actual_receipts != expected_receipts:
        fail(f"receipt inventory mismatch: got {sorted(actual_receipts)}")

    for count in COUNTS:
        case = args.cases / f"fg037-{count}-steps.Jenkinsfile"
        receipt = args.receipts / f"fg037-{count}-steps.receipt.txt"
        check_receipt(receipt, check_case(case, count), count, args.jenkins_core)

    print("FG-037 semantic evidence PASS: 250 proven; 251/400 Jenkins fail before effects; Fogell runs all steps")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError, ValueError) as error:
        print(f"FG-037 semantic evidence FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
