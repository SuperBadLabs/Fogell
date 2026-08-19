#!/usr/bin/env python3
"""Bind harness-owned Jenkins/Fogell SCM attestations to the FG-177 pin."""

from __future__ import annotations

import hashlib
import os
import pathlib
import re
import sys
import tempfile

PIN_KEYS = (
    "format", "source-branch", "source-revision", "scm-pinned-branch",
    "scm-pinned-revision", "scm-tree", "jenkinsfile-blob",
    "jenkinsfile-sha256", "git-pinned-branch", "git-pinned-revision", "git-tree",
)
HEX40 = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
JENKINS_NOTE = re.compile(r"^    ! git-build-data revision=([0-9a-f]{40})$")
FOGELL_PREFLIGHT = re.compile(
    r"^    ! scm-preflight branch=(fogell-pins/[0-9a-f]{40}) "
    r"revision=([0-9a-f]{40}) tree=([0-9a-f]{40}) "
    r"jenkinsfile-blob=([0-9a-f]{40})$"
)
FOGELL_CHECKOUT = re.compile(
    r"^    ! git-checkout branch=(fogell-pins/[0-9a-f]{40}) "
    r"revision=([0-9a-f]{40}) url=(\S+)$"
)


def fail(message: str) -> None:
    raise ValueError(message)


def regular_nonempty(path: pathlib.Path, label: str) -> None:
    if path.is_symlink() or not path.is_file() or path.stat().st_size == 0:
        fail(f"{label} must be a non-empty regular non-symlink file: {path}")


