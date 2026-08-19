#!/usr/bin/env bash
# Known-bad proof for the evidence-only fixture synchronizer.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

test_tmp=$(mktemp -d)
trap 'rm -rf "$test_tmp"' EXIT
remote="$test_tmp/fixture.git"
seed="$test_tmp/seed"
desired="$test_tmp/desired.Jenkinsfile"
observed="$test_tmp/observed.Jenkinsfile"
git_stub_dir="$test_tmp/bin"
branch='case/fg177-probe-checkout-scm'
case_file='evidence/20260818-fg-177-measurement/cases/fg177-probe-checkout-scm.Jenkinsfile'
sync_script='evidence/20260818-fg-177-measurement/sync-checkout-scm-fixture.sh'

sed '1d' "$case_file" > "$desired"

git init -q --bare "$remote"
git init -q "$seed"
git -C "$seed" config user.email proof@fogell
git -C "$seed" config user.name fogell-proof
cp "$desired" "$seed/Jenkinsfile"
printf 'tree sentinel\n' > "$seed/README"
git -C "$seed" add Jenkinsfile README
GIT_AUTHOR_DATE='2025-01-01T00:00:00Z' \
GIT_COMMITTER_DATE='2025-01-01T00:00:00Z' \
  git -C "$seed" commit -qm main
git -C "$seed" remote add origin "file://$remote"
git -C "$seed" push -q origin HEAD:refs/heads/main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main
main_head=$(git --git-dir="$remote" rev-parse refs/heads/main)
main_tree=$(git --git-dir="$remote" rev-parse "$main_head^{tree}")

# A push failure must be returned unchanged, and must not leave a branch that
# looks synchronized. This plants the original failure shape: fixture main
# already has the desired bytes and the reserved branch does not exist.
mkdir -p "$git_stub_dir"
real_git=$(command -v git)
cat > "$git_stub_dir/git" <<'EOF'
#!/usr/bin/env bash
if [[ " $* " == *' push '* ]]; then
  exit 47
fi
exec "$FOGELL_REAL_GIT" "$@"
EOF
chmod +x "$git_stub_dir/git"
set +e
PATH="$git_stub_dir:$PATH" \
FOGELL_REAL_GIT="$real_git" \
FOGELL_SCM_URL="file://$remote" \
  bash "$sync_script" > "$test_tmp/push-failure.log" 2>&1
push_rc=$?
set -e
[[ "$push_rc" -eq 47 ]]
if git --git-dir="$remote" show-ref --verify --quiet "refs/heads/$branch"; then
  echo 'ERROR: failed fixture push unexpectedly created the reserved branch' >&2
  exit 1
fi

assert_exact_branch() {
  local expected_head=$1
  local actual_head actual_parent actual_tree advertised_head

  actual_head=$(git --git-dir="$remote" rev-parse "refs/heads/$branch")
  actual_parent=$(git --git-dir="$remote" rev-parse "$actual_head^")
  actual_tree=$(git --git-dir="$remote" rev-parse "$actual_head^{tree}")
  advertised_head=$(git ls-remote "file://$remote" "refs/heads/$branch" | awk 'NR == 1 { print $1 }')
  git --git-dir="$remote" show "$actual_head:Jenkinsfile" > "$observed"

  cmp -s "$desired" "$observed"
  [[ "$actual_head" == "$expected_head" ]]
  [[ "$advertised_head" == "$actual_head" ]]
  [[ "$actual_parent" == "$main_head" ]]
  [[ "$actual_tree" == "$main_tree" ]]
}

expected_head=$(
  printf 'sync: fg177-probe-checkout-scm\n' |
    GIT_AUTHOR_DATE='2026-01-01T00:00:00Z' \
    GIT_COMMITTER_DATE='2026-01-01T00:00:00Z' \
      git --git-dir="$remote" \
        -c user.email=harness@fogell \
        -c user.name=fogell-harness \
        commit-tree "$main_tree" -p "$main_head"
)

# Fresh identical main plus a missing branch still requires a distinct direct
# child. --allow-empty makes that topology deterministic without changing tree.
FOGELL_SCM_URL="file://$remote" bash "$sync_script"
identical_head=$(git --git-dir="$remote" rev-parse "refs/heads/$branch")
[[ "$identical_head" != "$main_head" ]]
assert_exact_branch "$expected_head"

# Exact state is a no-op.
FOGELL_SCM_URL="file://$remote" bash "$sync_script"
assert_exact_branch "$identical_head"

# Deleting the branch and resynchronizing reconstructs the same commit SHA.
git -C "$seed" push -qd origin "$branch"
if git --git-dir="$remote" show-ref --verify --quiet "refs/heads/$branch"; then
  echo 'ERROR: reserved fixture branch deletion did not take effect' >&2
  exit 1
fi
FOGELL_SCM_URL="file://$remote" bash "$sync_script"
assert_exact_branch "$identical_head"

# A direct child with exact Jenkinsfile bytes but an extra tree entry is still
# drift. This proves the tree check is independent of the bytes/parent checks.
printf 'must not survive sync\n' > "$seed/rogue.txt"
git -C "$seed" add rogue.txt
GIT_AUTHOR_DATE='2025-01-10T00:00:00Z' \
GIT_COMMITTER_DATE='2025-01-10T00:00:00Z' \
  git -C "$seed" commit -qm 'drift: extra tree entry'
extra_tree_head=$(git -C "$seed" rev-parse HEAD)
git -C "$seed" push -qf origin "HEAD:refs/heads/$branch"
FOGELL_SCM_URL="file://$remote" bash "$sync_script"
[[ "$extra_tree_head" != "$identical_head" ]]
assert_exact_branch "$identical_head"

# A root case commit can have the exact tree and bytes but no parent. Topology
# alone makes it drift, and the synchronizer must repair it rather than abort.
root_head=$(
  printf 'root drift\n' |
    GIT_AUTHOR_DATE='2025-01-15T00:00:00Z' \
    GIT_COMMITTER_DATE='2025-01-15T00:00:00Z' \
      git -C "$seed" commit-tree "$main_tree"
)
git -C "$seed" push -qf origin "$root_head:refs/heads/$branch"
FOGELL_SCM_URL="file://$remote" bash "$sync_script"
[[ "$root_head" != "$identical_head" ]]
assert_exact_branch "$identical_head"

git -C "$seed" checkout -q main
printf 'drifted evidence\n' > "$seed/Jenkinsfile"
git -C "$seed" add Jenkinsfile
GIT_AUTHOR_DATE='2025-02-01T00:00:00Z' \
GIT_COMMITTER_DATE='2025-02-01T00:00:00Z' \
  git -C "$seed" commit -qm drift
drift_head=$(git -C "$seed" rev-parse HEAD)
git -C "$seed" push -qf origin "HEAD:refs/heads/$branch"

FOGELL_SCM_URL="file://$remote" bash "$sync_script"
[[ "$drift_head" != "$identical_head" ]]
assert_exact_branch "$identical_head"

# Replant the known-bad branch. Fixed identity, dates, parent and bytes must
# rebuild the same commit rather than a timestamp-dependent equivalent.
git -C "$seed" push -qf origin "$drift_head:refs/heads/$branch"
[[ "$(git --git-dir="$remote" rev-parse "refs/heads/$branch")" == "$drift_head" ]]
FOGELL_SCM_URL="file://$remote" bash "$sync_script"
assert_exact_branch "$identical_head"

printf 'SCM FIXTURE SYNC PROOF: identical-main/missing, root/no-parent and ordinary drift repaired; push rc 47 propagated; exact bytes/tree/main parent and deterministic SHA %s verified\n' \
  "$identical_head"
