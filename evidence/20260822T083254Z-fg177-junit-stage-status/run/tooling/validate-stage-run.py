#!/usr/bin/env python3
from __future__ import annotations

import csv
import hashlib
import json
import pathlib
import sys


CASES = ("b0s0", "b0s1", "b1s0", "b1s1")
MARKER_CONTENT = {
    "counts.txt": "4,2,1",
    "successor.txt": "successor",
    "stage-always.txt": "always",
    "stage-unstable.txt": "unstable",
    "stage-success.txt": "success",
    "later.txt": "later",
    "pipeline-unstable.txt": "unstable",
    "pipeline-success.txt": "success",
}
REQUIRED_CASE_FILES = (
    "attribution.tsv",
    "submitted-config.xml",
    "returned-config.xml",
    "build.json",
    "console.txt",
    "wfapi-describe.json",
    "stages.canonical.json",
    "workspace.tsv",
    "workspace.sha256",
    "stage-nodes/probe.json",
    "stage-nodes/later.json",
)


def fail(message: str) -> "NoReturn":
    raise ValueError(message)


def digest_text(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()


def read_expected(path: pathlib.Path) -> dict[str, dict[str, str]]:
    with path.open(newline="") as handle:
        reader = csv.DictReader(handle, delimiter="\t")
        required = {
            "case", "build_result", "probe_status", "later_status",
            "required_markers", "forbidden_markers",
        }
        if set(reader.fieldnames or []) != required:
            fail("expected.tsv header is not the closed schema")
        rows = {row["case"]: row for row in reader}
    if tuple(sorted(rows)) != CASES:
        fail("expected.tsv does not contain the exact four-case matrix")
    return rows


def parse_workspace(case_dir: pathlib.Path) -> dict[str, str]:
    raw = (case_dir / "workspace.tsv").read_text()
    entries: dict[str, str] = {}
    for line in raw.splitlines():
        parts = line.split("\t")
        if len(parts) != 2 or not parts[0] or len(parts[1]) != 64:
            fail(f"{case_dir.name}: malformed workspace row")
        if parts[0] in entries:
            fail(f"{case_dir.name}: duplicate workspace path {parts[0]}")
        entries[parts[0]] = parts[1]
    canonical = "\n".join(f"{path}\t{entries[path]}" for path in sorted(entries))
    expected_hash = (case_dir / "workspace.sha256").read_text().strip()
    if digest_text(canonical) != expected_hash:
        fail(f"{case_dir.name}: workspace hash does not bind its canonical manifest")
    return entries


def canonical_from_raw(case_dir: pathlib.Path) -> list[dict[str, str]]:
    raw = json.loads((case_dir / "wfapi-describe.json").read_text())
    stages = raw.get("stages") if isinstance(raw, dict) else None
    if not isinstance(stages, list):
        fail(f"{case_dir.name}: raw wfapi has no stages")
    canonical: list[dict[str, str]] = []
    observed_names: list[str] = []
    for stage in stages:
        if not isinstance(stage, dict):
            fail(f"{case_dir.name}: raw wfapi stage is malformed")
        name, status = stage.get("name"), stage.get("status")
        if not isinstance(name, str) or not isinstance(status, str):
            fail(f"{case_dir.name}: raw wfapi stage has no name/status")
        observed_names.append(name)
        if name in {"probe", "later"}:
            canonical.append({"name": name, "status": status})
    if sorted(observed_names) != ["Declarative: Post Actions", "later", "probe"]:
        fail(f"{case_dir.name}: raw wfapi stage surface has unexpected names")
    names = [stage["name"] for stage in canonical]
    if sorted(names) != ["later", "probe"] or len(set(names)) != 2:
        fail(f"{case_dir.name}: stage names are not exactly probe/later")
    return canonical


def validate_case(case: str, case_dir: pathlib.Path, expected: dict[str, str]) -> None:
    for relative in REQUIRED_CASE_FILES:
        path = case_dir / relative
        if not path.is_file() or path.stat().st_size == 0:
            fail(f"{case}: required capture absent or empty: {relative}")

    build = json.loads((case_dir / "build.json").read_text())
    if build.get("building") is not False or build.get("result") != expected["build_result"]:
        fail(f"{case}: terminal build result differs from expected matrix")

    raw_canonical = canonical_from_raw(case_dir)
    retained_canonical = json.loads((case_dir / "stages.canonical.json").read_text())
    if retained_canonical != raw_canonical:
        fail(f"{case}: canonical stage projection is not derived from raw wfapi")
    statuses = {stage["name"]: stage["status"] for stage in raw_canonical}
    if statuses != {"probe": expected["probe_status"], "later": expected["later_status"]}:
        fail(f"{case}: declared stage statuses differ from expected matrix: {statuses}")

    for stage_name in ("probe", "later"):
        detail = json.loads((case_dir / "stage-nodes" / f"{stage_name}.json").read_text())
        if detail.get("name") != stage_name or detail.get("status") != statuses[stage_name]:
            fail(f"{case}: stage-node detail disagrees for {stage_name}")

    workspace = parse_workspace(case_dir)
    required = {name for name in expected["required_markers"].split(",") if name}
    forbidden = {name for name in expected["forbidden_markers"].split(",") if name}
    for marker in required:
        if marker not in workspace:
            fail(f"{case}: required marker absent: {marker}")
        content = MARKER_CONTENT.get(marker)
        if content is None or workspace[marker] != digest_text(content):
            fail(f"{case}: marker content differs: {marker}")
    present_forbidden = forbidden.intersection(workspace)
    if present_forbidden:
        fail(f"{case}: forbidden markers present: {sorted(present_forbidden)}")


def validate_run(root: pathlib.Path) -> None:
    if (root / "STATUS").read_text().strip() != "COMPLETE":
        fail("run STATUS is not COMPLETE")
    expected = read_expected(root / "expected.tsv")
    for case in CASES:
        validate_case(case, root / "runs" / case, expected[case])

    for side in ("before", "after"):
        for relative in (
            f"oracle-snapshot-{side}/jenkins-core.txt",
            f"oracle-snapshot-{side}/jenkins-plugins.tsv",
            f"oracle-snapshot-{side}/jenkins-controller-image.txt",
            f"surface-{side}/container.tsv",
            f"surface-{side}/plugin-jars.tsv",
            f"surface-{side}/plugin-jars.sha256",
        ):
            path = root / relative
            if not path.is_file() or path.stat().st_size == 0:
                fail(f"oracle surface absent or empty: {relative}")
    for relative in ("jenkins-core.txt", "jenkins-plugins.tsv", "jenkins-controller-image.txt"):
        if (root / "oracle-snapshot-before" / relative).read_bytes() != (root / "oracle-snapshot-after" / relative).read_bytes():
            fail(f"oracle drifted across matrix: {relative}")
    for relative in ("container.tsv", "plugin-jars.tsv", "plugin-jars.sha256"):
        if (root / "surface-before" / relative).read_bytes() != (root / "surface-after" / relative).read_bytes():
            fail(f"controller/plugin surface drifted across matrix: {relative}")


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: validate-stage-run.py RUN", file=sys.stderr)
        return 2
    try:
        validate_run(pathlib.Path(argv[1]))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print("FG-177 JUNIT STAGE RUN VALID")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
