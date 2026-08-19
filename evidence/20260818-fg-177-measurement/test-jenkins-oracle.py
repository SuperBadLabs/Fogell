#!/usr/bin/env python3
"""Known-bad proof for the FG-177 Jenkins oracle identity gate."""

from __future__ import annotations

import http.server
import json
import os
import pathlib
import socket
import subprocess
import tempfile
import threading
import urllib.parse


ROOT = pathlib.Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "evidence/20260818-fg-177-measurement"
ORACLE = EVIDENCE / "jenkins-oracle.sh"
CORE = "2.568.1"
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


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        state = self.server.state  # type: ignore[attr-defined]
        path = urllib.parse.urlparse(self.path).path
        endpoint = "plugins" if path == "/pluginManager/api/json" else "root"
        response = state[endpoint]
        self.send_response(response.get("status", 200))
        for name, value in response.get("headers", [("X-Jenkins", CORE)]):
            self.send_header(name, value)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        body = response.get("body", b"{}")
        if isinstance(body, str):
            body = body.encode()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        pass


def state() -> dict[str, dict[str, object]]:
    return {
        "root": {"headers": [("x-jEnKiNs", CORE)], "body": b"ok"},
        "plugins": {
            "headers": [("X-JENKINS", CORE)],
            "body": json.dumps({"plugins": PLUGINS}),
        },
    }


def write_executable(path: pathlib.Path, source: str) -> None:
    path.write_text(source, encoding="utf-8")
    path.chmod(0o755)


