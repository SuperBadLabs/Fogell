#!/usr/bin/env python3
"""Small Jenkins adapter used by run-retained-scm-oracle.sh.

The separate adapter keeps the fixture/ref scheduler hermetically testable.  The
surface command is mandatory because controller/container and installed-jar
identity cannot be established through the public Jenkins API alone.
"""
from __future__ import annotations

import base64
import http.cookiejar
import html
import json
import os
import pathlib
import re
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


def request_detail(path: str, *, method: str = "GET", body: bytes | None = None,
                   content_type: str | None = None,
                   expected: tuple[int, ...] = (200,)) -> tuple[bytes, object]:
    if not BASE:
        fail("FG177_JENKINS_URL is required")
    headers: dict[str, str] = {}
    if USER or TOKEN:
        raw = base64.b64encode(f"{USER}:{TOKEN}".encode()).decode()
        headers["Authorization"] = f"Basic {raw}"
    if content_type:
        headers["Content-Type"] = content_type
    if method == "POST":
        try:
            crumb = json.loads(request("/crumbIssuer/api/json"))
            headers[str(crumb["crumbRequestField"])] = str(crumb["crumb"])
        except (urllib.error.HTTPError, KeyError, ValueError):
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


def request(path: str, *, method: str = "GET", body: bytes | None = None,
            content_type: str | None = None, expected: tuple[int, ...] = (200,)) -> bytes:
    data, _ = request_detail(
        path, method=method, body=body, content_type=content_type, expected=expected
    )
    return data


def job_path(job: str) -> str:
    return "/job/" + urllib.parse.quote(job, safe="")


def reset(job: str) -> None:
    path = job_path(job)
    try:
        request(path + "/api/json")
    except SystemExit as error:
        if "HTTP 404" in str(error):
            return
        raise
    request(path + "/doDelete", method="POST", body=b"", expected=(200, 302))


def assert_absent(job: str) -> None:
    try:
        request(job_path(job) + "/api/json")
    except SystemExit as error:
        if "HTTP 404" in str(error):
            return
        raise
    fail(f"refusing to reuse existing Jenkins job: {job}")


def inline_xml(case: pathlib.Path) -> bytes:
    script = html.escape(case.read_text(encoding="utf-8"), quote=False)
    return (
        "<flow-definition plugin=\"workflow-job\"><actions/><description>FG-177 retained SCM oracle</description>"
        "<keepDependencies>false</keepDependencies><properties/><definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
        f"<script>{script}</script><sandbox>true</sandbox></definition><triggers/><disabled>false</disabled></flow-definition>"
    ).encode()


def scm_xml(branch: str, url: str) -> bytes:
    branch_xml, url_xml = html.escape(branch), html.escape(url)
    return (
        "<flow-definition plugin=\"workflow-job\"><actions/><description>FG-177 retained SCM oracle</description>"
        "<keepDependencies>false</keepDependencies><properties/><definition class=\"org.jenkinsci.plugins.workflow.cps.CpsScmFlowDefinition\" plugin=\"workflow-cps\">"
        "<scm class=\"hudson.plugins.git.GitSCM\" plugin=\"git\"><configVersion>2</configVersion>"
        "<userRemoteConfigs><hudson.plugins.git.UserRemoteConfig>"
        f"<url>{url_xml}</url></hudson.plugins.git.UserRemoteConfig></userRemoteConfigs>"
        f"<branches><hudson.plugins.git.BranchSpec><name>*/{branch_xml}</name></hudson.plugins.git.BranchSpec></branches>"
        "<doGenerateSubmoduleConfigurations>false</doGenerateSubmoduleConfigurations><submoduleCfg class=\"empty-list\"/><extensions/></scm>"
        "<scriptPath>Jenkinsfile</scriptPath><lightweight>false</lightweight></definition><triggers/><disabled>false</disabled></flow-definition>"
    ).encode()


def configure(job: str, kind: str, branch: str, case: pathlib.Path, url: str,
              destination: pathlib.Path) -> None:
    xml = inline_xml(case) if kind == "git" else scm_xml(branch, url)
    if not destination.is_dir():
        fail(f"configuration destination is absent: {destination}")
    (destination / "submitted-config.xml").write_bytes(xml)
    path = job_path(job)
    exists = True
    try:
        request(path + "/api/json")
    except SystemExit as error:
        if "HTTP 404" not in str(error):
            raise
        exists = False
    target = path + "/config.xml" if exists else "/createItem?name=" + urllib.parse.quote(job, safe="")
    request(target, method="POST", body=xml, content_type="application/xml", expected=(200, 201))
    returned = request(path + "/config.xml")
    (destination / "returned-config.xml").write_bytes(returned)


