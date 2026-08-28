#!/usr/bin/env bash
# FG-037. Mutation proof for the retained-evidence checkers.
set -euo pipefail
PATH=/usr/local/bin:/usr/bin:/bin
export PATH
script_source_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
publication_repo_root=${FG037_PUBLICATION_REPO_ROOT:-$script_source_root}
cd "$publication_repo_root"

if { [ "$#" -ne 1 ] && [ "$#" -ne 6 ]; } \
  || [ ! -d "$1/cases" ] || [ ! -d "$1/receipts" ]; then
  echo "usage: $0 <fg037-evidence-directory> [bundle-ref prerequisite bundle-head measured-commit measured-tree]" >&2
  exit 2
fi

source_dir=$1
scratch=$(mktemp -d)
msbuild_workspace=
proof_cleanup() {
  set +e
  if [[ $msbuild_workspace == /run/fogell-fg037-source.* ]] \
    && [ -d "$msbuild_workspace" ] && [ ! -L "$msbuild_workspace" ] \
    && [ "$(stat -Lc %u -- "$msbuild_workspace" 2>/dev/null)" = 0 ]; then
    /usr/bin/sudo -n /bin/chmod -R u+w -- "$msbuild_workspace"
    /usr/bin/sudo -n /bin/rm -rf --one-file-system -- "$msbuild_workspace"
  fi
  rm -rf "$scratch"
}
trap proof_cleanup EXIT

check() {
  python3 "$script_source_root/scripts/check-fg037-step-ceiling.py" \
    --cases "$1/cases" --receipts "$1/receipts" --jenkins-core 2.568.1
}

fresh() {
  rm -rf "$scratch/case"
  mkdir -p "$scratch/case"
  cp -a "$source_dir/cases" "$source_dir/receipts" "$scratch/case/"
}

expect_reject() {
  local label=$1
  if check "$scratch/case" >/dev/null 2>&1; then
    echo "FAIL: checker accepted $label" >&2
    exit 1
  fi
  echo "  rejected $label"
}

echo "=== FG-037 semantic checker mutation proof ==="
check "$source_dir" >/dev/null
echo "  accepted the unmodified retained evidence"

manifest_checker=$script_source_root/scripts/check-fg037-manifest.py
manifest_case=$scratch/manifest-case
cp -a "$source_dir" "$manifest_case"
rm -f "$manifest_case/manifest.sha256"
manifest_case_tmp=$scratch/manifest-case.sha256
(
  cd "$manifest_case"
  find . -type f ! -name manifest.sha256 -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 sha256sum >"$manifest_case_tmp"
)
mv "$manifest_case_tmp" "$manifest_case/manifest.sha256"
python3 "$manifest_checker" "$manifest_case" >/dev/null
printf 'unlisted\n' >"$manifest_case/unlisted.txt"
if python3 "$manifest_checker" "$manifest_case" >/dev/null 2>&1; then
  echo "FAIL: manifest checker accepted an unlisted evidence file" >&2
  exit 1
fi
echo "  rejected an unlisted evidence file"

manifest_substitution=$scratch/manifest-substitution
cp -a "$source_dir" "$manifest_substitution"
rm -f "$manifest_substitution/manifest.sha256"
manifest_substitution_tmp=$scratch/manifest-substitution.sha256
(
  cd "$manifest_substitution"
  find . -type f ! -name manifest.sha256 -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 sha256sum >"$manifest_substitution_tmp"
)
mv "$manifest_substitution_tmp" "$manifest_substitution/manifest.sha256"
trusted_manifest_sha=$(sha256sum "$manifest_substitution/manifest.sha256" | awk '{print $1}')
printf '\nsubstituted payload\n' >>"$manifest_substitution/build.log"
(
  cd "$manifest_substitution"
  find . -type f ! -name manifest.sha256 -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 sha256sum >"$manifest_substitution_tmp"
)
mv "$manifest_substitution_tmp" "$manifest_substitution/manifest.sha256"
if python3 "$manifest_checker" \
  --expected-manifest-sha256 "$trusted_manifest_sha" \
  "$manifest_substitution" >/dev/null 2>&1; then
  echo "FAIL: manifest checker accepted a substituted payload and self-consistent manifest" >&2
  exit 1
fi
echo "  rejected a substituted payload and self-consistent manifest"

source_bundle_checker=${FG037_SOURCE_BUNDLE_CHECKER:-$script_source_root/scripts/check-fg037-source-bundle.sh}
dotnet_wrapper=$script_source_root/scripts/fg037-controlled-dotnet.sh
source_bundle=$source_dir/source/fg037-measured-source.bundle
source_allowed_signers=$source_dir/source/allowed_signers
if [ "$#" -eq 6 ]; then
  source_bundle_ref=$2
  source_prerequisite=$3
  source_bundle_head=$4
  measured_commit=$5
  measured_tree=$6
else
  source_bundle_ref=refs/heads/codex/fg-037-step-ceiling-publish
  source_prerequisite=804bf7967cf3708eb3bb44387d59a24310c89607
  source_bundle_head=488b662000dea32859ae507f92f4dc045f6e8fcd
  measured_commit=65674f9a4af80e358f645ad3409765a8738c68b4
  measured_tree=7e09d220260b9117890cf4275fc240d989101f7c
fi

bash "$source_bundle_checker" "$source_bundle" "$source_allowed_signers" "$source_bundle_ref" \
  "$source_prerequisite" "$source_bundle_head" \
  "$measured_commit" "$measured_tree" >/dev/null
echo "  reconstructed the signed measured source commit from the retained bundle"

