#!/usr/bin/env bash
# Shared strict Jenkins workspace collector for differential probes.
#
# Callers source this file, then pass the SSH host and container name to
# `fogell_configure_jenkins_workspace_v2`. Keeping the framed collector and its
# matching wipe in one place prevents a focused probe from silently falling
# back to PROVEN-PARTIAL when the canonical manifest protocol evolves.

# The differential CLI evaluates configured commands with local `/bin/sh -c`,
# then SSH gives its final argument to the remote shell. Quote each parse as a
# separate layer so an escape needed remotely is not consumed locally.
fogell_quote_posix_shell_v2() {
  if [ "$#" -ne 1 ]; then
    echo "usage: fogell_quote_posix_shell_v2 <value>" >&2
    return 2
  fi

  local escaped=${1//\'/\'\\\'\'}
  printf "'%s'" "$escaped"
}

fogell_jenkins_ssh_command_v2() {
  if [ "$#" -ne 2 ]; then
    echo "usage: fogell_jenkins_ssh_command_v2 <host> <remote-command>" >&2
    return 2
  fi

  local host_q
  local remote_command_q
  host_q=$(fogell_quote_posix_shell_v2 "$1") || return 2
  remote_command_q=$(fogell_quote_posix_shell_v2 "$2") || return 2
  printf 'ssh -- %s %s' "$host_q" "$remote_command_q"
}

fogell_configure_jenkins_workspace_v2() {
  if [ "$#" -ne 2 ]; then
    echo "usage: fogell_configure_jenkins_workspace_v2 <host> <container>" >&2
    return 2
  fi

  local jenkins_host=$1
  local jenkins_container=$2
  local jenkins_container_remote_q
  local collector_source
  local collector_b64
  local collector_script
  local collector_script_remote_q
  local workspace_remote_command
  local wipe_script
  local wipe_script_remote_q
  local wipe_remote_command

  jenkins_container_remote_q=$(fogell_quote_posix_shell_v2 "$jenkins_container") || return 2

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
  collector_script="printf %s ${collector_b64} | base64 -d | bash -s -- '/var/jenkins_home/workspace/{job}'"
  collector_script_remote_q=$(fogell_quote_posix_shell_v2 "$collector_script") || return 2
  workspace_remote_command="podman exec ${jenkins_container_remote_q} bash -c ${collector_script_remote_q}"
  FOGELL_JENKINS_WORKSPACE_CMD=$(
    fogell_jenkins_ssh_command_v2 "$jenkins_host" "$workspace_remote_command"
  ) || return 2
  export FOGELL_JENKINS_WORKSPACE_CMD

  # A compile-time refusal may never allocate a workspace. Precreate the empty
  # root so protocol v2 observes a real empty tree instead of treating a failed
  # `cd` as evidence.
  wipe_script="rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp && mkdir -p /var/jenkins_home/workspace/{job}"
  wipe_script_remote_q=$(fogell_quote_posix_shell_v2 "$wipe_script") || return 2
  wipe_remote_command="podman exec ${jenkins_container_remote_q} sh -c ${wipe_script_remote_q}"
  FOGELL_JENKINS_WIPE_CMD=$(
    fogell_jenkins_ssh_command_v2 "$jenkins_host" "$wipe_remote_command"
  ) || return 2
  export FOGELL_JENKINS_WIPE_CMD
}

fogell_jenkins_podman_inspect_v2() {
  if [ "$#" -ne 3 ]; then
    echo "usage: fogell_jenkins_podman_inspect_v2 <host> <container> <format>" >&2
    return 2
  fi

  local jenkins_host=$1
  local jenkins_container=$2
  local inspect_format=$3
  local jenkins_container_q
  local inspect_format_q

  printf -v jenkins_container_q '%q' "$jenkins_container" || return 2
  printf -v inspect_format_q '%q' "$inspect_format" || return 2
  # Both interpolations are deliberately client-expanded only after `%q`
  # escaping; the remote shell must receive one inert command string.
  # shellcheck disable=SC2029
  ssh -- "$jenkins_host" \
    "podman inspect --format=$inspect_format_q $jenkins_container_q"
}
