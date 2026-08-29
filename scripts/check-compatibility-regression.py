#!/usr/bin/env python3
"""FG-094: fail a corpus-host gate when proven compatibility regresses."""

from __future__ import annotations

import hashlib
import json
import os
from dataclasses import dataclass
from pathlib import Path
import re
import stat
import subprocess
import sys
from typing import NoReturn


BASELINE_KEYS = {
    "schema_version",
    "baseline_commit",
    "baseline_tree",
    "baseline_ledger_sha256",
    "corpus_manifest_sha256",
    "corpus_file_count",
    "jenkins_core",
    "jenkins_oracle_sha256",
    "jenkins_compiled_count",
    "jenkins_reached_agent_count",
}
SHA1_RE = re.compile(r"[0-9a-f]{40}\Z")
SHA256_RE = re.compile(r"[0-9a-f]{64}\Z")
FILENAME_RE = re.compile(r"[^/\t\r\n]+\.Jenkinsfile\Z")
MAX_BASELINE_BYTES = 16 * 1024
LEDGER_HEADER = ("file", "tier", "code", "evidence")
ORACLE_HEADER = (
    "file", "repo", "stars", "bytes", "jenkins_lint", "jenkins_job_raw",
    "jenkins_job", "jenkins_elapsed_ms", "jenkins_compiled",
    "jenkins_reached_agent", "chengis", "chengis_stages", "chengis_steps",
    "ranvil", "ranvil_actions", "ranvil_errors",
)
GIT_CONFIG = (
    "-c", "core.fileMode=true",
    "-c", "core.ignoreCase=false",
    "-c", "core.trustctime=true",
    "-c", "core.checkStat=default",
    "-c", "core.fsmonitor=false",
    "-c", "core.untrackedCache=false",
    "-c", "core.ignoreStat=false",
)


class Refusal(Exception):
    """The checker cannot establish a trustworthy non-regression verdict."""


@dataclass(frozen=True)
class Ledger:
    filenames: tuple[str, ...]
    accepted: frozenset[str]
    tier1: frozenset[str]


@dataclass(frozen=True)
class Oracle:
    filenames: tuple[str, ...]
    compiled: frozenset[str]
    reached: frozenset[str]
    reached_by_file: dict[str, bool]


def controlled_environment() -> dict[str, str]:
    environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    environment.update(
        {
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_NO_REPLACE_OBJECTS": "1",
        }
    )
    return environment


def refuse(message: str) -> NoReturn:
    print(f"FG-094 REGRESSION GATE REFUSED: {message}", file=sys.stderr)
    raise SystemExit(1)


def stable_regular_bytes(path: Path, what: str, maximum: int | None = None) -> bytes:
    try:
        before = os.lstat(path)
    except OSError as error:
        raise Refusal(f"{what} cannot be inspected: {error.strerror}") from error
    if not stat.S_ISREG(before.st_mode):
        raise Refusal(f"{what} is not a regular non-symlink file")
    if maximum is not None and before.st_size > maximum:
        raise Refusal(f"{what} exceeds {maximum} bytes")

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
        opened = os.fstat(descriptor)
        if not stat.S_ISREG(opened.st_mode) or (before.st_dev, before.st_ino) != (opened.st_dev, opened.st_ino):
            raise Refusal(f"{what} changed while it was opened")
        with os.fdopen(descriptor, "rb", closefd=False) as handle:
            payload = handle.read()
        after = os.fstat(descriptor)
        identity_before = (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns, opened.st_ctime_ns)
        identity_after = (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns)
        if identity_before != identity_after:
            raise Refusal(f"{what} changed while it was read")
        return payload
    finally:
        os.close(descriptor)


def require_external(path: Path, repository: Path, what: str) -> Path:
    try:
        resolved = path.resolve(strict=True)
    except OSError as error:
        raise Refusal(f"{what} cannot be resolved: {error}") from error
    try:
        resolved.relative_to(repository)
    except ValueError:
        return resolved
    raise Refusal(f"{what} must be external to the checkout")


def sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def duplicate_rejecting_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise Refusal("baseline contains a duplicate JSON key")
        result[key] = value
    return result


