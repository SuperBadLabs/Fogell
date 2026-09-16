#!/usr/bin/env python3
"""Check VM ownership and cleanup without running Podman or a guest."""
import contextlib
import subprocess
import types
import unittest
from unittest import mock

import vm


class BootCleanup(unittest.TestCase):
    def setUp(self):
        self.created_id = "a" * 64
        self.other_id = "b" * 64
        self.container = None
        self.removed = []
        self.start_failure = None
        self.collide = False
        self.stack = contextlib.ExitStack()
        self.addCleanup(self.stack.close)
        self.stack.enter_context(mock.patch.object(vm, "run", self.fake_run))
        self.stack.enter_context(mock.patch.object(vm, "inspect", lambda: self.container))
        self.stack.enter_context(mock.patch.object(vm, "disk_identity", return_value={}))
        self.records = self.stack.enter_context(mock.patch.object(vm, "record"))
        self.guest = self.stack.enter_context(mock.patch.object(
            vm, "guest", return_value=types.SimpleNamespace(returncode=0, stdout="boot-id\n")
        ))

    def fake_run(self, args, **kwargs):
        if args[:2] == ["podman", "run"]:
            label = args[args.index("--label") + 1].split("=", 1)
            self.container = {
                "Id": self.other_id if self.collide else self.created_id,
                "Config": {"Labels": None if self.collide else {label[0]: label[1]}},
                "State": {"Running": True, "Pid": 42},
            }
            if self.start_failure:
                raise self.start_failure
            return types.SimpleNamespace(stdout=self.created_id + "\n")
        self.assertEqual(args[:3], ["podman", "rm", "--force"])
        self.assertEqual(args[3], self.created_id)
        self.removed.append(args[3])
        self.container = None
        return types.SimpleNamespace(stdout="")

    def test_success_keeps_guest_running(self):
        self.assertEqual(vm.boot("test"), "boot-id")
        self.assertEqual(self.removed, [])
        self.assertEqual(self.container["Id"], self.created_id)

    def test_start_record_failure_removes_owned_id(self):
        self.records.side_effect = OSError("evidence write failed")
        with self.assertRaises(OSError):
            vm.boot("test")
        self.assertEqual(self.removed, [self.created_id])

    def test_ssh_deadline_removes_owned_id(self):
        with mock.patch.object(vm.time, "monotonic", side_effect=[0, 151]):
            with self.assertRaises(TimeoutError):
                vm.boot("test")
        self.assertEqual(self.removed, [self.created_id])

    def test_early_guest_exit_removes_owned_id(self):
        def exited(*args, **kwargs):
            self.container["State"]["Running"] = False
            return types.SimpleNamespace(returncode=255, stdout="")
        self.guest.side_effect = exited
        with self.assertRaisesRegex(AssertionError, "QEMU stopped"):
            vm.boot("test")
        self.assertEqual(self.removed, [self.created_id])

    def test_ssh_process_timeout_removes_owned_id(self):
        self.guest.side_effect = subprocess.TimeoutExpired("ssh", 6)
        with self.assertRaises(subprocess.TimeoutExpired):
            vm.boot("test")
        self.assertEqual(self.removed, [self.created_id])

    def test_interruption_removes_owned_id(self):
        self.guest.side_effect = KeyboardInterrupt()
        with self.assertRaises(KeyboardInterrupt):
            vm.boot("test")
        self.assertEqual(self.removed, [self.created_id])

    def test_failed_run_cleans_container_with_our_label(self):
        self.start_failure = RuntimeError("run failed after creating container")
        with self.assertRaises(RuntimeError) as raised:
            vm.boot("test")
        self.assertIs(raised.exception, self.start_failure)
        self.assertEqual(self.removed, [self.created_id])

    def test_name_race_preserves_unrelated_container(self):
        self.collide = True
        self.start_failure = RuntimeError("name already taken")
        with self.assertRaises(RuntimeError) as raised:
            vm.boot("test")
        self.assertIs(raised.exception, self.start_failure)
        self.assertEqual(self.removed, [])
        self.assertEqual(self.container["Id"], self.other_id)

    def test_preexisting_container_is_refused_without_removal(self):
        self.container = {"Id": self.other_id}
        with self.assertRaisesRegex(AssertionError, "name already exists"):
            vm.boot("test")
        self.assertEqual(self.removed, [])
        self.assertEqual(self.container["Id"], self.other_id)


if __name__ == "__main__":
    unittest.main()
