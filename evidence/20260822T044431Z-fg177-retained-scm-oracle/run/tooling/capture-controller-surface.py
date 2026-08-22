#!/usr/bin/env python3
"""Capture the non-script-writable Jenkins/controller surface for this oracle."""
from __future__ import annotations
import base64, hashlib, json, os, pathlib, shlex, subprocess, sys, urllib.request

def fail(message: str) -> "NoReturn": raise SystemExit(f"ERROR: {message}")

def http(path: str):
    base = os.environ.get("FG177_JENKINS_URL", "").rstrip("/")
    if not base: fail("FG177_JENKINS_URL is required")
    req = urllib.request.Request(base + path)
    user, token = os.environ.get("FG177_JENKINS_USER", ""), os.environ.get("FG177_JENKINS_TOKEN", "")
    if user or token:
        req.add_header("Authorization", "Basic " + base64.b64encode(f"{user}:{token}".encode()).decode())
    return urllib.request.urlopen(req, timeout=30)

def ssh(*args: str) -> str:
    host = os.environ.get("FG177_ORACLE_SSH_HOST", "")
    if not host: fail("FG177_ORACLE_SSH_HOST is required")
    # OpenSSH concatenates arguments into one remote-shell command.  Quote the
    # complete argv explicitly so Podman format strings and `sh -ceu` scripts
    # cannot be reinterpreted by that shell.
    return subprocess.run(
        ["ssh", host, shlex.join(args)],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout

def plugin_digests(container: str, short_name: str, jar_name: str) -> tuple[str, str, str]:
    script = (
        'set -eu; p="/var/jenkins_home/plugins/$1.jpi"; j="/var/jenkins_home/plugins/$1/WEB-INF/lib/$2"; '
        'sha256sum "$p"; sha256sum "$j"; unzip -p "$p" META-INF/MANIFEST.MF | sha256sum'
    )
    output = ssh("podman", "exec", container, "sh", "-ceu", script, "sh", short_name, jar_name)
    rows = output.splitlines()
    if len(rows) != 3: fail(f"{short_name} digest capture returned {len(rows)} rows")
    values = tuple(row.split()[0] for row in rows)
    if any(len(value) != 64 for value in values): fail(f"{short_name} digest is malformed")
    return values  # type: ignore[return-value]

def main(argv: list[str]) -> int:
    if len(argv) != 2: fail("usage: capture-controller-surface.py DESTINATION")
    destination = pathlib.Path(argv[1]); destination.mkdir(parents=True, exist_ok=False)
    container = os.environ.get("FG177_JENKINS_CONTAINER", "")
    if not container: fail("FG177_JENKINS_CONTAINER is required")
    expected_container_id = os.environ.get("FG177_EXPECTED_CONTAINER_ID", "")
    if len(expected_container_id) != 64: fail("FG177_EXPECTED_CONTAINER_ID is required")
    with http("/api/json") as response:
        core = response.headers.get("X-Jenkins", "")
        session = response.headers.get("X-Jenkins-Session", "")
    if not core or not session: fail("Jenkins identity headers are absent")
    inspect = ssh(
        "podman", "inspect", container, "--format",
        "{{.Id}}|{{.ImageName}}|{{.Image}}|{{.ImageDigest}}",
    ).strip()
    parts = inspect.split("|")
    if (len(parts) != 4 or len(parts[0]) != 64 or len(parts[2]) != 64
            or not parts[3].startswith("sha256:") or len(parts[3]) != 71):
        fail("controller inspect identity is malformed")
    if parts[0] != expected_container_id:
        fail("surface container differs from the canonical oracle receipt")
    (destination / "identity.tsv").write_text(
        f"container-id\t{parts[0]}\nimage-name\t{parts[1]}\nimage-id\t{parts[2]}\n"
        f"image-digest\t{parts[3]}\nsession-sha256\t{hashlib.sha256(session.encode()).hexdigest()}\n")
    (destination / "jenkins-core.txt").write_text(core + "\n")
    with http("/pluginManager/api/json?tree=plugins[shortName,version,active,enabled]") as response:
        plugins = json.load(response).get("plugins", [])
    rows = sorted((str(item["shortName"]), str(item["version"]), str(item["active"]).lower(), str(item["enabled"]).lower()) for item in plugins)
    (destination / "jenkins-plugins.tsv").write_text("".join("\t".join(row) + "\n" for row in rows))
    versions = {row[0]: row[1] for row in rows}
    for short_name, jar_name, filename in (
        ("git", "git.jar", "git-plugin.tsv"),
        ("workflow-scm-step", "workflow-scm-step.jar", "workflow-scm-step-plugin.tsv"),
    ):
        digests = plugin_digests(parts[0], short_name, jar_name)
        (destination / filename).write_text("\t".join((short_name, versions.get(short_name, "<absent>"), *digests)) + "\n")
    return 0

if __name__ == "__main__": raise SystemExit(main(sys.argv))
