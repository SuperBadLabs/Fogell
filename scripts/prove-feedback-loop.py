#!/usr/bin/env python3
"""FG-265: bounded real-controller failure/correction proof via the F# client.

Uses only an explicitly supplied, disposable deployment. Never provisions,
stops, or deletes a controller/database. Receipts contain trusted fixture output.
"""
import argparse
import copy
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = ROOT / "scripts/prove-feedback-loop"
MARKERS = {"failed": "FG265_ASSERTION_FAILED expected=2 actual=1",
           "corrected": "FG265_ASSERTION_PASSED expected=2 actual=2"}
CAPTURE_LIMIT = 4 * 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def capture(command, deadline, origin, output_dir, label):
    """Bound process lifetime and total output, timestamp each complete JSON line."""
    records, pending, total = [], b"", 0
    selector = selectors.DefaultSelector()
    process, streams = None, {}
    try:
        streams["stdout"] = (output_dir / (label + ".ndjson")).open("wb")
        streams["stderr"] = (output_dir / (label + ".stderr")).open("wb")
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   start_new_session=True)
        for name in streams:
            pipe = getattr(process, name)
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, selectors.EVENT_READ, name)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            require(remaining > 0, label + " exceeded outer process deadline")
            for key, _ in selector.select(min(remaining, 0.1)):
                data = os.read(key.fileobj.fileno(), 65536)
                if not data:
                    selector.unregister(key.fileobj)
                    continue
                total += len(data)
                require(total <= CAPTURE_LIMIT, label + " exceeded aggregate output cap")
                streams[key.data].write(data)
                if key.data == "stdout":
                    pending += data
                    while b"\n" in pending:
                        line, pending = pending.split(b"\n", 1)
                        if line.strip():
                            records.append({"observed_ms": (time.monotonic() - origin) * 1000,
                                            "record": json.loads(line)})
        if pending.strip():
            records.append({"observed_ms": (time.monotonic() - origin) * 1000,
                            "record": json.loads(pending)})
        return process.wait(timeout=max(0.01, deadline - time.monotonic())), records
    finally:
        # Kill only this harness-owned process group, including on malformed JSON.
        if process is not None:
            # Do not signal after successful wait() reaped the leader: its PID
            # can be reused. On refusal the unreaped leader retains ownership.
            if process.returncode is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait(timeout=5)
            process.stdout.close()
            process.stderr.close()
        selector.close()
        for stream in streams.values():
            stream.close()


def validate(receipt):
    require(receipt["schema_version"] == 1, "unsupported receipt schema")
    runs = receipt["runs"]
    require(len(runs) == 2, "expected both failure and corrected runs")
    identities = []
    attempts = []
    for run, kind in zip(runs, ("failed", "corrected")):
        require(run["fixture"] == kind, "fixture order differs")
        require(run["fixture_sha256"] == digest(FIXTURES / (kind + ".Jenkinsfile")),
                "fixture digest differs from repository fixture")
        admission = run["admission"]
        require(run["submit_exit"] == 0 and admission["was_existing"] is False,
                "admission was unsuccessful or reused")
        uuid.UUID(admission["build_id"])
        uuid.UUID(admission["attempt_id"])
        identities.append(admission["build_id"])
        attempts.append(admission["attempt_id"])
        pages = run["pages"]
        require(bool(pages), "no feedback pages")
        cursor, output = 0, ""
        previous_observation = run["admission_ms"]
        for page in pages:
            body = page["record"]
            require(math.isfinite(page["observed_ms"]) and
                    page["observed_ms"] >= previous_observation, "non-monotonic observation")
            previous_observation = page["observed_ms"]
            require(body["schema_version"] == 1, "unknown feedback schema")
            terminal_statuses = {"failure", "failed", "success", "succeeded", "unstable", "aborted"}
            require(body["status"] in terminal_statuses | {"queued", "running", "not_built",
                    "reconciliation_required"}, "unknown feedback status")
            require(type(body["is_terminal"]) is bool and
                    body["is_terminal"] == (body["status"] in terminal_statuses),
                    "inconsistent terminal status")
            require(all(type(body[name]) is bool for name in
                    ("has_more", "cancellation_requested", "truncated")), "invalid feedback flags")
            require(body["build_id"] == admission["build_id"], "feedback build identity differs")
            require(body["from_sequence"] == cursor, "feedback cursor gap or replay")
            for chunk in body["chunks"]:
                require(type(chunk["truncated"]) is bool, "invalid chunk truncation flag")
                require(chunk["sequence"] >= cursor, "repeated log sequence")
                cursor = chunk["sequence"] + 1
                output += chunk["body"]
            require(body["truncated"] == any(c["truncated"] for c in body["chunks"]),
                    "inconsistent truncation")
            require(body["next_sequence"] == cursor, "feedback cursor skipped evidence")
        terminal = pages[-1]["record"]
        expected = ("failure", "failed") if kind == "failed" else ("success", "succeeded")
        require(terminal["status"] in expected, kind + " has unexpected terminal status")
        require(terminal["is_terminal"] is True and terminal["has_more"] is False,
                "terminal evidence was not drained")
        require(run["watch_exit"] == (1 if kind == "failed" else 0), "watch exit differs")
        require(MARKERS[kind] in output, "expected execution output absent")
        require(any(not p["record"]["is_terminal"] and
                    any(c["body"] for c in p["record"]["chunks"]) for p in pages),
                "nonempty progressive feedback was not observed before terminal")
        first = next((page["observed_ms"] for page in pages
                      if any(chunk["body"] for chunk in page["record"]["chunks"])), None)
        require(first is not None and run["first_nonempty_feedback_ms"] == first,
                "first nonempty feedback timing differs")
        require(run["terminal_observed_ms"] == pages[-1]["observed_ms"],
                "terminal timing differs")
        require(math.isfinite(run["admission_ms"]) and 0 <= run["admission_ms"] <= first,
                "invalid admission timing")
    require(identities[0] != identities[1], "corrected submission reused build identity")
    require(attempts[0] != attempts[1], "corrected submission reused attempt identity")
    require(runs[0]["idempotency_key"] != runs[1]["idempotency_key"], "reused submission key")


