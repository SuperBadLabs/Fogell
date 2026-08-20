#!/usr/bin/env python3
"""Known-bad proof for the FG-177 Jenkins oracle identity gate."""

from __future__ import annotations

import http.server
import hashlib
import json
import os
import pathlib
import shutil
import socket
import subprocess
import tempfile
import threading
import urllib.parse


ROOT = pathlib.Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "evidence/20260818-fg-177-measurement"
ORACLE = EVIDENCE / "jenkins-oracle.sh"
CORE = "2.568.1"
SESSION = "fixture-session-secret"
CONTAINER_ID = "3" * 64
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
PLUGIN_TEXT = "alpha\t1.2\ttrue\ttrue\nbeta\t3.4\tfalse\ttrue\n"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        state = self.server.state  # type: ignore[attr-defined]
        path = urllib.parse.urlparse(self.path).path
        endpoint = "plugins" if path == "/pluginManager/api/json" else "root"
        response = state[endpoint]
        self.send_response(response.get("status", 200))
        for name, value in response.get(
            "headers",
            [("X-Jenkins", CORE), ("X-Jenkins-Session", SESSION)],
        ):
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
        "root": {
            "headers": [("x-jEnKiNs", CORE), ("X-JeNkInS-SeSsIoN", SESSION)],
            "body": b"ok",
        },
        "plugins": {
            "headers": [("X-JENKINS", CORE), ("x-jenkins-session", SESSION)],
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
    snapshot: pathlib.Path | None = None,
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
            "FOGELL_STUB_CONTAINER_ID": CONTAINER_ID,
            "FOGELL_STUB_INTERNAL_CORE": CORE,
            "FOGELL_STUB_INTERNAL_SESSION": SESSION,
            "FOGELL_STUB_INTERNAL_PLUGIN_BODY": json.dumps({"plugins": PLUGINS}),
            "FOGELL_STUB_INSPECT_STATE": str(bin_dir.parent / "inspect-state"),
        }
    )
    if extra_env:
        env.update(extra_env)
    pathlib.Path(env["FOGELL_STUB_INSPECT_STATE"]).unlink(missing_ok=True)
    command = ["bash", str(ORACLE), action, str(metadata)]
    if snapshot is not None:
        command.append(str(snapshot))
    return subprocess.run(
        command,
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
        (metadata / "jenkins-plugins.tsv").write_text(PLUGIN_TEXT, encoding="utf-8")
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
        shutil.copy2(EVIDENCE / "jenkins-oracle-ssh-fixture.sh", bin_dir / "ssh")
        (bin_dir / "ssh").chmod(0o755)
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
        core_digest = hashlib.sha256(f"{CORE}\n".encode()).hexdigest()
        plugin_digest = hashlib.sha256(PLUGIN_TEXT.encode()).hexdigest()
        image_digest = hashlib.sha256(f"{IMAGE}\n".encode()).hexdigest()
        session_digest = hashlib.sha256(SESSION.encode()).hexdigest()
        expected_receipt = "\n".join(
            (
                "format\tfogell-jenkins-oracle-v2",
                f"jenkins-core\t{CORE}",
                f"jenkins-session-sha256\t{session_digest}",
                f"controller-container-id\t{CONTAINER_ID}",
                f"core-metadata-sha256\t{core_digest}",
                "plugin-count\t2",
                f"plugin-manifest-sha256\t{plugin_digest}",
                "controller-image-name\tfixture/jenkins:2.568.1",
                f"controller-image-id\t{'1' * 64}",
                f"controller-image-digest\tsha256:{'2' * 64}",
                f"image-metadata-sha256\t{image_digest}",
                "",
            )
        )
        if result.stdout != expected_receipt:
            raise AssertionError(
                f"verify receipt did not bind the full canonical identity:\n{result.stdout}"
            )

        snapshot = temp / "verified-snapshot"
        result = invoke("verify", metadata, url, bin_dir, snapshot=snapshot)
        if result.returncode != 0 or result.stdout != expected_receipt:
            raise AssertionError(f"snapshot verification failed: {result.stdout}")
        if {path.name for path in snapshot.iterdir()} != {
            "jenkins-core.txt",
            "jenkins-plugins.tsv",
            "jenkins-controller-image.txt",
        }:
            raise AssertionError("verified snapshot does not have the exact file set")
        for name in (
            "jenkins-core.txt",
            "jenkins-plugins.tsv",
            "jenkins-controller-image.txt",
        ):
            if (snapshot / name).read_bytes() != (metadata / name).read_bytes():
                raise AssertionError(f"verified snapshot differs for {name}")
            if (snapshot / name).is_symlink():
                raise AssertionError(f"verified snapshot is symlinked for {name}")

        if SESSION in result.stdout:
            raise AssertionError("raw Jenkins session leaked into the oracle receipt")
        for path in (*metadata.iterdir(), *snapshot.iterdir()):
            if SESSION.encode() in path.read_bytes():
                raise AssertionError(f"raw Jenkins session leaked into {path.name}")

        require_refusal(
            invoke("verify", metadata, url, bin_dir, snapshot=snapshot),
            "existing snapshot destination",
        )

        root_bad = [
            (
                "split-controller core",
                {
                    "headers": [
                        ("X-Jenkins", "2.999"),
                        ("X-Jenkins-Session", SESSION),
                    ]
                },
            ),
            ("missing core", {"headers": [("X-Jenkins-Session", SESSION)]}),
            (
                "multiple core",
                {
                    "headers": [
                        ("X-Jenkins", CORE),
                        ("x-jenkins", CORE),
                        ("X-Jenkins-Session", SESSION),
                    ]
                },
            ),
            ("missing session", {"headers": [("X-Jenkins", CORE)]}),
            (
                "multiple session",
                {
                    "headers": [
                        ("X-Jenkins", CORE),
                        ("X-Jenkins-Session", SESSION),
                        ("x-jenkins-session", SESSION),
                    ]
                },
            ),
            (
                "split-controller session",
                {"headers": [("X-Jenkins", CORE), ("X-Jenkins-Session", "other")]},
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
            (
                "plugin endpoint missing session",
                {"headers": [("X-Jenkins", CORE)], "body": "{}"},
            ),
            (
                "plugin endpoint multiple session",
                {
                    "headers": [
                        ("X-Jenkins", CORE),
                        ("X-Jenkins-Session", SESSION),
                        ("X-Jenkins-Session", SESSION),
                    ],
                    "body": json.dumps({"plugins": PLUGINS}),
                },
            ),
            (
                "plugin endpoint session mismatch",
                {
                    "headers": [
                        ("X-Jenkins", CORE),
                        ("X-Jenkins-Session", "other"),
                    ],
                    "body": json.dumps({"plugins": PLUGINS}),
                },
            ),
            ("plugin endpoint missing core", {"headers": [], "body": "{}"}),
            (
                "plugin endpoint multiple core",
                {
                    "headers": [("X-Jenkins", CORE), ("X-Jenkins", CORE)],
                    "body": json.dumps({"plugins": PLUGINS}),
                },
            ),
            (
                "same-count plugin version drift",
                {
                    "headers": [("X-Jenkins", CORE)],
                    "body": json.dumps(
                        {"plugins": [PLUGINS[0], {**PLUGINS[1], "version": "9.9"}]}
                    ),
                },
            ),
            (
                "same-count plugin active drift",
                {
                    "headers": [("X-Jenkins", CORE)],
                    "body": json.dumps(
                        {"plugins": [PLUGINS[0], {**PLUGINS[1], "active": True}]}
                    ),
                },
            ),
            (
                "same-count plugin enabled drift",
                {
                    "headers": [("X-Jenkins", CORE)],
                    "body": json.dumps(
                        {"plugins": [PLUGINS[0], {**PLUGINS[1], "enabled": False}]}
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
        server.state["plugins"]["body"] = json.dumps(  # type: ignore[index]
            {"plugins": [PLUGINS[0], {**PLUGINS[1], "version": "9.9"}]}
        )
        require_refusal(
            invoke("verify", metadata, url, bin_dir),
            "external/internal plugin surface hybrid",
        )

        server.state = state()  # type: ignore[attr-defined]
        internal_bad = [
            (
                "internal root missing core",
                {"FOGELL_STUB_INTERNAL_ROOT_HEADERS": "missing-core"},
            ),
            (
                "internal root multiple core",
                {"FOGELL_STUB_INTERNAL_ROOT_HEADERS": "multiple-core"},
            ),
            (
                "internal root missing session",
                {"FOGELL_STUB_INTERNAL_ROOT_HEADERS": "missing-session"},
            ),
            (
                "internal root multiple session",
                {"FOGELL_STUB_INTERNAL_ROOT_HEADERS": "multiple-session"},
            ),
            (
                "internal plugin missing core",
                {"FOGELL_STUB_INTERNAL_PLUGIN_HEADERS": "missing-core"},
            ),
            (
                "internal plugin multiple core",
                {"FOGELL_STUB_INTERNAL_PLUGIN_HEADERS": "multiple-core"},
            ),
            (
                "internal plugin missing session",
                {"FOGELL_STUB_INTERNAL_PLUGIN_HEADERS": "missing-session"},
            ),
            (
                "internal plugin multiple session",
                {"FOGELL_STUB_INTERNAL_PLUGIN_HEADERS": "multiple-session"},
            ),
            ("internal root core mismatch", {"FOGELL_STUB_INTERNAL_ROOT_CORE": "2.999"}),
            (
                "internal plugin core mismatch",
                {"FOGELL_STUB_INTERNAL_PLUGIN_CORE": "2.999"},
            ),
            (
                "internal plugin session mismatch",
                {"FOGELL_STUB_INTERNAL_PLUGIN_SESSION": "other"},
            ),
            (
                "internal plugin surface mismatch",
                {
                    "FOGELL_STUB_INTERNAL_PLUGIN_BODY": json.dumps(
                        {"plugins": [PLUGINS[0], {**PLUGINS[1], "version": "9.9"}]}
                    )
                },
            ),
            ("internal root transport", {"FOGELL_STUB_INTERNAL_ROOT_RC": "43"}),
            ("internal plugin HTTP status", {"FOGELL_STUB_INTERNAL_PLUGIN_STATUS": "503"}),
            (
                "container replacement during verification",
                {"FOGELL_STUB_CONTAINER_ID_AFTER": "4" * 64},
            ),
            (
                "image replacement during verification",
                {"FOGELL_STUB_IMAGE_AFTER": IMAGE.replace("2" * 64, "4" * 64)},
            ),
        ]
        for label, planted_env in internal_bad:
            require_refusal(invoke("verify", metadata, url, bin_dir, planted_env), label)

        secret_marker = "do-not-reflect-session-secret"
        server.state = state()  # type: ignore[attr-defined]
        server.state["root"] = {  # type: ignore[attr-defined]
            "headers": [
                ("X-Jenkins", CORE),
                ("X-Jenkins-Session", secret_marker),
            ]
        }
        secret_result = invoke("verify", metadata, url, bin_dir)
        require_refusal(secret_result, "session mismatch secret reflection")
        if secret_marker in secret_result.stdout:
            raise AssertionError("refusal reflected raw Jenkins session material")

        server.state = state()  # type: ignore[attr-defined]
        original_plugins = (metadata / "jenkins-plugins.tsv").read_text()
        (metadata / "jenkins-plugins.tsv").write_text(
            original_plugins.replace("3.4", "9.9"), encoding="utf-8"
        )
        require_refusal(
            invoke("verify", metadata, url, bin_dir),
            "exact pinned plugin digest differs at the same count",
        )
        (metadata / "jenkins-plugins.tsv").write_text(
            original_plugins, encoding="utf-8"
        )

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
        server.state["root"] = {  # type: ignore[attr-defined]
            "headers": [
                ("X-Jenkins", "2.999"),
                ("X-Jenkins-Session", SESSION),
            ]
        }
        for runner in ("run-probes.sh", "run-archive-schema.sh"):
            runner_out = temp / f"refused-{runner}"
            if dotnet_calls.exists():
                dotnet_calls.unlink()
            runner_inspect_state = temp / f"inspect-{runner}"
            runner_inspect_state.unlink(missing_ok=True)
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
                    "FOGELL_STUB_IMAGE": IMAGE,
                    "FOGELL_STUB_CONTAINER_ID": CONTAINER_ID,
                    "FOGELL_STUB_INTERNAL_CORE": CORE,
                    "FOGELL_STUB_INTERNAL_SESSION": SESSION,
                    "FOGELL_STUB_INTERNAL_PLUGIN_BODY": json.dumps({"plugins": PLUGINS}),
                    "FOGELL_STUB_INSPECT_STATE": str(runner_inspect_state),
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
        "wrong/transport/plugin version/active/enabled/image drift refused; full "
        "canonical identity digest bound; capture stable; both runners "
        "refuse before output or CLI"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
