#!/usr/bin/env bash
# FG-037. Prove the retained thin Git bundle reconstructs the exact measured
# source commit from a prerequisite already carried by the publication branch.
set -euo pipefail

if [ "$#" -ne 6 ]; then
  echo "usage: $0 <bundle> <allowed-signers> <prerequisite> <bundle-head> <measured-commit> <measured-tree>" >&2
  exit 2
fi

bundle_input=$1
allowed_signers_input=$2
prerequisite=$3
bundle_head=$4
measured_commit=$5
measured_tree=$6

# Do not let caller-supplied repository/object environment bleed into either
# the publication checkout or the isolated import. In particular,
# GIT_ALTERNATE_OBJECT_DIRECTORIES could make an empty bundle appear complete.
clean_git_env=(
  -u GIT_ALTERNATE_OBJECT_DIRECTORIES
  -u GIT_CONFIG
  -u GIT_CONFIG_PARAMETERS
  -u GIT_CONFIG_COUNT
  -u GIT_OBJECT_DIRECTORY
  -u GIT_DIR
  -u GIT_WORK_TREE
  -u GIT_IMPLICIT_WORK_TREE
  -u GIT_GRAFT_FILE
  -u GIT_INDEX_FILE
  -u GIT_NO_REPLACE_OBJECTS
  -u GIT_REPLACE_REF_BASE
  -u GIT_PREFIX
  -u GIT_SHALLOW_FILE
  -u GIT_COMMON_DIR
  GIT_CONFIG_NOSYSTEM=1
  GIT_CONFIG_GLOBAL=/dev/null
)
clean_git() {
  env "${clean_git_env[@]}" git "$@"
}

repo_root=$(clean_git rev-parse --show-toplevel)

for value in "$prerequisite" "$bundle_head" "$measured_commit" "$measured_tree"; do
  if [[ ! $value =~ ^[0-9a-f]{40}$ ]]; then
    echo "FG-037 source bundle FAIL: identities must be lowercase SHA-1 values" >&2
    exit 2
  fi
done

if [ ! -f "$bundle_input" ] || [ -L "$bundle_input" ] \
  || [ ! -f "$allowed_signers_input" ] || [ -L "$allowed_signers_input" ]; then
  echo "FG-037 source bundle FAIL: bundle and allowed-signers input must be real files" >&2
  exit 1
fi
bundle=$(realpath "$bundle_input")
allowed_signers=$(realpath "$allowed_signers_input")

if ! clean_git -C "$repo_root" merge-base --is-ancestor "$prerequisite" HEAD; then
  echo "FG-037 source bundle FAIL: prerequisite is not retained by current HEAD" >&2
  exit 1
fi

expected_ref=refs/heads/codex/fg-037-step-ceiling-publish
if [ "$(clean_git bundle list-heads "$bundle")" != "$bundle_head $expected_ref" ]; then
  echo "FG-037 source bundle FAIL: bundle exposes an unexpected head" >&2
  exit 1
fi

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
clean_git -C "$scratch" init -q

# Fetch only the prerequisite and its ancestors. The measured descendant must
# arrive from the bundle rather than leak from this checkout's object database.
clean_git -C "$scratch" fetch -q --no-tags "$repo_root" "$prerequisite"

# Neither descendant may be present before the bundle import. This assertion is
# independent of bundle verification and turns any future isolation regression
# into a refusal rather than an ambient-object false pass.
if clean_git -C "$scratch" cat-file -e "$measured_commit^{commit}" 2>/dev/null \
  || clean_git -C "$scratch" cat-file -e "$bundle_head^{commit}" 2>/dev/null; then
  echo "FG-037 source bundle FAIL: descendant object present before bundle import" >&2
  exit 1
fi

clean_git -C "$scratch" bundle verify "$bundle" >/dev/null
clean_git -C "$scratch" fetch -q --no-tags "$bundle" \
  "$expected_ref:refs/remotes/evidence/fg037-source"

actual_head=$(clean_git -C "$scratch" rev-parse refs/remotes/evidence/fg037-source)
if [ "$actual_head" != "$bundle_head" ]; then
  echo "FG-037 source bundle FAIL: imported head differs" >&2
  exit 1
fi

if ! clean_git -C "$scratch" cat-file -e "$measured_commit^{commit}"; then
  echo "FG-037 source bundle FAIL: measured commit is absent" >&2
  exit 1
fi

actual_tree=$(clean_git -C "$scratch" rev-parse "$measured_commit^{tree}")
if [ "$actual_tree" != "$measured_tree" ]; then
  echo "FG-037 source bundle FAIL: measured tree differs" >&2
  exit 1
fi

clean_git -C "$scratch" merge-base --is-ancestor "$prerequisite" "$measured_commit"
clean_git -C "$scratch" merge-base --is-ancestor "$measured_commit" "$bundle_head"
signature_config=(
  -c gpg.format=ssh
  -c gpg.ssh.allowedSignersFile="$allowed_signers"
)
clean_git -C "$scratch" "${signature_config[@]}" \
  verify-commit "$measured_commit" >/dev/null 2>&1

expected_signer=srikanth.remani@gmail.com
expected_fingerprint=SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY
actual_signer=$(clean_git -C "$scratch" "${signature_config[@]}" \
  show -s --format=%GS "$measured_commit")
actual_fingerprint=$(clean_git -C "$scratch" "${signature_config[@]}" \
  show -s --format=%GF "$measured_commit")
if [ "$actual_signer" != "$expected_signer" ] \
  || [ "$actual_fingerprint" != "$expected_fingerprint" ]; then
  echo "FG-037 source bundle FAIL: measured commit signer identity differs" >&2
  exit 1
fi

echo "FG-037 source bundle PASS: signed measured commit $measured_commit reconstructs as tree $measured_tree"
