#!/usr/bin/env python3
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import pathlib
import shutil
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("validator", ROOT / "validate-stage-run.py")
assert SPEC and SPEC.loader
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


def digest(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()


class ValidatorProof(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = pathlib.Path(tempfile.mkdtemp(prefix="fg177-stage-validator-"))
        shutil.copy(ROOT / "expected.tsv", self.temp / "expected.tsv")
        (self.temp / "STATUS").write_text("COMPLETE\n")
        expected = VALIDATOR.read_expected(self.temp / "expected.tsv")

        for side in ("before", "after"):
            oracle = self.temp / f"oracle-snapshot-{side}"
            surface = self.temp / f"surface-{side}"
            oracle.mkdir()
            surface.mkdir()
            (oracle / "jenkins-core.txt").write_text("2.568.1\n")
            (oracle / "jenkins-plugins.tsv").write_text("junit\t1416\ttrue\ttrue\n")
            (oracle / "jenkins-controller-image.txt").write_text("sha256:fixture\n")
            (surface / "container.tsv").write_text("container-id\tfixture\n")
            (surface / "plugin-jars.tsv").write_text("junit\t" + "a" * 64 + "\t/junit.jar\n")
            (surface / "plugin-jars.sha256").write_text("b" * 64 + "\n")

        for case in VALIDATOR.CASES:
            row = expected[case]
            case_dir = self.temp / "runs" / case
            (case_dir / "stage-nodes").mkdir(parents=True)
            for relative in ("attribution.tsv", "submitted-config.xml", "returned-config.xml", "console.txt"):
                (case_dir / relative).write_text("fixture\n")
            (case_dir / "build.json").write_text(
                json.dumps({"building": False, "result": row["build_result"]})
            )
            stages = [
                {"id": "1", "name": "probe", "status": row["probe_status"]},
                {"id": "2", "name": "later", "status": row["later_status"]},
                {"id": "3", "name": "Declarative: Post Actions", "status": row["build_result"]},
            ]
            (case_dir / "wfapi-describe.json").write_text(json.dumps({"stages": stages}))
            canonical = [
                {"name": s["name"], "status": s["status"]}
                for s in stages if s["name"] in {"probe", "later"}
            ]
            (case_dir / "stages.canonical.json").write_text(json.dumps(canonical))
            for stage in stages[:2]:
                (case_dir / "stage-nodes" / f"{stage['name']}.json").write_text(
                    json.dumps({"name": stage["name"], "status": stage["status"]})
                )
            entries = {
                marker: digest(VALIDATOR.MARKER_CONTENT[marker])
                for marker in row["required_markers"].split(",")
            }
            manifest = "\n".join(f"{path}\t{entries[path]}" for path in sorted(entries))
            (case_dir / "workspace.tsv").write_text(manifest + "\n")
            (case_dir / "workspace.sha256").write_text(digest(manifest) + "\n")

    def tearDown(self) -> None:
        shutil.rmtree(self.temp)

    def test_clean_fixture_passes(self) -> None:
        VALIDATOR.validate_run(self.temp)

    def test_flipped_stage_status_fails(self) -> None:
        path = self.temp / "runs" / "b1s0" / "wfapi-describe.json"
        payload = json.loads(path.read_text())
        payload["stages"][0]["status"] = "SUCCESS"
        path.write_text(json.dumps(payload))
        with self.assertRaises(ValueError):
            VALIDATOR.validate_run(self.temp)

    def test_missing_node_capture_fails(self) -> None:
        (self.temp / "runs" / "b0s0" / "stage-nodes" / "probe.json").unlink()
        with self.assertRaises(ValueError):
            VALIDATOR.validate_run(self.temp)

    def test_forbidden_marker_fails(self) -> None:
        case_dir = self.temp / "runs" / "b0s1"
        raw = (case_dir / "workspace.tsv").read_text().rstrip("\n")
        raw += "\nwrong-stage.txt\t" + digest("wrong")
        (case_dir / "workspace.tsv").write_text(raw + "\n")
        (case_dir / "workspace.sha256").write_text(digest(raw) + "\n")
        with self.assertRaises(ValueError):
            VALIDATOR.validate_run(self.temp)

    def test_oracle_drift_fails(self) -> None:
        (self.temp / "surface-after" / "plugin-jars.tsv").write_text("drift\n")
        with self.assertRaises(ValueError):
            VALIDATOR.validate_run(self.temp)


if __name__ == "__main__":
    unittest.main()
