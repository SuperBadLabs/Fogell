#!/usr/bin/env python3
"""Atomically publish one fully staged evidence-run directory."""

from __future__ import annotations

import argparse
import ctypes
import errno
import os
import pathlib
import sys
from typing import NoReturn


AT_FDCWD = -100
RENAME_EXCHANGE = 2


def fail(message: str) -> NoReturn:
    raise RuntimeError(message)


def exchange(left: pathlib.Path, right: pathlib.Path) -> None:
    libc = ctypes.CDLL(None, use_errno=True)
    renameat2 = getattr(libc, "renameat2", None)
    if renameat2 is None:
        fail("libc does not expose renameat2; refusing a non-atomic replacement")
    renameat2.argtypes = [
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_uint,
    ]
    renameat2.restype = ctypes.c_int
    rc = renameat2(
        AT_FDCWD,
        os.fsencode(left),
        AT_FDCWD,
        os.fsencode(right),
        RENAME_EXCHANGE,
    )
    if rc != 0:
        error = ctypes.get_errno()
        if error in {errno.ENOSYS, errno.EINVAL, errno.EOPNOTSUPP}:
            fail(
                "filesystem does not support atomic directory exchange; "
                "prior evidence was left untouched"
            )
        raise OSError(error, os.strerror(error), f"{left} <-> {right}")


def fsync_directory(path: pathlib.Path) -> None:
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("stage", type=pathlib.Path)
    parser.add_argument("target", type=pathlib.Path)
    args = parser.parse_args()

    stage = args.stage.absolute()
    target = args.target.absolute()
    parent = target.parent
    if not parent.is_dir():
        fail(f"publication parent does not exist: {parent}")
    if stage.parent != parent:
        fail("stage and target must be direct siblings on one filesystem")
    expected_prefix = f".{target.name}-stage."
    if not stage.name.startswith(expected_prefix):
        fail(f"stage name must start with {expected_prefix}")
    if stage.is_symlink() or not stage.is_dir():
        fail(f"stage is not a real directory: {stage}")
    if target.is_symlink():
        fail(f"publication target must not be a symlink: {target}")
    if target.exists() and not target.is_dir():
        fail(f"publication target is not a directory: {target}")

    if target.exists():
        exchange(stage, target)
        action = "exchanged"
    else:
        os.rename(stage, target)
        action = "created"
    fsync_directory(parent)
    print(f"atomic evidence publication {action}: {target}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError) as error:
        print(f"ERROR: atomic evidence publication refused: {error}", file=sys.stderr)
        raise SystemExit(1)
