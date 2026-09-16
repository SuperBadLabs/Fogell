#!/usr/bin/env python3
"""Ensure guest and controller API transport target the captured VM ID."""
import pathlib
import tempfile
import types
import unittest
from unittest import mock

import api
import vm


IDENT = "a" * 64


class TransportOwnership(unittest.TestCase):
    def setUp(self):
        vm.OWNED_ID = IDENT
        self.addCleanup(setattr, vm, "OWNED_ID", None)

    def test_guest_and_copy_default_to_active_id(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "input"
            source.write_text("x", encoding="utf-8")
            with mock.patch.object(vm, "ROOT", root), mock.patch.object(
                vm, "run", return_value=types.SimpleNamespace(stdout="", returncode=0)
            ) as run:
                vm.guest(["true"])
                vm.copy_to_guest(source, "/tmp/input")
        self.assertEqual(run.call_args_list[0].args[0][:4], ["podman", "exec", "-i", IDENT])
        self.assertEqual(run.call_args_list[1].args[0][:4], ["podman", "exec", "-i", IDENT])

    def test_fresh_process_adopts_then_routes_guest_and_copy_by_id(self):
        vm.OWNED_ID = None
        container = {
            "Id": IDENT,
            "Config": {"Labels": {"io.fogell.persistence.boot": "d" * 32}},
        }
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "input"
            source.write_text("x", encoding="utf-8")
            with mock.patch.object(vm, "ROOT", root), mock.patch.object(
                vm, "inspect", return_value=container
            ), mock.patch.object(vm, "run", return_value=types.SimpleNamespace(stdout="", returncode=0)) as run:
                self.assertEqual(vm.adopt_named_owned(), IDENT)
                vm.guest(["true"])
                vm.copy_to_guest(source, "/tmp/input")
        self.assertTrue(all(call.args[0][3] == IDENT for call in run.call_args_list))

    def test_api_request_targets_active_id_not_fixed_name(self):
        response = types.SimpleNamespace(stdout='{"code": 200}')
        with mock.patch.object(api, "run", return_value=response) as run:
            self.assertEqual(api.request("/health/ready", auth=False), {"code": 200})
        args = run.call_args.args[0]
        self.assertEqual(args[:4], ["podman", "exec", "-i", IDENT])
        self.assertNotIn(vm.NAME, args)

    def test_transport_refuses_absent_or_different_active_id(self):
        for candidate in (None, {"Id": IDENT, "Config": {"Labels": []}}):
            with self.subTest(candidate=candidate):
                vm.OWNED_ID = None
                with mock.patch.object(vm, "inspect", return_value=candidate), mock.patch.object(vm, "run") as run:
                    self.assertIsNone(vm.adopt_named_owned())
                    with self.assertRaisesRegex(AssertionError, "active owned"):
                        vm.guest(["true"])
                run.assert_not_called()
        vm.OWNED_ID = IDENT
        with self.assertRaisesRegex(AssertionError, "active owned"):
            vm.guest(["true"], container_id="b" * 64)


if __name__ == "__main__":
    unittest.main()
