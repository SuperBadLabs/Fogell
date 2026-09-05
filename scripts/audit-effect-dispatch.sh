#!/usr/bin/env bash
# FG-026b. Closed-world registry/dispatch audit: can any modelled producer
# bypass the effect ledger?
#
# The compile-time half of the closed world is the exhaustive match over
# EffectProducer in EffectRegistry.fs and EffectDispatch.fs (FS0025 is an error
# in Directory.Build.props). This is the source half, and it asks six questions
# the compiler cannot:
#
#   1. Is EffectDispatch.run the ONLY caller of the ledger under src/? A direct
#      Store.PrepareEffect/AdvanceEffect call anywhere else is a producer that
#      chose its own key, digest and window, outside the audited path.
#   2. Are the reconciliation triggers bound to exactly the production sites
#      (the worker's lease-expiry scan and the host's startup pass) and the
#      Store itself? A third caller is a trigger nobody proved.
#   3. Is every declared EffectProducer case registered in EffectProducer.all,
#      routed by EffectDispatch, and named by one literal that appears nowhere
#      else under the controller? A case the dispatch does not route cannot
#      reach a destination through the ledger; a second spelling of its name is
#      a key minted outside the registry.
#   4. Are the effect-bearing calls under the controller projects exactly the
#      pinned allow-list? A new HttpClient, socket, process launch or file write
#      is a producer that never declared itself.
#   5. Does the in-process trigger test stay inside its FG026B_NO_MANUAL_STORE
#      fence — no manual reconciliation call standing in for the production
#      trigger?
#   6. Are a producer's Invoke/Confirm closures and the connector's invocation
#      builder reached only from EffectDispatch.fs? A `.Invoke()` elsewhere
#      drives the destination with no ledger row at all.
#
# Comment lines are excluded from every scan (a comment naming Process.Start is
# prose, not a call). scripts/prove-effect-dispatch-audit.sh plants one
# violation per question in a scratch copy and requires the named refusal, and
# runs the accept arm on a clean copy. Exit 0 is a pass; every refusal names
# its question and its file.
#
# LIMITS, so nobody mistakes a pass for a proof: this is a name-based source
# tripwire over src/ (and one fenced test region). A helper defined outside a
# scanned pattern and called inside it, a reflection call, or a spelling this
# pattern does not know is not caught. "Closed world" is this tripwire plus the
# compile-time exhaustive match and the registry test, not a formal proof.
set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 2

command -v rg >/dev/null 2>&1 \
  || { echo "FG-026b DISPATCH AUDIT REFUSED: rg (ripgrep) is required" >&2; exit 2; }

store_fs="src/Fogell.Store/Store.fs"
host_dir="src/Fogell.Controller.Host"
api_dir="src/Fogell.Controller.Api"
registry_fs="$host_dir/EffectRegistry.fs"
dispatch_fs="$host_dir/EffectDispatch.fs"
worker_fs="$host_dir/Worker.fs"
program_fs="$host_dir/Program.fs"
api_tests_fs="tests/Fogell.Controller.Api.Tests/Tests.fs"

for required in "$store_fs" "$registry_fs" "$dispatch_fs" "$worker_fs" "$program_fs" "$api_tests_fs"; do
  [ -f "$required" ] \
    || { echo "FG-026b DISPATCH AUDIT REFUSED: missing $required" >&2; exit 2; }
done

problems=0
refuse() {
  echo "FG-026b DISPATCH AUDIT REFUSED: $*" >&2
  problems=$((problems + 1))
}

# Non-comment lines of one file matching a pattern, as "file:line:text".
hits() {
  local pattern="$1"
  local file="$2"
  rg -n -e "$pattern" "$file" 2>/dev/null | rg -v '^[0-9]+:[[:space:]]*//' | sed "s|^|$file:|"
}

# ---- 1. the ledger has one caller -------------------------------------------
ledger_pattern='PrepareEffect|AdvanceEffect|RecordApplied|RecordConfirmed'
while IFS= read -r file; do
  case "$file" in
    "$store_fs"|"$dispatch_fs") continue ;;
  esac
  offending=$(hits "$ledger_pattern" "$file")
  [ -z "$offending" ] \
    || refuse "question 1: ledger call outside EffectDispatch in $file:"$'\n'"$offending"
done < <(rg -l -e "$ledger_pattern" src --glob '*.fs' | sort)

# Every non-comment token, not only the `store.` spelling: a second path
# through a differently named Store binding is still a second path.
dispatch_ledger_tokens=$(hits '\b(PrepareEffect|AdvanceEffect)\b' "$dispatch_fs" | rg -o -e '\b(PrepareEffect|AdvanceEffect)\b' | wc -l | tr -d ' ')
[ "$dispatch_ledger_tokens" = 2 ] \
  || refuse "question 1: EffectDispatch.fs must hold exactly one PrepareEffect and one AdvanceEffect call, observed $dispatch_ledger_tokens ledger token(s)"

