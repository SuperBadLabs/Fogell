#!/bin/bash
# FG-037. Invoke the measured .NET build or its exact output without inheriting
# arbitrary MSBuild properties from the custodian shell.
set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "usage: $0 <build|exec|version> <private-workspace> [dotnet-arguments...]" >&2
  exit 2
fi

mode=$1
workspace=$2
shift 2

if [[ $workspace != /run/fogell-fg037-source.* ]] \
  || [ ! -d "$workspace" ] || [ -L "$workspace" ]; then
  echo "REFUSED: controlled dotnet workspace must be one root-owned private directory" >&2
  exit 2
fi
workspace=$(/usr/bin/realpath -- "$workspace")
run_mode=$(/usr/bin/stat -Lc %a -- /run)
if [ "$(/usr/bin/stat -Lc %u -- "$workspace")" != 0 ] \
  || [ "$(/usr/bin/stat -Lc %u -- /run)" != 0 ] \
  || [ -L /run ] || [ "$(/usr/bin/realpath -- /run)" != /run ] \
  || [[ ! $run_mode =~ ^[0-7]+$ ]] \
  || (( (8#$run_mode & 0022) != 0 )); then
  echo "REFUSED: controlled dotnet workspace ancestry is not root-owned" >&2
  exit 2
fi
workspace_mode=$(/usr/bin/stat -Lc %a -- "$workspace")
if [[ ! $workspace_mode =~ ^[0-7]+$ ]] \
  || (( (8#$workspace_mode & 0022) != 0 )); then
  echo "REFUSED: controlled dotnet workspace namespace must be read-only" >&2
  exit 2
fi
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
source_mode=$(/usr/bin/stat -Lc %a -- "$source_root")
if [ "$(/usr/bin/stat -Lc %u -- "$source_root")" != 0 ] \
  || [[ ! $source_mode =~ ^[0-7]+$ ]] \
  || (( (8#$source_mode & 0022) != 0 )); then
  echo "REFUSED: controlled dotnet source root is writable by the probe identity" >&2
  exit 2
fi
source_global_json=$source_root/global.json
source_build_props=$source_root/Directory.Build.props
source_build_targets=$source_root/Directory.Build.targets
source_packages_props=$source_root/Directory.Packages.props
for governed_build_input in "$source_global_json" "$source_build_props" \
  "$source_build_targets" "$source_packages_props"; do
  governed_mode=$(/usr/bin/stat -Lc %a -- "$governed_build_input" 2>/dev/null || true)
  if [ ! -f "$governed_build_input" ] || [ -L "$governed_build_input" ] \
    || [ "$(/usr/bin/stat -Lc %u -- "$governed_build_input" 2>/dev/null)" != 0 ] \
    || [[ ! $governed_mode =~ ^[0-7]+$ ]] \
    || (( (8#$governed_mode & 0022) != 0 )); then
    echo "REFUSED: controlled dotnet source root lacks real governed build inputs" >&2
    exit 2
  fi
done

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
runtime_tmp=$workspace/runtime-tmp
/usr/bin/mkdir -p "$build_home" "$build_tmp" "$nuget_packages" \
  "$nuget_cache" "$bundle_cache" "$build_cwd"
for writable_dir in "$build_home" "$build_tmp" "$nuget_packages" \
  "$nuget_cache" "$bundle_cache" "$build_cwd"; do
  if [ ! -d "$writable_dir" ] || [ -L "$writable_dir" ] \
    || [[ $(/usr/bin/realpath -- "$writable_dir") != "$workspace/"* ]]; then
    echo "REFUSED: controlled dotnet writable directory is not one real directory" >&2
    exit 2
  fi
done
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
msbuild_global_properties=(
  "-p:DirectoryBuildPropsPath=$source_build_props"
  "-p:DirectoryBuildTargetsPath=$source_build_targets"
  "-p:DirectoryPackagesPropsPath=$source_packages_props"
)

case "$mode" in
  build)
    if [ "$#" -eq 0 ]; then
      echo "REFUSED: controlled dotnet build mode requires arguments" >&2
      exit 2
    fi
    exec /usr/bin/env -i "${controlled_env[@]}" "$dotnet_bin" \
      "$@" "${msbuild_global_properties[@]}"
    ;;
  exec)
    if [ "$#" -eq 0 ]; then
      echo "REFUSED: controlled dotnet exec mode requires an assembly" >&2
      exit 2
    fi
    if [ ! -d "$runtime_tmp" ] || [ -L "$runtime_tmp" ] \
      || [[ $(/usr/bin/realpath -- "$runtime_tmp") != "$workspace/"* ]]; then
      echo "REFUSED: controlled dotnet runtime directory is not one real directory" >&2
      exit 2
    fi
    # This launches a built assembly directly, so no MSBuild evaluation occurs.
    # Preserve only the runtime inputs consumed by the focused differential CLI.
    runtime_env=(
      "PATH=$controlled_path"
      "HOME=${HOME:?}"
      TMPDIR="$runtime_tmp"
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
  version)
    if [ "$#" -ne 0 ]; then
      echo "REFUSED: controlled dotnet version mode takes no arguments" >&2
      exit 2
    fi
    exec /usr/bin/env -i "${controlled_env[@]}" "$dotnet_bin" --version
    ;;
  *)
    echo "REFUSED: controlled dotnet mode must be build, exec, or version" >&2
    exit 2
    ;;
esac
