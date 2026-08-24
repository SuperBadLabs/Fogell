#!/usr/bin/env python3
"""FG-093: refuse a downstream release action unless provenance matches exactly.

The expected manifest is an external, trusted input.  Regenerating it from the
checkout being judged would make every mismatch agree with itself and prove
nothing.  This program therefore verifies only; it never writes a manifest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
from typing import NoReturn


EXPECTED_KEYS = {
    "schema_version",
    "commit",
    "tree",
    "artifact_sha256",
    "corpus_manifest_sha256",
}
SHA1_RE = re.compile(r"[0-9a-f]{40}\Z")
SHA256_RE = re.compile(r"[0-9a-f]{64}\Z")
MAX_MANIFEST_BYTES = 16 * 1024
GIT_SAFETY_CONFIG = (
    "-c", "core.fileMode=true",
    "-c", "core.ignoreCase=false",
    "-c", "core.trustctime=true",
    "-c", "core.checkStat=default",
    "-c", "core.fsmonitor=false",
    "-c", "core.untrackedCache=false",
    "-c", "core.ignoreStat=false",
)


class Refusal(Exception):
    """No trustworthy provenance verdict can be made."""


def controlled_git_environment() -> dict[str, str]:
    environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    environment["GIT_CONFIG_NOSYSTEM"] = "1"
    environment["GIT_CONFIG_GLOBAL"] = os.devnull
    return environment


def duplicate_rejecting_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise Refusal(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def stable_regular_file(path: Path, what: str) -> tuple[int, os.stat_result]:
    try:
        path_metadata = os.lstat(path)
    except OSError as error:
        raise Refusal(f"{what} cannot be inspected: {error.strerror}") from error
    if not stat.S_ISREG(path_metadata.st_mode):
        raise Refusal(f"{what} is not a regular non-symlink file")

    flags = os.O_RDONLY
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW

    try:
        descriptor = os.open(path, flags)
    except OSError as error:
        raise Refusal(f"{what} is not readable: {error.strerror}") from error

    try:
        descriptor_metadata = os.fstat(descriptor)
        if not stat.S_ISREG(descriptor_metadata.st_mode):
            raise Refusal(f"{what} is not a regular file")
        if (path_metadata.st_dev, path_metadata.st_ino) != (
            descriptor_metadata.st_dev,
            descriptor_metadata.st_ino,
        ):
            raise Refusal(f"{what} changed while it was opened")
        return descriptor, descriptor_metadata
    except Exception:
        os.close(descriptor)
        raise


def unchanged(before: os.stat_result, after: os.stat_result, what: str) -> None:
    before_identity = (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns)
    after_identity = (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns)
    if before_identity != after_identity:
        raise Refusal(f"{what} changed while it was read")


def regular_file_bytes(path: Path, what: str, maximum: int | None = None) -> bytes:
    descriptor, before = stable_regular_file(path, what)
    try:
        if maximum is not None and before.st_size > maximum:
            raise Refusal(f"{what} exceeds {maximum} bytes")
        with os.fdopen(descriptor, "rb", closefd=False) as handle:
            result = handle.read()
        unchanged(before, os.fstat(descriptor), what)
        return result
    finally:
        os.close(descriptor)


def sha256_file(path: Path, what: str) -> str:
    digest = hashlib.sha256()
    descriptor, before = stable_regular_file(path, what)
    try:
        with os.fdopen(descriptor, "rb", closefd=False) as handle:
            while block := handle.read(1024 * 1024):
                digest.update(block)
        unchanged(before, os.fstat(descriptor), what)
    finally:
        os.close(descriptor)

    return digest.hexdigest()


def require_external_path(path: Path, repository: Path, what: str) -> Path:
    try:
        resolved = path.resolve(strict=True)
    except OSError as error:
        raise Refusal(f"{what} cannot be resolved: {error}") from error

    try:
        resolved.relative_to(repository)
    except ValueError:
        pass
    else:
        raise Refusal(f"{what} must be external to the checkout being judged")
    return resolved


def load_manifest(path: Path, repository: Path) -> dict[str, str]:
    require_external_path(path, repository, "manifest")

    raw = regular_file_bytes(path.absolute(), "manifest", MAX_MANIFEST_BYTES)
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=duplicate_rejecting_object)
    except Refusal:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal(f"manifest is not strict UTF-8 JSON: {error}") from error

    if not isinstance(value, dict):
        raise Refusal("manifest root must be a JSON object")

    actual_keys = set(value)
    missing = sorted(EXPECTED_KEYS - actual_keys)
    unknown = sorted(actual_keys - EXPECTED_KEYS)
    if missing:
        raise Refusal("manifest missing key(s): " + ", ".join(missing))
    if unknown:
        raise Refusal("manifest has unknown key(s): " + ", ".join(unknown))
    if type(value["schema_version"]) is not int:
        raise Refusal("schema_version must be the JSON integer 1")
    if value["schema_version"] != 1:
        raise Refusal("schema_version must be exactly 1")
    string_keys = EXPECTED_KEYS - {"schema_version"}
    if any(not isinstance(value[key], str) for key in string_keys):
        raise Refusal("commit, tree, and digest values must be JSON strings")

    manifest = {key: str(value[key]) for key in string_keys}
    if not SHA1_RE.fullmatch(manifest["commit"]):
        raise Refusal("commit must be exactly 40 lowercase hexadecimal characters")
    if not SHA1_RE.fullmatch(manifest["tree"]):
        raise Refusal("tree must be exactly 40 lowercase hexadecimal characters")
    for key in ("artifact_sha256", "corpus_manifest_sha256"):
        if not SHA256_RE.fullmatch(manifest[key]):
            raise Refusal(f"{key} must be exactly 64 lowercase hexadecimal characters")
    return manifest


def git(repository: Path, *arguments: str) -> str:
    result = subprocess.run(
        ["git", *GIT_SAFETY_CONFIG, *arguments],
        cwd=repository,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=controlled_git_environment(),
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or f"exit {result.returncode}"
        raise Refusal(f"git {' '.join(arguments)} failed: {detail}")
    return result.stdout.strip()


def git_bytes(repository: Path, *arguments: str) -> bytes:
    result = subprocess.run(
        ["git", *GIT_SAFETY_CONFIG, *arguments],
        cwd=repository,
        capture_output=True,
        env=controlled_git_environment(),
    )
    if result.returncode != 0:
        raise Refusal(f"git {' '.join(arguments)} failed")
    return result.stdout


def require_filemode_tracking(repository: Path) -> None:
    result = subprocess.run(
        ["git", "config", "--local", "--bool", "core.fileMode"],
        cwd=repository,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=controlled_git_environment(),
    )
    if result.returncode != 0 or result.stdout.strip().lower() != "true":
        raise Refusal("checkout disables tracked executable-bit detection")


def require_no_assume_unchanged(repository: Path) -> None:
    records = git_bytes(repository, "ls-files", "-v", "-z").split(b"\0")
    if any(record[:1].islower() for record in records if record):
        raise Refusal("checkout index contains assume-unchanged entries")


def require_no_skip_worktree(repository: Path) -> None:
    records = git_bytes(repository, "ls-files", "-t", "-z").split(b"\0")
    if any(record.startswith(b"S ") for record in records if record):
        raise Refusal("checkout index contains skip-worktree entries")


def required_submodules(repository: Path) -> list[Path]:
    result: list[Path] = []
    records = git_bytes(repository, "ls-files", "--stage", "-z").split(b"\0")
    for record in records:
        if not record:
            continue
        header, separator, raw_path = record.partition(b"\t")
        fields = header.split()
        if not separator or not fields or fields[0] != b"160000":
            continue
        if len(fields) != 3 or fields[2] != b"0":
            raise Refusal("gitlink has an unexpected index stage")
        expected_commit = fields[1].decode("ascii", errors="strict")
        path = repository / os.fsdecode(raw_path)
        if path.is_symlink() or not path.is_dir() or not (path / ".git").exists():
            raise Refusal("gitlink is not an initialized Git worktree")
        resolved = path.resolve()
        try:
            resolved.relative_to(repository)
        except ValueError as error:
            raise Refusal("initialized submodule escapes its parent checkout") from error
        if Path(git(resolved, "rev-parse", "--show-toplevel")).resolve() != resolved:
            raise Refusal("initialized submodule has an unexpected Git top level")
        if git(resolved, "rev-parse", "--verify", "HEAD^{commit}") != expected_commit:
            raise Refusal("initialized submodule HEAD does not match its index gitlink")
        result.append(resolved)
    return result


def raw_git_blob_id(repository: Path, payload: bytes) -> str:
    result = subprocess.run(
        ["git", *GIT_SAFETY_CONFIG, "hash-object", "--stdin"],
        cwd=repository,
        input=payload,
        capture_output=True,
        env=controlled_git_environment(),
    )
    if result.returncode != 0:
        raise Refusal("git hash-object --stdin failed during raw tracked-file audit")
    try:
        return result.stdout.decode("ascii").strip()
    except UnicodeDecodeError as error:
        raise Refusal("git returned a non-ASCII object ID during raw tracked-file audit") from error


def require_raw_tracked_identity(repository: Path) -> None:
    records = git_bytes(repository, "ls-files", "--stage", "-z").split(b"\0")
    for record in records:
        if not record:
            continue
        header, separator, raw_path = record.partition(b"\t")
        fields = header.split()
        if not separator or len(fields) != 3:
            raise Refusal("tracked index entry has an unexpected shape")
        raw_mode, expected_blob, raw_stage = fields
        if raw_stage != b"0":
            raise Refusal("tracked index contains a non-stage-0 entry")
        if raw_mode == b"160000":
            continue

        path = repository / os.fsdecode(raw_path)
        if raw_mode in (b"100644", b"100755"):
            payload = regular_file_bytes(path, "tracked regular file")
            metadata = os.lstat(path)
            executable = bool(metadata.st_mode & 0o111)
            if executable != (raw_mode == b"100755"):
                raise Refusal("tracked regular-file executable mode does not match index")
            if raw_git_blob_id(repository, payload).encode("ascii") != expected_blob:
                raise Refusal("tracked regular-file raw bytes do not match index blob")
        elif raw_mode == b"120000":
            try:
                before = os.lstat(path)
                if not stat.S_ISLNK(before.st_mode):
                    raise Refusal("tracked symlink is not a symlink in the worktree")
                payload = os.readlink(os.fsencode(path))
                unchanged(before, os.lstat(path), "tracked symlink")
            except Refusal:
                raise
            except OSError as error:
                raise Refusal(f"tracked symlink cannot be inspected: {error.strerror}") from error
            if raw_git_blob_id(repository, payload).encode("ascii") != expected_blob:
                raise Refusal("tracked symlink target bytes do not match index blob")
        else:
            raise Refusal("tracked index contains an unsupported file mode")


def require_no_physical_untracked(repository: Path) -> None:
    # Deliberately omit --exclude-standard: release eligibility rejects ignored
    # build products and local/global exclude tricks as well as ordinary untracked
    # files. Git administration paths are not worktree candidates to ls-files.
    if git_bytes(repository, "ls-files", "--others", "-z"):
        raise Refusal("checkout contains physical untracked paths, including ignored paths")


def require_clean_checkout(repository: Path) -> None:
    queue = [repository]
    visited: set[Path] = set()
    while queue:
        worktree = queue.pop()
        if worktree in visited:
            raise Refusal("checkout contains a recursive submodule worktree")
        visited.add(worktree)
        require_filemode_tracking(worktree)
        require_no_assume_unchanged(worktree)
        require_no_skip_worktree(worktree)
        submodules = required_submodules(worktree)
        require_raw_tracked_identity(worktree)
        require_no_physical_untracked(worktree)
        dirty = git(
            worktree,
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
            "--ignore-submodules=none",
        )
        if dirty:
            raise Refusal("checkout has tracked or untracked changes")
        queue.extend(submodules)


def repository_snapshot(repository: Path, artifact: Path) -> dict[str, str]:
    top = Path(git(repository, "rev-parse", "--show-toplevel")).resolve()
    if top != repository:
        raise Refusal(f"checker belongs to {repository}, but git reports {top}")

    require_external_path(artifact, repository, "artifact")
    require_clean_checkout(repository)

    corpus_manifest = repository / "corpus" / "CORPUS-SHA256SUMS"
    return {
        "commit": git(repository, "rev-parse", "--verify", "HEAD^{commit}"),
        "tree": git(repository, "rev-parse", "--verify", "HEAD^{tree}"),
        "artifact_sha256": sha256_file(artifact, "artifact"),
        "corpus_manifest_sha256": sha256_file(corpus_manifest, "canonical corpus manifest"),
    }


def require_match(field: str, expected: str, actual: str) -> None:
    if expected != actual:
        raise Refusal(f"{field} mismatch")


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="verify Fogell release provenance, then replace this process with COMMAND"
    )
    parser.add_argument("--manifest", required=True, type=Path, help="trusted manifest outside the checkout")
    parser.add_argument("--artifact", required=True, type=Path, help="exact release artifact bytes to bind")
    parser.add_argument("command", nargs=argparse.REMAINDER, help="-- COMMAND [ARG ...]")
    parsed = parser.parse_args()
    if parsed.command and parsed.command[0] == "--":
        parsed.command = parsed.command[1:]
    if not parsed.command:
        parser.error("a downstream COMMAND is required after --")
    return parsed


def refuse(message: str) -> NoReturn:
    print(f"PROVENANCE REFUSED: {message}", file=sys.stderr)
    raise SystemExit(1)


def main() -> NoReturn:
    parsed = arguments()
    repository = Path(__file__).resolve().parent.parent

    try:
        manifest = load_manifest(parsed.manifest, repository)
        snapshot = repository_snapshot(repository, parsed.artifact)
        require_match("commit", manifest["commit"], snapshot["commit"])
        require_match("tree", manifest["tree"], snapshot["tree"])
        require_match("artifact_sha256", manifest["artifact_sha256"], snapshot["artifact_sha256"])
        require_match("corpus_manifest_sha256", manifest["corpus_manifest_sha256"], snapshot["corpus_manifest_sha256"])
    except Refusal as error:
        refuse(str(error))

    print(
        "PROVENANCE VERIFIED: "
        f"commit={snapshot['commit']} tree={snapshot['tree']} "
        f"artifact_sha256={snapshot['artifact_sha256']} "
        f"corpus_manifest_sha256={snapshot['corpus_manifest_sha256']}",
        flush=True,
    )
    environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    environment.update(
        {
            "FOGELL_VERIFIED_COMMIT": snapshot["commit"],
            "FOGELL_VERIFIED_TREE": snapshot["tree"],
            "FOGELL_VERIFIED_ARTIFACT": str(parsed.artifact.resolve()),
            "FOGELL_VERIFIED_ARTIFACT_SHA256": snapshot["artifact_sha256"],
            "FOGELL_VERIFIED_CORPUS_MANIFEST_SHA256": snapshot["corpus_manifest_sha256"],
        }
    )
    try:
        os.execvpe(parsed.command[0], parsed.command, environment)
    except OSError as error:
        refuse(f"verified downstream command could not start: {error}")


if __name__ == "__main__":
    main()
