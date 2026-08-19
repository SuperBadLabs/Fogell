#!/usr/bin/env bash
# Synchronize the evidence-only SCM case that scripts/sync-scm-cases.bb cannot see.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

: "${FOGELL_SCM_URL:=git://100.105.179.51/repo.git}"

case_file='evidence/20260818-fg-177-measurement/cases/fg177-probe-checkout-scm.Jenkinsfile'
branch='case/fg177-probe-checkout-scm'
marker='//// SCM JOB ////'

IFS= read -r first_line < "$case_file"
if [[ "$first_line" != "$marker" ]]; then
  echo "ERROR: $case_file is not an SCM fixture (missing marker)" >&2
  exit 1
fi

sync_tmp=$(mktemp -d)
trap 'rm -rf "$sync_tmp"' EXIT
repo="$sync_tmp/repo"
desired="$sync_tmp/desired.Jenkinsfile"
current="$sync_tmp/current.Jenkinsfile"
remote="$sync_tmp/remote.Jenkinsfile"

# The marker configures the differential harness; Jenkins checks out only the body.
sed '1d' "$case_file" > "$desired"
if [[ ! -s "$desired" ]]; then
  echo "ERROR: $case_file has no fixture body" >&2
  exit 1
fi

git clone -q "$FOGELL_SCM_URL" "$repo"
main_head=$(git -C "$repo" rev-parse refs/remotes/origin/main)
needs_sync=true

if git -C "$repo" show "refs/remotes/origin/$branch:Jenkinsfile" > "$current" 2>/dev/null; then
  branch_head=$(git -C "$repo" rev-parse "refs/remotes/origin/$branch")
  branch_parent=$(git -C "$repo" rev-parse "$branch_head^" 2>/dev/null || true)
  if cmp -s "$desired" "$current" && [[ "$branch_parent" == "$main_head" ]]; then
    needs_sync=false
  fi
fi

if [[ "$needs_sync" == true ]]; then
  git -C "$repo" checkout -q -B "$branch" refs/remotes/origin/main
  cp "$desired" "$repo/Jenkinsfile"
  git -C "$repo" add Jenkinsfile
  GIT_AUTHOR_DATE='2026-01-01T00:00:00Z' \
  GIT_COMMITTER_DATE='2026-01-01T00:00:00Z' \
    git -C "$repo" \
      -c user.email=harness@fogell \
      -c user.name=fogell-harness \
      commit -qm 'sync: fg177-probe-checkout-scm'
  git -C "$repo" push -qf origin "HEAD:refs/heads/$branch"
fi

# Do not trust a successful push or a stale clone: refetch, resolve the exact
# remote branch, and verify its parent and Jenkinsfile bytes independently.
git -C "$repo" fetch -q origin "+refs/heads/$branch:refs/remotes/origin/$branch"
remote_head=$(git -C "$repo" rev-parse "refs/remotes/origin/$branch")
advertised_head=$(
  git ls-remote "$FOGELL_SCM_URL" "refs/heads/$branch" |
    awk 'NR == 1 { print $1 }'
)
if [[ -z "$advertised_head" || "$advertised_head" != "$remote_head" ]]; then
  echo "ERROR: $branch did not resolve to the verified remote commit" >&2
  exit 1
fi

remote_parent=$(git -C "$repo" rev-parse "$remote_head^")
if [[ "$remote_parent" != "$main_head" ]]; then
  echo "ERROR: $branch is not based directly on fixture main" >&2
  exit 1
fi

git -C "$repo" show "$remote_head:Jenkinsfile" > "$remote"
if ! cmp -s "$desired" "$remote"; then
  echo "ERROR: $branch Jenkinsfile differs from $case_file" >&2
  exit 1
fi

printf 'fixture %s at %s verified: exact Jenkinsfile bytes, parent %s\n' \
  "$branch" "$remote_head" "$main_head"
