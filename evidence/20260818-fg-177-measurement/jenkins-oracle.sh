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

fail() {
  printf 'ERROR: Jenkins oracle verification refused: %s\n' "$*" >&2
  exit 1
}

case "$action" in
  capture|verify) ;;
  *)
    echo "usage: $0 {capture|verify} [metadata-directory]" >&2
    exit 2
    ;;
esac

curl_options=(
  --disable
  --silent
  --show-error
  --globoff
  --connect-timeout 5
  --max-time 15
  --max-redirs 0
)
if [[ -n ${FOGELL_JENKINS_NETRC_FILE:-} ]]; then
  [[ -r "$FOGELL_JENKINS_NETRC_FILE" ]] ||
    fail "netrc file is unreadable: $FOGELL_JENKINS_NETRC_FILE"
  curl_options+=(--netrc-file "$FOGELL_JENKINS_NETRC_FILE" --netrc-optional)
fi

oracle_tmp=$(mktemp -d)
trap 'rm -rf "$oracle_tmp"' EXIT

read_core_header() {
  local headers=$1
  local label=$2
  local values=()

  mapfile -t values < <(
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
  if [[ ${#values[@]} -ne 1 || -z ${values[0]} ]]; then
    fail "$label returned ${#values[@]} non-empty X-Jenkins value(s); expected exactly one"
  fi
  LIVE_CORE=${values[0]}
}

fetch() {
  local label=$1
  local url=$2
  local headers=$3
  local body=$4
  local expected_core=$5
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
  read_core_header "$headers" "$label"
  if [[ -n "$expected_core" && "$LIVE_CORE" != "$expected_core" ]]; then
    fail "$label reports Jenkins $LIVE_CORE, pinned oracle is $expected_core"
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

read_image() {
  local destination=$1
  local image_line inspect_command rc

  printf -v inspect_command '%q ' \
    podman inspect "$FOGELL_JENKINS_CONTAINER" \
    --format '{{.ImageName}}|{{.Image}}|{{.ImageDigest}}'

  set +e
  # inspect_command is assembled with printf %q above; expansion here is the
  # intended, shell-escaped single remote command rather than local execution.
  # shellcheck disable=SC2029
  image_line=$(ssh "$FOGELL_JENKINS_HOST" "$inspect_command")
  rc=$?
  set -e
  if [[ $rc -ne 0 ]]; then
    fail "controller image inspection failed (ssh rc=$rc)"
  fi
  if [[ "$image_line" == *$'\n'* ||
        ! "$image_line" =~ ^[^\|]+\|[0-9a-f]{64}\|sha256:[0-9a-f]{64}$ ]]; then
    fail 'controller image inspection returned malformed or multiple records'
  fi
  printf '%s\n' "$image_line" > "$destination"
}

base_url=${FOGELL_JENKINS_URL%/}
root_headers="$oracle_tmp/root.headers"
root_body="$oracle_tmp/root.body"
plugin_headers="$oracle_tmp/plugins.headers"
plugin_body="$oracle_tmp/plugins.json"
live_plugins="$oracle_tmp/jenkins-plugins.tsv"
live_image="$oracle_tmp/jenkins-controller-image.txt"

if [[ "$action" == verify ]]; then
  core_file="$metadata_dir/jenkins-core.txt"
  plugin_file="$metadata_dir/jenkins-plugins.tsv"
  image_file="$metadata_dir/jenkins-controller-image.txt"
  [[ -f "$core_file" ]] || fail "missing pinned core file: $core_file"
  [[ -f "$plugin_file" ]] || fail "missing pinned plugin manifest: $plugin_file"
  [[ -f "$image_file" ]] || fail "missing pinned image metadata: $image_file"
  mapfile -t pinned_core_lines < "$core_file"
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

fetch 'controller root' "$base_url/" "$root_headers" "$root_body" "$pinned_core"
root_core=$LIVE_CORE
fetch 'plugin API' \
  "$base_url/pluginManager/api/json?tree=plugins%5BshortName,version,active,enabled%5D" \
  "$plugin_headers" "$plugin_body" "$root_core"
render_plugins "$plugin_body" "$live_plugins"
read_image "$live_image"

if [[ "$action" == capture ]]; then
  mkdir -p "$metadata_dir"
  printf '%s\n' "$root_core" > "$oracle_tmp/jenkins-core.txt"
  mv "$oracle_tmp/jenkins-core.txt" "$metadata_dir/jenkins-core.txt"
  mv "$live_plugins" "$metadata_dir/jenkins-plugins.tsv"
  mv "$live_image" "$metadata_dir/jenkins-controller-image.txt"
  printf 'captured Jenkins %s, %s plugins, image %s\n' \
    "$root_core" "$(wc -l < "$metadata_dir/jenkins-plugins.tsv")" \
    "$(cut -d '|' -f 3 "$metadata_dir/jenkins-controller-image.txt")"
  exit 0
fi

if ! cmp -s "$plugin_file" "$live_plugins"; then
  fail "live plugin manifest differs from $plugin_file"
fi
if ! cmp -s "$image_file" "$live_image"; then
  fail "live controller image differs from $image_file"
fi

printf 'Jenkins oracle verified: core %s, %s plugins, image %s\n' \
  "$pinned_core" "$(wc -l < "$plugin_file")" "$(cut -d '|' -f 3 "$image_file")"
