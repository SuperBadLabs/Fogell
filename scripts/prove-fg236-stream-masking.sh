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
fogell="$scratch/src/Fogell.Differential/Fogell.fs"
filter='FG-236'

bash -ic "dotnet restore '$project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet restore '$differential_project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet build '$project' -c Release --no-restore -m:1"
bash -ic "dotnet build '$differential_project' -c Release --no-restore -m:1"
dotnet run --project "$project" -c Release --no-build -- \
  --filter-test-case "$filter" --sequenced
dotnet run --project "$differential_project" -c Release --no-build -- \
  --filter-test-case "$filter" --sequenced

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
cp "$fogell" "$scratch/fogell.clean"

# An incomplete whole-buffer prefix can still end in another complete form.
# Publishing pending characters wholesale at EOF recreates that credential leak.
target='            finalizeAll output'
[[ $(rg -F -c "$target" "$redaction") == 2 ]] \
  || { echo 'FG-236 proof: EOF-suffix mutation targets drifted' >&2; exit 1; }
sed -i '/member _.CompleteRedacted()/,/output.ToRedactedText()/ s/^            finalizeAll output$/            while pending.Count > 0 do let item = pending.Dequeue() in output.AppendRaw item.Character; output.AppendRaw(item.TrailingSeparator.ToRedactedText().Text)/' "$redaction"
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
target='                        true)'
[[ $(rg -F -c "$target" "$process_group") == 1 ]] \
  || { echo 'FG-236 proof: control-frame mutation target is not unique' >&2; exit 1; }
sed -i 's/^                        true)$/                        false)/' "$process_group"
kill_mutant control-frame 'parses the private process-group frame before overlapping credential redaction'
cp "$scratch/process-group.clean" "$process_group"

# A provenance-bearing process callback is still a public output callback. If
# its reader does not reach EOF inside the bounded drain window, returning
# success would bless a truncated stream merely because it bypassed OnLine.
target='                        || Option.isSome request.OnRedactedAdmission'
[[ $(rg -F -c "$target" "$process_group") == 1 ]] \
  || { echo 'FG-236 proof: reader-enforcement mutation target is not unique' >&2; exit 1; }
sed -i 's/^                        || Option\.isSome request\.OnRedactedAdmission$/                        || false/' "$process_group"
kill_mutant reader-enforcement 'a provenance callback cannot turn bounded reader truncation into success'
cp "$scratch/process-group.clean" "$process_group"

# A provenance callback is inactive without a raw redaction policy. Treating
# its mere presence as a transport contract falsely turns a bounded raw pipe
# snapshot into an EOF timeout even though no provenance callback can run.
target='                || (Option.isSome request.OutputRedaction'
[[ $(rg -F -c "$target" "$process_group") == 1 ]] \
  || { echo 'FG-236 proof: inactive-callback mutation target is not unique' >&2; exit 1; }
sed -i 's/^                || (Option\.isSome request\.OutputRedaction$/                || (true/' "$process_group"
kill_mutant inactive-callback 'an inactive provenance callback does not impose a false EOF contract'
cp "$scratch/process-group.clean" "$process_group"

# Executor receives protected matcher tokens, so both its progressive warning
# scan and final-buffer warning scan must retain that provenance. Flattening
# either path manufactures a transformed-form warning for a `****` credential.
cp "$scratch/executor.clean" "$executor"
target='                        for leak in Secrets.detectUnregisteredLeaksRedacted (secretsForOutput ()) line do'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: executor-stream-provenance mutation target is not unique' >&2; exit 1; }
sed -i 's/^                        for leak in Secrets\.detectUnregisteredLeaksRedacted (secretsForOutput ()) line do$/                        for leak in Secrets.detectUnregisteredLeaks (secretsForOutput ()) masked do/' "$executor"
kill_mutant executor-stream-provenance 'streamed leak detection preserves raw-matcher token provenance'

