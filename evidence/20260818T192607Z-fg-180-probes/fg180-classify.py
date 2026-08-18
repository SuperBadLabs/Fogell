#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# ///
"""Mechanical classification of tier-3 corpus rejects: for each ledger row,
pull the FIRST error position and print the source line with a caret so the
construct at the stop point is read from the file, not guessed by eye.
Parse-only evidence gathering; corpus code is never executed."""
import csv, re, sys
from pathlib import Path

CORPUS = Path("/sn8100/work/exchange/crucible-gate/corpus/jenkinsfiles")
LEDGER = Path("/home/srikanth/projects/fogell/docs/COMPATIBILITY-LEDGER.tsv")

pos_re = re.compile(r"@(\d+):(\d+)")

rows = []
for line in LEDGER.read_text().splitlines():
    if line.startswith("#") or "\t" not in line:
        continue
    f = line.split("\t")
    if len(f) < 4 or f[2] != "malformed_syntax":
        continue
    name, detail = f[0], f[3]
    # innermost (first-reported) position: for "script block did not parse ...
    # at L:C: ... @L2:C2" the @ position is where the SCRIPT BLOCK starts in
    # the outer file; the inner L:C is inside the block. Take the LAST @ match
    # for the outer anchor and any inner "at L:C" separately.
    outer = pos_re.findall(detail)
    inner = re.findall(r"at (\d+):(\d+)", detail)
    rows.append((name, detail, outer, inner))

print(f"{len(rows)} malformed_syntax rows")
for name, detail, outer, inner in rows:
    src = (CORPUS / name).read_text(errors="replace").splitlines()
    print(f"\n=== {name}")
    print(f"    {detail}")
    if outer:
        l, c = map(int, outer[-1])
        if inner:
            il, ic = map(int, inner[0])
            # inner position is relative to the script block's text, whose
            # first line is outer line l (block opens there)
            al = l + il - 1
            line_txt = src[al - 1] if al - 1 < len(src) else "<past EOF>"
            print(f"    inner {il}:{ic} -> file line ~{al}: {line_txt.rstrip()}")
            print(f"    caret: {' ' * (ic - 1)}^ (col in block-relative line)")
        else:
            line_txt = src[l - 1] if l - 1 < len(src) else "<past EOF>"
            print(f"    line {l}: {line_txt.rstrip()}")
            print(f"    caret: {' ' * (c - 1)}^")
