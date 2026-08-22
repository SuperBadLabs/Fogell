#!/usr/bin/env python3
"""Hermetic Jenkins-driver double.  It derives only from runner-written inputs."""
from __future__ import annotations
import html, json, os, pathlib, shutil, sys

state = pathlib.Path(os.environ["FG177_FAKE_STATE"])
state.mkdir(parents=True, exist_ok=True)

def read_expected(directory: pathlib.Path) -> dict[str, str]:
    return dict(line.split("\t", 1) for line in (directory / "expected.tsv").read_text().splitlines())

def surface(destination: pathlib.Path) -> None:
    destination.mkdir(parents=True)
    (destination / "identity.tsv").write_text(
        "container-id\t" + "1" * 64 + "\n"
        "image-name\tfixture/jenkins:2.568.1\n"
        "image-id\t" + "2" * 64 + "\n"
        "image-digest\tsha256:" + "3" * 64 + "\n"
        "session-sha256\t" + "4" * 64 + "\n")
    (destination / "jenkins-core.txt").write_text("2.568.1\n")
    (destination / "jenkins-plugins.tsv").write_text(
        "git\t5.10.1\ttrue\ttrue\ngit-client\t6.6.1\ttrue\ttrue\n"
        "scm-api\t728.vc30dcf7a_0df5\ttrue\ttrue\nworkflow-scm-step\t466.va_d69e602552b_\ttrue\ttrue\n")
    digest = "\t".join(("a" * 64, "b" * 64, "c" * 64))
    (destination / "git-plugin.tsv").write_text("git\t5.10.1\t" + digest + "\n")
    (destination / "workflow-scm-step-plugin.tsv").write_text("workflow-scm-step\t466.va_d69e602552b_\t" + digest + "\n")

def assert_absent(job: str) -> None:
    if (state / f"{job}.json").exists():
        raise SystemExit("fake job already exists")

def configure(job: str, kind: str, branch: str, case: pathlib.Path, url: str,
              destination: pathlib.Path) -> None:
    if not case.is_file(): raise SystemExit("missing case")
    (state / f"{job}.json").write_text(json.dumps({"kind": kind, "branch": branch, "url": url}))
    if kind == "git":
        script = html.escape(case.read_text(), quote=False)
        xml = ("<flow-definition><definition class=\"fixture.CpsFlowDefinition\">"
               f"<script>{script}</script><sandbox>true</sandbox>"
               "</definition></flow-definition>")
    else:
        xml = ("<flow-definition><definition class=\"fixture.CpsScmFlowDefinition\"><scm>"
               "<userRemoteConfigs><hudson.plugins.git.UserRemoteConfig>"
               f"<url>{html.escape(url)}</url></hudson.plugins.git.UserRemoteConfig></userRemoteConfigs>"
               "<branches><hudson.plugins.git.BranchSpec>"
               f"<name>*/{html.escape(branch)}</name></hudson.plugins.git.BranchSpec></branches>"
               "</scm><scriptPath>Jenkinsfile</scriptPath><lightweight>false</lightweight>"
               "</definition></flow-definition>")
    (destination / "submitted-config.xml").write_text(xml)
    (destination / "returned-config.xml").write_text(xml)

def build(job: str, number: int, destination: pathlib.Path) -> None:
    config = json.loads((state / f"{job}.json").read_text())
    expected = read_expected(destination)
    producer, branch, revision = config["kind"], expected["branch"], expected["sha"]
    entries = {"GIT_BRANCH": f"origin/{branch}", "GIT_COMMIT": revision, "GIT_URL": expected["clone-url"]}
    if producer == "git": entries["GIT_LOCAL_BRANCH"] = branch
    if expected["previous"] != "-": entries["GIT_PREVIOUS_COMMIT"] = expected["previous"]
    if expected["previous-successful"] != "-": entries["GIT_PREVIOUS_SUCCESSFUL_COMMIT"] = expected["previous-successful"]
    lines = ["[Pipeline] Start"]
    if producer == "checkout-scm":
        lines = [f"Cloning repository {expected['clone-url']}",
                 f"Checking out Revision {revision} (refs/remotes/origin/{branch})",
                 "[Pipeline] Start"]
    lines += [
        f"FG177 MAP PRODUCER={producer} BUILD={number} CLASS=java.util.TreeMap",
        "FG177 MAP RENDER=[" + ", ".join(f"{key}:{entries[key]}" for key in sorted(entries)) + "]",
        "FG177 MAP KEYS=" + ",".join(sorted(entries)),
    ]
    lines += [f"FG177 MAP ENTRY={key}|java.lang.String|{entries[key]}" for key in sorted(entries)]
    for key, name in (("GIT_PREVIOUS_COMMIT", "previous"), ("GIT_PREVIOUS_SUCCESSFUL_COMMIT", "previous-successful")):
        present = expected[name] != "-"; value = expected[name] if present else "null"
        lines.append(f"FG177 HISTORY KEY={key}|PRESENT={str(present).lower()}|VALUE={value}")
    lines += [
        f"FG177 ACCESS PROPERTY={revision}|INDEX={revision}|DYNAMIC={revision}",
        "FG177 MISSING PROPERTY=null|INDEX=null|GET=null|CONTAINS=false",
        "FG177 WRONG-INDEX integer=THREW:java.lang.ClassCastException",
        "FG177 WRONG-INDEX null=THREW:java.lang.NullPointerException",
    ]
    console = "\n".join(lines) + "\n"
    (destination / "console.txt").write_text(console)
    prefix = console.split("[Pipeline] Start", 1)[0]
    (destination / "definition-scm.txt").write_text(prefix)
    builddata = {
        "_class": "hudson.plugins.git.util.BuildData",
        "buildsByBranchName": {f"refs/remotes/origin/{branch}": {"revision": {"SHA1": revision}}},
        "lastBuiltRevision": {"SHA1": revision},
        "remoteUrls": [expected["clone-url"]],
    }
    (destination / "builddata.json").write_text(json.dumps([builddata]) + "\n")
    artifacts = [{"relativePath": f"checkout/{name}"} for name in ("fg177-workspace-revision.txt", "fg177-workspace-payload.txt")]
    (destination / "build.json").write_text(json.dumps({"number": number, "result": expected["result"], "building": False, "artifacts": artifacts}) + "\n")
    artifact_dir = destination / "artifacts"; artifact_dir.mkdir()
    (artifact_dir / "fg177-workspace-revision.txt").write_text(revision + "\n")
    (artifact_dir / "fg177-workspace-payload.txt").write_text(expected["payload"] + "\n")

args = sys.argv[1:]
if args[0] == "surface": surface(pathlib.Path(args[1]))
elif args[0] == "reset":
    target = state / f"{args[1]}.json"
    if target.exists(): target.unlink()
elif args[0] == "assert-absent": assert_absent(args[1])
elif args[0] == "configure": configure(args[1], args[2], args[3], pathlib.Path(args[4]), args[5], pathlib.Path(args[6]))
elif args[0] == "build": build(args[1], int(args[2]), pathlib.Path(args[3]))
else: raise SystemExit("unexpected fake-driver command")
