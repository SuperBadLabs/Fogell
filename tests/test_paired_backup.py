import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "paired-backup.py"
spec = importlib.util.spec_from_file_location("paired_backup", SCRIPT)
paired_backup = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(paired_backup)


class PairedBackupTests(unittest.TestCase):
    def test_create_writes_a_checkable_pair_with_fake_libpq_tools(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root = base / "state"
            root.mkdir()
            (root / "artifact").write_bytes(b"local fixture")
            bindir = base / "bin"
            bindir.mkdir()
            dump = bindir / "pg_dump"
            dump.write_text("#!/bin/sh\nprintf 'custom archive fixture'\n")
            dump.chmod(0o755)
            psql = bindir / "psql"
            psql.write_text("#!/bin/sh\nprintf '17\\n'\n")
            psql.chmod(0o755)
            restore = bindir / "pg_restore"
            restore.write_text("#!/bin/sh\ncat >/dev/null\nprintf 'TABLE public fixture\\n'\n")
            restore.chmod(0o755)
            old_path = os.environ.get("PATH", "")
            old_url = os.environ.get("FOGELL_MAINTENANCE_DATABASE_URL")
            try:
                os.environ["PATH"] = str(bindir) + os.pathsep + old_path
                os.environ["FOGELL_MAINTENANCE_DATABASE_URL"] = (
                    'Host=127.0.0.1;Port=5432;Username=fogell;Database=fixture;Password="p;w"'
                )
                args = type("Args", (), {
                    "state_root": str(root), "output": str(base / "point"),
                    "release_id": "fixture-commit", "writers_quiesced": True,
                })()
                result = paired_backup.create(args)
                checked = paired_backup.check(base / "point")
            finally:
                os.environ["PATH"] = old_path
                if old_url is None:
                    os.environ.pop("FOGELL_MAINTENANCE_DATABASE_URL", None)
                else:
                    os.environ["FOGELL_MAINTENANCE_DATABASE_URL"] = old_url
            self.assertEqual(result["schema_version"], "17")
            self.assertEqual(result["release_id"], "fixture-commit")
            self.assertEqual(checked, result)
            manifest = json.loads((base / "point" / "manifest.json").read_text())
            self.assertTrue(manifest["paired"])
            self.assertEqual(manifest["status"], "complete")

    def test_npgsql_connection_string_is_mapped_to_private_environment(self):
        environment = paired_backup.libpq_environment(
            'Host=localhost;Port=5432;Username=fogell;Database=fixture;Password="p;w";SSL Mode=Verify Full'
        )
        self.assertEqual(environment["PGHOST"], "localhost")
        self.assertEqual(environment["PGPORT"], "5432")
        self.assertEqual(environment["PGUSER"], "fogell")
        self.assertEqual(environment["PGDATABASE"], "fixture")
        self.assertEqual(environment["PGPASSWORD"], "p;w")
        self.assertEqual(environment["PGSSLMODE"], "verify-full")

    def test_archive_inventory_matches_tree_and_detects_tampering(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "state"
            root.mkdir()
            (root / "payload").write_bytes(b"fixture")
            (root / "nested").mkdir()
            (root / "nested" / "metadata").write_text("version=1\n")
            (root / "link").symlink_to("payload")
            expected = paired_backup.inventory(root)
            archive_path = Path(temporary) / "state.tar"
            import tarfile
            with tarfile.open(archive_path, "w:") as archive:
                archive.add(root, arcname=".", recursive=True)
            self.assertEqual(expected, paired_backup.state_inventory_from_tar(archive_path))

            backup = Path(temporary) / "point"
            backup.mkdir()
            bindir = Path(temporary) / "bin"
            bindir.mkdir()
            restore_tool = bindir / "pg_restore"
            restore_tool.write_text("#!/bin/sh\ncat >/dev/null\nprintf 'TABLE fixture\\n'\n")
            restore_tool.chmod(0o755)
            (backup / "database.custom").write_bytes(b"database")
            (backup / "state.tar").write_bytes(archive_path.read_bytes())
            manifest = {
                "format": 1,
                "status": "complete",
                "paired": True,
                "writers_quiesced": True,
                "created_at": "2026-09-29T00:00:00+00:00",
                "release_id": "test-build",
                "schema_version": "17",
                "state_inventory_sha256": paired_backup.canonical_hash(expected),
                "state_entries": len(expected),
                "files": {
                    name: {"sha256": paired_backup.digest(backup / name),
                           "size": (backup / name).stat().st_size}
                    for name in ("database.custom", "state.tar")
                },
            }
            (backup / "manifest.json").write_text(json.dumps(manifest))
            old_path = os.environ.get("PATH", "")
            os.environ["PATH"] = str(bindir) + os.pathsep + old_path
            try:
                checked = paired_backup.check(backup)
            finally:
                os.environ["PATH"] = old_path
            self.assertEqual(checked["state_entries"], 4)
            (backup / "database.custom").write_bytes(b"changed")
            os.environ["PATH"] = str(bindir) + os.pathsep + old_path
            try:
                with self.assertRaisesRegex(ValueError, "changed recovery file"):
                    paired_backup.check(backup)
            finally:
                os.environ["PATH"] = old_path

    def test_state_inventory_refuses_special_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fifo = root / "unexpected-pipe"
            os.mkfifo(fifo)
            with self.assertRaisesRegex(ValueError, "special file"):
                paired_backup.inventory(root)

    def test_destination_must_not_overlap_state_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "state"
            root.mkdir()
            with self.assertRaisesRegex(ValueError, "outside and separate"):
                paired_backup.safe_relative_state(root, root / "backup")


if __name__ == "__main__":
    unittest.main()
