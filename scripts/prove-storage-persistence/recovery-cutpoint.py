#!/usr/bin/env python3
"""Pause an explicit storage-pool recovery after its receipt-directory fsync.

Usage: recovery-cutpoint.py HELPER_PATH recover [the helper's recover arguments]

This is a VM-test witness, not a durability claim.  Once the target directory
fsync has succeeded it writes a checkpoint and deliberately never returns to
the helper, allowing the harness to kill the VM at this cutpoint.
"""

from __future__ import annotations

import hashlib
import json
import os
import runpy
import stat
import sys
import time
from datetime import UTC, datetime


RECEIPT_DIRECTORY = "/srv/fogell/state/storage-pool-receipts"
CHECKPOINT_DIRECTORY = "/run/fogell-proof"
CHECKPOINT_PATH = f"{CHECKPOINT_DIRECTORY}/checkpoint.json"


def fail(message: str) -> "None":
    raise SystemExit(f"recovery-cutpoint: {message}")


def file_sha256_no_follow(path: str) -> str:
    flags = os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW
    try:
        fd = os.open(path, flags)
    except OSError as error:
        fail(f"cannot open helper without following links: {error.strerror}")
    try:
        if not stat.S_ISREG(os.fstat(fd).st_mode):
            fail("helper must be a regular file")
        digest = hashlib.sha256()
        while True:
            chunk = os.read(fd, 1024 * 1024)
            if not chunk:
                return digest.hexdigest()
            digest.update(chunk)
    finally:
        os.close(fd)


def receipt_identity() -> tuple[int, int]:
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    try:
        fd = os.open(RECEIPT_DIRECTORY, flags)
    except OSError as error:
        fail(f"receipt directory must already exist: {error.strerror}")
    try:
        info = os.fstat(fd)
        if not stat.S_ISDIR(info.st_mode):
            fail("receipt path is not a directory")
        if os.listdir(fd):
            fail("receipt directory must be empty before the cutpoint recovery")
        return info.st_dev, info.st_ino
    finally:
        os.close(fd)


def write_all(fd: int, body: bytes) -> None:
    offset = 0
    while offset < len(body):
        written = os.write(fd, body[offset:])
        if written <= 0:
            raise OSError("checkpoint write made no progress")
        offset += written


def write_checkpoint(device: int, inode: int, helper_sha256: str) -> None:
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW
    values = {
        "fd_identity": {"device": device, "inode": inode},
        "helper_sha256": helper_sha256,
        "successful_fsync": True,
        "time": datetime.now(UTC).isoformat(),
    }
    body = json.dumps(values, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"
    try:
        fd = os.open(CHECKPOINT_PATH, flags, 0o600)
    except OSError as error:
        fail(f"cannot create checkpoint at {CHECKPOINT_PATH}: {error.strerror}")
    try:
        write_all(fd, body)
        # This does not establish that a guest crash survives physical media loss.
        os.fsync(fd)
    finally:
        os.close(fd)


def main(argv: list[str]) -> int:
    if len(argv) < 3 or argv[2] != "recover":
        fail("usage: recovery-cutpoint.py HELPER_PATH recover [helper recover arguments]")
    helper_path = argv[1]
    if not os.path.isabs(helper_path):
        fail("helper path must be absolute")
    if os.path.exists(CHECKPOINT_PATH):
        fail(f"checkpoint already exists: {CHECKPOINT_PATH}")

    expected_helper_sha256 = file_sha256_no_follow(helper_path)
    target_device, target_inode = receipt_identity()
    original_fsync = os.fsync

    def cutpoint_fsync(fd: int) -> None:
        try:
            info = os.fstat(fd)
        except OSError:
            # Preserve the helper's fsync behavior if this descriptor is invalid.
            return original_fsync(fd)
        if (
            not stat.S_ISDIR(info.st_mode)
            or info.st_dev != target_device
            or info.st_ino != target_inode
        ):
            return original_fsync(fd)

        original_fsync(fd)
        if file_sha256_no_follow(helper_path) != expected_helper_sha256:
            fail("helper changed during cutpoint invocation")
        write_checkpoint(target_device, target_inode, expected_helper_sha256)
        while True:
            time.sleep(60)

    # runpy executes storage-pool.py in this interpreter.  Its import of `os`
    # resolves this same module object, so this assignment intercepts helper fsyncs.
    os.fsync = cutpoint_fsync
    sys.argv = [helper_path, *argv[2:]]
    runpy.run_path(helper_path, run_name="__main__")
    fail("helper exited without fsyncing the pre-statted receipt directory")


if __name__ == "__main__":
    main(sys.argv)
