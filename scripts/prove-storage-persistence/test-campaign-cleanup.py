#!/usr/bin/env python3
"""Exercise host credential cleanup without starting a VM or contacting the API."""
import pathlib
import tempfile
import unittest
from unittest import mock

import campaign


class CredentialCleanup(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
