#!/usr/bin/env python3
"""Fail-closed validator for one FG-177 retained SCM evidence bundle."""
from __future__ import annotations

import hashlib
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


HEX = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
PINNED_CORE_SHA256 = "5c527167c41f888bbbe66d3d520592654a496c74f4847e5db4c0a3c4f5246577"
PINNED_PLUGINS_SHA256 = "6af6817f555a6fbbbcb5b41d48e7be58b00517efc913b21cfb825d0614630b3a"
PINNED_IMAGE_SHA256 = "2a2612da07718c48b9da3b79a3665bba9e14ba99a450f09789a2d9e9dce3230c"
BASE = {
    "git": ("GIT_BRANCH", "GIT_COMMIT", "GIT_LOCAL_BRANCH", "GIT_URL"),
    "checkout-scm": ("GIT_BRANCH", "GIT_COMMIT", "GIT_URL"),
}


def fail(message: str) -> "NoReturn":
    raise SystemExit(f"ERROR: {message}")


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    digest.update(path.read_bytes())
    return digest.hexdigest()


def manifest(root: pathlib.Path) -> None:
    manifest_path = root / "MANIFEST.sha256"
    seen: set[str] = set()
    for line in manifest_path.read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (\./.+)", line)
        if not match or match.group(2) in seen:
            fail("malformed or duplicate manifest row")
        relative = match.group(2)
        seen.add(relative)
        target = root / relative[2:]
        if target.is_symlink() or not target.is_file() or sha256(target) != match.group(1):
            fail(f"manifest mismatch: {relative}")
    actual = {"./" + str(path.relative_to(root)) for path in root.rglob("*") if path.is_file() and path.name != "MANIFEST.sha256"}
    if seen != actual:
        fail("manifest is not a closed inventory")
    if any(path.is_symlink() for path in root.rglob("*")):
        fail("bundle contains a symbolic link")


def tsv_map(path: pathlib.Path) -> dict[str, str]:
    result: dict[str, str] = {}
    for line in path.read_text().splitlines():
        key, value = line.split("\t", 1)
        if key in result:
            fail(f"duplicate key in {path}: {key}")
        result[key] = value
    return result


def validate_checksum_inventory(directory: pathlib.Path) -> None:
    inventory = directory / "SHA256SUMS"
    seen: set[str] = set()
    for line in inventory.read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (\./.+)", line)
        if not match or match.group(2) in seen:
            fail("tooling SHA256SUMS is malformed or duplicate")
        seen.add(match.group(2))
        target = directory / match.group(2)[2:]
        if target.is_symlink() or not target.is_file() or sha256(target) != match.group(1):
            fail(f"tooling digest mismatch: {match.group(2)}")
    actual = {"./" + str(path.relative_to(directory)) for path in directory.rglob("*") if path.is_file() and path.name != "SHA256SUMS"}
    if actual != seen:
        fail("tooling SHA256SUMS is not a closed inventory")


def validate_tooling(root: pathlib.Path) -> None:
    tooling = root / "tooling"
    required = {
        "README.md", "capture-controller-surface.py", "jenkins-driver.py",
        "run-retained-scm-oracle.sh", "validate-retained-scm-run.py", "SHA256SUMS",
        "cases/Jenkinsfile", "cases/fg177-retained-git.Jenkinsfile.in",
    }
    actual = {str(path.relative_to(tooling)) for path in tooling.rglob("*") if path.is_file()}
    if actual != required:
        fail("retained tooling file set is not exact")
    validate_checksum_inventory(tooling)
    if (tooling / "cases/Jenkinsfile").read_bytes() != (root / "inputs/checkout-scm.Jenkinsfile").read_bytes():
        fail("retained checkout case differs from the executed input")


def validate_oracle_receipt(path: pathlib.Path) -> dict[str, str]:
    receipt = tsv_map(path)
    required = {
        "format", "jenkins-core", "jenkins-session-sha256", "controller-container-id",
        "core-metadata-sha256", "plugin-count", "plugin-manifest-sha256",
        "controller-image-name", "controller-image-id", "controller-image-digest",
        "image-metadata-sha256",
    }
    if set(receipt) != required or receipt["format"] != "fogell-jenkins-oracle-v2":
        fail(f"oracle receipt schema is not exact: {path.name}")
    if receipt["jenkins-core"] != "2.568.1" or receipt["plugin-count"] != "154":
        fail("oracle receipt is not the pinned 2.568.1/154-plugin surface")
    if receipt["core-metadata-sha256"] != PINNED_CORE_SHA256 or receipt["plugin-manifest-sha256"] != PINNED_PLUGINS_SHA256 or receipt["image-metadata-sha256"] != PINNED_IMAGE_SHA256:
        fail("oracle receipt metadata digests differ from the accepted pin")
    for key in ("jenkins-session-sha256", "controller-container-id", "controller-image-id"):
        if not SHA256.fullmatch(receipt[key]):
            fail(f"oracle receipt {key} is malformed")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", receipt["controller-image-digest"]):
        fail("oracle receipt image digest is malformed")
    return receipt


