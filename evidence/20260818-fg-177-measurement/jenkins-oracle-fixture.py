#!/usr/bin/env python3
"""Local Jenkins identity fixture for hermetic FG-177 runner proofs."""

from __future__ import annotations

import http.server
import json
import os
import pathlib
import re
import sys
import urllib.parse


CORE = os.environ.get("FOGELL_FIXTURE_JENKINS_CORE", "2.568.1")
if re.fullmatch(r"[0-9]+\.[0-9]+(?:\.[0-9]+)?", CORE) is None:
    raise RuntimeError(f"invalid FOGELL_FIXTURE_JENKINS_CORE: {CORE!r}")
IMAGE = (
    "fixture/jenkins:2.568.1|"
    + "1" * 64
    + "|sha256:"
    + "2" * 64
)
PLUGINS = [
    {"shortName": "alpha", "version": "1.2", "active": True, "enabled": True},
    {"shortName": "beta", "version": "3.4", "active": False, "enabled": True},
]
STATE_FILE = os.environ.get("FOGELL_FIXTURE_STATE_FILE")


def drift_mode() -> str:
    if STATE_FILE is None:
        return ""
    try:
        return pathlib.Path(STATE_FILE).read_text(encoding="ascii").strip()
    except FileNotFoundError:
        return ""


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        mode = drift_mode()
        if mode == "transport":
            self.send_response(503)
            self.end_headers()
            return
        path = urllib.parse.urlparse(self.path).path
        if path == "/":
            body = b"fixture"
        elif path == "/pluginManager/api/json":
            plugins = PLUGINS
            if mode == "plugin":
                plugins = [dict(PLUGINS[0]), {**PLUGINS[1], "version": "9.9"}]
            body = json.dumps({"plugins": plugins}).encode()
        else:
            self.send_response(404)
            self.end_headers()
            return
        self.send_response(200)
        self.send_header("x-jEnKiNs", "2.568.2" if mode == "core" else CORE)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        pass


def write_metadata(destination: pathlib.Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "jenkins-core.txt").write_text(f"{CORE}\n", encoding="ascii")
    (destination / "jenkins-plugins.tsv").write_text(
        "alpha\t1.2\ttrue\ttrue\nbeta\t3.4\tfalse\ttrue\n", encoding="utf-8"
    )
    (destination / "jenkins-controller-image.txt").write_text(
        f"{IMAGE}\n", encoding="ascii"
    )


def serve(ready_file: pathlib.Path) -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    ready_tmp = ready_file.with_suffix(ready_file.suffix + ".tmp")
    ready_tmp.write_text(f"http://127.0.0.1:{server.server_port}\n", encoding="ascii")
    ready_tmp.replace(ready_file)
    server.serve_forever()


def main() -> int:
    if len(sys.argv) != 3 or sys.argv[1] not in {"metadata", "serve"}:
        print(f"usage: {sys.argv[0]} {{metadata|serve}} PATH", file=sys.stderr)
        return 2
    path = pathlib.Path(sys.argv[2])
    if sys.argv[1] == "metadata":
        write_metadata(path)
    else:
        serve(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
