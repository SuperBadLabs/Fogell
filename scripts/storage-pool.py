#!/usr/bin/env python3
"""Provision and explicitly recover Fogell's workspace storage pool."""

from __future__ import annotations

import argparse
import ctypes
import datetime as dt
import fcntl
import hashlib
import json
import os
import stat
import sys
from dataclasses import dataclass


STATE_FILE = ".fogell-pool-state"
MARKER_FILE = ".fogell-pool-id"
STATE_BYTES = 4096
ACTIVE_GUARD = 0xA5
KNOWN_EMPTY_ENTRIES = {"lost+found"}


class PoolError(Exception):
    pass


@dataclass(frozen=True)
class FileIdentity:
    device: int
    inode: int


def refuse(message: str) -> None:
    raise PoolError(message)


def require_linux() -> None:
    if sys.platform != "linux":
        refuse("storage pool operations require Linux")
    for name in ("O_NOFOLLOW", "O_CLOEXEC", "O_DIRECTORY"):
        if not hasattr(os, name):
            refuse(f"storage pool operations require os.{name}")


def clean_absolute_path(value: str, name: str) -> str:
    if not value or not os.path.isabs(value):
        refuse(f"{name} must be an absolute path")
    if value != "/":
        parts = value.split("/")
        if any(part in {"", ".", ".."} for part in parts[1:]):
            refuse(f"{name} must not contain empty, dot, or parent components")
    return value


def open_tree(path: str, name: str) -> int:
    """Open each absolute component as a directory without following links."""
    clean = clean_absolute_path(path, name)
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    current = os.open("/", flags)
    try:
        for part in clean.split("/")[1:]:
            try:
                next_fd = os.open(part, flags, dir_fd=current)
            except OSError as error:
                refuse(f"{name} cannot be opened without following links: {error.strerror}")
            os.close(current)
            current = next_fd
        return current
    except Exception:
        os.close(current)
        raise


def identity(fd: int) -> FileIdentity:
    value = os.fstat(fd)
    return FileIdentity(value.st_dev, value.st_ino)


def require_fixed_inode_filesystem(fd: int) -> None:
    if ctypes.sizeof(ctypes.c_long) != 8:
        refuse("storage pool operations require 64-bit Linux")
    libc = ctypes.CDLL(None, use_errno=True)
    libc.fstatfs.argtypes = [ctypes.c_int, ctypes.c_void_p]
    libc.fstatfs.restype = ctypes.c_int
    buffer = ctypes.create_string_buffer(256)
    if libc.fstatfs(fd, buffer) != 0:
        refuse("storage pool filesystem type is unavailable")
    kind = ctypes.c_long.from_buffer(buffer).value
    if kind not in {0xEF53, 0x01021994}:
        refuse("storage pool requires a fixed-inode ext filesystem or bounded tmpfs")


def open_pool(state_root: str) -> tuple[int, int]:
    root_fd = open_tree(state_root, "state root")
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    try:
        pool_fd = os.open("workspaces", flags, dir_fd=root_fd)
        try:
            require_fixed_inode_filesystem(pool_fd)
            return root_fd, pool_fd
        except Exception:
            os.close(pool_fd)
            raise
    except Exception as error:
        os.close(root_fd)
        if isinstance(error, OSError):
            refuse(f"workspace pool cannot be opened without following links: {error.strerror}")
        raise


def require_regular_0600(status: os.stat_result, name: str) -> None:
    if not stat.S_ISREG(status.st_mode):
        refuse(f"{name} must be a regular file")
    if stat.S_IMODE(status.st_mode) != 0o600:
        refuse(f"{name} must have mode 0600")


def open_regular(pool_fd: int, name: str, access: int, description: str) -> tuple[int, FileIdentity]:
    flags = access | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
    try:
        fd = os.open(name, flags, dir_fd=pool_fd)
    except OSError as error:
        refuse(f"{description} cannot be opened without following links: {error.strerror}")
    try:
        require_regular_0600(os.fstat(fd), description)
        return fd, identity(fd)
    except Exception:
        os.close(fd)
        raise


def read_exact(fd: int, size: int, description: str) -> bytes:
    os.lseek(fd, 0, os.SEEK_SET)
    data = bytearray()
    while len(data) < size:
        block = os.read(fd, size - len(data))
        if not block:
            refuse(f"{description} became shorter while being read")
        data.extend(block)
    if os.read(fd, 1):
        refuse(f"{description} changed size while being read")
    return bytes(data)


def write_exact(fd: int, data: bytes, description: str) -> None:
    os.lseek(fd, 0, os.SEEK_SET)
    offset = 0
    while offset < len(data):
        count = os.write(fd, data[offset:])
        if count <= 0:
            refuse(f"{description} could not be fully written")
        offset += count
    os.fsync(fd)


