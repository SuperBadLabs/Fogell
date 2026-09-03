#!/usr/bin/env bash
# FG-236 — prove separator-transparent masking lives before physical-line
# framing, remains grammar-bounded, and cannot corrupt the private PGID frame.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

scratch="$(mktemp -d /tmp/fogell-fg236-mutant.XXXXXX)"
trap 'rm -rf "$scratch"' EXIT

git ls-files -z -- \
  src tests/Fogell.Execution.Tests tests/Fogell.Differential.Tests \
  Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  | tar --null -T - -cf - \
  | tar -xf - -C "$scratch"

project="$scratch/tests/Fogell.Execution.Tests/Fogell.Execution.Tests.fsproj"
differential_project="$scratch/tests/Fogell.Differential.Tests/Fogell.Differential.Tests.fsproj"
redaction="$scratch/src/Fogell.Execution/OutputRedaction.fs"
executor="$scratch/src/Fogell.Execution/Executor.fs"
process_group="$scratch/src/Fogell.Execution/ProcessGroup.fs"
secrets="$scratch/src/Fogell.Execution/Secrets.fs"
walker_step="$scratch/src/Fogell.Differential/WalkerStep.fs"
walker_ctx="$scratch/src/Fogell.Differential/WalkerCtx.fs"
filter='FG-236'

bash -ic "dotnet restore '$project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet restore '$differential_project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet build '$project' -c Release --no-restore -m:1"
bash -ic "dotnet build '$differential_project' -c Release --no-restore -m:1"
dotnet run --project "$project" -c Release --no-build -- \
  --filter-test-case "$filter" --sequenced >/dev/null
dotnet run --project "$differential_project" -c Release --no-build -- \
  --filter-test-case "$filter" --sequenced >/dev/null

kill_mutant() {
  local label=$1 expected=$2
  set +e
  bash -ic "dotnet build '$project' -c Release --no-restore -m:1" >/dev/null
  local build_rc=$?
  local output
  output=$(dotnet run --project "$project" -c Release --no-build -- \
    --filter-test-case "$filter" --sequenced 2>&1)
  local test_rc=$?
  set -e

  (( build_rc == 0 )) || { echo "FG-236 proof: $label mutant did not compile" >&2; exit 1; }
  (( test_rc != 0 )) || { echo "FG-236 proof: $label mutant survived" >&2; exit 1; }
  printf '%s\n' "$output" | rg -F "$expected" >/dev/null \
    || { printf '%s\n' "$output" >&2; echo "FG-236 proof: $label mutant failed elsewhere" >&2; exit 1; }
}

kill_differential_mutant() {
  local label=$1 expected=$2
  set +e
  bash -ic "dotnet build '$differential_project' -c Release --no-restore -m:1" >/dev/null
  local build_rc=$?
  local output
  output=$(dotnet run --project "$differential_project" -c Release --no-build -- \
    --filter-test-case "$filter" --sequenced 2>&1)
  local test_rc=$?
  set -e

  (( build_rc == 0 )) || { echo "FG-236 proof: $label mutant did not compile" >&2; exit 1; }
  (( test_rc != 0 )) || { echo "FG-236 proof: $label mutant survived" >&2; exit 1; }
  printf '%s\n' "$output" | rg -F "$expected" >/dev/null \
    || { printf '%s\n' "$output" >&2; echo "FG-236 proof: $label mutant failed elsewhere" >&2; exit 1; }
}

cp "$redaction" "$scratch/redaction.clean"
cp "$secrets" "$scratch/secrets.clean"
cp "$walker_step" "$scratch/walker-step.clean"
cp "$walker_ctx" "$scratch/walker-ctx.clean"

# An incomplete whole-buffer prefix can still end in another complete form.
# Publishing pending characters wholesale at EOF recreates that credential leak.
target='            finalizeAll output'
[[ $(rg -F -c "$target" "$redaction") == 2 ]] \
  || { echo 'FG-236 proof: EOF-suffix mutation targets drifted' >&2; exit 1; }
sed -i '/member _.CompleteRedacted()/,/output.ToRedactedText()/ s/^            finalizeAll output$/            while pending.Count > 0 do let item = pending.Dequeue() in output.AppendRaw item.Character; output.AppendRaw(item.TrailingSeparator.ToString())/' "$redaction"
kill_mutant eof-suffix "EOF replays a shorter match's suffix through overlapping forms"

# If repeated line endings are treated as transparent forever, matcher memory is
# no longer grammar-bounded and unrelated split text can be falsely redacted.
cp "$scratch/redaction.clean" "$redaction"
target='        if separatorCount >= 2 then'
[[ $(rg -F -c "$target" "$redaction") == 2 ]] \
  || { echo 'FG-236 proof: grammar mutation targets drifted' >&2; exit 1; }
