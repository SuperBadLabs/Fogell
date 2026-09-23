#!/usr/bin/env bash
# FG-269 development workload: real Domain build/tests, not the publication gate.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="$PWD/pilot-tool-home"
export NUGET_PACKAGES
NUGET_PACKAGES="$(cat pilot-cache.txt)"
[[ "$NUGET_PACKAGES" = /tmp/fogell-pilot-*/* ]] || exit 2
[[ -d "$NUGET_PACKAGES" ]] || exit 2
mkdir -p reports
# Exercise both workspace-independent home data and the pipeline's stash so
# retention must reclaim all controller-owned payload roots, not just outputs.
mkdir -p "$HOME/fg269-cache"
head -c 65536 /dev/zero > "$HOME/fg269-cache/payload.bin"
dotnet --version > reports/sdk.txt
dotnet restore tests/Fogell.Domain.Tests/Fogell.Domain.Tests.fsproj \
  --locked-mode --configfile pilot-nuget.config -p:NuGetAudit=false
dotnet build tests/Fogell.Domain.Tests/Fogell.Domain.Tests.fsproj \
  -c Release --no-restore -p:NuGetAudit=false
test_exit=0
dotnet tests/Fogell.Domain.Tests/bin/Release/net10.0/Fogell.Domain.Tests.dll \
  --sequenced --no-spinner --colours 0 --junit-summary reports/domain.xml \
  --summary > reports/tests.txt 2>&1 || test_exit=$?
cat reports/tests.txt
[[ "$test_exit" = 0 || "$test_exit" = 1 ]] || exit "$test_exit"
[[ -s reports/domain.xml ]] || exit 2
printf '%s\n' "$test_exit" > reports/test-exit.txt
# JUnit publishes the typed test failure and unstable result in the next step.
