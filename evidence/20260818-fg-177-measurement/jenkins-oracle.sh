#!/usr/bin/env bash
# Capture or verify the exact Jenkins controller that owns FG-177 evidence.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

: "${FOGELL_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"

evidence_root='evidence/20260818-fg-177-measurement'
action=${1:-}
metadata_dir=${2:-$evidence_root}
snapshot_destination=${3:-}

fail() {
  printf 'ERROR: Jenkins oracle verification refused: %s\n' "$*" >&2
  exit 1
}

require_regular_nonempty() {
  local path=$1
  local label=$2
  [[ -f "$path" && ! -L "$path" && -s "$path" ]] ||
    fail "$label must be a non-empty regular non-symlink file: $path"
}

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

case "$action" in
  capture|verify) ;;
  *)
    echo "usage: $0 {capture|verify} [metadata-directory]" >&2
    exit 2
    ;;
esac
if [[ "$action" == capture && -n "$snapshot_destination" ]]; then
  fail 'capture does not accept a verification snapshot destination'
fi

curl_options=(
  --disable
  --silent
  --show-error
  --globoff
  --connect-timeout 5
  --max-time 15
  --max-redirs 0
)
internal_curl_options=("${curl_options[@]}")
if [[ -n ${FOGELL_JENKINS_NETRC_FILE:-} ]]; then
  [[ -r "$FOGELL_JENKINS_NETRC_FILE" ]] ||
    fail "netrc file is unreadable: $FOGELL_JENKINS_NETRC_FILE"
  curl_options+=(--netrc-file "$FOGELL_JENKINS_NETRC_FILE" --netrc-optional)
fi

oracle_tmp=$(mktemp -d)
snapshot_stage=''
cleanup() {
  rm -rf "$oracle_tmp"
  if [[ -n "$snapshot_stage" && -e "$snapshot_stage" ]]; then
    rm -rf "$snapshot_stage"
  fi
}
trap cleanup EXIT

