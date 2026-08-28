#!/usr/bin/env bash
# FG-002/002b. Runs every differential case through both engines and seals
# receipts. Exits non-zero unless every case is FULLY proven.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

: "${FOGELL_JENKINS_URL:=http://127.0.0.1:18099}"
: "${FOGELL_JENKINS_CORE:=2.568.1}"
: "${FOGELL_JENKINS_HOST:=luigi}"
: "${FOGELL_JENKINS_CONTAINER:=jenkins-lab}"

# The five `withCredentials` receipts need the same fake values on BOTH sides.
# Without this a fresh shell simply has no store, those cases fail CLOSED, and the
# run reads as a code regression — which cost a full debugging cycle once. The path
# is exactly what provision-credentials.sh writes and prints an `export` line for.
: "${FOGELL_CREDENTIALS_FILE:=$PWD/.fogell-credentials.tsv}"
if [[ -f "$FOGELL_CREDENTIALS_FILE" ]]; then
  export FOGELL_CREDENTIALS_FILE
else
  echo "note: no credential store at $FOGELL_CREDENTIALS_FILE — withCredentials cases" \
       "will fail closed. Run scripts/provision-credentials.sh first." >&2
fi

# Jenkins does not share a filesystem with us, so the workspace is enumerated WHERE
# IT LIVES. FG-173's v2 wire format is strict and framed; Trace.collectRemote validates
# it before reducing it to the byte-compatible canonical file records plus tagged empty
# leaf records. GNU find's default -P policy is stated explicitly: directory symlinks are
# never followed, and a directory containing only a symlink is not physically empty.
read -r -d '' FOGELL_WORKSPACE_COLLECTOR_SOURCE <<'FOGELL_WORKSPACE_COLLECTOR' || true
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
FOGELL_WORKSPACE_COLLECTOR_B64=$(printf '%s' "$FOGELL_WORKSPACE_COLLECTOR_SOURCE" | base64 -w 0)
export FOGELL_JENKINS_WORKSPACE_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} bash -c \\\"printf %s ${FOGELL_WORKSPACE_COLLECTOR_B64} | base64 -d | bash -s -- '/var/jenkins_home/workspace/{job}'\\\"\""
# engine-inherited env (PATH and friends) for trace canonicalisation
export FOGELL_JENKINS_ENV_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} env\""
# each engine's `git --version` folds to ${GITVERSION} (FG-111 git step)
export FOGELL_JENKINS_GIT_VERSION_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} git --version\""

# FG-111: the SCM lane repo. The git-step cases clone git://<luigi>/repo.git,
# served by a git daemon on ${FOGELL_JENKINS_HOST} (base-path ~/fogell-scm).
# If it is down the cases fail closed as build failures on BOTH engines — this
# preflight names the actual cause instead.
: "${FOGELL_SCM_URL:=git://100.105.179.51/repo.git}"
# NOTE: FOGELL_SCM_URL moves the preflight, the sync target, and the SCM job
# spec together. What it CANNOT move is the URL text inside the committed
# git-step cases (pinned lab infrastructure, like the Jenkins core).
if ! timeout 15 git ls-remote "$FOGELL_SCM_URL" >/dev/null 2>&1; then
  echo "warning: SCM lane repo unreachable at $FOGELL_SCM_URL — git-step cases will fail." >&2
  echo "         start it on ${FOGELL_JENKINS_HOST}: git daemon --base-path=\$HOME/fogell-scm --export-all --enable=receive-pack --reuseaddr --port=9418" >&2
fi
# Establish the empty starting workspace explicitly after the wipe. A Jenkins
# compile-time refusal may never allocate one; treating a failed `cd` as empty was
# the old collector's false-pass route, while refusing it would make such cases
# permanently partial. Precreating the root lets v2 observe an actual empty tree.
export FOGELL_JENKINS_WIPE_CMD="ssh ${FOGELL_JENKINS_HOST} \"podman exec ${FOGELL_JENKINS_CONTAINER} sh -c \\\"rm -rf /var/jenkins_home/workspace/{job} /var/jenkins_home/workspace/{job}@tmp && mkdir -p /var/jenkins_home/workspace/{job}\\\"\""

# FG-052: SCM-marked cases live in the fixture repo — sync before running.
# A failed sync only WARNS: every SCM case verifies its checked-out bytes
# against the local body and fails CLOSED on drift, so stale content cannot
# seal a receipt; unrelated cases still run.
# BUILD BEFORE USE. `scripts/bin/` is gitignored, so on a fresh checkout this
# tool does not exist and the sync silently took the warning path — where the
# babashka original ran from a committed file that was always present. This
# runner is standalone and never enters `build-and-test.sh`, so it must build
# the tool itself. Raised by Codex on PR #180.
./scripts/build-audits.sh >/dev/null || { echo "warning: audit tool build failed — skipping scm case sync" >&2; }
./scripts/bin/sync-scm-cases || echo "warning: scm case sync failed — SCM cases will fail closed on drift" >&2

dotnet build -c Release --nologo >/dev/null 2>&1 || { echo "build failed"; exit 1; }
exec dotnet run --project tools/Fogell.Differential.Cli -c Release --no-build -- \
  "$FOGELL_JENKINS_URL" "$FOGELL_JENKINS_CORE" differential/receipts differential/cases/*.Jenkinsfile
