#!/usr/bin/env python3
"""Read-only Fogell operator checks for durable work and capacity pressure.

Database access uses libpq's standard PG* environment/service settings and psql.
The configured role needs SELECT access only. This command never changes state.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from typing import Any


SQL = """
SELECT json_build_object(
  'reconciliation_required', (SELECT count(*) FROM attempts WHERE state='reconciliation_required'),
  'retention_held', (SELECT count(*) FROM build_retention WHERE state='held'),
  'database_bytes', pg_database_size(current_database())
)::text;
"""


def utc_now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def parse_time(value: str) -> dt.datetime:
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("timestamp must include a timezone")
    return parsed.astimezone(dt.timezone.utc)


def add_alert(alerts: list[dict[str, Any]], code: str, message: str, action: str,
              observed: Any, threshold: Any) -> None:
    alerts.append({"code": code, "severity": "warning", "message": message,
                   "action": action, "observed": observed, "threshold": threshold})


def check_backup_evidence(directory: Path | None, now: dt.datetime, max_age_hours: float,
                          alerts: list[dict[str, Any]]) -> dict[str, Any]:
    if directory is None:
        return {"configured": False}
    if not directory.is_dir():
        add_alert(alerts, "backup_directory_missing", "Backup evidence directory is unavailable.",
                  "Restore the backup job's evidence path and verify the latest paired backup.",
                  str(directory), "existing directory")
        return {"configured": True, "directory": str(directory), "latest_complete": None,
                "failed_evidence": 1}

    complete: list[tuple[dt.datetime, str, dict[str, Any]]] = []
    invalid_points: list[str] = []
    failure_history: list[dict[str, Any]] = []
    for entry in sorted(directory.iterdir()):
        if entry.name.endswith(".failed.json"):
            try:
                record = json.loads(entry.read_text(encoding="utf-8"))
                failed_at = parse_time(str(record["timestamp"]))
                output_name = record["output"]
                failure_class = record["failure_class"]
                if not isinstance(output_name, str) or not isinstance(failure_class, str):
                    raise ValueError("invalid failure fields")
                failure_history.append({"record": entry.name, "timestamp": failed_at,
                                        "output": output_name, "failure_class": failure_class})
            except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError):
                failure_history.append({"record": entry.name, "timestamp": None,
                                        "output": None, "failure_class": "invalid_record"})
        elif entry.is_dir():
            try:
                checker = Path(__file__).resolve().parents[1] / "paired-backup.py"
                result = subprocess.run([sys.executable, str(checker), "check", str(entry)],
                                        check=False, capture_output=True, text=True, timeout=30)
                if result.returncode != 0:
                    invalid_points.append(entry.name)
                    continue
                verified = json.loads(result.stdout)
                created = parse_time(str(verified["created_at"]))
                complete.append((created, entry.name, verified))
            except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError,
                    subprocess.TimeoutExpired):
                invalid_points.append(entry.name)

    newest = max(complete, key=lambda record: record[0], default=None)
    age_seconds = (now - newest[0]).total_seconds() if newest else None
    newest_time = newest[0] if newest else None
    unresolved = [record for record in failure_history
                  if record["timestamp"] is None or newest_time is None or newest_time <= record["timestamp"]]
    for record in failure_history:
        if record["timestamp"] is not None:
            record["timestamp"] = record["timestamp"].isoformat()
            record["resolved"] = newest_time is not None and newest_time > parse_time(record["timestamp"])
        else:
            record["resolved"] = False
    if unresolved:
        add_alert(alerts, "backup_failed_evidence", "Failed or malformed backup evidence was found.",
                  "Repair the backup failure and produce a newer verified paired recovery point; retain the failure record for history.",
                  [record["record"] for record in unresolved], 0)
    if invalid_points:
        add_alert(alerts, "backup_invalid_point", "An existing paired recovery point failed verification.",
                  "Preserve the point for diagnosis, then create and verify a replacement recovery point.",
                  invalid_points, 0)
    if newest is None:
        add_alert(alerts, "backup_missing", "No complete paired backup manifest was found.",
                  "Run the paired backup procedure and verify both database and state-root inventories.",
                  None, "at least one complete paired manifest")
    elif age_seconds is not None and age_seconds > max_age_hours * 3600:
        add_alert(alerts, "backup_stale", "The latest complete paired backup exceeds the configured age.",
                  "Run and verify a new paired backup; retain the prior evidence until the new pair is checked.",
                  round(age_seconds / 3600, 3), max_age_hours)
    return {"configured": True, "directory": str(directory),
            "latest_complete": ({"created_at": newest[0].isoformat(), "directory": newest[1],
                                 "verification": newest[2]}
                                if newest else None),
            "age_hours": round(age_seconds / 3600, 3) if age_seconds is not None else None,
            "failed_attempts": failure_history,
            "unresolved_failures": [record["record"] for record in unresolved],
            "invalid_points": invalid_points, "complete_count": len(complete)}


def database_snapshot(organization: str) -> dict[str, int]:
    psql = shutil.which("psql")
    if not psql:
        raise RuntimeError("psql was not found on PATH")
    if not (os.environ.get("PGSERVICE") or os.environ.get("PGDATABASE")):
        raise RuntimeError("configure PG* libpq variables or PGSERVICE for the read-only check role")
    sql = ("BEGIN; SET LOCAL fogell.organization_id = '" + organization + "';\n"
           + SQL + "\nCOMMIT;")
    result = subprocess.run([psql, "-X", "-qAt", "-v", "ON_ERROR_STOP=1", "-c", sql],
                            check=False, capture_output=True, text=True, timeout=15)
    if result.returncode != 0:
        raise RuntimeError("database query failed; check connectivity, read-only SELECT grants, and schema")
    try:
        row = json.loads(result.stdout.strip())
        return {key: int(row[key]) for key in
                ("reconciliation_required", "retention_held", "database_bytes")}
    except (ValueError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise RuntimeError("database returned an invalid health snapshot") from error


def filesystem_snapshot(paths: list[Path], min_free_bytes: int,
                        min_free_inodes: int, alerts: list[dict[str, Any]]) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []
    for path in paths:
        try:
            usage = os.statvfs(path)
            free_bytes = usage.f_bavail * usage.f_frsize
            free_inodes = usage.f_favail
            record = {"path": str(path), "free_bytes": free_bytes,
                      "total_bytes": usage.f_blocks * usage.f_frsize,
                      "free_inodes": free_inodes, "total_inodes": usage.f_files}
            results.append(record)
            if free_bytes < min_free_bytes:
                add_alert(alerts, "filesystem_bytes_low", "Filesystem free bytes are below the configured floor.",
                          "Inspect the mount, workload growth and retention policy; do not delete pool-control evidence.",
                          {"path": str(path), "free_bytes": free_bytes}, min_free_bytes)
            if free_inodes < min_free_inodes:
                add_alert(alerts, "filesystem_inodes_low", "Filesystem free inodes are below the configured floor.",
                          "Inspect small-file growth and mount capacity before admitting more work.",
                          {"path": str(path), "free_inodes": free_inodes}, min_free_inodes)
        except OSError as error:
            add_alert(alerts, "filesystem_unavailable", "Configured filesystem path cannot be inspected.",
                      "Restore the expected mount and verify its identity before resuming work.",
                      {"path": str(path), "error": error.strerror}, "readable mounted path")
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--organization", required=True,
                        help="tenant UUID used to scope RLS-protected Fogell tables")
    parser.add_argument("--filesystem", type=Path, action="append", required=True,
                        help="filesystem mount/path to inspect; repeat for state, workspace and database mounts")
    parser.add_argument("--backup-dir", type=Path, required=True,
                        help="dedicated directory whose immediate child directories are paired backup points")
    parser.add_argument("--max-backup-age-hours", type=float, default=24.0)
    parser.add_argument("--min-free-bytes", type=int, default=10 * 1024**3)
    parser.add_argument("--min-free-inodes", type=int, default=100_000)
    parser.add_argument("--max-database-bytes", type=int, default=100 * 1024**3,
                        help="soft alarm on current database size; calibrate against database-volume capacity")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
                        args.organization):
        parser.error("--organization must be a UUID")
    if (not math.isfinite(args.max_backup_age_hours) or args.max_backup_age_hours <= 0 or args.min_free_bytes < 0 or
            args.min_free_inodes < 0 or args.max_database_bytes <= 0):
        parser.error("thresholds must be positive (free-space floors may be zero)")

    now = utc_now()
    alerts: list[dict[str, Any]] = []
    checks: dict[str, Any] = {}
    try:
        db = database_snapshot(args.organization)
        checks["database"] = db
        if db["reconciliation_required"] > 0:
            add_alert(alerts, "reconciliation_required", "Attempts require operator reconciliation.",
                      "Review attempt evidence and the recovery runbook; never replay uncertain external effects automatically.",
                      db["reconciliation_required"], 0)
        if db["retention_held"] > 0:
            add_alert(alerts, "retention_held", "Retention deletion journals are held.",
                      "Inspect build_retention.error and the retention runbook; preserve the journal and payload evidence.",
                      db["retention_held"], 0)
        if db["database_bytes"] > args.max_database_bytes:
            add_alert(alerts, "database_size_high", "Current database size exceeds the configured soft limit.",
                      "Compare database growth with its backing volume and planned retention; PostgreSQL may not shrink files after deletes.",
                      db["database_bytes"], args.max_database_bytes)
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        checks["database_error"] = str(error)
        add_alert(alerts, "database_check_failed", "Database health could not be read.",
                  "Restore read-only database connectivity and verify the schema before treating the check as healthy.",
                  str(error), "successful read-only query")

    checks["filesystems"] = filesystem_snapshot(args.filesystem, args.min_free_bytes,
                                                  args.min_free_inodes, alerts)
    checks["backups"] = check_backup_evidence(args.backup_dir, now,
                                               args.max_backup_age_hours, alerts)
    report = {"schema_version": 1, "checked_at": now.isoformat(),
              "status": "alert" if alerts else "ok", "checks": checks,
              "thresholds": {"min_free_bytes": args.min_free_bytes,
                             "min_free_inodes": args.min_free_inodes,
                             "max_database_bytes": args.max_database_bytes,
                             "max_backup_age_hours": args.max_backup_age_hours},
              "alerts": alerts}
    print(json.dumps(report, sort_keys=True, separators=(",", ":")))
    return 1 if alerts else 0


if __name__ == "__main__":
    raise SystemExit(main())
