#!/usr/bin/env python3
from __future__ import annotations

import base64
import hashlib
import html
import http.cookiejar
import json
import os
import pathlib
import re
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


BASE = os.environ.get("FG177_JENKINS_URL", "").rstrip("/")
USER = os.environ.get("FG177_JENKINS_USER", "")
TOKEN = os.environ.get("FG177_JENKINS_TOKEN", "")
OPENER = urllib.request.build_opener(
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar())
)


def fail(message: str) -> "NoReturn":
    raise SystemExit(f"ERROR: {message}")


def request_detail(
    path: str,
    *,
    method: str = "GET",
    body: bytes | None = None,
    content_type: str | None = None,
    expected: tuple[int, ...] = (200,),
) -> tuple[bytes, object]:
    if not BASE:
        fail("FG177_JENKINS_URL is required")
    headers: dict[str, str] = {}
    if USER or TOKEN:
        encoded = base64.b64encode(f"{USER}:{TOKEN}".encode()).decode()
        headers["Authorization"] = f"Basic {encoded}"
    if content_type:
        headers["Content-Type"] = content_type
    if method == "POST":
        try:
            crumb = json.loads(request("/crumbIssuer/api/json"))
            headers[str(crumb["crumbRequestField"])] = str(crumb["crumb"])
        except (urllib.error.HTTPError, KeyError, ValueError, SystemExit):
            pass
    req = urllib.request.Request(BASE + path, data=body, headers=headers, method=method)
    try:
        with OPENER.open(req, timeout=30) as response:
            data = response.read()
            if response.status not in expected:
                fail(f"{method} {path} returned HTTP {response.status}")
            return data, response.headers
    except urllib.error.HTTPError as error:
        if error.code in expected:
            return error.read(), error.headers
        fail(f"{method} {path} returned HTTP {error.code}")


def request(path: str, **kwargs: object) -> bytes:
    return request_detail(path, **kwargs)[0]


def job_path(job: str) -> str:
    return "/job/" + urllib.parse.quote(job, safe="")


def job_xml(case: pathlib.Path) -> bytes:
    script = html.escape(case.read_text(encoding="utf-8"), quote=False)
    return (
        '<flow-definition plugin="workflow-job"><actions/>'
        '<description>FG-177 retained JUnit stage-status oracle</description>'
        '<keepDependencies>false</keepDependencies><properties/>'
        '<definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition" plugin="workflow-cps">'
        f"<script>{script}</script><sandbox>true</sandbox></definition>"
        '<triggers/><disabled>false</disabled></flow-definition>'
    ).encode()


def assert_absent(job: str) -> None:
    try:
        request(job_path(job) + "/api/json")
    except SystemExit as error:
        if "HTTP 404" in str(error):
            return
        raise
    fail(f"refusing to reuse existing Jenkins job: {job}")


def delete(job: str) -> None:
    try:
        request(job_path(job) + "/doDelete", method="POST", body=b"", expected=(200, 302))
    except SystemExit as error:
        if "HTTP 404" not in str(error):
            raise


def configure(job: str, case: pathlib.Path, destination: pathlib.Path) -> None:
    submitted = job_xml(case)
    (destination / "submitted-config.xml").write_bytes(submitted)
    request(
        "/createItem?name=" + urllib.parse.quote(job, safe=""),
        method="POST",
        body=submitted,
        content_type="application/xml",
        expected=(200, 201),
    )
    returned = request(job_path(job) + "/config.xml")
    if not returned:
        fail("Jenkins returned an empty job configuration")
    (destination / "returned-config.xml").write_bytes(returned)


def wait_for_build(job: str) -> tuple[int, dict]:
    _, headers = request_detail(
        job_path(job) + "/build", method="POST", body=b"", expected=(200, 201, 302)
    )
    location = str(headers.get("Location", ""))  # type: ignore[attr-defined]
    parsed = urllib.parse.urlsplit(location)
    base = urllib.parse.urlsplit(BASE)
    if (parsed.scheme or parsed.netloc) and (parsed.scheme, parsed.netloc) != (base.scheme, base.netloc):
        fail("queue Location points at a different Jenkins origin")
    queue_path = parsed.path
    if not queue_path.endswith("/"):
        queue_path += "/"
    if not re.fullmatch(r"/queue/item/[0-9]+/", queue_path):
        fail("build trigger returned no canonical queue item")

    number: int | None = None
    attempts = int(os.environ.get("FG177_POLL_ATTEMPTS", "600"))
    interval = float(os.environ.get("FG177_POLL_INTERVAL", "0.5"))
    for _ in range(attempts):
        try:
            queued = json.loads(request(queue_path + "api/json"))
            if queued.get("cancelled") is True:
                fail("Jenkins cancelled the attributed queue item")
            executable = queued.get("executable")
            if isinstance(executable, dict) and isinstance(executable.get("number"), int):
                number = int(executable["number"])
                break
        except SystemExit as error:
            if "HTTP 404" not in str(error):
                raise
        time.sleep(interval)
    if number is None:
        fail("attributed queue item did not become executable")

    payload: dict | None = None
    for _ in range(attempts):
        try:
            candidate = json.loads(request(job_path(job) + f"/{number}/api/json?depth=2"))
            if candidate.get("building") is False and isinstance(candidate.get("result"), str):
                payload = candidate
                break
        except SystemExit as error:
            if "HTTP 404" not in str(error):
                raise
        time.sleep(interval)
    if payload is None:
        fail(f"build {number} did not reach a terminal state")
    return number, payload


