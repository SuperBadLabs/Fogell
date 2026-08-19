#!/usr/bin/env python3
"""Render auditable FG-177 probe templates with the configured SCM URL."""

from __future__ import annotations

import hashlib
import os
import pathlib
import shutil
import stat
import subprocess
import sys
import tempfile


TOKEN = "@@FOGELL_SCM_URL@@"
CASES = (
    "fg177-probe-unknown-policy.Jenkinsfile",
    "fg177-probe-requiredness.Jenkinsfile",
    "fg177-probe-return-semantics.Jenkinsfile",
    "fg177-probe-checkout-scm.Jenkinsfile",
    "fg177-plan-git-history.Jenkinsfile",
)
TOKEN_COUNTS = {
    "fg177-probe-unknown-policy.Jenkinsfile": 1,
    "fg177-probe-return-semantics.Jenkinsfile": 1,
    "fg177-plan-git-history.Jenkinsfile": 2,
}


def groovy_single_quoted(value: str) -> str:
    """Return one deterministic Groovy single-quoted string literal."""
    if not value or any(ord(char) < 32 or ord(char) == 127 for char in value):
        raise ValueError("FOGELL_SCM_URL must be non-empty printable text")
    return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"


def real_parent(path: pathlib.Path) -> pathlib.Path:
    """Return a verified parent without following aliases or traversal."""
    absolute_parent = path.parent.absolute()
    if ".." in path.parts:
        raise ValueError("FOGELL_RENDERED_CASES_DIR must not contain '..'")
    try:
        resolved_parent = path.parent.resolve(strict=True)
    except FileNotFoundError as error:
        raise ValueError(f"render output parent does not exist: {path.parent}") from error
    if resolved_parent != absolute_parent or not absolute_parent.is_dir():
        raise ValueError(
            f"render output parent must be a real non-symlink directory: {path.parent}"
        )
    return absolute_parent


def validate_existing_output(output: pathlib.Path, expected: set[str]) -> None:
    if not output.exists() and not output.is_symlink():
        return
    if output.is_symlink() or not output.is_dir():
        raise ValueError(f"render output must be a real directory: {output}")
    entries = {entry.name: entry for entry in output.iterdir()}
    if set(entries) != expected:
        missing = sorted(expected - set(entries))
        unexpected = sorted(set(entries) - expected)
        raise ValueError(
            "render output entries differ from the exact expected set; "
            f"missing={missing}, unexpected={unexpected}"
        )
    for name, entry in entries.items():
        mode = entry.stat(follow_symlinks=False).st_mode
        if entry.is_symlink() or not stat.S_ISREG(mode):
            raise ValueError(
                f"render output entry must be a regular non-symlink file: {name}"
            )


def main() -> int:
    evidence = pathlib.Path(__file__).resolve().parent
    cases = evidence / "cases"
    output = pathlib.Path(
        os.environ.get("FOGELL_RENDERED_CASES_DIR", evidence / "rendered-cases")
    )
    scm_url = os.environ.get("FOGELL_SCM_URL", "git://100.105.179.51/repo.git")

    try:
        replacement = groovy_single_quoted(scm_url)
    except ValueError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

    expected = set(CASES) | {"SHA256SUMS"}
    try:
        output_parent = real_parent(output)
        validate_existing_output(output, expected)
    except (OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

    manifest: list[str] = []
    stage = pathlib.Path(
        tempfile.mkdtemp(prefix=f".{output.name}-stage.", dir=output_parent)
    )
    try:
        for name in CASES:
            source_path = cases / name
            source = source_path.read_text(encoding="utf-8")
            expected_tokens = TOKEN_COUNTS.get(name, 0)
            actual_tokens = source.count(TOKEN)
            if actual_tokens != expected_tokens:
                raise ValueError(
                    f"{source_path} has {actual_tokens} SCM tokens; "
                    f"expected {expected_tokens}"
                )

            rendered = source.replace(TOKEN, replacement)
            if TOKEN in rendered:
                raise ValueError(f"unresolved SCM token in {source_path}")
            rendered_path = stage / name
            rendered_path.write_text(rendered, encoding="utf-8")
            digest = hashlib.sha256(rendered.encode("utf-8")).hexdigest()
            manifest.append(f"{digest}  {name}\n")
            print(f"staged {name} sha256={digest}")

        (stage / "SHA256SUMS").write_text("".join(manifest), encoding="ascii")
        publisher = evidence / "publish-run-bundle.py"
        subprocess.run(
            [sys.executable, str(publisher), str(stage), str(output)], check=True
        )
        print(f"rendered exact case set: {output}")
        return 0
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"ERROR: probe rendering refused: {error}", file=sys.stderr)
        return 1
    finally:
        if stage.exists() and not stage.is_symlink():
            shutil.rmtree(stage)


if __name__ == "__main__":
    raise SystemExit(main())