def controls(receipt):
    """Deterministic receipt mutations; these do not claim timing-race coverage."""
    def collapse_progress(receipt):
        run = receipt["runs"][0]
        terminal = copy.deepcopy(run["pages"][-1])
        terminal["record"]["from_sequence"] = 0
        terminal["record"]["chunks"] = [c for p in run["pages"] for c in p["record"]["chunks"]]
        run["pages"] = [terminal]
        run["first_nonempty_feedback_ms"] = terminal["observed_ms"]

    mutations = {
        "false_success": lambda r: r["runs"][0]["pages"][-1]["record"].update(status="success"),
        "missing_output": lambda r: [c.update(body="") for p in r["runs"][0]["pages"]
                                     for c in p["record"]["chunks"]],
        "undrained_terminal": lambda r: r["runs"][1]["pages"][-1]["record"].update(has_more=True),
        "wrong_watch_exit": lambda r: r["runs"][0].update(watch_exit=0),
        "reused_submission_key": lambda r: r["runs"][1].update(
            idempotency_key=r["runs"][0]["idempotency_key"]),
        "wrong_build_identity": lambda r: r["runs"][1]["pages"][-1]["record"].update(
            build_id=r["runs"][0]["admission"]["build_id"]),
        "cursor_gap": lambda r: r["runs"][0]["pages"][0]["record"].update(from_sequence=1000),
        "nonfinite_timing": lambda r: r["runs"][0]["pages"][0].update(observed_ms=float("nan")),
        "regressed_timing": lambda r: r["runs"][0]["pages"][0].update(observed_ms=-1),
        "missing_progressive_feedback": collapse_progress,
    }
    results = []
    for name, mutate in mutations.items():
        broken = copy.deepcopy(receipt)
        mutate(broken)
        try:
            validate(broken)
        except ValueError as error:
            results.append({"control": name, "rejected": True, "reason": str(error)})
        else:
            raise ValueError("known-bad control accepted: " + name)
    return results