cp "$scratch/executor.clean" "$executor"
target='                |> List.collect (Secrets.detectUnregisteredLeaksRedacted bufferedSecrets)'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: executor-buffer-provenance mutation target is not unique' >&2; exit 1; }
sed -i 's/^                |> List\.collect (Secrets\.detectUnregisteredLeaksRedacted bufferedSecrets)$/                |> List.collect (fun value -> Secrets.detectUnregisteredLeaks bufferedSecrets value.Text)/' "$executor"
kill_mutant executor-buffer-provenance 'buffered leak detection preserves raw-matcher token provenance'

# A bounded capture snapshot is not EOF. Completing its matcher publishes an
# attacker-selected secret prefix that the escaped pipe holder may later finish.
cp "$scratch/process-group.clean" "$process_group"
cp "$scratch/executor.clean" "$executor"
target='                    policy.MaskAvailablePrefixRedacted runResult.Stdout'
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
kill_mutant missing-warning-sink 'the buffered warning crosses ordinary masking'

# Buffered synthesized warnings are public StepResult output too. Appending the
# original variable-bearing text can disclose a second credential whose value
# equals that variable name even though callback publication was masked.
cp "$scratch/executor.clean" "$executor"
target='                |> List.map (Secrets.mask bufferedSecrets)'
[[ $(rg -F -c "$target" "$executor") == 1 ]] \
  || { echo 'FG-236 proof: buffered-warning mutation target is not unique' >&2; exit 1; }
sed -i 's/^                |> List\.map (Secrets\.mask bufferedSecrets)$/                |> List.map id/' "$executor"
kill_mutant buffered-warning 'the buffered warning crosses ordinary masking'

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

# Timestamp text is synthesized after raw matching. Losing its ordinary
# literal scan can publish an active credential such as the current year even
# though the shell portion of the line retains exact redaction provenance.
target='                            && List.isEmpty (Secrets.detectLeaks secrets prefix)'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: timestamp-prefix mutation target is not unique' >&2; exit 1; }
sed -i 's/^                            && List\.isEmpty (Secrets\.detectLeaks secrets prefix)$/                            \&\& true/' "$walker_ctx"
kill_differential_mutant timestamp-prefix 'a credential-shaped timestamp cannot cross progressive publication'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Prefix and value are individually safe but their composition can complete a
# credential. This check is separate from the prefix-only scan so canonical
# redaction-token provenance remains opaque.
target='                            && List.isEmpty (Secrets.detectBoundaryLeaks secrets prefix safeValue))'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: timestamp-boundary mutation target is not unique' >&2; exit 1; }
sed -i 's/^                            && List\.isEmpty (Secrets\.detectBoundaryLeaks secrets prefix safeValue))$/                            \&\& true)/' "$walker_ctx"
kill_differential_mutant timestamp-boundary 'a credential composed only across the timestamp/output boundary cannot publish'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# A rendered canonical token is not raw credential text. Flattening its
# provenance lets a second credential consume the stars across the prefix join.
target='                            raw <- not value.TokenCharacters[index - boundary]'
[[ $(rg -F -c "$target" "$secrets") == 1 ]] \
  || { echo 'FG-236 proof: timestamp-boundary-provenance mutation target is not unique' >&2; exit 1; }
sed -i 's/^                            raw <- not value\.TokenCharacters\[index - boundary\]$/                            raw <- true/' "$secrets"
kill_differential_mutant timestamp-boundary-provenance 'a boundary match cannot consume characters from a protected token'
cp "$scratch/secrets.clean" "$secrets"

# The terminal trace uses the same composed-boundary rule. Publication refusal
# alone is insufficient because callback-free runs must also fail closed.
target='                            @ Secrets.detectBoundaryLeaks active prefix value'
[[ $(rg -F -c "$target" "$fogell") == 1 ]] \
  || { echo 'FG-236 proof: terminal-timestamp-boundary mutation target is not unique' >&2; exit 1; }
sed -i 's/^                            @ Secrets\.detectBoundaryLeaks active prefix value$//' "$fogell"
kill_differential_mutant terminal-timestamp-boundary 'the composed timestamp/output credential escaped terminal refusal'
cp "$scratch/fogell.clean" "$fogell"