read_identity_headers() {
  local headers=$1
  local label=$2
  local core_values=()
  local session_values=()

  mapfile -t core_values < <(
    awk -F: '
      tolower($1) == "x-jenkins" {
        value = substr($0, index($0, ":") + 1)
        sub(/\r$/, "", value)
        sub(/^[[:space:]]+/, "", value)
        sub(/[[:space:]]+$/, "", value)
        print value
      }
    ' "$headers"
  )
  mapfile -t session_values < <(
    awk -F: '
      tolower($1) == "x-jenkins-session" {
        value = substr($0, index($0, ":") + 1)
        sub(/\r$/, "", value)
        sub(/^[[:space:]]+/, "", value)
        sub(/[[:space:]]+$/, "", value)
        print value
      }
    ' "$headers"
  )
  if [[ ${#core_values[@]} -ne 1 ||
        ! ${core_values[0]} =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    fail "$label did not return exactly one canonical X-Jenkins identity"
  fi
  if [[ ${#session_values[@]} -ne 1 ||
        ! ${session_values[0]} =~ ^[[:graph:]]+$ ]]; then
    fail "$label did not return exactly one canonical X-Jenkins-Session identity"
  fi
  LIVE_CORE=${core_values[0]}
  LIVE_SESSION=${session_values[0]}
}

fetch_external() {
  local label=$1
  local url=$2
  local headers=$3
  local body=$4
  local expected_core=$5
  local expected_session=$6
  local status rc

  set +e
  status=$(curl "${curl_options[@]}" -D "$headers" -o "$body" \
    --write-out '%{http_code}' "$url")
  rc=$?
  set -e
  if [[ $rc -ne 0 ]]; then
    fail "$label transport failed (curl rc=$rc)"
  fi
  if [[ "$status" != 200 ]]; then
    fail "$label returned HTTP $status; redirects and authentication failures are not evidence"
  fi
  read_identity_headers "$headers" "$label"
  if [[ -n "$expected_core" && "$LIVE_CORE" != "$expected_core" ]]; then
    fail "$label Jenkins identity differs from the inspected controller"
  fi
  if [[ -n "$expected_session" && "$LIVE_SESSION" != "$expected_session" ]]; then
    fail "$label Jenkins session differs from the inspected controller"
  fi
}

fetch_internal() {
  local label=$1
  local container_id=$2
  local url=$3
  local headers=$4
  local body=$5
  local expected_core=$6
  local expected_session=$7
  local response="$oracle_tmp/internal-response"
  local command rc status_line

  printf -v command '%q ' \
    podman exec "$container_id" curl "${internal_curl_options[@]}" \
    -D - -o - "$url"
  set +e
  # command is assembled with printf %q above as one shell-escaped remote command.
  # shellcheck disable=SC2029
  ssh "$FOGELL_JENKINS_HOST" "$command" > "$response"
  rc=$?
  set -e
  if [[ $rc -ne 0 ]]; then
    fail "$label transport failed (ssh rc=$rc)"
  fi
  if ! awk -v headers="$headers" -v body="$body" '
      BEGIN { in_headers = 1; separated = 0 }
      in_headers {
        print > headers
        if ($0 == "\r" || $0 == "") {
          in_headers = 0
          separated = 1
        }
        next
      }
      { print > body }
      END { if (!separated) exit 1 }
    ' "$response"; then
    fail "$label returned a malformed HTTP response"
  fi
  IFS= read -r status_line < "$headers"
  status_line=${status_line%$'\r'}
  if [[ ! "$status_line" =~ ^HTTP/[0-9]+\.[0-9]+[[:space:]]+200([[:space:]]|$) ]]; then
    fail "$label did not return HTTP 200"
  fi
  read_identity_headers "$headers" "$label"
  if [[ -n "$expected_core" && "$LIVE_CORE" != "$expected_core" ]]; then
    fail "$label Jenkins identity differs from the inspected controller"
  fi
  if [[ -n "$expected_session" && "$LIVE_SESSION" != "$expected_session" ]]; then
    fail "$label Jenkins session differs from the inspected controller"
  fi
}

render_plugins() {
  local body=$1
  local destination=$2

  if ! jq -er '
      if (.plugins | type) != "array" or (.plugins | length) == 0 then
        error("plugins must be a non-empty array")
      else .plugins end
      | map(
          if (.shortName | type) != "string" or (.shortName | length) == 0 or
             (.shortName | test("[\\t\\r\\n]")) or
             (.version | type) != "string" or (.version | length) == 0 or
             (.version | test("[\\t\\r\\n]")) or
             (.active | type) != "boolean" or
             (.enabled | type) != "boolean"
          then error("invalid plugin record")
          else . end
        )
      | if (map(.shortName) | unique | length) != length
        then error("duplicate plugin shortName") else . end
      | sort_by(.shortName)
      | .[]
      | [.shortName, .version, (.active | tostring), (.enabled | tostring)]
      | @tsv
    ' "$body" > "$destination"; then
    fail 'plugin API returned malformed, empty, duplicate, or incomplete metadata'
  fi
}

read_container() {
  local container_ref=$1
  local destination=$2
  local container_line inspect_command rc

  printf -v inspect_command '%q ' \
    podman inspect "$container_ref" \
    --format '{{.Id}}|{{.ImageName}}|{{.Image}}|{{.ImageDigest}}'

  set +e
  # inspect_command is assembled with printf %q above; expansion here is the
  # intended, shell-escaped single remote command rather than local execution.
  # shellcheck disable=SC2029
  container_line=$(ssh "$FOGELL_JENKINS_HOST" "$inspect_command")
  rc=$?
  set -e
  if [[ $rc -ne 0 ]]; then
    fail "controller inspection failed (ssh rc=$rc)"
  fi
  if [[ "$container_line" == *$'\n'* ||
        ! "$container_line" =~ ^[0-9a-f]{64}\|[^\|]+\|[0-9a-f]{64}\|sha256:[0-9a-f]{64}$ ]]; then
    fail 'controller inspection returned malformed or multiple records'
  fi
  IFS='|' read -r LIVE_CONTAINER_ID LIVE_IMAGE_NAME LIVE_IMAGE_ID LIVE_IMAGE_DIGEST \
    <<< "$container_line"
  if [[ -n "$destination" ]]; then
    printf '%s|%s|%s\n' "$LIVE_IMAGE_NAME" "$LIVE_IMAGE_ID" "$LIVE_IMAGE_DIGEST" \
      > "$destination"
  fi
}

external_base_url=${FOGELL_JENKINS_URL%/}
internal_base_url='http://127.0.0.1:8080'
external_root_headers="$oracle_tmp/external-root.headers"
external_root_body="$oracle_tmp/external-root.body"
external_plugin_headers="$oracle_tmp/external-plugins.headers"
external_plugin_body="$oracle_tmp/external-plugins.json"
internal_root_headers="$oracle_tmp/internal-root.headers"
internal_root_body="$oracle_tmp/internal-root.body"
internal_plugin_headers="$oracle_tmp/internal-plugins.headers"
internal_plugin_body="$oracle_tmp/internal-plugins.json"
live_plugins="$oracle_tmp/jenkins-plugins.tsv"
internal_plugins="$oracle_tmp/internal-jenkins-plugins.tsv"
live_image="$oracle_tmp/jenkins-controller-image.txt"
live_image_after="$oracle_tmp/jenkins-controller-image-after.txt"

if [[ "$action" == verify ]]; then
  core_file="$metadata_dir/jenkins-core.txt"
  plugin_file="$metadata_dir/jenkins-plugins.tsv"
  image_file="$metadata_dir/jenkins-controller-image.txt"
  require_regular_nonempty "$core_file" 'pinned core metadata'
  require_regular_nonempty "$plugin_file" 'pinned plugin manifest'
  require_regular_nonempty "$image_file" 'pinned image metadata'
  pinned_core_file="$oracle_tmp/pinned-jenkins-core.txt"
  pinned_plugin_file="$oracle_tmp/pinned-jenkins-plugins.tsv"
  pinned_image_file="$oracle_tmp/pinned-jenkins-controller-image.txt"
  cp -- "$core_file" "$pinned_core_file"
  cp -- "$plugin_file" "$pinned_plugin_file"
  cp -- "$image_file" "$pinned_image_file"
  mapfile -t pinned_core_lines < "$pinned_core_file"
  if [[ ${#pinned_core_lines[@]} -ne 1 ||
        ! ${pinned_core_lines[0]} =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    fail 'pinned core file is not one canonical version line'
  fi
  pinned_core=${pinned_core_lines[0]}
  if [[ -n ${FOGELL_JENKINS_CORE:-} && "$FOGELL_JENKINS_CORE" != "$pinned_core" ]]; then
    fail "runner requests Jenkins $FOGELL_JENKINS_CORE, metadata pins $pinned_core"
  fi
else
  pinned_core=''
fi

read_container "$FOGELL_JENKINS_CONTAINER" "$live_image"
controller_container_id=$LIVE_CONTAINER_ID
controller_image_name=$LIVE_IMAGE_NAME
controller_image_id=$LIVE_IMAGE_ID
controller_image_digest=$LIVE_IMAGE_DIGEST

fetch_internal 'inspected controller root' "$controller_container_id" \
  "$internal_base_url/" "$internal_root_headers" "$internal_root_body" \
  "$pinned_core" ''
controller_core=$LIVE_CORE
controller_session=$LIVE_SESSION
fetch_internal 'inspected controller plugin API' "$controller_container_id" \
  "$internal_base_url/pluginManager/api/json?tree=plugins%5BshortName,version,active,enabled%5D" \
  "$internal_plugin_headers" "$internal_plugin_body" \
  "$controller_core" "$controller_session"
render_plugins "$internal_plugin_body" "$internal_plugins"

fetch_external 'external controller root' "$external_base_url/" \
  "$external_root_headers" "$external_root_body" \
  "$controller_core" "$controller_session"
fetch_external 'external plugin API' \
  "$external_base_url/pluginManager/api/json?tree=plugins%5BshortName,version,active,enabled%5D" \
  "$external_plugin_headers" "$external_plugin_body" \
  "$controller_core" "$controller_session"
render_plugins "$external_plugin_body" "$live_plugins"
if ! cmp -s "$internal_plugins" "$live_plugins"; then
  fail 'external plugin surface differs from the inspected controller'
fi

read_container "$FOGELL_JENKINS_CONTAINER" "$live_image_after"
if [[ "$LIVE_CONTAINER_ID" != "$controller_container_id" ||
      "$LIVE_IMAGE_NAME" != "$controller_image_name" ||
      "$LIVE_IMAGE_ID" != "$controller_image_id" ||
      "$LIVE_IMAGE_DIGEST" != "$controller_image_digest" ]] ||
    ! cmp -s "$live_image" "$live_image_after"; then
  fail 'controller container or image changed during oracle verification'
fi
session_sha256=$(printf '%s' "$controller_session" | sha256sum | awk '{print $1}')

if [[ "$action" == capture ]]; then
  mkdir -p "$metadata_dir"
  printf '%s\n' "$controller_core" > "$oracle_tmp/jenkins-core.txt"
  mv "$oracle_tmp/jenkins-core.txt" "$metadata_dir/jenkins-core.txt"
  mv "$live_plugins" "$metadata_dir/jenkins-plugins.tsv"
  mv "$live_image" "$metadata_dir/jenkins-controller-image.txt"
  printf 'captured Jenkins %s, %s plugins, image %s\n' \
    "$controller_core" "$(wc -l < "$metadata_dir/jenkins-plugins.tsv")" \
    "$(cut -d '|' -f 3 "$metadata_dir/jenkins-controller-image.txt")"
  exit 0
fi

if ! cmp -s "$pinned_plugin_file" "$live_plugins"; then
  fail "live plugin manifest differs from $plugin_file"
fi
if ! cmp -s "$pinned_image_file" "$live_image"; then
  fail "live controller image differs from $image_file"
fi

IFS='|' read -r pinned_image_name pinned_image_id pinned_image_digest < "$pinned_image_file"
if [[ ! "$pinned_image_name" =~ ^[[:graph:]]+$ ]]; then
  fail 'pinned image name is not canonical printable text'
fi

if [[ -n "$snapshot_destination" ]]; then
  snapshot_parent=$(dirname "$snapshot_destination")
  snapshot_name=$(basename "$snapshot_destination")
  [[ "$snapshot_name" != . && "$snapshot_name" != .. ]] ||
    fail 'verification snapshot destination has an unsafe basename'
  [[ -d "$snapshot_parent" && ! -L "$snapshot_parent" ]] ||
    fail "verification snapshot parent must be a real directory: $snapshot_parent"
  [[ ! -e "$snapshot_destination" && ! -L "$snapshot_destination" ]] ||
    fail "verification snapshot destination already exists: $snapshot_destination"
  snapshot_stage=$(mktemp -d "$snapshot_parent/.${snapshot_name}-stage.XXXXXX")
  cp -- "$pinned_core_file" "$snapshot_stage/jenkins-core.txt"
  cp -- "$pinned_plugin_file" "$snapshot_stage/jenkins-plugins.tsv"
  cp -- "$pinned_image_file" "$snapshot_stage/jenkins-controller-image.txt"
  chmod 0444 "$snapshot_stage"/*
  mv -- "$snapshot_stage" "$snapshot_destination"
  snapshot_stage=''
fi

printf 'format\tfogell-jenkins-oracle-v2\n'
printf 'jenkins-core\t%s\n' "$pinned_core"
printf 'jenkins-session-sha256\t%s\n' "$session_sha256"
printf 'controller-container-id\t%s\n' "$controller_container_id"
printf 'core-metadata-sha256\t%s\n' "$(sha256_file "$pinned_core_file")"
printf 'plugin-count\t%s\n' "$(wc -l < "$pinned_plugin_file")"
printf 'plugin-manifest-sha256\t%s\n' "$(sha256_file "$pinned_plugin_file")"
printf 'controller-image-name\t%s\n' "$pinned_image_name"
printf 'controller-image-id\t%s\n' "$pinned_image_id"
printf 'controller-image-digest\t%s\n' "$pinned_image_digest"
printf 'image-metadata-sha256\t%s\n' "$(sha256_file "$pinned_image_file")"