def digest(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_pin(path: pathlib.Path) -> dict[str, str]:
    regular_nonempty(path, "SCM pin")
    rows = [line.split("\t") for line in path.read_text(encoding="utf-8").splitlines()]
    if len(rows) != len(PIN_KEYS):
        fail("SCM pin has the wrong line count")
    values: dict[str, str] = {}
    for expected, row in zip(PIN_KEYS, rows, strict=True):
        if len(row) != 2 or row[0] != expected or not row[1]:
            fail(f"SCM pin is noncanonical at {expected}")
        values[row[0]] = row[1]
    if values["format"] != "fogell-scm-pin-v1":
        fail("unsupported SCM pin format")
    if values["source-branch"] != "case/fg177-probe-checkout-scm":
        fail("SCM pin source branch is not the reserved evidence branch")
    for key in (
        "source-revision", "scm-pinned-revision", "scm-tree",
        "jenkinsfile-blob", "git-pinned-revision", "git-tree",
    ):
        if HEX40.fullmatch(values[key]) is None:
            fail(f"SCM pin {key} is not one SHA-1")
    if HEX64.fullmatch(values["jenkinsfile-sha256"]) is None:
        fail("SCM pin Jenkinsfile SHA-256 is malformed")
    if values["source-revision"] != values["scm-pinned-revision"]:
        fail("source and SCM pinned revisions differ")
    for prefix in ("scm", "git"):
        if values[f"{prefix}-pinned-branch"] != "fogell-pins/" + values[f"{prefix}-pinned-revision"]:
            fail(f"{prefix} pin branch is not content-addressed")
    return values


def receipt_sections(path: pathlib.Path) -> dict[str, list[str]]:
    regular_nonempty(path, "receipt")
    sections: dict[str, list[str]] = {"Jenkins": [], "Fogell": []}
    current = ""
    tokens = ("git-build-data", "scm-preflight", "git-checkout")
    for line in path.read_text(encoding="utf-8").splitlines():
        if line == "## Jenkins":
            current = "Jenkins"
        elif line == "## Fogell":
            current = "Fogell"
        elif line.startswith("## "):
            current = ""
        if any(token in line for token in tokens):
            if current not in sections or not line.startswith("    ! "):
                fail(f"out-of-section or spoofed SCM attestation in {path}: {line}")
            sections[current].append(line)
    return sections


def exact_matches(lines: list[str], pattern: re.Pattern[str], label: str) -> list[tuple[str, ...]]:
    matched: list[tuple[str, ...]] = []
    tokens = ("git-build-data", "scm-preflight", "git-checkout")
    for line in lines:
        match = pattern.fullmatch(line)
        if match is not None:
            matched.append(match.groups())
        elif any(token in line for token in tokens):
            fail(f"malformed or wrong-kind {label} attestation: {line}")
    return matched


def validate_checkout(path: pathlib.Path, pin: dict[str, str]) -> tuple[str, str]:
    sections = receipt_sections(path)
    revision = pin["scm-pinned-revision"]
    jenkins = exact_matches(sections["Jenkins"], JENKINS_NOTE, "Jenkins BuildData")
    if not jenkins or any(value != (revision,) for value in jenkins):
        fail(f"{path} Jenkins BuildData does not exclusively attest {revision}: {jenkins}")
    preflight = exact_matches(sections["Fogell"], FOGELL_PREFLIGHT, "Fogell SCM preflight")
    expected = (pin["scm-pinned-branch"], revision, pin["scm-tree"], pin["jenkinsfile-blob"])
    if preflight != [expected]:
        fail(f"{path} Fogell preflight differs from pinned SCM identity: {preflight}")
    return "executed", "preflight"


def validate_git(path: pathlib.Path, pin: dict[str, str]) -> tuple[str, str]:
    sections = receipt_sections(path)
    revision = pin["git-pinned-revision"]
    jenkins = exact_matches(sections["Jenkins"], JENKINS_NOTE, "Jenkins BuildData")
    if not jenkins or any(value != (revision,) for value in jenkins):
        fail(f"{path} Jenkins BuildData does not exclusively attest {revision}: {jenkins}")
    fogell = exact_matches(sections["Fogell"], FOGELL_CHECKOUT, "Fogell git checkout")
    expected = (pin["git-pinned-branch"], revision)
    if any(value[:2] != expected for value in fogell):
        fail(f"{path} Fogell checkout differs from pinned git identity: {fogell}")
    if len(fogell) > 1:
        fail(f"{path} has duplicate Fogell git checkout attestations")
    return "executed", "executed" if fogell else "not-executed"


def main() -> int:
    if len(sys.argv) != 4:
        print(f"usage: {sys.argv[0]} PIN RECEIPT_DIR OUTPUT", file=sys.stderr)
        return 2
    pin_path, receipt_dir, output = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3])
    if output.exists() or output.is_symlink():
        fail(f"SCM execution output already exists: {output}")
    if output.parent.is_symlink() or not output.parent.is_dir():
        fail(f"SCM execution output parent is not a real directory: {output.parent}")
    pin = read_pin(pin_path)
    cases = (
        ("checkout", receipt_dir / "fg177-probe-checkout-scm.receipt.txt", "scm", validate_checkout,
         (pin["scm-pinned-revision"], pin["scm-tree"], pin["jenkinsfile-blob"])),
        ("unknown-policy-git", receipt_dir / "fg177-probe-unknown-policy.receipt.txt", "git", validate_git,
         (pin["git-pinned-revision"], pin["git-tree"])),
        ("return-semantics-git", receipt_dir / "fg177-probe-return-semantics.receipt.txt", "git", validate_git,
         (pin["git-pinned-revision"], pin["git-tree"])),
    )
    rows = ["format\tfogell-scm-execution-v2\n"]
    for name, receipt, kind, validator, identity in cases:
        jenkins_state, fogell_state = validator(receipt, pin)
        joined = ":".join(identity)
        rows.extend((
            f"case\t{name}\t{kind}\t{joined}\t{digest(receipt)}\n",
            f"attested\t{name}\tjenkins\t{jenkins_state}\t{joined}\n",
            f"attested\t{name}\tfogell\t{fogell_state}\t{joined}\n",
        ))
    descriptor, temporary = tempfile.mkstemp(prefix=f".{output.name}.", dir=output.parent)
    temporary_path = pathlib.Path(temporary)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.writelines(rows)
            stream.flush()
            os.fsync(stream.fileno())
        temporary_path.replace(output)
    finally:
        temporary_path.unlink(missing_ok=True)
    print(f"SCM harness attestations matched pinned identities: {output}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError) as error:
        print(f"ERROR: SCM execution validation refused: {error}", file=sys.stderr)
        raise SystemExit(1)
