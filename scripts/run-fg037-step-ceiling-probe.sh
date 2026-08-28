#!/usr/bin/env bash
# FG-037. Live, intentionally asymmetric proof of the Jenkins step ceiling.
# This is not part of build-and-test: it needs the pinned Jenkins lab, and the
# 251/400 cases are supposed to diverge. Retain them as evidence, never as
# canonical compatibility cases or receipts.
set -euo pipefail
PATH=/usr/local/bin:/usr/bin:/bin
export PATH

immutable_runner_fd=${FOGELL_FG037_IMMUTABLE_RUNNER_FD:-}
immutable_runner_path=
if [ -n "$immutable_runner_fd" ]; then
  immutable_runner_path=/proc/self/fd/$immutable_runner_fd
  if [[ ! $immutable_runner_fd =~ ^[0-9]+$ ]] \
    || [ -z "${FOGELL_FG037_PHYSICAL_REPO_ROOT:-}" ] \
    || [ "${BASH_SOURCE[0]}" != "$immutable_runner_path" ] \
    || [ ! -f "$immutable_runner_path" ] \
    || [[ $(readlink "$immutable_runner_path") != *" (deleted)" ]]; then
    echo "REFUSED: immutable FG-037 runner handoff is malformed" >&2
    exit 2
  fi
  cd "$FOGELL_FG037_PHYSICAL_REPO_ROOT"
else
  cd "$(dirname "${BASH_SOURCE[0]}")/.."
fi

unset FOGELL_FG037_IMMUTABLE_RUNNER_FD FOGELL_FG037_PHYSICAL_REPO_ROOT

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <new-evidence-directory>" >&2
  exit 2
fi

physical_repo_root=$(pwd -P)

# A real empty graft file suppresses legacy graft discovery without relying on
# an invalid parent path. It is created before the first Git operation and
# removed together with the later HEAD snapshots.
graft_file=$(mktemp)
collector_snapshot=
identity_checker_snapshot=
manifest_checker_snapshot=
source_bundle_checker_snapshot=
allowed_signers_snapshot=
dotnet_wrapper_snapshot=
source_workspace=
cleanup() {
  set +e
  rm -f -- "$graft_file"
  local snapshot
  for snapshot in "$collector_snapshot" "$identity_checker_snapshot" \
    "$manifest_checker_snapshot" "$source_bundle_checker_snapshot" \
    "$allowed_signers_snapshot" "$dotnet_wrapper_snapshot"; do
    if [ -n "$snapshot" ] \
      && { [ -z "$source_workspace" ] \
        || [[ $snapshot != "$source_workspace/"* ]]; }; then
      rm -f -- "$snapshot"
    fi
  done
  if [ -n "$source_workspace" ]; then
    if [[ $source_workspace == /run/fogell-fg037-source.* ]] \
      && [ -d "$source_workspace" ] && [ ! -L "$source_workspace" ] \
      && [ "$(stat -Lc %u -- "$source_workspace" 2>/dev/null)" = 0 ]; then
      /usr/bin/sudo -n /bin/chmod -R u+w -- "$source_workspace"
      /usr/bin/sudo -n /bin/rm -rf --one-file-system -- "$source_workspace"
    fi
  fi
}
trap cleanup EXIT

