#!/usr/bin/env python3
"""Measure native coordinator startup/POM handling with no target to remove."""

import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parent
FIXTURE = ROOT.parent / "ocaml-maven-poc" / "fixture"
BINARIES = {
    "ocaml": ROOT.parent / "ocaml-maven-poc" / "mini_mvn",
    "rust": ROOT / "mini_mvn_rust" if (ROOT / "mini_mvn_rust").exists() else ROOT / "target/release/mini_mvn_rust",
}


def once(tool):
    command = [str(BINARIES[tool]), "--project", str(FIXTURE), "clean"]
    started = time.perf_counter_ns()
    result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    elapsed = time.perf_counter_ns() - started
    if result.returncode:
        raise RuntimeError(f"{tool} clean failed: {result.stderr.decode(errors='replace')}")
    return elapsed


def main():
    count = int(sys.argv[1]) if len(sys.argv) > 1 else 2000
    if count <= 0:
        raise SystemExit("count must be positive")
    # The first invocation removes any output from the full-build comparison.
    once("ocaml")
    if (FIXTURE / "target").exists():
        raise RuntimeError("clean target still exists")
    for _ in range(100):
        once("ocaml")
        once("rust")
    samples = {"ocaml": [], "rust": []}
    for index in range(count):
        order = ("ocaml", "rust") if index % 2 == 0 else ("rust", "ocaml")
        for tool in order:
            samples[tool].append(once(tool))
    output = {
        "host": platform.node(),
        "scope": "process startup, literal POM/dependency parsing, no-op clean; no javac, JUnit, jar, or file deletion in measured runs",
        "count_per_tool": count,
        "binary_sha256": {tool: hashlib.sha256(path.read_bytes()).hexdigest() for tool, path in BINARIES.items()},
        "elapsed_nanoseconds": samples,
    }
    result_file = ROOT / "benchmark-results" / "clean-results.json"
    result_file.parent.mkdir(exist_ok=True)
    result_file.write_text(json.dumps(output) + "\n")
    for tool in ("ocaml", "rust"):
        values = sorted(samples[tool])
        print(f"{tool}: median={statistics.median(values)/1e6:.3f}ms, p95={values[int(0.95*(len(values)-1))]/1e6:.3f}ms")


if __name__ == "__main__":
    main()
