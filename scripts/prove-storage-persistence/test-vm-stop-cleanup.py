#!/usr/bin/env python3
"""Exercise shutdown failures and ID-based ownership without a VM."""
import contextlib
import copy
import types
import unittest
from unittest import mock

import vm


class StopCleanup(unittest.TestCase):
    def setUp(self):
        self.ident = "a" * 64
        self.before = {
            "Id": self.ident,
            "Config": {"Labels": {"io.fogell.persistence.boot": "d" * 32}},
            "State": {"Running": True, "Pid": 42},
        }
        self.after = copy.deepcopy(self.before)
        self.after["State"].update(Running=False, ExitCode=0)
        self.removed = []
        self.cleanup_failure = None
        self.stack = contextlib.ExitStack()
        self.addCleanup(self.stack.close)
        vm.OWNED_ID = self.ident
        self.inspect_by_id_calls = 0
        self.stack.enter_context(mock.patch.object(vm, "run", self.fake_run))
        self.inspector = self.stack.enter_context(mock.patch.object(vm, "inspect", self.inspect))
        self.records = self.stack.enter_context(mock.patch.object(vm, "record"))
        self.disks = self.stack.enter_context(mock.patch.object(vm, "disk_identity", return_value={}))
        self.guest = self.stack.enter_context(mock.patch.object(vm, "guest"))

    def inspect(self, container_id=None):
        if container_id is None:
            return self.before
        self.assertEqual(container_id, self.ident)
        self.inspect_by_id_calls += 1
        return self.before if self.inspect_by_id_calls == 1 else self.after

    def fake_run(self, args, **kwargs):
        if args[:3] == ["podman", "rm", "--force"]:
            self.assertEqual(args[3], self.ident)
            self.removed.append(args[3])
            if self.cleanup_failure:
                raise self.cleanup_failure
        else:
            self.assertEqual(args, ["podman", "kill", "--signal", "KILL", self.ident])
        return types.SimpleNamespace(stdout="")

    def test_clean_shutdown_uses_owned_id(self):
        vm.stop("test")
        self.guest.assert_called_once_with(
            ["sudo", "poweroff"], check=False, timeout=10, container_id=self.ident
        )
        self.assertEqual(self.removed, [self.ident])

    def test_abrupt_shutdown_uses_owned_id(self):
        self.after["State"]["ExitCode"] = 137
        vm.stop("test", abrupt=True)
        self.guest.assert_not_called()
        self.assertEqual(self.removed, [self.ident])

    def test_timeout_removes_owned_id(self):
        with mock.patch.object(vm.time, "monotonic", side_effect=[0, 61]):
            with self.assertRaises(TimeoutError):
                vm.stop("test")
        self.assertEqual(self.removed, [self.ident])

    def test_record_failure_removes_owned_id(self):
        self.records.side_effect = OSError("journal write failed")
        with self.assertRaises(OSError):
            vm.stop("test")
        self.assertEqual(self.removed, [self.ident])

    def test_disk_read_failure_removes_owned_id(self):
        self.disks.side_effect = OSError("disk lookup failed")
        with self.assertRaises(OSError):
            vm.stop("test")
        self.assertEqual(self.removed, [self.ident])

    def test_already_stopped_owned_vm_is_removed(self):
        self.before["State"]["Running"] = False
        with self.assertRaisesRegex(AssertionError, "already stopped"):
            vm.stop("test")
        self.assertEqual(self.removed, [self.ident])

    def test_replacement_name_never_becomes_shutdown_target(self):
        def replacement(*args, **kwargs):
            self.before = {"Id": "b" * 64, "Config": {"Labels": None}}
        self.guest.side_effect = replacement
        vm.stop("test")
        self.assertEqual(self.before["Id"], "b" * 64)
        self.assertEqual(self.removed, [self.ident])

    def test_unknown_owner_is_not_removed(self):
        self.before["Config"]["Labels"] = None
        with self.assertRaisesRegex(AssertionError, "differs from the owned target"):
            vm.stop("test")
        self.guest.assert_not_called()
        self.assertEqual(self.removed, [])

    def test_explicit_id_refuses_different_observed_owned_vm(self):
        self.before["Id"] = "b" * 64
        self.before["Config"]["Labels"] = {"io.fogell.persistence.boot": "e" * 32}
        with self.assertRaisesRegex(AssertionError, "differs from the owned target"):
            vm.stop("test", container_id=self.ident)
        self.assertEqual(self.removed, [])

    def test_cleanup_failure_preserves_primary_exception(self):
        primary = OSError("evidence write failed")
        self.records.side_effect = primary
        self.cleanup_failure = RuntimeError("Podman unavailable")
        with self.assertRaises(OSError) as caught:
            vm.stop("test")
        self.assertIs(caught.exception, primary)
        self.assertIn("cleanup also failed", " ".join(primary.__notes__))

    def test_cleanup_failure_is_reported_after_successful_shutdown(self):
        self.cleanup_failure = RuntimeError("Podman unavailable")
        with self.assertRaises(RuntimeError):
            vm.stop("test")


if __name__ == "__main__":
    unittest.main()
