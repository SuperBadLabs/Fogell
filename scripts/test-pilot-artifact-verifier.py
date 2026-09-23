#!/usr/bin/env python3
"""Deterministic synthetic controls for FG-269's saved pilot receipt checker.

This exercises verification only. It never contacts or provisions a controller,
executes a workload, or claims real pilot/recovery evidence.
"""
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import uuid

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("pilot", ROOT / "scripts/prove-self-hosted-pilot.py")
pilot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pilot)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def fixture(root):
    tool = "a" * 64
    receipt = {"schema_version": 1, "passed": True,
               "declaration": {"loops": 30, "expected_test_count": 1, "tool_sha256": tool, "source_files": {}},
               "runs": [], "loop_durations_ms": [80.0] * 30, "unexpected_failures": 0,
               "censored_runs": 0, "elapsed_seconds": 2.4, "retention_phases": []}
    for index in range(60):
        loop = index // 2 + 1
        kind = "failed" if index % 2 == 0 else "corrected"
        parent = str(uuid.uuid5(uuid.NAMESPACE_URL, "synthetic-loop-" + str(loop)))
        build = str(uuid.uuid5(uuid.NAMESPACE_URL, "synthetic-build-" + str(index)))
        attempt = str(uuid.uuid5(uuid.NAMESPACE_URL, "synthetic-attempt-" + str(index)))
        directory = root / f"loop-{loop:02d}-{kind}"
        directory.mkdir()
        content, pipeline = (kind + "-source").encode(), b"synthetic pipeline input"
        file_sha = sha(content)
        tree = sha(("input.txt\0" + file_sha + "\0" + "0\n").encode())
        receipt["declaration"]["source_files"][kind] = {"input.txt": file_sha}
        envelope = {"schema_version": 1, "kind": "fogell.source_snapshot", "snapshot_sha256": tree,
                    "pipeline_sha256": sha(pipeline), "parent_loop_id": parent, "expected_tool_sha256": tool,
                    "pipeline_base64": base64.b64encode(pipeline).decode(),
                    "environment": {"os": "linux", "architecture": "x64", "dotnet_major": 10},
                    "files": [{"path": "input.txt", "sha256": file_sha,
                               "content_base64": base64.b64encode(content).decode(), "executable": False}]}
        bundle = json.dumps(envelope).encode()
        (directory / "source.json").write_bytes(bundle)
        summary = {"snapshot_sha256": tree, "pipeline_sha256": sha(pipeline),
                   "manifest_sha256": sha(bundle), "parent_loop_id": parent}
        (directory / "snapshot.ndjson").write_text(json.dumps(summary) + "\n")
        xml = ('<testsuite><testcase name="' + pilot.TEST + '">' +
               ('<failure/>' if kind == "failed" else '') + '</testcase></testsuite>').encode()
        (directory / "domain.xml").write_bytes(xml)
        sdk = b"10.0.301\n"
        (directory / "sdk.txt").write_bytes(sdk)
        identity = dict(summary, kind="content_snapshot", schema_version=1, verification_state="verified",
                        expected_tool_sha256=tool, run_host_sha256=tool, attempt_id=attempt)
        diagnostic = {"schema_version": 1, "category": "test", "result": "failure", "test_name": pilot.TEST}
        chunk = {"sequence": 0, "body": "output", "truncated": False,
                 "diagnostic": diagnostic if kind == "failed" else None}
        progress = {"schema_version": 1, "build_id": build, "from_sequence": 0, "next_sequence": 1,
                    "status": "running", "is_terminal": False, "has_more": False,
                    "cancellation_requested": False, "truncated": False, "chunks": [chunk], "source_identity": identity}
        terminal = dict(progress, from_sequence=1, next_sequence=1, chunks=[], is_terminal=True,
                        status="unstable" if kind == "failed" else "success")
        receipt["runs"].append({"kind": kind, "passed": True, "submit_exit": 0,
            "watch_exit": 1 if kind == "failed" else 0, "idempotency_key": "synthetic-key-" + str(index),
            "admission": {"build_id": build, "attempt_id": attempt, "was_existing": False},
            "admission_ms": 5, "parent_loop_id": parent, "snapshot_digest": tree, "tool_sha256": tool,
            "pipeline_sha256": sha(pipeline), "manifest_sha256": sha(bundle), "test_count": 1,
            "failed_count": 1 if kind == "failed" else 0, "skipped_count": 0,
            "junit_sha256": sha(xml), "sdk_sha256": sha(sdk), "first_actionable_diagnostic_ms": 10 if kind == "failed" else None,
            "terminal_observed_ms": 20, "pages": [{"observed_ms": 10, "record": progress}, {"observed_ms": 20, "record": terminal}]})
    for loop in range(5, 31, 5):
        receipt["retention_phases"].append({"after_loop": loop, "passed": True,
            "physical_bytes_before": 100, "physical_bytes_after": 50,
            "sweeps": [{"Held": 0, "Pending": 0, "Selected": 0, "Operations": 1, "ReclaimableBytesBefore": 10}]})
    return receipt


