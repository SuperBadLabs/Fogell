#!/usr/bin/env python3
from __future__ import annotations
import importlib.util
import pathlib
import subprocess
import sys

sys.dont_write_bytecode = True

EVIDENCE = pathlib.Path(__file__).resolve().parent
VALIDATOR = EVIDENCE / "scm_url.py"
SPEC = importlib.util.spec_from_file_location("fg177_scm_url", VALIDATOR)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

def main() -> int:
    valid = {
        "git://fixture.example/repo.git": "git://fixture.example/repo.git",
        "https://fixture.example/repo.git": "https://fixture.example/repo.git",
        "ssh://fixture.example/repo.git": "ssh://fixture.example/repo.git",
        "file:///tmp/clean/repo.git": "file:///tmp/clean/repo.git",
        "file:///tmp/with space/repo.git": "file:///tmp/with%20space/repo.git",
        "file:///tmp/with%20space/repo.git": "file:///tmp/with%20space/repo.git",
        "file://localhost/tmp/repo.git": "file://localhost/tmp/repo.git",
    }
    for candidate, expected in valid.items():
        if MODULE.canonical_scm_url(candidate) != expected:
            raise AssertionError("valid SCM URL canonicalized incorrectly")
    secret = "do-not-reflect-this-secret"
    invalid = (
        f"https://user:{secret}@fixture.example/repo.git",
        f"https://fixture.example/repo.git?token={secret}",
        f"https://fixture.example/repo.git#{secret}",
        f"https://fixture.example/{secret}@repo.git",
        f"https://fixture.example/{secret}:password/repo.git",
        f"https://fixture.example/{secret};param/repo.git",
        f"https://fixture.example/{secret}%40repo.git",
        f"https://fixture.example/{secret}%3Arepo.git",
        f"https://fixture.example/{secret}%3brepo.git",
        f"https://fixture.example/{secret}%3frepo.git",
        f"https://fixture.example/{secret}%23repo.git",
        "file:///tmp/repo%", "file:///tmp/repo%0", "file:///tmp/repo%GG",
        "file:///tmp/repo%ff", "file:///tmp/repo%c0%af",
        "file:///tmp/repo%ed%a0%80", "file:///tmp/répo.git",
        "file://remote.example/tmp/repo.git", "fixture.example:repo.git",
        "git:///missing-host.git", "https://fixture.example",
    )
    expected_error = "ERROR: FOGELL_SCM_URL is not a strict credential-free URL\n"
    for candidate in invalid:
        result = subprocess.run(
            [sys.executable, str(VALIDATOR), candidate], text=True,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False,
        )
        if result.returncode == 0 or result.stdout != expected_error:
            raise AssertionError("unsafe SCM URL did not refuse canonically")
        if secret in result.stdout or candidate in result.stdout:
            raise AssertionError("SCM URL refusal reflected caller-controlled input")
    print("SCM URL PROOF: clean and spaced file URLs pass; unsafe URL forms refuse without reflection")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
