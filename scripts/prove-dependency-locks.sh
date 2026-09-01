#!/usr/bin/env bash
# FG-074 — prove that the checked-in NuGet graph is complete, locked, and
# sufficient for a source-cleared restore after populating an isolated cache.
# Clearing NuGet sources is not OS-level network isolation and makes no claim
# about process egress outside NuGet package resolution.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
scratch="$(mktemp -d /tmp/fogell-lock-proof.XXXXXX)"
trap 'rm -rf -- "$scratch"' EXIT

source_cleared_config="$scratch/NuGet.Config"
# The isolated cache the cold restore populates and the no-restore build reads.
# By default it lives under the scratch directory and dies with it. A caller may
# name a location in FOGELL_LOCK_PROOF_PACKAGE_CACHE, which this proof then
# LEAVES IN PLACE: the gate does so and points NUGET_PACKAGES at the same
# directory for every later step. Deleting the cache here made the next plain
# `dotnet build` in the gate (FG-207, then the restart and approval lanes)
# restore into ~/.nuget instead, rewrite every project.assets.json, and
# recompile the solution a second time — ~60 s of hosted run 33567174871 spent
# rebuilding what the no-restore build below had already built. The emptiness
# requirement in populate_isolated_cache is unchanged: a caller-named cache that
# already holds anything is refused, not reused, so the cold-restore claim
# still means what it says.
if [ -n "${FOGELL_LOCK_PROOF_PACKAGE_CACHE:-}" ]; then
  mkdir -p -- "$FOGELL_LOCK_PROOF_PACKAGE_CACHE"
  package_cache="$(cd -- "$FOGELL_LOCK_PROOF_PACKAGE_CACHE" && pwd -P)"
else
  package_cache="$scratch/nuget-packages"
  mkdir -p "$package_cache"
fi
cat >"$source_cleared_config" <<'CONFIG'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
CONFIG

check_embedded_repositories() {
  local root="$1"
  local embedded="$scratch/embedded-repositories"

  if ! git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    return
  fi

  # An outer `git ls-files --others` deliberately does not descend into an
  # embedded repository. Reject that ambiguous boundary before trusting the
  # Git inventory. The named Codex worktree area is an explicit exception and
  # is already outside this repository's dependency boundary.
  find "$root" \
    \( -path "$root/.git" -o -path "$root/.claude/worktrees" \) -prune \
    -o -name .git -print \
    | sed "s|^$root/||" | LC_ALL=C sort >"$embedded"
  if [ -s "$embedded" ]; then
    echo "dependency-lock inventory: embedded Git repositories are not permitted:" >&2
    cat "$embedded" >&2
    return 1
  fi
}

list_projects() {
  local root="$1"
  if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    # Audit the repository-visible tree, including non-ignored untracked
    # projects, after embedded repositories have been rejected explicitly.
    git -C "$root" ls-files --cached --others \
      --exclude-per-directory=.gitignore -- \
      ':(glob)**/*.fsproj' ':(glob)**/*.csproj' ':(glob)**/*.vbproj' \
      ':(exclude,glob).claude/worktrees/**' \
      | LC_ALL=C sort
    return
  fi
  (
    cd "$root"
    find . \
      \( -path './.git' -o -path '*/bin' -o -path '*/obj' \
         -o -path './.forge-home' -o -path './.fogell-home' \
         -o -path './evidence/*/workspace' \) -prune \
      -o -type f \( -name '*.fsproj' -o -name '*.csproj' -o -name '*.vbproj' \) \
      -print \
      | sed 's|^./||' | LC_ALL=C sort
  )
}

list_locks() {
  local root="$1"
  if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git -C "$root" ls-files --cached --others \
      --exclude-per-directory=.gitignore -- \
      ':(glob)**/packages.lock.json' \
      ':(exclude,glob).claude/worktrees/**' | LC_ALL=C sort
    return
  fi
  (
    cd "$root"
    find . \
      \( -path './.git' -o -path '*/bin' -o -path '*/obj' \
         -o -path './.forge-home' -o -path './.fogell-home' \
         -o -path './evidence/*/workspace' \) -prune \
      -o -type f -name packages.lock.json -print \
      | sed 's|^./||' | LC_ALL=C sort
  )
}