def check(path, receipt):
    pilot.validate(receipt)
    pilot.verify_artifacts(path, receipt)


def main():
    results = []
    with tempfile.TemporaryDirectory(prefix="fogell-pilot-verifier-test-") as scratch:
        root = Path(scratch)
        path = root / "receipt.json"
        receipt = fixture(root)
        check(path, receipt)
        def final(value):
            return value["runs"][0]["pages"][-1]["record"]
        mutations = {
            "false_failure_success": lambda r: final(r).update(status="success"),
            "false_successful_exit": lambda r: r["runs"][0].update(watch_exit=0),
            "unverified_source": lambda r: final(r)["source_identity"].update(verification_state="pending"),
            "wrong_snapshot": lambda r: final(r)["source_identity"].update(snapshot_sha256="b" * 64),
            "wrong_manifest": lambda r: final(r)["source_identity"].update(manifest_sha256="b" * 64),
            "wrong_pipeline": lambda r: final(r)["source_identity"].update(pipeline_sha256="b" * 64),
            "wrong_observed_tool": lambda r: final(r)["source_identity"].update(run_host_sha256="b" * 64),
            "unbound_tool": lambda r: final(r)["source_identity"].update(expected_tool_sha256=None),
            "wrong_build": lambda r: final(r).update(build_id=str(uuid.uuid4())),
            "unlinked_correction": lambda r: r["runs"][1].update(parent_loop_id=str(uuid.uuid4())),
            "reused_key": lambda r: r["runs"][1].update(idempotency_key=r["runs"][0]["idempotency_key"]),
            "reused_admission": lambda r: r["runs"][0]["admission"].update(was_existing=True),
            "missing_diagnostic": lambda r: r["runs"][0]["pages"][0]["record"]["chunks"][0].update(diagnostic=None),
            "successful_diagnostic": lambda r: r["runs"][0]["pages"][0]["record"]["chunks"][0]["diagnostic"].update(result="success"),
            "nonfinite_observation": lambda r: r["runs"][0]["pages"][0].update(observed_ms=float("nan")),
            "regressed_observation": lambda r: r["runs"][0]["pages"][-1].update(observed_ms=1),
            "cursor_gap": lambda r: final(r).update(from_sequence=100),
            "undrained_terminal": lambda r: final(r).update(has_more=True),
            "truncated_evidence": lambda r: r["runs"][0]["pages"][0]["record"].update(truncated=True),
            "missing_iterations": lambda r: r["runs"].pop(),
            "censored_hidden": lambda r: r.update(censored_runs=1),
            "nonfinite_latency": lambda r: r["loop_durations_ms"].__setitem__(0, float("nan")),
            "impossible_latency": lambda r: r.update(loop_durations_ms=[1.0] * 30),
            "latency_target_exceeded": lambda r: r.update(loop_durations_ms=[240001.0] * 30),
            "retention_missing": lambda r: r["retention_phases"].pop(),
            "retention_no_capacity_recovered": lambda r: r["retention_phases"][0].update(physical_bytes_after=100),
            "retention_still_pending": lambda r: r["retention_phases"][0]["sweeps"][-1].update(Pending=1),
            "wrong_test_count": lambda r: r["runs"][0].update(test_count=2),
            "wrong_failure_count": lambda r: r["runs"][0].update(failed_count=0),
            "wrong_artifact_sdk_hash": lambda r: r["runs"][0].update(sdk_sha256="b" * 64),
            "wrong_declared_source": lambda r: r["declaration"]["source_files"]["failed"].update({"input.txt": "b" * 64}),
        }
        for name, mutate in mutations.items():
            broken = copy.deepcopy(receipt)
            mutate(broken)
            try:
                check(path, broken)
            except (ValueError, KeyError, TypeError):
                results.append({"control": name, "rejected": True})
            else:
                raise AssertionError("known-bad receipt accepted: " + name)
        for name in ("domain.xml", "sdk.txt", "source.json", "snapshot.ndjson"):
            artifact = root / "loop-01-failed" / name
            original = artifact.read_bytes()
            artifact.write_bytes(original + b"altered")
            try:
                check(path, receipt)
            except (ValueError, KeyError, TypeError):
                results.append({"control": "altered-" + name, "rejected": True})
            else:
                raise AssertionError("known-bad retained artifact accepted: " + name)
            finally:
                artifact.write_bytes(original)
    print(json.dumps({"passed": True, "synthetic_checker_controls": True,
                      "real_controller_proof": False, "controls": results}))


if __name__ == "__main__":
    main()