sed -i 's/^        if separatorCount >= 2 then$/        if false then/' "$redaction"
kill_mutant grammar 'two physical separators terminate adjacency'

# A safe front character must publish as soon as it no longer begins any live
# candidate. Waiting for the globally longest registered form stalls short
# lines behind unrelated long credentials and breaks progressive delivery.
cp "$scratch/redaction.clean" "$redaction"
target='        while pending.Count > 0 && not (frontHasUnfinishedCandidate ()) do'
[[ $(rg -F -c "$target" "$redaction") == 1 ]] \
  || { echo 'FG-236 proof: progressive mutation target is not unique' >&2; exit 1; }
sed -i 's/^        while pending.Count > 0 && not (frontHasUnfinishedCandidate ()) do$/        while pending.Count > 0 \&\& (logicalIndex - pending.Peek().Index + 1L >= int64 longestForm) do/' "$redaction"
kill_mutant progressive 'a safe line is delivered while the credential-bearing process is still running'

# The pure matcher is insufficient if Executor fails to hand the opaque policy
# to ProcessGroup: this recreates the original line-local leak.
cp "$scratch/redaction.clean" "$redaction"
cp "$executor" "$scratch/executor.clean"
target='                        OutputRedaction = outputRedaction'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: wiring mutation target is not unique' >&2; exit 1; }
sed -i "s/^$target$/                        OutputRedaction = None/" "$executor"
kill_mutant wiring 'masks wrapped base64 in progressive and buffered shell output'

# Redacting the bootstrap frame before parsing it lets a credential overlapping
# the marker erase the process-group identity and disable containment.
cp "$scratch/executor.clean" "$executor"
cp "$process_group" "$scratch/process-group.clean"
target='                Some(startRedactingReader proc.StandardError stderrCallbackGate stderrClosed handleRedactedStderrLine true)'
[[ $(rg -F -c "$target" "$process_group") == 1 ]] \
  || { echo 'FG-236 proof: control-frame mutation target is not unique' >&2; exit 1; }
sed -i "s/handleRedactedStderrLine true)/handleRedactedStderrLine false)/" "$process_group"
kill_mutant control-frame 'parses the private process-group frame before overlapping credential redaction'
cp "$scratch/process-group.clean" "$process_group"

# A provenance-bearing process callback is still a public output callback. If
# its reader does not reach EOF inside the bounded drain window, returning
# success would bless a truncated stream merely because it bypassed OnLine.
target='                || Option.isSome request.OnRedactedAdmission'
[[ $(rg -F -c "$target" "$process_group") == 1 ]] \
  || { echo 'FG-236 proof: reader-enforcement mutation target is not unique' >&2; exit 1; }
sed -i 's/^                || Option\.isSome request\.OnRedactedAdmission$/                || false/' "$process_group"
kill_mutant reader-enforcement 'a provenance callback cannot turn bounded reader truncation into success'
cp "$scratch/process-group.clean" "$process_group"

# A bounded capture snapshot is not EOF. Completing its matcher publishes an
# attacker-selected secret prefix that the escaped pipe holder may later finish.
cp "$scratch/process-group.clean" "$process_group"
target='                    (policy.MaskAvailablePrefixRedacted runResult.Stdout).Text'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: capture-cutoff mutation target is not unique' >&2; exit 1; }
sed -i 's/policy\.MaskAvailablePrefixRedacted runResult\.Stdout/policy.MaskRedacted runResult.Stdout/' "$executor"
kill_mutant capture-cutoff 'withholds an ambiguous capture prefix at bounded cutoff'

# Warning text is synthesized after raw matching and therefore needs the
# ordinary callback. Inheriting the raw callback can expose a second credential
# whose value overlaps the warning's variable name.
cp "$scratch/executor.clean" "$executor"
target='                                generatedLine |> Option.iter (fun emit -> leakReports.Add note; emit note)'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: generated-warning mutation target is not unique' >&2; exit 1; }
sed -i 's/^                                generatedLine |> Option\.iter (fun emit -> leakReports\.Add note; emit note)$/                                decoded |> Option.iter (fun emit -> leakReports.Add note; emit note)/' "$executor"
kill_mutant generated-warning 'the synthesized warning crosses the ordinary run-wide masker'

