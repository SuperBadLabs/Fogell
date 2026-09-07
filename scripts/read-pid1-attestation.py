#!/usr/bin/env python3
"""Read one controller PID1 attestation without reopening its pathname."""

from __future__ import annotations

import os
import stat
import sys
from collections.abc import Callable
from pathlib import Path


MAX_ATTESTATION_BYTES = 4096


class Refusal(Exception):
    pass


def _metadata(value: os.stat_result) -> tuple[int, ...]:
    return (
        value.st_dev,
        value.st_ino,
        value.st_mode,
        value.st_uid,
        value.st_nlink,
        value.st_size,
        value.st_mtime_ns,
        value.st_ctime_ns,
    )


def _read_bounded(descriptor: int) -> bytes:
    chunks: list[bytes] = []
    remaining = MAX_ATTESTATION_BYTES + 1
    while remaining > 0:
        chunk = os.read(descriptor, remaining)
        if not chunk:
            break
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def read_attestation(
    path: Path,
    *,
    expected_uid: int | None = None,
    after_open: Callable[[], None] | None = None,
) -> bytes:
    """Open once, validate and read that descriptor; after_open exists for the swap proof."""
    if not hasattr(os, "O_NOFOLLOW") or not hasattr(os, "O_CLOEXEC"):
        raise Refusal("PID1 attestation reader requires Linux no-follow and close-on-exec flags")

    flags = os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC
    try:
        descriptor = os.open(path, flags)
    except FileNotFoundError:
        raise
    except OSError as error:
        raise Refusal(f"PID1 attestation could not be opened safely: {error.strerror}") from error

    try:
        if after_open is not None:
            after_open()

        opened = os.fstat(descriptor)
        owner = os.getuid() if expected_uid is None else expected_uid
        if not stat.S_ISREG(opened.st_mode):
            raise Refusal("PID1 attestation is not a regular file")
        if opened.st_uid != owner:
            raise Refusal("PID1 attestation is not owned by the proof user")
        if opened.st_nlink != 1:
            raise Refusal("PID1 attestation must have exactly one link")
        if opened.st_size <= 0 or opened.st_size > MAX_ATTESTATION_BYTES:
            raise Refusal("PID1 attestation has an invalid byte length")

        payload = _read_bounded(descriptor)
        after = os.fstat(descriptor)
        if _metadata(opened) != _metadata(after):
            raise Refusal("PID1 attestation changed while it was read")
        if len(payload) != opened.st_size or len(payload) > MAX_ATTESTATION_BYTES:
            raise Refusal("PID1 attestation read was not byte-exact")
        return payload
    finally:
        os.close(descriptor)


def main(arguments: list[str]) -> int:
    if len(arguments) != 1:
        print("FG-224 REFUSED: PID1 attestation reader needs exactly one path", file=sys.stderr)
        return 2

    try:
        payload = read_attestation(Path(arguments[0]))
    except FileNotFoundError:
        return 3
    except Refusal as error:
        print(f"FG-224 REFUSED: {error}", file=sys.stderr)
        return 1

    sys.stdout.buffer.write(payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
