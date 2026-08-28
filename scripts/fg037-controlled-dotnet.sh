#!/bin/bash
# FG-037. Invoke the measured .NET build or its exact output without inheriting
# arbitrary MSBuild properties from the custodian shell.
set -euo pipefail

if [ "$#" -lt 3 ]; then
  echo "usage: $0 <build|exec> <private-workspace> <dotnet-arguments...>" >&2
  exit 2
fi

mode=$1
workspace=$2
shift 2

if [[ $workspace != /* ]] || [ ! -d "$workspace" ] || [ -L "$workspace" ]; then
  echo "REFUSED: controlled dotnet workspace must be one real absolute directory" >&2
  exit 2
fi
workspace=$(/usr/bin/realpath -- "$workspace")
source_root=$workspace/source
if [ ! -d "$source_root" ] || [ -L "$source_root" ]; then
  echo "REFUSED: controlled dotnet source root must be one real directory" >&2
  exit 2
fi
source_root=$(/usr/bin/realpath -- "$source_root")
if [[ $source_root != "$workspace/"* ]]; then
  echo "REFUSED: controlled dotnet source root escapes its private workspace" >&2
  exit 2
fi
source_global_json=$source_root/global.json
if [ ! -f "$source_global_json" ] || [ -L "$source_global_json" ]; then
  echo "REFUSED: controlled dotnet source root lacks one real global.json" >&2
  exit 2
fi

dotnet_bin=/usr/bin/dotnet
controlled_path=/usr/local/bin:/usr/bin:/bin
if [ ! -x "$dotnet_bin" ]; then
  echo "REFUSED: FG-037 requires the host-pinned /usr/bin/dotnet" >&2
  exit 2
fi

build_home=$workspace/dotnet-home
build_tmp=$workspace/tmp
nuget_packages=$workspace/nuget-packages
nuget_cache=$workspace/nuget-http-cache
bundle_cache=$workspace/dotnet-bundle-cache
build_cwd=$workspace/dotnet-cwd
/usr/bin/mkdir -p "$build_home" "$build_tmp" "$nuget_packages" \
  "$nuget_cache" "$bundle_cache" "$build_cwd"
if [ -L "$build_cwd" ] \
  || [[ $(/usr/bin/realpath -- "$build_cwd") != "$workspace/"* ]]; then
  echo "REFUSED: controlled dotnet working directory escapes its private workspace" >&2
  exit 2
fi
build_global_json=$build_cwd/global.json
if [ -e "$build_global_json" ] || [ -L "$build_global_json" ]; then
  if [ ! -f "$build_global_json" ] || [ -L "$build_global_json" ] \
    || ! /usr/bin/cmp -s -- "$source_global_json" "$build_global_json"; then
    echo "REFUSED: controlled dotnet SDK selection differs from exported HEAD" >&2
    exit 2
  fi
else
  /usr/bin/install -m 0444 -- "$source_global_json" "$build_global_json"
fi
cd -- "$build_cwd"

controlled_env=(
  "PATH=$controlled_path"
  HOME="$build_home"
  TMPDIR="$build_tmp"
  LANG=C.UTF-8
  LC_ALL=C.UTF-8
  DOTNET_CLI_HOME="$build_home"
  DOTNET_BUNDLE_EXTRACT_BASE_DIR="$bundle_cache"
  DOTNET_CLI_TELEMETRY_OPTOUT=1
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
  DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1
  DOTNET_MULTILEVEL_LOOKUP=0
  DOTNET_NOLOGO=1
  MSBUILDDISABLENODEREUSE=1
  MSBUILDFAILONDRIVEENUMERATINGWILDCARD=1
  MSBuildEnableWorkloadResolver=false
  NUGET_PACKAGES="$nuget_packages"
  NUGET_HTTP_CACHE_PATH="$nuget_cache"
)

case "$mode" in
  build)
    exec /usr/bin/env -i "${controlled_env[@]}" "$dotnet_bin" "$@"
    ;;
  exec)
    # This launches a built assembly directly, so no MSBuild evaluation occurs.
    # Preserve only the runtime inputs consumed by the focused differential CLI.
    runtime_env=(
      "PATH=$controlled_path"
      "HOME=${HOME:?}"
      TMPDIR="$build_tmp"
      LANG=C.UTF-8
      LC_ALL=C.UTF-8
      "FOGELL_JENKINS_WORKSPACE_CMD=${FOGELL_JENKINS_WORKSPACE_CMD:-}"
      "FOGELL_JENKINS_WIPE_CMD=${FOGELL_JENKINS_WIPE_CMD:-}"
      "FOGELL_JENKINS_ENV_CMD=${FOGELL_JENKINS_ENV_CMD:-}"
      "FOGELL_JENKINS_GIT_VERSION_CMD=${FOGELL_JENKINS_GIT_VERSION_CMD:-}"
      "FOGELL_JENKINS_RAW_CONSOLE_JOB=${FOGELL_JENKINS_RAW_CONSOLE_JOB:-}"
      "FOGELL_JENKINS_RAW_CONSOLE_BUILD=${FOGELL_JENKINS_RAW_CONSOLE_BUILD:-}"
      "FOGELL_JENKINS_RAW_CONSOLE_PATH=${FOGELL_JENKINS_RAW_CONSOLE_PATH:-}"
    )
    if [ -n "${SSH_AUTH_SOCK:-}" ]; then
      runtime_env+=("SSH_AUTH_SOCK=$SSH_AUTH_SOCK")
    fi
    exec /usr/bin/env -i "${runtime_env[@]}" "$dotnet_bin" "$@"
    ;;
  *)
    echo "REFUSED: controlled dotnet mode must be build or exec" >&2
    exit 2
    ;;
esac