def self_test():
    receipt = {"schema_version": 1, "runs": []}
    for index, kind in enumerate(("failed", "corrected")):
        build_id = str(uuid.uuid4())
        page = {"schema_version": 1, "build_id": build_id,
                "from_sequence": 1, "next_sequence": 1,
                "is_terminal": True, "has_more": False,
                "cancellation_requested": False, "truncated": False,
                "status": "failure" if index == 0 else "success",
                "chunks": []}
        progress = dict(page, from_sequence=0, status="running", is_terminal=False,
                        chunks=[{"sequence": 0, "body": MARKERS[kind], "truncated": False}])
        receipt["runs"].append({"fixture": kind,
            "fixture_sha256": digest(FIXTURES / (kind + ".Jenkinsfile")),
            "submit_exit": 0, "watch_exit": 1 if index == 0 else 0,
            "idempotency_key": kind, "admission_ms": 1,
            "first_nonempty_feedback_ms": 2, "terminal_observed_ms": 3,
            "admission": {"build_id": build_id, "attempt_id": str(uuid.uuid4()),
                          "was_existing": False},
            "pages": [{"observed_ms": 2, "record": progress}, {"observed_ms": 3, "record": page}]})
    validate(receipt)
    guards = []
    with tempfile.TemporaryDirectory(prefix="fogell-fg265-checker-") as scratch:
        for index in range(3):
            for label, source, duration, expected in (
                ("process_deadline", "import time; time.sleep(30)", 0.2, "outer process deadline"),
                ("aggregate_output", "import os\nwhile True: os.write(1, b' ' * 65536)", 5,
                 "aggregate output cap"),
            ):
                origin = time.monotonic()
                try:
                    capture([sys.executable, "-c", source], origin + duration, origin,
                            Path(scratch), label + str(index))
                except ValueError as error:
                    require(expected in str(error), "guard refused for unexpected reason: " + str(error))
                    guards.append({"control": label, "repetition": index + 1, "rejected": True})
                else:
                    raise ValueError("guard failed to reject: " + label)
    print(json.dumps({"checker_self_test": True, "real_controller_proof": False,
                      "controls": controls(receipt), "process_guards": guards}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify", type=Path, help="verify saved receipt and rerun deterministic controls")
    parser.add_argument("--self-test", action="store_true", help="checker-only synthetic controls")
    parser.add_argument("--url")
    parser.add_argument("--organization")
    parser.add_argument("--project")
    parser.add_argument("--token-file", type=Path)
    parser.add_argument("--client", type=Path,
                        default=ROOT / "tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--controller-identity", default="unknown")
    parser.add_argument("--controller-binary", type=Path, help="record SHA-256 of running controller DLL")
    parser.add_argument("--environment", default="unspecified")
    parser.add_argument("--watch-timeout-seconds", type=int, default=60)
    parser.add_argument("--request-timeout-seconds", type=int, default=10)
    parser.add_argument("--poll-interval-ms", type=int, default=100)
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if args.verify:
        receipt = json.loads(args.verify.read_text())
        require(receipt.get("passed") is True and "error" not in receipt,
                "saved campaign did not pass")
        validate(receipt)
        print(json.dumps({"verified": True, "controls": controls(receipt)}))
        return
    for name in ("url", "organization", "project", "token_file", "output"):
        require(getattr(args, name) is not None, "missing --" + name.replace("_", "-"))
    for name in ("watch_timeout_seconds", "request_timeout_seconds", "poll_interval_ms"):
        require(getattr(args, name) > 0, name + " must be positive")
    args.output.mkdir(parents=True, exist_ok=False)
    receipt = {"schema_version": 1, "controller_identity": args.controller_identity,
               "campaign_id": str(uuid.uuid4()), "started_at_utc": datetime.now(timezone.utc).isoformat(),
               "controller_binary_sha256": digest(args.controller_binary) if args.controller_binary else None,
               "client_sha256": digest(args.client), "environment": args.environment,
               "organization_id": args.organization, "project_id": args.project,
               "observation_clock": "harness monotonic; relative to each submit process start",
               "timing_scope": "client process startup, HTTP, polling; not server queue or diagnosis latency",
               "limits": {"poll_interval_ms": args.poll_interval_ms,
                          "watch_timeout_seconds": args.watch_timeout_seconds,
                          "request_timeout_seconds": args.request_timeout_seconds,
                          "max_response_bytes": 1048576, "max_process_output_bytes": CAPTURE_LIMIT},
               "runs": [], "passed": False}
    common = ["--url", args.url, "--organization", args.organization, "--project", args.project,
              "--token-file", str(args.token_file), "--request-timeout-seconds",
              str(args.request_timeout_seconds), "--max-response-bytes", "1048576"]
    try:
        for kind in ("failed", "corrected"):
            fixture = FIXTURES / (kind + ".Jenkinsfile")
            key = "fg265-" + kind + "-" + str(uuid.uuid4())
            run = {"fixture": kind, "fixture_sha256": digest(fixture), "idempotency_key": key}
            receipt["runs"].append(run)
            origin = time.monotonic()
            submit_code, admissions = capture(
                ["dotnet", str(args.client), "submit"] + common +
                ["--pipeline", str(fixture), "--idempotency-key", key],
                origin + args.request_timeout_seconds + 10, origin, args.output, kind + "-submit")
            admission_ms = (time.monotonic() - origin) * 1000
            run.update(submit_exit=submit_code, admission_ms=admission_ms)
            require(submit_code == 0 and len(admissions) == 1, kind + " admission failed")
            admission = admissions[0]["record"]
            run["admission"] = admission
            watch_code, pages = capture(
                ["dotnet", str(args.client), "watch"] + common +
                ["--build", admission["build_id"], "--watch-timeout-seconds",
                 str(args.watch_timeout_seconds), "--poll-interval-ms", str(args.poll_interval_ms)],
                time.monotonic() + args.watch_timeout_seconds + 10, origin, args.output, kind + "-watch")
            run.update(watch_exit=watch_code, pages=pages,
                       first_nonempty_feedback_ms=next((p["observed_ms"] for p in pages
                           if any(c["body"] for c in p["record"]["chunks"])), None),
                       terminal_observed_ms=pages[-1]["observed_ms"] if pages else None)
        validate(receipt)
        receipt["controls"] = controls(receipt)
        receipt["passed"] = True
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps({"passed": True, "receipt": str(args.output / "receipt.json"),
                      "build_ids": [r["admission"]["build_id"] for r in receipt["runs"]]}))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.TimeoutExpired) as error:
        print("FG-265 REFUSED: " + str(error), file=sys.stderr)
        sys.exit(1)
