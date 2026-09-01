#!/usr/bin/env bash
# FG-228 — bind retained probe bytes to their self-verifying receipts.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

bundle='evidence/20260901T023015Z-fg-228-stash-symlink-boundary'
receipts="$bundle/postfix-receipts"

for name in fg228-stash-symlinks-default fg228-stash-symlinks-optout; do
  probe="$bundle/probes/$name.Jenkinsfile"
  receipt="$receipts/$name.receipt.txt"
  [[ -f "$probe" && ! -L "$probe" && -f "$receipt" && ! -L "$receipt" ]] \
    || { echo "FG-228 evidence input missing, linked, or non-regular: $name" >&2; exit 1; }
  expected="$(sed -n 's/^case-digest:  *//p' "$receipt")"
  [[ "$expected" =~ ^[0-9a-f]{64}$ ]] \
    || { echo "FG-228 receipt has no unique canonical case digest: $name" >&2; exit 1; }
  actual="$(sha256sum "$probe" | sed 's/ .*//')"
  [[ "$actual" == "$expected" ]] \
    || { echo "FG-228 probe/receipt digest mismatch: $name" >&2; exit 1; }
done

dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- --verify-seals "$receipts"
echo 'FG-228 EVIDENCE PASS: 2 probe digests bound; 2 receipt seals recomputed'
