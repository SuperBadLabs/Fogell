#!/usr/bin/env bash
# FG-207 — prove that a completed step and its optional reason are one locked
# append group and one EveryStep durability force. strace strengthens the proof
# when installed; the in-process observer is the deterministic cross-platform
# gate and can only observe a completed real Flush(true).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

journal_project=tests/Fogell.Journal.Tests/Fogell.Journal.Tests.fsproj
host_project=tools/Fogell.Run.Host/Fogell.Run.Host.fsproj
journal_dll=tests/Fogell.Journal.Tests/bin/Release/net10.0/Fogell.Journal.Tests.dll
host_dll=tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host.dll

# The observer is notification after the owned durability operation, never its
# replacement. Make that ownership load-bearing even where strace is absent:
# exactly one real force belongs to the private wrapper and it precedes the
# observer. This exact source guard is followed by an unconditional rebuild, so
# stale binaries cannot satisfy the proof after a source mutation.
force_region="$(sed -n '/let force origin recordCount (s: FileStream) =/,/let ensure () =/p' src/Fogell.Journal/Journal.fs)"
[[ "$(printf '%s\n' "$force_region" | rg -c '^        s\.Flush true$')" -eq 1 ]]
[[ "$(printf '%s\n' "$force_region" | rg -c '^        forceObserver$')" -eq 1 ]]
flush_line="$(printf '%s\n' "$force_region" | rg -n '^        s\.Flush true$' | cut -d: -f1)"
observer_line="$(printf '%s\n' "$force_region" | rg -n '^        forceObserver$' | cut -d: -f1)"
[[ "$flush_line" -lt "$observer_line" ]]

# Host owns exactly one typed completion call. Reintroducing the old two-hook
# mapping can preserve bytes while paying two forces, so the one-call boundary
# is part of the mandatory proof rather than something strace availability may
# decide.
[[ "$(rg -c 'journal\.AppendStepFinished\(stage, i, status, reason\)' tools/Fogell.Run.Host/Program.fs)" -eq 1 ]]
if rg -q 'OnStepReason|journal\.Append\(StepFinished|journal\.Append\(StepReason' \
  tools/Fogell.Run.Host/Program.fs src/Fogell.Differential/WalkerOrchestration.fs; then
  echo "FG-207 split step-completion mapping returned"
  exit 1
fi

# The current executor usually clears diagnostics before a green completion,
# so an ordinary retry alone cannot prove the defensive status boundary. Pin
# its four semantic ingredients: Failure/Aborted may consume LastDiagnostic;
# every green/unstable completion must supply None to the typed Journal API.
reason_region="$(sed -n '/let reason =/,/hooks.OnStepFinished stage.Name i observed.Value reason/p' src/Fogell.Differential/WalkerOrchestration.fs)"
[[ "$(printf '%s\n' "$reason_region" | rg -c 'observed\.Value = BuildStatus\.Failure')" -eq 1 ]]
[[ "$(printf '%s\n' "$reason_region" | rg -c 'observed\.Value = BuildStatus\.Aborted')" -eq 1 ]]
[[ "$(printf '%s\n' "$reason_region" | rg -c 'observing\.LastDiagnostic\.Value')" -eq 1 ]]
[[ "$(printf '%s\n' "$reason_region" | rg -c '^                                        None$')" -eq 1 ]]

dotnet build "$journal_project" -c Release --nologo >/dev/null
dotnet build "$host_project" -c Release --nologo >/dev/null

dotnet "$journal_dll" --filter-test-list "FG-207 grouped step completion durability"

proof_root="$(mktemp -d /tmp/fogell-fg207-proof.XXXXXX)"
trap 'rm -rf -- "$proof_root"' EXIT

probe_journal="$proof_root/probe.journal"
dotnet "$journal_dll" --fg207-probe "$probe_journal"
mapfile -t probe_lines < "$probe_journal"
[[ ${#probe_lines[@]} -eq 2 ]]
[[ "${probe_lines[0]}" == $'step-finished\tProbe\t0\tfailure' ]]
[[ "${probe_lines[1]}" == $'step-reason\tProbe\t0\tscript returned exit code 7' ]]

if command -v strace >/dev/null 2>&1; then
  trace_journal="$proof_root/strace.journal"
  trace_log="$proof_root/strace.log"
  strace -f -yy -s 4096 -e trace=write,pwrite64,fsync,fdatasync \
    -o "$trace_log" dotnet "$journal_dll" --fg207-probe "$trace_journal"
  force_count="$({ rg -F "$trace_journal" "$trace_log" || true; } | rg -c 'fsync|fdatasync' || true)"
  [[ "$force_count" -eq 1 ]] || {
    echo "FG-207 strace proof expected one force for the journal, observed $force_count"
    exit 1
  }
  echo "FG-207 optional strace proof: one journal force"
else
  echo "FG-207 optional strace proof skipped: strace is unavailable"
fi

failure_file="$proof_root/failure.Jenkinsfile"
failure_root="$proof_root/failure-root"
failure_journal="$proof_root/failure.journal"
mkdir -p "$failure_root"
printf '%s\n' \
  'pipeline {' \
  '  agent any' \
  '  stages {' \
  "    stage('Probe') { steps { sh 'exit 7' } }" \
  '  }' \
  '}' > "$failure_file"

set +e
dotnet "$host_dll" "$failure_file" "$failure_root" job "$failure_journal" >"$proof_root/failure.out" 2>&1
failure_rc=$?
set -e
[[ "$failure_rc" -ne 0 ]]
mapfile -t failure_finish < <(rg -n $'^step-finished\tProbe\t0\tfailure$' "$failure_journal" | cut -d: -f1)
mapfile -t failure_reason < <(rg -n $'^step-reason\tProbe\t0\tscript returned exit code 7$' "$failure_journal" | cut -d: -f1)
[[ ${#failure_finish[@]} -eq 1 ]]
[[ ${#failure_reason[@]} -eq 1 ]]
[[ "${failure_reason[0]}" -eq "$((failure_finish[0] + 1))" ]]

retry_file="$proof_root/retry.Jenkinsfile"
retry_root="$proof_root/retry-root"
retry_journal="$proof_root/retry.journal"
mkdir -p "$retry_root"
printf '%s\n' \
  'pipeline {' \
  '  agent any' \
  '  stages {' \
  "    stage('Probe') { steps { retry(2) { sh 'if [ -f attempt ]; then exit 0; else touch attempt; exit 7; fi' } } }" \
  '  }' \
  '}' > "$retry_file"

dotnet "$host_dll" "$retry_file" "$retry_root" job "$retry_journal" >"$proof_root/retry.out" 2>&1
[[ "$(rg -c $'^step-finished\tProbe\t0\tsuccess$' "$retry_journal")" -eq 1 ]]
if rg -q $'^step-reason\tProbe\t0\t' "$retry_journal"; then
  echo "FG-207 stale diagnostic escaped into a successful completion"
  exit 1
fi

echo "FG-207 grouped step-finish force proof passed"
