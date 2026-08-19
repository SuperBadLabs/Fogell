#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

test_tmp=$(mktemp -d)
trap 'rm -rf "$test_tmp"' EXIT
mkdir -p "$test_tmp/bin"
stub="$test_tmp/bin/dotnet"
cat > "$stub" <<'EOF'
#!/usr/bin/env bash
printf 'intentional archive schema CLI failure\n'
exit 23
EOF
chmod +x "$stub"

out="$test_tmp/fresh-evidence-output"
set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$out" \
  bash evidence/20260818-fg-177-measurement/run-archive-schema.sh
rc=$?
set -e

if [[ $rc -ne 23 ]]; then
  echo "ERROR: expected standalone archive runner rc 23, got $rc" >&2
  exit 1
fi
if [[ ! -d "$out/raw-receipts" ]]; then
  echo "ERROR: standalone archive runner did not create raw-receipts" >&2
  exit 1
fi
grep -Fx 'intentional archive schema CLI failure' "$out/archive-schema-run.log"
grep -Fx 'archive-schema-cli-exit=23' "$out/archive-schema-exit.txt"

printf 'standalone archive runner created raw-receipts and propagated rc=%s\n' "$rc"
