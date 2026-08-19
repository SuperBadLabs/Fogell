#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

evidence='evidence/20260818-fg-177-measurement'
proof_tmp=$(mktemp -d)
trap 'rm -rf "$proof_tmp"' EXIT
metadata="$proof_tmp/metadata"
out="$proof_tmp/out"
mkdir -p "$metadata" "$out/raw-receipts"

printf '2.568.1\n' > "$metadata/jenkins-core.txt"
printf 'alpha\t1.0\ttrue\ttrue\n' > "$metadata/jenkins-plugins.tsv"
printf 'fixture/jenkins:2.568.1|%064d|sha256:%064d\n' 1 2 \
  > "$metadata/jenkins-controller-image.txt"
printf 'runner output\n' > "$out/probe-run.log"
printf 'probe-cli-exit=1\n' > "$out/probe-exit.txt"
printf 'echo(message: "one")\n' > "$out/one.Jenkinsfile"
printf 'echo(message: "two")\n' > "$out/two.Jenkinsfile"
printf 'receipt one\n' > "$out/raw-receipts/one.receipt.txt"
printf 'receipt two\n' > "$out/raw-receipts/two.receipt.txt"

manifest="$out/probe-run-manifest.tsv"
bash "$evidence/write-run-manifest.sh" \
  "$manifest" probes \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z 2026-08-19T10:00:02Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" "$metadata" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile"

grep -Fx $'started-at-utc\t2026-08-19T10:00:00Z' "$manifest"
grep -Fx $'oracle-verified-at-utc\t2026-08-19T10:00:01Z' "$manifest"
grep -Fx $'finished-at-utc\t2026-08-19T10:00:02Z' "$manifest"
grep -Fx $'jenkins-core\t2.568.1' "$manifest"
grep -Fx $'plugin-manifest-sha256\t'"$(sha256sum "$metadata/jenkins-plugins.tsv" | awk '{print $1}')" "$manifest"
grep -Fx $'controller-image-id\t'"$(printf '%064d' 1)" "$manifest"
grep -Fx $'controller-image-digest\tsha256:'"$(printf '%064d' 2)" "$manifest"
[[ $(awk -F '\t' '$1 == "case" { print $3 }' "$manifest") == $'one.Jenkinsfile\ntwo.Jenkinsfile' ]]

# A reversed timestamp and a missing receipt both refuse. Failure is atomic:
# the last complete manifest remains byte-identical and no temp is published.
manifest_hash=$(sha256sum "$manifest" | awk '{ print $1 }')
if bash "$evidence/write-run-manifest.sh" \
  "$manifest" probes \
  2026-08-19T10:00:02Z 2026-08-19T10:00:01Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" "$metadata" \
  "$out/one.Jenkinsfile" > "$proof_tmp/reversed.log" 2>&1; then
  echo 'ERROR: reversed provenance timestamps unexpectedly passed' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]

mv "$out/raw-receipts/two.receipt.txt" "$proof_tmp/two.receipt.txt"
if bash "$evidence/write-run-manifest.sh" \
  "$manifest" probes \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z 2026-08-19T10:00:02Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" "$metadata" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile" > "$proof_tmp/missing.log" 2>&1; then
  echo 'ERROR: missing receipt unexpectedly produced a manifest' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]
if find "$out" -maxdepth 1 -name 'probe-run-manifest.tsv.tmp.*' | grep -q .; then
  echo 'ERROR: refused manifest left a partial temp file' >&2
  exit 1
fi

printf 'RUN MANIFEST PROOF: timestamps ordered, provenance and case order bound, refusal atomic\n'
