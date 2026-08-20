#!/usr/bin/env python3
"""Static name/type proof for the atomic publication helper."""

from __future__ import annotations

import importlib.util
import pathlib
import py_compile
import tempfile
import typing


MODULE_PATH = pathlib.Path(__file__).with_name("publish-run-bundle.py")

with tempfile.TemporaryDirectory() as temporary:
    py_compile.compile(
        str(MODULE_PATH),
        cfile=str(pathlib.Path(temporary) / "publish-run-bundle.pyc"),
        doraise=True,
    )

spec = importlib.util.spec_from_file_location("fg177_publish_run_bundle", MODULE_PATH)
if spec is None or spec.loader is None:
    raise RuntimeError(f"cannot load atomic publication helper from {MODULE_PATH}")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
hints = typing.get_type_hints(module.fail)
if hints != {"message": str, "return": typing.NoReturn}:
    raise RuntimeError(f"unexpected fail() type hints: {hints!r}")

print("ATOMIC PUBLISH TYPE PROOF: module compiles and fail() resolves NoReturn")
