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
branch='case/fg177-probe-checkout-scm'
case_file='evidence/20260818-fg-177-measurement/cases/fg177-probe-checkout-scm.Jenkinsfile'

git init -q --bare "$remote"
git init -q "$seed"
git -C "$seed" config user.email proof@fogell
git -C "$seed" config user.name fogell-proof
printf 'fixture main\n' > "$seed/Jenkinsfile"
git -C "$seed" add Jenkinsfile
GIT_AUTHOR_DATE='2025-01-01T00:00:00Z' \
GIT_COMMITTER_DATE='2025-01-01T00:00:00Z' \
  git -C "$seed" commit -qm main
git -C "$seed" remote add origin "file://$remote"
git -C "$seed" push -q origin HEAD:refs/heads/main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main
main_head=$(git --git-dir="$remote" rev-parse refs/heads/main)

sed '1d' "$case_file" > "$desired"

# A root case commit has bytes but no parent. It is drift, not a reason for the
# synchronizer itself to abort before it gets a chance to repair the branch.
printf 'root drift\n' > "$seed/Jenkinsfile"
git -C "$seed" add Jenkinsfile
root_tree=$(git -C "$seed" write-tree)
root_head=$(
  printf 'root drift\n' |
    GIT_AUTHOR_DATE='2025-01-15T00:00:00Z' \
    GIT_COMMITTER_DATE='2025-01-15T00:00:00Z' \
      git -C "$seed" commit-tree "$root_tree"
)
git -C "$seed" push -q origin "$root_head:refs/heads/$branch"
FOGELL_SCM_URL="file://$remote" \
  bash evidence/20260818-fg-177-measurement/sync-checkout-scm-fixture.sh
repaired_root=$(git --git-dir="$remote" rev-parse "refs/heads/$branch")
root_repaired_parent=$(git --git-dir="$remote" rev-parse "$repaired_root^")
git --git-dir="$remote" show "$repaired_root:Jenkinsfile" > "$observed"
cmp -s "$desired" "$observed"
[[ "$root_repaired_parent" == "$main_head" ]]
[[ "$repaired_root" != "$root_head" ]]

printf 'drifted evidence\n' > "$seed/Jenkinsfile"
git -C "$seed" add Jenkinsfile
GIT_AUTHOR_DATE='2025-02-01T00:00:00Z' \
GIT_COMMITTER_DATE='2025-02-01T00:00:00Z' \
  git -C "$seed" commit -qm drift
drift_head=$(git -C "$seed" rev-parse HEAD)
git -C "$seed" push -qf origin "HEAD:refs/heads/$branch"

FOGELL_SCM_URL="file://$remote" \
  bash evidence/20260818-fg-177-measurement/sync-checkout-scm-fixture.sh
repaired_once=$(git --git-dir="$remote" rev-parse "refs/heads/$branch")
repaired_parent=$(git --git-dir="$remote" rev-parse "$repaired_once^")
git --git-dir="$remote" show "$repaired_once:Jenkinsfile" > "$observed"
cmp -s "$desired" "$observed"
[[ "$repaired_parent" == "$main_head" ]]
[[ "$repaired_once" != "$drift_head" ]]
[[ "$repaired_once" == "$repaired_root" ]]

# Exact state is a no-op.
FOGELL_SCM_URL="file://$remote" \
  bash evidence/20260818-fg-177-measurement/sync-checkout-scm-fixture.sh
repaired_twice=$(git --git-dir="$remote" rev-parse "refs/heads/$branch")
[[ "$repaired_twice" == "$repaired_once" ]]

# Replant the known-bad branch. Fixed identity, dates, parent and bytes must
# rebuild the same commit rather than a timestamp-dependent equivalent.
git -C "$seed" push -qf origin "$drift_head:refs/heads/$branch"
[[ "$(git --git-dir="$remote" rev-parse "refs/heads/$branch")" == "$drift_head" ]]
FOGELL_SCM_URL="file://$remote" \
  bash evidence/20260818-fg-177-measurement/sync-checkout-scm-fixture.sh
repaired_after_replant=$(git --git-dir="$remote" rev-parse "refs/heads/$branch")
[[ "$repaired_after_replant" == "$repaired_once" ]]
git --git-dir="$remote" show "$repaired_after_replant:Jenkinsfile" > "$observed"
cmp -s "$desired" "$observed"

printf 'SCM FIXTURE SYNC PROOF: root/no-parent and ordinary drift repaired, exact bytes/main parent verified, deterministic SHA %s\n' \
  "$repaired_once"
