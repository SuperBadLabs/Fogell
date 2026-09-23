#!/usr/bin/env python3
"""Explicitly authorized, bounded Luigi-only load/fault campaign; default declares only.

Requires the deployment owner's private config and immutable container IDs. No
container creation/removal, database edits, arbitrary shell commands, filesystem
cleanup, host-wide signals or resubmission of uncertain requests. --execute must
be supplied after deployment handoff. Failed campaigns retain all observations.

Usage (on Luigi, with script and its sibling fixture directory installed):
  python3 prove-luigi-adversarial.py --self-test
  python3 prove-luigi-adversarial.py --config deployment.json --output new-declaration
  python3 prove-luigi-adversarial.py --config deployment.json --output new-receipts --execute

Requires five protected_container_ids in addition to both owned service/container
identities. Each prerequisite must pass before later injections. A v1 diagnostic
shortcoming remains a failed/partial receipt; it is never waived to unlock faults.
The finite ENOSPC fixture runs only after verifying the container's actual /tmp
mount is a 256MiB tmpfs. It never fills the host or state filesystem.
"""
import argparse
import concurrent.futures
import hashlib
import json
import math
import os
from pathlib import Path
import re
import selectors
import signal
import socket
import stat
import subprocess
import threading
import time
import sys
import tempfile
import urllib.error
import urllib.request
import uuid

FIXTURES = Path(__file__).with_suffix("")
TERMINAL = {"success", "succeeded", "failure", "failed", "unstable", "aborted"}
UNCERTAIN = "reconciliation_required"
SUCCESS = {"success", "succeeded"}
KNOWN = TERMINAL | {"queued", "running", "pending", UNCERTAIN}
MAX_RESPONSE = 4 * 1024 * 1024
MAX_EVIDENCE = 256 * 1024 * 1024


class CampaignInterrupted(BaseException):
    """Operator/service cancellation must escape per-scenario Exception handling."""


