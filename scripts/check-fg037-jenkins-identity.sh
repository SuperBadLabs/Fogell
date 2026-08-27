#!/usr/bin/env bash
# FG-037. Fail closed unless the build URL and selected container are one pinned
# Jenkins controller. Observations are exact CORE<TAB>SESSION header records.
set -euo pipefail

if [ "$#" -lt 4 ] || [ "$#" -gt 5 ]; then
  echo "usage: $0 <expected-core> <war-core> <endpoint-observation> <container-observation> [prior-session]" >&2
  exit 2
fi

expected_core=$1
war_core=$2
endpoint_observation=$3
container_observation=$4
prior_session=${5-}

parse_observation() {
  local label=$1
  local observation=$2
  local core
  local session

  if [[ "$observation" == *$'\n'* || "$observation" == *$'\r'* \
    || "$observation" != *$'\t'* ]]; then
    echo "REFUSED: $label Jenkins identity is not one CORE<TAB>SESSION record" >&2
    return 2
  fi
  core=${observation%%$'\t'*}
  session=${observation#*$'\t'}
  if [ -z "$core" ] || [ -z "$session" ] || [[ "$session" == *$'\t'* ]]; then
    echo "REFUSED: $label Jenkins identity is incomplete or ambiguous" >&2
    return 2
  fi
  printf '%s\t%s\n' "$core" "$session"
}

endpoint_identity=$(parse_observation endpoint "$endpoint_observation") || exit $?
container_identity=$(parse_observation container "$container_observation") || exit $?
endpoint_core=${endpoint_identity%%$'\t'*}
endpoint_session=${endpoint_identity#*$'\t'}
container_core=${container_identity%%$'\t'*}
container_session=${container_identity#*$'\t'}

if [ "$war_core" != "$expected_core" ] \
  || [ "$endpoint_core" != "$expected_core" ] \
  || [ "$container_core" != "$expected_core" ]; then
  echo "REFUSED: Jenkins artifact, endpoint, and container HTTP core must all be $expected_core" >&2
  exit 2
fi
if [ "$endpoint_session" != "$container_session" ]; then
  echo "REFUSED: build endpoint is not the SSH-selected Jenkins container session" >&2
  exit 2
fi
if [ -n "$prior_session" ] && [ "$endpoint_session" != "$prior_session" ]; then
  echo "REFUSED: Jenkins controller session changed while the live probe ran" >&2
  exit 2
fi

printf '%s\n' "$endpoint_session"
