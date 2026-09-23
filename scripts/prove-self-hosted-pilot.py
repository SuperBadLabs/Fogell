#!/usr/bin/env python3
"""FG-269 sustained source -> build/test -> typed diagnostic -> correction proof.

Runs against an explicitly supplied disposable controller. Provisioning, recovery
and retention are separate measured phases; this receipt alone is not FG-269 DONE.
"""
import argparse
import copy
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time
import urllib.request
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("feedback_proof", ROOT / "scripts/prove-feedback-loop.py")
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)
require = proof.require
digest = proof.digest
TEST = "wire round-trip is total over the type"
ORIGINAL = "Expect.equal (BuildStatus.ofWireString (BuildStatus.toWireString a)) (Some a)"
PLANTED = "Expect.equal (BuildStatus.ofWireString (BuildStatus.toWireString a)) (Some NotBuilt)"


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")
    temporary.replace(path)


def percentile(values, percent):
    require(bool(values), "cannot calculate percentile with no observations")
    return sorted(values)[math.ceil(len(values) * percent / 100) - 1]


def physical_bytes(root):
    files = [p for p in root.rglob("*") if p.is_file() and not p.is_symlink()]
    require(len(files) <= 100000, "state inventory exceeds proof bound")
    return sum(p.stat().st_size for p in files)


def source_tree(target, cache, failed):
    target.mkdir()
    files = [ROOT / "Directory.Build.props", ROOT / "global.json"]
    for relative in ("src/Fogell.Domain", "tests/Fogell.Domain.Tests", "scripts/self-hosted-pilot"):
        files.extend(p for p in (ROOT / relative).iterdir() if p.is_file())
    for source in sorted(files):
        require(not source.is_symlink(), "source inventory contains a symlink")
        dest = target / source.relative_to(ROOT)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, dest)
    test_file = target / "tests/Fogell.Domain.Tests/Tests.fs"
    original = test_file.read_text()
    require(original.count(ORIGINAL) == 1, "planted assertion no longer has an exact source match")
    if failed:
        test_file.write_text(original.replace(ORIGINAL, PLANTED))
    (target / "pilot-cache.txt").write_text(str(cache) + "\n")
    (target / "pilot-nuget.config").write_text(
        '<configuration><packageSources><clear /></packageSources></configuration>\n')
    inventory = sorted(str(p.relative_to(target)) for p in target.rglob("*") if p.is_file())
    return inventory


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError("artifact endpoint redirected")


def artifact(args, admission, name, output):
    url = (f"{args.url}/api/v1/organizations/{args.organization}/projects/{args.project}"
           f"/builds/{admission['build_id']}/attempts/{admission['attempt_id']}/artifacts/{name}")
    token = args.token_file.read_bytes()
    require(0 < len(token) <= 4096, "invalid token file length")
    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + token.decode().strip()})
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=10) as response:
        require(response.status == 200, "artifact did not return 200")
        data = response.read(2 * 1024 * 1024 + 1)
    require(len(data) <= 2 * 1024 * 1024, "artifact exceeds proof bound")
    output.write_bytes(data)
    return data


def failed_test_diagnostics(pages):
    found = []
    for page in pages:
        for chunk in page["record"]["chunks"]:
            diagnostic = chunk.get("diagnostic")
            if (diagnostic and diagnostic.get("schema_version") == 1 and diagnostic.get("category") == "test"
                    and diagnostic.get("result") in {"failure", "error"}
                    and TEST in (diagnostic.get("test_name") or "")):
                found.append((page["observed_ms"], diagnostic))
    return found