def download_artifacts(job: str, number: int, build: dict, destination: pathlib.Path) -> None:
    artifact_dir = destination / "artifacts"
    artifact_dir.mkdir()
    wanted = {"checkout/fg177-workspace-revision.txt", "checkout/fg177-workspace-payload.txt"}
    found = {str(item.get("relativePath", "")) for item in build.get("artifacts", [])}
    if not wanted.issubset(found):
        fail(f"build {number} did not publish both workspace artifacts")
    for relative in sorted(wanted):
        data = request(job_path(job) + f"/{number}/artifact/" + urllib.parse.quote(relative, safe="/"))
        (artifact_dir / pathlib.Path(relative).name).write_bytes(data)


def build(job: str, number: int, destination: pathlib.Path) -> None:
    if not destination.is_dir() or not (destination / "expected.tsv").is_file():
        fail(f"build destination is not a prepared evidence directory: {destination}")
    _, headers = request_detail(
        job_path(job) + "/build", method="POST", body=b"", expected=(200, 201, 302)
    )
    location = str(headers.get("Location", ""))  # type: ignore[attr-defined]
    parsed_location = urllib.parse.urlsplit(location)
    parsed_base = urllib.parse.urlsplit(BASE)
    if (parsed_location.scheme or parsed_location.netloc) and (
        parsed_location.scheme != parsed_base.scheme or parsed_location.netloc != parsed_base.netloc
    ):
        fail("Jenkins queue Location points at a different origin")
    queue_path = parsed_location.path
    if not queue_path.endswith("/"):
        queue_path += "/"
    if not re.fullmatch(r"/queue/item/[0-9]+/", queue_path):
        fail("Jenkins build trigger returned no canonical queue item")
    queued_number: int | None = None
    for _ in range(int(os.environ.get("FG177_POLL_ATTEMPTS", "600"))):
        try:
            queued = json.loads(request(queue_path + "api/json"))
            if queued.get("cancelled") is True:
                fail("Jenkins cancelled the attributed queue item")
            executable = queued.get("executable")
            if isinstance(executable, dict) and isinstance(executable.get("number"), int):
                queued_number = executable["number"]
                break
        except SystemExit as error:
            if "HTTP 404" not in str(error):
                raise
        time.sleep(float(os.environ.get("FG177_POLL_INTERVAL", "0.5")))
    if queued_number is None:
        fail("attributed Jenkins queue item did not become executable")
    if queued_number != number:
        fail(f"attributed queue item became build {queued_number}, expected {number}")
    tree = "?depth=4"
    payload: dict | None = None
    for _ in range(int(os.environ.get("FG177_POLL_ATTEMPTS", "600"))):
        try:
            candidate = json.loads(request(job_path(job) + f"/{number}/api/json{tree}"))
            if not candidate.get("building", True) and candidate.get("result"):
                payload = candidate
                break
        except SystemExit as error:
            if "HTTP 404" not in str(error):
                raise
        time.sleep(float(os.environ.get("FG177_POLL_INTERVAL", "0.5")))
    if payload is None:
        fail(f"build {number} did not reach a terminal state")
    (destination / "build.json").write_text(json.dumps(payload, sort_keys=True, indent=2) + "\n")
    actions = [item for item in payload.get("actions", []) if item.get("_class") == "hudson.plugins.git.util.BuildData"]
    if not actions:
        fail(f"build {number} has no controller-owned git BuildData action")
    (destination / "builddata.json").write_text(json.dumps(actions, sort_keys=True, indent=2) + "\n")
    console = request(job_path(job) + f"/{number}/consoleText").decode("utf-8", errors="replace")
    (destination / "console.txt").write_text(console)
    if console.count("[Pipeline] Start") != 1:
        fail(f"build {number} console lacks one exact Pipeline start boundary")
    prefix = console.split("[Pipeline] Start", 1)[0]
    (destination / "definition-scm.txt").write_text(prefix)
    download_artifacts(job, number, payload, destination)


def surface(destination: pathlib.Path) -> None:
    command = os.environ.get("FG177_SURFACE_CAPTURE", "")
    if not command or os.path.basename(command) != command and not pathlib.Path(command).is_file():
        fail("FG177_SURFACE_CAPTURE must name an executable surface-capture file")
    subprocess.run([command, str(destination)], check=True, env=os.environ.copy())


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        fail("usage: jenkins-driver.py surface|reset|configure|build ...")
    command = argv[1]
    if command == "surface" and len(argv) == 3:
        surface(pathlib.Path(argv[2]))
    elif command == "reset" and len(argv) == 3:
        reset(argv[2])
    elif command == "assert-absent" and len(argv) == 3:
        assert_absent(argv[2])
    elif command == "configure" and len(argv) == 8:
        configure(argv[2], argv[3], argv[4], pathlib.Path(argv[5]), argv[6], pathlib.Path(argv[7]))
    elif command == "build" and len(argv) == 5:
        build(argv[2], int(argv[3]), pathlib.Path(argv[4]))
    else:
        fail("invalid driver command or arity")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
