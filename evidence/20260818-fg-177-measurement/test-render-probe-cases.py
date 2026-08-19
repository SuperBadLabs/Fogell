#!/usr/bin/env python3
"""Filesystem-shape and atomicity proof for FG-177 probe rendering."""

from __future__ import annotations

import os
import pathlib
import shutil
import stat
import subprocess
import tempfile


EVIDENCE = pathlib.Path(__file__).resolve().parent
RENDERER = EVIDENCE / "render-probe-cases.py"


def invoke(output: pathlib.Path, scm: str = "file:///fixture/repo.git") -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    env.update(
        {
            "FOGELL_RENDERED_CASES_DIR": str(output),
            "FOGELL_SCM_URL": scm,
            "FOGELL_GIT_PINNED_BRANCH": "fogell-pins/" + "a" * 40,
            "PYTHONDONTWRITEBYTECODE": "1",
        }
    )
    return subprocess.run(
        ["python3", str(RENDERER)],
        cwd=EVIDENCE.parents[1],
        env=env,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
        timeout=20,
    )


def snapshot(path: pathlib.Path) -> tuple[tuple[str, int, bytes | str], ...]:
    records: list[tuple[str, int, bytes | str]] = []
    for entry in sorted(path.iterdir(), key=lambda item: item.name):
        mode = entry.lstat().st_mode
        if stat.S_ISREG(mode):
            value: bytes | str = entry.read_bytes()
        elif stat.S_ISLNK(mode):
            value = os.readlink(entry)
        else:
            value = "special"
        records.append((entry.name, stat.S_IFMT(mode), value))
    return tuple(records)


def refuse_unchanged(output: pathlib.Path, label: str) -> None:
    before = snapshot(output) if output.is_dir() and not output.is_symlink() else None
    result = invoke(output)
    if result.returncode == 0:
        raise AssertionError(f"{label} unexpectedly rendered:\n{result.stdout}")
    after = snapshot(output) if output.is_dir() and not output.is_symlink() else None
    if after != before:
        raise AssertionError(f"{label} mutated output on refusal")


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="fg177-render-proof.") as temporary:
        root = pathlib.Path(temporary)
        pristine = root / "pristine"
        result = invoke(pristine)
        if result.returncode != 0:
            raise AssertionError(f"fresh render failed:\n{result.stdout}")
        pristine_snapshot = snapshot(pristine)
        if len(pristine_snapshot) != 6:
            raise AssertionError("fresh render did not publish the exact six-file set")

        # A valid prior set is replaced atomically and remains exact.
        result = invoke(pristine, "file:///fixture/other.git")
        if result.returncode != 0 or len(snapshot(pristine)) != 6:
            raise AssertionError(f"valid replacement failed:\n{result.stdout}")

        def valid_copy(name: str) -> pathlib.Path:
            destination = root / name
            shutil.copytree(pristine, destination)
            return destination

        output = valid_copy("unexpected-file")
        (output / "extra").write_text("sentinel", encoding="ascii")
        refuse_unchanged(output, "unexpected regular file")

        output = valid_copy("unexpected-directory")
        (output / "extra").mkdir()
        refuse_unchanged(output, "unexpected directory")

        outside = root / "outside"
        outside.write_text("outside-sentinel", encoding="ascii")
        output = valid_copy("expected-symlink")
        expected_name = next(iter(sorted(path.name for path in output.iterdir())))
        (output / expected_name).unlink()
        (output / expected_name).symlink_to(outside)
        refuse_unchanged(output, "expected-name symlink")
        if outside.read_text(encoding="ascii") != "outside-sentinel":
            raise AssertionError("expected-name symlink escaped the output root")

        output = valid_copy("unexpected-symlink")
        (output / "extra").symlink_to(outside)
        refuse_unchanged(output, "unexpected symlink")

        output = valid_copy("unexpected-fifo")
        os.mkfifo(output / "extra")
        refuse_unchanged(output, "unexpected FIFO")

        output = valid_copy("root-file-parent")
        root_file = root / "render-root-file"
        root_file.write_text("root-sentinel", encoding="ascii")
        result = invoke(root_file)
        if result.returncode == 0 or root_file.read_text() != "root-sentinel":
            raise AssertionError("non-directory output root was not preserved")

        real_target = valid_copy("root-symlink-target")
        root_symlink = root / "root-symlink"
        root_symlink.symlink_to(real_target, target_is_directory=True)
        target_before = snapshot(real_target)
        result = invoke(root_symlink)
        if result.returncode == 0 or snapshot(real_target) != target_before:
            raise AssertionError("symlink output root was followed or mutated")

        real_parent = root / "real-parent"
        real_parent.mkdir()
        parent_symlink = root / "parent-symlink"
        parent_symlink.symlink_to(real_parent, target_is_directory=True)
        result = invoke(parent_symlink / "rendered")
        if result.returncode == 0 or (real_parent / "rendered").exists():
            raise AssertionError("symlink output parent was followed")

        traversal = root / "real-parent" / ".." / "traversed"
        result = invoke(traversal)
        if result.returncode == 0 or (root / "traversed").exists():
            raise AssertionError("parent traversal output was accepted")

        missing_parent = root / "missing" / "rendered"
        result = invoke(missing_parent)
        if result.returncode == 0 or missing_parent.parent.exists():
            raise AssertionError("missing output parent was created")

        output = valid_copy("invalid-scm")
        before = snapshot(output)
        result = invoke(output, "bad\nurl")
        if result.returncode == 0 or snapshot(output) != before:
            raise AssertionError("invalid SCM input mutated a valid prior output")

    print(
        "RENDER PROBE CASES FILESYSTEM PROOF: fresh/exact replacement passed; "
        "unexpected file/dir/symlink/FIFO, expected symlink, root/parent symlink, "
        "traversal, missing parent, and invalid input refused without mutation"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