# A raw-only caller has no ordinary sink. Marking a warning reported before an
# actual emission filters it out of the later buffered warning pass.
cp "$scratch/executor.clean" "$executor"
target='                                generatedLine |> Option.iter (fun emit -> leakReports.Add note; emit note)'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: missing-warning-sink mutation target is not unique' >&2; exit 1; }
sed -i 's/^                                generatedLine |> Option\.iter (fun emit -> leakReports\.Add note; emit note)$/                                leakReports.Add note; decoded |> Option.iter (fun emit -> emit note)/' "$executor"
kill_mutant missing-warning-sink 'the missing ordinary sink keeps the warning buffered'

# ProcessGroup authors timeout/cancellation narration after its raw matcher.
# Losing the ordinary callback routes those lines to the raw callback fallback
# and can expose a credential whose value overlaps the fixed narration.
cp "$scratch/executor.clean" "$executor"
target='                            else Some(defaultArg generatedLine ignore)'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: generated-termination mutation target is not unique' >&2; exit 1; }
sed -i 's/^                            else Some(defaultArg generatedLine ignore)$/                            else None/' "$executor"
kill_mutant generated-termination 'the synthesized termination line crosses the ordinary run-wide masker'
cp "$scratch/executor.clean" "$executor"

# Direct Executor callers historically supply only OnLine. Generated lifecycle
# narration must still cross an ordinary live-inventory masker before reaching
# that callback; otherwise a credential equal to "Terminated" is disclosed.
target='                        fun line -> emit (Secrets.mask (secretsForOutput ()) line))'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: direct-generated-callback mutation target is not unique' >&2; exit 1; }
sed -i 's/^                        fun line -> emit (Secrets\.mask (secretsForOutput ()) line))$/                        fun line -> emit line)/' "$executor"
kill_mutant direct-generated-callback 'a direct callback cannot receive the credential-shaped generated line'
cp "$scratch/executor.clean" "$executor"

# A binding can land after a stream matcher's chunk snapshot and be learned by
# its queued callback. Omitting the final live-inventory pass leaves that stale
# literal in the public StepResult buffers even though publication was masked.
target='                | Some policy -> policy.MaskAlreadyRedacted value'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: buffered-race mutation target is not unique' >&2; exit 1; }
sed -i 's/^                | Some policy -> policy\.MaskAlreadyRedacted value$/                | Some _ -> value/' "$executor"
kill_mutant buffered-race 'the final inventory masks the returned buffer'
cp "$scratch/executor.clean" "$executor"

# The same lock must cover inventory sampling, separator-aware matching,
# framing, and callback admission. Replacing it with a private policy lock
# reopens the snapshot-to-queue registration window.
target='                | Some bindings -> Some(Secrets.outputRedactionLive bindings request.MaskingSecretsLock)'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: synchronization mutation target is not unique' >&2; exit 1; }
sed -i 's/^                | Some bindings -> Some(Secrets\.outputRedactionLive bindings request\.MaskingSecretsLock)$/                | Some bindings -> Some(Secrets.outputRedactionLive bindings None)/' "$executor"
kill_mutant synchronization 'separator-aware sampling shares the registration linearization lock'
cp "$scratch/executor.clean" "$executor"

# A startup snapshot cannot see a credential registered later by a parallel
# sibling, and a step-local inventory also forgets post-scope credentials.
target='                          MaskingSecrets = Some runCtx.BoundSecrets'
[[ $(rg -F -c "$target" "$walker_step") == 1 ]] \
  || { echo 'FG-236 proof: live-policy mutation target is not unique' >&2; exit 1; }
sed -i 's/^                          MaskingSecrets = Some runCtx\.BoundSecrets$/                          MaskingSecrets = None/' "$walker_step"
kill_differential_mutant live-policy 'the late-registered split form is redacted'

# A sibling may bind after the raw matcher snapshots its inventory but before
# the queued callback publishes. Dropping the locked publication recheck makes
# that registered-before-publication credential visible.
cp "$scratch/walker-ctx.clean" "$walker_ctx"
target='                    if List.isEmpty secrets then line else Secrets.maskAlreadyRedacted secrets line'
[[ $(rg -F -c "$target" "$walker_ctx") == 2 ]] \
  || { echo 'FG-236 proof: publication-race mutation targets drifted' >&2; exit 1; }
