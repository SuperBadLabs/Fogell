#!/usr/bin/env python3
"""Exercise host credential cleanup without starting a VM or contacting the API."""
import pathlib
import tempfile
import unittest
from unittest import mock

import campaign


class CredentialCleanup(unittest.TestCase):
    def setUp(self):
        campaign.vm.OWNED_ID = None

    def exercise(self, failure=None, create_token=True):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            token = root / "token"

            def run():
                if create_token:
                    token.write_text("test-credential", encoding="utf-8")
                if failure is not None:
                    raise failure

            with mock.patch.object(campaign.vm, "ROOT", root), mock.patch.object(
                campaign, "run_campaign", run
            ), mock.patch.object(campaign.vm, "adopt_named_owned", return_value="a" * 64), mock.patch.object(
                campaign.vm, "cleanup_active_owned"
            ):
                if failure is None:
                    campaign.main()
                else:
                    with self.assertRaises(type(failure)) as raised:
                        campaign.main()
                    self.assertIs(raised.exception, failure)
            self.assertFalse(token.exists())

    def test_success_removes_credential(self):
        self.exercise()

    def test_failure_removes_credential_and_propagates(self):
        self.exercise(RuntimeError("campaign failed"))

    def test_early_failure_without_token_preserves_error(self):
        self.exercise(RuntimeError("setup failed"), create_token=False)

    def test_interrupt_removes_credential_and_preserves_identity(self):
        self.exercise(KeyboardInterrupt())

    def test_absent_or_unowned_initial_vm_refuses_without_campaign_cleanup(self):
        for candidate in (None, {"Id": "b" * 64, "Config": {"Labels": None}}):
            with self.subTest(candidate=candidate), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                with mock.patch.object(campaign.vm, "ROOT", root), mock.patch.object(
                    campaign.vm, "inspect", return_value=candidate
                ), mock.patch.object(campaign, "run_campaign") as run, mock.patch.object(
                    campaign.vm, "run"
                ) as podman:
                    with self.assertRaisesRegex(AssertionError, "active harness-owned"):
                        campaign.main()
                run.assert_not_called()
                podman.assert_not_called()

    def test_malformed_initial_metadata_is_refused_without_removal(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            malformed = {"Id": "b" * 64, "Config": {"Labels": []}}
            with mock.patch.object(campaign.vm, "ROOT", root), mock.patch.object(
                campaign.vm, "inspect", return_value=malformed
            ), mock.patch.object(campaign, "run_campaign") as run, mock.patch.object(
                campaign.vm, "run"
            ) as podman:
                with self.assertRaisesRegex(AssertionError, "active harness-owned"):
                    campaign.main()
            run.assert_not_called()
            podman.assert_not_called()

    def test_primary_error_survives_vm_cleanup_error(self):
        primary = RuntimeError("API failed")
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            with mock.patch.object(campaign.vm, "ROOT", root), mock.patch.object(
                campaign.vm, "adopt_named_owned", return_value="a" * 64
            ), mock.patch.object(campaign, "run_campaign", side_effect=primary), mock.patch.object(
                campaign.vm, "cleanup_active_owned", side_effect=OSError("remove failed")
            ):
                with self.assertRaises(RuntimeError) as raised:
                    campaign.main()
        self.assertIs(raised.exception, primary)
        self.assertIn("remove failed", " ".join(primary.__notes__))

    def test_primary_error_survives_cleanup_inspection_and_token_errors(self):
        primary = RuntimeError("campaign failure")
        ident = "a" * 64
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "token").mkdir()
            campaign.vm.OWNED_ID = ident
            with mock.patch.object(campaign.vm, "ROOT", root), mock.patch.object(
                campaign.vm, "adopt_named_owned", return_value=ident
            ), mock.patch.object(campaign, "run_campaign", side_effect=primary), mock.patch.object(
                campaign.vm, "inspect", side_effect=OSError("inspect failed")
            ):
                with self.assertRaises(RuntimeError) as raised:
                    campaign.main()
        self.assertIs(raised.exception, primary)
        notes = " ".join(primary.__notes__)
        self.assertIn("inspect failed", notes)
        self.assertIn("token", notes)


if __name__ == "__main__":
    unittest.main()
