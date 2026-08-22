#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import os
import pathlib
import shlex
import subprocess
import sys


PLUGINS = (
    "junit",
    "pipeline-rest-api",
    "pipeline-graph-analysis",
    "workflow-api",
    "pipeline-model-definition",
)


def fail(message: str) -> "NoReturn":
    raise SystemExit(f"ERROR: {message}")


def ssh(*args: str) -> str:
    host = os.environ.get("FG177_ORACLE_SSH_HOST", "")
    if not host:
        fail("FG177_ORACLE_SSH_HOST is required")
    return subprocess.run(
        ["ssh", host, shlex.join(args)],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        fail("usage: capture-surface.py DESTINATION")
    destination = pathlib.Path(argv[1])
    destination.mkdir(parents=True, exist_ok=False)
    container = os.environ.get("FG177_JENKINS_CONTAINER", "")
    if not container:
        fail("FG177_JENKINS_CONTAINER is required")

    inspect = ssh(
        "podman", "inspect", container, "--format",
        "{{.Id}}|{{.ImageName}}|{{.Image}}|{{.ImageDigest}}",
    ).strip()
    fields = inspect.split("|")
    if len(fields) != 4 or len(fields[0]) != 64 or len(fields[2]) != 64:
        fail("controller/container identity is malformed")
    (destination / "container.tsv").write_text(
        "container-id\t" + fields[0] + "\n"
        + "image-name\t" + fields[1] + "\n"
        + "image-id\t" + fields[2] + "\n"
        + "image-digest\t" + fields[3] + "\n"
    )

    rows: list[str] = []
    for plugin in PLUGINS:
        script = (
            'set -eu; root="/var/jenkins_home/plugins/$1"; jpi="/var/jenkins_home/plugins/$1.jpi"; '
            '[ -s "$jpi" ] && [ -d "$root/WEB-INF/lib" ]; '
            'sha256sum "$jpi"; find "$root/WEB-INF/lib" -type f -name "*.jar" -print0 '
            '| sort -z | xargs -0 -r sha256sum'
        )
        output = ssh("podman", "exec", container, "sh", "-ceu", script, "sh", plugin)
        plugin_rows = [line for line in output.splitlines() if line.strip()]
        if len(plugin_rows) < 2:
            fail(f"{plugin} returned no exact jar inventory")
        for line in plugin_rows:
            parts = line.split(None, 1)
            if len(parts) != 2 or len(parts[0]) != 64:
                fail(f"{plugin} returned a malformed digest row")
            rows.append(f"{plugin}\t{parts[0]}\t{parts[1]}")
    (destination / "plugin-jars.tsv").write_text("\n".join(rows) + "\n")
    (destination / "plugin-jars.sha256").write_text(
        hashlib.sha256(("\n".join(rows) + "\n").encode()).hexdigest() + "\n"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