# ---- 2. the triggers have bound sites ----------------------------------------
trigger_pattern='MarkStaleEffectsUncertain|ReconcileStaleEffects|ActivateRestore'
while IFS= read -r file; do
  case "$file" in
    "$store_fs"|"$worker_fs"|"$program_fs") continue ;;
  esac
  offending=$(hits "$trigger_pattern" "$file")
  [ -z "$offending" ] \
    || refuse "question 2: reconciliation trigger call outside its bound sites in $file:"$'\n'"$offending"
done < <(rg -l -e "$trigger_pattern" src --glob '*.fs' | sort)

# One site in the worker: the reconciliation cadence, which does not wait on
# claim execution (the scan-loop pass was removed by verifier P2-2 on #424).
worker_triggers=$(hits 'store\.ReconcileStaleEffects\(' "$worker_fs" | wc -l | tr -d ' ')
[ "$worker_triggers" = 1 ] \
  || refuse "question 2: Worker.fs must hold exactly one lease-expiry ReconcileStaleEffects site (the periodic cadence), observed $worker_triggers"
program_triggers=$(hits '\.ReconcileStaleEffects\(' "$program_fs" | wc -l | tr -d ' ')
[ "$program_triggers" = 1 ] \
  || refuse "question 2: Program.fs must hold exactly one startup ReconcileStaleEffects site, observed $program_triggers"
for forbidden in MarkStaleEffectsUncertain ActivateRestore; do
  for file in "$worker_fs" "$program_fs"; do
    offending=$(hits "$forbidden" "$file")
    [ -z "$offending" ] \
      || refuse "question 2: $file calls $forbidden, which is not a production trigger:"$'\n'"$offending"
  done
done