def validate_run(run, expected_count):
    require(run.get("passed") is True and not run.get("error"), "run did not complete")
    expected = "unstable" if run["kind"] == "failed" else "success"
    require(run["submit_exit"] == 0, "submission failed")
    require(run["watch_exit"] == (1 if expected == "unstable" else 0), "incorrect watch exit")
    require(run["admission"].get("was_existing") is False, "build reused admission")
    pages = run["pages"]
    require(bool(pages), "no feedback pages")
    cursor, previous_ms = 0, 0.0
    for page in pages:
        record = page["record"]
        timestamp = page["observed_ms"]
        require(math.isfinite(timestamp) and timestamp >= previous_ms, "invalid timestamp")
        previous_ms = timestamp
        require(record["build_id"] == run["admission"]["build_id"], "wrong build identity")
        require(record["schema_version"] == 1, "unknown feedback schema")
        require(record["from_sequence"] == cursor, "cursor gap")
        require(record["status"] in {"queued", "running", "not_built", "unstable", "success"}, "unexpected result")
        require(record["is_terminal"] == (record["status"] in {"unstable", "success"}), "terminal flag mismatch")
        require(record["cancellation_requested"] is False, "unexpected cancellation")
        require(type(record["has_more"]) is bool and record["truncated"] is False, "invalid bounded feedback")
        for chunk in record["chunks"]:
            require(chunk["sequence"] == cursor and chunk["truncated"] is False, "missing or truncated evidence")
            cursor += 1
        require(record["next_sequence"] == cursor, "next cursor differs")
    final = pages[-1]["record"]
    require(final["status"] == expected and final["is_terminal"] is True and final["has_more"] is False,
            "wrong or undrained final result")
    identity = final.get("source_identity")
    require(identity is not None and identity["verification_state"] == "verified", "source was not verified")
    require(identity["parent_loop_id"] == run["parent_loop_id"], "source correction lineage differs")
    require(identity["snapshot_sha256"] == run["snapshot_digest"], "executed different snapshot")
    require(identity["pipeline_sha256"] == run["pipeline_sha256"], "executed different pipeline")
    require(identity["manifest_sha256"] == run["manifest_sha256"], "executed different submission manifest")
    require(identity["expected_tool_sha256"] == run["tool_sha256"], "manifest did not bind the expected tool")
    require(identity["run_host_sha256"] == run["tool_sha256"], "worker tool differs from pinned input")
    require(run["test_count"] == expected_count and run["skipped_count"] == 0, "missing/skipped tests")
    require(run["failed_count"] == (1 if expected == "unstable" else 0), "unexpected test failures")
    if expected == "unstable":
        named = [(ms, d) for ms, d in failed_test_diagnostics(pages) if TEST in json.dumps(d)]
        require(bool(named), "planted failed test absent from typed diagnostics")
        require(run["first_actionable_diagnostic_ms"] == named[0][0], "diagnostic latency differs")
    require(run["terminal_observed_ms"] == pages[-1]["observed_ms"], "terminal latency differs")