check_lock_inventory() {
  local root="$1"
  local projects
  local expected
  local actual
  local duplicate_dirs

  projects="$(mktemp "$scratch/projects.XXXXXX")"
  expected="$(mktemp "$scratch/expected-locks.XXXXXX")"
  actual="$(mktemp "$scratch/actual-locks.XXXXXX")"
  duplicate_dirs="$(mktemp "$scratch/duplicate-project-dirs.XXXXXX")"

  check_embedded_repositories "$root" || return 1
  list_projects "$root" >"$projects"
  if [ ! -s "$projects" ]; then
    echo "dependency-lock inventory: no projects found" >&2
    return 1
  fi

  sed 's|/[^/]*$||' "$projects" | uniq -d >"$duplicate_dirs"
  if [ -s "$duplicate_dirs" ]; then
    echo "dependency-lock inventory: project directories would share one default lock file:" >&2
    cat "$duplicate_dirs" >&2
    return 1
  fi

  sed 's|/[^/]*$|/packages.lock.json|' "$projects" >"$expected"
  list_locks "$root" >"$actual"
  if ! cmp -s "$expected" "$actual"; then
    echo "dependency-lock inventory: missing or orphaned project lock:" >&2
    diff -u "$expected" "$actual" >&2 || true
    return 1
  fi
}

check_solution_inventory() {
  local root="$1"
  local declared="$scratch/solution-projects"
  local actual="$scratch/filesystem-projects"

  check_embedded_repositories "$root" || return 1
  sed -n 's|.*<Project Path="\([^"]*\.[fcv][sb]proj\)".*|\1|p' "$root/Fogell.slnx" \
    | LC_ALL=C sort >"$declared"
  list_projects "$root" >"$actual"
  if ! cmp -s "$declared" "$actual"; then
    echo "dependency-lock inventory: Fogell.slnx and project files disagree:" >&2
    diff -u "$declared" "$actual" >&2 || true
    return 1
  fi
}

check_lock_policy() {
  local root="$1"
  local project
  local property
  local actual
  local results
  local jobs_max
  local i
  local -a projects

  mapfile -t projects < <(list_projects "$root")
  results="$(mktemp -d "$scratch/lock-policy.XXXXXX")"

  # ONE EVALUATION PER PROJECT, RUN CONCURRENTLY. The earlier form started one
  # msbuild per property per project — 52 serial evaluations for two booleans,
  # ~24 s of hosted run 33567174871. Asking for both properties in one call
  # halves the starts, and the projects are independent, so the evaluations
  # run in parallel and are judged afterwards in inventory order: the first
  # diagnostic is deterministic whatever the completion order, and the message
  # is unchanged because the mutation arms below match it verbatim. The cap is
  # one evaluation per core, not build-audits.sh's nproc/3: an evaluation-only
  # msbuild is a fraction of an fflat compile. UNMEASURED on the 4-core runner;
  # if this shows up in a step timing, that divisor is the knob.
  jobs_max=$(nproc 2>/dev/null || echo 4)
  i=0
  for project in "${projects[@]}"; do
    while [ "$(jobs -rp | wc -l)" -ge "$jobs_max" ]; do wait -n; done
    {
      if dotnet msbuild "$root/$project" \
        -getProperty:RestorePackagesWithLockFile -getProperty:RestoreLockedMode \
        >"$results/$i.json" 2>"$results/$i.log"; then
        : >"$results/$i.ok"
      fi
    } &
    i=$((i + 1))
  done
  wait

  i=0
  for project in "${projects[@]}"; do
    if [ ! -e "$results/$i.ok" ]; then
      echo "dependency-lock policy: $project: property evaluation failed" >&2
      cat "$results/$i.json" "$results/$i.log" >&2
      return 1
    fi
    # Parsed to a file and checked before it is read: a parser failure inside a
    # process substitution would otherwise read as "no properties, nothing
    # wrong". Both names are required, so a missing key is a failure too.
    if ! python3 - "$results/$i.json" >"$results/$i.properties" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    properties = json.load(stream)["Properties"]
for name in ("RestorePackagesWithLockFile", "RestoreLockedMode"):
    print(f"{name}={properties[name]}")
PY
    then
      echo "dependency-lock policy: $project: property evaluation output was not readable" >&2
      cat "$results/$i.json" >&2
      return 1
    fi
    while IFS='=' read -r property actual; do
      if [ "$actual" != "true" ]; then
        echo "dependency-lock policy: $project: $property evaluated to '$actual', expected 'true'" >&2
        return 1
      fi
    done <"$results/$i.properties"
    i=$((i + 1))
  done
}

restore_locked() {
  local project="$1"
  local log="$2"
  dotnet restore "$project" \
    --locked-mode --force --no-http-cache --packages "$package_cache" \
    --configfile "$source_cleared_config" \
    --nologo >"$log" 2>&1
}

