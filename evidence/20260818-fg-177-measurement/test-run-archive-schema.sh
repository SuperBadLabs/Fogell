#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

test_tmp=$(mktemp -d)
trap 'rm -rf "$test_tmp"' EXIT
mkdir -p "$test_tmp/bin"
stub="$test_tmp/bin/dotnet"
cat > "$stub" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$FOGELL_STUB_CALLS"
case "$1" in
  build)
    printf 'stub Release build\n'
    exit "${FOGELL_STUB_BUILD_RC:-0}"
    ;;
  run)
    printf 'intentional archive schema CLI failure\n'
    exit 23
    ;;
  *)
    printf 'ERROR: unexpected dotnet command: %s\n' "$1" >&2
    exit 97
    ;;
esac
EOF
chmod +x "$stub"

out="$test_tmp/fresh-evidence-output"
calls="$test_tmp/dotnet-calls"
set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$out" \
FOGELL_STUB_CALLS="$calls" \
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
expected_build='build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --nologo'
expected_run_prefix='run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --no-build -- '
if [[ $(sed -n '1p' "$calls") != "$expected_build" || \
      $(sed -n '2p' "$calls") != "$expected_run_prefix"* || \
      $(wc -l < "$calls") -ne 2 ]]; then
  printf 'ERROR: archive runner did not build once before CLI; calls=%s\n' \
    "$(tr '\n' '|' < "$calls")" >&2
  exit 1
fi
grep -Fx 'intentional archive schema CLI failure' "$out/archive-schema-run.log"
grep -Fx 'archive-schema-cli-exit=23' "$out/archive-schema-exit.txt"

build_failure_out="$test_tmp/build-failure-output"
build_failure_calls="$test_tmp/build-failure-calls"
set +e
PATH="$test_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$build_failure_out" \
FOGELL_STUB_CALLS="$build_failure_calls" \
FOGELL_STUB_BUILD_RC=37 \
  bash evidence/20260818-fg-177-measurement/run-archive-schema.sh \
  > "$test_tmp/build-failure.log" 2>&1
build_failure_rc=$?
set -e
if [[ $build_failure_rc -ne 37 ]]; then
  printf 'ERROR: expected archive build rc 37, got %s\n' "$build_failure_rc" >&2
  exit 1
fi
if [[ $(sed -n '1p' "$build_failure_calls") != "$expected_build" || \
      $(wc -l < "$build_failure_calls") -ne 1 ]]; then
  printf 'ERROR: failed archive build did not stop before CLI; calls=%s\n' \
    "$(tr '\n' '|' < "$build_failure_calls")" >&2
  exit 1
fi
if [[ -e "$build_failure_out/archive-schema-exit.txt" ]]; then
  echo 'ERROR: failed archive build wrote a misleading CLI exit marker' >&2
  exit 1
fi

printf 'standalone archive runner created raw-receipts and propagated rc=%s\n' "$rc"
