#!/usr/bin/env python3
"""Render auditable FG-177 probe templates with the configured SCM URL."""

from __future__ import annotations

import hashlib
import os
import pathlib
import sys


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

    output.mkdir(parents=True, exist_ok=True)
    expected = set(CASES) | {"SHA256SUMS"}
    for path in output.iterdir():
        if path.is_file() and path.name not in expected:
            print(f"ERROR: unexpected stale rendered file: {path}", file=sys.stderr)
            return 1

    manifest: list[str] = []
    for name in CASES:
        source_path = cases / name
        source = source_path.read_text(encoding="utf-8")
        expected_tokens = TOKEN_COUNTS.get(name, 0)
        actual_tokens = source.count(TOKEN)
        if actual_tokens != expected_tokens:
            print(
                f"ERROR: {source_path} has {actual_tokens} SCM tokens; "
                f"expected {expected_tokens}",
                file=sys.stderr,
            )
            return 1

        rendered = source.replace(TOKEN, replacement)
        if TOKEN in rendered:
            print(f"ERROR: unresolved SCM token in {source_path}", file=sys.stderr)
            return 1
        rendered_path = output / name
        rendered_path.write_text(rendered, encoding="utf-8")
        digest = hashlib.sha256(rendered.encode("utf-8")).hexdigest()
        manifest.append(f"{digest}  {name}\n")
        print(f"rendered {rendered_path} sha256={digest}")

    manifest_path = output / "SHA256SUMS"
    manifest_path.write_text("".join(manifest), encoding="ascii")
    print(f"manifest {manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
