#!/usr/bin/env python3
"""Verify the FG-037 evidence manifest binds the exact payload inventory."""

from __future__ import annotations

import argparse
import hashlib
import re
import stat
import sys
from pathlib import Path, PurePosixPath


LINE = re.compile(r"^([0-9a-f]{64})  \./([^\\\r\n]+)$")


def fail(message: str) -> None:
    raise ValueError(message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence", type=Path)
    args = parser.parse_args()

    root = args.evidence
    if root.is_symlink() or not root.is_dir():
        fail("evidence root must be a real directory")

    manifest = root / "manifest.sha256"
    if manifest.is_symlink() or not manifest.is_file():
        fail("manifest.sha256 must be a real file")
    raw = manifest.read_bytes()
    if not raw or not raw.endswith(b"\n") or b"\r" in raw:
        fail("manifest must be non-empty LF-terminated UTF-8")
    text = raw.decode("utf-8")

    expected: dict[str, str] = {}
    for number, line in enumerate(text.splitlines(), start=1):
        match = LINE.fullmatch(line)
        if match is None:
            fail(f"manifest line {number} has invalid grammar")
        digest, name = match.groups()
        pure = PurePosixPath(name)
        if pure.is_absolute() or any(part in ("", ".", "..") for part in pure.parts):
            fail(f"manifest line {number} has an unsafe path")
        if name == "manifest.sha256":
            fail("manifest may not list itself")
        if name in expected:
            fail(f"manifest contains duplicate path: {name}")
        expected[name] = digest

    actual: set[str] = set()
    for path in root.rglob("*"):
        relative = path.relative_to(root).as_posix()
        mode = path.lstat().st_mode
        if stat.S_ISLNK(mode):
            fail(f"evidence contains a symlink: {relative}")
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            fail(f"evidence contains a non-regular payload: {relative}")
        if relative != "manifest.sha256":
            actual.add(relative)

    expected_names = set(expected)
    if actual != expected_names:
        missing = sorted(expected_names - actual)
        extra = sorted(actual - expected_names)
        fail(f"manifest inventory mismatch: missing={missing}, extra={extra}")

    for name in sorted(expected):
        digest = hashlib.sha256((root / name).read_bytes()).hexdigest()
        if digest != expected[name]:
            fail(f"manifest digest mismatch: {name}")

    print(
        f"FG-037 manifest verification PASS: {len(expected)} payload file(s), "
        "exact inventory and hashes match"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError, ValueError) as error:
        print(f"FG-037 manifest verification FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