populate_isolated_cache() {
  local log="$scratch/cold-cache-restore.log"

  if find "$package_cache" -mindepth 1 -print -quit | grep -q .; then
    echo "dependency-lock proof: isolated NuGet package cache was not empty" >&2
    return 1
  fi
  if ! dotnet restore "$repo_root/Fogell.slnx" \
    --locked-mode --force --no-http-cache --packages "$package_cache" --nologo \
    >"$log" 2>&1; then
    cat "$log" >&2
    return 1
  fi
  if ! find "$package_cache" -mindepth 1 -print -quit | grep -q .; then
    echo "dependency-lock proof: network-enabled locked restore left the isolated cache empty" >&2
    return 1
  fi
  grep -E 'warning NU|error NU' "$log" || true
}

check_regenerated_locks() {
  local root="$1"
  local entry="$2"
  local snapshot
  local log
  local rel

  snapshot="$(mktemp -d "$scratch/regenerated.XXXXXX")"
  log="$snapshot/restore.log"
  cp "$root/Directory.Build.props" "$snapshot/"
  [ ! -f "$root/global.json" ] || cp "$root/global.json" "$snapshot/"
  [ ! -f "$root/Fogell.slnx" ] || cp "$root/Fogell.slnx" "$snapshot/"
  while IFS= read -r rel; do
    mkdir -p "$snapshot/$(dirname "$rel")"
    cp "$root/$rel" "$snapshot/$rel"
  done < <(list_projects "$root")
  while IFS= read -r rel; do
    mkdir -p "$snapshot/$(dirname "$rel")"
    cp "$root/$rel" "$snapshot/$rel"
  done < <(list_locks "$root")

  if ! dotnet restore "$snapshot/$entry" --force-evaluate \
    -p:RestoreLockedMode=false --no-http-cache --packages "$package_cache" \
    --configfile "$source_cleared_config" \
    --nologo >"$log" 2>&1; then
    cat "$log" >&2
    return 1
  fi

  while IFS= read -r rel; do
    if ! cmp -s "$root/$rel" "$snapshot/$rel"; then
      echo "dependency-lock audit: regenerated lock differs: $rel" >&2
      diff -u "$root/$rel" "$snapshot/$rel" >&2 || true
      return 1
    fi
  done < <(list_locks "$root")
}

expect_restore_failure() {
  local label="$1"
  local expected_code="$2"
  local project="$3"
  local log="$scratch/$label.log"

  if restore_locked "$project" "$log"; then
    echo "dependency-lock mutation unexpectedly passed: $label" >&2
    return 1
  fi
  if ! grep -Fq "$expected_code" "$log"; then
    echo "dependency-lock mutation failed for the wrong reason: $label" >&2
    cat "$log" >&2
    return 1
  fi
  echo "mutation rejected: $label ($expected_code)"
}

make_fixture() {
  local destination="$1"
  mkdir -p "$destination/src/Fogell.Store" "$destination/src/Fogell.Domain" \
    "$destination/tests" "$destination/tools"
  cp "$repo_root/Directory.Build.props" "$destination/"
  cp "$repo_root/src/Fogell.Store/Fogell.Store.fsproj" \
    "$repo_root/src/Fogell.Store/packages.lock.json" \
    "$destination/src/Fogell.Store/"
  cp "$repo_root/src/Fogell.Domain/Fogell.Domain.fsproj" \
    "$repo_root/src/Fogell.Domain/packages.lock.json" \
    "$destination/src/Fogell.Domain/"
}

echo "=== dependency-lock inventory ==="
check_solution_inventory "$repo_root"
check_lock_inventory "$repo_root"
check_lock_policy "$repo_root"
echo "=== network-enabled locked restore into an empty isolated package cache ==="
populate_isolated_cache
echo "isolated package cache populated; vulnerability freshness is not asserted by this proof"
check_regenerated_locks "$repo_root" "Fogell.slnx"
project_count="$(list_projects "$repo_root" | wc -l)"
echo "project locks present: $project_count/$project_count"

fixture="$scratch/fixture"
make_fixture "$fixture"
restore_locked "$fixture/src/Fogell.Store/Fogell.Store.fsproj" "$scratch/fixture-baseline.log"

locked_mode_mutated="$scratch/non-first-locked-mode-disabled"
cp -a "$fixture" "$locked_mode_mutated"
sed -i 's|</PropertyGroup>|<RestoreLockedMode>false</RestoreLockedMode></PropertyGroup>|' \
  "$locked_mode_mutated/src/Fogell.Store/Fogell.Store.fsproj"
if check_lock_policy "$locked_mode_mutated" >"$scratch/non-first-locked-mode-disabled.log" 2>&1; then
  echo "dependency-lock mutation unexpectedly passed: non-first project disabled locked mode" >&2
  exit 1
