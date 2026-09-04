#!/usr/bin/env bash
# FG-235 — prove CR/LF-bearing credential values remain refused outside
# FG-236's single-line raw-output matching grammar.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
scratch=$(mktemp -d /tmp/fogell-fg235-mutant.XXXXXX)
trap 'rm -rf "$scratch"' EXIT

# Copy current-worktree bytes so the proof binds the candidate, including
# uncommitted review fixes, while keeping mutant bin/obj output out of it.
git -C "$repo" ls-files -z -- \
  src tests/Fogell.Execution.Tests tests/Fogell.Differential.Tests \
  Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  | tar -C "$repo" --null -T - -cf - \
  | tar -xf - -C "$scratch"

execution_project="$scratch/tests/Fogell.Execution.Tests/Fogell.Execution.Tests.fsproj"
differential_project="$scratch/tests/Fogell.Differential.Tests/Fogell.Differential.Tests.fsproj"
secrets="$scratch/src/Fogell.Execution/Secrets.fs"
walker="$scratch/src/Fogell.Differential/WalkerOrchestration.fs"
execution_filter='FG-071 masking is ON the output path'
differential_filter='FG-235 multiline credential progressive-output refusal'

bash -ic "dotnet restore '$execution_project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet restore '$differential_project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet build '$execution_project' -c Release --no-restore -m:1"
bash -ic "dotnet build '$differential_project' -c Release --no-restore -m:1"
dotnet run --project "$execution_project" -c Release --no-build -- \
  --filter-test-list "$execution_filter" --sequenced >/dev/null
dotnet run --project "$differential_project" -c Release --no-build -- \
  --filter-test-list "$differential_filter" --sequenced >/dev/null

predicate='        not (isNull value)'
[[ $(rg -F -c "$predicate" "$secrets") == 1 ]] \
  || { echo 'FG-235 proof: central predicate mutation target is not unique' >&2; exit 1; }
sed -i "s/^$predicate$/        false/" "$secrets"
rg -F '        false' "$secrets" >/dev/null \
  || { echo 'FG-235 proof: central predicate mutant did not change bytes' >&2; exit 1; }

set +e
bash -ic "dotnet build '$execution_project' -c Release --no-restore -m:1" >/dev/null
predicate_execution_build_rc=$?
bash -ic "dotnet build '$differential_project' -c Release --no-restore -m:1" >/dev/null
predicate_differential_build_rc=$?
predicate_execution_output=$(dotnet run --project "$execution_project" -c Release --no-build -- \
  --filter-test-list "$execution_filter" --sequenced 2>&1)
predicate_execution_rc=$?
predicate_differential_output=$(dotnet run --project "$differential_project" -c Release --no-build -- \
  --filter-test-list "$differential_filter" --sequenced 2>&1)
predicate_differential_rc=$?
set -e

(( predicate_execution_build_rc == 0 && predicate_differential_build_rc == 0 )) \
  || { echo 'FG-235 proof: central predicate mutant did not compile' >&2; exit 1; }
(( predicate_execution_rc != 0 )) \
  || { echo 'FG-235 proof: direct-binding predicate mutant survived' >&2; exit 1; }
(( predicate_differential_rc != 0 )) \
  || { echo 'FG-235 proof: runtime predicate mutant survived' >&2; exit 1; }
printf '%s\n' "$predicate_execution_output" | rg -F 'multiline text secrets refuse before binding' >/dev/null \
  || { printf '%s\n' "$predicate_execution_output" >&2; echo 'FG-235 proof: direct mutant failed elsewhere' >&2; exit 1; }
printf '%s\n' "$predicate_differential_output" | rg -F 'mixed safe and multiline requests refuse atomically' >/dev/null \
  || { printf '%s\n' "$predicate_differential_output" >&2; echo 'FG-235 proof: runtime mutant failed elsewhere' >&2; exit 1; }

# Restore the predicate, then bypass only the walker's user-facing preflight.
# The binding backstop must throw, making the end-to-end test fail outside the
# normal runtime trace rather than silently executing the body.
cp "$repo/src/Fogell.Execution/Secrets.fs" "$secrets"
preflight='                elif not (List.isEmpty lineBreakRefusals) then'
[[ $(rg -F -c "$preflight" "$walker") == 1 ]] \
  || { echo 'FG-235 proof: runtime preflight mutation target is not unique' >&2; exit 1; }
sed -i "s/^$preflight$/                elif false then/" "$walker"

set +e
bash -ic "dotnet build '$differential_project' -c Release --no-restore -m:1" >/dev/null
preflight_build_rc=$?
preflight_output=$(dotnet run --project "$differential_project" -c Release --no-build -- \
  --filter-test-list "$differential_filter" --sequenced 2>&1)
preflight_rc=$?
set -e

(( preflight_build_rc == 0 )) \
  || { echo 'FG-235 proof: runtime preflight mutant did not compile' >&2; exit 1; }
(( preflight_rc != 0 )) \
  || { echo 'FG-235 proof: runtime preflight mutant survived' >&2; exit 1; }
printf '%s\n' "$preflight_output" | rg -F 'pipeline refused outside execution' >/dev/null \
  || { printf '%s\n' "$preflight_output" >&2; echo 'FG-235 proof: preflight mutant failed elsewhere' >&2; exit 1; }

echo 'FG-235 PROOF PASS: baseline passed; central-refusal and runtime-preflight mutants compiled and were killed'
