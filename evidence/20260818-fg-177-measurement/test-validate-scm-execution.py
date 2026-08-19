#!/usr/bin/env python3
"""Focused fail-closed proof for harness-owned FG-177 SCM attestation."""

from __future__ import annotations

import hashlib
import pathlib
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent
VALIDATOR = ROOT / "validate-scm-execution.py"
SCM_REV, SCM_TREE, SCM_BLOB = "1" * 40, "2" * 40, "3" * 40
GIT_REV, GIT_TREE = "4" * 40, "5" * 40


def digest(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def checkout_receipt() -> str:
    return (
        "fixture\n\n## Jenkins\n  engine notes (not compared):\n"
        f"    ! git-build-data revision={SCM_REV}\n\n"
        "## Fogell\n  result: failure\n  engine notes (not compared):\n"
        f"    ! scm-preflight branch=fogell-pins/{SCM_REV} revision={SCM_REV} "
        f"tree={SCM_TREE} jenkinsfile-blob={SCM_BLOB}\n"
    )


def git_receipt(*, fogell_executed: bool = False) -> str:
    fogell = ""
    if fogell_executed:
        fogell = f"    ! git-checkout branch=fogell-pins/{GIT_REV} revision={GIT_REV} url=file:///fixture.git\n"
    return (
        "fixture\n\n## Jenkins\n  engine notes (not compared):\n"
        f"    ! git-build-data revision={GIT_REV}\n\n"
        "## Fogell\n  result: failure\n  engine notes (not compared):\n" + fogell
    )


def write_fixture(root: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    receipts = root / "raw-receipts"
    receipts.mkdir()
    pin = root / "scm-pin.tsv"
    pin.write_text(
        "\n".join((
            "format\tfogell-scm-pin-v1",
            "source-branch\tcase/fg177-probe-checkout-scm",
            f"source-revision\t{SCM_REV}",
            f"scm-pinned-branch\tfogell-pins/{SCM_REV}",
            f"scm-pinned-revision\t{SCM_REV}",
            f"scm-tree\t{SCM_TREE}",
            f"jenkinsfile-blob\t{SCM_BLOB}",
            f"jenkinsfile-sha256\t{'6' * 64}",
            f"git-pinned-branch\tfogell-pins/{GIT_REV}",
            f"git-pinned-revision\t{GIT_REV}",
            f"git-tree\t{GIT_TREE}",
        )) + "\n",
        encoding="utf-8",
    )
    (receipts / "fg177-probe-checkout-scm.receipt.txt").write_text(checkout_receipt(), encoding="utf-8")
    (receipts / "fg177-probe-unknown-policy.receipt.txt").write_text(git_receipt(), encoding="utf-8")
    (receipts / "fg177-probe-return-semantics.receipt.txt").write_text(
        git_receipt(fogell_executed=True), encoding="utf-8"
    )
    return pin, receipts


def invoke(pin: pathlib.Path, receipts: pathlib.Path, output: pathlib.Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(VALIDATOR), str(pin), str(receipts), str(output)],
        text=True, capture_output=True, check=False,
    )


def refusal(mutate, diagnostic: str) -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = pathlib.Path(temporary)
        pin, receipts = write_fixture(root)
        mutate(receipts)
        output = root / "scm-execution.tsv"
        result = invoke(pin, receipts, output)
        assert result.returncode == 1, result.stderr
        assert diagnostic in result.stderr, result.stderr
        assert not output.exists()


def main() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = pathlib.Path(temporary)
        pin, receipts = write_fixture(root)
        output = root / "scm-execution.tsv"
        result = invoke(pin, receipts, output)
        assert result.returncode == 0, result.stderr
        lines = output.read_text(encoding="utf-8").splitlines()
        assert len(lines) == 10
        assert lines[0] == "format\tfogell-scm-execution-v2"
        assert lines[1].endswith(digest(receipts / "fg177-probe-checkout-scm.receipt.txt"))
        assert lines[6].split("\t")[3] == "not-executed"
        assert lines[9].split("\t")[3] == "executed"
        second = invoke(pin, receipts, output)
        assert second.returncode == 1 and "already exists" in second.stderr

    refusal(
        lambda receipts: (receipts / "fg177-probe-checkout-scm.receipt.txt").write_text(
            checkout_receipt().replace(SCM_REV, "0" * 40), encoding="utf-8"
        ),
        "does not exclusively attest",
    )
    refusal(
        lambda receipts: (receipts / "fg177-probe-return-semantics.receipt.txt").write_text(
            git_receipt(fogell_executed=True)
            + f"    ! git-checkout branch=fogell-pins/{GIT_REV} revision={GIT_REV} url=file:///fixture.git\n",
            encoding="utf-8",
        ),
        "duplicate Fogell",
    )
    refusal(
        lambda receipts: (receipts / "fg177-probe-unknown-policy.receipt.txt").write_text(
            f"    ! git-build-data revision={GIT_REV}\n" + git_receipt(), encoding="utf-8"
        ),
        "out-of-section or spoofed",
    )
    refusal(
        lambda receipts: (receipts / "fg177-probe-unknown-policy.receipt.txt").write_text(
            git_receipt().replace(f"revision={GIT_REV}", "revision=not-a-sha", 1), encoding="utf-8"
        ),
        "malformed or wrong-kind",
    )
    refusal(
        lambda receipts: (receipts / "fg177-probe-unknown-policy.receipt.txt").write_text(
            git_receipt().replace(f"revision={GIT_REV}", f"revision= {GIT_REV}", 1), encoding="utf-8"
        ),
        "malformed or wrong-kind",
    )
    refusal(
        lambda receipts: (receipts / "fg177-probe-unknown-policy.receipt.txt").write_text(
            git_receipt().replace(f"revision={GIT_REV}", f"revision=\n    ! {GIT_REV}", 1), encoding="utf-8"
        ),
        "malformed or wrong-kind",
    )
    print(
        "SCM HARNESS ATTESTATION PROOF: early Fogell non-execution represented; "
        "SCM preflight, executed git, mismatch, duplicate, spoof, padded/multiline/malformed and overwrite paths verified"
    )


if __name__ == "__main__":
    main()