# ---- 3. the registry is closed -----------------------------------------------
# Cases are the `| Name` lines between `type EffectProducer =` and the next
# top-level type or module in EffectRegistry.fs.
cases=$(awk '
  /^type EffectProducer =/ { inside = 1; next }
  inside && /^(type|module|\[<)/ { inside = 0 }
  inside && /^[[:space:]]*\| [A-Za-z0-9_]+/ { sub(/^[[:space:]]*\| /, ""); sub(/[[:space:]].*$/, ""); print }
' "$registry_fs")
[ -n "$cases" ] || refuse "question 3: no EffectProducer case was found in $registry_fs"

all_line=$(rg -n -e '^\s*let all = \[' "$registry_fs" | head -n 1)
[ -n "$all_line" ] || refuse "question 3: EffectProducer.all is missing from $registry_fs"

for case_name in $cases; do
  printf '%s\n' "$all_line" | rg -q -e "EffectProducer\.$case_name\b" \
    || refuse "question 3: producer $case_name is declared but not registered in EffectProducer.all"
  [ -n "$(hits "EffectProducer\.$case_name\b" "$dispatch_fs")" ] \
    || refuse "question 3: producer $case_name is not routed by EffectDispatch"
  literal=$(rg -o -e "\| EffectProducer\.$case_name -> \"[^\"]+\"" "$registry_fs" | sed 's/.*-> "\([^"]*\)"/\1/' | head -n 1)
  [ -n "$literal" ] \
    || { refuse "question 3: producer $case_name has no name literal in EffectProducer.name"; continue; }
  while IFS= read -r file; do
    [ "$file" = "$registry_fs" ] && continue
    offending=$(hits "\"$literal\"" "$file")
    [ -z "$offending" ] \
      || refuse "question 3: producer name \"$literal\" is spelled outside the registry in $file:"$'\n'"$offending"
  done < <(rg -l -F -e "\"$literal\"" "$host_dir" "$api_dir" --glob '*.fs' | sort)
done

# ---- 4. effect-bearing calls are exactly the allow-list ----------------------
effect_pattern='HttpClient|SmtpClient|Socket|TcpClient|WebRequest|Process\.Start|new Process\b|Environment\.Exit|File\.Move|File\.Copy|File\.Create|File\.Open|File\.WriteAll[A-Za-z]*|FileStream|Directory\.Move|\.Kill\(\)|DllImport'
# Per-file token counts, one line per (file, token), so an added call of an
# already-allowed kind is refused as well as a new kind.
observed=$(
  for file in "$host_dir"/*.fs "$api_dir"/*.fs; do
    hits "$effect_pattern" "$file" | sed 's/^\([^:]*\):[0-9]*:\(.*\)$/\1\t\2/' \
      | while IFS=$'\t' read -r matched_file text; do
          printf '%s\n' "$text" | rg -o -e "$effect_pattern" | sed "s|^|$matched_file |"
        done
  done | LC_ALL=C sort | uniq -c | sed 's/^ *//' | LC_ALL=C sort
)
# The allow-list, per file and token: every native entry point (DllImport) under
# the controller, the descriptor-bound receipt writer and evidence reader in
# EffectRegistry.fs (shared by dispatch and the startup probe), the kill hook, the
# worker's atomic definition write, child launch and event-stream reader, the
# readiness probes in Config.fs, the FG-251 secure
# token-file reader in Config.fs, the artifact reader in Router.fs, and the
# staging -> snapshot move in ArtifactSnapshots.fs.
expected=$(printf '%s\n' \
  "1 $dispatch_fs .Kill()" \
  "7 $registry_fs DllImport" \
  "1 $worker_fs File.Move" \
  "1 $worker_fs File.WriteAllBytes" \
  "1 $worker_fs FileStream" \
  "1 $worker_fs new Process" \
  "2 $host_dir/Config.fs File.Open" \
  "4 $host_dir/Config.fs DllImport" \
  "4 $host_dir/ProcessGroup.fs DllImport" \
  "3 $host_dir/Config.fs FileStream" \
  "2 $registry_fs FileStream" \
  "3 $api_dir/Router.fs FileStream" \
  "3 $api_dir/Router.fs DllImport" \
  "1 $api_dir/ArtifactSnapshots.fs Directory.Move" | LC_ALL=C sort)
if [ "$observed" != "$expected" ]; then
  refuse "question 4: effect-bearing calls under the controller are not the pinned allow-list"$'\n'"expected:"$'\n'"$expected"$'\n'"observed:"$'\n'"${observed:-<none>}"
fi

# ---- 5. the trigger test makes no manual Store call --------------------------
begin_count=$(rg -c -F 'FG026B_NO_MANUAL_STORE_BEGIN' "$api_tests_fs" || true)
end_count=$(rg -c -F 'FG026B_NO_MANUAL_STORE_END' "$api_tests_fs" || true)
if [ "${begin_count:-0}" != 1 ] || [ "${end_count:-0}" != 1 ]; then
  refuse "question 5: the trigger test must carry exactly one FG026B_NO_MANUAL_STORE_BEGIN and one _END marker (observed ${begin_count:-0}/${end_count:-0})"
else
  begin_line=$(rg -n -F 'FG026B_NO_MANUAL_STORE_BEGIN' "$api_tests_fs" | cut -d: -f1)
  end_line=$(rg -n -F 'FG026B_NO_MANUAL_STORE_END' "$api_tests_fs" | cut -d: -f1)
  if [ "$begin_line" -ge "$end_line" ]; then
    refuse "question 5: the no-manual-store fence ends before it begins"
  else
    fenced=$(sed -n "$((begin_line + 1)),$((end_line - 1))p" "$api_tests_fs" \
      | rg -v '^[[:space:]]*//' \
      | rg -n -e 'MarkStaleEffectsUncertain|ReconcileStaleEffects|ActivateRestore|RequeueExpiredLocalAttempts' || true)
    [ -z "$fenced" ] \
      || refuse "question 5: manual Store call inside the no-manual-store fence:"$'\n'"$fenced"
    [ "$(sed -n "$((begin_line + 1)),$((end_line - 1))p" "$api_tests_fs" | rg -c 'ScanOrganization' || true)" -ge 1 ] \
      || refuse "question 5: the fenced trigger test does not run the production worker scan"
  fi
fi

# ---- 6. the invocation closures are reachable only through dispatch ---------
# A producer's Invoke/Confirm closures and the connector's invocation builder
# are public F# values; nothing stops another file in the host from calling
# `(FileDropReceipt.invocation ...).Invoke()` and driving the destination with
# no ledger row at all. Pin every spelling of that reach to EffectDispatch.fs.
# Spacing-tolerant: `invocation.Invoke ()`, `{ Invoke = ... }` and the record
# pattern `{ Invoke = f }` are the same reach as `.Invoke(`.
invocation_pattern='\bInvoke\s*\(|\.Invoke\b|\bInvoke\s*=|\bDestination\s*=|FileDropReceipt\.(invocation|pinned)|EffectInvocation'
while IFS= read -r file; do
  [ "$file" = "$dispatch_fs" ] && continue
  offending=$(hits "$invocation_pattern" "$file")
  [ -z "$offending" ] \
    || refuse "question 6: effect invocation reached outside EffectDispatch in $file:"$'\n'"$offending"
done < <(rg -l -e "$invocation_pattern" src --glob '*.fs' | sort)

if [ "$problems" -ne 0 ]; then
  echo "FG-026b DISPATCH AUDIT FAILED: $problems problem(s)" >&2
  exit 1
fi

case_count=$(printf '%s\n' "$cases" | rg -c . || true)
echo "FG-026b DISPATCH AUDIT: ledger called only from EffectDispatch; triggers bound to the worker scan and startup; $case_count registered producer(s) each routed and uniquely named; effect-bearing calls are the pinned allow-list; the trigger test makes no manual Store call; invocation closures are reached only through dispatch"