sed -i '/let emitRedacted/,/let admit line/ s/^                    if List\.isEmpty secrets then line else Secrets\.maskAlreadyRedacted secrets line$/                    line/' "$walker_ctx"
kill_differential_mutant publication-race 'the terminal trace masks the late-bound credential'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# A slow earlier callback leaves later provenance-bearing lines pending. A
# newly bound credential must recheck that queue as one separator-aware stream,
# not merely recheck each already-framed fragment in isolation.
target='                    if not (List.isEmpty bindings) then'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: pending-publication mutation target is not unique' >&2; exit 1; }
sed -i 's/^                    if not (List\.isEmpty bindings) then$/                    if false then/' "$walker_ctx"
kill_differential_mutant pending-publication 'pending external lines are rechecked together before actual publication'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Reassembly is scoped to one Executor invocation. Treating every pending raw
# line as one stream lets parallel shells compose unrelated fragments and
# falsely redact ordinary output.
target='                                | Some candidate -> obj.ReferenceEquals(stream, candidate)'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: stream-identity mutation target is not unique' >&2; exit 1; }
sed -i 's/^                                | Some candidate -> obj\.ReferenceEquals(stream, candidate)$/                                | Some _ -> true/' "$walker_ctx"
kill_differential_mutant stream-identity 'pending lines from separate shell streams never compose one credential'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Literal stars are not evidence of prior masking. Reintroducing textual token
# inference lets raw `a****b` evade a credential registered before publication.
cp "$scratch/redaction.clean" "$redaction"
target='        let protectedAt index = value.TokenCharacters[index]'
[[ $(rg -F -c "$target" "$redaction") == 1 ]] \
  || { echo 'FG-236 proof: raw-token-inference mutation target is not unique' >&2; exit 1; }
sed -i 's/^        let protectedAt index = value\.TokenCharacters\[index\]$/        let protectedAt index = value.TokenCharacters[index] || value.Text[index] = '\''*'\''/' "$redaction"
kill_differential_mutant raw-token-inference 'literal four-star runs are not mistaken for matcher-produced tokens'
cp "$scratch/redaction.clean" "$redaction"

# Conversely, discarding the matcher's explicit token provenance lets a later
# credential consume text across a genuine canonical-token boundary.
target='        let protectedAt index = value.TokenCharacters[index]'
[[ $(rg -F -c "$target" "$redaction") == 1 ]] \
  || { echo 'FG-236 proof: token-provenance-loss mutation target is not unique' >&2; exit 1; }
sed -i 's/^        let protectedAt index = value\.TokenCharacters\[index\]$/        let protectedAt _ = false/' "$redaction"
kill_differential_mutant token-provenance-loss 'two adjacent raw-matcher tokens retain their separate cardinality'
cp "$scratch/redaction.clean" "$redaction"

# Existing raw-matcher tokens are already safe. Collapsing a whole star run to
# one token loses the cardinality of adjacent matches (eight stars are two
# tokens), even though remasking must remain idempotent.
cp "$scratch/redaction.clean" "$redaction"
target='                output.AppendProtected(value.Text.Substring(start, index - start))'
[[ $(rg -F -c "$target" "$redaction") == 1 ]] \
  || { echo 'FG-236 proof: adjacent-token mutation target is not unique' >&2; exit 1; }
sed -i 's/^                output\.AppendProtected(value\.Text\.Substring(start, index - start))$/                output.AppendProtected "****"/' "$redaction"
kill_differential_mutant adjacent-token 'two adjacent raw-matcher tokens retain their separate cardinality'
cp "$scratch/redaction.clean" "$redaction"

# Executor has already canonicalized a registered raw form. Sending that line
# through the raw masker again lets a literal one-character credential expand
# the canonical token and destroys the stable publication contract.
cp "$scratch/walker-step.clean" "$walker_step"
decoded_target='                          OnRedactedLine = None'
admission_target='                          OnRedactedAdmission = Some redactedAdmission'
[[ $(rg -F -c "$decoded_target" "$walker_step") == 1 ]] \
  || { echo 'FG-236 proof: idempotence decoded target is not unique' >&2; exit 1; }
[[ $(rg -F -c "$admission_target" "$walker_step") == 1 ]] \
  || { echo 'FG-236 proof: idempotence admission target is not unique' >&2; exit 1; }
sed -i 's/^                          OnRedactedLine = None$/                          OnRedactedLine = Some runCtx.Emit/' "$walker_step"
sed -i 's/^                          OnRedactedAdmission = Some redactedAdmission$/                          OnRedactedAdmission = None/' "$walker_step"
kill_differential_mutant idempotence 'canonical-token pipeline refused outside execution'

echo 'FG-236 PROOF PASS: baseline passed; EOF-suffix, grammar, progressive, wiring, control-frame, reader-enforcement, capture-cutoff, generated-warning, missing-warning-sink, generated-termination, direct-generated-callback, buffered-race, synchronization, live-policy, publication-race, pending-publication, stream-identity, raw-token-inference, token-provenance-loss, adjacent-token, and idempotence mutants compiled and were killed'