clean_home=$scratch/clean-home
mkdir "$clean_home"
HOME="$clean_home" GIT_CONFIG_NOSYSTEM=1 \
  bash "$source_bundle_checker" "$source_bundle" "$source_allowed_signers" "$source_bundle_ref" \
  "$source_prerequisite" "$source_bundle_head" \
  "$measured_commit" "$measured_tree" >/dev/null
echo "  verified the pinned signer without custodian Git configuration"

corrupt_bundle=$scratch/corrupt-source.bundle
cp "$source_bundle" "$corrupt_bundle"
truncate -s -1 "$corrupt_bundle"
if bash "$source_bundle_checker" "$corrupt_bundle" "$source_allowed_signers" "$source_bundle_ref" \
  "$source_prerequisite" "$source_bundle_head" \
  "$measured_commit" "$measured_tree" >/dev/null 2>&1; then
  echo "FAIL: source-bundle checker accepted a truncated bundle" >&2
  exit 1
fi
echo "  rejected a truncated source bundle"

if bash "$source_bundle_checker" "$source_bundle" "$source_allowed_signers" "$source_bundle_ref" \
  "$source_prerequisite" "$source_bundle_head" \
  0000000000000000000000000000000000000000 "$measured_tree" >/dev/null 2>&1; then
  echo "FAIL: source-bundle checker accepted a missing measured commit" >&2
  exit 1
fi
echo "  rejected a missing measured commit identity"

empty_bundle=$scratch/empty-source.bundle
printf '# v2 git bundle\n-%s prerequisite\n%s %s\n\n' \
  "$source_prerequisite" "$source_bundle_head" \
  "$source_bundle_ref" >"$empty_bundle"
printf '' | git pack-objects --stdout >>"$empty_bundle"
if GIT_ALTERNATE_OBJECT_DIRECTORIES=$PWD/.git/objects \
  bash "$source_bundle_checker" "$empty_bundle" "$source_allowed_signers" "$source_bundle_ref" \
  "$source_prerequisite" "$source_bundle_head" \
  "$measured_commit" "$measured_tree" >/dev/null 2>&1; then
  echo "FAIL: source-bundle checker borrowed descendants from an ambient object store" >&2
  exit 1
fi
echo "  rejected an empty bundle despite a hostile ambient object alternate"

identity_checker=$script_source_root/scripts/check-fg037-jenkins-identity.sh
good_identity=$'2.568.1\tpinned-session'
stale_identity=$'2.568.1\tstale-forward-session'
wrong_core_identity=$'9.99.9\tpinned-session'
restarted_identity=$'2.568.1\trestarted-session'
bash "$identity_checker" 2.568.1 2.568.1 \
  "$good_identity" "$good_identity" >/dev/null
echo "  accepted one pinned endpoint/container identity"

if bash "$identity_checker" 2.568.1 2.568.1 \
  "$stale_identity" "$good_identity" >/dev/null 2>&1; then
  echo "FAIL: identity checker accepted a same-core stale endpoint session" >&2
  exit 1
fi
echo "  rejected a same-core stale endpoint session"

if bash "$identity_checker" 2.568.1 2.568.1 \
  "$wrong_core_identity" "$good_identity" >/dev/null 2>&1; then
  echo "FAIL: identity checker accepted a wrong endpoint core" >&2
  exit 1
fi
echo "  rejected a wrong endpoint core"

if bash "$identity_checker" 2.568.1 2.568.1 \
  "$restarted_identity" "$restarted_identity" pinned-session >/dev/null 2>&1; then
  echo "FAIL: identity checker accepted a controller session change" >&2
  exit 1
fi
echo "  rejected a controller session change across the live run"

fake_bin=$scratch/fake-bin
mkdir -p "$fake_bin"
printf '#!/usr/bin/env bash\nexit 9\n' >"$fake_bin/base64"
chmod +x "$fake_bin/base64"
set +e
PATH="$fake_bin:$PATH" bash -c \
  'source "$1"; fogell_configure_jenkins_workspace_v2 luigi jenkins-lab' \
  _ "$script_source_root/scripts/jenkins-workspace-v2.sh" \
  >"$scratch/base64-failure.log" 2>&1
base64_rc=$?
set -e
if [ "$base64_rc" -ne 2 ]; then
  echo "FAIL: shared collector configuration accepted a Base64 encoder failure" >&2
  sed -n '1,80p' "$scratch/base64-failure.log" >&2
  exit 1
fi
echo "  rejected a shared-collector Base64 encoder failure"

inspect_fake_bin=$scratch/inspect-fake-bin
mkdir -p "$inspect_fake_bin"
cat >"$inspect_fake_bin/ssh" <<'FAKE_SSH'
#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 3 ]
[ "$1" = -- ]
[ "$2" = "$FG037_EXPECTED_HOST" ]
shift 2
bash -c "$1"
FAKE_SSH
cat >"$inspect_fake_bin/podman" <<'FAKE_PODMAN'
#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 3 ]
[ "$1" = inspect ]
[ "$2" = '--format={{.Image}}' ]
[ "$3" = "$FG037_EXPECTED_CONTAINER" ]
printf '%s\n' pinned-image-id
FAKE_PODMAN
chmod +x "$inspect_fake_bin/ssh" "$inspect_fake_bin/podman"
inspect_injection_marker=$scratch/remote-inspect-injection
host_injection_marker=$scratch/ssh-host-injection
hostile_host="-oProxyCommand=touch $host_injection_marker"
hostile_container="jenkins-lab; touch $inspect_injection_marker"
inspect_result=$(
  PATH="$inspect_fake_bin:$PATH" \
    FG037_EXPECTED_HOST="$hostile_host" \
    FG037_EXPECTED_CONTAINER="$hostile_container" \
    bash -c \
      'source "$1"; fogell_jenkins_podman_inspect_v2 "$2" "$3" "{{.Image}}"' \
      _ "$script_source_root/scripts/jenkins-workspace-v2.sh" \
      "$hostile_host" "$hostile_container"
) || {
  echo "FAIL: shared collector rejected a safely quoted inspect override" >&2
  exit 1
}
if [ "$inspect_result" != pinned-image-id ] \
  || [ -e "$host_injection_marker" ] \
  || [ -e "$inspect_injection_marker" ]; then
  echo "FAIL: remote inspect did not contain hostile host/container overrides" >&2
  exit 1