fi
grep -Fq "src/Fogell.Store/Fogell.Store.fsproj: RestoreLockedMode evaluated to 'false'" \
  "$scratch/non-first-locked-mode-disabled.log" \
  || { cat "$scratch/non-first-locked-mode-disabled.log" >&2; exit 1; }
echo "mutation rejected: non-first project disabled RestoreLockedMode"

lock_file_policy_mutated="$scratch/non-first-lock-file-policy-disabled"
cp -a "$fixture" "$lock_file_policy_mutated"
sed -i 's|</PropertyGroup>|<RestorePackagesWithLockFile>false</RestorePackagesWithLockFile></PropertyGroup>|' \
  "$lock_file_policy_mutated/src/Fogell.Store/Fogell.Store.fsproj"
if check_lock_policy "$lock_file_policy_mutated" \
  >"$scratch/non-first-lock-file-policy-disabled.log" 2>&1; then
  echo "dependency-lock mutation unexpectedly passed: non-first project disabled lock files" >&2
  exit 1
fi
grep -Fq "src/Fogell.Store/Fogell.Store.fsproj: RestorePackagesWithLockFile evaluated to 'false'" \
  "$scratch/non-first-lock-file-policy-disabled.log" \
  || { cat "$scratch/non-first-lock-file-policy-disabled.log" >&2; exit 1; }
echo "mutation rejected: non-first project disabled RestorePackagesWithLockFile"

solution_mutated="$scratch/solution-inventory-drift"
cp -a "$fixture" "$solution_mutated"
cat >"$solution_mutated/Fogell.slnx" <<'SLNX'
<Solution>
  <Project Path="src/Fogell.Domain/Fogell.Domain.fsproj" />
  <Project Path="src/Fogell.Store/Fogell.Store.fsproj" />
</Solution>
SLNX
mkdir -p "$solution_mutated/extras/Unlisted"
cat >"$solution_mutated/extras/Unlisted/Unlisted.fsproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
PROJECT
if check_solution_inventory "$solution_mutated" \
  >"$scratch/solution-inventory-drift.log" 2>&1; then
  echo "dependency-lock mutation unexpectedly passed: project omitted from solution" >&2
  exit 1
fi
grep -Fq "Fogell.slnx and project files disagree" "$scratch/solution-inventory-drift.log" \
  || { cat "$scratch/solution-inventory-drift.log" >&2; exit 1; }
echo "mutation rejected: repo-wide project omitted from Fogell.slnx"

ignored_nested="$scratch/ignored-nested-worktree"
cp -a "$fixture" "$ignored_nested"
cat >"$ignored_nested/Fogell.slnx" <<'SLNX'
<Solution>
  <Project Path="src/Fogell.Domain/Fogell.Domain.fsproj" />
  <Project Path="src/Fogell.Store/Fogell.Store.fsproj" />
</Solution>
SLNX
cat >"$ignored_nested/.gitignore" <<'IGNORE'
.claude/worktrees/
IGNORE
mkdir -p "$ignored_nested/.claude/worktrees/other/src/Nested"
git init -q "$ignored_nested/.claude/worktrees/other"
cp "$fixture/src/Fogell.Domain/Fogell.Domain.fsproj" \
  "$ignored_nested/.claude/worktrees/other/src/Nested/Nested.fsproj"
cp "$fixture/src/Fogell.Domain/packages.lock.json" \
  "$ignored_nested/.claude/worktrees/other/src/Nested/packages.lock.json"
git -C "$ignored_nested" init -q
printf 'src/Fogell.Store/\n' >"$ignored_nested/hostile-global-ignore"
git -C "$ignored_nested" config core.excludesFile \
  "$ignored_nested/hostile-global-ignore"
check_solution_inventory "$ignored_nested"
check_lock_inventory "$ignored_nested"
echo "control passed: ignored nested worktree is outside repository inventory; operator-global ignores cannot hide projects or locks"

embedded_repo="$scratch/embedded-repository"
cp -a "$fixture" "$embedded_repo"
cat >"$embedded_repo/Fogell.slnx" <<'SLNX'
<Solution>
  <Project Path="src/Fogell.Domain/Fogell.Domain.fsproj" />
  <Project Path="src/Fogell.Store/Fogell.Store.fsproj" />
</Solution>
SLNX
git -C "$embedded_repo" init -q
mkdir -p "$embedded_repo/vendor/Hidden"
git init -q "$embedded_repo/vendor/Hidden"
cat >"$embedded_repo/vendor/Hidden/Hidden.fsproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
PROJECT
if check_solution_inventory "$embedded_repo" \
  >"$scratch/embedded-repository.log" 2>&1; then
  echo "dependency-lock mutation unexpectedly passed: embedded repository" >&2
  exit 1
