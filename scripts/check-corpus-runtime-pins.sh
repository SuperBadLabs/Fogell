#!/usr/bin/env bash
# Verify the exact command resolution (present at pinned bytes, or absent),
# Jenkins image, and published endpoint selected by an allowlisted corpus row.
# The corpus runner calls this under its cross-host lease before execution and
# again before receipt promotion.
set -Eeuo pipefail
cd "$(dirname "$0")/.."

die() { printf 'corpus runtime pin: REFUSED: %s\n' "$*" >&2; exit 2; }
[ "$#" -gt 1 ] || die "usage: check-corpus-runtime-pins.sh <pins.tsv> <pin-id>..."

pins_file=$1
shift
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"
: "${FOGELL_JENKINS_URL:=http://luigi:18083}"
[ -f "$pins_file" ] || die "$pins_file is missing"

# This is the fixed build PATH in ProcessGroup.fs. Resolving under the same
# value catches a later /usr/local/bin tool shadowing the pinned /usr/bin byte.
build_path=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# shellcheck source=scripts/jenkins-workspace-v2.sh disable=SC1091
source scripts/jenkins-workspace-v2.sh || die "Jenkins command quoting helpers could not be loaded"

declare -A tool_name=() expectation=() local_tool=() jenkins_tool=() tool_sha=() image_id=() image_digest=() container_port=() host_binding=() jenkins_node=() plugin_count=() plugin_sha=()
while IFS=$'\t' read -r pin command expected_resolution local_path jenkins_path sha expected_image expected_digest expected_container_port expected_host_binding expected_node expected_plugin_count expected_plugin_sha extra; do
  [ -n "$pin" ] || continue
  case "$pin" in \#*) continue ;; esac
  [ -z "${extra:-}" ] || die "pin '$pin' has more than thirteen tab-separated fields"
  [[ "$pin" =~ ^[a-z0-9][a-z0-9._-]*$ ]] || die "invalid pin id '$pin'"
  [ -z "${tool_name[$pin]+x}" ] || die "duplicate pin id '$pin'"
  [[ "$command" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] || die "pin '$pin' has an unsafe tool name"
  case "$expected_resolution" in
    present)
      [[ "$local_path" =~ ^/[A-Za-z0-9._/+:-]+$ ]] || die "pin '$pin' has an unsafe local tool path"
      [[ "$jenkins_path" =~ ^/[A-Za-z0-9._/+:-]+$ ]] || die "pin '$pin' has an unsafe Jenkins tool path"
      [[ "$sha" =~ ^[0-9a-f]{64}$ ]] || die "pin '$pin' has an invalid tool SHA-256"
      ;;
    absent)
      if [ "$local_path" != - ] || [ "$jenkins_path" != - ] || [ "$sha" != - ]; then
        die "pin '$pin' absent expectation requires '-' for both tool paths and SHA-256"
      fi
      ;;
    *) die "pin '$pin' has invalid expectation '$expected_resolution' (expected present or absent)" ;;
  esac
  [[ "$expected_image" =~ ^[0-9a-f]{64}$ ]] || die "pin '$pin' has an invalid Jenkins image ID"
  [[ "$expected_digest" =~ ^sha256:[0-9a-f]{64}$ ]] || die "pin '$pin' has an invalid Jenkins image digest"
  [[ "$expected_container_port" =~ ^[1-9][0-9]{0,4}/tcp$ ]] \
    || die "pin '$pin' has an invalid Jenkins container port"
  port_number=${expected_container_port%/tcp}
  [ "$port_number" -le 65535 ] || die "pin '$pin' has an invalid Jenkins container port"
  [[ "$expected_host_binding" =~ ^(0\.0\.0\.0|\[::\]):[1-9][0-9]{0,4}$ ]] \
    || die "pin '$pin' has an invalid Jenkins host binding"
  [[ "$expected_node" =~ ^[A-Za-z0-9._-]+$ ]] \
    || die "pin '$pin' has an invalid Jenkins node"
  [[ "$expected_plugin_count" =~ ^[1-9][0-9]*$ ]] \
    || die "pin '$pin' has an invalid Jenkins plugin count"
  [[ "$expected_plugin_sha" =~ ^[0-9a-f]{64}$ ]] \
    || die "pin '$pin' has an invalid Jenkins plugin digest"
  binding_port=${expected_host_binding##*:}
  [ "$binding_port" -le 65535 ] || die "pin '$pin' has an invalid Jenkins host binding"
  tool_name[$pin]=$command
  expectation[$pin]=$expected_resolution
  local_tool[$pin]=$local_path
  jenkins_tool[$pin]=$jenkins_path
  tool_sha[$pin]=$sha
  image_id[$pin]=$expected_image
  image_digest[$pin]=$expected_digest
  container_port[$pin]=$expected_container_port
  host_binding[$pin]=$expected_host_binding
  jenkins_node[$pin]=$expected_node
  plugin_count[$pin]=$expected_plugin_count
  plugin_sha[$pin]=$expected_plugin_sha
done < "$pins_file"

declare -A requested=()
for pin in "$@"; do
  [ -z "${requested[$pin]+x}" ] || continue
  requested[$pin]=1
  [ -n "${tool_name[$pin]+x}" ] || die "pin '$pin' is not defined in $pins_file"

  if observed_local_path=$(PATH="$build_path" command -v "${tool_name[$pin]}" 2>/dev/null); then
    local_resolution=present
  else
    observed_local_path=
    local_resolution=absent
  fi

  case "${expectation[$pin]}" in
    present)
      [ "$local_resolution" = present ] \
        || die "pin '$pin' local command resolves to nothing, expected ${local_tool[$pin]}"
      [ "$observed_local_path" = "${local_tool[$pin]}" ] \
        || die "pin '$pin' local command resolves to $observed_local_path, expected ${local_tool[$pin]}"
      if ! observed_local=$(sha256sum -- "$observed_local_path" 2>/dev/null); then
        die "pin '$pin' could not hash local tool $observed_local_path"
      fi
      observed_local=${observed_local%% *}
      [ "$observed_local" = "${tool_sha[$pin]}" ] \
        || die "pin '$pin' local tool SHA-256 is $observed_local, expected ${tool_sha[$pin]}"
      ;;
    absent)
      [ "$local_resolution" = absent ] \
        || die "pin '$pin' local command unexpectedly resolves to $observed_local_path under the fixed build PATH"
      ;;
  esac

  container_q=$(fogell_quote_posix_shell_v2 "$FOGELL_JENKINS_CONTAINER") \
    || die "pin '$pin' could not quote the Jenkins container"
  command_q=$(fogell_quote_posix_shell_v2 "${tool_name[$pin]}") \
    || die "pin '$pin' could not quote the tool name"
  path_q=$(fogell_quote_posix_shell_v2 "PATH=$build_path") \
    || die "pin '$pin' could not quote the fixed Jenkins build PATH"
  # The single quotes deliberately preserve $1 for the remote `sh -c`. The
  # exact marker distinguishes command absence from SSH, podman, or shell
  # failure: transport failure is nonzero, and any unexpected stdout refuses.
  # shellcheck disable=SC2016
  resolve_q=$(fogell_quote_posix_shell_v2 'if actual=$(command -v "$1" 2>/dev/null); then printf "present:%s\n" "$actual"; else printf "absent\n"; fi') \
    || die "pin '$pin' could not quote the resolution probe"
  if ! observed_remote_resolution=$(ssh -o BatchMode=yes -- "$FOGELL_JENKINS_HOST" \
      "podman exec --user 1000 --env $path_q $container_q /bin/sh -c $resolve_q sh $command_q" 2>/dev/null); then
    die "pin '$pin' could not inspect Jenkins command ${tool_name[$pin]}"
  fi
  case "${expectation[$pin]}" in
    present)
      [ "$observed_remote_resolution" = "present:${jenkins_tool[$pin]}" ] \
        || die "pin '$pin' Jenkins command resolution is ${observed_remote_resolution:-no output}, expected present:${jenkins_tool[$pin]}"
      tool_q=$(fogell_quote_posix_shell_v2 "${jenkins_tool[$pin]}") \
        || die "pin '$pin' could not quote the Jenkins tool path"
      if ! observed_remote=$(ssh -o BatchMode=yes -- "$FOGELL_JENKINS_HOST" \
          "podman exec --user 1000 $container_q sha256sum -- $tool_q" 2>/dev/null); then
        die "pin '$pin' could not hash Jenkins tool ${jenkins_tool[$pin]}"
      fi
      observed_remote=${observed_remote%% *}
      [ "$observed_remote" = "${tool_sha[$pin]}" ] \
        || die "pin '$pin' Jenkins tool SHA-256 is $observed_remote, expected ${tool_sha[$pin]}"
      ;;
    absent)
      [ "$observed_remote_resolution" = absent ] \
        || die "pin '$pin' Jenkins command unexpectedly resolves (${observed_remote_resolution:-no output}) under the fixed build PATH"
      ;;
  esac

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

  plugin_json=$(ssh -o BatchMode=yes -- "$FOGELL_JENKINS_HOST" \
    "curl --globoff -sS 'http://127.0.0.1:${host_binding[$pin]##*:}/pluginManager/api/json?tree=plugins[shortName,version,active,enabled]'" 2>/dev/null) \
    || die "pin '$pin' could not read the Jenkins plugin inventory"
  observed_plugin_count=$(printf '%s' "$plugin_json" | jq -r '.plugins | length')
  observed_plugin_sha=$(printf '%s' "$plugin_json" | jq -cS '.plugins | sort_by(.shortName)' | sha256sum | cut -d' ' -f1)
  [ "$observed_plugin_count" = "${plugin_count[$pin]}" ] \
    || die "pin '$pin' Jenkins plugin count is $observed_plugin_count, expected ${plugin_count[$pin]}"
  [ "$observed_plugin_sha" = "${plugin_sha[$pin]}" ] \
    || die "pin '$pin' Jenkins plugin digest is $observed_plugin_sha, expected ${plugin_sha[$pin]}"

  # The URL used by the differential must name the same SSH host whose
  # container was inspected, and its port must be the exact published binding
  # of that container's pinned Jenkins port. This prevents an independently
  # overridden URL from executing on an uninspected Jenkins service.
  if [[ "$FOGELL_JENKINS_URL" =~ ^http://([A-Za-z0-9._-]+):([1-9][0-9]{0,4})$ ]]; then
    url_host=${BASH_REMATCH[1]}
    url_port=${BASH_REMATCH[2]}
  else
    die "pin '$pin' Jenkins URL must be exactly http://<ssh-host>:<published-port>"
  fi
  [ "$url_port" -le 65535 ] \
    || die "pin '$pin' Jenkins URL has an invalid published port"
  [ "$url_host" = "$FOGELL_JENKINS_HOST" ] \
    || die "pin '$pin' Jenkins URL host '$url_host' does not match inspected SSH host '$FOGELL_JENKINS_HOST'"
  expected_url_port=${host_binding[$pin]##*:}
  [ "$url_port" = "$expected_url_port" ] \
    || die "pin '$pin' Jenkins URL port is $url_port, expected published port $expected_url_port"
  container_port_q=$(fogell_quote_posix_shell_v2 "${container_port[$pin]}") \
    || die "pin '$pin' could not quote the Jenkins container port"
  if ! observed_binding=$(ssh -o BatchMode=yes -- "$FOGELL_JENKINS_HOST" \
      "podman port $container_q $container_port_q" 2>/dev/null); then
    die "pin '$pin' could not inspect the Jenkins container port binding"
  fi
  [ "$observed_binding" = "${host_binding[$pin]}" ] \
    || die "pin '$pin' Jenkins port binding is ${observed_binding:-nothing}, expected ${host_binding[$pin]}"

  printf 'corpus runtime pin: %s verified (command %s %s; image %s; digest %s; plugins %s/%s; endpoint %s; node %s)\n' \
    "$pin" "${tool_name[$pin]}" "${expectation[$pin]}" "${image_id[$pin]}" "${image_digest[$pin]}" \
    "${plugin_count[$pin]}" "${plugin_sha[$pin]}" "$FOGELL_JENKINS_URL" "${jenkins_node[$pin]}"
done