fi
echo "  contained hostile remote-inspect host/container overrides"

nested_fake_bin=$scratch/nested-fake-bin
mkdir -p "$nested_fake_bin"
cat >"$nested_fake_bin/ssh" <<'FAKE_SSH'
#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 3 ]
[ "$1" = -- ]
[ "$2" = "$FG037_EXPECTED_HOST" ]
shift 2
bash -c "$1"
FAKE_SSH
cat >"$nested_fake_bin/podman" <<'FAKE_PODMAN'
#!/usr/bin/env bash
set -euo pipefail
[ "$1" = exec ]
[ "$2" = "$FG037_EXPECTED_CONTAINER" ]
printf '%s\n' "$2" >>"$FG037_CONTAINER_LOG"
shift 2
case "$1 ${2-}" in
  'bash -c')
    printf 'FOGELL-WORKSPACE-MANIFEST\t2\nEND\t0\n'
    ;;
  'sh -c')
    ;;
  'env ')
    printf 'PATH=/usr/bin\n'
    ;;
  'git --version')
    printf 'git version 2.0\n'
    ;;
  *)
    printf 'unexpected fake podman argv: %q ' "$@" >&2
    printf '\n' >&2
    exit 9
    ;;
esac
FAKE_PODMAN
chmod +x "$nested_fake_bin/ssh" "$nested_fake_bin/podman"
nested_host_marker=$scratch/nested-host-injection
nested_container_marker=$scratch/nested-container-injection
nested_backtick_marker=$scratch/nested-backtick-injection
nested_host="-oProxyCommand=touch $nested_host_marker"
nested_container="jenkins'\"; touch $nested_container_marker; \`touch $nested_backtick_marker\`"
nested_container_log=$scratch/nested-containers.log
PATH="$nested_fake_bin:$PATH" \
  FG037_EXPECTED_HOST="$nested_host" \
  FG037_EXPECTED_CONTAINER="$nested_container" \
  FG037_CONTAINER_LOG="$nested_container_log" \
  bash -c '
    set -euo pipefail
    source "$1"
    fogell_configure_jenkins_workspace_v2 "$2" "$3"
    container_q=$(fogell_quote_posix_shell_v2 "$3")
    env_cmd=$(fogell_jenkins_ssh_command_v2 "$2" "podman exec ${container_q} env")
    git_cmd=$(fogell_jenkins_ssh_command_v2 "$2" "podman exec ${container_q} git --version")
    workspace_cmd=${FOGELL_JENKINS_WORKSPACE_CMD//\{job\}/proof-job}
    wipe_cmd=${FOGELL_JENKINS_WIPE_CMD//\{job\}/proof-job}
    /bin/sh -c "$workspace_cmd" >/dev/null
    /bin/sh -c "$wipe_cmd" >/dev/null
    /bin/sh -c "$env_cmd" >/dev/null
    /bin/sh -c "$git_cmd" >/dev/null
  ' _ "$script_source_root/scripts/jenkins-workspace-v2.sh" \
  "$nested_host" "$nested_container"
if [ "$(wc -l <"$nested_container_log")" -ne 4 ] \
  || [ -e "$nested_host_marker" ] \
  || [ -e "$nested_container_marker" ] \
  || [ -e "$nested_backtick_marker" ] \
  || [ "$(sort -u "$nested_container_log")" != "$nested_container" ]; then
  echo "FAIL: nested local/remote shell commands did not contain hostile overrides" >&2
  exit 1
fi
echo "  contained hostile overrides across nested workspace/wipe/env/git commands"

msbuild_case=$scratch/msbuild-environment
proof_run_mode=$(/usr/bin/stat -Lc %a -- /run 2>/dev/null || true)
if [ ! -d /run ] || [ -L /run ] \
  || [ "$(/usr/bin/realpath -- /run 2>/dev/null)" != /run ] \
  || [ "$(/usr/bin/stat -Lc %u -- /run 2>/dev/null)" != 0 ] \
  || [[ ! $proof_run_mode =~ ^[0-7]+$ ]] \
  || (( (8#$proof_run_mode & 0022) != 0 )); then
  echo "FAIL: root-workspace proof requires canonical root-owned non-writable /run" >&2
  exit 1
fi
if ! /usr/bin/sudo -n /bin/true; then
  echo "FAIL: FG-037 root-workspace proof requires noninteractive sudo" >&2
  exit 1
fi
if [ ! -x /usr/bin/systemd-run ]; then
  echo "FAIL: root-workspace proof requires systemd DynamicUser isolation" >&2
  exit 1
fi
msbuild_workspace=$(/usr/bin/sudo -n /usr/bin/mktemp -d \
  /run/fogell-fg037-source.XXXXXXXXXX)
if [[ $msbuild_workspace != /run/fogell-fg037-source.* ]] \
  || [ ! -d "$msbuild_workspace" ] || [ -L "$msbuild_workspace" ] \
  || [ "$(stat -Lc %u -- "$msbuild_workspace")" != 0 ]; then
  echo "FAIL: root-owned proof workspace is malformed" >&2
  exit 1
fi
/usr/bin/sudo -n /bin/chmod 0777 "$msbuild_workspace"
mkdir -p "$msbuild_workspace/source" "$msbuild_case/ambient-cwd" \
  "$msbuild_case/fake-bin"
safe_dotnet_version=$(python3 - "$script_source_root/global.json" <<'PY'
import json
import pathlib
import sys

value = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))["sdk"]["version"]
if not isinstance(value, str) or not value:
    raise SystemExit("global.json sdk.version is not one nonempty string")
print(value)
PY
)
printf '{"sdk":{"version":"%s","rollForward":"disable"}}\n' \
  "$safe_dotnet_version" >"$msbuild_workspace/source/global.json"
cp "$script_source_root/Directory.Build.props" \
  "$msbuild_workspace/source/Directory.Build.props"
msbuild_marker=$msbuild_case/ambient-target-ran
build_ancestor_marker=$msbuild_case/ancestor-build-target-ran
packages_ancestor_marker=$msbuild_case/ancestor-packages-props-ran
build_ancestor_file=$msbuild_workspace/Directory.Build.targets
packages_ancestor_file=$msbuild_workspace/Directory.Packages.props
protected_dotnet_wrapper=$msbuild_workspace/source/fg037-controlled-dotnet.sh
cp "$dotnet_wrapper" "$protected_dotnet_wrapper"
safe_project_marker=$msbuild_workspace/source/bin/protected-build-output.dll
cat >"$msbuild_workspace/source/project.proj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <Target Name="Probe">
    <WriteLinesToFile
      File="$(MSBuildProjectDirectory)/bin/protected-build-output.dll"
      Lines="safe" />
  </Target>
</Project>
PROJECT
printf '<Project><Target Name="Injected" BeforeTargets="Probe"><WriteLinesToFile File="%s" Lines="hostile" /></Target></Project>\n' \
  "$msbuild_marker" >"$msbuild_case/hostile.targets"
mkdir "$msbuild_case/precondition-home"
DOTNET_CLI_HOME=$msbuild_case/precondition-home \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 \
  MSBuildEnableWorkloadResolver=false \
  DirectoryBuildTargetsPath=$msbuild_case/hostile.targets \
  /usr/bin/dotnet msbuild "$msbuild_workspace/source/project.proj" \
  -t:Probe -nologo >/dev/null 2>&1
if [ ! -f "$msbuild_marker" ]; then
  echo "FAIL: ambient MSBuild attack precondition did not import its target" >&2
  exit 1
fi
rm "$msbuild_marker"

printf '<Project><Target Name="InjectedBuildAncestor" BeforeTargets="Probe"><WriteLinesToFile File="%s" Lines="hostile" /></Target></Project>\n' \
  "$build_ancestor_marker" >"$build_ancestor_file"
DOTNET_CLI_HOME=$msbuild_case/precondition-home \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 \
  MSBuildEnableWorkloadResolver=false \
  /usr/bin/dotnet msbuild "$msbuild_workspace/source/project.proj" \
  -t:Probe -nologo >/dev/null 2>&1
if [ ! -f "$build_ancestor_marker" ]; then
  echo "FAIL: ancestor Directory.Build.targets attack precondition did not import" >&2
  exit 1
fi
rm "$build_ancestor_marker"
rm "$build_ancestor_file"

printf '<Project><Target Name="InjectedPackagesAncestor" BeforeTargets="Probe"><WriteLinesToFile File="%s" Lines="hostile" /></Target></Project>\n' \
  "$packages_ancestor_marker" >"$packages_ancestor_file"
DOTNET_CLI_HOME=$msbuild_case/precondition-home \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 \
  MSBuildEnableWorkloadResolver=false \
  /usr/bin/dotnet msbuild "$msbuild_workspace/source/project.proj" \
  -t:Probe -nologo >/dev/null 2>&1
if [ ! -f "$packages_ancestor_marker" ]; then
  echo "FAIL: ancestor Directory.Packages.props attack precondition did not import" >&2
  exit 1
fi
rm "$packages_ancestor_marker"
printf '<Project><Target Name="InjectedBuildAncestor" BeforeTargets="Probe"><WriteLinesToFile File="%s" Lines="hostile" /></Target></Project>\n' \
  "$build_ancestor_marker" >"$build_ancestor_file"
cp "$script_source_root/Directory.Build.targets" \
  "$msbuild_workspace/source/Directory.Build.targets"
cp "$script_source_root/Directory.Packages.props" \
  "$msbuild_workspace/source/Directory.Packages.props"
controlled_writable_dirs=(
  "$msbuild_workspace/dotnet-home"
  "$msbuild_workspace/tmp"
  "$msbuild_workspace/nuget-packages"
  "$msbuild_workspace/nuget-http-cache"
  "$msbuild_workspace/dotnet-bundle-cache"
  "$msbuild_workspace/dotnet-cwd"
)
/usr/bin/sudo -n /usr/bin/install -d -o root -g root -m 0700 \
  "${controlled_writable_dirs[@]}"
set +e
/usr/bin/env -i PATH=/usr/local/bin:/usr/bin:/bin \
  /bin/bash --noprofile --norc "$dotnet_wrapper" version \
  "$msbuild_workspace" >"$scratch/writable-workspace.log" 2>&1
writable_workspace_rc=$?
set -e
if [ "$writable_workspace_rc" -ne 2 ] \
  || ! grep -Fq "workspace namespace must be read-only" \
    "$scratch/writable-workspace.log"; then
  echo "FAIL: controlled dotnet accepted a writable workspace namespace" >&2
  sed -n '1,80p' "$scratch/writable-workspace.log" >&2
  exit 1
fi
/usr/bin/sudo -n /bin/chown -R root:root "$msbuild_workspace/source"
/usr/bin/sudo -n /bin/chmod -R a-w,u+rwX,go+rX "$msbuild_workspace/source"
/usr/bin/sudo -n /usr/bin/install -d -o root -g root -m 0700 \
  "$msbuild_workspace/source/bin" "$msbuild_workspace/source/obj"
/usr/bin/sudo -n /bin/chmod 0755 "$msbuild_workspace"
source_identity=$(stat -Lc '%d:%i' "$msbuild_workspace/source")
set +e
chmod u+w "$msbuild_workspace" 2>/dev/null
chmod_rc=$?
mv "$msbuild_workspace/source" "$msbuild_workspace/source-swapped" 2>/dev/null
source_swap_rc=$?
mv "$msbuild_workspace" "$msbuild_workspace-swapped" 2>/dev/null
workspace_swap_rc=$?
set -e
if [ "$chmod_rc" -eq 0 ] || [ "$source_swap_rc" -eq 0 ] \
  || [ "$workspace_swap_rc" -eq 0 ] \
  || [ "$(stat -Lc '%d:%i' "$msbuild_workspace/source")" != "$source_identity" ]; then
  echo "FAIL: probe UID could mutate or replace the root-owned workspace namespace" >&2
  exit 1
fi

bash_env_marker=$msbuild_case/bash-env-ran
fake_env_marker=$msbuild_case/fake-env-ran
printf 'printf hostile >%q\n' "$bash_env_marker" >"$msbuild_case/bash-env"
printf '#!/bin/sh\nprintf hostile >%s\nexec /usr/bin/env "$@"\n' \
  "$fake_env_marker" >"$msbuild_case/fake-bin/env"
chmod +x "$msbuild_case/fake-bin/env"
BASH_ENV=$msbuild_case/bash-env \
  PATH=$msbuild_case/fake-bin:/usr/local/bin:/usr/bin:/bin \
  /bin/bash --noprofile --norc -c 'env -i /bin/true'
if [ ! -e "$bash_env_marker" ] || [ ! -e "$fake_env_marker" ]; then
  echo "FAIL: hostile Bash-entry/PATH attack precondition did not execute" >&2
  exit 1
fi
rm "$bash_env_marker" "$fake_env_marker"

cat >"$msbuild_case/ambient-cwd/global.json" <<'JSON'
{"sdk":{"version":"0.0.0","rollForward":"disable"}}
JSON
if (cd "$msbuild_case/ambient-cwd" && /usr/bin/dotnet --version >/dev/null 2>&1); then
  echo "FAIL: hostile startup-directory SDK attack precondition did not fail" >&2
  exit 1
fi

controlled_build_log=$msbuild_case/controlled-build.log
dynamic_build_paths=
# `$USER` expands only in systemd's root ExecStartPre after DynamicUser exists.
# shellcheck disable=SC2016
dynamic_prepare='/usr/bin/chown -R "$USER:$USER"'
for dynamic_build_path in \
  "$msbuild_workspace/source/bin" "$msbuild_workspace/source/obj" \
  "${controlled_writable_dirs[@]}"; do
  if [[ ! $dynamic_build_path =~ ^/run/fogell-fg037-source\.[A-Za-z0-9]+/[A-Za-z0-9._/-]+$ ]]; then
    echo "FAIL: proof dynamic build path has an unsafe shape" >&2
    exit 1
  fi
  dynamic_build_paths+="${dynamic_build_paths:+ }$dynamic_build_path"
  dynamic_prepare+=" $dynamic_build_path"
done
set +e
(
  cd "$msbuild_case/ambient-cwd"
  # The parent shell intentionally owns the diagnostic redirection.
  # shellcheck disable=SC2024
  DirectoryBuildTargetsPath=$msbuild_case/hostile.targets \
    BASH_ENV=$msbuild_case/bash-env \
    PATH=$msbuild_case/fake-bin:/usr/local/bin:/usr/bin:/bin \
    /usr/bin/sudo -n /usr/bin/systemd-run --pipe --wait --collect --quiet \
    -p DynamicUser=yes -p PrivateTmp=yes -p NoNewPrivileges=yes \
    -p RestrictSUIDSGID=yes -p ProtectSystem=strict -p ProtectHome=yes \
    -p KillMode=control-group \
    -p "ReadOnlyPaths=$msbuild_workspace/source" \
    -p "ReadWritePaths=$dynamic_build_paths" \
    -p "ExecStartPre=+/bin/sh -c '$dynamic_prepare'" \
    -p "ExecStopPost=+/usr/bin/chown -R root:root $dynamic_build_paths" \
    -p "ExecStopPost=+/usr/bin/chmod -R a-w,u+rwX,go+rX $dynamic_build_paths" \
    /usr/bin/env -i PATH=/usr/local/bin:/usr/bin:/bin \
    /bin/bash --noprofile --norc "$protected_dotnet_wrapper" build \
    "$msbuild_workspace" msbuild \
    "$msbuild_workspace/source/project.proj" \
    "-p:DirectoryBuildPropsPath=$msbuild_case/hostile.targets" \
    "-p:DirectoryBuildTargetsPath=$msbuild_case/hostile.targets" \
    "-p:DirectoryPackagesPropsPath=$packages_ancestor_file" \
    -t:Probe -nologo \
    >"$controlled_build_log" 2>&1
)
controlled_build_rc=$?
set -e
if [ "$controlled_build_rc" -ne 0 ]; then
  echo "FAIL: controlled isolated-identity build exited $controlled_build_rc" >&2
  sed -n '1,160p' "$controlled_build_log" >&2
  exit 1
fi
for inherited_input in \
  "MSBuild property:$msbuild_marker" \
  "ancestor Directory.Build.targets:$build_ancestor_marker" \
  "ancestor Directory.Packages.props:$packages_ancestor_marker" \
  "BASH_ENV:$bash_env_marker" \
  "PATH:$fake_env_marker"; do
  inherited_label=${inherited_input%%:*}
  inherited_marker=${inherited_input#*:}
  if [ -e "$inherited_marker" ]; then
    echo "FAIL: controlled dotnet launch inherited $inherited_label" >&2
    exit 1
  fi
done
proof_output_dirs=(
  "$msbuild_workspace/source/bin"
  "$msbuild_workspace/source/obj"
)
proof_assembly=$safe_project_marker
proof_output_violation=$(/usr/bin/find "${proof_output_dirs[@]}" -xdev \
  \( ! -user root -o -type l -o \( ! -type d ! -type f \) \
    -o -perm /022 \) -print -quit)
if [ ! -f "$safe_project_marker" ]; then
  echo "FAIL: controlled dotnet did not remain in its private workspace" >&2
  sed -n '1,160p' "$controlled_build_log" >&2
  exit 1
fi
if [ ! -f "$proof_assembly" ] || [ -L "$proof_assembly" ] \
  || [ -n "$proof_output_violation" ]; then
  echo "FAIL: controlled build did not freeze one real root-owned output tree" >&2
  exit 1
fi
proof_special=$msbuild_workspace/source/obj/hostile.fifo
/usr/bin/sudo -n /usr/bin/mkfifo -- "$proof_special"
/usr/bin/sudo -n /bin/chmod 0444 -- "$proof_special"
proof_special_violation=$(/usr/bin/find "${proof_output_dirs[@]}" -xdev \
  \( ! -user root -o -type l -o \( ! -type d ! -type f \) \
    -o -perm /022 \) -print -quit)
if [ "$proof_special_violation" != "$proof_special" ]; then
  echo "FAIL: frozen-output proof accepted a special file" >&2
  exit 1
fi
/usr/bin/sudo -n /bin/rm -- "$proof_special"
proof_output_dir=${proof_output_dirs[0]}
proof_output_identity=$(stat -Lc '%d:%i' "$proof_output_dir")
proof_assembly_identity=$(stat -Lc '%d:%i' "$proof_assembly")
proof_assembly_sha=$(sha256sum "$proof_assembly" | awk '{print $1}')
set +e
chmod u+w "$proof_output_dir" 2>/dev/null
proof_output_chmod_rc=$?
mv "$proof_output_dir" "$proof_output_dir-swapped" 2>/dev/null
proof_output_swap_rc=$?
(printf hostile >"$proof_assembly") 2>/dev/null
proof_assembly_write_rc=$?
rm "$proof_assembly" 2>/dev/null
proof_assembly_unlink_rc=$?
set -e
if [ "$proof_output_chmod_rc" -eq 0 ] \
  || [ "$proof_output_swap_rc" -eq 0 ] \
  || [ "$proof_assembly_write_rc" -eq 0 ] \
  || [ "$proof_assembly_unlink_rc" -eq 0 ] \
  || [ "$(stat -Lc '%d:%i' "$proof_output_dir")" != "$proof_output_identity" ] \
  || [ "$(stat -Lc '%d:%i' "$proof_assembly")" != "$proof_assembly_identity" ] \
  || [ "$(sha256sum "$proof_assembly" | awk '{print $1}')" != "$proof_assembly_sha" ]; then
  echo "FAIL: probe UID could mutate or replace controlled build output" >&2
  exit 1
fi
/usr/bin/sudo -n /bin/chmod -R u+w -- "$msbuild_workspace"
/usr/bin/sudo -n /bin/rm -rf --one-file-system -- "$msbuild_workspace"
msbuild_workspace=
echo "  cleared ambient Bash, PATH, CWD/SDK, MSBuild policy, and same-UID namespace inputs"

fresh
sed -i 's/^jenkins-core: 2\.568\.1$/jenkins-core: 9.99.9/' \
  "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a substituted Jenkins core"

fresh
sed -i '/^## Jenkins$/,/^## Fogell$/s/^  result:         failure$/  result:         success/' \
  "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a 251-step Jenkins success substitution"

fresh
sed -i '0,/^    | FG037-400$/s//    | FG037-399/' \
  "$scratch/case/receipts/fg037-400-steps.receipt.txt"
expect_reject "a skipped final Fogell marker"

fresh
sed -i 's/The max number of supported arguments is 255, but found 400/compiler diagnostic removed/' \
  "$scratch/case/receipts/fg037-400-steps.receipt.txt"
expect_reject "a missing exact 400/255 compiler diagnostic"

fresh
printf 'Started by user unknown or anonymous\nAgent provisioning failed\nFinished: FAILURE\n' \
  >"$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "an unrelated 251-step infrastructure failure"

fresh
sed -i '0,/java\.lang\.Object, /s///' \
  "$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "a 250-object substitution in the 251-step causal signature"

fresh
sed -i '/^Finished: FAILURE$/i [Pipeline] Start of Pipeline' \
  "$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "a 251-step console claiming Pipeline execution began"

fresh
sed -i '/CpsFlowExecution\.parseScript/d' \
  "$scratch/case/receipts/fg037-251-steps.jenkins-console.txt"
expect_reject "a missing 251-step CPS parse frame"

fresh
sed -i 's/^VERDICT: DIVERGED (/VERDICT: PROVEN (tier 1) — forged (/' \
  "$scratch/case/receipts/fg037-400-steps.receipt.txt"
expect_reject "a promoted intentional divergence"

fresh
sed -i 's/^VERDICT: DIVERGED (3)$/VERDICT: DIVERGED (1)/' \
  "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a weakened one-difference verdict"

fresh
sed -i 's/^VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash$/& — forged suffix/' \
  "$scratch/case/receipts/fg037-250-steps.receipt.txt"
expect_reject "a forged suffix on the exact control verdict"

fresh
sed -i '2s/$/ /' "$scratch/case/cases/fg037-250-steps.Jenkinsfile"
expect_reject "fixture drift even before its digest is updated"

fresh
cp "$scratch/case/receipts/fg037-250-steps.receipt.txt" \
  "$scratch/case/receipts/unexpected.receipt.txt"
expect_reject "an extra receipt outside the exact three-file inventory"

fresh
rm "$scratch/case/receipts/fg037-251-steps.receipt.txt"
expect_reject "a missing boundary receipt"

probe_repo=$scratch/probe-repo
mkdir -p "$probe_repo/scripts" "$probe_repo/tools/Fogell.Differential.Cli" \
  "$probe_repo/src" \
  "$probe_repo/evidence/20260827T185436Z-fg037-step-ceiling/source"
cp "$script_source_root/scripts/run-fg037-step-ceiling-probe.sh" \
  "$script_source_root/scripts/check-fg037-jenkins-identity.sh" \
  "$script_source_root/scripts/check-fg037-manifest.py" \
  "$script_source_root/scripts/check-fg037-source-bundle.sh" \
  "$script_source_root/scripts/check-fg037-step-ceiling.py" \
  "$script_source_root/scripts/fg037-controlled-dotnet.sh" \
  "$script_source_root/scripts/jenkins-workspace-v2.sh" \
  "$script_source_root/scripts/prove-fg037-step-ceiling.sh" \
  "$probe_repo/scripts/"
chmod u+w "$probe_repo/scripts/"*
cp evidence/20260827T185436Z-fg037-step-ceiling/source/allowed_signers \
  "$probe_repo/evidence/20260827T185436Z-fg037-step-ceiling/source/"
touch "$probe_repo/Fogell.slnx" "$probe_repo/global.json" \
  "$probe_repo/Directory.Build.props" "$probe_repo/Directory.Build.targets" \
  "$probe_repo/Directory.Packages.props" \
  "$probe_repo/tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj" \
  "$probe_repo/src/Engine.fs"
git -C "$probe_repo" init -q --initial-branch=main
git -C "$probe_repo" add .
git -C "$probe_repo" -c user.name=FG-037 -c user.email=fg037@example.invalid \
  -c commit.gpgsign=false commit -qm baseline

# A caller must not be able to forge the private immutable-runner handoff and
# turn its cleanup into deletion of the tracked runner (or skip the re-exec).
probe_output=$probe_repo/forged-runner-handoff-evidence
set +e
(
  exec 9<"$probe_repo/scripts/run-fg037-step-ceiling-probe.sh"
  FOGELL_FG037_IMMUTABLE_RUNNER_FD=9 \
    FOGELL_FG037_PHYSICAL_REPO_ROOT="$probe_repo" \
    "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output"
) >"$scratch/forged-runner-handoff.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || [ ! -f "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" ] \
  || ! grep -Fq "immutable FG-037 runner handoff is malformed" \
    "$scratch/forged-runner-handoff.log"; then
  echo "FAIL: probe accepted or destructively cleaned a forged runner handoff" >&2
  sed -n '1,120p' "$scratch/forged-runner-handoff.log" >&2
  exit 1
fi
echo "  refused a forged immutable-runner handoff without deleting its target"

# A tracked bin/obj path is part of HEAD but must never become a write route out
# of the read-only export. Commit that shape in an independent fake repository
# and prove the runner refuses it before any external target or evidence exists.
output_path_repo=$scratch/output-path-repo
cp -a "$probe_repo/." "$output_path_repo/"
external_output_target=$scratch/external-build-output
mkdir "$external_output_target"
ln -s "$external_output_target" \
  "$output_path_repo/tools/Fogell.Differential.Cli/bin"
git -C "$output_path_repo" add -f tools/Fogell.Differential.Cli/bin
git -C "$output_path_repo" -c user.name=FG-037 -c user.email=fg037@example.invalid \
  -c commit.gpgsign=false commit -qm 'tracked output symlink'
probe_output=$output_path_repo/tracked-output-symlink-evidence
set +e
FOGELL_JENKINS_URL=http://127.0.0.1:1 \
  "$output_path_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output" \
  >"$scratch/tracked-output-symlink.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || find "$external_output_target" -mindepth 1 -print -quit | grep -q . \
  || ! grep -Fq "pre-existing project bin/obj path" \
    "$scratch/tracked-output-symlink.log"; then
  echo "FAIL: probe accepted a tracked project-output symlink" >&2
  sed -n '1,120p' "$scratch/tracked-output-symlink.log" >&2
  exit 1
fi
echo "  refused a tracked project-output symlink without writing its target"

ambient_worktree=$scratch/ambient-clean-worktree
mkdir "$ambient_worktree"
cp -a "$probe_repo/." "$ambient_worktree/"
printf '\n# planted collector drift\n' >>"$probe_repo/scripts/jenkins-workspace-v2.sh"
probe_output=$probe_repo/collector-drift-evidence
set +e
GIT_DIR="$probe_repo/.git" GIT_WORK_TREE="$ambient_worktree" \
  FOGELL_JENKINS_URL=http://127.0.0.1:1 \
  "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output" \
  >"$scratch/collector-drift.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || ! grep -Fq "scripts/jenkins-workspace-v2.sh" "$scratch/collector-drift.log"; then
  echo "FAIL: probe did not refuse a dirty shared workspace collector before evidence creation" >&2
  sed -n '1,120p' "$scratch/collector-drift.log" >&2
  exit 1
fi
echo "  refused a dirty shared workspace collector despite hostile Git repository selectors"

# The source-bundle verifier is as load-bearing as the collector: allowing its
# worktree copy to drift would let a fresh run attest with code other than the
# recorded HEAD snapshot. Restore the first mutation, plant this one, and prove
# the runner refuses it before allocating an evidence directory.
git -C "$probe_repo" show HEAD:scripts/jenkins-workspace-v2.sh \
  >"$probe_repo/scripts/jenkins-workspace-v2.sh"
printf '\n# planted source-bundle checker drift\n' \
  >>"$probe_repo/scripts/check-fg037-source-bundle.sh"
probe_output=$probe_repo/source-bundle-checker-drift-evidence
set +e
FOGELL_JENKINS_URL=http://127.0.0.1:1 \
  "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output" \
  >"$scratch/source-bundle-checker-drift.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || ! grep -Fq "scripts/check-fg037-source-bundle.sh" \
    "$scratch/source-bundle-checker-drift.log"; then
  echo "FAIL: probe did not refuse a dirty source-bundle checker before evidence creation" >&2
  sed -n '1,120p' "$scratch/source-bundle-checker-drift.log" >&2
  exit 1
fi
echo "  refused a dirty source-bundle checker before evidence creation"

# A committed build-policy ignore must not let a caller-selected literal
# pathspec mode or repository-local exclude hide a new root MSBuild import.
git -C "$probe_repo" show HEAD:scripts/check-fg037-source-bundle.sh \
  >"$probe_repo/scripts/check-fg037-source-bundle.sh"
printf '<Project />\n' >"$probe_repo/Directory.Build.hostile.props"
printf '/Directory.Build.hostile.props\n' >>"$probe_repo/.git/info/exclude"
probe_output=$probe_repo/hostile-build-policy-evidence
set +e
GIT_LITERAL_PATHSPECS=1 FOGELL_JENKINS_URL=http://127.0.0.1:1 \
  "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output" \
  >"$scratch/hostile-build-policy.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || ! grep -Fq "Directory.Build.hostile.props" \
    "$scratch/hostile-build-policy.log"; then
  echo "FAIL: probe did not refuse a hidden build-policy input under hostile pathspec mode" >&2
  sed -n '1,120p' "$scratch/hostile-build-policy.log" >&2
  exit 1
fi
echo "  refused a hidden build-policy input under hostile Git pathspec mode"

# Local core.worktree is another repository selector. It must not redirect Git
# to the clean copy while the physical checkout supplies dirty engine bytes to
# dotnet and the differential CLI.
rm "$probe_repo/Directory.Build.hostile.props"
git -C "$probe_repo" config core.worktree "$ambient_worktree"
printf '\n// planted physical engine drift\n' >>"$probe_repo/src/Engine.fs"
probe_output=$probe_repo/core-worktree-drift-evidence
set +e
FOGELL_JENKINS_URL=http://127.0.0.1:1 \
  "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output" \
  >"$scratch/core-worktree-drift.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || ! grep -Fq "src/Engine.fs" "$scratch/core-worktree-drift.log"; then
  echo "FAIL: probe did not bind cleanliness to the physical checkout" >&2
  sed -n '1,120p' "$scratch/core-worktree-drift.log" >&2
  exit 1
fi
echo "  refused physical engine drift despite hostile core.worktree"

# Git status applies repository-local clean filters. Prove that an attacker
# cannot use .git/info/attributes plus a filter that returns the HEAD payload to
# make modified physical engine bytes look clean to porcelain status.
git -C "$probe_repo" config --unset core.worktree
git -C "$probe_repo" show HEAD:src/Engine.fs >"$probe_repo/src/Engine.fs"
git -C "$probe_repo" config filter.constant.clean 'git show HEAD:src/Engine.fs'
printf '/src/Engine.fs filter=constant\n' >"$probe_repo/.git/info/attributes"
printf '\n// drift hidden by a repository-local clean filter\n' \
  >>"$probe_repo/src/Engine.fs"
if [ -n "$(git -C "$probe_repo" status --short -- src/Engine.fs)" ]; then
  echo "FAIL: clean-filter attack precondition did not hide the engine drift" >&2
  exit 1
fi
probe_output=$probe_repo/clean-filter-drift-evidence
set +e
FOGELL_JENKINS_URL=http://127.0.0.1:1 \
  "$probe_repo/scripts/run-fg037-step-ceiling-probe.sh" "$probe_output" \
  >"$scratch/clean-filter-drift.log" 2>&1
probe_rc=$?
set -e
if [ "$probe_rc" -ne 2 ] \
  || [ -e "$probe_output" ] \
  || ! grep -Fq "tracked regular-file raw bytes do not match index blob: src/Engine.fs" \
    "$scratch/clean-filter-drift.log"; then
  echo "FAIL: probe accepted physical engine drift hidden by a clean filter" >&2
  sed -n '1,120p' "$scratch/clean-filter-drift.log" >&2
  exit 1
fi
echo "  refused physical engine drift hidden by a repository-local clean filter"

echo "FG-037 proof PASS (14 semantic + 3 controller-identity + 11 collector/configuration + 2 manifest + 3 source-bundle rejection arms)"
