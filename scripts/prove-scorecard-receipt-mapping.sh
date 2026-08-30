#!/usr/bin/env bash
# FG-163/FG-166. Proves that scorecard naming strips exactly one terminal
# `.Jenkinsfile` extension and that freshness carries the originating case forward
# to every exact receipt name the writer can emit. In particular, a singleton
# case literally named `literal.b1.Jenkinsfile` is not the same thing as build 1
# of `literal.Jenkinsfile`, while every receipt of a real multi-build case maps
# back to that one physical case.
#
# This is a self-contained mapping/freshness proof. It stubs corpus verification
# and scoring, never runs a Jenkinsfile, and makes no claim that mtimes establish
# content identity or that receipt seals are valid. The production check remains
# a warning because checkout mtimes are environment state; seal verification is
# owned by the differential CLI.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
GENERATOR="$PWD/scripts/bin/generate-scorecard"
if ! audit_check=$(./scripts/build-audits.sh --check 2>&1); then
  echo "SCORECARD RECEIPT-MAPPING PROOF FAILED: audit binaries missing or stale — run scripts/build-audits.sh" >&2
  printf '%s\n' "$audit_check" | tail -20 >&2
  exit 1
fi
LAB=$(mktemp -d /tmp/fogell-scorecard-mapping-proof.XXXXXX)
trap 'rm -rf "$LAB"' EXIT

new_root() {
  local name=$1
  local root="$LAB/$name"
  mkdir -p "$root/scripts" "$root/bin" "$root/docs" \
    "$root/differential/cases" "$root/differential/receipts"
  mkdir -p "$root/scripts/bin"
  cp "$GENERATOR" "$root/scripts/bin/generate-scorecard"
  cat > "$root/scripts/verify-corpus.sh" <<'EOF'
#!/bin/sh
exit 0
EOF
  cat > "$root/bin/dotnet" <<'EOF'
#!/bin/sh
if [ -e scorer-refuse ]; then
  printf 'planted scorer stdout detail\n'
  exit 1
fi
printf 'file\tverdict\tcode\tstages\tsteps\tdetail\n'
EOF
  chmod +x "$root/scripts/bin/generate-scorecard" \
    "$root/scripts/verify-corpus.sh" "$root/bin/dotnet"
  printf '%s\n' "$root"
}

run_generator() {
  local root=$1
  PATH="$root/bin:$PATH" "$root/scripts/bin/generate-scorecard" 2>&1
}

write_case() {
  local root=$1 name=$2 builds=$3
  printf 'pipeline { agent none; stages { } }\n' > "$root/differential/cases/$name.Jenkinsfile"
  if [ "$builds" -eq 2 ]; then
    printf '//// NEXT BUILD ////\n' >> "$root/differential/cases/$name.Jenkinsfile"
    printf 'pipeline { agent none; stages { } }\n' >> "$root/differential/cases/$name.Jenkinsfile"
  fi
}

write_receipt() {
  local root=$1 name=$2
  printf '# synthetic receipt for FG-166 mapping only\n' > "$root/differential/receipts/$name.receipt.txt"
}

OLDER=202401010000.00
MIDDLE=202401020000.00
NEWER=202401030000.00

root=$(new_root mapping)

touch "$root/scorer-refuse"
set +e
scorer_output=$(run_generator "$root")
scorer_rc=$?
set -e
rm -f "$root/scorer-refuse"
if [ "$scorer_rc" -eq 0 ] \
  || ! grep -Fq 'FAIL: corpus scorer did not run' <<<"$scorer_output" \
  || ! grep -Fq 'planted scorer stdout detail' <<<"$scorer_output"; then
  echo 'FAIL: scorer stdout diagnostic was discarded'
  printf '%s\n' "$scorer_output" | sed 's/^/  | /'
  exit 1
fi

# The doubled-suffix row exercises the scorecard's corpus promotion and ledger
# evidence derivations as well as its expected-case mapping. All three must use
# the same terminal-only rule as the writer.
cat > "$root/bin/dotnet" <<'EOF'
#!/bin/sh
printf 'file\tverdict\tcode\tstages\tsteps\tdetail\n'
printf 'embedded.Jenkinsfile.Jenkinsfile\tok\t\t1\t1\t\n'
EOF
chmod +x "$root/bin/dotnet"

# A literal `.b1` singleton is the reported defect. The two multi-build fixtures
# hold both directions: every receipt can be stale independently, and two stale
# receipts from one case must produce two warnings rather than one case warning.
write_case "$root" literal.b1 1
write_receipt "$root" literal.b1
touch -t "$NEWER" "$root/differential/cases/literal.b1.Jenkinsfile"
touch -t "$OLDER" "$root/differential/receipts/literal.b1.receipt.txt"

