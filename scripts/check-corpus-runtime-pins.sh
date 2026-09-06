#!/usr/bin/env bash
# Verify the exact command resolution, tool bytes and Jenkins image selected by
# an allowlisted corpus row. The corpus runner calls this under its cross-host
# lease before execution and again before receipt promotion.
set -Eeuo pipefail
cd "$(dirname "$0")/.."

die() { printf 'corpus runtime pin: REFUSED: %s\n' "$*" >&2; exit 2; }
[ "$#" -gt 0 ] || die "name at least one runtime pin"

: "${FOGELL_CORPUS_RUNTIME_PINS:=differential/corpus-runtime-pins.tsv}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"
[ -f "$FOGELL_CORPUS_RUNTIME_PINS" ] || die "$FOGELL_CORPUS_RUNTIME_PINS is missing"

# This is the fixed build PATH in ProcessGroup.fs. Resolving under the same
# value catches a later /usr/local/bin tool shadowing the pinned /usr/bin byte.
build_path=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# shellcheck source=scripts/jenkins-workspace-v2.sh disable=SC1091
source scripts/jenkins-workspace-v2.sh || die "Jenkins command quoting helpers could not be loaded"

declare -A tool_name=() local_tool=() jenkins_tool=() tool_sha=() image_id=() image_digest=()
while IFS=$'\t' read -r pin command local_path jenkins_path sha expected_image expected_digest extra; do
  [ -n "$pin" ] || continue
  case "$pin" in \#*) continue ;; esac
  [ -z "${extra:-}" ] || die "pin '$pin' has more than seven tab-separated fields"
  [[ "$pin" =~ ^[a-z0-9][a-z0-9._-]*$ ]] || die "invalid pin id '$pin'"
  [ -z "${tool_name[$pin]+x}" ] || die "duplicate pin id '$pin'"
  [[ "$command" =~ ^[A-Za-z0-9._+-]+$ ]] || die "pin '$pin' has an unsafe tool name"
  [[ "$local_path" =~ ^/[A-Za-z0-9._/+:-]+$ ]] || die "pin '$pin' has an unsafe local tool path"
  [[ "$jenkins_path" =~ ^/[A-Za-z0-9._/+:-]+$ ]] || die "pin '$pin' has an unsafe Jenkins tool path"
  [[ "$sha" =~ ^[0-9a-f]{64}$ ]] || die "pin '$pin' has an invalid tool SHA-256"
  [[ "$expected_image" =~ ^[0-9a-f]{64}$ ]] || die "pin '$pin' has an invalid Jenkins image ID"
  [[ "$expected_digest" =~ ^sha256:[0-9a-f]{64}$ ]] || die "pin '$pin' has an invalid Jenkins image digest"
  tool_name[$pin]=$command
  local_tool[$pin]=$local_path
  jenkins_tool[$pin]=$jenkins_path
  tool_sha[$pin]=$sha
  image_id[$pin]=$expected_image
  image_digest[$pin]=$expected_digest
done < "$FOGELL_CORPUS_RUNTIME_PINS"

declare -A requested=()
for pin in "$@"; do
  [ -z "${requested[$pin]+x}" ] || continue
  requested[$pin]=1
  [ -n "${tool_name[$pin]+x}" ] || die "pin '$pin' is not defined in $FOGELL_CORPUS_RUNTIME_PINS"

  observed_local_path=$(PATH="$build_path" command -v "${tool_name[$pin]}" 2>/dev/null || true)
  [ "$observed_local_path" = "${local_tool[$pin]}" ] \
    || die "pin '$pin' local command resolves to ${observed_local_path:-nothing}, expected ${local_tool[$pin]}"
  if ! observed_local=$(sha256sum -- "$observed_local_path" 2>/dev/null); then
    die "pin '$pin' could not hash local tool $observed_local_path"
  fi
  observed_local=${observed_local%% *}
  [ "$observed_local" = "${tool_sha[$pin]}" ] \
    || die "pin '$pin' local tool SHA-256 is $observed_local, expected ${tool_sha[$pin]}"

  container_q=$(fogell_quote_posix_shell_v2 "$FOGELL_JENKINS_CONTAINER") \
    || die "pin '$pin' could not quote the Jenkins container"
  command_q=$(fogell_quote_posix_shell_v2 "${tool_name[$pin]}") \
    || die "pin '$pin' could not quote the tool name"
  # The single quotes deliberately preserve $1 for the remote `sh -c`.
  # shellcheck disable=SC2016
  resolve_q=$(fogell_quote_posix_shell_v2 'command -v "$1"') \
    || die "pin '$pin' could not quote the resolution probe"
  if ! observed_remote_path=$(ssh -o BatchMode=yes -- "$FOGELL_JENKINS_HOST" \
      "podman exec --user 1000 $container_q sh -c $resolve_q sh $command_q" 2>/dev/null); then
    die "pin '$pin' could not resolve Jenkins tool ${tool_name[$pin]}"
  fi
  [ "$observed_remote_path" = "${jenkins_tool[$pin]}" ] \
    || die "pin '$pin' Jenkins command resolves to ${observed_remote_path:-nothing}, expected ${jenkins_tool[$pin]}"
  tool_q=$(fogell_quote_posix_shell_v2 "$observed_remote_path") \
    || die "pin '$pin' could not quote the Jenkins tool path"
  if ! observed_remote=$(ssh -o BatchMode=yes -- "$FOGELL_JENKINS_HOST" \
      "podman exec --user 1000 $container_q sha256sum -- $tool_q" 2>/dev/null); then
    die "pin '$pin' could not hash Jenkins tool $observed_remote_path"
  fi
  observed_remote=${observed_remote%% *}
  [ "$observed_remote" = "${tool_sha[$pin]}" ] \
    || die "pin '$pin' Jenkins tool SHA-256 is $observed_remote, expected ${tool_sha[$pin]}"

  if ! observed_image=$(fogell_jenkins_podman_inspect_v2 \
      "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER" '{{.Image}}' 2>/dev/null); then
    die "pin '$pin' could not inspect the Jenkins container image"
  fi
  [ "$observed_image" = "${image_id[$pin]}" ] \
    || die "pin '$pin' Jenkins image ID is $observed_image, expected ${image_id[$pin]}"

  image_q=$(fogell_quote_posix_shell_v2 "$observed_image") \
    || die "pin '$pin' could not quote the Jenkins image ID"
  if ! observed_digest=$(ssh -o BatchMode=yes -- "$FOGELL_JENKINS_HOST" \
      "podman image inspect --format '{{.Digest}}' $image_q" 2>/dev/null); then
    die "pin '$pin' could not inspect Jenkins image digest for $observed_image"
  fi
  [ "$observed_digest" = "${image_digest[$pin]}" ] \
    || die "pin '$pin' Jenkins image digest is $observed_digest, expected ${image_digest[$pin]}"

  printf 'corpus runtime pin: %s verified (tool %s; image %s; digest %s)\n' \
    "$pin" "${tool_sha[$pin]}" "${image_id[$pin]}" "${image_digest[$pin]}"
done
