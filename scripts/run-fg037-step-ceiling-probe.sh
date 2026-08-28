#!/usr/bin/env bash
# FG-037. Live, intentionally asymmetric proof of the Jenkins step ceiling.
# This is not part of build-and-test: it needs the pinned Jenkins lab, and the
# 251/400 cases are supposed to diverge. Retain them as evidence, never as
# canonical compatibility cases or receipts.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <new-evidence-directory>" >&2
  exit 2
fi

physical_repo_root=$(pwd -P)

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
  GIT_GRAFT_FILE=/dev/null/fogell-no-grafts
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
  tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  scripts/check-fg037-jenkins-identity.sh \
  scripts/check-fg037-manifest.py \
  scripts/check-fg037-source-bundle.sh \
  "$source_signers_input" \
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

collector_snapshot=$(mktemp)
identity_checker_snapshot=$(mktemp)
manifest_checker_snapshot=$(mktemp)
source_bundle_checker_snapshot=$(mktemp)
allowed_signers_snapshot=$(mktemp)
trap 'rm -f "$collector_snapshot" "$identity_checker_snapshot" "$manifest_checker_snapshot" "$source_bundle_checker_snapshot" "$allowed_signers_snapshot"' EXIT
clean_git show HEAD:scripts/jenkins-workspace-v2.sh >"$collector_snapshot"
clean_git show HEAD:scripts/check-fg037-jenkins-identity.sh >"$identity_checker_snapshot"
clean_git show HEAD:scripts/check-fg037-manifest.py >"$manifest_checker_snapshot"
clean_git show HEAD:scripts/check-fg037-source-bundle.sh >"$source_bundle_checker_snapshot"
clean_git show "HEAD:$source_signers_input" >"$allowed_signers_snapshot"
if ! cmp -s "$collector_snapshot" scripts/jenkins-workspace-v2.sh \
  || ! cmp -s "$identity_checker_snapshot" scripts/check-fg037-jenkins-identity.sh \
  || ! cmp -s "$manifest_checker_snapshot" scripts/check-fg037-manifest.py \
  || ! cmp -s "$source_bundle_checker_snapshot" scripts/check-fg037-source-bundle.sh \
  || ! cmp -s "$allowed_signers_snapshot" "$source_signers_input"; then
  echo "REFUSED: load-bearing probe input changed after the clean-input check" >&2
  exit 2
fi
collector_sha=$(sha256sum "$collector_snapshot" | awk '{print $1}')
identity_checker_sha=$(sha256sum "$identity_checker_snapshot" | awk '{print $1}')
manifest_checker_sha=$(sha256sum "$manifest_checker_snapshot" | awk '{print $1}')
source_bundle_checker_sha=$(sha256sum "$source_bundle_checker_snapshot" | awk '{print $1}')
allowed_signers_sha=$(sha256sum "$allowed_signers_snapshot" | awk '{print $1}')

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
    || ! cmp -s "$collector_snapshot" scripts/jenkins-workspace-v2.sh \
    || ! cmp -s "$identity_checker_snapshot" scripts/check-fg037-jenkins-identity.sh \
    || ! cmp -s "$manifest_checker_snapshot" scripts/check-fg037-manifest.py \
    || ! cmp -s "$source_bundle_checker_snapshot" scripts/check-fg037-source-bundle.sh \
    || ! cmp -s "$allowed_signers_snapshot" "$source_signers_input"; then
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

python3 scripts/check-fg037-step-ceiling.py \
  --cases "$output/cases" --receipts "$output/receipts" >/dev/null 2>&1 && {
    echo "REFUSED: semantic checker accepted an empty receipt inventory" >&2
    exit 1
  }

cli_project=tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj
jenkins_251_console=$output/receipts/fg037-251-steps.jenkins-console.txt
export FOGELL_JENKINS_RAW_CONSOLE_JOB=diff-fg037-251-steps
export FOGELL_JENKINS_RAW_CONSOLE_BUILD=1
export FOGELL_JENKINS_RAW_CONSOLE_PATH=$jenkins_251_console

# Build the exact executable used below, including its transitive engine
# projects. A solution-default build followed by `dotnet run --no-build` left a
# stale-binary route if the CLI were ever removed from that solution.
dotnet build "$cli_project" -c Release --nologo --no-incremental >"$output/build.log" 2>&1

set +e
dotnet run --project "$cli_project" -c Release --no-build -- \
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

python3 scripts/check-fg037-step-ceiling.py \
  --cases "$output/cases" --receipts "$output/receipts" \
  --jenkins-core "$FOGELL_JENKINS_CORE" | tee "$output/semantic-check.log"

dotnet run --project "$cli_project" -c Release --no-build -- \
  --verify-seals "$output/receipts" >"$output/seal-verification.log" 2>&1

FG037_SOURCE_BUNDLE_CHECKER="$source_bundle_checker_snapshot" \
  scripts/prove-fg037-step-ceiling.sh "$output" \
  "$source_bundle_ref" "$source_prerequisite" "$source_head" "$source_head" "$source_tree" \
  >"$output/proof.log"

# The build, semantic checker and hostile proof above intentionally use the
# checkout. Revalidate every governed input and the exact commit/tree before
# making any provenance statement or manifest; an initial clean check alone
# cannot license evidence if the checkout changed during a long live run.
require_stable_inputs || exit $?

{
  echo "utc: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host: $(hostname)"
  echo "head: $source_head"
  echo "tree: $source_tree"
  echo "dotnet: $(dotnet --version)"
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
  echo "engine-build-project: $cli_project"
  echo "engine-build-configuration: Release"
  echo "engine-build-mode: --no-incremental (no stale output reuse)"
  echo "engine-build-pathspecs:"
  printf '  %s\n' "${engine_input_pathspecs[@]}"
  echo "probe-input-pathspecs:"
  printf '  %s\n' "${probe_input_pathspecs[@]}"
  echo ""
  echo "implementation-sha256:"
  sha256sum \
    tests/Fogell.Differential.Tests/Tests.fs \
    scripts/check-fg037-step-ceiling.py \
    scripts/prove-fg037-step-ceiling.sh \
    scripts/run-fg037-step-ceiling-probe.sh
  printf '%s  %s\n' "$identity_checker_sha" \
    "scripts/check-fg037-jenkins-identity.sh (executed HEAD snapshot)"
  printf '%s  %s\n' "$manifest_checker_sha" \
    "scripts/check-fg037-manifest.py (executed HEAD snapshot)"
  printf '%s  %s\n' "$source_bundle_checker_sha" \
    "scripts/check-fg037-source-bundle.sh (executed HEAD snapshot)"
  printf '%s  %s\n' "$allowed_signers_sha" \
    "$source_signers_input (executed HEAD snapshot)"
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