def validate_production_oracle(root: pathlib.Path) -> str:
    before_path = root / "oracle-before-verification.txt"
    after_path = root / "oracle-after-verification.txt"
    if before_path.read_bytes() != after_path.read_bytes():
        fail("before/after canonical oracle receipts differ")
    before = validate_oracle_receipt(before_path)
    after = validate_oracle_receipt(after_path)
    if before != after:
        fail("before/after canonical oracle identities differ")
    required = {"jenkins-core.txt", "jenkins-plugins.tsv", "jenkins-controller-image.txt"}
    for suffix in ("before", "after"):
        snapshot = root / f"oracle-snapshot-{suffix}"
        if {path.name for path in snapshot.iterdir() if path.is_file()} != required:
            fail(f"canonical oracle {suffix} snapshot file set is not exact")
        expected = {
            "jenkins-core.txt": PINNED_CORE_SHA256,
            "jenkins-plugins.tsv": PINNED_PLUGINS_SHA256,
            "jenkins-controller-image.txt": PINNED_IMAGE_SHA256,
        }
        for name, digest in expected.items():
            if sha256(snapshot / name) != digest:
                fail(f"canonical oracle {suffix} snapshot differs from the accepted pin: {name}")
        if len((snapshot / "jenkins-plugins.tsv").read_text().splitlines()) != 154:
            fail("canonical oracle snapshot is not exactly 154 plugin rows")
    for name in required:
        if (root / "oracle-snapshot-before" / name).read_bytes() != (root / "oracle-snapshot-after" / name).read_bytes():
            fail(f"canonical oracle snapshot drifted: {name}")
    snapshot = root / "oracle-snapshot-before"
    if (snapshot / "jenkins-core.txt").read_text() != before["jenkins-core"] + "\n":
        fail("oracle receipt core does not equal its retained snapshot")
    image_fields = (snapshot / "jenkins-controller-image.txt").read_text().strip().split("|")
    if image_fields != [before["controller-image-name"], before["controller-image-id"], before["controller-image-digest"]]:
        fail("oracle receipt image identity does not equal its retained snapshot")
    return before["controller-container-id"]


def validate_surface(root: pathlib.Path, *, hermetic: bool, expected_container: str | None) -> None:
    required = {"identity.tsv", "jenkins-core.txt", "jenkins-plugins.tsv", "git-plugin.tsv", "workflow-scm-step-plugin.tsv"}
    before, after = root / "surface-before", root / "surface-after"
    if {p.name for p in before.iterdir() if p.is_file()} != required:
        fail("before surface does not contain the exact required files")
    if {p.name for p in after.iterdir() if p.is_file()} != required:
        fail("after surface does not contain the exact required files")
    for name in required:
        if (before / name).read_bytes() != (after / name).read_bytes():
            fail(f"oracle surface drifted: {name}")
    identity = tsv_map(before / "identity.tsv")
    if set(identity) != {"container-id", "image-name", "image-id", "image-digest", "session-sha256"}:
        fail("controller surface identity schema is not exact")
    if not SHA256.fullmatch(identity["container-id"]) or not SHA256.fullmatch(identity["image-id"]) or not SHA256.fullmatch(identity["session-sha256"]):
        fail("controller surface identity contains malformed hashes")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", identity["image-digest"]):
        fail("controller surface image digest is malformed")
    if not identity["image-name"] or any(character in identity["image-name"] for character in "\t\r\n"):
        fail("controller surface image name is malformed")
    if (before / "jenkins-core.txt").read_text().strip() != "2.568.1":
        fail("Jenkins core is not pinned to 2.568.1")
    plugins = (before / "jenkins-plugins.tsv").read_text()
    for row in ("git\t5.10.1\ttrue\ttrue", "git-client\t6.6.1\ttrue\ttrue",
                "workflow-scm-step\t466.va_d69e602552b_\ttrue\ttrue", "scm-api\t728.vc30dcf7a_0df5\ttrue\ttrue"):
        if row not in plugins.splitlines():
            fail(f"missing pinned plugin row: {row}")
    if not hermetic:
        if len(plugins.splitlines()) != 154 or sha256(before / "jenkins-plugins.tsv") != PINNED_PLUGINS_SHA256:
            fail("production surface is not the exact accepted 154-plugin manifest")
        if sha256(before / "jenkins-core.txt") != PINNED_CORE_SHA256:
            fail("production surface core metadata differs from the accepted pin")
        receipt = validate_oracle_receipt(root / "oracle-before-verification.txt")
        if expected_container is None or identity["container-id"] != expected_container:
            fail("production surface is not bound to the pre-verified controller container")
        if identity["container-id"] != receipt["controller-container-id"] or identity["image-name"] != receipt["controller-image-name"] or identity["image-id"] != receipt["controller-image-id"] or identity["image-digest"] != receipt["controller-image-digest"] or identity["session-sha256"] != receipt["jenkins-session-sha256"]:
            fail("custom surface identity differs from the canonical oracle receipt")
    for name in ("git-plugin.tsv", "workflow-scm-step-plugin.tsv"):
        fields = (before / name).read_text().strip().split("\t")
        if len(fields) < 4 or not all(re.fullmatch(r"[0-9a-f]{64}", item) for item in fields[-3:]):
            fail(f"{name} must bind jpi, implementation-jar, and manifest SHA-256")


