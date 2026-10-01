#!/usr/bin/env python3
"""Serial five-way warm-cache clean-package comparison on one host."""

import hashlib
import json
import os
from pathlib import Path
import platform
import re
import statistics
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parent
FIXTURE = ROOT.parent / "ocaml-maven-poc" / "fixture"
OUTPUT = ROOT / "benchmark-results"
JAR = FIXTURE / "target" / "build-fixture-1.0.0.jar"
OCAML_BINARY = ROOT.parent / "ocaml-maven-poc" / "mini_mvn"
RUST_ROOT = ROOT.parent / "rust-maven-poc"
RUST_BINARY = RUST_ROOT / "mini_mvn_rust" if (RUST_ROOT / "mini_mvn_rust").exists() else RUST_ROOT / "target" / "release" / "mini_mvn_rust"
FSHARP_MANAGED = ROOT / "publish" / "managed" / "FSharpMavenPoc"
FSHARP_AOT = ROOT / "publish" / "aot" / "FSharpMavenPoc"
EXPECTED = {
    "dev/fogell/fixture/Arithmetic.class",
    "dev/fogell/fixture/Series.class",
    "dev/fogell/fixture/Text.class",
    "dev/fogell/fixture/message.txt",
}
COMMANDS = {
    "maven": ["mvn", "-o", "-B", "-ntp", "-q", "clean", "package"],
    "ocaml": [str(OCAML_BINARY), "--project", str(FIXTURE), "clean", "package"],
    "rust": [str(RUST_BINARY), "--project", str(FIXTURE), "clean", "package"],
    "fsharp-managed": [str(FSHARP_MANAGED), "--project", str(FIXTURE), "clean", "package"],
    "fsharp-aot": [str(FSHARP_AOT), "--project", str(FIXTURE), "clean", "package"],
}


def version(command):
    result = subprocess.run(command, text=True, capture_output=True, check=True)
    return (result.stdout or result.stderr).strip()


def verify(tool, output):
    if tool != "maven":
        match = re.search(r"OK \((\d+) tests\)", output)
        if not match or int(match.group(1)) != 10:
            raise RuntimeError(f"{tool} run did not report 10 passing tests:\n{output}")
    else:
        reports = list((FIXTURE / "target" / "surefire-reports").glob("TEST-*.xml"))
        test_count = 0
        for report in reports:
            suite = ET.parse(report).getroot()
            if any(int(suite.attrib.get(key, "0")) for key in ("errors", "failures", "skipped")):
                raise RuntimeError(f"Maven test failure in {report}")
            test_count += int(suite.attrib["tests"])
        if test_count != 10:
            raise RuntimeError(f"Maven ran {test_count} tests, expected 10")
    with zipfile.ZipFile(JAR) as archive:
        names = set(archive.namelist())
        if not EXPECTED <= names:
            raise RuntimeError(f"JAR missing entries: {EXPECTED - names}")
        return {
            name: hashlib.sha256(archive.read(name)).hexdigest()
            for name in sorted(EXPECTED)
        }


def trial(tool, index, warmup=False):
    stem = f"{'warmup' if warmup else f'{index:02d}'}-{tool}"
    timing_file = OUTPUT / f"{stem}.time"
    log_file = OUTPUT / f"{stem}.log"
    command = COMMANDS[tool]
    before_load = os.getloadavg()
    started = time.monotonic()
    with log_file.open("w") as log:
        result = subprocess.run(
            ["/usr/bin/time", "-f", "%e,%U,%S,%M", "-o", str(timing_file)] + command,
            cwd=FIXTURE,
            stdout=log,
            stderr=subprocess.STDOUT,
            check=False,
        )
    elapsed = time.monotonic() - started
    output = log_file.read_text()
    if result.returncode:
        raise RuntimeError(f"{tool} exited {result.returncode}; see {log_file}\n{output[-2000:]}")
    hashes = verify(tool, output)
    wall, user, system, rss = timing_file.read_text().strip().split(",")
    return {
        "tool": tool,
        "iteration": index,
        "warmup": warmup,
        "command": command,
        "wall_seconds": float(wall),
        "monotonic_wall_seconds": round(elapsed, 4),
        "user_cpu_seconds": float(user),
        "system_cpu_seconds": float(system),
        "max_rss_kib": int(rss),
        "load_before": list(before_load),
        "load_after": list(os.getloadavg()),
        "test_count": 10,
        "jar_payload_sha256": hashes,
        "log": str(log_file),
        "time_file": str(timing_file),
    }


def main():
    count = int(sys.argv[1]) if len(sys.argv) > 1 else 10
    if count <= 0 or count % 5:
        raise SystemExit("trial count must be a positive multiple of 5 for balanced order")
    OUTPUT.mkdir(exist_ok=True)
    data = {
        "host": platform.node(),
        "platform": platform.platform(),
        "cpu_count": os.cpu_count(),
        "maven_version": version(["mvn", "-version"]).splitlines()[0],
        "java_version": version(["javac", "-version"]),
        "dotnet_version": version(["dotnet", "--version"]),
        "ocaml_version": os.environ.get("OCAML_VERSION", "4.14.1"),
        "rust_version": os.environ.get("RUSTC_VERSION", "unknown"),
        "ocaml_executable_sha256": hashlib.sha256(OCAML_BINARY.read_bytes()).hexdigest(),
        "rust_executable_sha256": hashlib.sha256(RUST_BINARY.read_bytes()).hexdigest(),
        "fsharp_managed_dll_sha256": hashlib.sha256((ROOT / "publish/managed/FSharpMavenPoc.dll").read_bytes()).hexdigest(),
        "fsharp_aot_executable_sha256": hashlib.sha256(FSHARP_AOT.read_bytes()).hexdigest(),
        "scope": "serial, offline warm-cache clean package; same three main and three test sources, one resource, ten JUnit 4 tests; balanced rotating order",
        "runs": [],
    }
    tools = ("maven", "ocaml", "rust", "fsharp-managed", "fsharp-aot")
    for tool in tools:
        print(f"warmup {tool}", flush=True)
        data["runs"].append(trial(tool, 0, warmup=True))
    for index in range(1, count + 1):
        offset = (index - 1) % len(tools)
        order = tools[offset:] + tools[:offset]
        for tool in order:
            print(f"{index}/{count} {tool}", flush=True)
            data["runs"].append(trial(tool, index))
    (OUTPUT / "results.json").write_text(json.dumps(data, indent=2) + "\n")
    payloads = {json.dumps(run["jar_payload_sha256"], sort_keys=True) for run in data["runs"]}
    if len(payloads) != 1:
        raise RuntimeError("payload hashes differed across tools or trials")
    for tool in tools:
        times = [run["wall_seconds"] for run in data["runs"] if run["tool"] == tool and not run["warmup"]]
        print(f"{tool}: {times}; median={statistics.median(times):.3f}s")


if __name__ == "__main__":
    main()