def is_scaffolding(path: str) -> bool:
    path = path.replace("\\", "/")
    name = pathlib.PurePosixPath(path).name
    return (
        path.startswith(".git/")
        or "@tmp/" in path
        or path.endswith(".pid")
        or path.startswith("durable-")
        or "/durable-" in path
        or name in {"jenkins-log.txt", "jenkins-result.txt", "script.sh", "script.sh.copy"}
    )


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


def capture_workspace(job: str, destination: pathlib.Path) -> None:
    container = os.environ.get("FG177_JENKINS_CONTAINER", "")
    if not container:
        fail("FG177_JENKINS_CONTAINER is required")
    script = (
        'set -eu; root="/var/jenkins_home/workspace/$1"; [ -d "$root" ]; '
        'cd "$root"; find . -type f -print0 | sort -z | xargs -0 -r sha256sum'
    )
    output = ssh("podman", "exec", container, "sh", "-ceu", script, "sh", job)
    entries: list[tuple[str, str]] = []
    for raw in output.splitlines():
        parts = raw.split("  ", 1)
        if len(parts) != 2:
            continue
        digest, path = parts[0].strip(), parts[1].strip().replace("\\", "/")
        if path.startswith("./"):
            path = path[2:]
        if len(digest) == 64 and path and not is_scaffolding(path):
            entries.append((path, digest))
    entries.sort()
    manifest = "\n".join(f"{path}\t{digest}" for path, digest in entries)
    (destination / "workspace.tsv").write_text(manifest + ("\n" if manifest else ""))
    (destination / "workspace.sha256").write_text(hashlib.sha256(manifest.encode()).hexdigest() + "\n")


def capture_build(job: str, number: int, payload: dict, destination: pathlib.Path) -> None:
    (destination / "build.json").write_text(json.dumps(payload, sort_keys=True, indent=2) + "\n")
    console = request(job_path(job) + f"/{number}/consoleText").decode("utf-8", errors="replace")
    if console.count("[Pipeline] Start") != 1:
        fail("console lacks one exact Pipeline start boundary")
    (destination / "console.txt").write_text(console)

    raw = json.loads(request(job_path(job) + f"/{number}/wfapi/describe"))
    if not isinstance(raw, dict) or not isinstance(raw.get("stages"), list):
        fail("wfapi/describe has no stages array")
    (destination / "wfapi-describe.json").write_text(json.dumps(raw, sort_keys=True, indent=2) + "\n")

    canonical: list[dict[str, str]] = []
    observed_names: list[str] = []
    node_dir = destination / "stage-nodes"
    node_dir.mkdir()
    for stage in raw["stages"]:
        if not isinstance(stage, dict):
            fail("wfapi stage is not an object")
        name, status, node_id = stage.get("name"), stage.get("status"), stage.get("id")
        if not all(isinstance(value, str) and value for value in (name, status, node_id)):
            fail("wfapi stage lacks nonempty name/status/id")
        observed_names.append(name)
        if name in {"probe", "later"}:
            canonical.append({"name": name, "status": status})
        detail = json.loads(
            request(job_path(job) + f"/{number}/execution/node/{urllib.parse.quote(node_id, safe='')}/wfapi/describe")
        )
        filename = (
            f"{name}.json"
            if name in {"probe", "later"}
            else "synthetic-post-actions.json"
        )
        (node_dir / filename).write_text(json.dumps(detail, sort_keys=True, indent=2) + "\n")
    names = [stage["name"] for stage in canonical]
    if sorted(observed_names) != ["Declarative: Post Actions", "later", "probe"]:
        fail(f"wfapi stage surface has unexpected declared/synthetic stages: {observed_names}")
    if sorted(names) != ["later", "probe"] or len(names) != len(set(names)):
        fail(f"declared stage projection is not exactly probe/later: {names}")
    (destination / "stages.canonical.json").write_text(
        json.dumps(canonical, sort_keys=True, indent=2) + "\n"
    )
    capture_workspace(job, destination)


def run(case: pathlib.Path, job: str, destination: pathlib.Path) -> None:
    if destination.exists():
        fail(f"destination already exists: {destination}")
    destination.mkdir(parents=True)
    assert_absent(job)
    try:
        configure(job, case, destination)
        number, payload = wait_for_build(job)
        (destination / "attribution.tsv").write_text(f"job\t{job}\nbuild\t{number}\n")
        capture_build(job, number, payload, destination)
    finally:
        delete(job)


def main(argv: list[str]) -> int:
    if len(argv) != 5 or argv[1] != "run":
        fail("usage: jenkins-driver.py run CASE JOB DESTINATION")
    run(pathlib.Path(argv[2]), argv[3], pathlib.Path(argv[4]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
