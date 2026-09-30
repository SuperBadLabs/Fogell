#!/usr/bin/env python3
"""Serial warm-cache clean-package comparison on one host."""

import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parent
FIXTURE = ROOT / "fixture"
OUTPUT = ROOT / "benchmark-results"
JAR = FIXTURE / "target" / "build-fixture-1.0.0.jar"
EXPECTED = {
    "dev/fogell/fixture/Arithmetic.class",
    "dev/fogell/fixture/Series.class",
    "dev/fogell/fixture/Text.class",
    "dev/fogell/fixture/message.txt",
}
COMMANDS = {
    "maven": ["mvn", "-o", "-B", "-ntp", "-q", "clean", "package"],
    "ocaml": [str(ROOT / "mini_mvn"), "--project", str(FIXTURE), "clean", "package"],
}


def version(command):
    result = subprocess.run(command, text=True, capture_output=True, check=True)
    return (result.stdout or result.stderr).strip()


def verify(tool, output):
    if tool == "ocaml":
        match = re.search(r"OK \((\d+) tests\)", output)
        if not match or int(match.group(1)) != 10:
            raise RuntimeError(f"OCaml run did not report 10 passing tests:\n{output}")
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
    count = int(sys.argv[1]) if len(sys.argv) > 1 else 7
    OUTPUT.mkdir(exist_ok=True)
    compiler = ROOT / "toolchain" / "usr" / "bin" / "ocamlopt"
    data = {
        "host": platform.node(),
        "platform": platform.platform(),
        "cpu_count": os.cpu_count(),
        "maven_version": version(["mvn", "-version"]).splitlines()[0],
        "java_version": version(["javac", "-version"]),
        "ocaml_version": version([str(compiler), "-version"]) if compiler.exists() else "compiler unavailable",
        "ocaml_executable_sha256": hashlib.sha256((ROOT / "mini_mvn").read_bytes()).hexdigest(),
        "scope": "serial, offline warm-cache clean package; same three main and three test sources, one resource, ten JUnit 4 tests",
        "runs": [],
    }
    for tool in ("maven", "ocaml"):
        print(f"warmup {tool}", flush=True)
        data["runs"].append(trial(tool, 0, warmup=True))
    for index in range(1, count + 1):
        order = ("maven", "ocaml") if index % 2 else ("ocaml", "maven")
        for tool in order:
            print(f"{index}/{count} {tool}", flush=True)
            data["runs"].append(trial(tool, index))
    (OUTPUT / "results.json").write_text(json.dumps(data, indent=2) + "\n")
    for tool in ("maven", "ocaml"):
        times = [run["wall_seconds"] for run in data["runs"] if run["tool"] == tool and not run["warmup"]]
        print(f"{tool}: {times}; median={sorted(times)[len(times)//2]:.2f}s")


if __name__ == "__main__":
    main()