def validate_fixture(root: pathlib.Path) -> dict[str, str]:
    rows = root.joinpath("fixture.tsv").read_text().splitlines()
    if rows[0] != "label\tsha\tparent\tpayload\tjenkinsfile-sha256" or len(rows) != 7:
        fail("fixture inventory shape is wrong")
    commits: dict[str, str] = {}
    parents: dict[str, str] = {}
    jenkins_hashes: set[str] = set()
    for row in rows[1:]:
        label, revision, parent, payload, jenkins_hash = row.split("\t")
        if label in commits or payload != label or not HEX.fullmatch(revision):
            fail("fixture row is malformed")
        commits[label], parents[label] = revision, parent
        jenkins_hashes.add(jenkins_hash)
    expected_parents = {"A": "-", "B": commits["A"], "C": commits["B"], "D": commits["C"], "F": commits["A"], "G": commits["F"]}
    if parents != expected_parents or len(jenkins_hashes) != 1:
        fail("fixture DAG or byte-identical Jenkinsfile invariant is wrong")
    bundle = root / "fixture.bundle"
    with tempfile.TemporaryDirectory(prefix="fg177-bundle-") as temporary:
        bare = pathlib.Path(temporary) / "fixture.git"
        listed = subprocess.run(
            ["git", "bundle", "list-heads", str(bundle)], check=True,
            text=True, stdout=subprocess.PIPE,
        ).stdout.splitlines()
        expected_heads = {f"{commits[label]} refs/heads/fixture/{label}" for label in commits}
        if set(listed) != expected_heads or len(listed) != 6:
            fail("fixture bundle does not expose exactly the six declared heads")
        subprocess.run(["git", "clone", "-q", "--bare", str(bundle), str(bare)], check=True)
        checkout_case = (root / "inputs/checkout-scm.Jenkinsfile").read_bytes()
        checkout_hash = sha256(root / "inputs/checkout-scm.Jenkinsfile")
        if jenkins_hashes != {checkout_hash}:
            fail("fixture inventory Jenkinsfile hash differs from the executed checkout case")
        for label, revision in commits.items():
            commit = subprocess.run(
                ["git", "-C", str(bare), "cat-file", "-p", revision], check=True,
                text=True, stdout=subprocess.PIPE,
            ).stdout.splitlines()
            actual_parents = [line.split(" ", 1)[1] for line in commit if line.startswith("parent ")]
            expected_parent = [] if parents[label] == "-" else [parents[label]]
            if actual_parents != expected_parent:
                fail(f"fixture bundle parent mismatch for {label}")
            jenkinsfile = subprocess.run(
                ["git", "-C", str(bare), "show", f"{revision}:Jenkinsfile"], check=True,
                stdout=subprocess.PIPE,
            ).stdout
            payload = subprocess.run(
                ["git", "-C", str(bare), "show", f"{revision}:payload.txt"], check=True,
                stdout=subprocess.PIPE,
            ).stdout
            if jenkinsfile != checkout_case or payload != (label + "\n").encode():
                fail(f"fixture bundle tree content mismatch for {label}")
    return commits