fi
grep -Fq "embedded Git repositories are not permitted" \
  "$scratch/embedded-repository.log" \
  || { cat "$scratch/embedded-repository.log" >&2; exit 1; }
echo "mutation rejected: embedded repository cannot hide projects from the outer inventory"

missing="$scratch/missing-lock"
cp -a "$fixture" "$missing"
rm -- "$missing/src/Fogell.Store/packages.lock.json"
if check_lock_inventory "$missing" >"$scratch/missing-lock.log" 2>&1; then
  echo "dependency-lock mutation unexpectedly passed: missing lock" >&2
  exit 1
fi
grep -Fq "missing or orphaned project lock" "$scratch/missing-lock.log" \
  || { cat "$scratch/missing-lock.log" >&2; exit 1; }
echo "mutation rejected: missing lock (inventory)"

drift="$scratch/package-reference-drift"
cp -a "$fixture" "$drift"
sed -i 's/Npgsql" Version="8.0.5"/Npgsql" Version="8.0.4"/' \
  "$drift/src/Fogell.Store/Fogell.Store.fsproj"
grep -Fq 'Npgsql" Version="8.0.4"' "$drift/src/Fogell.Store/Fogell.Store.fsproj" \
  || { echo "dependency-lock mutation was not planted: PackageReference drift" >&2; exit 1; }
expect_restore_failure "package-reference-drift" "NU1004" \
  "$drift/src/Fogell.Store/Fogell.Store.fsproj"

tampered="$scratch/content-hash-tamper"
cp -a "$fixture" "$tampered"
python3 - "$tampered/src/Fogell.Store/packages.lock.json" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as stream:
    lock = json.load(stream)
entry = lock["dependencies"]["net10.0"]["Npgsql"]
entry["contentHash"] = "AAAA"
with open(path, "w", encoding="utf-8") as stream:
    json.dump(lock, stream, indent=2)
    stream.write("\n")
PY
expect_restore_failure "content-hash-tamper" "NU1403" \
  "$tampered/src/Fogell.Store/Fogell.Store.fsproj"

dependency_tampered="$scratch/dependency-edge-tamper"
cp -a "$fixture" "$dependency_tampered"
python3 - "$dependency_tampered/src/Fogell.Store/packages.lock.json" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as stream:
    lock = json.load(stream)
entry = lock["dependencies"]["net10.0"]["Npgsql"]
entry["dependencies"]["Microsoft.Extensions.Logging.Abstractions"] = "8.0.1"
with open(path, "w", encoding="utf-8") as stream:
    json.dump(lock, stream, indent=2)
    stream.write("\n")
PY
if check_regenerated_locks "$dependency_tampered" \
  "src/Fogell.Store/Fogell.Store.fsproj" \
  >"$scratch/dependency-edge-tamper.log" 2>&1; then
  echo "dependency-lock mutation unexpectedly passed: dependency-edge tamper" >&2
  exit 1
fi
grep -Fq "regenerated lock differs" "$scratch/dependency-edge-tamper.log" \
  || { cat "$scratch/dependency-edge-tamper.log" >&2; exit 1; }
echo "mutation rejected: dependency-edge tamper (regeneration mismatch)"

echo "=== source-cleared locked restore using the populated isolated cache ==="
before="$scratch/locks.before"
after="$scratch/locks.after"
(
  cd "$repo_root"
  while IFS= read -r lock; do sha256sum "$lock"; done < <(list_locks "$repo_root")
) >"$before"
restore_locked "$repo_root/Fogell.slnx" "$scratch/source-cleared-restore.log" \
  || { cat "$scratch/source-cleared-restore.log" >&2; exit 1; }
(
  cd "$repo_root"
  while IFS= read -r lock; do sha256sum "$lock"; done < <(list_locks "$repo_root")
) >"$after"
cmp -s "$before" "$after" \
  || { echo "locked restore modified a checked-in lock file" >&2; diff -u "$before" "$after" >&2 || true; exit 1; }
echo "source-cleared locked restore passed; this is not an OS-level no-egress proof"

echo "=== no-restore build (warnings are errors for FS0025/FS0026) ==="
if ! NUGET_PACKAGES="$package_cache" \
  dotnet build "$repo_root/Fogell.slnx" -c Release --no-restore --nologo \
  >"$scratch/build.log" 2>&1; then
  cat "$scratch/build.log" >&2
  exit 1
fi
tail -5 "$scratch/build.log"
echo "dependency-lock proof passed"