def valid_pool_id(pool_id: str) -> str:
    allowed = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")
    if not pool_id or len(pool_id) > 64 or any(character not in allowed for character in pool_id):
        refuse("--pool-id must be 1..64 ASCII alphanumeric, '.', '_' or '-' characters")
    return pool_id


def read_marker(pool_fd: int, pool_id: str) -> None:
    fd, _ = open_regular(pool_fd, MARKER_FILE, os.O_RDONLY, "storage pool marker")
    try:
        size = os.fstat(fd).st_size
        if size < 1 or size > 128:
            refuse("storage pool marker must contain 1 through 128 bytes")
        try:
            marker = read_exact(fd, size, "storage pool marker").decode("utf-8", "strict")
        except UnicodeDecodeError:
            refuse("storage pool marker must be valid UTF-8")
        if marker != pool_id:
            refuse("storage pool marker does not match --pool-id")
    finally:
        os.close(fd)


def open_valid_state(state_root: str, pool_id: str, access: int, pool_lock: int | None = None) -> tuple[int, int, int, FileIdentity]:
    root_fd, pool_fd = open_pool(state_root)
    try:
        if identity(root_fd).device == identity(pool_fd).device:
            refuse("workspaces must be a dedicated filesystem distinct from the state root")
        if pool_lock is not None:
            lock(pool_fd, pool_lock)
        read_marker(pool_fd, pool_id)
        fd, file_id = open_regular(pool_fd, STATE_FILE, access, "storage pool state")
        try:
            if os.fstat(fd).st_size != STATE_BYTES:
                refuse("storage pool state must be exactly 4096 bytes")
            return root_fd, pool_fd, fd, file_id
        except Exception:
            os.close(fd)
            raise
    except Exception:
        os.close(pool_fd)
        os.close(root_fd)
        raise


def verify_current_state(state_root: str, pool_id: str, expected: FileIdentity) -> None:
    root_fd, pool_fd, fd, observed = open_valid_state(state_root, pool_id, os.O_RDONLY)
    try:
        if observed != expected:
            refuse("storage pool state path was replaced during this operation")
    finally:
        os.close(fd)
        os.close(pool_fd)
        os.close(root_fd)


def lock(fd: int, operation: int) -> None:
    try:
        fcntl.flock(fd, operation | fcntl.LOCK_NB)
    except BlockingIOError:
        refuse("storage pool is locked by another controller or operator")
    except OSError as error:
        refuse(f"storage pool lock is unavailable: {error.strerror}")


def record_hash(record: bytes) -> str:
    return hashlib.sha256(record).hexdigest()


def expected_active_record(text: str) -> bytes:
    if not text or "\x00" in text:
        refuse("--expected-active-text must be nonempty and contain no NUL")
    encoded = text.encode("utf-8", "strict")
    if len(encoded) > STATE_BYTES - 2:
        refuse("--expected-active-text is too long")
    record = bytearray(STATE_BYTES)
    record[0] = 1
    record[1 : 1 + len(encoded)] = encoded
    record[-1] = ACTIVE_GUARD
    return bytes(record)


def match_expected(args: argparse.Namespace, record: bytes) -> None:
    if args.expected_record_sha256:
        expected = args.expected_record_sha256.lower()
        if len(expected) != 64 or any(character not in "0123456789abcdef" for character in expected):
            refuse("--expected-record-sha256 must be a lowercase SHA-256 digest")
        if record_hash(record) != expected:
            refuse("storage pool state does not match --expected-record-sha256")
    elif args.expected_active_text is not None:
        if record != expected_active_record(args.expected_active_text):
            refuse("storage pool state does not match --expected-active-text")
    else:
        refuse("recovery requires an expected active record")


def init(args: argparse.Namespace) -> int:
    require_linux()
    pool_id = valid_pool_id(args.pool_id)
    root_fd, pool_fd = open_pool(args.state_root)
    try:
        if identity(root_fd).device == identity(pool_fd).device:
            refuse("workspaces must be a dedicated mounted filesystem distinct from the state root")
        lock(pool_fd, fcntl.LOCK_EX)
        entries = set(os.listdir(pool_fd))
        if entries - KNOWN_EMPTY_ENTRIES:
            refuse("workspace pool is not empty; init never overwrites or adopts existing data")
        if "lost+found" in entries:
            info = os.stat("lost+found", dir_fd=pool_fd, follow_symlinks=False)
            if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode):
                refuse("workspace pool lost+found is not an ordinary directory")
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
        marker_fd = state_fd = None
        try:
            marker_fd = os.open(MARKER_FILE, flags, 0o600, dir_fd=pool_fd)
            os.fchmod(marker_fd, 0o600)
            write_exact(marker_fd, pool_id.encode("utf-8"), "storage pool marker")
            state_fd = os.open(STATE_FILE, flags, 0o600, dir_fd=pool_fd)
            os.fchmod(state_fd, 0o600)
            write_exact(state_fd, bytes(STATE_BYTES), "storage pool state")
            os.fsync(pool_fd)
        except FileExistsError:
            refuse("storage pool marker or state already exists; init never overwrites")
        finally:
            if state_fd is not None:
                os.close(state_fd)
            if marker_fd is not None:
                os.close(marker_fd)
    finally:
        os.close(pool_fd)
        os.close(root_fd)
    print(json.dumps({"pool_id": pool_id, "status": "initialized"}, sort_keys=True))
    return 0


