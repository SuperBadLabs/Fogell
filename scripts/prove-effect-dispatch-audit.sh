#!/usr/bin/env bash
# FG-026b. Proves scripts/audit-effect-dispatch.sh fails: one planted violation
# per question it asks, each in a scratch copy of the audited files, each
# required to produce the refusal that names that question; then the accept arm
# on an unmodified copy. A checker nobody has watched fail is itself a claim.
#
# The audit derives its root from its own path, so a copy beside a planted
# `src/` and `tests/` tree exercises it end to end without touching this repo.
set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 2
audit="$PWD/scripts/audit-effect-dispatch.sh"
[ -x "$audit" ] || { echo "EFFECT-DISPATCH AUDIT PROOF FAILED: $audit is not executable" >&2; exit 1; }
command -v rg >/dev/null 2>&1 \
  || { echo "EFFECT-DISPATCH AUDIT PROOF FAILED: rg (ripgrep) is required" >&2; exit 1; }

audited_files=(
  src/Fogell.Store/Store.fs
  src/Fogell.Controller.Host/EffectRegistry.fs
  src/Fogell.Controller.Host/EffectDispatch.fs
  src/Fogell.Controller.Host/Worker.fs
  src/Fogell.Controller.Host/Program.fs
  src/Fogell.Controller.Host/Config.fs
  src/Fogell.Controller.Api/Router.fs
  src/Fogell.Controller.Api/ArtifactSnapshots.fs
  tests/Fogell.Controller.Api.Tests/Tests.fs
)

fails=0
tmp=$(mktemp -d /tmp/fogell-fg026b-audit-proof.XXXXXX) || exit 1
trap 'rm -rf -- "$tmp"' EXIT

# One scratch root per arm so no arm inherits another's plant.
new_root() {
  local root="$tmp/case-$1"
  local file
  mkdir -p "$root/scripts"
  for file in "${audited_files[@]}"; do
    mkdir -p "$root/$(dirname "$file")"
    cp "$file" "$root/$file"
  done
  cp "$audit" "$root/scripts/audit-effect-dispatch.sh"
  printf '%s\n' "$root"
}

# expect <reject|accept> <label> <question-marker> <plant-command...>
# The plant command runs with the scratch root as its working directory.
expect() {
  local want="$1" label="$2" marker="$3"
  shift 3
  local root
  root=$(new_root "$(printf '%s' "$label" | tr -c 'a-zA-Z0-9' '-')")
  ( cd "$root" && "$@" ) || { echo "  FAILED  could not plant: $label"; fails=$((fails + 1)); return; }
  local out rc
  out=$("$root/scripts/audit-effect-dispatch.sh" 2>&1)
  rc=$?
  case "$want" in
    reject)
      if [ "$rc" -eq 1 ] && printf '%s\n' "$out" | rg -q -F -e "$marker"; then
        echo "  ok      rejects: $label"
      else
        echo "  FAILED  should reject with '$marker': $label (rc=$rc)"
        printf '%s\n' "$out" | sed 's/^/            /'
        fails=$((fails + 1))
      fi
      ;;
    accept)
      if [ "$rc" -eq 0 ]; then
        echo "  ok      accepts: $label"
      else
        echo "  FAILED  should accept: $label (rc=$rc)"
        printf '%s\n' "$out" | sed 's/^/            /'
        fails=$((fails + 1))
      fi
      ;;
  esac
}

append_line() { printf '%s\n' "$2" >>"$1"; }

# Inserts a line after the first line matching a fixed anchor.
insert_after() {
  local file="$1" anchor="$2" line="$3"
  local number
  number=$(rg -n -F -e "$anchor" "$file" | head -n 1 | cut -d: -f1)
  [ -n "$number" ] || return 1
  sed -i "${number}a\\
$line" "$file"
}

echo "=== violations the audit must REJECT ==="

expect reject "a direct ledger call in the worker bypasses EffectDispatch" \
  "question 1: ledger call outside EffectDispatch" \
  append_line src/Fogell.Controller.Host/Worker.fs \
  '    let planted (store: Store) org attempt fence = store.PrepareEffect(org, attempt, fence, "owner", "webhook:x", [| 1uy |])'

expect reject "a second AdvanceEffect call inside EffectDispatch is a second path" \
  "question 1: EffectDispatch.fs must hold exactly one PrepareEffect and one AdvanceEffect call" \
  append_line src/Fogell.Controller.Host/EffectDispatch.fs \
  '    let planted (store: Store) org attempt fence = store.AdvanceEffect(org, attempt, fence, "owner", "k", [| 1uy |], RecordApplied)'

expect reject "a second ledger path through a differently named Store binding inside EffectDispatch" \
  "question 1: EffectDispatch.fs must hold exactly one PrepareEffect and one AdvanceEffect call" \
  append_line src/Fogell.Controller.Host/EffectDispatch.fs \
  '    let plantedSecondPath (s: Store) org attempt fence = s.AdvanceEffect(org, attempt, fence, "owner", "k", [| 1uy |], RecordConfirmed)'

expect reject "a reconciliation trigger outside its bound sites" \
  "question 2: reconciliation trigger call outside its bound sites" \
  append_line src/Fogell.Controller.Api/Router.fs \
  '    let planted (store: Store) org = store.ReconcileStaleEffects(org, "api_request")'

expect reject "the worker calling the surface-free marking primitive instead of the trigger" \
  "question 2: src/Fogell.Controller.Host/Worker.fs calls MarkStaleEffectsUncertain" \
  append_line src/Fogell.Controller.Host/Worker.fs \
  '    let planted (store: Store) org = store.MarkStaleEffectsUncertain org'