def validate_config_xml(path: pathlib.Path, producer: str, expected: dict[str, str], input_case: pathlib.Path) -> None:
    if path.is_symlink() or not path.is_file() or path.stat().st_size == 0:
        fail(f"{producer} build {expected['build']}: missing submitted/returned config: {path.name}")
    try:
        root = ET.fromstring(path.read_bytes())
    except ET.ParseError as error:
        fail(f"{producer} build {expected['build']}: malformed {path.name}: {error}")
    definition = root.find("definition")
    if definition is None:
        fail(f"{producer} build {expected['build']}: {path.name} lacks one definition")
    definition_class = definition.attrib.get("class", "")
    if producer == "git":
        if not definition_class.endswith("CpsFlowDefinition"):
            fail(f"git build {expected['build']}: {path.name} is not an inline definition")
        script, sandbox = definition.find("script"), definition.find("sandbox")
        if script is None or (script.text or "") != input_case.read_text() or sandbox is None or sandbox.text != "true":
            fail(f"git build {expected['build']}: {path.name} does not carry the exact sandboxed input")
    else:
        if not definition_class.endswith("CpsScmFlowDefinition"):
            fail(f"checkout-scm build {expected['build']}: {path.name} is not an SCM definition")
        url = definition.find("./scm/userRemoteConfigs/hudson.plugins.git.UserRemoteConfig/url")
        branch = definition.find("./scm/branches/hudson.plugins.git.BranchSpec/name")
        script_path, lightweight = definition.find("scriptPath"), definition.find("lightweight")
        if url is None or url.text != expected["clone-url"] or branch is None or branch.text != f"*/{expected['branch']}":
            fail(f"checkout-scm build {expected['build']}: {path.name} SCM URL/branch mismatch")
        if script_path is None or script_path.text != "Jenkinsfile" or lightweight is None or lightweight.text != "false":
            fail(f"checkout-scm build {expected['build']}: {path.name} scriptPath/lightweight mismatch")


