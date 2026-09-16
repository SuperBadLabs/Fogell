#!/usr/bin/env python3
"""Run campaign.main through the real ownership cleanup with fake Podman state."""
import pathlib
import tempfile
import types
import unittest
from unittest import mock

import campaign


def owned(ident, running=True):
    return {
        "Id": ident,
        "Config": {"Labels": {"io.fogell.persistence.boot": "d" * 32}},
        "State": {"Running": running, "Pid": 42},
    }


class FakePodman:
    def __init__(self, initial):
        self.name = initial
        self.containers = {initial["Id"]: initial}
        self.commands = []
        self.inspect_error = None
        self.remove_error = None

    def inspect(self, container_id=None):
        if self.inspect_error is not None and container_id is not None:
            raise self.inspect_error
        if container_id is None:
            return self.name
        return self.containers.get(container_id)

    def run(self, args, **_):
        self.commands.append(args)
        self.assert_remove(args)
        if self.remove_error is not None:
            raise self.remove_error
        ident = args[3]
        self.containers.pop(ident, None)
        if self.name and self.name["Id"] == ident:
            self.name = None
        return types.SimpleNamespace(stdout="")

    @staticmethod
    def assert_remove(args):
        if args[:3] != ["podman", "rm", "--force"]:
            raise AssertionError(f"unexpected Podman operation: {args}")


class CampaignOwnedCleanup(unittest.TestCase):
    def setUp(self):
        campaign.vm.OWNED_ID = None
        self.old = "a" * 64
        self.new = "b" * 64

    def run_main(self, fake, body):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            with mock.patch.object(campaign.vm, "ROOT", root), mock.patch.object(
                campaign.vm, "inspect", side_effect=fake.inspect
            ), mock.patch.object(campaign.vm, "run", side_effect=fake.run), mock.patch.object(
                campaign, "run_campaign", side_effect=body
            ):
                with self.assertRaises(RuntimeError) as raised:
                    campaign.main()
        return raised.exception

    def test_adopted_initial_id_is_removed_after_failure(self):
        primary = RuntimeError("campaign failure")
        fake = FakePodman(owned(self.old))
        self.assertIs(self.run_main(fake, primary), primary)
        self.assertEqual(fake.commands, [["podman", "rm", "--force", self.old]])
        self.assertIsNone(campaign.vm.OWNED_ID)

    def test_internal_reboot_generation_replaces_cleanup_target(self):
        fake = FakePodman(owned(self.old))
        fake.containers[self.new] = owned(self.new)
        def after_reboot():
            campaign.vm.register_owned(self.new)
            raise RuntimeError("after internal reboot")
        self.run_main(fake, after_reboot)
        self.assertEqual(fake.commands, [["podman", "rm", "--force", self.new]])
        self.assertIn(self.old, fake.containers)
        self.assertIsNone(campaign.vm.OWNED_ID)

    def test_stopped_owned_id_is_removed(self):
        fake = FakePodman(owned(self.old, running=False))
        self.run_main(fake, RuntimeError("stopped before failure"))
        self.assertEqual(fake.commands, [["podman", "rm", "--force", self.old]])
        self.assertIsNone(campaign.vm.OWNED_ID)

    def test_replacement_or_absent_original_is_never_addressed_by_name(self):
        fake = FakePodman(owned(self.old))
        replacement = {"Id": self.new, "Config": {"Labels": None}, "State": {"Running": True}}
        def replaced():
            fake.containers.pop(self.old)
            fake.name = replacement
            raise RuntimeError("name replaced")
        self.run_main(fake, replaced)
        self.assertEqual(fake.commands, [])
        self.assertIs(fake.name, replacement)
        self.assertIsNone(campaign.vm.OWNED_ID)

    def test_primary_survives_real_inspection_or_removal_failure(self):
        for failure in (OSError("inspect failed"), OSError("remove failed")):
            with self.subTest(failure=failure):
                primary = RuntimeError("campaign failure")
                fake = FakePodman(owned(self.old))
                if "inspect" in str(failure):
                    fake.inspect_error = failure
                else:
                    fake.remove_error = failure
                raised = self.run_main(fake, primary)
                self.assertIs(raised, primary)
                self.assertIn(str(failure), " ".join(primary.__notes__))


if __name__ == "__main__":
    unittest.main()
