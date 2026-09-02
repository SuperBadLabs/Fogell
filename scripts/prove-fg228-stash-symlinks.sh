#!/usr/bin/env bash
# FG-228 — prove the stash containment test kills the historical two-part
# traversal mechanism: enter selected directory links, then copy from an
# ordinary pathname-opening API that follows links.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

scratch="$(mktemp -d /tmp/fogell-fg228-mutant.XXXXXX)"
trap 'rm -rf "$scratch"' EXIT

# Copy current-worktree bytes for every tracked source/build input needed by the
# focused project. Modified tracked files are read from the worktree, binding
# the proof to the candidate under review without touching its live bin/obj.
git ls-files -z -- \
  src tests/Fogell.Execution.Tests \
  Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  | tar --null -T - -cf - \
  | tar -xf - -C "$scratch"

project="$scratch/tests/Fogell.Execution.Tests/Fogell.Execution.Tests.fsproj"
publish="$scratch/src/Fogell.Execution/Publish.fs"
native="$scratch/src/Fogell.Execution/Native.fs"
filter='FG-228 stash symlink containment'
mutant_filter='FG-228 linked-directory traversal mutant'

bash -ic "dotnet restore '$project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet build '$project' -c Release --no-restore -m:1"
dotnet run --project "$project" -c Release --no-build -- \
  --filter-test-list "$filter" >/dev/null

selector='let private entryIsSymbolicLink (entry: FileSystemInfo) ='
# FG-238: the flag bits come from LinuxOpenFlags; the no-follow guard is `table.NoFollow`.
child_opener='let private openChildDirectoryWithoutLinksUsing'
opener='match Native.openFileWithoutLinks workspace relative with'
[[ "$(rg -F -c "$selector" "$publish")" == 1 ]] \
  || { echo 'FG-228 proof: selector mutation target is not unique' >&2; exit 1; }
[[ "$(rg -F -c "$opener" "$publish")" == 1 ]] \
  || { echo 'FG-228 proof: source-open mutation target is not unique' >&2; exit 1; }
[[ "$(rg -F -c "$child_opener" "$native")" == 1 ]] \
  || { echo 'FG-228 proof: child-directory mutation target is not unique' >&2; exit 1; }

# These three changes recreate the vulnerable behavior: misclassify the link,
# drop O_NOFOLLOW from the child-directory open so its target is entered, then
# replace the source descriptor boundary with a pathname open that follows it.
sed -i '/let private entryIsSymbolicLink (entry: FileSystemInfo) =/{n;s/not (isNull entry.LinkTarget)/false/;}' "$publish"
guards_before="$(rg -F -c '||| table.NoFollow' "$native")"
sed -i '/let private openChildDirectoryWithoutLinksUsing/,/if descriptor < 0/{/||| table.NoFollow/d;}' "$native"
guards_after="$(rg -F -c '||| table.NoFollow' "$native")"
sed -i 's/match Native.openFileWithoutLinks workspace relative with/match Ok(File.OpenRead(Path.Combine(workspace, relative))) with/' "$publish"

rg -F '        false' "$publish" >/dev/null \
  || { echo 'FG-228 proof: selector mutant did not change bytes' >&2; exit 1; }
[[ "$guards_before" -eq $((guards_after + 1)) ]] \
  || { echo 'FG-228 proof: child-directory mutant did not remove exactly one no-follow guard' >&2; exit 1; }
rg -F 'match Ok(File.OpenRead(Path.Combine(workspace, relative))) with' "$publish" >/dev/null \
  || { echo 'FG-228 proof: source-open mutant did not change bytes' >&2; exit 1; }

# A compile failure is not a killed semantic mutant.
set +e
bash -ic "dotnet build '$project' -c Release --no-restore -m:1"
mutant_build_rc=$?
mutant_output="$(dotnet run --project "$project" -c Release --no-build -- \
  --filter-test-list "$mutant_filter" 2>&1)"
mutant_rc=$?
set -e

if [[ "$mutant_build_rc" -ne 0 ]]; then
  echo 'FG-228 proof: semantic mutant did not compile' >&2
  exit 1
fi

if [[ "$mutant_rc" -eq 0 ]]; then
  echo 'FG-228 proof: traversal/following mutant survived' >&2
  exit 1
fi

printf '%s\n' "$mutant_output" | rg -F 'selected directory symlink was copied' >/dev/null \
  || { printf '%s\n' "$mutant_output" >&2; echo 'FG-228 proof: mutant failed for an unrelated reason' >&2; exit 1; }

echo 'FG-228 PROOF PASS: baseline passed; linked-directory/openat/path-follow mutant compiled and was killed'
