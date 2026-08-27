#!/usr/bin/env bash
# Shared strict Jenkins workspace collector for differential probes.
#
# Callers source this file, then pass the SSH host and container name to
# `fogell_configure_jenkins_workspace_v2`. Keeping the framed collector and its
# matching wipe in one place prevents a focused probe from silently falling
# back to PROVEN-PARTIAL when the canonical manifest protocol evolves.

fogell_configure_jenkins_workspace_v2() {
  if [ "$#" -ne 2 ]; then
    echo "usage: fogell_configure_jenkins_workspace_v2 <host> <container>" >&2
    return 2
  fi

  local jenkins_host=$1
  local jenkins_container=$2
  local jenkins_host_q
  local jenkins_container_q
  local collector_source
  local collector_b64

  printf -v jenkins_host_q '%q' "$jenkins_host" || return 2
  printf -v jenkins_container_q '%q' "$jenkins_container" || return 2

  read -r -d '' collector_source <<'FOGELL_WORKSPACE_COLLECTOR' || true
set -uo pipefail
workspace=$1
cd "$workspace" 2>/dev/null || exit 3
export LC_ALL=C
printf 'FOGELL-WORKSPACE-MANIFEST\t2\n'
count=0
inventory=$(mktemp) || exit 4
trap 'rm -f "$inventory"' EXIT
if ! find -P . -mindepth 1 \( -type f -o \( -type d -empty \) \) -print0 | sort -z >"$inventory"; then
  exit 5
fi
while IFS= read -r -d '' entry; do
  relative=${entry#./}
  encoded=$(printf '%s' "$relative" | base64 -w 0) || exit 6
  if [[ -f "$entry" && ! -L "$entry" ]]; then
    hash=$(sha256sum -- "$entry" | cut -d ' ' -f 1) || exit 7
    printf 'F\t%s\t%s\n' "$hash" "$encoded"
  elif [[ -d "$entry" && ! -L "$entry" ]]; then
    printf 'D\t%s\n' "$encoded"
  else
    exit 8
  fi
  count=$((count + 1))
done <"$inventory"
printf 'END\t%s\n' "$count"
FOGELL_WORKSPACE_COLLECTOR

  collector_b64=$(printf '%s' "$collector_source" | base64 -w 0) || {
    echo "REFUSED: shared Jenkins workspace collector could not be encoded" >&2
    return 2
  }
  export FOGELL_JENKINS_WORKSPACE_CMD="ssh ${jenkins_host_q} \"podman exec ${jenkins_container_q} bash -c \\\"printf %s ${collector_b64} | base64 -d | bash -s -- '/var/jenkins_home/workspace/{job}'\\\"\""

  # A compile-time refusal may never allocate a workspace. Precreate the empty
  # root so protocol v2 observes a real empty tree instead of treating a failed
  # `cd` as evidence.
  export FOGELL_JENKINS_WIPE_CMD="ssh ${jenkins_host_q} \"podman exec ${jenkins_container_q} sh -c \\\"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp && mkdir -p /var/jenkins_home/workspace/{job}\\\"\""
}