expect reject "a producer case declared but not registered or routed" \
  "question 3: producer WebhookPost is declared but not registered in EffectProducer.all" \
  insert_after src/Fogell.Controller.Host/EffectRegistry.fs '    | FileDropReceipt' '    | WebhookPost'

expect reject "a producer case registered in all but not routed by EffectDispatch" \
  "question 3: producer WebhookPost is not routed by EffectDispatch" \
  bash -c '
    insert() { n=$(rg -n -F -e "$2" "$1" | head -n 1 | cut -d: -f1); sed -i "${n}a\\
$3" "$1"; }
    insert src/Fogell.Controller.Host/EffectRegistry.fs "    | FileDropReceipt" "    | WebhookPost" \
      && sed -i "s/let all = \[ EffectProducer.FileDropReceipt \]/let all = [ EffectProducer.FileDropReceipt; EffectProducer.WebhookPost ]/" src/Fogell.Controller.Host/EffectRegistry.fs \
      && sed -i "s/        | EffectProducer.FileDropReceipt -> \"file-drop-receipt\"/        | EffectProducer.FileDropReceipt -> \"file-drop-receipt\"\n        | EffectProducer.WebhookPost -> \"webhook-post\"/" src/Fogell.Controller.Host/EffectRegistry.fs'

expect reject "a producer name literal spelled outside the registry" \
  'question 3: producer name "file-drop-receipt" is spelled outside the registry' \
  append_line src/Fogell.Controller.Host/Worker.fs \
  '    let plantedKey attempt = "file-drop-receipt" + ":" + attempt'

expect reject "an unlisted HttpClient in the host" \
  "question 4: effect-bearing calls under the controller are not the pinned allow-list" \
  append_line src/Fogell.Controller.Host/Program.fs \
  '    let plantedClient = new System.Net.Http.HttpClient()'

expect reject "a second file write of an already-allowed kind in the worker" \
  "question 4: effect-bearing calls under the controller are not the pinned allow-list" \
  append_line src/Fogell.Controller.Host/Worker.fs \
  '    let plantedWrite (path: string) = File.WriteAllBytes(path, [| 0uy |])'

expect reject "an unlisted directory move in the host" \
  "question 4: effect-bearing calls under the controller are not the pinned allow-list" \
  append_line src/Fogell.Controller.Host/Program.fs \
  '    let plantedMove (source: string) (target: string) = Directory.Move(source, target)'

expect reject "a producer driven through its Invoke closure with no ledger row" \
  "question 6: effect invocation reached outside EffectDispatch" \
  append_line src/Fogell.Controller.Host/Worker.fs \
  '    let plantedBypass root claim status = (FileDropReceipt.invocation root claim status).Invoke()'

expect reject "a producer driven through a spaced Invoke call on a record pattern" \
  "question 6: effect invocation reached outside EffectDispatch" \
  append_line src/Fogell.Controller.Host/Worker.fs \
  '    let bypass ({ Identity = _ } as invocation) = invocation.Invoke ()'

expect reject "a record built with spaced Invoke = outside dispatch" \
  "question 6: effect invocation reached outside EffectDispatch" \
  append_line src/Fogell.Controller.Host/Program.fs \
  '    let planted template = { template with Invoke   =   fun () -> Ok() }'

expect reject "an EffectInvocation assembled outside dispatch" \
  "question 6: effect invocation reached outside EffectDispatch" \
  append_line src/Fogell.Controller.Api/Router.fs \
  '    let plantedInvocation (template: EffectInvocation) = { template with Invoke = fun () -> Ok() }'

expect reject "a manual reconciliation call inside the trigger test fence" \
  "question 5: manual Store call inside the no-manual-store fence" \
  insert_after tests/Fogell.Controller.Api.Tests/Tests.fs '// FG026B_NO_MANUAL_STORE_BEGIN' \
  '              store.ReconcileStaleEffects(OrganizationId Guid.Empty, "planted") |> ignore'

expect reject "the fence removed from the trigger test" \
  "question 5: the trigger test must carry exactly one FG026B_NO_MANUAL_STORE_BEGIN and one _END marker" \
  sed -i '/FG026B_NO_MANUAL_STORE_END/d' tests/Fogell.Controller.Api.Tests/Tests.fs

expect reject "the fenced test no longer running the production scan" \
  "question 5: the fenced trigger test does not run the production worker scan" \
  bash -c 'b=$(rg -n -F FG026B_NO_MANUAL_STORE_BEGIN tests/Fogell.Controller.Api.Tests/Tests.fs | cut -d: -f1); e=$(rg -n -F FG026B_NO_MANUAL_STORE_END tests/Fogell.Controller.Api.Tests/Tests.fs | cut -d: -f1); sed -i "${b},${e}s/ScanOrganization/PlantedScan/" tests/Fogell.Controller.Api.Tests/Tests.fs'

echo "=== the clean tree the audit must ACCEPT ==="
expect accept "an unmodified copy of the audited files" "" true

# Comments are prose, never calls: the accept arm must survive one.
expect accept "a comment naming Process.Start and PrepareEffect" "" \
  append_line src/Fogell.Controller.Host/Program.fs \
  '// Process.Start and store.PrepareEffect are named here in prose only.'

if [ "$fails" -ne 0 ]; then
  echo "EFFECT-DISPATCH AUDIT PROOF FAILED: $fails arm(s)" >&2
  exit 1
fi
echo "EFFECT-DISPATCH AUDIT PROOF: 18 planted violations rejected by name, 2 clean copies accepted"
