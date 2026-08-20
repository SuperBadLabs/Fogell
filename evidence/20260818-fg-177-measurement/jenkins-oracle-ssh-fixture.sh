#!/usr/bin/env bash
# Hermetic ssh/podman fixture for FG-177 Jenkins oracle proofs.
set -euo pipefail

if [[ ${FOGELL_STUB_SSH_RC:-0} -ne 0 ]]; then
  exit "$FOGELL_STUB_SSH_RC"
fi

remote_command=${*:2}
if [[ "$remote_command" == *'podman inspect'* ]]; then
  count=0
  if [[ -n ${FOGELL_STUB_INSPECT_STATE:-} && -f $FOGELL_STUB_INSPECT_STATE ]]; then
    count=$(<"$FOGELL_STUB_INSPECT_STATE")
  fi
  count=$((count + 1))
  if [[ -n ${FOGELL_STUB_INSPECT_STATE:-} ]]; then
    printf '%s\n' "$count" > "$FOGELL_STUB_INSPECT_STATE"
  fi
  container_id=${FOGELL_STUB_CONTAINER_ID:-$(printf '3%.0s' {1..64})}
  image=${FOGELL_STUB_IMAGE:?FOGELL_STUB_IMAGE is required}
  if [[ $count -gt 1 ]]; then
    container_id=${FOGELL_STUB_CONTAINER_ID_AFTER:-$container_id}
    image=${FOGELL_STUB_IMAGE_AFTER:-$image}
  fi
  printf '%s|%s\n' "$container_id" "$image"
  exit 0
fi

if [[ "$remote_command" != *'podman exec'* ]]; then
  echo 'fixture refused an unexpected remote command' >&2
  exit 97
fi

endpoint=ROOT
body=${FOGELL_STUB_INTERNAL_ROOT_BODY:-fixture}
if [[ "$remote_command" == *'pluginManager/api/json'* ]]; then
  endpoint=PLUGIN
  default_plugins='{"plugins":[{"shortName":"alpha","version":"1.2","active":true,"enabled":true},{"shortName":"beta","version":"3.4","active":false,"enabled":true}]}'
  body=${FOGELL_STUB_INTERNAL_PLUGIN_BODY:-$default_plugins}
fi

rc_name="FOGELL_STUB_INTERNAL_${endpoint}_RC"
status_name="FOGELL_STUB_INTERNAL_${endpoint}_STATUS"
core_name="FOGELL_STUB_INTERNAL_${endpoint}_CORE"
session_name="FOGELL_STUB_INTERNAL_${endpoint}_SESSION"
headers_name="FOGELL_STUB_INTERNAL_${endpoint}_HEADERS"
rc=${!rc_name:-${FOGELL_STUB_INTERNAL_RC:-0}}
[[ $rc -eq 0 ]] || exit "$rc"
status=${!status_name:-200}
core=${!core_name:-${FOGELL_STUB_INTERNAL_CORE:-2.568.1}}
session=${!session_name:-${FOGELL_STUB_INTERNAL_SESSION:-fixture-session-secret}}
headers=${!headers_name:-normal}

printf 'HTTP/1.1 %s Fixture\r\n' "$status"
case "$headers" in
  normal)
    printf 'x-jEnKiNs: %s\r\nX-JeNkInS-SeSsIoN: %s\r\n' "$core" "$session"
    ;;
  missing-core)
    printf 'X-Jenkins-Session: %s\r\n' "$session"
    ;;
  multiple-core)
    printf 'X-Jenkins: %s\r\nx-jenkins: %s\r\nX-Jenkins-Session: %s\r\n' \
      "$core" "$core" "$session"
    ;;
  missing-session)
    printf 'X-Jenkins: %s\r\n' "$core"
    ;;
  multiple-session)
    printf 'X-Jenkins: %s\r\nX-Jenkins-Session: %s\r\nx-jenkins-session: %s\r\n' \
      "$core" "$session" "$session"
    ;;
  malformed)
    printf 'not-a-header\r\n'
    ;;
  *)
    echo 'fixture received an unknown header plant' >&2
    exit 96
    ;;
esac
printf 'Content-Type: application/json\r\n\r\n%s\n' "$body"