def validate_build(root: pathlib.Path, producer: str, number: int, commits: dict[str, str]) -> None:
    directory = root / "runs" / producer / f"build-{number}"
    expected = tsv_map(directory / "expected.tsv")
    required_keys = {"build", "branch", "label", "sha", "result", "previous", "previous-successful", "payload", "clone-url"}
    if set(expected) != required_keys or expected["build"] != str(number) or commits.get(expected["label"]) != expected["sha"]:
        fail(f"{producer} build {number}: malformed expectation")
    input_case = root / "inputs" / ("git.Jenkinsfile" if producer == "git" else "checkout-scm.Jenkinsfile")
    validate_config_xml(directory / "submitted-config.xml", producer, expected, input_case)
    validate_config_xml(directory / "returned-config.xml", producer, expected, input_case)
    for ref_name in ("ref-before.tsv", "ref-after.tsv"):
        fields = (directory / ref_name).read_text().strip().split("\t")
        if fields != [expected["sha"], f"refs/heads/{expected['branch']}"]:
            fail(f"{producer} build {number}: mutable ref was not exact")
    build = json.loads((directory / "build.json").read_text())
    if build.get("number") != number or build.get("result") != expected["result"] or build.get("building") is not False:
        fail(f"{producer} build {number}: terminal build metadata mismatch")
    builddata = json.loads((directory / "builddata.json").read_text())
    if (not isinstance(builddata, list) or not builddata or
            any(not isinstance(action, dict) or action.get("_class") != "hudson.plugins.git.util.BuildData"
                for action in builddata)):
        fail(f"{producer} build {number}: BuildData action set is not exact")
    branch_key = f"refs/remotes/origin/{expected['branch']}"
    matching_actions = [
        action for action in builddata
        if isinstance(action.get("lastBuiltRevision"), dict)
        and action["lastBuiltRevision"].get("SHA1") == expected["sha"]
        and expected["clone-url"] in action.get("remoteUrls", [])
        and branch_key in action.get("buildsByBranchName", {})
    ]
    if not matching_actions:
        fail(f"{producer} build {number}: no BuildData action binds current revision, branch, and remote")
    console = (directory / "console.txt").read_text()
    if f"FG177 MAP PRODUCER={producer} BUILD={number} CLASS=java.util.TreeMap" not in console:
        fail(f"{producer} build {number}: return class marker absent")
    if "FG177 MAP RENDER=" not in console:
        fail(f"{producer} build {number}: raw render measurement absent")
    history = number in (2, 3, 5, 6)
    keys = list(BASE[producer]) + (["GIT_PREVIOUS_COMMIT", "GIT_PREVIOUS_SUCCESSFUL_COMMIT"] if history else [])
    if "FG177 MAP KEYS=" + ",".join(sorted(keys)) not in console:
        fail(f"{producer} build {number}: exact key set mismatch")
    entries = {
        "GIT_BRANCH": f"origin/{expected['branch']}",
        "GIT_COMMIT": expected["sha"],
        "GIT_URL": expected["clone-url"],
    }
    if producer == "git":
        entries["GIT_LOCAL_BRANCH"] = expected["branch"]
    if history:
        entries["GIT_PREVIOUS_COMMIT"] = expected["previous"]
        entries["GIT_PREVIOUS_SUCCESSFUL_COMMIT"] = expected["previous-successful"]
    for key, value in entries.items():
        if f"FG177 MAP ENTRY={key}|java.lang.String|{value}" not in console:
            fail(f"{producer} build {number}: entry mismatch for {key}")
    for key, expectation_key in (("GIT_PREVIOUS_COMMIT", "previous"), ("GIT_PREVIOUS_SUCCESSFUL_COMMIT", "previous-successful")):
        present = expected[expectation_key] != "-"
        value = expected[expectation_key] if present else "null"
        if f"FG177 HISTORY KEY={key}|PRESENT={str(present).lower()}|VALUE={value}" not in console:
            fail(f"{producer} build {number}: history presence/value mismatch")
    commit = expected["sha"]
    fixed = (
        f"FG177 ACCESS PROPERTY={commit}|INDEX={commit}|DYNAMIC={commit}",
        "FG177 MISSING PROPERTY=null|INDEX=null|GET=null|CONTAINS=false",
        "FG177 WRONG-INDEX integer=",
        "FG177 WRONG-INDEX null=",
    )
    if any(marker not in console for marker in fixed):
        fail(f"{producer} build {number}: map-surface marker absent")
    artifact_dir = directory / "artifacts"
    if (artifact_dir / "fg177-workspace-revision.txt").read_text().strip() != commit:
        fail(f"{producer} build {number}: workspace revision artifact mismatch")
    if (artifact_dir / "fg177-workspace-payload.txt").read_text().strip() != expected["payload"]:
        fail(f"{producer} build {number}: workspace payload artifact mismatch")
    definition = (directory / "definition-scm.txt").read_text()
    if console.count("[Pipeline] Start") != 1 or definition != console.split("[Pipeline] Start", 1)[0]:
        fail(f"{producer} build {number}: retained pre-Pipeline prefix is not exact")
    if "FG177 " in definition:
        fail(f"{producer} build {number}: probe output occurred before Pipeline start")
    if producer == "checkout-scm":
        revision_line = f"Checking out Revision {commit} (refs/remotes/origin/{expected['branch']})"
        if revision_line not in definition or expected["clone-url"] not in definition:
            fail(f"checkout-scm build {number}: pre-Pipeline definition URL/ref/revision absent")
    elif "Checking out Revision" in definition or expected["clone-url"] in definition:
        fail(f"git build {number}: unexpected pre-Pipeline SCM definition checkout")