case "$1" in
  /*) output=$1 ;;
  *) output=$PWD/$1 ;;
esac

if [ -e "$output" ]; then
  echo "REFUSED: evidence directory already exists: $output" >&2
  exit 2
fi

: "${FOGELL_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FOGELL_JENKINS_CORE:=2.568.1}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"
source_signers_input=evidence/20260827T185436Z-fg037-step-ceiling/source/allowed_signers

# Every source-provenance operation must resolve this checkout, index and object
# store rather than a caller-selected repository, alternate, graft, replace ref
# or quarantine. Keep this denylist aligned with the isolated bundle checker.
clean_git_env=(
  -u GIT_ALTERNATE_OBJECT_DIRECTORIES
  -u GIT_CONFIG
  -u GIT_CONFIG_PARAMETERS
  -u GIT_CONFIG_COUNT
  -u GIT_OBJECT_DIRECTORY
  -u GIT_DIR
  -u GIT_WORK_TREE
  -u GIT_IMPLICIT_WORK_TREE
  -u GIT_INDEX_FILE
  -u GIT_LITERAL_PATHSPECS
  -u GIT_GLOB_PATHSPECS
  -u GIT_NOGLOB_PATHSPECS
  -u GIT_ICASE_PATHSPECS
  -u GIT_NAMESPACE
  -u GIT_QUARANTINE_PATH
  -u GIT_REPLACE_REF_BASE
  -u GIT_PREFIX
  -u GIT_SHALLOW_FILE
  -u GIT_COMMON_DIR
  GIT_CONFIG_NOSYSTEM=1
  GIT_CONFIG_GLOBAL=/dev/null
  "GIT_GRAFT_FILE=$graft_file"
  GIT_NO_REPLACE_OBJECTS=1
)
clean_git() {
  env "${clean_git_env[@]}" git \
    -C "$physical_repo_root" --work-tree="$physical_repo_root" \
    -c advice.graftFileDeprecated=false "$@"
}

if [ "$FOGELL_JENKINS_CORE" != "2.568.1" ]; then
  echo "REFUSED: FG-037 evidence is specified against Jenkins 2.568.1" >&2
  exit 2
fi

# A dirty execution engine or evidence collector would make the receipt's HEAD
# identity false. Tests and docs may be uncommitted while the slice is developed;
# engine, differential-harness, probe/checker and repository build-policy changes
# may not be. The glob pathspecs deliberately include FUTURE root-level
# Directory.Build.* / Directory.Packages.* candidates, tracked or untracked.
for required in \
  Fogell.slnx \
  global.json \
  Directory.Build.props \
  Directory.Build.targets \
  Directory.Packages.props \
  tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  scripts/check-fg037-jenkins-identity.sh \
  scripts/check-fg037-manifest.py \
  scripts/check-fg037-source-bundle.sh \
  "$source_signers_input" \
  scripts/fg037-controlled-dotnet.sh \
  scripts/jenkins-workspace-v2.sh; do
  if [ ! -f "$required" ]; then
    echo "REFUSED: required FG-037 engine/build input is absent: $required" >&2
    exit 2
  fi
done

policy_input_pathspecs=(
  ':(top,glob)Directory.Build.*'
  ':(top,glob)Directory.Packages.*'
  ':(top,icase)nuget.config'
)

engine_input_pathspecs=(
  .gitignore
  Fogell.slnx
  global.json
  "${policy_input_pathspecs[@]}"
  src
  tools
)

probe_input_pathspecs=(
  scripts/check-fg037-jenkins-identity.sh
  scripts/check-fg037-manifest.py
  scripts/check-fg037-source-bundle.sh
  scripts/check-fg037-step-ceiling.py
  scripts/fg037-controlled-dotnet.sh
  scripts/jenkins-workspace-v2.sh
  scripts/prove-fg037-step-ceiling.sh
  scripts/run-fg037-step-ceiling-probe.sh
  "$source_signers_input"
)

clean_input_status() {
  local tracked_status
  local untracked_status
  local hidden_index_flags
  local policy_filesystem_status=
  local candidate
  local tracked_candidate

  # Do not trust a repository-local/global exclude file or fsmonitor response to
  # hide an input. Only committed .gitignore rules may suppress generated files;
  # .gitignore itself and any nested copy under src/tools are governed inputs.
  tracked_status=$(clean_git \
    -c core.fsmonitor=false -c core.untrackedCache=false \
    -c core.fileMode=true -c core.symlinks=true \
    status --porcelain=v1 --untracked-files=no -- \
    "${engine_input_pathspecs[@]}" "${probe_input_pathspecs[@]}")
  untracked_status=$(clean_git ls-files --others \
    --exclude-per-directory=.gitignore -- \
    "${engine_input_pathspecs[@]}" "${probe_input_pathspecs[@]}")
  hidden_index_flags=$(clean_git ls-files -v -- \
    "${engine_input_pathspecs[@]}" "${probe_input_pathspecs[@]}" \
    | LC_ALL=C awk '$1 ~ /^[a-zS]/ { print "INDEX-FLAG " $0 }')

  # MSBuild/NuGet discover these root names from the physical filesystem. Walk
  # that namespace directly so even a committed .gitignore cannot hide a local
  # input that the build would consume but the source bundle would omit.
  for candidate in Directory.Build.* Directory.Packages.* \
    [Nn][Uu][Gg][Ee][Tt].[Cc][Oo][Nn][Ff][Ii][Gg]; do
    [ -e "$candidate" ] || [ -L "$candidate" ] || continue
    tracked_candidate=$(clean_git ls-files -- ":(top,literal)$candidate")
    if [ "$tracked_candidate" != "$candidate" ]; then
      policy_filesystem_status+="UNTRACKED-POLICY $candidate"$'\n'
    fi
  done

  [ -z "$tracked_status" ] || printf '%s\n' "$tracked_status"
  [ -z "$untracked_status" ] \
    || printf '?? %s\n' "$untracked_status"
  [ -z "$hidden_index_flags" ] || printf '%s\n' "$hidden_index_flags"
  [ -z "$policy_filesystem_status" ] \
    || printf '%s' "$policy_filesystem_status"
}

require_raw_tracked_inputs() {
  local candidate_root=${1:-$physical_repo_root}
  local raw_index
  local audit_rc=0
  raw_index=$(mktemp)
  clean_git ls-files --stage -z -- \
    "${engine_input_pathspecs[@]}" "${probe_input_pathspecs[@]}" \
    >"$raw_index" || audit_rc=$?
  if [ "$audit_rc" -eq 0 ]; then
    python3 - "$candidate_root" "$raw_index" <<'PY' || audit_rc=$?
import hashlib
import os
import stat
import sys


def refuse(message: str) -> None:
    print(f"RAW-IDENTITY {message}", file=sys.stderr)
    raise SystemExit(1)


def unchanged(before: os.stat_result, after: os.stat_result, label: str) -> None:
    fields = ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns")
    if tuple(getattr(before, field) for field in fields) != tuple(
        getattr(after, field) for field in fields
    ):
        refuse(f"tracked input changed while it was read: {label}")


def blob_id(payload: bytes) -> bytes:
    header = b"blob " + str(len(payload)).encode("ascii") + b"\0"
    return hashlib.sha1(header + payload).hexdigest().encode("ascii")


root = os.fsencode(sys.argv[1])
with open(sys.argv[2], "rb") as handle:
    records = handle.read().split(b"\0")

for record in records:
    if not record:
        continue
    header, separator, raw_path = record.partition(b"\t")
    fields = header.split()
    label = os.fsdecode(raw_path)
    if not separator or len(fields) != 3:
        refuse("tracked index entry has an unexpected shape")
    raw_mode, expected_blob, raw_stage = fields
    if raw_stage != b"0":
        refuse(f"tracked input has a non-stage-0 index entry: {label}")

    path = os.path.join(root, raw_path)
    try:
        before = os.lstat(path)
        if raw_mode in (b"100644", b"100755"):
            if not stat.S_ISREG(before.st_mode):
                refuse(f"tracked regular input is not a regular file: {label}")
            flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
            descriptor = os.open(path, flags)
            try:
                opened = os.fstat(descriptor)
                if not stat.S_ISREG(opened.st_mode) or (
                    before.st_dev,
                    before.st_ino,
                ) != (opened.st_dev, opened.st_ino):
                    refuse(f"tracked input changed while it was opened: {label}")
                with os.fdopen(descriptor, "rb", closefd=False) as handle:
                    payload = handle.read()
                unchanged(opened, os.fstat(descriptor), label)
            finally:
                os.close(descriptor)
            executable = bool(opened.st_mode & 0o111)
            if executable != (raw_mode == b"100755"):
                refuse(f"tracked regular-file executable mode does not match index: {label}")
            if blob_id(payload) != expected_blob:
                refuse(f"tracked regular-file raw bytes do not match index blob: {label}")
        elif raw_mode == b"120000":
            if not stat.S_ISLNK(before.st_mode):
                refuse(f"tracked symlink input is not a symlink: {label}")
            payload = os.readlink(path)
            unchanged(before, os.lstat(path), label)
            if blob_id(payload) != expected_blob:
                refuse(f"tracked symlink target bytes do not match index blob: {label}")
        else:
            refuse(f"tracked input has an unsupported index mode {os.fsdecode(raw_mode)}: {label}")
    except OSError as error:
        refuse(f"tracked input cannot be read: {label}: {error.strerror}")
PY
  fi
  rm -f -- "$raw_index"
  return "$audit_rc"
}

source_head=$(clean_git rev-parse HEAD)
source_tree=$(clean_git rev-parse 'HEAD^{tree}')
if [ "$(realpath "$(clean_git rev-parse --show-toplevel)")" \
  != "$physical_repo_root" ]; then
  echo "REFUSED: Git did not resolve the physical probe checkout" >&2
  exit 2
fi
input_status=$(clean_input_status)
if [ -n "$input_status" ]; then
  echo "REFUSED: engine/build or probe inputs differ from HEAD; commit or identify them before probing" >&2
  printf '%s\n' "$input_status" >&2
  exit 2
fi
if ! require_raw_tracked_inputs; then
  echo "REFUSED: governed input bytes or modes differ from the HEAD index" >&2
  exit 2
fi

# Bash may read a script incrementally, so bookend checks cannot make this
# physical file immutable during a long probe. Before any evidence or network
# activity, re-exec the exact HEAD blob from a read-only temporary file. The
# child validates that blob again and owns cleanup of both bootstrap files.
if [ -z "$immutable_runner_fd" ]; then
  immutable_runner_tmp=$(mktemp)
  clean_git show HEAD:scripts/run-fg037-step-ceiling-probe.sh \
    >"$immutable_runner_tmp"
  chmod a-w,u+x "$immutable_runner_tmp"
  exec 9<"$immutable_runner_tmp"
  rm -f -- "$immutable_runner_tmp" "$graft_file"
  immutable_runner_env=(
    "PATH=$PATH"
    "HOME=${HOME:?}"
    LANG=C.UTF-8
    LC_ALL=C.UTF-8
    "FOGELL_JENKINS_URL=$FOGELL_JENKINS_URL"
    "FOGELL_JENKINS_CORE=$FOGELL_JENKINS_CORE"
    "FOGELL_JENKINS_HOST=$FOGELL_JENKINS_HOST"
    "FOGELL_JENKINS_CONTAINER=$FOGELL_JENKINS_CONTAINER"
    "FOGELL_JENKINS_WORKSPACE_CMD=${FOGELL_JENKINS_WORKSPACE_CMD:-}"
    "FOGELL_JENKINS_WIPE_CMD=${FOGELL_JENKINS_WIPE_CMD:-}"
    "FOGELL_JENKINS_ENV_CMD=${FOGELL_JENKINS_ENV_CMD:-}"
    "FOGELL_JENKINS_GIT_VERSION_CMD=${FOGELL_JENKINS_GIT_VERSION_CMD:-}"
    "FOGELL_JENKINS_RAW_CONSOLE_JOB=${FOGELL_JENKINS_RAW_CONSOLE_JOB:-}"
    "FOGELL_JENKINS_RAW_CONSOLE_BUILD=${FOGELL_JENKINS_RAW_CONSOLE_BUILD:-}"
    "FOGELL_JENKINS_RAW_CONSOLE_PATH=${FOGELL_JENKINS_RAW_CONSOLE_PATH:-}"
  )
  if [ -n "${SSH_AUTH_SOCK:-}" ]; then
    immutable_runner_env+=("SSH_AUTH_SOCK=$SSH_AUTH_SOCK")
  fi
  exec /usr/bin/env -i "${immutable_runner_env[@]}" \
    FOGELL_FG037_IMMUTABLE_RUNNER_FD=9 \
    "FOGELL_FG037_PHYSICAL_REPO_ROOT=$physical_repo_root" \
    /bin/bash /proc/self/fd/9 "$@"
fi
runner_head_snapshot=$(mktemp)
clean_git show HEAD:scripts/run-fg037-step-ceiling-probe.sh \
  >"$runner_head_snapshot"
if ! cmp -s "$runner_head_snapshot" "$immutable_runner_path"; then
  rm -f -- "$runner_head_snapshot"
  echo "REFUSED: immutable FG-037 runner does not match HEAD" >&2
  exit 2
fi
rm -f -- "$runner_head_snapshot"

if ! clean_git show-ref --verify --quiet refs/heads/main; then
  echo "REFUSED: local main is required to anchor the thin source bundle" >&2
  exit 2
fi
if ! source_prerequisite=$(clean_git merge-base "$source_head" main); then
  echo "REFUSED: source HEAD and local main have no common prerequisite" >&2
  exit 2
fi
if [ "$source_prerequisite" = "$source_head" ]; then
  if ! source_prerequisite=$(clean_git rev-parse "$source_head^"); then
    echo "REFUSED: source HEAD has no parent available as a bundle prerequisite" >&2
    exit 2
  fi
fi
if ! clean_git merge-base --is-ancestor "$source_prerequisite" main; then
  echo "REFUSED: source-bundle prerequisite is not retained by local main" >&2
  exit 2
fi
source_bundle_ref=HEAD

# Build and execute only bytes exported from the recorded HEAD. The physical
# checkout remains under bookend audit, but it is never the compiler's source:
# a concurrent edit-and-restore there cannot influence the measured executable.
# Compilation runs as a distinct unprivileged identity inside the private
# workspace. Its outputs are frozen root-owned before the ordinary probe
# identity executes them, so neither identity can rewrite the measured binary.
run_mode=$(/usr/bin/stat -Lc %a -- /run 2>/dev/null || true)
if [ ! -d /run ] || [ -L /run ] \
  || [ "$(/usr/bin/realpath -- /run 2>/dev/null)" != /run ] \
  || [ "$(/usr/bin/stat -Lc %u -- /run 2>/dev/null)" != 0 ] \
  || [[ ! $run_mode =~ ^[0-7]+$ ]] \
  || (( (8#$run_mode & 0022) != 0 )); then
  echo "REFUSED: FG-037 requires canonical root-owned non-writable /run" >&2
  exit 2
fi
if [ ! -x /usr/bin/sudo ] || ! /usr/bin/sudo -n /bin/true; then
  echo "REFUSED: FG-037 requires noninteractive root workspace setup" >&2
  exit 2
fi
if [ ! -x /usr/bin/systemd-run ]; then
  echo "REFUSED: FG-037 requires systemd DynamicUser build isolation" >&2
  exit 2
fi
source_workspace=$(/usr/bin/sudo -n /usr/bin/mktemp -d \
  /run/fogell-fg037-source.XXXXXXXXXX)
if [[ $source_workspace != /run/fogell-fg037-source.* ]] \
  || [ ! -d "$source_workspace" ] || [ -L "$source_workspace" ] \
  || [ "$(stat -Lc %u -- "$source_workspace")" != 0 ]; then
  echo "REFUSED: FG-037 root-owned source workspace is malformed" >&2
  exit 2
fi
source_snapshot=$source_workspace/source
/usr/bin/sudo -n /usr/bin/install -d -o root -g root -m 0755 \
  "$source_snapshot"
clean_git archive --format=tar "$source_head" \
  | /usr/bin/sudo -n /bin/tar --no-same-owner --no-same-permissions \
      -xf - -C "$source_snapshot"
/usr/bin/sudo -n /bin/chmod -R a-w,u+rwX,go+rX -- "$source_snapshot"
/usr/bin/sudo -n /bin/chmod 0755 -- "$source_workspace"
root_export_violation=$(/usr/bin/find "$source_snapshot" -xdev \
  \( ! -user root -o \( ! -type l -perm /022 \) \) -print -quit)
if [ -n "$root_export_violation" ]; then
  echo "REFUSED: exported HEAD contains a non-root-owned or group/other-writable path: $root_export_violation" >&2
  exit 2
fi
if ! require_raw_tracked_inputs "$source_snapshot"; then
  echo "REFUSED: exported HEAD source bytes or modes differ from the recorded index" >&2
  exit 2
fi
build_output_dirs=()
while IFS= read -r -d '' project; do
  project_dir=${project%/*}
  if [ -e "$project_dir/bin" ] || [ -L "$project_dir/bin" ] \
    || [ -e "$project_dir/obj" ] || [ -L "$project_dir/obj" ]; then
    echo "REFUSED: exported HEAD contains a pre-existing project bin/obj path" >&2
    exit 2
  fi
  /usr/bin/sudo -n /usr/bin/install -d -o root -g root \
    -m 0700 "$project_dir/bin" "$project_dir/obj"
  build_output_dirs+=("$project_dir/bin" "$project_dir/obj")
done < <(/usr/bin/find "$source_snapshot/src" "$source_snapshot/tools" \
  -type f -name '*.fsproj' -print0)
for output_dir in "${build_output_dirs[@]}"; do
  if [ ! -d "$output_dir" ] || [ -L "$output_dir" ] \
    || [[ $(/usr/bin/realpath "$output_dir") != "$source_snapshot"/* ]]; then
    echo "REFUSED: project build output directory escapes the exported HEAD" >&2
    exit 2
  fi
done
workspace_writable_dirs=(
  "$source_workspace/dotnet-home"
  "$source_workspace/tmp"
  "$source_workspace/nuget-packages"
  "$source_workspace/nuget-http-cache"
  "$source_workspace/dotnet-bundle-cache"
  "$source_workspace/dotnet-cwd"
)
/usr/bin/sudo -n /usr/bin/install -d -o root -g root \
  -m 0700 -- "${workspace_writable_dirs[@]}"
for writable_dir in "${workspace_writable_dirs[@]}"; do
  if [ ! -d "$writable_dir" ] || [ -L "$writable_dir" ] \
    || [[ $(/usr/bin/realpath "$writable_dir") \
      != "$(/usr/bin/realpath "$source_workspace")/"* ]]; then
    echo "REFUSED: controlled build writable directory escapes its private workspace" >&2
    exit 2
  fi
done
# The root-owned workspace and non-writable root-owned `/run` parent prevent a
# process running as the probe UID from replacing the workspace or source entry.
# systemd assigns a run-exclusive dynamic UID for compilation and freezes the
# output/cache trees root-owned in ExecStopPost before releasing that identity.
/usr/bin/sudo -n /bin/chmod 0755 -- "$source_workspace"

collector_snapshot=$source_snapshot/scripts/jenkins-workspace-v2.sh
identity_checker_snapshot=$source_snapshot/scripts/check-fg037-jenkins-identity.sh
manifest_checker_snapshot=$source_snapshot/scripts/check-fg037-manifest.py
source_bundle_checker_snapshot=$source_snapshot/scripts/check-fg037-source-bundle.sh
allowed_signers_snapshot=$source_snapshot/$source_signers_input
dotnet_wrapper_snapshot=$source_snapshot/scripts/fg037-controlled-dotnet.sh
if ! cmp -s "$collector_snapshot" scripts/jenkins-workspace-v2.sh \
  || ! cmp -s "$identity_checker_snapshot" scripts/check-fg037-jenkins-identity.sh \
  || ! cmp -s "$manifest_checker_snapshot" scripts/check-fg037-manifest.py \
  || ! cmp -s "$source_bundle_checker_snapshot" scripts/check-fg037-source-bundle.sh \
  || ! cmp -s "$allowed_signers_snapshot" "$source_signers_input" \
  || ! cmp -s "$dotnet_wrapper_snapshot" scripts/fg037-controlled-dotnet.sh; then
  echo "REFUSED: load-bearing probe input changed after the clean-input check" >&2
  exit 2
fi
collector_sha=$(sha256sum "$collector_snapshot" | awk '{print $1}')
identity_checker_sha=$(sha256sum "$identity_checker_snapshot" | awk '{print $1}')
manifest_checker_sha=$(sha256sum "$manifest_checker_snapshot" | awk '{print $1}')
source_bundle_checker_sha=$(sha256sum "$source_bundle_checker_snapshot" | awk '{print $1}')
allowed_signers_sha=$(sha256sum "$allowed_signers_snapshot" | awk '{print $1}')
dotnet_wrapper_sha=$(sha256sum "$dotnet_wrapper_snapshot" | awk '{print $1}')

# Containment starts before Bash reads the wrapper. In particular, BASH_ENV and
# exported shell functions must not execute before the wrapper's own env -i.
controlled_dotnet() {
  local mode=$1
  shift
  local -a entry_env=("PATH=/usr/local/bin:/usr/bin:/bin")
  if [ "$mode" = exec ]; then
    entry_env+=(
      "HOME=${HOME:?}"
      "FOGELL_JENKINS_WORKSPACE_CMD=${FOGELL_JENKINS_WORKSPACE_CMD:-}"
      "FOGELL_JENKINS_WIPE_CMD=${FOGELL_JENKINS_WIPE_CMD:-}"
      "FOGELL_JENKINS_ENV_CMD=${FOGELL_JENKINS_ENV_CMD:-}"
      "FOGELL_JENKINS_GIT_VERSION_CMD=${FOGELL_JENKINS_GIT_VERSION_CMD:-}"
      "FOGELL_JENKINS_RAW_CONSOLE_JOB=${FOGELL_JENKINS_RAW_CONSOLE_JOB:-}"
      "FOGELL_JENKINS_RAW_CONSOLE_BUILD=${FOGELL_JENKINS_RAW_CONSOLE_BUILD:-}"
      "FOGELL_JENKINS_RAW_CONSOLE_PATH=${FOGELL_JENKINS_RAW_CONSOLE_PATH:-}"
    )
    if [ -n "${SSH_AUTH_SOCK:-}" ]; then
      entry_env+=("SSH_AUTH_SOCK=$SSH_AUTH_SOCK")
    fi
  fi
  if [ "$mode" = build ]; then
    local dynamic_paths=
    # `$USER` must expand only inside systemd's root ExecStartPre shell, after
    # DynamicUser has allocated the run-exclusive account.
    # shellcheck disable=SC2016
    local dynamic_prepare='/usr/bin/chown -R "$USER:$USER"'
    local dynamic_path
    for dynamic_path in "${build_output_dirs[@]}" \
      "${workspace_writable_dirs[@]}"; do
      if [[ ! $dynamic_path =~ ^/run/fogell-fg037-source\.[A-Za-z0-9]+/[A-Za-z0-9._/-]+$ ]]; then
        echo "REFUSED: dynamic build path has an unsafe shape" >&2
        return 2
      fi
      dynamic_paths+="${dynamic_paths:+ }$dynamic_path"
      dynamic_prepare+=" $dynamic_path"
    done
    /usr/bin/sudo -n /usr/bin/systemd-run --pipe --wait --collect --quiet \
      -p DynamicUser=yes -p PrivateTmp=yes -p NoNewPrivileges=yes \
      -p RestrictSUIDSGID=yes -p ProtectSystem=strict -p ProtectHome=yes \
      -p KillMode=control-group \
      -p "ReadOnlyPaths=$source_snapshot" \
      -p "ReadWritePaths=$dynamic_paths" \
      -p "ExecStartPre=+/bin/sh -c '$dynamic_prepare'" \
      -p "ExecStopPost=+/usr/bin/chown -R root:root $dynamic_paths" \
      -p "ExecStopPost=+/usr/bin/chmod -R a-w,u+rwX,go+rX $dynamic_paths" \
      /usr/bin/env -i "${entry_env[@]}" \
      /bin/bash --noprofile --norc "$dotnet_wrapper_snapshot" \
      "$mode" "$source_workspace" "$@"
  else
    /usr/bin/env -i "${entry_env[@]}" \
      /bin/bash --noprofile --norc "$dotnet_wrapper_snapshot" \
      "$mode" "$source_workspace" "$@"
  fi
}

require_stable_inputs() {
  local current_head
  local current_tree
  local current_status

  current_head=$(clean_git rev-parse HEAD)
  current_tree=$(clean_git rev-parse 'HEAD^{tree}')
  current_status=$(clean_input_status)
  if [ "$current_head" != "$source_head" ] \
    || [ "$current_tree" != "$source_tree" ] \
    || [ -n "$current_status" ] \
    || ! require_raw_tracked_inputs \
    || ! require_raw_tracked_inputs "$source_snapshot" \
    || ! cmp -s "$collector_snapshot" scripts/jenkins-workspace-v2.sh \
    || ! cmp -s "$identity_checker_snapshot" scripts/check-fg037-jenkins-identity.sh \
    || ! cmp -s "$manifest_checker_snapshot" scripts/check-fg037-manifest.py \
    || ! cmp -s "$source_bundle_checker_snapshot" scripts/check-fg037-source-bundle.sh \
    || ! cmp -s "$allowed_signers_snapshot" "$source_signers_input" \
    || ! cmp -s "$dotnet_wrapper_snapshot" scripts/fg037-controlled-dotnet.sh; then
    echo "REFUSED: load-bearing HEAD or probe inputs changed while evidence was being produced" >&2
    [ -z "$current_status" ] || printf '%s\n' "$current_status" >&2
    return 2
  fi
}

jenkins_api_url=${FOGELL_JENKINS_URL%/}/api/json
printf -v jenkins_container_q '%q' "$FOGELL_JENKINS_CONTAINER"
# The quoted container word intentionally expands on HeMan for Luigi's shell.
# shellcheck disable=SC2029
actual_core=$(ssh -- "$FOGELL_JENKINS_HOST" \
  "podman exec $jenkins_container_q java -jar /usr/share/jenkins/jenkins.war --version" \
  2>/dev/null)
if [ "$actual_core" != "$FOGELL_JENKINS_CORE" ]; then
  echo "REFUSED: live Jenkins core is $actual_core, expected $FOGELL_JENKINS_CORE" >&2
  exit 2
fi

observe_endpoint() {
  curl -fsS --max-time 10 --max-redirs 0 -o /dev/null \
    -w '%header{x-jenkins}\t%header{x-jenkins-session}' "$jenkins_api_url"
}

observe_container() {
  # The validated/quoted container argument intentionally expands on HeMan;
  # the resulting shell word and quoted curl format are interpreted on Luigi.
  # shellcheck disable=SC2029
  ssh -- "$FOGELL_JENKINS_HOST" \
    "podman exec $jenkins_container_q curl -fsS --max-time 10 --max-redirs 0 -o /dev/null -w '%header{x-jenkins}\\t%header{x-jenkins-session}' http://127.0.0.1:8080/api/json"
}

endpoint_identity=$(observe_endpoint) || {
  echo "REFUSED: build endpoint did not expose Jenkins identity" >&2
  exit 2
}
container_identity=$(observe_container) || {
  echo "REFUSED: selected Jenkins container did not expose HTTP identity" >&2
  exit 2
}
jenkins_session=$(bash "$identity_checker_snapshot" \
  "$FOGELL_JENKINS_CORE" "$actual_core" \
  "$endpoint_identity" "$container_identity") || exit $?

# shellcheck source=scripts/jenkins-workspace-v2.sh disable=SC1091
source "$collector_snapshot"
fogell_configure_jenkins_workspace_v2 "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER"
jenkins_container_remote_q=$(fogell_quote_posix_shell_v2 "$FOGELL_JENKINS_CONTAINER")
jenkins_env_remote_command="podman exec ${jenkins_container_remote_q} env"
jenkins_git_remote_command="podman exec ${jenkins_container_remote_q} git --version"
FOGELL_JENKINS_ENV_CMD=$(
  fogell_jenkins_ssh_command_v2 "$FOGELL_JENKINS_HOST" "$jenkins_env_remote_command"
)
FOGELL_JENKINS_GIT_VERSION_CMD=$(
  fogell_jenkins_ssh_command_v2 "$FOGELL_JENKINS_HOST" "$jenkins_git_remote_command"
)
export FOGELL_JENKINS_ENV_CMD FOGELL_JENKINS_GIT_VERSION_CMD

mkdir -p "$output/cases" "$output/receipts" "$output/source"

source_bundle=$output/source/fg037-measured-source.bundle
clean_git bundle create "$source_bundle" HEAD "^$source_prerequisite"
cp "$allowed_signers_snapshot" "$output/source/allowed_signers"
bash "$source_bundle_checker_snapshot" \
  "$source_bundle" "$output/source/allowed_signers" "$source_bundle_ref" \
  "$source_prerequisite" "$source_head" "$source_head" "$source_tree" >/dev/null

python3 - "$output/cases" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
for count in (250, 251, 400):
    steps = ["        sh 'printf reached > reached-agent.txt'"]
    steps.extend(f"        echo 'FG037-{i:03d}'" for i in range(2, count + 1))
    source = (
        "pipeline {\n"
        "  agent any\n"
        "  stages {\n"
        "    stage('boundary') {\n"
        "      steps {\n"
        + "\n".join(steps)
        + "\n      }\n"
        "    }\n"
        "  }\n"
        "}\n"
    )
    (root / f"fg037-{count}-steps.Jenkinsfile").write_text(source, encoding="utf-8")
PY

python3 "$source_snapshot/scripts/check-fg037-step-ceiling.py" \
  --cases "$output/cases" --receipts "$output/receipts" >/dev/null 2>&1 && {
    echo "REFUSED: semantic checker accepted an empty receipt inventory" >&2
    exit 1
  }

cli_project_relative=tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj
cli_project=$source_snapshot/$cli_project_relative
cli_assembly=$source_snapshot/tools/Fogell.Differential.Cli/bin/Release/net10.0/fogell-diff.dll
jenkins_251_console=$output/receipts/fg037-251-steps.jenkins-console.txt
export FOGELL_JENKINS_RAW_CONSOLE_JOB=diff-fg037-251-steps
export FOGELL_JENKINS_RAW_CONSOLE_BUILD=1
export FOGELL_JENKINS_RAW_CONSOLE_PATH=$jenkins_251_console

# Build the exact executable used below, including its transitive engine
# projects. A solution-default build followed by `dotnet run --no-build` left a
# stale-binary route if the CLI were ever removed from that solution.
controlled_dotnet build \
  build "$cli_project" -c Release --nologo --no-incremental \
  -m:1 \
  >"$output/build.log" 2>&1
if [ ! -f "$cli_assembly" ] || [ -L "$cli_assembly" ]; then
  echo "REFUSED: controlled build did not produce the expected real CLI assembly" >&2
  exit 2
fi
output_violation=$(/usr/bin/find "${build_output_dirs[@]}" -xdev \
  \( ! -user root -o -type l -o \( ! -type d ! -type f \) \
    -o -perm /022 \) -print -quit)
if [ -n "$output_violation" ]; then
  echo "REFUSED: controlled build output is not root-owned and read-only to the probe UID: $output_violation" >&2
  exit 2
fi
frozen_output_digest() {
  /usr/bin/find "${build_output_dirs[@]}" -xdev -type f -print0 \
    | /usr/bin/sort -z \
    | /usr/bin/xargs -0 -r /usr/bin/sha256sum --zero \
    | /usr/bin/sha256sum \
    | /usr/bin/awk '{print $1}'
}
frozen_output_sha=$(frozen_output_digest)
require_frozen_outputs() {
  local violation
  violation=$(/usr/bin/find "${build_output_dirs[@]}" -xdev \
    \( ! -user root -o -type l -o \( ! -type d ! -type f \) \
      -o -perm /022 \) -print -quit)
  if [ -n "$violation" ] \
    || [ "$(frozen_output_digest)" != "$frozen_output_sha" ]; then
    echo "REFUSED: frozen controlled build output changed after attestation" >&2
    return 2
  fi
}
runtime_tmp=$source_workspace/runtime-tmp
/usr/bin/sudo -n /usr/bin/install -d -o "$(/usr/bin/id -u)" \
  -g "$(/usr/bin/id -g)" -m 0700 -- "$runtime_tmp"

set +e
require_frozen_outputs || exit $?
controlled_dotnet exec "$cli_assembly" \
  "$FOGELL_JENKINS_URL" "$FOGELL_JENKINS_CORE" "$output/receipts" \
  "$output/cases/fg037-250-steps.Jenkinsfile" \
  "$output/cases/fg037-251-steps.Jenkinsfile" \
  "$output/cases/fg037-400-steps.Jenkinsfile" \
  >"$output/differential.log" 2>&1
run_rc=$?
set -e

# The ordinary CLI must fail because two cases are intentional divergences. A
# generic non-zero is not evidence; the semantic checker below decides whether
# it was the exact 250-pass / 251+400 pre-effect Jenkins-failure boundary.
if [ "$run_rc" -ne 1 ]; then
  echo "FG-037 probe FAIL: differential CLI exited $run_rc, expected 1" >&2
  sed -n '1,240p' "$output/differential.log" >&2
  exit 1
fi

# The comparison receipt deliberately normalises engine-specific diagnostic
# wording, so it cannot by itself distinguish this boundary from an unrelated
# pre-effect infrastructure failure. The CLI's exact-build export above retains
# each confirmed attempt before its disposable Jenkins job is deleted; the last
# retry atomically replaces the prior attempt. Its ArrayUtil.createArray
# NoSuchMethodError carries the exact 251-argument cause, which the semantic
# checker below binds before the bundle can be sealed.

endpoint_identity=$(observe_endpoint) || {
  echo "REFUSED: build endpoint identity disappeared after the live probe" >&2
  exit 2
}
container_identity=$(observe_container) || {
  echo "REFUSED: selected container identity disappeared after the live probe" >&2
  exit 2
}
bash "$identity_checker_snapshot" \
  "$FOGELL_JENKINS_CORE" "$actual_core" \
  "$endpoint_identity" "$container_identity" "$jenkins_session" >/dev/null \
  || exit $?

python3 "$source_snapshot/scripts/check-fg037-step-ceiling.py" \
  --cases "$output/cases" --receipts "$output/receipts" \
  --jenkins-core "$FOGELL_JENKINS_CORE" | tee "$output/semantic-check.log"

require_frozen_outputs || exit $?
controlled_dotnet exec "$cli_assembly" \
  --verify-seals "$output/receipts" >"$output/seal-verification.log" 2>&1
require_frozen_outputs || exit $?

FG037_PUBLICATION_REPO_ROOT="$physical_repo_root" \
  FG037_SOURCE_BUNDLE_CHECKER="$source_bundle_checker_snapshot" \
  "$source_snapshot/scripts/prove-fg037-step-ceiling.sh" "$output" \
  "$source_bundle_ref" "$source_prerequisite" "$source_head" "$source_head" "$source_tree" \
  >"$output/proof.log"

# The build, semantic checker and hostile proof above use the read-only HEAD
# export. Revalidate the physical checkout too before making any provenance
# statement or manifest; an initial clean check alone cannot license evidence
# if the publication checkout changed during a long live run.
require_stable_inputs || exit $?

{
  echo "utc: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host: $(hostname)"
  echo "head: $source_head"
  echo "tree: $source_tree"
  echo "dotnet: $(controlled_dotnet version)"
  echo "jenkins-url: $FOGELL_JENKINS_URL"
  echo "jenkins-core: $actual_core (artifact, endpoint, and container-loopback agree)"
  echo "jenkins-session: $jenkins_session (endpoint and container-loopback agree before and after builds)"
  echo "jenkins-host: $FOGELL_JENKINS_HOST"
  echo "jenkins-container: $FOGELL_JENKINS_CONTAINER"
  echo -n "jenkins-image-name: "
  fogell_jenkins_podman_inspect_v2 \
    "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER" '{{.ImageName}}'
  echo -n "jenkins-image-id: "
  fogell_jenkins_podman_inspect_v2 \
    "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER" '{{.Image}}'
  echo "engine-build-input-status: clean against the recorded HEAD before and after the live run"
  echo "engine-build-project: $cli_project_relative (read-only export of recorded HEAD)"
  echo "engine-build-configuration: Release"
  echo "engine-build-mode: --no-incremental (no stale output reuse)"
  echo "engine-build-environment: env -i allowlist from scripts/fg037-controlled-dotnet.sh"
  echo "engine-build-pathspecs:"
  printf '  %s\n' "${engine_input_pathspecs[@]}"
  echo "probe-input-pathspecs:"
  printf '  %s\n' "${probe_input_pathspecs[@]}"
  echo ""
  echo "implementation-sha256:"
  sha256sum \
    "$source_snapshot/tests/Fogell.Differential.Tests/Tests.fs" \
    "$source_snapshot/scripts/check-fg037-step-ceiling.py" \
    "$source_snapshot/scripts/prove-fg037-step-ceiling.sh" \
    "$source_snapshot/scripts/run-fg037-step-ceiling-probe.sh" \
    | sed "s#$source_snapshot/##"
  printf '%s  %s\n' "$identity_checker_sha" \
    "scripts/check-fg037-jenkins-identity.sh (executed HEAD snapshot)"
  printf '%s  %s\n' "$manifest_checker_sha" \
    "scripts/check-fg037-manifest.py (executed HEAD snapshot)"
  printf '%s  %s\n' "$source_bundle_checker_sha" \
    "scripts/check-fg037-source-bundle.sh (executed HEAD snapshot)"
  printf '%s  %s\n' "$allowed_signers_sha" \
    "$source_signers_input (executed HEAD snapshot)"
  printf '%s  %s\n' "$dotnet_wrapper_sha" \
    "scripts/fg037-controlled-dotnet.sh (executed HEAD snapshot)"
  printf '%s  %s\n' "$collector_sha" \
    "scripts/jenkins-workspace-v2.sh (executed HEAD snapshot)"
  echo "source-bundle-ref: $source_bundle_ref"
  echo "source-bundle-prerequisite: $source_prerequisite (retained by local main)"
  echo "source-bundle-head: $source_head"
  echo "source-bundle-sha256: $(sha256sum "$source_bundle" | awk '{print $1}')"
  echo ""
  echo "worktree-status:"
  clean_git status --short
} >"$output/source-identity.txt"

{
  echo "# FG-037 retained step-ceiling evidence"
  echo ""
  echo "The exact 250-step control is tier-1 PROVEN on Jenkins 2.568.1 and Fogell."
  echo "The adjacent 251-step input and the 400-step input are deliberately DIVERGED:"
  echo "Jenkins fails with an empty workspace before the sentinel step, while Fogell"
  echo "succeeds, writes the sentinel, and emits every ordered marker. The retained raw"
  echo "251 console binds that refusal to its 251-argument ArrayUtil NoSuchMethodError."
  echo ""
  echo "These receipts are intentional capability differences. They must remain outside"
  echo "differential/receipts and are not part of the compatibility scorecard."
  echo ""
  echo "source/fg037-measured-source.bundle reconstructs the signed measured source"
  echo "from a prerequisite retained by local main; source/allowed_signers pins its signer."
} >"$output/README.md"

require_stable_inputs || exit $?

(
  cd "$output"
  manifest_tmp=$(mktemp) || exit 1
  trap 'rm -f "$manifest_tmp"' EXIT
  find . -type f ! -name manifest.sha256 -print0 \
    | LC_ALL=C sort -z \
    | xargs -0 sha256sum >"$manifest_tmp"
  mv "$manifest_tmp" manifest.sha256
  trap - EXIT
)

python3 "$manifest_checker_snapshot" "$output" >/dev/null

manifest_identity=$(sha256sum "$output/manifest.sha256" | awk '{print $1}')
echo "FG-037 retained evidence: $output"
echo "FG-037 manifest identity: $manifest_identity"
