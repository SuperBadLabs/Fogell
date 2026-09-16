#!/usr/bin/env python3
"""Exercise collector ownership cleanup without booting a VM."""
import json
import pathlib
import tempfile
import unittest
import contextlib
from unittest import mock

import collect


IDENT = "a" * 64


class CollectorCleanup(unittest.TestCase):
    def setUp(self):
        collect.vm.OWNED_ID = None

    def test_stopped_collector_uses_captured_id_for_removal(self):
        stopped = AssertionError("owned VM was already stopped")
        with mock.patch.object(collect.vm, "stop", side_effect=stopped) as stop, mock.patch.object(
            collect.vm, "cleanup_owned"
        ) as cleanup:
            with self.assertRaises(AssertionError) as raised:
                collect.finish_collector(IDENT)
        self.assertIs(raised.exception, stopped)
        stop.assert_called_once_with("evidence-collection", container_id=IDENT)
        cleanup.assert_called_once_with(IDENT, stopped)

    def test_primary_error_survives_double_cleanup_failure(self):
        primary = RuntimeError("evidence failed")
        with mock.patch.object(collect.vm, "stop", side_effect=OSError("stop failed")), mock.patch.object(
            collect.vm, "cleanup_owned", side_effect=OSError("remove failed")
        ):
            collect.finish_collector(IDENT, primary)
        self.assertIn("stop failed", " ".join(primary.__notes__))
        self.assertIn("remove failed", " ".join(primary.__notes__))

    def test_real_finalizer_stops_and_removes_exact_running_id(self):
        state = {
            "Id": IDENT,
            "Config": {"Labels": {"io.fogell.persistence.boot": "d" * 32}},
            "State": {"Running": True, "Pid": 42},
        }
        removed = []
        def inspect(container_id=None):
            self.assertEqual(container_id, IDENT)
            return None if removed else state
        def run(args, **_):
            self.assertEqual(args, ["podman", "rm", "--force", IDENT])
            removed.append(IDENT)
            return mock.Mock(stdout="")
        def guest(*_, **kwargs):
            self.assertEqual(kwargs["container_id"], IDENT)
            state["State"].update(Running=False, ExitCode=0)
            return mock.Mock(returncode=0)
        collect.vm.OWNED_ID = IDENT
        with mock.patch.object(collect.vm, "inspect", side_effect=inspect), mock.patch.object(
            collect.vm, "run", side_effect=run
        ), mock.patch.object(collect.vm, "guest", side_effect=guest), mock.patch.object(
            collect.vm, "disk_identity", return_value={}
        ), mock.patch.object(collect.vm, "record"):
            collect.finish_collector(IDENT)
        self.assertEqual(removed, [IDENT])
        self.assertIsNone(collect.vm.OWNED_ID)

    def test_real_finalizer_removes_stopped_id_without_name_lookup(self):
        state = {
            "Id": IDENT,
            "Config": {"Labels": {"io.fogell.persistence.boot": "d" * 32}},
            "State": {"Running": False, "Pid": 42},
        }
        removed = []
        def inspect(container_id=None):
            self.assertEqual(container_id, IDENT)
            return None if removed else state
        def run(args, **_):
            self.assertEqual(args, ["podman", "rm", "--force", IDENT])
            removed.append(IDENT)
            return mock.Mock(stdout="")
        collect.vm.OWNED_ID = IDENT
        primary = RuntimeError("collection failed")
        with mock.patch.object(collect.vm, "inspect", side_effect=inspect), mock.patch.object(
            collect.vm, "run", side_effect=run
        ):
            collect.finish_collector(IDENT, primary)
        self.assertEqual(removed, [IDENT])
        self.assertIsNone(collect.vm.OWNED_ID)
        self.assertIn("already stopped", " ".join(primary.__notes__))

    def test_main_restores_record_and_preserves_primary_after_cleanup_error(self):
        original_record = collect.vm.record
        primary = KeyboardInterrupt()
        with tempfile.TemporaryDirectory() as directory:
            before = pathlib.Path(directory) / "before.json"
            before.write_text(json.dumps([]), encoding="utf-8")
            def boot(_):
                collect.vm.OWNED_ID = IDENT
            with mock.patch("sys.argv", ["collect.py", "--protected-before", str(before)]), mock.patch.object(
                collect, "protected_projection", return_value=[]
            ), mock.patch.object(collect.vm, "boot", side_effect=boot), mock.patch.object(
                collect, "guest_evidence", side_effect=primary
            ), mock.patch.object(collect, "finish_collector", side_effect=OSError("cleanup failed")) as finish:
                with self.assertRaises(KeyboardInterrupt) as raised:
                    collect.main()
        self.assertIs(raised.exception, primary)
        self.assertIs(collect.vm.record, original_record)
        finish.assert_called_once_with(IDENT, primary)
        self.assertIn("cleanup failed", " ".join(primary.__notes__))

    def test_main_restores_record_after_real_stop_and_removal_failures(self):
        original_record = collect.vm.record
        primary = RuntimeError("evidence failed")
        with tempfile.TemporaryDirectory() as directory:
            before = pathlib.Path(directory) / "before.json"
            before.write_text(json.dumps([]), encoding="utf-8")
            def boot(_):
                collect.vm.OWNED_ID = IDENT
            with mock.patch("sys.argv", ["collect.py", "--protected-before", str(before)]), mock.patch.object(
                collect, "protected_projection", return_value=[]
            ), mock.patch.object(collect.vm, "boot", side_effect=boot), mock.patch.object(
                collect, "guest_evidence", side_effect=primary
            ), mock.patch.object(collect.vm, "stop", side_effect=OSError("stop failed")), mock.patch.object(
                collect.vm, "inspect", side_effect=OSError("inspect failed")
            ):
                with self.assertRaises(RuntimeError) as raised:
                    collect.main()
        self.assertIs(raised.exception, primary)
        self.assertIs(collect.vm.record, original_record)
        notes = " ".join(primary.__notes__)
        self.assertIn("stop failed", notes)
        self.assertIn("inspect failed", notes)

    def test_main_failure_points_use_captured_id_and_restore_callback(self):
        failure_points = ("guest", "disks", "qemu", "protected", "record", "write")
        for point in failure_points:
            with self.subTest(point=point), tempfile.TemporaryDirectory() as directory:
                before = pathlib.Path(directory) / "before.json"
                before.write_text(json.dumps([]), encoding="utf-8")
                primary = RuntimeError(point + " failed")
                original_record = collect.vm.record
                def boot(_):
                    collect.vm.OWNED_ID = IDENT
                    if point == "record":
                        collect.vm.record("vm_start")
                with contextlib.ExitStack() as stack:
                    stack.enter_context(mock.patch("sys.argv", ["collect.py", "--protected-before", str(before)]))
                    stack.enter_context(mock.patch.object(collect, "protected_projection", return_value=[]))
                    stack.enter_context(mock.patch.object(collect.vm, "boot", side_effect=boot))
                    guest = stack.enter_context(mock.patch.object(collect, "guest_evidence", return_value={}))
                    stack.enter_context(mock.patch.object(collect, "host_disks", return_value={}))
                    stack.enter_context(mock.patch.object(collect, "qemu_version", return_value="qemu"))
                    stack.enter_context(mock.patch.object(collect, "host_protected", return_value=[]))
                    stack.enter_context(mock.patch.object(collect, "write_json"))
                    stack.enter_context(mock.patch.object(collect, "finish_collector"))
                    if point == "guest": guest.side_effect = primary
                    elif point == "disks": collect.host_disks.side_effect = primary
                    elif point == "qemu": collect.qemu_version.side_effect = primary
                    elif point == "protected": stack.enter_context(mock.patch.object(collect, "fail", side_effect=primary)); collect.host_protected.return_value = ["changed"]
                    elif point == "record": stack.enter_context(mock.patch.object(collect, "collector_record", side_effect=primary))
                    elif point == "write": collect.write_json.side_effect = primary
                    with self.assertRaises(RuntimeError) as raised:
                        collect.main()
                    self.assertIs(raised.exception, primary)
                    collect.finish_collector.assert_called_once_with(None if point == "record" else IDENT, primary)
                    if point != "record":
                        guest.assert_called_once_with(IDENT)
                self.assertIs(collect.vm.record, original_record)

    def test_main_success_stops_captured_id_and_restores_callback(self):
        original_record = collect.vm.record
        with tempfile.TemporaryDirectory() as directory:
            before = pathlib.Path(directory) / "before.json"
            before.write_text(json.dumps([]), encoding="utf-8")
            def boot(_):
                collect.vm.OWNED_ID = IDENT
            with mock.patch("sys.argv", ["collect.py", "--protected-before", str(before)]), mock.patch.object(
                collect, "protected_projection", return_value=[]
            ), mock.patch.object(collect.vm, "boot", side_effect=boot), mock.patch.object(
                collect, "guest_evidence", return_value={}
            ) as guest, mock.patch.object(collect, "host_disks", return_value={}), mock.patch.object(
                collect, "qemu_version", return_value="qemu"), mock.patch.object(
                collect, "host_protected", return_value=[]), mock.patch.object(
                collect, "write_json"
            ), mock.patch.object(collect, "finish_collector") as finish:
                collect.main()
        guest.assert_called_once_with(IDENT)
        finish.assert_called_once_with(IDENT)
        self.assertIs(collect.vm.record, original_record)


if __name__ == "__main__":
    unittest.main()