def main(argv: list[str]) -> int:
    hermetic = False
    if len(argv) == 3 and argv[1] == "--hermetic":
        hermetic = True
        bundle_arg = argv[2]
    elif len(argv) == 2:
        bundle_arg = argv[1]
    else:
        fail("usage: validate-retained-scm-run.py [--hermetic] BUNDLE")
    root = pathlib.Path(bundle_arg).resolve()
    if root.joinpath("STATUS").read_text() != "complete\n":
        fail("STATUS is absent or incomplete")
    manifest(root)
    mode = root.joinpath("capture-mode.txt").read_text()
    if hermetic:
        if mode != "hermetic\n":
            fail("--hermetic accepts only an explicitly hermetic bundle")
        forbidden = [
            root / "oracle-before-verification.txt", root / "oracle-after-verification.txt",
            root / "oracle-snapshot-before", root / "oracle-snapshot-after",
        ]
        if any(path.exists() or path.is_symlink() for path in forbidden):
            fail("hermetic bundle is contaminated with production oracle artifacts")
        expected_container = None
    else:
        if mode != "production\n":
            fail("production validator rejects fake/hermetic evidence; use --hermetic only in tests")
        expected_container = validate_production_oracle(root)
    validate_tooling(root)
    validate_surface(root, hermetic=hermetic, expected_container=expected_container)
    commits = validate_fixture(root)
    schedule = root.joinpath("schedule.tsv").read_text().splitlines()
    if len(schedule) != 7 or schedule[0] != "build\tlabel\tsha\tresult\tprevious\tprevious-successful\tpayload":
        fail("schedule is not exactly six builds")
    parsed_schedule = [row.split("\t") for row in schedule[1:]]
    if [row[0] for row in parsed_schedule] != [str(number) for number in range(1, 7)]:
        fail("schedule build numbers are not 1..6")
    if [row[1] for row in parsed_schedule] != ["A", "B", "C", "F", "G", "D"]:
        fail("schedule does not implement main A/B/C, feature F/G, main D")
    if [row[3] for row in parsed_schedule] != ["SUCCESS", "FAILURE", "SUCCESS", "SUCCESS", "SUCCESS", "SUCCESS"]:
        fail("schedule does not contain the discriminating build-2 failure")
    expected_history = [
        ("-", "-"), (commits["A"], commits["A"]), (commits["B"], commits["A"]),
        ("-", "-"), (commits["F"], commits["F"]), (commits["C"], commits["C"]),
    ]
    for row, history in zip(parsed_schedule, expected_history):
        if row[2] != commits[row[1]] or (row[4], row[5]) != history or row[6] != row[1]:
            fail("schedule revision/history/payload relationship is wrong")
    run_ids: set[str] = set()
    clone_urls: set[str] = set()
    for producer in BASE:
        if sorted(p.name for p in (root / "runs" / producer).iterdir()) != [f"build-{n}" for n in range(1, 7)]:
            fail(f"{producer}: build directory set is not exact")
        for number in range(1, 7):
            expected = tsv_map(root / "runs" / producer / f"build-{number}" / "expected.tsv")
            suffix = "feature" if number in (4, 5) else "main"
            match = re.fullmatch(rf"fg177-retained/([^/]+)/{re.escape(producer)}/{suffix}", expected.get("branch", ""))
            if match is None:
                fail(f"{producer} build {number}: branch is not its isolated stable {suffix} ref")
            run_ids.add(match.group(1))
            clone_urls.add(expected.get("clone-url", ""))
            row = parsed_schedule[number - 1]
            if [expected.get(key) for key in ("build", "label", "sha", "result", "previous", "previous-successful", "payload")] != row:
                fail(f"{producer} build {number}: expectation differs from the global schedule")
            validate_build(root, producer, number, commits)
    if len(run_ids) != 1:
        fail("producer branches do not share exactly one isolated run identity")
    run_id = next(iter(run_ids))
    if len(clone_urls) != 1 or not next(iter(clone_urls)):
        fail("all builds/producers do not share one nonempty clone URL")
    if not hermetic and not re.fullmatch(r"[0-9]{8}T[0-9]{6}Z-[0-9a-f]{32}", run_id):
        fail("production run identity is not timestamp plus 128 cryptographic bits")
    prefix = f"fg177-retained/{run_id}"
    expected_targets = {
        *(f"refs/heads/{prefix}/pins/{label}" for label in "ABCDFG"),
        f"refs/heads/{prefix}/git/main", f"refs/heads/{prefix}/git/feature",
        f"refs/heads/{prefix}/checkout-scm/main", f"refs/heads/{prefix}/checkout-scm/feature",
    }
    target_rows = root.joinpath("target-refs.tsv").read_text().splitlines()
    if len(target_rows) != 10 or set(target_rows) != expected_targets:
        fail("preflight target-ref inventory is not exact")
    if root.joinpath("ref-preflight.tsv").read_bytes() != b"":
        fail("preflight found an already-existing target ref")
    pin_rows = root.joinpath("pin-refs.tsv").read_text().splitlines()
    expected_pins = {f"{commits[label]}\trefs/heads/{prefix}/pins/{label}" for label in "ABCDFG"}
    if len(pin_rows) != 6 or set(pin_rows) != expected_pins:
        fail("non-force pin publication does not exactly bind the six fixture commits")
    print(f"FG177 RETAINED SCM VALIDATION: {mode.strip()} 12-build capture is internally bound; raw render/wrong-index outcomes retained without broader claims")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
