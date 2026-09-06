#!/usr/bin/env bash
# Hostile proof for the fail-closed corpus runtime-pin boundary. It uses this
# host's ordinary `make` as inert bytes and a fake ssh; no Jenkins, corpus,
# container runtime or network is used.
set -Eeuo pipefail
cd "$(dirname "$0")/.."

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/bin"
local_path=$(PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin command -v make)
local_sha=$(sha256sum -- "$local_path" | cut -d' ' -f1)
image_id=$(printf 'a%.0s' {1..64})
image_digest="sha256:$(printf 'b%.0s' {1..64})"

apply_fixture() {
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    make-pin make "$local_path" /usr/local/bin/make "$local_sha" "$image_id" "$image_digest" \
    > "$scratch/pins.tsv"
  export FAKE_REMOTE_PATH=/usr/local/bin/make
  export FAKE_REMOTE_TOOL_SHA=$local_sha
  export FAKE_IMAGE_ID=$image_id
  export FAKE_IMAGE_DIGEST=$image_digest
  unset FAKE_SSH_FAIL
}

cat > "$scratch/bin/ssh" <<'EOF'
#!/usr/bin/env bash
set -eu
remote_command=${!#}
[ -z "${FAKE_SSH_FAIL:-}" ] || exit 1
case "$remote_command" in
  *"podman exec "*"command -v"*) printf '%s\n' "$FAKE_REMOTE_PATH" ;;
  *"podman exec "*" sha256sum -- "*) printf '%s  %s\n' "$FAKE_REMOTE_TOOL_SHA" "$FAKE_REMOTE_PATH" ;;
  *"podman inspect "*"Image"*) printf '%s\n' "$FAKE_IMAGE_ID" ;;
  *"podman image inspect "*"Digest"*) printf '%s\n' "$FAKE_IMAGE_DIGEST" ;;
  *) printf 'unexpected ssh command: %s\n' "$remote_command" >&2; exit 3 ;;
esac
EOF
chmod +x "$scratch/bin/ssh"

check() {
  PATH="$scratch/bin:$PATH" \
  FOGELL_JENKINS_HOST=fake FOGELL_JENKINS_CONTAINER=jenkins-lab \
    ./scripts/check-corpus-runtime-pins.sh "$scratch/pins.tsv" make-pin
}

must_refuse() {
  label=$1 expected=$2
  set +e
  out=$(check 2>&1); rc=$?
  set -e
  [ "$rc" -ne 0 ] || { echo "RUNTIME-PIN PROOF FAILED: $label was accepted" >&2; exit 1; }
  case "$out" in *"$expected"*) ;; *) printf 'RUNTIME-PIN PROOF FAILED: %s emitted:\n%s\n' "$label" "$out" >&2; exit 1 ;; esac
  printf '  refused %s\n' "$label"
}

apply_fixture
check >/dev/null
echo "=== corpus runtime pin: exact tuple accepted ==="

sed -i "s#\t$local_path\t#\t/bin/not-the-resolved-make\t#" "$scratch/pins.tsv"
must_refuse "local command shadow/path drift" "local command resolves"
apply_fixture

bad_sha=$(printf 'c%.0s' {1..64})
sed -i "s/$local_sha/$bad_sha/" "$scratch/pins.tsv"
must_refuse "local tool-byte drift" "local tool SHA-256"
apply_fixture

FAKE_REMOTE_PATH=/opt/shadow/make
must_refuse "Jenkins command shadow/path drift" "Jenkins command resolves"
apply_fixture

FAKE_REMOTE_TOOL_SHA=$bad_sha
must_refuse "Jenkins tool-byte drift" "Jenkins tool SHA-256"
apply_fixture

FAKE_IMAGE_ID=$(printf 'd%.0s' {1..64})
must_refuse "container image replacement" "Jenkins image ID"
apply_fixture

FAKE_IMAGE_DIGEST="sha256:$(printf 'e%.0s' {1..64})"
must_refuse "image digest replacement" "Jenkins image digest"
apply_fixture

export FAKE_SSH_FAIL=1
must_refuse "unavailable remote identity" "could not resolve Jenkins tool"
apply_fixture

sed -i "s/$local_sha/bad/" "$scratch/pins.tsv"
must_refuse "malformed tool digest" "invalid tool SHA-256"
apply_fixture

printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
  make-pin make "$local_path" /usr/local/bin/make "$local_sha" "$image_id" "$image_digest" \
  >> "$scratch/pins.tsv"
must_refuse "duplicate pin id" "duplicate pin id"
apply_fixture

set +e
missing_out=$(PATH="$scratch/bin:$PATH" \
  FOGELL_JENKINS_HOST=fake FOGELL_JENKINS_CONTAINER=jenkins-lab \
  ./scripts/check-corpus-runtime-pins.sh "$scratch/pins.tsv" absent-pin 2>&1); missing_rc=$?
set -e
if [ "$missing_rc" -eq 0 ] || [[ "$missing_out" != *"is not defined"* ]]; then
  echo "RUNTIME-PIN PROOF FAILED: missing pin was accepted or misreported" >&2; exit 1
fi
echo "  refused missing pin id"

audit_runner() {
  runner=$1
  pre=$(rg -n '^verify_runtime_pins \|\| die ' "$runner" | sed -n '1s/:.*//p')
  run=$(rg -n 'dotnet "\$cli"' "$runner" | sed -n '1s/:.*//p')
  post=$(rg -n '^elif ! verify_runtime_pins; then$' "$runner" | sed -n '1s/:.*//p')
  promote=$(rg -n '^[[:space:]]*mkdir -p "\$FOGELL_RECEIPT_DIR"' "$runner" | sed -n '1s/:.*//p')
  [[ "$pre" =~ ^[0-9]+$ && "$run" =~ ^[0-9]+$ && "$post" =~ ^[0-9]+$ && "$promote" =~ ^[0-9]+$ ]] || return 1
  [ "$(rg -c '^  \./scripts/check-corpus-runtime-pins\.sh differential/corpus-runtime-pins\.tsv "\$\{pin_ids\[@\]\}"$' "$runner")" -eq 1 ] || return 1
  [ "$pre" -lt "$run" ] && [ "$run" -lt "$post" ] && [ "$post" -lt "$promote" ]
}

audit_runner scripts/run-corpus-differential.sh \
  || { echo "RUNTIME-PIN PROOF FAILED: runner pin boundary markers are missing or misordered" >&2; exit 1; }
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i '0,/^verify_runtime_pins || die /{/^verify_runtime_pins || die /d;}' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: pre-execution check mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i '/^elif ! verify_runtime_pins; then$/d' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: pre-promotion check mutant was accepted" >&2; exit 1
fi
echo "=== corpus runtime pin: both runner call sites and removal mutants proven ==="
echo "CORPUS RUNTIME PIN: ALL ASSERTIONS PASSED"