def validate(receipt):
    require(receipt["schema_version"] == 1 and receipt.get("passed") is True, "campaign did not pass")
    declaration = receipt["declaration"]
    require(declaration["loops"] >= 30, "sustained campaign has fewer than 30 loops")
    runs = receipt["runs"]
    require(len(runs) == 2 * declaration["loops"], "campaign omitted observations")
    ids, keys = set(), set()
    for index, run in enumerate(runs):
        require(run["kind"] == ("failed" if index % 2 == 0 else "corrected"), "wrong correction order")
        validate_run(run, declaration["expected_test_count"])
        identity = run["admission"]["build_id"]
        require(identity not in ids and run["idempotency_key"] not in keys, "duplicate build/key")
        ids.add(identity)
        keys.add(run["idempotency_key"])
        if index % 2:
            require(run["parent_loop_id"] == runs[index - 1]["parent_loop_id"], "unlinked correction")
            require(run["snapshot_digest"] != runs[index - 1]["snapshot_digest"], "correction changed no inputs")
    loop_times = receipt["loop_durations_ms"]
    require(len(loop_times) == declaration["loops"] and all(math.isfinite(v) and v > 0 for v in loop_times),
            "missing measured correction loop duration")
    require(all(duration >= runs[2*i]["terminal_observed_ms"] + runs[2*i+1]["terminal_observed_ms"]
                for i, duration in enumerate(loop_times)), "loop duration excludes measured submissions")
    require(percentile(loop_times, 95) <= 240000, "p95 loop acceptance target exceeded")
    require(receipt["unexpected_failures"] == 0 and receipt["censored_runs"] == 0, "failures omitted")
    sweeps = receipt["retention_phases"]
    require(len(sweeps) == declaration["loops"] // 5, "missing declared retention phases")
    for phase in sweeps:
        require(phase["passed"] is True and len(phase["sweeps"]) <= 20, "retention did not finish within sweep bound")
        last = phase["sweeps"][-1]
        require(last["Held"] == 0 and last["Pending"] == 0 and last["Selected"] == 0, "retention did not converge")
        require(last["ReclaimableBytesBefore"] <= 67108864, "logical retention capacity exceeded")
        require(phase["physical_bytes_after"] < phase["physical_bytes_before"], "retention recovered no physical capacity")
        require(phase["physical_bytes_after"] <= 201326592, "state exceeds declared physical pilot ceiling")
    return {"loops": len(loop_times), "loop_p50_ms": percentile(loop_times, 50),
            "loop_p95_ms": percentile(loop_times, 95),
            "diagnostic_p50_ms": percentile([r["first_actionable_diagnostic_ms"] for r in runs[::2]], 50),
            "diagnostic_p95_ms": percentile([r["first_actionable_diagnostic_ms"] for r in runs[::2]], 95)}


def verify_artifacts(receipt_path, receipt):
    """Rebind saved summaries to retained artifact and admitted source bytes."""
    import base64
    base = receipt_path.resolve().parent
    declaration = receipt["declaration"]
    for index, run in enumerate(receipt["runs"]):
        kind = "failed" if index % 2 == 0 else "corrected"
        directory = base / f"loop-{index // 2 + 1:02d}-{kind}"
        require(directory.is_dir() and not directory.is_symlink(), "retained run directory is missing or linked")
        def bounded(name, limit):
            path = directory / name
            require(path.is_file() and not path.is_symlink(), "retained artifact missing or linked: " + name)
            require(path.stat().st_size <= limit, "retained artifact exceeds bound: " + name)
            with path.open("rb") as stream:
                data = stream.read(limit + 1)
            require(len(data) <= limit, "retained artifact grew past bound: " + name)
            return data
        junit = bounded("domain.xml", 2 * 1024 * 1024)
        require(hashlib.sha256(junit).hexdigest() == run["junit_sha256"], "retained JUnit digest differs")
        cases = list(ET.fromstring(junit).iter("testcase"))
        failed = [c for c in cases if c.find("failure") is not None or c.find("error") is not None]
        skipped = [c for c in cases if c.find("skipped") is not None]
        require(len(cases) == run["test_count"] == declaration["expected_test_count"], "retained test count differs")
        require(len(failed) == run["failed_count"] == (1 if kind == "failed" else 0), "retained failure count differs")
        require(len(skipped) == run["skipped_count"] == 0, "retained skipped count differs")
        if kind == "failed":
            require(TEST in failed[0].get("name", ""), "retained failure is not the planted test")
        sdk = bounded("sdk.txt", 4096)
        require(hashlib.sha256(sdk).hexdigest() == run["sdk_sha256"] and sdk.strip() == b"10.0.301", "retained SDK identity differs")
        packaged = bounded("source.json", 24 * 1024 * 1024)
        envelope = json.loads(packaged)
        require(envelope["schema_version"] == 1 and envelope["kind"] == "fogell.source_snapshot", "retained source schema differs")
        require(hashlib.sha256(packaged).hexdigest() == run["manifest_sha256"], "retained manifest digest differs")
        summaries = [json.loads(line) for line in bounded("snapshot.ndjson", 16384).splitlines() if line.strip()]
        require(len(summaries) == 1, "retained snapshot summary count differs")
        summary = summaries[0]
        for name, recorded in (("snapshot_sha256", run["snapshot_digest"]), ("pipeline_sha256", run["pipeline_sha256"]),
                               ("manifest_sha256", run["manifest_sha256"]), ("parent_loop_id", run["parent_loop_id"])):
            require(summary[name] == recorded, "snapshot summary differs: " + name)
            if name != "manifest_sha256":
                require(envelope[name] == recorded, "admitted source differs: " + name)
        require(envelope["expected_tool_sha256"] == run["tool_sha256"] == declaration["tool_sha256"], "retained tool pin differs")
        pipeline = base64.b64decode(envelope["pipeline_base64"], validate=True)
        require(hashlib.sha256(pipeline).hexdigest() == run["pipeline_sha256"], "retained pipeline bytes differ")
        require(0 < len(envelope["files"]) <= 4096, "retained inventory outside bound")
        selected, total = {}, len(pipeline)
        for item in envelope["files"]:
            require(item["path"] not in selected and item["executable"] is False, "retained source mode/path differs")
            content = base64.b64decode(item["content_base64"], validate=True)
            total += len(content)
            require(total <= 16 * 1024 * 1024, "retained source exceeds byte bound")
            require(hashlib.sha256(content).hexdigest() == item["sha256"], "retained source file digest differs")
            selected[item["path"]] = item["sha256"]
        require(selected == declaration["source_files"][kind], "retained source differs from predeclared inventory")
        tree = "".join(path + "\0" + selected[path] + "\0" + "0\n" for path in sorted(selected))
        require(hashlib.sha256(tree.encode()).hexdigest() == run["snapshot_digest"], "retained tree identity differs")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify", type=Path)
    parser.add_argument("--url")
    parser.add_argument("--organization")
    parser.add_argument("--project")
    parser.add_argument("--token-file", type=Path)
    parser.add_argument("--cache", type=Path)
    parser.add_argument("--work", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--state-root", type=Path)
    parser.add_argument("--deployment-declaration", type=Path)
    parser.add_argument("--retention-command", type=Path)
    parser.add_argument("--tool-sha256")
    parser.add_argument("--expected-test-count", type=int, default=41)
    parser.add_argument("--loops", type=int, default=30)
    parser.add_argument("--client", type=Path, default=ROOT / "tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll")
    args = parser.parse_args()
    if args.verify:
        saved = json.loads(args.verify.read_text())
        verify_artifacts(args.verify, saved)
        print(json.dumps(validate(saved)))
        return
    for name in ("url", "organization", "project", "token_file", "cache", "work", "output", "state_root",
                 "deployment_declaration", "retention_command", "tool_sha256"):
        require(getattr(args, name) is not None, "missing --" + name.replace("_", "-"))
    require(args.url.startswith("http://127.0.0.1:"), "pilot requires explicit local disposable deployment")
    require(1 <= args.loops <= 100, "loops must be bounded from 1 through 100")
    args.output.mkdir()
    args.work.mkdir()
    trees = {}
    inventories = {}
    for kind in ("failed", "corrected"):
        trees[kind] = args.work / kind
        files = source_tree(trees[kind], args.cache, kind == "failed")
        inventories[kind] = args.work / (kind + ".files")
        inventories[kind].write_text("\n".join(files) + "\n")
    declaration = {"profile_version": 1, "condition": "fresh-workspace-warm-locked-nuget",
                   "loops": args.loops, "expected_test_count": args.expected_test_count,
                   "concurrency": 1, "build_deadline_seconds": 120, "campaign_deadline_seconds": 5400,
                   "platform": platform.platform(), "cpu_count": os.cpu_count(),
                   "cpuinfo_sha256": digest(Path("/proc/cpuinfo")),
                   "meminfo": Path("/proc/meminfo").read_text().splitlines()[:2],
                   "client_sha256": digest(args.client), "harness_sha256": digest(Path(__file__)),
                   "tool_sha256": args.tool_sha256,
                   "deployment": json.loads(args.deployment_declaration.read_text()),
                   "retention_command_sha256": digest(args.retention_command),
                   "retention_policy": {"keep_builds": 2, "max_bytes": 67108864, "max_age_seconds": 86400,
                                        "max_operations_per_sweep": 1024, "max_sweeps_per_phase": 20},
                   "source_files": {kind: {p: digest(trees[kind] / p) for p in inventories[kind].read_text().splitlines()}
                                    for kind in trees},
                   "package_files": {str(p.relative_to(args.cache)): digest(p) for p in args.cache.rglob("*") if p.is_file()}}
    write_json(args.output / "declaration.json", declaration)
    receipt = {"schema_version": 1, "passed": False, "declaration": declaration,
               "runs": [], "loop_durations_ms": [], "retention_phases": [], "unexpected_failures": 0, "censored_runs": 0}
    retention_command = json.loads(args.retention_command.read_text())
    require(isinstance(retention_command, list) and all(isinstance(p, str) for p in retention_command), "invalid retention command")
    common = ["--url", args.url, "--organization", args.organization, "--project", args.project,
              "--token-file", str(args.token_file)]
    client = ["dotnet", str(args.client)]
    campaign_start = time.monotonic()
    try:
        for loop in range(args.loops):
            loop_start = time.monotonic()
            parent = str(uuid.uuid4())
            for kind in ("failed", "corrected"):
                require(time.monotonic() - campaign_start < 5400, "campaign deadline exceeded")
                directory = args.output / f"loop-{loop + 1:02d}-{kind}"
                directory.mkdir()
                bundle = args.work / f"loop-{loop + 1:02d}-{kind}.json"
                origin = time.monotonic()
                rc, snapshots = proof.capture(client + ["snapshot", "--pipeline", str(trees[kind] / "scripts/self-hosted-pilot/Jenkinsfile"),
                    "--source-root", str(trees[kind]), "--files-from", str(inventories[kind]),
                    "--output", str(bundle), "--parent-loop", parent, "--tool-sha256", args.tool_sha256], origin + 30, origin, directory, "snapshot")
                require(rc == 0 and len(snapshots) == 1, "snapshot packaging failed")
                shutil.copyfile(bundle, directory / "source.json")
                run = {"kind": kind, "parent_loop_id": parent, "idempotency_key": str(uuid.uuid4()),
                       "snapshot_digest": snapshots[0]["record"]["snapshot_sha256"],
                       "pipeline_sha256": snapshots[0]["record"]["pipeline_sha256"],
                       "manifest_sha256": snapshots[0]["record"]["manifest_sha256"],
                       "tool_sha256": args.tool_sha256, "passed": False}
                receipt["runs"].append(run)
                origin = time.monotonic()
                deadline = origin + 120
                rc, admitted = proof.capture(client + ["submit"] + common + ["--snapshot", str(bundle),
                    "--idempotency-key", run["idempotency_key"]], deadline, origin, directory, "submit")
                run["submit_exit"] = rc
                require(rc == 0 and len(admitted) == 1, "submission failed")
                run["admission"] = admitted[0]["record"]
                run["admission_ms"] = admitted[0]["observed_ms"]
                rc, pages = proof.capture(client + ["watch"] + common + ["--build", run["admission"]["build_id"],
                    "--poll-interval-ms", "100", "--watch-timeout-seconds", "115"], deadline, origin, directory, "watch")
                run.update(watch_exit=rc, pages=pages)
                require(bool(pages), "watch produced no pages")
                run["terminal_observed_ms"] = pages[-1]["observed_ms"]
                run["first_output_ms"] = next((p["observed_ms"] for p in pages if p["record"]["chunks"]), None)
                diagnostics = [(ms, d) for ms, d in failed_test_diagnostics(pages) if TEST in json.dumps(d)]
                run["first_actionable_diagnostic_ms"] = diagnostics[0][0] if diagnostics else None
                data = artifact(args, run["admission"], "reports/domain.xml", directory / "domain.xml")
                xml = ET.fromstring(data)
                cases = list(xml.iter("testcase"))
                run.update(test_count=len(cases), failed_count=sum(c.find("failure") is not None or c.find("error") is not None for c in cases),
                           skipped_count=sum(c.find("skipped") is not None for c in cases), junit_sha256=hashlib.sha256(data).hexdigest())
                sdk = artifact(args, run["admission"], "reports/sdk.txt", directory / "sdk.txt")
                require(sdk.strip() == b"10.0.301", "executed with unexpected SDK")
                run["sdk_sha256"] = hashlib.sha256(sdk).hexdigest()
                run["passed"] = True
                validate_run(run, args.expected_test_count)
                write_json(args.output / "receipt.json", receipt)
                print(json.dumps({"loop": loop + 1, "kind": kind, "terminal_ms": run["terminal_observed_ms"], "passed": True}), flush=True)
            receipt["loop_durations_ms"].append((time.monotonic() - loop_start) * 1000)
            if (loop + 1) % 5 == 0:
                phase = {"after_loop": loop + 1, "physical_bytes_before": physical_bytes(args.state_root), "sweeps": [], "passed": False}
                receipt["retention_phases"].append(phase)
                for sweep in range(20):
                    now = time.monotonic()
                    rc, records = proof.capture(retention_command, now + 35, now, args.output,
                                                f"retention-{loop + 1}-{sweep}")
                    require(rc == 0 and len(records) == 1, "retention sweep refused")
                    result = records[0]["record"]
                    require(result["Held"] == 0 and result["Operations"] <= 1024, "retention held or exceeded operation bound")
                    phase["sweeps"].append(result)
                    if result["Pending"] == 0 and result["Selected"] == 0:
                        break
                else:
                    raise ValueError("retention did not converge in 20 sweeps")
                phase["physical_bytes_after"] = physical_bytes(args.state_root)
                phase["passed"] = True
                write_json(args.output / "receipt.json", receipt)
        receipt["passed"] = True
        if args.loops >= 30:
            receipt["statistics"] = validate(receipt)
        else:
            receipt["smoke_only"] = True
    except Exception as error:
        receipt["passed"] = False
        receipt["unexpected_failures"] += 1
        receipt["error"] = str(error)
        raise
    finally:
        receipt["elapsed_seconds"] = time.monotonic() - campaign_start
        write_json(args.output / "receipt.json", receipt)


if __name__ == "__main__":
    main()