# A future sibling binding can complete a credential begun in an already
# committed fragment. The binding must atomically bar every open stream so the
# completing suffix cannot publish before history-aware EOF remasking.
target='                for stream in openStreams do'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: open-stream-barrier mutation target is not unique' >&2; exit 1; }
sed -i 's/^                for stream in openStreams do$/                for stream in Seq.empty<PublicationStream> do/' "$walker_ctx"
kill_differential_mutant open-stream-barrier 'a binding bars every open stream before a completing fragment can publish'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# A slow earlier callback leaves later provenance-bearing lines pending. A
# newly bound credential must recheck that queue as one separator-aware stream,
# not merely recheck each already-framed fragment in isolation.
target='                    if Secrets.maskingForms active <> previousMaskingForms then'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: pending-publication mutation target is not unique' >&2; exit 1; }
sed -i 's/^                    if Secrets\.maskingForms active <> previousMaskingForms then$/                    if false then/' "$walker_ctx"
kill_differential_mutant pending-publication 'a stalled non-stream line is rechecked before external publication'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Literal remasking alone is insufficient when a later binding teaches the
# detector a transformed form. Recompute eligibility before requeueing it.
target='                            Publishable = item.Publishable && List.isEmpty leaks }'
[[ $(rg -F -x -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: pending-leak-screen mutation target is not unique' >&2; exit 1; }
sed -i 's/^                            Publishable = item\.Publishable && List\.isEmpty leaks }$/                            Publishable = item.Publishable }/' "$walker_ctx"
kill_differential_mutant pending-leak-screen 'a newly recognized transformed form never leaves the queued boundary'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Transformed-form screening must likewise treat matcher tokens as opaque.
# String-only detection would classify a safely protected `****` credential as
# its own reversed leak.
target='                    raw <- not value.TokenCharacters[index]'
[[ $(rg -F -c "$target" "$secrets") == 1 ]] \
  || { echo 'FG-236 proof: transformed-token-provenance mutation target is not unique' >&2; exit 1; }
sed -i 's/^                    raw <- not value\.TokenCharacters\[index\]$/                    raw <- true/' "$secrets"
kill_differential_mutant transformed-token-provenance 'a transformed form cannot be manufactured wholly from a protected token'
cp "$scratch/secrets.clean" "$secrets"

# A secret learned after external transport may use committed bytes as left
# context for a match which finishes later, but must not rewrite a complete
# audit line that was already irrevocably published.
target='                        not (committedPublicationOrders.Contains item.Order)'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: committed-history mutation target is not unique' >&2; exit 1; }
sed -i 's/^                        not (committedPublicationOrders\.Contains item\.Order)$/                        true/' "$walker_ctx"
kill_differential_mutant committed-history 'a future binding preserves committed publication and terminal audit history'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# EOF closes admission but not necessarily publication: a callback can still
# hold one committed line while a later line from the completed stream remains
# queued. Its history is required until that suffix commits or is remasked.
target='            if stream.Completed && not (streamHasPendingPublication stream) then'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: completed-history mutation target is not unique' >&2; exit 1; }
sed -i 's/^            if stream\.Completed && not (streamHasPendingPublication stream) then$/            if stream.Completed then/' "$walker_ctx"
kill_differential_mutant completed-history 'completed stream history survives until its queued suffix is remasked'

# Reframing needs committed bytes as match context, but returning the whole
# reframed line replays any ordinary prefix that transport already committed.
cp "$scratch/walker-ctx.clean" "$walker_ctx"
target='                        |> Secrets.maskAlreadyRedactedPendingLines secrets pendingSources'
test "$(rg -F -x -c "$target" "$walker_ctx")" -eq 1 \
  || { echo 'FG-236 proof: committed-prefix-projection mutation target is not unique' >&2; exit 1; }
sed -i 's/^                        |> Secrets\.maskAlreadyRedactedPendingLines secrets pendingSources$/                        |> Secrets.maskAlreadyRedactedLines secrets/' "$walker_ctx"
kill_differential_mutant committed-prefix-projection 'committed ordinary bytes remain matcher context but are never replayed'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Empty bindings and duplicate masking inventories add no new secret form. They
# must not hold an otherwise-progressive open stream until EOF.
target='                    if Secrets.maskingForms active <> previousMaskingForms then'
test "$(rg -F -x -c "$target" "$walker_ctx")" -eq 1 \
  || { echo 'FG-236 proof: non-expanding-binding mutation target is not unique' >&2; exit 1; }
sed -i 's/^                    if Secrets\.maskingForms active <> previousMaskingForms then$/                    if not (List.isEmpty bindings) then/' "$walker_ctx"
kill_differential_mutant non-expanding-binding 'a non-expanding binding does not create an EOF publication barrier'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Reassembly follows stream identity across globally interleaved queue entries.
# Dropping that identity reduces the late pass to isolated physical lines.
target='                                      RedactedStream = redactedStream }'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: stream-continuity mutation target is not unique' >&2; exit 1; }
sed -i 's/^                                      RedactedStream = redactedStream }$/                                      RedactedStream = None }/' "$walker_ctx"
kill_differential_mutant stream-continuity 'pending interleaved lines from one shell stream retain adjacency'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Conversely, collapsing every admission record onto one identity lets separate
# process streams compose an invented credential. Keep the real lifecycle
# objects distinct so the mutant changes only reassembly identity.
declaration='        let barrierStreams = System.Collections.Generic.HashSet<PublicationStream>(HashIdentity.Reference)'
[[ $(rg -F -c "$declaration" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: stream-identity declaration target is not unique' >&2; exit 1; }
sed -i "/^        let barrierStreams =/a\\        let collapsedPublicationStream = PublicationStream()" "$walker_ctx"
target='                                      RedactedStream = redactedStream }'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: stream-identity record target is not unique' >&2; exit 1; }
sed -i 's/^                                      RedactedStream = redactedStream }$/                                      RedactedStream = redactedStream |> Option.map (fun _ -> collapsedPublicationStream) }/' "$walker_ctx"
kill_differential_mutant stream-identity 'pending lines from separate shell streams never compose one credential'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# A registration-time snapshot is not EOF. Losing the lifecycle completion
# keeps the first physical fragment stranded instead of resolving it together
# with bytes admitted after the binding.
target='                  Complete = fun () -> completePublicationStream stream }'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: publication-EOF mutation target is not unique' >&2; exit 1; }
sed -i 's/^                  Complete = fun () -> completePublicationStream stream }$/                  Complete = ignore }/' "$walker_ctx"
kill_differential_mutant publication-eof 'progressive output stream did not reach EOF'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# A failed external transport is sticky, but synchronous raw admission must
# keep draining the child pipe until FlushOutput surfaces that failure.
target='                            | Some _ when deferExternalDrain -> false'
[[ $(rg -F -c "$target" "$walker_ctx") == 1 ]] \
  || { echo 'FG-236 proof: failed-reader-drain mutation target is not unique' >&2; exit 1; }
sed -i 's/^                            | Some _ when deferExternalDrain -> false$/                            | Some failure when deferExternalDrain -> raise failure/' "$walker_ctx"
kill_differential_mutant failed-reader-drain 'FG-236 publisher failure is typed and cannot stop synchronous reader admission'
cp "$scratch/walker-ctx.clean" "$walker_ctx"

# Walker must hand the lifecycle factory through Executor. Falling back to the
# historical decoded callback discards the pending stream state at the real
# production seam.
target='                          CreateRedactedAdmission = Some runCtx.CreateRedactedAdmission'
[[ $(rg -F -c "$target" "$walker_step") == 1 ]] \
  || { echo 'FG-236 proof: admission-factory mutation target is not unique' >&2; exit 1; }
sed -i 's/^                          CreateRedactedAdmission = Some runCtx\.CreateRedactedAdmission$/                          CreateRedactedAdmission = None/' "$walker_step"
kill_differential_mutant admission-factory 'the sibling registers the credential between the two physical fragments'
cp "$scratch/walker-step.clean" "$walker_step"

# ProcessGroup owns two raw matchers and therefore must mint two publication
# lifecycles. Reusing stderr's lifecycle for stdout recreates coarse provenance.
target='                let stdoutAdmission = request.CreateRedactedAdmission |> Option.map (fun create -> create ())'
[[ $(rg -F -c "$target" "$process_group") == 1 ]] \
  || { echo 'FG-236 proof: raw-pipe-identity mutation target is not unique' >&2; exit 1; }
sed -i 's/^                let stdoutAdmission = request\.CreateRedactedAdmission |> Option\.map (fun create -> create ())$/                let stdoutAdmission = stderrAdmission/' "$process_group"
kill_mutant raw-pipe-identity 'stdout and stderr each mint an independent admission lifecycle'
cp "$scratch/process-group.clean" "$process_group"
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
target='                for protectedIndex = start to index - 1 do'
[[ $(rg -F -c "$target" "$redaction") == 1 ]] \
  || { echo 'FG-236 proof: adjacent-token mutation target is not unique' >&2; exit 1; }
sed -i 's/^                for protectedIndex = start to index - 1 do$/                for protectedIndex = start to min start (index - 1) do/' "$redaction"
kill_differential_mutant adjacent-token 'two adjacent raw-matcher tokens retain their separate cardinality'
cp "$scratch/redaction.clean" "$redaction"

# Every collapsed match inherits the last physical line that contributed to
# that match, not the tail of the whole changed region. Flattening character
# sources shifts timestamps when two independent matches collapse at once.
target='                else this.SourceCharacters[sourceStart .. sourceFinish - 1] |> Array.max'
[[ $(rg -F -c "$target" "$redaction") == 1 ]] \
  || { echo 'FG-236 proof: token-source mutation target is not unique' >&2; exit 1; }
sed -i 's/^                else this\.SourceCharacters\[sourceStart \.\. sourceFinish - 1\] |> Array\.max$/                else this.SourceCharacters |> Array.max/' "$redaction"
kill_differential_mutant token-source 'independent collapsed matches retain their output cardinality and source slots'
cp "$scratch/redaction.clean" "$redaction"

# Executor has already canonicalized a registered raw form. Sending that line
# through the raw masker again lets a literal one-character credential expand
# the canonical token and destroys the stable publication contract.
cp "$scratch/walker-step.clean" "$walker_step"
decoded_target='                          OnRedactedLine = None'
factory_target='                          CreateRedactedAdmission = Some runCtx.CreateRedactedAdmission'
[[ $(rg -F -c "$decoded_target" "$walker_step") == 1 ]] \
  || { echo 'FG-236 proof: idempotence decoded target is not unique' >&2; exit 1; }
[[ $(rg -F -c "$factory_target" "$walker_step") == 1 ]] \
  || { echo 'FG-236 proof: idempotence factory target is not unique' >&2; exit 1; }
sed -i 's/^                          OnRedactedLine = None$/                          OnRedactedLine = Some runCtx.Emit/' "$walker_step"
sed -i 's/^                          CreateRedactedAdmission = Some runCtx\.CreateRedactedAdmission$/                          CreateRedactedAdmission = None/' "$walker_step"
kill_differential_mutant idempotence 'canonical-token pipeline refused outside execution'

echo 'FG-236 PROOF PASS: baseline passed; EOF-suffix, grammar, progressive, wiring, control-frame, reader-enforcement, inactive-callback, executor-stream-provenance, executor-buffer-provenance, capture-cutoff, generated-warning, missing-warning-sink, buffered-warning, generated-termination, direct-generated-callback, buffered-race, synchronization, live-policy, publication-race, timestamp-prefix, timestamp-boundary, timestamp-boundary-provenance, terminal-timestamp-boundary, open-stream-barrier, pending-publication, pending-leak-screen, transformed-token-provenance, committed-history, completed-history, committed-prefix-projection, non-expanding-binding, stream-continuity, stream-identity, publication-EOF, failed-reader-drain, admission-factory, raw-pipe-identity, raw-token-inference, token-provenance-loss, adjacent-token, token-source, and idempotence mutants compiled and were killed'
