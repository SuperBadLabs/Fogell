#!/usr/bin/env bash
# Synchronize the evidence-only SCM case that scripts/sync-scm-cases.bb cannot see.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

: "${FOGELL_SCM_URL:=git://100.105.179.51/repo.git}"
: "${FOGELL_SCM_PIN_OUTPUT:=}"

case_file='evidence/20260818-fg-177-measurement/cases/fg177-probe-checkout-scm.Jenkinsfile'
branch='case/fg177-probe-checkout-scm'
marker='//// SCM JOB ////'

IFS= read -r first_line < "$case_file"
if [[ "$first_line" != "$marker" ]]; then
  echo "ERROR: $case_file is not an SCM fixture (missing marker)" >&2
  exit 1
fi

sync_tmp=$(mktemp -d)
pin_tmp=''
cleanup() {
  rm -rf "$sync_tmp"
  [[ -z "$pin_tmp" || ! -e "$pin_tmp" ]] || rm -f "$pin_tmp"
}
trap cleanup EXIT
repo="$sync_tmp/repo"
desired="$sync_tmp/desired.Jenkinsfile"
current="$sync_tmp/current.Jenkinsfile"
remote="$sync_tmp/remote.Jenkinsfile"
expected_index="$sync_tmp/expected.index"

# The marker configures the differential harness; Jenkins checks out only the body.
sed '1d' "$case_file" > "$desired"
if [[ ! -s "$desired" ]]; then
  echo "ERROR: $case_file has no fixture body" >&2
  exit 1
fi

git clone -q "$FOGELL_SCM_URL" "$repo"
main_head=$(git -C "$repo" rev-parse refs/remotes/origin/main)
desired_blob=$(git -C "$repo" hash-object -w -- "$desired")
GIT_INDEX_FILE="$expected_index" git -C "$repo" read-tree "$main_head"
GIT_INDEX_FILE="$expected_index" \
  git -C "$repo" update-index --add --cacheinfo "100644,$desired_blob,Jenkinsfile"
expected_tree=$(GIT_INDEX_FILE="$expected_index" git -C "$repo" write-tree)
needs_sync=true

if git -C "$repo" show "refs/remotes/origin/$branch:Jenkinsfile" > "$current" 2>/dev/null; then
  branch_head=$(git -C "$repo" rev-parse "refs/remotes/origin/$branch")
  branch_parent=$(git -C "$repo" rev-parse "$branch_head^" 2>/dev/null || true)
  branch_tree=$(git -C "$repo" rev-parse "$branch_head^{tree}")
  if cmp -s "$desired" "$current" &&
    [[ "$branch_parent" == "$main_head" ]] &&
    [[ "$branch_tree" == "$expected_tree" ]]; then
    needs_sync=false
  fi
fi

if [[ "$needs_sync" == true ]]; then
  git -C "$repo" checkout -q -B "$branch" refs/remotes/origin/main
  cp "$desired" "$repo/Jenkinsfile"
  chmod 0644 "$repo/Jenkinsfile"
  git -C "$repo" add Jenkinsfile
  GIT_AUTHOR_DATE='2026-01-01T00:00:00Z' \
  GIT_COMMITTER_DATE='2026-01-01T00:00:00Z' \
    git -C "$repo" \
      -c user.email=harness@fogell \
      -c user.name=fogell-harness \
      -c commit.gpgSign=false \
      commit --allow-empty -qm 'sync: fg177-probe-checkout-scm'
  local_tree=$(git -C "$repo" rev-parse 'HEAD^{tree}')
  if [[ "$local_tree" != "$expected_tree" ]]; then
    echo "ERROR: generated $branch tree differs from fixture main plus $case_file" >&2
    exit 1
  fi
  generated_head=$(git -C "$repo" rev-parse HEAD)
  if git -C "$repo" push -qf origin "HEAD:refs/heads/$branch"; then
    :
  else
    push_rc=$?
    # Another synchronizer may win the same deterministic update between our
    # clone and push. Accept only its byte-identical commit; otherwise preserve
    # the original transport/ref failure status.
    raced_head=$(
      git ls-remote "$FOGELL_SCM_URL" "refs/heads/$branch" |
        awk 'NR == 1 { print $1 }'
    )
    if [[ "$raced_head" != "$generated_head" ]]; then
      exit "$push_rc"
    fi
  fi
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

