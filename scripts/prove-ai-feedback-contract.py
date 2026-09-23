#!/usr/bin/env python3
"""Bounded FG-266/267 contracts against an explicitly configured disposable lab."""
import argparse
import importlib.util
import json
import hashlib
from pathlib import Path
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("feedback_proof", ROOT / "scripts/prove-feedback-loop.py")
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


def require(value, message):
    if not value:
        raise ValueError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lab-config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-only", action="store_true", help="independent source/cancellation checks only")
    args = parser.parse_args()
    lab = json.loads(args.lab_config.read_text())
    args.output.mkdir(parents=True, exist_ok=False)
    organization, project = str(uuid.uuid4()), str(uuid.uuid4())
    client = ROOT / "tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll"
    run_host = ROOT / "tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host"
    common = ["--url", lab["url"], "--organization", organization, "--project", project,
              "--token-file", str(Path(lab["root"]) / "token"), "--request-timeout-seconds", "10"]
    receipt = {"schema_version": 1, "organization": organization, "project": project,
               "passed": False, "scope": "source/cancellation only" if args.source_only else "all contracts", "runs": [], "controls": [],
               "client_sha256": proof.digest(client), "harness_sha256": proof.digest(__file__),
               "timing_scope": "monotonic client-process observations from admission start; typed diagnostics only"}

    def invoke(label, command, options, expected=0, origin=None, authenticated=True, seconds=20):
        start = time.monotonic() if origin is None else origin
        code, rows = proof.capture(["dotnet", str(client), command] +
                                  (common if authenticated else []) + options,
                                  time.monotonic() + seconds, start, args.output, label)
        require(code == expected if isinstance(expected, int) else code in expected,
                f"{label}: expected exit {expected}, observed {code}")
        invoke.last_exit_code = code
        return rows

    def submit(label, path, snapshot=False, key=None, expected=0):
        origin = time.monotonic()
        key = key or f"fg-contract-{label}-{uuid.uuid4()}"
        rows = invoke(label + "-submit", "submit",
                      ["--snapshot" if snapshot else "--pipeline", str(path), "--idempotency-key", key],
                      expected=expected, origin=origin)
        return (rows[0]["record"] if rows else None), origin, key

    def watch(label, admission, origin, expected, status):
        pages = invoke(label + "-watch", "watch", ["--build", admission["build_id"],
                       "--watch-timeout-seconds", "30", "--poll-interval-ms", "100"],
                       expected=expected, origin=origin, seconds=40)
        require(pages and (pages[-1]["record"]["status"] == status if isinstance(status, str) else
                pages[-1]["record"]["status"] in status), label + ": unexpected final status")
        status = pages[-1]["record"]["status"]
        if status != "reconciliation_required":
            require(pages[-1]["record"]["is_terminal"] and not pages[-1]["record"]["has_more"],
                    label + ": terminal evidence not drained")
        diagnostics = [{"observed_ms": p["observed_ms"], "diagnostic_id": c["diagnostic_id"],
                        "diagnostic": c["diagnostic"]} for p in pages for c in p["record"]["chunks"]
                       if c.get("diagnostic") is not None]
        run = {"label": label, "admission": admission, "watch_exit": invoke.last_exit_code, "status": status,
               "terminal_or_reconciliation_ms": pages[-1]["observed_ms"], "pages": pages,
               "first_actionable_diagnostic_ms": diagnostics[0]["observed_ms"] if diagnostics else None,
               "diagnostics": diagnostics}
        receipt["runs"].append(run)
        return run

    def control(name, passed):
        require(passed, name)
        receipt["controls"].append({"name": name, "passed": True})

    try:
        # UUID-only SQL; no credential value enters argv, receipt or stdout.
        sql = (f"INSERT INTO organizations(id,slug) VALUES('{organization}','contracts-{organization}');"
               f"INSERT INTO projects(id,organization_id,slug) VALUES('{project}','{organization}','contracts');")
        subprocess.run(["podman", "exec", "-i", lab["container"], "psql", "-X", "-q", "-U", "fogell",
                        "-d", lab["database"], "-v", "ON_ERROR_STOP=1"], input=sql.encode(),
                       check=True, timeout=15, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if not args.source_only:
            m1 = subprocess.run(["python3", str(ROOT / "scripts/prove-feedback-loop.py")] + common +
                                ["--output", str(args.output / "m1"), "--controller-identity",
                                 "integrated uncommitted FG266/267/268/269 deployment; root campaign manifest",
                                 "--controller-binary", str(ROOT / "src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host.dll"),
                                 "--environment", "separate contract tenant; shared isolated pilot controller; not latency campaign"],
                                capture_output=True, text=True, timeout=100)
            (args.output / "m1-command.log").write_text(m1.stdout + m1.stderr)
            require(m1.returncode == 0, "integrated M1 proof failed")

            admitted, origin, _ = submit("shell", ROOT / "scripts/prove-diagnostics/shell.Jenkinsfile")
            shell = watch("shell", admitted, origin, 1, "failure")
            control("shell_exit7_typed_diagnostic", any(d["diagnostic"]["category"] == "workload_step"
                and d["diagnostic"]["exit_code"] == 7 and d["diagnostic"]["stage"] == "Shell failure"
                for d in shell["diagnostics"]))

            admitted, origin, _ = submit("junit", ROOT / "scripts/prove-diagnostics/tests.Jenkinsfile")
            junit = watch("junit", admitted, origin, 1, "unstable")
            control("junit_test_identity_and_source", any(d["diagnostic"]["category"] == "test"
                and d["diagnostic"]["test_name"] == "planted_failure"
                and d["diagnostic"]["source_path"] == "tests/example.fs"
                and d["diagnostic"]["source_line"] == 23 for d in junit["diagnostics"]))

        tool = invoke("tool-identity", "tool-identity", ["--run-host", str(run_host)], authenticated=False)[0]["record"]
        receipt["tool_identity"] = tool
        candidate = args.output / "candidate"
        candidate.mkdir()
        marker = "FG267_REPRODUCTION_ORIGINAL"
        (candidate / "message.txt").write_text(marker + "\n")
        (candidate / "inventory.txt").write_text("message.txt\n")
        pipeline = candidate / "Jenkinsfile"
        pipeline.write_text("pipeline {\n  agent any\n  stages {\n    stage('Reproduce') {\n      steps {\n        sh 'cat message.txt; sleep 1'\n      }\n    }\n  }\n}\n")
        bundle = args.output / "candidate.snapshot.json"
        invoke("snapshot-pack", "snapshot", ["--pipeline", str(pipeline), "--source-root", str(candidate),
               "--files-from", str(candidate / "inventory.txt"), "--output", str(bundle),
               "--parent-loop", str(uuid.uuid4()), "--tool-sha256", tool["run_host_sha256"]], authenticated=False)
        admitted, origin, key = submit("snapshot", bundle, snapshot=True)
        replay, _, _ = submit("exact-replay", bundle, snapshot=True, key=key)
        control("exact_snapshot_replay_same_identity", replay["was_existing"] is True and
                replay["build_id"] == admitted["build_id"] and replay["attempt_id"] == admitted["attempt_id"])
        source_run = watch("snapshot", admitted, origin, 0, "success")
        changed = json.loads(bundle.read_text())
        changed["parent_loop_id"] = str(uuid.uuid4())
        changed_file = args.output / "changed.snapshot.json"
        changed_file.write_text(json.dumps(changed, separators=(",", ":")))
        submit("changed-manifest", changed_file, snapshot=True, key=key, expected=3)
        control("changed_manifest_conflicts", "idempotency_conflict" in
                (args.output / "changed-manifest-submit.stderr").read_text())

        invoke("source-download", "source", ["--build", admitted["build_id"], "--max-response-bytes", "25165824"])
        downloaded = args.output / "source-download.ndjson"
        control("download_is_original_bytes", downloaded.read_bytes() == bundle.read_bytes())
        (candidate / "message.txt").write_text("LOCAL_SOURCE_MUTATED_AFTER_PACK\n")
        reproduced, origin, _ = submit("reproduced", downloaded, snapshot=True)
        reproduction = watch("reproduced", reproduced, origin, 0, "success")
        original_identity = source_run["pages"][-1]["record"]["source_identity"]
        reproduced_identity = reproduction["pages"][-1]["record"]["source_identity"]
        control("fresh_build_reproduction", reproduced["build_id"] != admitted["build_id"] and
                reproduced["attempt_id"] != admitted["attempt_id"])
        for name in ("snapshot_sha256", "pipeline_sha256", "manifest_sha256", "run_host_sha256"):
            control("reproduction_" + name, original_identity[name] == reproduced_identity[name])
        for run in (source_run, reproduction):
            control(run["label"] + "_verified_original_output",
                    run["pages"][-1]["record"]["source_identity"]["verification_state"] == "verified"
                    and any(c["body"] == marker for p in run["pages"] for c in p["record"]["chunks"]))

        bad = json.loads(bundle.read_text())
        bad["expected_tool_sha256"] = "0" * 64
        bad_file = args.output / "wrong-tool.snapshot.json"
        bad_file.write_text(json.dumps(bad, separators=(",", ":")))
        for label, snapshot in (("wrong-tool", True),):
            wrong, origin, _ = submit(label, bad_file, snapshot=snapshot)
            run = watch(label, wrong, origin, 5, "reconciliation_required")
            control(label + "_infrastructure_diagnostic", any(d["diagnostic"]["category"] == "infrastructure"
                    and d["diagnostic"]["result"] == "reconciliation_required" for d in run["diagnostics"]))

        submit("raw-snapshot-transport", bad_file, snapshot=False, expected=3)
        control("raw_snapshot_not_executed_as_pipeline", "HTTP 422" in
                (args.output / "raw-snapshot-transport-submit.stderr").read_text())

        unpinned = json.loads(bundle.read_text())
        unpinned["expected_tool_sha256"] = None
        unpinned_file = args.output / "unpinned.snapshot.json"
        unpinned_file.write_text(json.dumps(unpinned, separators=(",", ":")))
        unpinned_admission, origin, _ = submit("unpinned", unpinned_file, snapshot=True)
        unpinned_run = watch("unpinned", unpinned_admission, origin, 0, "success")
        identity = unpinned_run["pages"][-1]["record"]["source_identity"]
        control("optional_missing_pin_not_invented", identity["expected_tool_sha256"] is None and
                identity["run_host_sha256"] == tool["run_host_sha256"])

        cancel_pipeline = args.output / "cancel.Jenkinsfile"
        cancel_pipeline.write_text("pipeline {\n  agent any\n  stages {\n    stage('Cancelable') {\n      steps {\n        sh 'sleep 30'\n      }\n    }\n  }\n}\n")
        cancel, origin, _ = submit("cancel", cancel_pipeline)
        deadline = time.monotonic() + 10
        running = False
        while time.monotonic() < deadline:
            rows = invoke("cancel-status", "status", ["--build", cancel["build_id"]])
            if rows[0]["record"]["status"] == "running":
                running = True
                break
            time.sleep(.1)
        require(running, "cancel fixture did not start within bound")
        invoke("cancel-request", "cancel", ["--build", cancel["build_id"]])
        cancelled = watch("cancel", cancel, origin, (1, 5), ("aborted", "reconciliation_required"))
        control("explicit_cancellation_preserves_authority",
                cancelled["pages"][-1]["record"]["cancellation_requested"] is True and
                ((cancelled["status"] == "aborted" and cancelled["watch_exit"] == 1) or
                 (cancelled["status"] == "reconciliation_required" and cancelled["watch_exit"] == 5 and
                  any(d["diagnostic"]["message"] == "build_cancelled" for d in cancelled["diagnostics"]))))
        receipt["passed"] = True
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps({"passed": True, "receipt": str(args.output / "receipt.json"),
                      "runs": len(receipt["runs"]), "controls": len(receipt["controls"])}))


if __name__ == "__main__":
    main()