write_case "$root" mixed 2
write_receipt "$root" mixed.b1
write_receipt "$root" mixed.b2
touch -t "$MIDDLE" "$root/differential/cases/mixed.Jenkinsfile"
touch -t "$OLDER" "$root/differential/receipts/mixed.b1.receipt.txt"
touch -t "$NEWER" "$root/differential/receipts/mixed.b2.receipt.txt"

write_case "$root" multi 2
write_receipt "$root" multi.b1
write_receipt "$root" multi.b2
touch -t "$NEWER" "$root/differential/cases/multi.Jenkinsfile"
touch -t "$OLDER" "$root/differential/receipts/multi.b1.receipt.txt"
touch -t "$MIDDLE" "$root/differential/receipts/multi.b2.receipt.txt"

# Ordinary singletons retain the established no-warning direction when the
# receipt is at least as new as its case.
write_case "$root" plain 1
write_receipt "$root" plain
touch -t "$OLDER" "$root/differential/cases/plain.Jenkinsfile"
touch -t "$NEWER" "$root/differential/receipts/plain.receipt.txt"

# FG-163's exact collision shape. The physical case ends in two occurrences;
# the writer and generator must remove only the terminal one.
write_case "$root" embedded.Jenkinsfile 1
write_receipt "$root" embedded.Jenkinsfile
cat >> "$root/differential/receipts/embedded.Jenkinsfile.receipt.txt" <<'EOF'
jenkins-core: 2.568.1
VERDICT: PROVEN (tier 1)
EOF
touch -t "$OLDER" "$root/differential/cases/embedded.Jenkinsfile.Jenkinsfile"
touch -t "$NEWER" "$root/differential/receipts/embedded.Jenkinsfile.receipt.txt"

if ! output=$(run_generator "$root"); then
  echo "FAIL: compliant forward mapping did not generate"
  printf '%s\n' "$output" | sed 's/^/  | /'
  exit 1
fi

grep -Fqx '| 7 | 7 | 1 of 7 |' "$root/docs/COMPATIBILITY-SCORECARD.md" || {
  echo "FAIL: terminal suffix and forward mapping did not retain all seven exact expected receipt names"
  sed 's/^/  | /' "$root/docs/COMPATIBILITY-SCORECARD.md"
  exit 1
}

grep -Fqx $'embedded.Jenkinsfile.Jenkinsfile\t1\t-\treceipt:embedded.Jenkinsfile' \
  "$root/docs/COMPATIBILITY-LEDGER.tsv" || {
  echo "FAIL: doubled terminal suffix did not retain its exact corpus promotion and evidence name"
  sed 's/^/  | /' "$root/docs/COMPATIBILITY-LEDGER.tsv"
  exit 1
}

warnings=$(printf '%s\n' "$output" | grep '^WARN: receipt ' || true)
expected=$(cat <<'EOF'
WARN: receipt literal.b1 is OLDER than its case — the case was edited after the proof; re-run the suite
WARN: receipt mixed.b1 is OLDER than its case — the case was edited after the proof; re-run the suite
WARN: receipt multi.b1 is OLDER than its case — the case was edited after the proof; re-run the suite
WARN: receipt multi.b2 is OLDER than its case — the case was edited after the proof; re-run the suite
EOF
)

if [ "$warnings" != "$expected" ]; then
  echo "FAIL: stale warnings were not the exact sorted receipt inventory"
  echo "expected:"
  printf '%s\n' "$expected" | sed 's/^/  | /'
  echo "actual:"
  printf '%s\n' "$warnings" | sed 's/^/  | /'
  exit 1
fi

# The collision must be rejected while the two source cases are still visible.
# Constructing a map/set first would silently choose one source and pass this arm.
collision=$(new_root collision)
write_case "$collision" clash 2
write_case "$collision" clash.b1 1
set +e
collision_output=$(run_generator "$collision")
collision_rc=$?
set -e

[ "$collision_rc" -ne 0 ] || {
  echo "FAIL: two cases producing clash.b1 were silently deduplicated"
  exit 1
}
[ "$(printf '%s\n' "$collision_output" | grep -Fxc 'FAIL: two cases expect the same receipt name: clash.b1' || true)" -eq 1 ] || {
  echo "FAIL: collision refusal did not name the exact duplicate receipt"
  printf '%s\n' "$collision_output" | sed 's/^/  | /'
  exit 1
}
if find "$collision/docs" -mindepth 1 -type f -print -quit | grep -q .; then
  echo "FAIL: collision wrote generated artifacts before refusing"
  exit 1
fi

echo "SCORECARD RECEIPT-MAPPING PROOF: one terminal Jenkinsfile suffix, literal .b1, independent and repeated multi-build warnings, fresh singleton controls, exact sorting, pre-map collision refusal, and scorer stdout diagnostics all hold"