remote_tree=$(git -C "$repo" rev-parse "$remote_head^{tree}")
if [[ "$remote_tree" != "$expected_tree" ]]; then
  echo "ERROR: $branch tree differs from fixture main plus $case_file" >&2
  exit 1
fi

git -C "$repo" show "$remote_head:Jenkinsfile" > "$remote"
if ! cmp -s "$desired" "$remote"; then
  echo "ERROR: $branch Jenkinsfile differs from $case_file" >&2
  exit 1
fi

ensure_content_pin() {
  local revision=$1
  local pin_branch="fogell-pins/$revision"
  local advertised
  advertised=$(
    git ls-remote "$FOGELL_SCM_URL" "refs/heads/$pin_branch" |
      awk 'NR == 1 { print $1 }'
  )
  if [[ -z "$advertised" ]]; then
    # No force: a racing writer may create the same content-addressed ref, but
    # neither process may replace a different value under this name.
    git -C "$repo" push -q origin "$revision:refs/heads/$pin_branch" || true
    advertised=$(
      git ls-remote "$FOGELL_SCM_URL" "refs/heads/$pin_branch" |
        awk 'NR == 1 { print $1 }'
    )
  fi
  if [[ "$advertised" != "$revision" ]]; then
    echo "ERROR: immutable pin $pin_branch resolves to ${advertised:-<missing>}, expected $revision" >&2
    exit 1
  fi
  printf '%s' "$pin_branch"
}

main_tree=$(git -C "$repo" rev-parse "$main_head^{tree}")
main_pin_branch=$(ensure_content_pin "$main_head")
scm_pin_branch=$(ensure_content_pin "$remote_head")
jenkinsfile_sha256=$(sha256sum "$desired" | awk '{print $1}')

if [[ -n "$FOGELL_SCM_PIN_OUTPUT" ]]; then
  pin_parent=$(dirname "$FOGELL_SCM_PIN_OUTPUT")
  [[ -d "$pin_parent" && ! -L "$pin_parent" ]] || {
    echo "ERROR: SCM pin output parent must be a real directory: $pin_parent" >&2
    exit 1
  }
  [[ ! -e "$FOGELL_SCM_PIN_OUTPUT" && ! -L "$FOGELL_SCM_PIN_OUTPUT" ]] || {
    echo "ERROR: SCM pin output already exists: $FOGELL_SCM_PIN_OUTPUT" >&2
    exit 1
  }
  pin_tmp=$(mktemp "$pin_parent/.scm-pin.tsv.XXXXXX")
  {
    printf 'format\tfogell-scm-pin-v1\n'
    printf 'source-branch\t%s\n' "$branch"
    printf 'source-revision\t%s\n' "$remote_head"
    printf 'scm-pinned-branch\t%s\n' "$scm_pin_branch"
    printf 'scm-pinned-revision\t%s\n' "$remote_head"
    printf 'scm-tree\t%s\n' "$remote_tree"
    printf 'jenkinsfile-blob\t%s\n' "$desired_blob"
    printf 'jenkinsfile-sha256\t%s\n' "$jenkinsfile_sha256"
    printf 'git-pinned-branch\t%s\n' "$main_pin_branch"
    printf 'git-pinned-revision\t%s\n' "$main_head"
    printf 'git-tree\t%s\n' "$main_tree"
  } > "$pin_tmp"
  chmod 0444 "$pin_tmp"
  mv "$pin_tmp" "$FOGELL_SCM_PIN_OUTPUT"
  pin_tmp=''
fi

printf 'fixture %s at %s verified: exact tree %s, blob %s, parent %s; pins scm=%s git=%s\n' \
  "$branch" "$remote_head" "$remote_tree" "$desired_blob" "$main_head" \
  "$scm_pin_branch" "$main_pin_branch"