def load_baseline(path: Path, repository: Path) -> dict[str, object]:
    require_external(path, repository, "baseline")
    raw = stable_regular_bytes(path.absolute(), "baseline", MAX_BASELINE_BYTES)
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=duplicate_rejecting_object)
    except Refusal:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("baseline is not strict UTF-8 JSON") from error
    if not isinstance(value, dict):
        raise Refusal("baseline root must be a JSON object")
    actual_keys = set(value)
    if actual_keys != BASELINE_KEYS:
        raise Refusal("baseline keys do not exactly match schema version 1")
    if type(value["schema_version"]) is not int or value["schema_version"] != 1:
        raise Refusal("baseline schema_version must be the JSON integer 1")
    for key in ("corpus_file_count", "jenkins_compiled_count", "jenkins_reached_agent_count"):
        if type(value[key]) is not int or value[key] < 0:
            raise Refusal(f"baseline {key} must be a non-negative JSON integer")
    for key in ("baseline_commit", "baseline_tree", "baseline_ledger_sha256", "corpus_manifest_sha256", "jenkins_core", "jenkins_oracle_sha256"):
        if not isinstance(value[key], str):
            raise Refusal(f"baseline {key} must be a JSON string")
    if not SHA1_RE.fullmatch(value["baseline_commit"]) or not SHA1_RE.fullmatch(value["baseline_tree"]):
        raise Refusal("baseline commit and tree must be full lowercase SHA-1 object IDs")
    for key in ("baseline_ledger_sha256", "corpus_manifest_sha256", "jenkins_oracle_sha256"):
        if not SHA256_RE.fullmatch(value[key]):
            raise Refusal(f"baseline {key} must be a lowercase SHA-256 digest")
    if value["jenkins_core"] != "2.568.1":
        raise Refusal("baseline jenkins_core must be exactly 2.568.1")
    return value


def process_diagnostic(*outputs: bytes, limit: int = 2_000) -> str:
    combined = b"\n".join(output.strip() for output in outputs if output.strip())
    clipped = combined[:limit]
    detail = clipped.decode("utf-8", errors="replace")
    if len(combined) > limit:
        detail += "\n[diagnostic truncated]"
    return detail


def with_diagnostic(message: str, result: subprocess.CompletedProcess[bytes]) -> str:
    detail = process_diagnostic(result.stderr, result.stdout)
    suffix = f": {detail}" if detail else ""
    return f"{message} (exit {result.returncode}){suffix}"


def git(repository: Path, *arguments: str) -> bytes:
    result = subprocess.run(
        ["git", *GIT_CONFIG, *arguments],
        cwd=repository,
        capture_output=True,
        env=controlled_environment(),
    )
    if result.returncode == 0:
        return result.stdout
    raise Refusal(with_diagnostic(f"Git command failed: {' '.join(arguments)}", result))


def git_is_ancestor(repository: Path, ancestor: str) -> bool:
    result = subprocess.run(
        ["git", *GIT_CONFIG, "merge-base", "--is-ancestor", ancestor, "HEAD"],
        cwd=repository,
        capture_output=True,
        env=controlled_environment(),
    )
    if result.returncode not in {0, 1}:
        raise Refusal(with_diagnostic("Git merge-base ancestry check failed", result))
    return result.returncode == 0


def run_scorecard_check(repository: Path, corpus: Path) -> None:
    environment = controlled_environment()
    environment["FOGELL_CORPUS"] = str(corpus)
    build_check = repository / "scripts" / "build-audits.sh"
    try:
        freshness = subprocess.run(
            [str(build_check), "--check"],
            cwd=repository,
            capture_output=True,
            env=environment,
        )
    except OSError:
        raise Refusal(
            "audit-binary freshness check could not start; run scripts/build-audits.sh first"
        ) from None
    if freshness.returncode != 0:
        raise Refusal(
            with_diagnostic(
                "audit binaries are missing or stale; run scripts/build-audits.sh first",
                freshness,
            )
        )
    try:
        result = subprocess.run(
            [str(repository / "scripts" / "bin" / "generate-scorecard"), "--check"],
            cwd=repository,
            capture_output=True,
            env=environment,
        )
    except OSError:
        raise Refusal(
            "generate-scorecard --check could not start; run scripts/build-audits.sh first"
        ) from None
    if result.returncode != 0:
        raise Refusal(
            with_diagnostic(
                "generate-scorecard --check failed before regression comparison", result
            )
        )


def require_filename(filename: str) -> None:
    if not FILENAME_RE.fullmatch(filename):
        raise Refusal("compatibility input contains a malformed Jenkinsfile name")


