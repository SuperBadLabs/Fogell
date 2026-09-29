import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone


ROOT = Path(__file__).resolve().parents[1]
CHECK_PATH = ROOT / "scripts" / "qualification" / "operator-check.py"
BACKUP_PATH = ROOT / "scripts" / "paired-backup.py"


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


operator_check = load_module("operator_check", CHECK_PATH)
paired_backup = load_module("paired_backup", BACKUP_PATH)


class OperatorCheckTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.backups = self.root / "backups"
        self.backups.mkdir()
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        pg_restore = bin_dir / "pg_restore"
        pg_restore.write_text("#!/bin/sh\nprintf 'custom archive listing\\n'\n", encoding="utf-8")
        pg_restore.chmod(0o755)
        self.old_path = os.environ.get("PATH", "")
        os.environ["PATH"] = str(bin_dir) + os.pathsep + self.old_path
        self.addCleanup(self.restore_path)

    def restore_path(self):
        os.environ["PATH"] = self.old_path

    def make_point(self, name, created_at):
        point = self.backups / name
        point.mkdir()
        database = point / "database.custom"
        database.write_bytes(b"fixture postgres custom archive")
        state = self.root / f"state-{name}"
        state.mkdir()
        (state / "record").write_text("fixture\n", encoding="utf-8")
        import tarfile
        with tarfile.open(point / "state.tar", "w:") as archive:
            archive.add(state, arcname=".", recursive=True)
        entries = paired_backup.inventory(state)
        manifest = {
            "format": 1, "status": "complete", "paired": True,
            "created_at": created_at.isoformat(),
            "writers_quiesced": True, "release_id": "fixture",
            "schema_version": "17",
            "state_inventory_sha256": paired_backup.canonical_hash(entries),
            "state_entries": len(entries),
            "files": {item.name: {"sha256": paired_backup.digest(item), "size": item.stat().st_size}
                      for item in (database, point / "state.tar")},
        }
        (point / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        return point

    def test_valid_pair_is_clear_then_tampering_alerts(self):
        now = datetime.now(timezone.utc)
        point = self.make_point("point", now)
        alerts = []
        snapshot = operator_check.check_backup_evidence(self.backups, now, 24, alerts)
        self.assertEqual(snapshot["complete_count"], 1)
        self.assertEqual(alerts, [])

        (point / "database.custom").write_bytes(b"tampered")
        alerts = []
        snapshot = operator_check.check_backup_evidence(self.backups, now, 24, alerts)
        self.assertEqual(snapshot["complete_count"], 0)
        self.assertIn("point", snapshot["invalid_points"])
        self.assertEqual({item["code"] for item in alerts},
                         {"backup_invalid_point", "backup_missing"})

    def test_stale_pair_alerts_and_clears_after_new_verified_pair(self):
        now = datetime.now(timezone.utc)
        self.make_point("old", now.replace(year=now.year - 1))
        alerts = []
        operator_check.check_backup_evidence(self.backups, now, 24, alerts)
        self.assertIn("backup_stale", {item["code"] for item in alerts})

        old = self.backups / "old"
        import shutil
        shutil.rmtree(old)
        self.make_point("fresh", now)
        alerts = []
        operator_check.check_backup_evidence(self.backups, now, 24, alerts)
        self.assertEqual(alerts, [])

    def test_failed_attempt_alerts_until_a_newer_verified_point_and_keeps_history(self):
        now = datetime.now(timezone.utc)
        failure = {
            "timestamp": (now - timedelta(hours=1)).isoformat(),
            "output": "failed-point", "failure_class": "CalledProcessError",
        }
        failure_path = self.backups / "failed-point.20260929T000000Z.failed.json"
        failure_path.write_text(json.dumps(failure), encoding="utf-8")

        alerts = []
        snapshot = operator_check.check_backup_evidence(self.backups, now, 24, alerts)
        self.assertIn("backup_failed_evidence", {item["code"] for item in alerts})
        self.assertEqual(snapshot["unresolved_failures"], [failure_path.name])

        self.make_point("recovered", now.replace(microsecond=0))
        alerts = []
        snapshot = operator_check.check_backup_evidence(
            self.backups, now + timedelta(seconds=2), 24, alerts)
        self.assertNotIn("backup_failed_evidence", {item["code"] for item in alerts})
        self.assertEqual(snapshot["unresolved_failures"], [])
        self.assertTrue(snapshot["failed_attempts"][0]["resolved"])
        self.assertTrue(failure_path.is_file(), "resolved failure evidence remains in history")

    def test_nonfinite_backup_age_thresholds_are_rejected(self):
        for value in ("nan", "inf", "-inf"):
            with self.subTest(value=value):
                result = subprocess.run(
                    [sys.executable, str(CHECK_PATH), "--organization",
                     "00000000-0000-0000-0000-000000000001", "--filesystem", str(self.root),
                     "--backup-dir", str(self.backups), "--max-backup-age-hours=" + value],
                    check=False, capture_output=True, text=True,
                )
                self.assertEqual(result.returncode, 2)
                self.assertIn("thresholds must be positive", result.stderr)


if __name__ == "__main__":
    unittest.main()
