#!/usr/bin/env python3
"""Create and check a single-host PostgreSQL + Fogell state recovery point.

Writers must already be stopped. This tool records that operator assertion; it
does not stop or fence Fogell services and it does not schedule retention.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tarfile
from datetime import datetime, timezone


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def inventory(root: Path) -> list[dict[str, str | int]]:
    """Inventory paths without following links; reject sockets/devices/FIFOs."""
    result = []
    for current, dirs, files in os.walk(root, topdown=True, followlinks=False):
        base = Path(current)
        dirs.sort()
        files.sort()
        for name in list(dirs) + files:
            path = base / name
            rel = path.relative_to(root).as_posix()
            info = path.lstat()
            mode = info.st_mode
            entry: dict[str, str | int] = {"path": rel, "mode": mode & 0o7777}
            if path.is_symlink():
                entry.update(kind="symlink", target=os.readlink(path))
                if name in dirs:
                    dirs.remove(name)
            elif path.is_dir():
                entry["kind"] = "directory"
            elif path.is_file():
                entry.update(kind="file", size=info.st_size, sha256=digest(path))
            else:
                raise ValueError(f"unsupported special file in state root: {rel}")
            result.append(entry)
    return sorted(result, key=lambda entry: str(entry["path"]))


def state_inventory_from_tar(archive: Path) -> list[dict[str, str | int]]:
    result = []
    with tarfile.open(archive, "r:") as tar:
        for member in tar.getmembers():
            rel = PurePosixPath(member.name)
            if rel.is_absolute() or ".." in rel.parts:
                raise ValueError("state archive contains an unsafe path")
            if member.name in ("", "."):
                continue
            entry: dict[str, str | int] = {"path": str(rel), "mode": member.mode & 0o7777}
            if member.isdir():
                entry["kind"] = "directory"
            elif member.issym():
                entry.update(kind="symlink", target=member.linkname)
            elif member.isfile():
                source = tar.extractfile(member)
                if source is None:
                    raise ValueError("state archive has an unreadable regular file")
                h = hashlib.sha256()
                size = 0
                while block := source.read(1024 * 1024):
                    h.update(block)
                    size += len(block)
                entry.update(kind="file", size=size, sha256=h.hexdigest())
            else:
                raise ValueError(f"unsupported archive entry: {member.name}")
            result.append(entry)
    return sorted(result, key=lambda e: str(e["path"]))


def canonical_hash(value: object) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def libpq_environment(npgsql: str) -> dict[str, str]:
    """Translate the documented semicolon-delimited Npgsql connection string.

    Credentials are passed to clients through environment variables, never argv.
    Quoted values may contain semicolons; doubled quote characters are decoded.
    """
    pieces: list[str] = []
    value: list[str] = []
    quote: str | None = None
    i = 0
    while i < len(npgsql):
        char = npgsql[i]
        if quote and char == quote:
            if i + 1 < len(npgsql) and npgsql[i + 1] == quote:
                value.append(quote)
                i += 1
            else:
                quote = None
        elif not quote and char in ("'", '"'):
            quote = char
        elif not quote and char == ";":
            pieces.append("".join(value).strip())
            value = []
        else:
            value.append(char)
        i += 1
    if quote:
        raise ValueError("database connection string has an unterminated quoted value")
    pieces.append("".join(value).strip())

    parsed: dict[str, str] = {}
    for piece in pieces:
        if not piece:
            continue
        if "=" not in piece:
            raise ValueError("database connection string has a field without '='")
        key, field_value = piece.split("=", 1)
        key = "".join(key.lower().split())
        if key in parsed:
            raise ValueError(f"duplicate database connection setting: {key}")
        parsed[key] = field_value.strip()
    aliases = {
        "host": "host", "server": "host", "port": "port",
        "username": "user", "userid": "user", "user": "user",
        "database": "database", "initialcatalog": "database",
        "password": "password", "sslmode": "sslmode",
        "applicationname": "application_name", "timeout": "connect_timeout",
    }
    npgsql_only = {"commandtimeout", "pooling", "minimum pool size", "maximumpoolsize",
                   "minimumpoolsize", "keepalive", "includerrordetail",
                   "trustservercertificate", "searchpath"}
    result: dict[str, str] = {}
    for key, field_value in parsed.items():
        env_name = aliases.get(key)
        if env_name is None:
            if key in npgsql_only:
                continue
            raise ValueError(f"unsupported database connection setting: {key}")
        if not field_value:
            continue
        if env_name == "sslmode":
            ssl_modes = {"disable": "disable", "allow": "allow", "prefer": "prefer",
                         "require": "require", "verifyca": "verify-ca", "verifyfull": "verify-full"}
            normalized = field_value.lower().replace(" ", "")
            if normalized not in ssl_modes:
                raise ValueError("unsupported SSL Mode in database connection string")
            field_value = ssl_modes[normalized]
        if env_name == "connect_timeout":
            if not field_value.isdecimal() or not 1 <= int(field_value) <= 300:
                raise ValueError("database connection Timeout must be from 1 through 300 seconds")
        if env_name == "port" and (not field_value.isdecimal() or not 1 <= int(field_value) <= 65535):
            raise ValueError("database Port must be from 1 through 65535")
        result["PG" + env_name.upper()] = field_value
    if not all(key in result for key in ("PGHOST", "PGPORT", "PGUSER", "PGDATABASE")):
        raise ValueError("database connection string must include Host, Port, Username, and Database")
    return result


def safe_relative_state(state: Path, destination: Path) -> None:
    state_real = state.resolve(strict=True)
    destination_real = destination.resolve(strict=False)
    if not state_real.is_dir():
        raise ValueError("state root must be a directory")
    if state_real == destination_real or state_real in destination_real.parents or destination_real in state_real.parents:
        raise ValueError("backup destination must be outside and separate from the state root")


def check(backup: Path) -> dict[str, object]:
    manifest_path = backup / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (manifest.get("format") != 1 or manifest.get("writers_quiesced") is not True
            or manifest.get("status") != "complete" or manifest.get("paired") is not True):
        raise ValueError("unsupported or unquiesced recovery point")
    pg_restore = shutil.which("pg_restore")
    if not pg_restore:
        raise ValueError("pg_restore must be installed to check a recovery point")
    for key in ("database.custom", "state.tar"):
        path = backup / key
        if not path.is_file() or digest(path) != manifest["files"][key]["sha256"]:
            raise ValueError(f"missing or changed recovery file: {key}")
    with (backup / "database.custom").open("rb") as archive:
        listing = subprocess.run([pg_restore, "--list"], stdin=archive,
                                 check=True, capture_output=True)
    if not listing.stdout.strip():
        raise ValueError("database backup is not a readable PostgreSQL custom archive")
    actual_inventory = state_inventory_from_tar(backup / "state.tar")
    if canonical_hash(actual_inventory) != manifest["state_inventory_sha256"]:
        raise ValueError("state archive inventory does not match its manifest")
    if manifest.get("state_entries") != len(actual_inventory):
        raise ValueError("state archive entry count does not match its manifest")
    with tarfile.open(backup / "state.tar", "r:") as archive:
        archive.getmembers()  # validates tar framing and checksums
    return {"created_at": manifest["created_at"], "release_id": manifest["release_id"],
            "schema_version": manifest["schema_version"], "state_entries": len(actual_inventory),
            "database_sha256": manifest["files"]["database.custom"]["sha256"],
            "state_sha256": manifest["files"]["state.tar"]["sha256"]}


def create(args: argparse.Namespace) -> dict[str, object]:
    state = Path(args.state_root)
    output = Path(args.output)
    safe_relative_state(state, output)
    if not output.is_absolute():
        raise ValueError("output path must be absolute")
    if output.exists():
        raise ValueError("output must be a new, nonexistent directory")
    if not args.writers_quiesced:
        raise ValueError("--writers-quiesced is required as an operator assertion")
    if not args.release_id or any(ord(c) < 32 for c in args.release_id):
        raise ValueError("a nonempty release identity is required")
    connection = os.environ.get("FOGELL_MAINTENANCE_DATABASE_URL")
    if not connection:
        raise ValueError("FOGELL_MAINTENANCE_DATABASE_URL is required")
    connection_env = libpq_environment(connection)
    pg_dump = shutil.which("pg_dump")
    psql = shutil.which("psql")
    if not pg_dump or not psql:
        raise ValueError("pg_dump and psql must be installed")
    env = os.environ.copy()
    # Npgsql's semicolon connection string is translated to libpq environment;
    # credential-bearing fields are never placed in process arguments.
    env.update(connection_env)
    env.pop("FOGELL_MAINTENANCE_DATABASE_URL", None)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.mkdir(mode=0o700)
    try:
        state_entries = inventory(state.resolve(strict=True))
        with (output / "database.custom").open("wb") as stream:
            subprocess.run([pg_dump, "--format=custom", "--no-owner", "--no-privileges"],
                           env=env, stdout=stream, check=True)
        with tarfile.open(output / "state.tar", "w:") as archive:
            archive.add(state.resolve(strict=True), arcname=".", recursive=True)
        # Rewalk both sources after capture to refuse concurrent changes.
        if state_entries != inventory(state.resolve(strict=True)):
            raise ValueError("state root changed during backup; pair discarded")
        schema = subprocess.run([psql, "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1",
                                 "-c", "SELECT COALESCE(max(version),'unknown') FROM schema_migrations"],
                                env=env, check=True, capture_output=True, text=True).stdout.strip()
        manifest = {
            "format": 1,
            "created_at": datetime.now(timezone.utc).isoformat(),
            "status": "complete",
            "paired": True,
            "writers_quiesced": True,
            "release_id": args.release_id,
            "schema_version": schema or "unknown",
            "state_inventory_sha256": canonical_hash(state_entries),
            "state_entries": len(state_entries),
            "files": {name: {"sha256": digest(output / name), "size": (output / name).stat().st_size}
                      for name in ("database.custom", "state.tar")},
        }
        manifest_bytes = json.dumps(manifest, sort_keys=True, indent=2) + "\n"
        (output / "manifest.json").write_text(manifest_bytes, encoding="utf-8")
        # Recheck the durable representation before reporting success.
        return check(output)
    except Exception:
        shutil.rmtree(output, ignore_errors=True)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    make = sub.add_parser("create", help="create a new paired recovery point")
    make.add_argument("--state-root", required=True)
    make.add_argument("--output", required=True)
    make.add_argument("--release-id", required=True)
    make.add_argument("--writers-quiesced", action="store_true")
    make.set_defaults(func=create)
    verify = sub.add_parser("check", help="verify hashes and state inventory")
    verify.add_argument("backup")
    verify.set_defaults(func=lambda a: check(Path(a.backup)))
    args = parser.parse_args()
    try:
        print(json.dumps(args.func(args), sort_keys=True))
        return 0
    except (OSError, ValueError, KeyError, json.JSONDecodeError, subprocess.CalledProcessError, tarfile.TarError) as error:
        print(f"paired-backup REFUSED: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