def parse_ledger(raw: bytes, what: str) -> Ledger:
    if b"\r" in raw or not raw.endswith(b"\n"):
        raise Refusal(f"{what} must use canonical LF-terminated TSV")
    try:
        lines = raw.decode("utf-8").splitlines()
    except UnicodeDecodeError as error:
        raise Refusal(f"{what} is not UTF-8") from error
    headers = [index for index, line in enumerate(lines) if tuple(line.split("\t")) == LEDGER_HEADER]
    if len(headers) != 1 or any(not line.startswith("#") for line in lines[: headers[0]]):
        raise Refusal(f"{what} has an unexpected ledger header")
    rows = lines[headers[0] + 1 :]
    if not rows:
        raise Refusal(f"{what} contains no ledger rows")
    filenames: list[str] = []
    accepted: set[str] = set()
    tier1: set[str] = set()
    for row in rows:
        fields = row.split("\t")
        if len(fields) != 4:
            raise Refusal(f"{what} row does not have exactly four columns")
        filename, tier, code, evidence = fields[:4]
        require_filename(filename)
        if tier not in {"1", "admitted", "3"} or not code or not evidence:
            raise Refusal(f"{what} row violates the ledger schema")
        filenames.append(filename)
        if tier in {"1", "admitted"}:
            accepted.add(filename)
        if tier == "1":
            tier1.add(filename)
    if filenames != sorted(filenames) or len(filenames) != len(set(filenames)):
        raise Refusal(f"{what} filenames must be sorted and unique")
    return Ledger(tuple(filenames), frozenset(accepted), frozenset(tier1))


def parse_corpus_manifest(raw: bytes) -> tuple[str, ...]:
    if b"\r" in raw or not raw.endswith(b"\n"):
        raise Refusal("corpus manifest must use canonical LF-terminated rows")
    try:
        lines = raw.decode("utf-8").splitlines()
    except UnicodeDecodeError as error:
        raise Refusal("corpus manifest is not UTF-8") from error
    filenames: list[str] = []
    for line in lines:
        match = re.fullmatch(r"([0-9a-f]{64})  ([^\t\r\n]+)", line)
        if match is None:
            raise Refusal("corpus manifest row violates the exact schema")
        filename = match.group(2)
        require_filename(filename)
        filenames.append(filename)
    if len(filenames) != len(set(filenames)):
        raise Refusal("corpus manifest filenames must be unique")
    # CORPUS-SHA256SUMS has a separately pinned byte digest but its historical
    # row order is not lexical. Canonicalize only the set comparison here.
    return tuple(sorted(filenames))


def parse_oracle(raw: bytes) -> Oracle:
    remainder = raw.replace(b"\r\n", b"")
    if not raw.endswith(b"\r\n") or b"\r" in remainder or b"\n" in remainder:
        raise Refusal("Jenkins oracle must use exact CRLF line endings")
    try:
        lines = raw[:-2].decode("utf-8").split("\r\n")
    except UnicodeDecodeError as error:
        raise Refusal("Jenkins oracle is not UTF-8") from error
    if not lines or tuple(lines[0].split("\t")) != ORACLE_HEADER:
        raise Refusal("Jenkins oracle header does not match the exact 16-column schema")
    filenames: list[str] = []
    compiled: set[str] = set()
    reached: set[str] = set()
    reached_by_file: dict[str, bool] = {}
    for row in lines[1:]:
        fields = row.split("\t")
        if len(fields) != 16:
            raise Refusal("Jenkins oracle row does not have exactly 16 columns")
        filename = fields[0]
        require_filename(filename)
        if fields[8] not in {"True", "False"} or fields[9] not in {"True", "False"}:
            raise Refusal("Jenkins oracle boolean columns are not exact True/False values")
        is_compiled = fields[8] == "True"
        is_reached = fields[9] == "True"
        if is_reached and not is_compiled:
            raise Refusal("Jenkins oracle reached_agent=True without compiled=True")
        filenames.append(filename)
        reached_by_file[filename] = is_reached
        if is_compiled:
            compiled.add(filename)
        if is_reached:
            reached.add(filename)
    if len(filenames) != len(set(filenames)):
        raise Refusal("Jenkins oracle filenames must be unique")
    return Oracle(tuple(sorted(filenames)), frozenset(compiled), frozenset(reached), reached_by_file)


def require_equal_file_sets(
    corpus: tuple[str, ...], baseline: Ledger, current: Ledger, oracle: Oracle, expected_count: int
) -> None:
    sets = (set(corpus), set(baseline.filenames), set(current.filenames), set(oracle.filenames))
    if any(len(value) != expected_count for value in sets) or not all(value == sets[0] for value in sets[1:]):
        raise Refusal("corpus, baseline ledger, current ledger, and oracle filename sets differ")