def execute_campaign(campaign):
    def interrupted(signum, frame):
        # A second service-stop signal must not interrupt owned dependency
        # restoration while the first cancellation unwinds fault try/finally.
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        raise CampaignInterrupted('campaign interrupted by ' + signal.Signals(signum).name)

    previous = {signum: signal.getsignal(signum) for signum in (signal.SIGTERM, signal.SIGINT)}
    try:
        for signum in previous:
            signal.signal(signum, interrupted)
        try:
            campaign.execute()
        except BaseException as exc:
            campaign.receipt['passed'] = False
            campaign.receipt['error'] = type(exc).__name__ + ':' + str(exc)
            if isinstance(exc, CampaignInterrupted):
                campaign.receipt['interrupted'] = True
        finally:
            campaign.receipt['censored_jobs'] = sum(bool(j.get('censored')) for j in campaign.receipt['jobs'])
            campaign.checkpoint()
    finally:
        for signum, handler in previous.items():
            signal.signal(signum, handler)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def atomic_json(path, value):
    temp = path.with_suffix(path.suffix + ".tmp")
    with open(temp, "w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)


def validate_config(config):
    required = {"url", "token_file", "organization", "project", "controller_container_id",
                "postgres_container_id", "service_unit", "postgres_service_unit", "deployment_root", "state_root", "database", "protected_container_ids"}
    require(required <= set(config), "config_missing_required_fields")
    require(config["url"] == "http://127.0.0.1:46206", "unexpected_controller_url")
    require(config["service_unit"] == "fogell.service", "unexpected_service_unit")
    require(config["postgres_service_unit"] == "fogell-postgres.service", "unexpected_postgres_unit")
    root = Path(config["deployment_root"])
    require(str(root) == "/home/srikanth/services/fogell", "unexpected_deployment_root")
    for name in ("state_root", "token_file"):
        path = Path(config[name])
        require(path.is_absolute() and path.is_relative_to(root) and ".." not in path.parts,
                "path_outside_owned_deployment:" + name)
    for name in ("controller_container_id", "postgres_container_id"):
        require(re.fullmatch(r"[a-f0-9]{64}", config[name]) is not None, "immutable_container_id_required")
    require(config["controller_container_id"] != config["postgres_container_id"], "containers_not_distinct")
    protected = config["protected_container_ids"]
    require(isinstance(protected, list) and len(protected) == 5 and len(set(protected)) == 5,
            "exactly_five_protected_ids_required")
    require(all(isinstance(cid, str) and re.fullmatch(r"[a-f0-9]{64}", cid) is not None
                and cid not in (config["controller_container_id"], config["postgres_container_id"])
                for cid in protected), "invalid_protected_ids")
    for name in ("organization", "project"):
        require(str(uuid.UUID(config[name])) == config[name], "noncanonical_uuid:" + name)
    require(config["database"] == "fogell", "unexpected_database")


class Campaign:
    def __init__(self, config, output, repetitions):
        self.config, self.output, self.repetitions = config, output, repetitions
        self.lock = threading.RLock()
        self.started = time.monotonic()
        self.sequence = self.raw_bytes = 0
        self.token = None
        self.base = (config["url"] + "/api/v1/organizations/" + config["organization"]
                     + "/projects/" + config["project"] + "/builds")
        self.receipt = {"schema_version": 1, "passed": False, "declaration": {
            "campaign_id": str(uuid.uuid4()), "declared_at_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "harness_sha256": digest(__file__), "fixtures": {p.name: digest(p) for p in sorted(FIXTURES.glob("*.Jenkinsfile"))},
            "target": {k: v for k, v in config.items() if k in ("url", "organization", "project", "controller_container_id", "postgres_container_id", "service_unit", "postgres_service_unit", "deployment_root", "state_root", "database", "protected_container_ids")},
            "unique_submissions": 32, "concurrency": 32, "idempotent_racers": 16,
            "fault_repetitions": repetitions, "faults": ["cancel", "runner_kill", "controller_kill", "postgres_outage"],
            "feedback_sequence_scope": "contiguous fresh campaign builds; no retry, migration, or retention",
            "queued_controls_per_fault": 2, "request_timeout_seconds": 5, "wait_timeout_seconds": 120,
            "command_timeout_seconds": 20, "service_restart_timeout_seconds": 30, "campaign_timeout_seconds": 3600,
            "owned_service_recovery_grace_seconds": 150, "postgres_outage_hold_seconds": 20,
            "state_content_guard_bytes": 2147483648, "host_free_guard_bytes": 21474836480,
            "state_inventory_entry_cap": 100000, "state_inventory_timeout_seconds": 10,
            "max_response_bytes": MAX_RESPONSE, "max_raw_evidence_bytes": MAX_EVIDENCE,
            "output_fixture_max_generated_bytes": 41943040,
            "artifact_fixture_bytes": 2097152, "deployed_artifact_file_limit_bytes": 1048576,
            "scratch_fixture_max_generated_bytes": 314572800, "required_tmpfs_bytes": 268435456,
            "scratch_scope": "unique directory on verified container /tmp tmpfs only; trap cleanup",
            "latency_scope": "monotonic HTTP admission start through drained terminal feedback, including queue/poll; one32-job condition",
            "scope": "trusted isolated deployment fault injection; no host disk exhaustion or protected-service mutation"},
            "jobs": [], "scenarios": [], "commands": [], "unexpected_failures": 0, "censored_jobs": 0}
        self.checkpoint()

    def checkpoint(self):
        with self.lock:
            self.receipt["elapsed_seconds"] = time.monotonic() - self.started
            atomic_json(self.output / "receipt.json", self.receipt)

    def budget(self):
        require(time.monotonic() - self.started < 3600, "campaign_deadline")

    def raw(self, value):
        data = (json.dumps(value, allow_nan=False) + "\n").encode()
        with self.lock:
            require(self.raw_bytes + len(data) <= MAX_EVIDENCE, "raw_evidence_budget")
            with open(self.output / "http.ndjson", "ab") as stream:
                stream.write(data)
                stream.flush()
            self.raw_bytes += len(data)

    def http(self, method, url, payload=None, key=None, timeout=5, authenticated=True, recovery=False):
        if recovery:
            require(method == "GET" and url == self.config["url"] + "/health/ready", "recovery_probe_scope")
        else:
            self.budget()
        headers = {"Authorization": "Bearer " + self.token} if self.token and authenticated else {}
        if key:
            headers["Idempotency-Key"] = key
        if payload is not None:
            headers["Content-Type"] = "text/plain; charset=utf-8"
        start = time.monotonic()
        timeout = min(5, max(0.01, timeout))
        status, body, error = None, b"", None
        error_response = None  # Keep HTTPError owner alive through bounded body read.
        try:
            request = urllib.request.Request(url, data=payload, headers=headers, method=method)
            try:
                response = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect()).open(request, timeout=timeout)
            except urllib.error.HTTPError as failed:
                error_response = failed
                response = failed.fp
            with response:
                status = response.status
                while len(body) <= MAX_RESPONSE:
                    remaining = timeout - (time.monotonic() - start)
                    require(remaining > 0, "response_deadline")
                    if response.fp is None:
                        break
                    response.fp.raw._sock.settimeout(remaining)
                    chunk = response.read1(min(65536, MAX_RESPONSE + 1 - len(body)))
                    if not chunk:
                        break
                    body += chunk
            require(len(body) <= MAX_RESPONSE, "response_budget")
        except (OSError, ValueError) as exc:
            error = type(exc).__name__ + ":" + str(exc)
            status = None
        record = {"method": method, "url": url, "status": status,
                  "request_bytes": len(payload) if payload is not None else 0,
                  "request_sha256": hashlib.sha256(payload).hexdigest() if payload is not None else None,
                  "elapsed_ms": (time.monotonic() - start) * 1000,
                  "campaign_ms": (start - self.started) * 1000,
                  "body": body.decode("utf-8", errors="replace"), "error": error}
        self.raw(record)
        return record

    def command(self, args, allow_failure=False, timeout=20, recovery=False):
        if recovery:
            permitted = [["systemctl", "--user", "start", self.config["postgres_service_unit"]],
                         ["systemctl", "--user", "restart", self.config["service_unit"]]]
            require(args in permitted, "recovery_command_scope")
        else:
            self.budget()
        require(0 < timeout <= 30, "command_timeout_bound")
        start = time.monotonic()
        # Streaming output bound, including failed and stalled commands.
        with subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              start_new_session=True) as child:
            parts = {"stdout": bytearray(), "stderr": bytearray()}
            failure = None
            try:
                with selectors.DefaultSelector() as selector:
                    for name, pipe in (("stdout", child.stdout), ("stderr", child.stderr)):
                        os.set_blocking(pipe.fileno(), False)
                        selector.register(pipe, selectors.EVENT_READ, name)
                    while selector.get_map():
                        require(time.monotonic() - start < timeout, "command_deadline")
                        for selected, _ in selector.select(0.1):
                            chunk = os.read(selected.fileobj.fileno(), 65536)
                            if not chunk:
                                selector.unregister(selected.fileobj)
                            else:
                                parts[selected.data].extend(chunk)
                                require(sum(map(len, parts.values())) <= 1024 * 1024, "command_output_budget")
                    child.wait(timeout=max(0.01, timeout - (time.monotonic() - start)))
            except Exception as exc:
                failure = type(exc).__name__ + ":" + str(exc)
            finally:
                if child.returncode is None:
                    try:
                        os.killpg(child.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    child.wait(timeout=5)
            result = {"argv": args, "exit_code": child.returncode, "error": failure,
                      "campaign_ms": (start - self.started) * 1000,
                      "elapsed_ms": (time.monotonic() - start) * 1000,
                      **{k: bytes(v).decode(errors="replace") for k, v in parts.items()}}
        with self.lock:
            self.receipt["commands"].append(result)
            self.checkpoint()
        require(failure is None, failure or "command_capture_failed")
        require(allow_failure or result["exit_code"] == 0, "command_failed:" + args[0])
        return result

    def inspect(self, cid):
        result = self.command(["podman", "inspect", "--format", "{{.Id}} {{.State.Running}} {{.State.Pid}}", cid])
        fields = result["stdout"].strip().split()
        require(len(fields) == 3 and fields[0] == cid, "container_identity_changed")
        return {"id": fields[0], "running": fields[1] == "true", "pid": int(fields[2])}

    def protected_snapshot(self):
        # Projection occurs inside podman. Never fetch or retain Env/secrets.
        template = ('{"id":{{json .ID}},"name":{{json .Name}},"image":{{json .Image}},'
                    '"started_at":{{json .State.StartedAt}},"running":{{json .State.Running}},'
                    '"restart_count":{{json .RestartCount}},"ports":{{json .NetworkSettings.Ports}},'
                    '"port_bindings":{{json .HostConfig.PortBindings}},"mounts":{{json .Mounts}}}')
        snapshots = []
        for cid in sorted(self.config["protected_container_ids"]):
            row = self.command(["podman", "inspect", "--format", template, cid])
            snapshot = json.loads(row["stdout"])
            require(snapshot["id"] == cid, "protected_container_identity_changed")
            snapshots.append(snapshot)
        return snapshots

    def protected_check(self, label):
        snapshot = self.protected_snapshot()
        self.receipt.setdefault("protected_observations", []).append({"label": label, "containers": snapshot})
        self.checkpoint()
        require(snapshot == self.receipt["protected_baseline"], "protected_services_changed:" + label)

    def preflight(self):
        require(socket.gethostname().split(".")[0].lower() == "luigi", "execute_only_on_luigi")
        require(hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal"), "pidfd_required")
        for name in ("controller_container_id", "postgres_container_id"):
            require(self.inspect(self.config[name])["running"], name + "_not_running")
        for unit_key, container_key in (("service_unit", "controller_container_id"),
                                         ("postgres_service_unit", "postgres_container_id")):
            unit = self.command(["systemctl", "--user", "show", self.config[unit_key], "--property=ExecStart", "--value"])
            require(self.config[container_key] in unit["stdout"] and "podman start" in unit["stdout"], "unit_container_binding_mismatch")
        fd = os.open(self.config["token_file"], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            info = os.fstat(fd)
            require(stat.S_ISREG(info.st_mode) and info.st_mode & 0o077 == 0 and info.st_size <= 4096, "private_regular_token_required")
            self.token = os.read(fd, 4097).decode().rstrip("\r\n")
            require(bool(self.token) and not any(c.isspace() for c in self.token), "invalid_token")
        finally:
            os.close(fd)
        self.ready(200)
        self.receipt["protected_baseline"] = self.protected_snapshot()
        self.checkpoint()

    def ready(self, desired, seconds=120, recovery=False):
        deadline = time.monotonic() + min(seconds, 120)
        observations = []
        while time.monotonic() < deadline:
            row = self.http("GET", self.config["url"] + "/health/ready", timeout=deadline-time.monotonic(), recovery=recovery)
            observations.append({"status": row["status"], "error": row["error"]})
            if row["status"] == desired:
                return observations
            time.sleep(0.2)
        raise TimeoutError("readiness_deadline:" + str(desired))

    def submit(self, label, fixture="simple", key=None):
        origin = time.monotonic()
        key = key or self.receipt["declaration"]["campaign_id"] + ":" + label
        pipeline = (FIXTURES / (fixture + ".Jenkinsfile")).read_bytes()
        if fixture == "scratch-enospc":
            pipeline = pipeline.replace(b"__CAMPAIGN__", self.receipt["declaration"]["campaign_id"].encode())
        result = self.http("POST", self.base, pipeline, key)
        job = {"label": label, "fixture": fixture, "key": key, "origin_monotonic": origin,
               "admission_status": result["status"], "passed": False, "chunks": [], "next_sequence": 0}
        with self.lock:
            self.receipt["jobs"].append(job)
            self.checkpoint()
        require(result["status"] in (200, 201, 202), "admission_failed:" + label)
        value = json.loads(result["body"])
        require(isinstance(value.get("was_existing"), bool), "invalid_admission")
        for field in ("build_id", "attempt_id"):
            uuid.UUID(value[field])
        with self.lock:
            job["admission"] = value
        return job

    def page(self, job, timeout=5):
        cursor = job["next_sequence"]
        row = self.http("GET", self.base + "/" + job["admission"]["build_id"] + "/feedback?from=" + str(cursor), timeout=timeout)
        if row["status"] != 200:
            return None
        page = json.loads(row["body"])
        require(page.get("schema_version") == 1 and page.get("build_id") == job["admission"]["build_id"], "feedback_identity")
        require(page.get("status") in KNOWN, "unknown_status")
        for field in ("has_more", "is_terminal", "cancellation_requested", "truncated"):
            require(type(page.get(field)) is bool, "feedback_boolean:" + field)
        require(page["is_terminal"] == (page["status"] in TERMINAL), "terminal_consistency")
        require(type(page.get("from_sequence")) is int and page["from_sequence"] == cursor, "feedback_from")
        require(type(page.get("next_sequence")) is int and page["next_sequence"] >= cursor, "feedback_next")
        chunks = page.get("chunks")
        require(isinstance(chunks, list), "feedback_chunks")
        seq = cursor
        for chunk in chunks:
            # General API cursors may be sparse for migrated legacy logs. These
            # fresh campaign builds have no retry/retention and allocate only
            # committed frames, so missing evidence must fail this campaign.
            require(type(chunk.get("sequence")) is int and chunk["sequence"] == seq, "chunk_sequence")
            seq = chunk["sequence"] + 1
            require(isinstance(chunk.get("body"), str), "chunk_body")
        require(seq == page["next_sequence"], "cursor_skipped_evidence")
        with self.lock:
            job["next_sequence"] = seq
            job["chunks"].extend(chunks)
            job["last_page"] = {k: v for k, v in page.items() if k != "chunks"}
        return page

    def wait(self, job, marker=False, seconds=120):
        deadline = time.monotonic() + min(seconds, 120)
        while time.monotonic() < deadline:
            page = self.page(job, timeout=deadline-time.monotonic())
            if page is not None:
                if marker and any(c["body"] == "FG_LUIGI_FAULT_STARTED" for c in job["chunks"]):
                    require(not page["is_terminal"] and page["status"] != UNCERTAIN, "fault_target_already_stopped")
                    return job
                if page["is_terminal"] or page["status"] == UNCERTAIN:
                    if not page["has_more"]:
                        require(not marker, "fault_marker_missing")
                        with self.lock:
                            job["observed_ms"] = (time.monotonic() - job["origin_monotonic"]) * 1000
                        self.checkpoint()
                        return job
            time.sleep(0.1)
        with self.lock:
            job["censored"] = True
        self.checkpoint()
        raise TimeoutError("feedback_deadline:" + job["label"])

    @staticmethod
    def diagnostics(job):
        return [c["diagnostic"] for c in job["chunks"] if c.get("diagnostic") is not None]

    def success(self, job):
        self.wait(job)
        require(job["last_page"]["status"] in SUCCESS, "control_not_success:" + job["label"])
        require(sum(c["body"] == "FG_LUIGI_CONTROL" for c in job["chunks"]) == 1, "control_marker_count")
        with self.lock:
            job["passed"] = True
        self.checkpoint()
        return job

    def resources(self):
        origin = time.monotonic()
        size = count = 0
        def scan(fd, depth):
            nonlocal size, count
            require(depth <= 128, "state_inventory_depth")
            with os.scandir(fd) as entries:
                for entry in entries:
                    count += 1
                    require(count <= 100000, "state_inventory_limit")
                    require(time.monotonic() - origin < 10, "state_inventory_deadline")
                    try:
                        metadata = entry.stat(follow_symlinks=False)
                        if stat.S_ISDIR(metadata.st_mode):
                            child = os.open(entry.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                            try:
                                scan(child, depth + 1)
                            finally:
                                os.close(child)
                        elif stat.S_ISREG(metadata.st_mode):
                            size += metadata.st_size
                    except FileNotFoundError:
                        continue
                    require(size < 2147483648, "state_content_guard_2GiB")
        fd = os.open(self.config["state_root"], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            scan(fd, 0)
        finally:
            os.close(fd)
        fs = os.statvfs(self.config["deployment_root"])
        available = fs.f_bavail * fs.f_frsize
        require(available > 21474836480, "host_free_guard_20GiB")
        return {"state_regular_file_bytes": size, "inventory_entries": count,
                "filesystem_available_bytes": available, "elapsed_ms": (time.monotonic()-origin)*1000}

    def negative_api(self):
        unauth = self.http("GET", self.base + "/" + str(uuid.uuid4()), authenticated=False)
        require(unauth["status"] == 401, "unauthenticated_not_refused")
        malformed = self.http("POST", self.base, b"pipeline {", str(uuid.uuid4()))
        require(malformed["status"] == 422, "malformed_pipeline_not_refused")
        oversized = self.http("POST", self.base, b" " * (16777216 + 1), str(uuid.uuid4()))
        require(oversized["status"] == 413, "oversized_pipeline_not_refused")
        return {"unauthenticated_status": 401, "malformed_status": 422, "oversized_status": 413}

    def scenario(self, label, action):
        row = {"label": label, "passed": False}
        self.receipt["scenarios"].append(row)
        self.checkpoint()
        try:
            row["resources_before"] = self.resources()
            row["result"] = action()
            row["resources_after"] = self.resources()
            row["passed"] = True
        except Exception as exc:
            row["error"] = type(exc).__name__ + ":" + str(exc)
            self.receipt["unexpected_failures"] += 1
        finally:
            if "protected_baseline" in self.receipt:
                try:
                    self.protected_check(label)
                except Exception as exc:
                    row["protected_error"] = type(exc).__name__ + ":" + str(exc)
                    row["passed"] = False
                    self.receipt["unexpected_failures"] += 1
        self.checkpoint()
        require("protected_error" not in row, "protected_services_unverified_stop_campaign")
        return row["passed"]

    def load(self):
        def run(index):
            try:
                return self.success(self.submit("load-" + str(index)))
            except Exception as exc:
                return {"error": type(exc).__name__ + ":" + str(exc), "index": index}
        with concurrent.futures.ThreadPoolExecutor(max_workers=32) as pool:
            outcomes = list(pool.map(run, range(32)))
        errors = [r for r in outcomes if "error" in r]
        self.receipt["load_outcomes"] = [{k: v for k, v in r.items() if k in ("label", "observed_ms", "error", "index")} for r in outcomes]
        require(not errors and len(outcomes) == 32, "concurrent_load_failed")
        require(len({r["admission"]["build_id"] for r in outcomes}) == 32, "unique_builds_aliased")
        values = sorted(r["observed_ms"] for r in outcomes)
        return {"attempted": 32, "successes": 32, "unexpected_failures": 0, "censored": 0,
                "p50_ms": values[math.ceil(len(values) * .50) - 1], "p95_ms": values[math.ceil(len(values) * .95) - 1]}

    def idempotency(self):
        key = self.receipt["declaration"]["campaign_id"] + ":same-key"
        with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
            futures = [pool.submit(self.submit, "same-" + str(i), "simple", key) for i in range(16)]
            jobs = [f.result() for f in futures]
        require(len({(r["admission"]["build_id"], r["admission"]["attempt_id"]) for r in jobs}) == 1, "duplicate_identity")
        require(sum(not r["admission"]["was_existing"] for r in jobs) == 1, "new_admission_count")
        changed = self.http("POST", self.base, (FIXTURES / "shell.Jenkinsfile").read_bytes(), key)
        require(changed["status"] == 409 and "idempotency_conflict" in changed["body"], "changed_key_not_conflict")
        self.success(jobs[0])
        for job in jobs:
            with self.lock:
                job["passed"] = True
        return {"racers": 16, "distinct_builds": 1, "changed_request_status": 409}

    @staticmethod
    def validate_failure(job, fixture):
        status = job["last_page"]["status"]
        require(status in (TERMINAL - SUCCESS) | {UNCERTAIN}, "failure_became_success")
        diagnostics = Campaign.diagnostics(job)
        require(bool(diagnostics), "typed_failure_missing")
        # Metadata such as stage='Bounded timeout' is not failure evidence.
        reasons = "\n".join(str(d.get("message") or "") + "\n" + str(d.get("output") or "")
                            for d in diagnostics).lower()
        if fixture == "shell":
            require(status in {"failure", "failed"}, "exit7_status_not_workload_failure")
            require(any(d.get("category") == "workload_step" and d.get("exit_code") == 7
                        for d in diagnostics), "exit7_diagnostic_missing")
        elif fixture == "timeout":
            require(status == "aborted" and ("timeout" in reasons or "timed out" in reasons),
                    "timeout_abort_reason_missing")
        elif fixture == "output-cap":
            require("output_limit_exceeded" in reasons, "output_limit_reason_missing")
        elif fixture == "artifact-cap":
            require("artifact_limit_exceeded" in reasons, "artifact_limit_reason_missing")
        elif fixture == "scratch-enospc":
            require("no space left on device" in reasons and any(d.get("category") == "workload_step"
                    and d.get("exit_code") == 1 for d in diagnostics), "enospc_workload_reason_missing")
        else:
            raise ValueError("unknown_failure_fixture")
        return diagnostics

    def absent_owned_artifact(self, parts):
        """Read-only no-follow lookup under the configured owned state root."""
        fd = os.open(self.config["state_root"], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            for name in parts[:-1]:
                try:
                    child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                except FileNotFoundError:
                    return True
                os.close(fd)
                fd = child
            try:
                os.stat(parts[-1], dir_fd=fd, follow_symlinks=False)
                return False
            except FileNotFoundError:
                return True
        finally:
            os.close(fd)

    def failure(self, fixture):
        if fixture == "scratch-enospc":
            mountinfo = self.command(["podman", "exec", self.config["controller_container_id"],
                                      "cat", "/proc/self/mountinfo"])["stdout"]
            selected = []
            for line in mountinfo.splitlines():
                left, right = line.split(" - ", 1)
                if left.split()[4] == "/tmp":
                    selected.append(right.split())
            require(len(selected) == 1 and selected[0][0] == "tmpfs", "scratch_requires_actual_dedicated_tmpfs")
            sizes = [item[5:] for item in selected[0][2].split(",") if item.startswith("size=")]
            require(len(sizes) == 1, "tmpfs_size_not_declared")
            match = re.fullmatch(r"([0-9]+)([kmg]?)", sizes[0])
            require(match is not None, "tmpfs_size_unrecognized")
            size = int(match[1]) * {"": 1, "k": 1024, "m": 1048576, "g": 1073741824}[match[2]]
            require(size == 268435456, "tmpfs_size_not_256MiB")
            self.receipt["scratch_mount_verified"] = {"mountpoint": "/tmp", "type": "tmpfs", "bytes": size}
            self.checkpoint()
        job = self.wait(self.submit(fixture, fixture))
        diagnostics = self.validate_failure(job, fixture)
        result = {"build_id": job["admission"]["build_id"], "status": job["last_page"]["status"], "diagnostics": diagnostics}
        if fixture == "artifact-cap":
            absent = self.http("GET", self.base + "/" + job["admission"]["build_id"] + "/attempts/"
                               + job["admission"]["attempt_id"] + "/artifacts/oversized.bin")
            require((absent["status"] == 404 and "artifact_not_found" in absent["body"])
                    or (absent["status"] == 409 and "artifact_not_ready" in absent["body"]),
                    "oversized_artifact_api_did_not_refuse")
            org = uuid.UUID(self.config["organization"]).hex
            build, attempt = (uuid.UUID(job["admission"][key]).hex for key in ("build_id", "attempt_id"))
            paths = [["workspaces", org, "_artifacts", build, "oversized.bin"],
                     ["workspaces", org, "_artifact-snapshots", attempt, "oversized.bin"]]
            absence = [{"relative_path": "/".join(parts), "absent": self.absent_owned_artifact(parts)} for parts in paths]
            require(all(row["absent"] for row in absence), "oversized_artifact_payload_exists")
            result["artifact_read_status"] = absent["status"]
            result["artifact_absence_inventory"] = absence
            # 409 alone proves only unavailability; the descriptor-based
            # staging/snapshot lookups above establish this payload's absence.
        elif fixture == "scratch-enospc":
            path = "/tmp/fogell-campaign-" + self.receipt["declaration"]["campaign_id"]
            self.command(["podman", "exec", self.config["controller_container_id"], "test", "!", "-e", path])
            result["scratch_cleanup_confirmed"] = True
        with self.lock:
            job["passed"] = True
        self.success(self.submit(fixture + "-recovery-control"))
        result["subsequent_control_success"] = True
        return result

    def kill_runner(self, job):
        cid = self.config["controller_container_id"]
        top = self.command(["podman", "top", cid, "hpid", "pid", "args"])["stdout"]
        build = uuid.UUID(job["admission"]["build_id"]).hex
        candidates = []
        for line in top.splitlines()[1:]:
            parts = line.split(None, 2)
            if len(parts) == 3 and parts[0].isdigit() and "Fogell.Run.Host" in parts[2] and build in parts[2].replace("-", ""):
                candidates.append(int(parts[0]))
        require(len(candidates) == 1, "runner_target_not_unique")
        pid = candidates[0]
        with open(f"/proc/{pid}/cmdline", "rb") as stream:
            before = stream.read(65536)
        start = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
        require(b"Fogell.Run.Host" in before and build.encode() in before.replace(b"-", b""), "runner_cmdline_mismatch")
        fd = os.pidfd_open(pid)
        try:
            current = Path(f"/proc/{pid}/cmdline").read_bytes()
            current_start = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
            require(current == before and current_start == start, "runner_identity_changed")
            # Recheck container membership while holding the exact-process pidfd.
            top = self.command(["podman", "top", cid, "hpid"])["stdout"]
            require(str(pid) in [line.strip() for line in top.splitlines()[1:]], "runner_left_container")
            injection = {"container_id": cid, "runner_host_pid": pid, "start_ticks": start,
                         "signal": "SIGKILL", "pidfd": True, "delivered": False}
            self.receipt.setdefault("runner_signal_intents", []).append(injection)
            self.checkpoint()
            signal.pidfd_send_signal(fd, signal.SIGKILL)
            injection["delivered"] = True
            self.checkpoint()
        finally:
            os.close(fd)
        return injection

    def hold_postgres_outage(self):
        seconds = self.receipt["declaration"]["postgres_outage_hold_seconds"]
        require(seconds == 20, "postgres_outage_hold_declaration")
        start = time.monotonic()
        deadline = start + seconds
        while True:
            self.budget()
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            time.sleep(min(0.2, remaining))
        end = time.monotonic()
        measured = {"hold_ms": (end - start) * 1000,
                    "start_campaign_ms": (start - self.started) * 1000,
                    "end_campaign_ms": (end - self.started) * 1000}
        require(measured["hold_ms"] >= 20000, "postgres_outage_hold_too_short")
        return measured

    def fault(self, kind, repetition):
        label = kind + "-" + str(repetition)
        job = self.submit(label, "postgres" if kind == "postgres_outage" else "long")
        self.wait(job, marker=True)
        controls = [self.submit(label + "-queued-" + str(i)) for i in range(2)]
        queued = []
        for control in controls:
            page = self.page(control)
            require(page is not None and page["status"] == "queued", "control_not_queued_before_fault")
            queued.append(control["admission"]["build_id"])
        observed = self.page(job)
        require(observed is not None and not observed["is_terminal"] and observed["status"] != UNCERTAIN,
                "fault_target_finished_before_injection")
        result = {"build_id": job["admission"]["build_id"], "marker_observed_before_fault": True,
                  "status_before_injection": observed["status"], "verified_queued_builds": queued}
        if kind == "cancel":
            cancel = self.http("POST", self.base + "/" + job["admission"]["build_id"] + "/cancel", b"")
            require(cancel["status"] == 202, "cancellation_not_accepted")
        elif kind == "runner_kill":
            result["injection"] = self.kill_runner(job)
        elif kind == "controller_kill":
            cid = self.config["controller_container_id"]
            before = self.inspect(cid)
            try:
                self.command(["podman", "kill", "--signal", "KILL", cid])
            finally:
                # Killing podman-attach alone does not kill the controller. Kill
                # the exact container, then recover its explicitly bound unit.
                self.command(["systemctl", "--user", "restart", self.config["service_unit"]], timeout=30, recovery=True)
            self.ready(200, recovery=True)
            after = self.inspect(cid)
            require(after["running"] and after["pid"] != before["pid"], "controller_did_not_restart")
            result["injection"] = {"before": before, "after": after}
        else:
            cid = self.config["postgres_container_id"]
            before = self.inspect(cid)
            try:
                self.command(["systemctl", "--user", "stop", self.config["postgres_service_unit"]], timeout=30)
                result["unready_observations"] = self.ready(503, seconds=30)
                result["postgres_outage_hold"] = self.hold_postgres_outage()
            finally:
                # Restore the owned dependency even when readiness assertion fails.
                self.command(["systemctl", "--user", "start", self.config["postgres_service_unit"]], timeout=30, recovery=True)
                self.ready(200, recovery=True)
            after = self.inspect(cid)
            require(after["running"] and after["pid"] != before["pid"], "postgres_did_not_restart")
            result["injection"] = {"before": before, "after": after}
        self.wait(job)
        status = job["last_page"]["status"]
        starts = sum(c["body"] == "FG_LUIGI_FAULT_STARTED" for c in job["chunks"])
        require(starts == 1, "fault_step_replayed_or_evidence_duplicated")
        if kind != "postgres_outage":
            require(status in {"failure", "failed", "aborted", UNCERTAIN}, "interrupted_work_became_success")
            require(bool(self.diagnostics(job)), "fault_reason_missing")
        elif status in SUCCESS:
            require(sum(c["body"] == "FG_LUIGI_FAULT_COMPLETED" for c in job["chunks"]) == 1,
                    "outage_success_missing_completion")
        else:
            require(bool(self.diagnostics(job)), "outage_failure_reason_missing")
        if kind == "cancel":
            require(job["last_page"]["cancellation_requested"], "cancel_flag_missing")
        with self.lock:
            job["passed"] = True
        for control in controls:
            self.success(control)
        result.update(status=status, diagnostics=self.diagnostics(job), queued_controls_succeeded=len(controls))
        return result

    def execute(self):
        self.preflight()
        prerequisites = [("api_negative_controls", self.negative_api),
                         ("concurrent_unique_load", self.load), ("idempotency_race", self.idempotency)]
        prerequisites.extend((fixture, lambda fixture=fixture: self.failure(fixture))
                             for fixture in ("shell", "timeout", "output-cap", "artifact-cap", "scratch-enospc"))
        stopped = False
        for label, action in prerequisites:
            if not self.scenario(label, action):
                self.receipt["remaining_faults_not_run"] = True
                stopped = True
                break
        for kind in self.receipt["declaration"]["faults"]:
            if stopped:
                break
            for repeat in range(1, self.repetitions + 1):
                okay = self.scenario(kind + "-" + str(repeat), lambda kind=kind, repeat=repeat: self.fault(kind, repeat))
                if not okay:
                    # Do not pile more injections onto an unobserved fault state.
                    self.receipt["remaining_faults_not_run"] = True
                    self.checkpoint()
                    stopped = True
                    break
        self.scenario("final_healthy_control", lambda: {"job": self.success(self.submit("final-healthy"))["admission"], "readiness": self.ready(200)})
        self.receipt["censored_jobs"] = sum(bool(j.get("censored")) for j in self.receipt["jobs"])
        self.receipt["passed"] = all(s["passed"] for s in self.receipt["scenarios"])
        self.checkpoint()


def self_test():
    """Portable negative controls; no deployment access, signals only owned children."""
    import copy
    config = {"url": "http://127.0.0.1:46206", "token_file": "/home/srikanth/services/fogell/token",
              "organization": str(uuid.uuid4()), "project": str(uuid.uuid4()),
              "controller_container_id": "a" * 64, "postgres_container_id": "b" * 64,
              "service_unit": "fogell.service", "postgres_service_unit": "fogell-postgres.service",
              "deployment_root": "/home/srikanth/services/fogell", "state_root": "/home/srikanth/services/fogell/state",
              "database": "fogell", "protected_container_ids": [c*64 for c in "cdef0"]}
    validate_config(config)
    controls = []
    for key, bad in (("controller_container_id", "friendly-name"), ("postgres_container_id", "a"*64),
                     ("service_unit", "jenkins.service"), ("postgres_service_unit", "shared.service"),
                     ("protected_container_ids", ["a"*64]*5),
                     ("state_root", "/tmp/other"), ("url", "http://external.example:46206")):
        mutated = dict(config, **{key: bad})
        try:
            validate_config(mutated)
        except (ValueError, TypeError):
            controls.append("reject_" + key)
        else:
            raise AssertionError("unsafe config accepted:" + key)
    with tempfile.TemporaryDirectory(prefix="fogell-adversarial-check-") as directory:
        campaign = Campaign(config, Path(directory), 3)
        build = str(uuid.uuid4())
        page = {"schema_version": 1, "build_id": build, "status": "running", "is_terminal": False,
                "has_more": False, "cancellation_requested": False, "truncated": False,
                "from_sequence": 0, "next_sequence": 1,
                "chunks": [{"sequence": 0, "body": "marker", "truncated": False}]}
        def job():
            return {"admission": {"build_id": build}, "next_sequence": 0, "chunks": []}
        original_http = campaign.http
        campaign.http = lambda *args, **kwargs: {"status": 200, "body": json.dumps(page)}
        valid_job = job()
        campaign.page(valid_job)
        require(valid_job["next_sequence"] == 1, "valid_zero_based_cursor_refused")
        for key, bad in (("build_id", str(uuid.uuid4())), ("status", "invented"),
                         ("is_terminal", True), ("next_sequence", 0), ("from_sequence", 1),
                         ("has_more", 1)):
            broken = copy.deepcopy(page)
            broken[key] = bad
            campaign.http = lambda *args, broken=broken, **kwargs: {"status": 200, "body": json.dumps(broken)}
            try:
                campaign.page(job())
            except ValueError:
                controls.append("reject_feedback_" + key)
            else:
                raise AssertionError("invalid feedback accepted:" + key)
        for label, sequences in (("missing_first", [1]), ("missing_middle", [0, 2])):
            broken = copy.deepcopy(page)
            broken["chunks"] = [{"sequence": seq, "body": "marker", "truncated": False} for seq in sequences]
            broken["next_sequence"] = sequences[-1] + 1
            campaign.http = lambda *args, broken=broken, **kwargs: {"status": 200, "body": json.dumps(broken)}
            try:
                campaign.page(job())
            except ValueError:
                controls.append("reject_feedback_" + label)
            else:
                raise AssertionError("fresh campaign evidence gap accepted:" + label)
        campaign.http = original_http
        for fixture, stage in (("timeout", "Bounded timeout"), ("output-cap", "Bounded output overflow"),
                               ("artifact-cap", "ARTIFACT_LIMIT_EXCEEDED"), ("scratch-enospc", "No space left on device")):
            broken = {"last_page": {"status": "aborted" if fixture == "timeout" else "failure"},
                      "chunks": [{"diagnostic": {"stage": stage, "category": "infrastructure", "result": "failure",
                                  "message": "RUNNER_INTERNAL_ERROR", "output": "", "exit_code": None}}]}
            try:
                Campaign.validate_failure(broken, fixture)
            except ValueError:
                controls.append("metadata_only_" + fixture + "_rejected")
            else:
                raise AssertionError("unrelated failure accepted:" + fixture)
        require(NoRedirect().redirect_request(None, None, 302, "redirect", {}, "http://external.invalid") is None,
                "redirect_would_forward_authorization")
        controls.append("redirect_refused")
        from types import SimpleNamespace
        class FakeResponse:
            status = 401
            def __init__(self, body):
                self.body = body
                self.closed = False
                self.fp = SimpleNamespace(raw=SimpleNamespace(_sock=SimpleNamespace(settimeout=lambda n: None)))
            def __enter__(self): return self
            def __exit__(self, *args): self.close()
            def close(self): self.closed = True
            def read1(self, size):
                require(not self.closed, "response_closed_before_read")
                result, self.body = self.body[:size], self.body[size:]
                return result
        original_opener = urllib.request.build_opener
        def error_open(*args, **kwargs):
            raise urllib.error.HTTPError(config["url"], 401, "unauthorized", {}, FakeResponse(b'{"code":"unauthorized"}'))
        try:
            urllib.request.build_opener = lambda *args: SimpleNamespace(open=error_open)
            unauthorized = campaign.http("GET", config["url"], authenticated=False)
            require(unauthorized["status"] == 401 and unauthorized["error"] is None, "http_error_body_not_captured")
            controls.append("http_error_body_captured")
            urllib.request.build_opener = lambda *args: SimpleNamespace(open=lambda *args, **kwargs: FakeResponse(b"x"*(MAX_RESPONSE+1)))
            oversized = campaign.http("GET", config["url"])
            require(oversized["status"] is None and "response_budget" in oversized["error"], "oversized_response_accepted")
            controls.append("oversized_http_response_rejected")
        finally:
            urllib.request.build_opener = original_opener
        for name, code, timeout in (("timeout", "import time;time.sleep(5)", .1),
                                    ("output_cap", "import os;os.write(1,b'x'*2097152)", 3)):
            try:
                campaign.command([sys.executable, "-c", code], timeout=timeout)
            except ValueError:
                expected = "command_deadline" if name == "timeout" else "command_output_budget"
                require(expected in campaign.receipt["commands"][-1]["error"], "capture_did_not_reject_expected_condition")
                controls.append("bounded_capture_" + name)
            else:
                raise AssertionError("unbounded child accepted:" + name)
    # Exercise the real hold loop with a deterministic monotonic clock: no wait
    # and no service access. Every sleep is bounded and budget-checked.
    from unittest.mock import patch
    with tempfile.TemporaryDirectory(prefix="fogell-adversarial-hold-") as directory:
        campaign = Campaign(config, Path(directory), 3)
        clock = [100.0]
        sleeps, budget_checks = [], []
        campaign.started = 90.0
        campaign.budget = lambda: budget_checks.append(clock[0])
        def sleep(seconds):
            require(0 < seconds <= 0.2, "outage_sleep_not_bounded")
            sleeps.append(seconds)
            clock[0] += seconds
        with patch.object(time, "monotonic", side_effect=lambda: clock[0]), patch.object(time, "sleep", side_effect=sleep):
            measured = campaign.hold_postgres_outage()
        require(measured["hold_ms"] >= 20000 and measured["start_campaign_ms"] == 10000
                and measured["end_campaign_ms"] >= 30000 and len(budget_checks) == len(sleeps) + 1,
                "outage_hold_missing_duration_or_budget_checks")
        controls.append("postgres_hold_measured_20_seconds_with_bounded_budget_checks")
    # Deliver real process-local signals at mocked fault injection boundaries.
    # Only fake commands run: no deployment access, podman or systemctl calls.
    for kind, signum, during_hold in (("controller_kill", signal.SIGTERM, False),
                                     ("postgres_outage", signal.SIGINT, False),
                                     ("postgres_outage", signal.SIGTERM, True)):
        with tempfile.TemporaryDirectory(prefix="fogell-adversarial-interrupt-") as directory:
            campaign = Campaign(config, Path(directory), 3)
            calls = []
            originals = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
            def submit(label, fixture="simple", key=None):
                job = {"label": label, "admission": {"build_id": str(uuid.uuid4())}, "chunks": []}
                campaign.receipt["jobs"].append(job)
                return job
            campaign.submit = submit
            campaign.wait = lambda job, **kwargs: job
            campaign.page = lambda job: {"status": "queued" if "-queued-" in job["label"] else "running", "is_terminal": False}
            campaign.inspect = lambda cid: {"id": cid, "running": True, "pid": 123}
            campaign.resources = lambda: {}
            campaign.ready = lambda desired, **kwargs: [{"status": desired}]
            def command(args, **kwargs):
                calls.append((args, kwargs))
                injecting = args[:2] == ["podman", "kill"] or args[:3] == ["systemctl", "--user", "stop"]
                if injecting and not during_hold:
                    signal.raise_signal(signum)
                return {"exit_code": 0}
            campaign.command = command
            def execute():
                campaign.scenario(kind + "-1", lambda: campaign.fault(kind, 1))
                calls.append((['next_fault'], {}))
            campaign.execute = execute
            from unittest.mock import patch
            if during_hold:
                with patch.object(time, "sleep", side_effect=lambda _: signal.raise_signal(signum)):
                    execute_campaign(campaign)
            else:
                execute_campaign(campaign)
            expected = ["systemctl", "--user", "restart", config["service_unit"]] if kind == "controller_kill" else ["systemctl", "--user", "start", config["postgres_service_unit"]]
            require(len(calls) == 2 and calls[-1][0] == expected and calls[-1][1].get('recovery') is True,
                    "interruption did not restore only the owned service before abort")
            saved = json.loads((Path(directory)/'receipt.json').read_text())
            require(saved['passed'] is False and saved['interrupted'] is True and
                    saved['error'].startswith('CampaignInterrupted:') and len(saved['scenarios']) == 1,
                    "interrupted campaign did not retain failure or continued to another fault")
            require(all(signal.getsignal(sig) == handler for sig, handler in originals.items()), 'signal handler leaked after campaign')
            controls.append(kind + ('_hold' if during_hold else '') + '_signal_restores_owned_service_and_aborts')
    print(json.dumps({"passed": True, "controls": controls}))
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true", help="portable safety controls; no deployment access")
    parser.add_argument("--repetitions", type=int, default=3, choices=range(3, 11))
    parser.add_argument("--execute", action="store_true", help="inject faults after explicit deployment handoff; default declares only")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    require(args.config is not None and args.output is not None, "config_and_output_required")
    config = json.loads(args.config.read_text())
    validate_config(config)
    os.umask(0o077)
    args.output.mkdir(parents=True, exist_ok=False)
    campaign = Campaign(config, args.output, args.repetitions)
    if not args.execute:
        atomic_json(args.output / "declaration.json", campaign.receipt["declaration"])
        print(json.dumps({"declared_only": True, "output": str(args.output)}))
        return 0
    execute_campaign(campaign)
    print(json.dumps({"passed": campaign.receipt["passed"], "scenarios": len(campaign.receipt["scenarios"]),
                      "censored_jobs": campaign.receipt["censored_jobs"], "output": str(args.output)}))
    return 0 if campaign.receipt["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