def invoke(
    action: str,
    metadata: pathlib.Path,
    url: str,
    bin_dir: pathlib.Path,
    extra_env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    env.update(
        {
            "PATH": f"{bin_dir}:{env['PATH']}",
            "FOGELL_JENKINS_URL": url,
            "FOGELL_JENKINS_CORE": CORE,
            "FOGELL_JENKINS_HOST": "fixture-host",
            "FOGELL_JENKINS_CONTAINER": "fixture-controller",
            "FOGELL_STUB_IMAGE": IMAGE,
        }
    )
    if extra_env:
        env.update(extra_env)
    return subprocess.run(
        ["bash", str(ORACLE), action, str(metadata)],
        cwd=ROOT,
        env=env,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )


def require_refusal(result: subprocess.CompletedProcess[str], label: str) -> None:
    if result.returncode == 0:
        raise AssertionError(f"{label} unexpectedly passed: {result.stdout}")


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="fg177-oracle-proof.") as temporary:
        temp = pathlib.Path(temporary)
        metadata = temp / "metadata"
        metadata.mkdir()
        (metadata / "jenkins-core.txt").write_text(f"{CORE}\n", encoding="ascii")
        (metadata / "jenkins-plugins.tsv").write_text(
            "alpha\t1.2\ttrue\ttrue\nbeta\t3.4\tfalse\ttrue\n", encoding="utf-8"
        )
        (metadata / "jenkins-controller-image.txt").write_text(
            f"{IMAGE}\n", encoding="ascii"
        )

        bin_dir = temp / "bin"
        bin_dir.mkdir()
        write_executable(
            bin_dir / "ssh",
            """#!/usr/bin/env bash
if [[ ${FOGELL_STUB_SSH_RC:-0} -ne 0 ]]; then
  exit "$FOGELL_STUB_SSH_RC"
fi
printf '%s\\n' "$FOGELL_STUB_IMAGE"
""",
        )
        dotnet_calls = temp / "dotnet-calls"
        write_executable(
            bin_dir / "dotnet",
            """#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$FOGELL_STUB_CALLS"
exit 99
""",
        )

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        server.state = state()  # type: ignore[attr-defined]
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        url = f"http://127.0.0.1:{server.server_port}"

        result = invoke("verify", metadata, url, bin_dir)
        if result.returncode != 0:
            raise AssertionError(f"valid mixed-case headers refused: {result.stdout}")

        root_bad = [
            ("wrong core", {"headers": [("X-Jenkins", "2.999")]}),
            ("missing core", {"headers": []}),
            (
                "multiple core",
                {"headers": [("X-Jenkins", CORE), ("x-jenkins", CORE)]},
            ),
            (
                "redirect",
                {"status": 302, "headers": [("Location", "/elsewhere")]},
            ),
            ("authentication 401", {"status": 401}),
            ("authorization 403", {"status": 403}),
        ]
        for label, replacement in root_bad:
            server.state = state()  # type: ignore[attr-defined]
            server.state["root"] = replacement  # type: ignore[attr-defined]
            require_refusal(invoke("verify", metadata, url, bin_dir), label)

        plugin_bad = [
            ("plugin endpoint missing core", {"headers": [], "body": "{}"}),
            (
                "plugin endpoint multiple core",
                {
                    "headers": [("X-Jenkins", CORE), ("X-Jenkins", CORE)],
                    "body": json.dumps({"plugins": PLUGINS}),
                },
            ),
            (
                "plugin drift",
                {
                    "headers": [("X-Jenkins", CORE)],
                    "body": json.dumps(
                        {"plugins": [PLUGINS[0], {**PLUGINS[1], "version": "9.9"}]}
                    ),
                },
            ),
            (
                "duplicate plugin",
                {
                    "headers": [("X-Jenkins", CORE)],
                    "body": json.dumps({"plugins": [PLUGINS[0], PLUGINS[0]]}),
                },
            ),
            (
                "malformed plugin JSON",
                {"headers": [("X-Jenkins", CORE)], "body": "not-json"},
            ),
        ]
        for label, replacement in plugin_bad:
            server.state = state()  # type: ignore[attr-defined]
            server.state["plugins"] = replacement  # type: ignore[attr-defined]
            require_refusal(invoke("verify", metadata, url, bin_dir), label)

        server.state = state()  # type: ignore[attr-defined]
        require_refusal(
            invoke(
                "verify",
                metadata,
                url,
                bin_dir,
                {"FOGELL_STUB_IMAGE": IMAGE.replace("2" * 64, "3" * 64)},
            ),
            "image drift",
        )
        require_refusal(
            invoke(
                "verify",
                metadata,
                url,
                bin_dir,
                {"FOGELL_STUB_SSH_RC": "44"},
            ),
            "image inspection transport",
        )
        require_refusal(
            invoke(
                "verify",
                metadata,
                url,
                bin_dir,
                {"FOGELL_JENKINS_CORE": "2.1"},
            ),
            "runner core disagrees with pin",
        )

        capture = temp / "capture"
        result = invoke("capture", capture, url, bin_dir)
        if result.returncode != 0:
            raise AssertionError(f"capture failed: {result.stdout}")
        for name in (
            "jenkins-core.txt",
            "jenkins-plugins.tsv",
            "jenkins-controller-image.txt",
        ):
            if (capture / name).read_bytes() != (metadata / name).read_bytes():
                raise AssertionError(f"capture was not stable for {name}")

        # Each runner must reject the wrong live core before creating its output,
        # building/running the CLI, synchronizing SCM, or writing an exit marker.
        server.state = state()  # type: ignore[attr-defined]
        server.state["root"] = {"headers": [("X-Jenkins", "2.999")]}  # type: ignore[attr-defined]
        for runner in ("run-probes.sh", "run-archive-schema.sh"):
            runner_out = temp / f"refused-{runner}"
            if dotnet_calls.exists():
                dotnet_calls.unlink()
            env = os.environ.copy()
            env.update(
                {
                    "PATH": f"{bin_dir}:{env['PATH']}",
                    "FOGELL_JENKINS_URL": url,
                    "FOGELL_JENKINS_CORE": CORE,
                    "FOGELL_JENKINS_HOST": "fixture-host",
                    "FOGELL_JENKINS_CONTAINER": "fixture-controller",
                    "FOGELL_EVIDENCE_OUT": str(runner_out),
                    "FOGELL_STUB_CALLS": str(dotnet_calls),
                }
            )
            result = subprocess.run(
                ["bash", str(EVIDENCE / runner)],
                cwd=ROOT,
                env=env,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                check=False,
            )
            require_refusal(result, f"{runner} wrong core")
            if runner_out.exists() or dotnet_calls.exists():
                raise AssertionError(
                    f"{runner} mutated output or invoked dotnet before refusal"
                )

        server.shutdown()
        server.server_close()
        thread.join()

        with socket.socket() as unused:
            unused.bind(("127.0.0.1", 0))
            dead_url = f"http://127.0.0.1:{unused.getsockname()[1]}"
        require_refusal(
            invoke("verify", metadata, dead_url, bin_dir), "HTTP transport failure"
        )

    print(
        "JENKINS ORACLE PROOF: casing accepted; redirect/auth/missing/multiple/"
        "wrong/transport/plugin/image drift refused; capture stable; both runners "
        "refuse before output or CLI"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