def require_regression_sets(baseline: Ledger, current: Ledger, oracle: Oracle) -> tuple[set[str], set[str]]:
    if not baseline.tier1 <= current.tier1:
        raise Refusal("tier-1 filename set decreased")
    oracle_ready_baseline = {name for name in baseline.accepted if oracle.reached_by_file[name]}
    if not oracle_ready_baseline <= current.accepted:
        raise Refusal("accepted filename set decreased for a reached_agent=True Jenkinsfile")
    return set(baseline.accepted - current.accepted), set(current.accepted - baseline.accepted)


def main() -> None:
    repository = Path(__file__).resolve().parent.parent
    corpus = Path(os.environ.get("FOGELL_CORPUS", "/sn8100/work/exchange/crucible-gate/corpus"))
    baseline_setting = os.environ.get("FOGELL_REGRESSION_BASELINE")
    oracle_setting = os.environ.get("FOGELL_JENKINS_ORACLE")

    if not corpus.is_dir():
        print("FG-094 regression gate NOT RUN: corpus is not mounted; compatibility regression remains UNVERIFIED on this host")
        return
    if bool(baseline_setting) != bool(oracle_setting):
        refuse("FOGELL_REGRESSION_BASELINE and FOGELL_JENKINS_ORACLE must be supplied together")
    if not baseline_setting:
        refuse("corpus is present but the required external baseline and Jenkins oracle are not configured")

    try:
        run_scorecard_check(repository, corpus)
        baseline = load_baseline(Path(baseline_setting), repository)
        oracle_path = Path(oracle_setting)
        require_external(oracle_path, repository, "Jenkins oracle")
        oracle_raw = stable_regular_bytes(oracle_path.absolute(), "Jenkins oracle")
        if sha256(oracle_raw) != baseline["jenkins_oracle_sha256"]:
            raise Refusal("Jenkins oracle SHA-256 does not match baseline")

        commit = str(baseline["baseline_commit"])
        if not git_is_ancestor(repository, commit):
            raise Refusal("baseline commit is not an ancestor of current HEAD")
        tree = git(repository, "rev-parse", "--verify", f"{commit}^{{tree}}").decode("ascii").strip()
        if tree != baseline["baseline_tree"]:
            raise Refusal("baseline tree does not match baseline commit")
        baseline_ledger_raw = git(repository, "show", f"{commit}:docs/COMPATIBILITY-LEDGER.tsv")
        if sha256(baseline_ledger_raw) != baseline["baseline_ledger_sha256"]:
            raise Refusal("historical baseline ledger SHA-256 does not match baseline")

        corpus_manifest_raw = stable_regular_bytes(repository / "corpus" / "CORPUS-SHA256SUMS", "corpus manifest")
        if sha256(corpus_manifest_raw) != baseline["corpus_manifest_sha256"]:
            raise Refusal("corpus manifest SHA-256 does not match baseline")
        current_ledger_raw = stable_regular_bytes(repository / "docs" / "COMPATIBILITY-LEDGER.tsv", "current ledger")

        baseline_ledger = parse_ledger(baseline_ledger_raw, "historical baseline ledger")
        current_ledger = parse_ledger(current_ledger_raw, "current ledger")
        corpus_files = parse_corpus_manifest(corpus_manifest_raw)
        oracle = parse_oracle(oracle_raw)

        expected_count = int(baseline["corpus_file_count"])
        require_equal_file_sets(corpus_files, baseline_ledger, current_ledger, oracle, expected_count)
        if len(oracle.compiled) != baseline["jenkins_compiled_count"]:
            raise Refusal("Jenkins oracle compiled count does not match baseline")
        if len(oracle.reached) != baseline["jenkins_reached_agent_count"]:
            raise Refusal("Jenkins oracle reached_agent count does not match baseline")

        losses, gains = require_regression_sets(baseline_ledger, current_ledger, oracle)
    except Refusal as error:
        refuse(str(error))

    print(
        "FG-094 COMPATIBILITY REGRESSION GATE PASSED: "
        f"files={len(corpus_files)} baseline_accepted={len(baseline_ledger.accepted)} "
        f"current_accepted={len(current_ledger.accepted)} baseline_tier1={len(baseline_ledger.tier1)} "
        f"current_tier1={len(current_ledger.tier1)} allowed_oracle_not_ready_losses={len(losses)} "
        f"gains={len(gains)}"
    )


if __name__ == "__main__":
    main()