def status(args: argparse.Namespace) -> int:
    require_linux()
    pool_id = valid_pool_id(args.pool_id)
    root_fd, pool_fd, fd, expected = open_valid_state(args.state_root, pool_id, os.O_RDONLY, fcntl.LOCK_SH)
    try:
        record = read_exact(fd, STATE_BYTES, "storage pool state")
        verify_current_state(args.state_root, pool_id, expected)
    finally:
        os.close(fd)
        os.close(pool_fd)
        os.close(root_fd)
    print(json.dumps({"pool_id": pool_id, "record_sha256": record_hash(record), "status": "idle" if not any(record) else "dirty"}, sort_keys=True))
    return 0


def receipt_directory(state_root: str, pool_device: int) -> int:
    """Open the fixed, operator-created receipt directory beneath state root."""
    root_fd = open_tree(state_root, "state root")
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    try:
        fd = os.open("storage-pool-receipts", flags, dir_fd=root_fd)
        if os.fstat(fd).st_dev == pool_device:
            refuse("storage-pool-receipts must be outside the workspace pool filesystem")
        return fd
    except OSError as error:
        refuse(f"storage-pool-receipts cannot be opened without following links: {error.strerror}")
    except Exception:
        os.close(fd)
        raise
    finally:
        os.close(root_fd)


def write_receipt(directory_fd: int, values: dict[str, str]) -> None:
    body = json.dumps(values, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"
    token = hashlib.sha256(body).hexdigest()[:16]
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    name = f"storage-pool-recovery-{stamp}-{token}.json"
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
    try:
        fd = os.open(name, flags, 0o600, dir_fd=directory_fd)
    except FileExistsError:
        refuse("recovery receipt name already exists; change the operator note")
    try:
        os.fchmod(fd, 0o600)
        write_exact(fd, body, "recovery receipt")
        os.fsync(directory_fd)
    finally:
        os.close(fd)


def recover(args: argparse.Namespace) -> int:
    require_linux()
    pool_id = valid_pool_id(args.pool_id)
    if not args.writers_extinct:
        refuse("recovery requires --writers-extinct after stopping every controller and execution domain")
    if not args.operator_note or len(args.operator_note) > 512:
        refuse("recovery requires --operator-note of at most 512 characters")
    root_fd, pool_fd, fd, expected = open_valid_state(args.state_root, pool_id, os.O_RDWR, fcntl.LOCK_EX)
    try:
        lock(fd, fcntl.LOCK_EX)
        record = read_exact(fd, STATE_BYTES, "storage pool state")
        if not any(record):
            refuse("storage pool is already idle; recovery refuses a no-op clear")
        match_expected(args, record)
        verify_current_state(args.state_root, pool_id, expected)
        receipt_fd = receipt_directory(args.state_root, os.fstat(pool_fd).st_dev)
        try:
            write_receipt(receipt_fd, {"action": "explicit_recovery", "old_record_sha256": record_hash(record), "operator_note": args.operator_note, "pool_id": pool_id, "writers_extinct_attested": "true"})
        finally:
            os.close(receipt_fd)
        verify_current_state(args.state_root, pool_id, expected)
        write_exact(fd, bytes(STATE_BYTES), "storage pool state")
        verify_current_state(args.state_root, pool_id, expected)
    finally:
        os.close(fd)
        os.close(pool_fd)
        os.close(root_fd)
    print(json.dumps({"pool_id": pool_id, "status": "recovered"}, sort_keys=True))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    setup = commands.add_parser("init", help="initialize an empty mounted workspace pool")
    setup.add_argument("--state-root", required=True)
    setup.add_argument("--pool-id", required=True)
    setup.set_defaults(function=init)
    inspect = commands.add_parser("status", help="read state without changing it")
    inspect.add_argument("--state-root", required=True)
    inspect.add_argument("--pool-id", required=True)
    inspect.set_defaults(function=status)
    clear = commands.add_parser("recover", help="clear one explicitly attested dirty record")
    clear.add_argument("--state-root", required=True)
    clear.add_argument("--pool-id", required=True)
    clear.add_argument("--writers-extinct", action="store_true")
    expected = clear.add_mutually_exclusive_group(required=True)
    expected.add_argument("--expected-active-text")
    expected.add_argument("--expected-record-sha256")
    clear.add_argument("--operator-note", required=True)
    clear.set_defaults(function=recover)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        return args.function(args)
    except PoolError as error:
        print(f"storage-pool: {error}", file=sys.stderr)
        return 2
    except OSError as error:
        print(f"storage-pool: filesystem operation failed: {error.strerror}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
