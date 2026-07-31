namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir
open Fogell.Groovy.Interpreter

/// FG-036. What one parallel branch (or the single implicit branch of a
/// sequential pipeline) needs to know about itself.
/// FG-101. Why a step stopped. `Running` means it did not.
type Cancellation =
    | Running
    | DeadlineExpired
    | SiblingFailed

type BranchCtx =
    { /// Polled while a shell step runs; true means a failFast sibling failed.
      Interrupt: (unit -> bool) option
      /// Branch-local failure. Deliberately NOT the build status: a failed
      /// branch fails the build but does not halt its siblings.
      Failed: bool ref
      /// Where this branch's step statuses go. Normally the build status; inside
      /// a `retry` attempt, a throwaway sink — a failed attempt that is retried
      /// must not leave a permanent mark on the build, and the build status is a
      /// monotone worst-of that could never be walked back.
      Sink: BuildStatus -> unit
      /// FG-101. Elapsed-ms at which a failFast sibling signalled, if it has. The
      /// cancellation model claims the EARLIER event wins; without a timestamp that claim
      /// was unbackable and the code simply let expiry win every tie — a comment promising
      /// more than the code does, which is the FG-104 defect appearing inside FG-101.
      SiblingFailedAt: int64 ref
      /// FG-044. Credential bindings live for this scope, so output is masked.
      Secrets: SecretBinding list
      /// FG-041b. `withEnv([...]) { }` bindings, innermost last. Block-scoped:
      /// MEASURED on Jenkins, after the block an added variable is UNSET and a
      /// shadowed one reverts to its outer value.
      /// Receipt: `withenv-scoping`.
      EnvOverlay: (string * string) list }

/// The Fogell side. Parses the same Jenkinsfile, walks its stages, executes each
/// step, and reduces the run to a [Trace] in the same canonical form.
module FogellSide =

    /// Walk a parsed declarative pipeline. This is the minimum sequencer needed
    /// to make the differential meaningful; the durable scheduler (Wave 2) is a
    /// separate concern and is not on this path.
    /// FG-044. Credentials the harness has mirrored into the pinned Jenkins, so both
    /// engines bind the SAME values and a receipt means something. Supplied out of band
    /// (FOGELL_CREDENTIALS="id=value,id2=user:pass") rather than committed, so a real
    /// secret never enters the repository.
    let credentialStore () : Map<string, Credential> =
        // The value of a credential is ARBITRARY BYTES. Two rounds of review found a
        // delimiter bug here: the type was first inferred from the presence of a colon
        // (breaking any secret text holding a URL), and my fix then split entries on a
        // semicolon (breaking any value holding one). Choosing a third delimiter would
        // just move the bug, so the value is base64 and cannot contain a separator at
        // all. Fields are tab-separated, one credential per line:
        //
        //   <id>\t<text|userpass|file>\t<base64 value>
        //
        // `userpass` decodes to "user\npassword". Supplied via FOGELL_CREDENTIALS_FILE
        // so a real secret never appears in a process listing either.
        let spec =
            match Environment.GetEnvironmentVariable "FOGELL_CREDENTIALS_FILE" with
            | null
            | "" -> Environment.GetEnvironmentVariable "FOGELL_CREDENTIALS"
            | path -> if File.Exists path then File.ReadAllText path else null

        match spec with
        | null
        | "" -> Map.empty
        | text ->
            // REVIEW FIX (Codex, PR #15 round 4): this swallowed a decode failure and
            // returned "", so a typo'd credential line became an EMPTY secret — the exact
            // "build goes green while the deploy authenticates as nobody" outcome the
            // credential gate exists to prevent, committed by the decoder feeding it.
            // A malformed line is now dropped AND named, so the id is absent from the
            // store and the step fails closed on "credential id not found".
            let malformed = System.Collections.Generic.List<string>()

            let decodeBytes (id: string) (b64: string) =
                try
                    Some(Convert.FromBase64String(b64.Trim()))
                with _ ->
                    malformed.Add id
                    None

            text.Replace("\r\n", "\n").Split '\n'
            |> Array.toList
            |> List.choose (fun line ->
                if line.Trim() = "" || line.TrimStart().StartsWith "#" then
                    None
                else
                    match line.Split '\t' with
                    | [| id; kind; b64 |] ->
                        match decodeBytes (id.Trim()) b64 with
                        | None -> None
                        | Some bytes ->
                            let asText = Text.Encoding.UTF8.GetString bytes

                            match kind.Trim() with
                            | "text" -> Some(id.Trim(), SecretText asText)
                            // Bytes are preserved verbatim for a file credential.
                            | "file" -> Some(id.Trim(), SecretFile("secret.dat", bytes))
                            | "userpass" ->
                                match asText.Split '\n' with
                                | [| u; p |] -> Some(id.Trim(), UsernamePassword(u, p))
                                | _ -> None
                            | _ -> None
                    | _ -> None)
            |> fun entries ->
                for id in malformed do
                    eprintfn $"FOGELL_CREDENTIALS: '{id}' has malformed base64 and was DROPPED; the step will fail closed"

                Map.ofList entries

    let run (workspaceRoot: string) (jobName: string) (script: string) : Result<Trace, string> =
        match Fogell.Pipeline.Parser.Parser.parse script with
        | Result.Error e -> Result.Error $"{Fogell.Admission.ErrorCode.toWireString e.Code} at {e.Position}: {e.Message}"
        | Result.Ok pipeline ->
            let output = System.Collections.Generic.List<string>()
            // Parallel branches append from several threads at once.
            let outputLock = obj ()

            // FG-102: WHICH deadlines actually fired a cancellation, by absolute
            // value. The exceeded announcer for a scope speaks only when ITS OWN
            // declared bound is in this set — a shared boolean let branch B's
            // normal failure ride branch A's expiry, and clock arithmetic alone
            // announced timeouts that never caused anything. The firing deadline is
            // the EFFECTIVE one at the cancelled step, which by construction is the
            // owning scope's declared bound (the min of the chain is what fires).
            let firedDeadlines = System.Collections.Generic.HashSet<int64>()

            let recordFired (deadline: int64 option) =
                deadline |> Option.iter (fun d -> lock outputLock (fun () -> firedDeadlines.Add d |> ignore))

            let deadlineDidFire (declared: int64 option) =
                match declared with
                | Some d -> lock outputLock (fun () -> firedDeadlines.Contains d)
                | None -> false

            /// MEASURED (FG-036): declarative Jenkins emits parallel branch output
            /// with NO `[branchName]` prefix — that belongs to the scripted
            /// `parallel` map form. Fogell emitted one until the receipt said
            /// otherwise, which diverged every parallel case. Inventing attribution
            /// the reference engine does not provide is not a favour, it is a
            /// divergence.
            /// Receipt: `parallel-always-failfast`.
            /// Every secret bound anywhere in this run. Masking lives HERE, at the one
            /// place output is appended, rather than at each call site.
            ///
            /// The call-site version was reachable: once named arguments began rendering,
            /// `archiveArtifacts artifacts: "${TOKEN}/missing"` put the credential in the
            /// pattern, and `No artifacts found that match the file pattern "..."` came
            /// back as a Diagnostic that runStepInner emitted verbatim. A secret in the
            /// build log, produced by a step that never touches a shell — so masking the
            /// shell and echo paths could not have caught it.
            ///
            /// Masking is run-scoped, not block-scoped: a value is masked even after its
            /// `withCredentials` ends. That is deliberately broader than the binding, on
            /// the grounds that a leaked secret does not become safe when a block closes.
            ///
            /// Each binding carries the output index it was registered AT. The final leak
            /// check may only judge lines emitted from that point on: a build that
            /// happens to print the credential's value BEFORE the binding exists has not
            /// leaked anything — the text merely coincides — and flagging it would
            /// refuse a valid trace. Masking needs no such index because it naturally
            /// starts at registration; the retroactive scan is what had to be told.
            let boundSecrets = ResizeArray<SecretBinding * int>()

            // FG-103: engine-health notes for the RECEIPT — never build output.
            let engineNotes = ResizeArray<string>()

            // Groovy's script Binding for THIS build: a placeholder's assignment
            // outlives its step (receipt `gstring-binding-across-steps`). One per run
            // — a fresh build starts with a fresh Binding, exactly as Jenkins does.
            let scriptBinding = GString.ScriptBinding()


            /// MEASURED (FG-036): declarative Jenkins emits parallel branch output
            /// with NO `[branchName]` prefix — that belongs to the scripted
            /// `parallel` map form. Fogell emitted one until the receipt said
            /// otherwise, which diverged every parallel case. Inventing attribution
            /// the reference engine does not provide is not a favour, it is a
            /// divergence.
            /// Receipt: `parallel-always-failfast`.
            let emit (line: string) =
                lock outputLock (fun () ->
                    let safe =
                        if boundSecrets.Count = 0 then
                            line
                        else
                            Secrets.mask (boundSecrets |> Seq.map fst |> List.ofSeq) line

                    output.Add safe)

            // Jenkins' Binding-field advisory, emitted in ITS words so the two logs
            // COMPARE — a suppression keyed on this wording alone was the same
            // unconditional-shape-match defect as the retired secret-warning dialect:
            // a build printing the sentence was silently dropped from comparison.
            let adviseNewBinding (name: string, value: Value) =
                emit (
                    $"Did you forget the `def` keyword? WorkflowScript seems to be setting a field named {name} "
                    + $"(to a value of type {GString.javaTypeName value}) which could lead to memory leaks or other issues."
                )
            let workspace = Path.Combine(workspaceRoot, jobName)
            // artifacts live OUTSIDE the workspace so archiving cannot perturb
            // the workspace hash the differential compares
            let artifactRoot = Path.Combine(workspaceRoot, "_artifacts")
            if Directory.Exists workspace then Directory.Delete(workspace, true)
            Directory.CreateDirectory workspace |> ignore

            /// Environment visible to a step: pipeline scope, overridden by stage
            /// scope. Lexical, and stage wins — the semantics measured on Jenkins.
            /// Variables Jenkins provides to every build. REVIEW FIX (Codex, PR #13
            /// round 4): only pipeline- and stage-declared variables were visible, so
            /// `when { environment name: 'BUILD_NUMBER', value: '1' }` and
            /// `expression { env.BUILD_NUMBER == '1' }` saw them as ABSENT and skipped
            /// a stage Jenkins runs. Declarative overrides still win, as they do on
            /// Jenkins — hence these go first.
            let jenkinsProvided =
                [ "BUILD_NUMBER", "1"
                  "BUILD_ID", "1"
                  "BUILD_DISPLAY_NAME", "#1"
                  "JOB_NAME", jobName
                  "JOB_BASE_NAME", jobName
                  "WORKSPACE", Path.Combine(workspaceRoot, jobName)
                  "EXECUTOR_NUMBER", "0"
                  "NODE_NAME", "built-in" ]

            /// Values in an `environment { }` block interpolate `${NAME}` / `$NAME`
            /// against what is already visible, which is why `PATH = "/x:${PATH}"` is
            /// the idiom in 33 corpus files. Without expansion that assignment WIPES
            /// the inherited PATH — the failure that exposed this was a `tr: not
            /// found` in a differential case, i.e. a build broken by our own env
            /// handling.
            ///
            /// Resolution is a left-to-right fold, so a declaration sees the process
            /// environment plus every earlier declaration, and a later scope wins.
            /// An unknown name expands to empty, matching Groovy's null-to-string.
            // FG-100. The string model lives in `GString`, not here. It was a closure
            // inside `run`, which made it unreachable from tests and invited every consumer
            // to re-derive the rules — 52 findings' worth.
            let interpolate (known: Map<string, string>) (value: string) = GString.interpolate known value

            let envForWith (overlay: (string * string) list) (stage: Stage) =
                // REVIEW FIX (Codex, PR #14 round 5): the previous version UNIONED the
                // two scopes' literal-name sets, so a pipeline `VALUE = '$X'` followed
                // by a stage `VALUE = "$X"` left the stage's GString literal — the
                // name was still in the set. Codex had warned in round 4 to "carry
                // provenance with each binding or resolve each scope using its own
                // provenance", and I took the union shortcut anyway. Each scope is now
                // resolved against ITS OWN provenance.
                let withKind (names: Set<string>) (bindings: (string * string) list) =
                    bindings |> List.map (fun (k, v) -> k, v, not (Set.contains k names))

                // Jenkins-provided values are plain strings, never GStrings; a
                // withEnv overlay was already resolved where its quote form was known.
                let scoped =
                    withKind (jenkinsProvided |> List.map fst |> Set.ofList) jenkinsProvided
                    @ withKind pipeline.EnvironmentLiteralNames pipeline.Environment
                    @ withKind stage.EnvironmentLiteralNames stage.Environment
                    @ withKind (overlay |> List.map fst |> Set.ofList) overlay

                scoped
                |> List.fold
                    (fun acc (k, v, interpolates) ->
                        Map.add k (if interpolates then interpolate acc v else v) acc)
                    Map.empty
                |> Map.toList

            let mutable status = BuildStatus.Success
            let statusLock = obj ()
            let bump (s: BuildStatus) = lock statusLock (fun () -> status <- BuildStatus.worstOf status s)

            /// One clock for the whole build, so a `timeout` deadline is an
            /// absolute point in time rather than a per-step budget.
            let runClock = Diagnostics.Stopwatch.StartNew()

            /// FG-101. THE cancellation model: one predicate, one classification, one place
            /// that decides `aborted` versus `failure`.
            ///
            /// The same logic was previously written six times — `stop()`, `abort()`, a
            /// bare `expired`, `interruptedBySibling`, and two inline checks — and the CAUSE
            /// was misclassified six times: shell steps, stash, unstash, deleteDir, input,
            /// then input again as an ordering race. Twice I swept the class and the sweep
            /// was itself incomplete, because it asked whether every site CHECKED rather
            /// than whether every site checked in the right ORDER.
            ///
            /// Each rule below is established by a receipt, not by reasoning:
            ///   deadline expired -> ABORTED, and the step must say so (JB-DUR-005)
            ///   failFast sibling -> the build is a FAILURE; the sibling's failure is the
            ///                       cause and this interruption is collateral, so the step
            ///                       must NOT sink Aborted (parallel-failfast,
            ///                       input-failfast-is-failure)
            /// When both hold, the earlier event wins: a deadline already past preceded a
            /// sibling seen on this poll.
            let cancellationOf (ctx: BranchCtx) (deadline: int64 option) : Cancellation =
                let expiredNow =
                    match deadline with
                    | Some d -> runClock.ElapsedMilliseconds >= d
                    | None -> false

                let siblingNow =
                    match ctx.Interrupt with
                    | Some p -> (try p () with _ -> false)
                    | None -> false

                // The two predicates are sampled SEPARATELY, so the boolean pair cannot be
                // trusted to describe ordering: the deadline can pass between reading
                // `expiredNow` and reading `siblingNow`, and the sibling stamps its time
                // BEFORE calling Cancel(), so a stamp can exist while the token still reads
                // clear. Both windows produce a tuple that says one thing and a clock that
                // says another.
                //
                // So once EITHER event is observed, classify by the recorded TIMES.
                let siblingAt = ctx.SiblingFailedAt.Value
                let deadlineAt = defaultArg deadline Int64.MaxValue
                let siblingObserved = siblingNow || siblingAt >= 0L

                if not (expiredNow || siblingObserved) then Running
                elif siblingObserved && siblingAt >= 0L && siblingAt < deadlineAt then SiblingFailed
                elif expiredNow then DeadlineExpired
                else SiblingFailed

            /// Emit the reason, mark the branch failed, and sink the status the CAUSE
            /// dictates. Every cancellable step routes through this so the classification
            /// cannot drift per-step again.
            let applyCancellation (ctx: BranchCtx) (what: string) (deadline: int64 option) (c: Cancellation) =
                match c with
                | Running -> ()
                | SiblingFailed ->
                    emit $"ERROR: {what} interrupted: a failFast sibling failed"
                    ctx.Failed.Value <- true
                | DeadlineExpired ->
                    recordFired deadline
                    // Jenkins narrates the cancellation BEFORE the step's own abort
                    // line. Shell steps get this line from ProcessGroup at SIGTERM
                    // time; this is the path for steps with no process — input,
                    // stash, deleteDir waits (FG-102, measured wording).
                    emit "Cancelling nested steps due to timeout"
                    emit $"ERROR: {what} aborted: the step's deadline expired"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Aborted


            /// Remaining milliseconds before an absolute deadline, floored at 1.
            /// REVIEW FIX (Codex P1, PR #12): the first version handed the FULL
            /// timeout budget to every step inside the block, so two 2 s steps
            /// inside `timeout(3, SECONDS)` both succeeded and the block ran ~4 s.
            /// Jenkins bounds the BLOCK, not each step.
            /// REVIEW FIX (Codex, PR #13): this narrowed an int64 to int, so
            /// `timeout(time: 30, unit: 'DAYS')` — 2,592,000,000 ms, past
            /// Int32.MaxValue — wrapped negative and was floored to 1 ms, aborting
            /// instantly. Fixing "DAYS silently means minutes" had introduced
            /// "DAYS means one millisecond". Clamped at the executor's ceiling.
            /// REVIEW FIX (Codex, PR #13 round 2): clamping at Int32.MaxValue avoided
            /// the earlier integer wrap but still SHORTENED the requested deadline —
            /// a 30-day budget became 24.8 days, aborting work Jenkins still allows.
            /// The executor's budget is int64 now, so the deadline is represented
            /// exactly and nothing is silently rewritten.
            let remainingMs (deadline: int64 option) =
                deadline |> Option.map (fun d -> max 1L (d - runClock.ElapsedMilliseconds))

            /// ONE render pass for a step's arguments — source order, side effects
            /// once — plus the insecure-interpolation warning, computed HERE so every
            /// consumer gets it: ordinary steps, wrapper branches, `dir`, `input`.
            /// Rendering only in runStepInner left wrappers expanding secrets with no
            /// warning where Jenkins warns on the step invocation.
            ///
            /// The warning wants a REAL GString: Interpolating kind AND a live `$` in
            /// the source. Every double-quoted argument is Interpolating by kind, but
            /// `echo "abc"` with no placeholder is an ordinary constant — warning on
            /// a coincidental secret value there flags interpolation that never
            /// happened. (An escaped dollar is a sentinel at this point, so any `$`
            /// in the source is live.)
            /// The insecure-interpolation warning for a set of GString-rendered texts,
            /// factored out so BOTH render paths — step arguments and withEnv's
            /// `NAME=value` entries — say what Jenkins says.
            let warnSecretInterpolation (ctx: BranchCtx) (stepName: string) (texts: string list) =
                let leaked =
                    ctx.Secrets
                    |> List.filter (fun b ->
                        // a file() credential exports the PATH, not the content
                        let exported = if b.ValueVariableCarriesPath then b.FilePath else b.Value
                        exported <> "" && texts |> List.exists (fun t -> t.Contains exported))
                    |> List.map (fun b -> b.ValueVariable)
                    |> List.distinct

                if not (List.isEmpty leaked) then
                    emit $"Warning: A secret was passed to \"{stepName}\" using Groovy String interpolation, which is insecure."
                    emit $"""Affected argument(s) used the following variable(s): [{String.concat ", " leaked}]"""

            let renderStepArgs (ctx: BranchCtx) (stage: Stage) (step: Step) : Step =
                let env = envForWith ctx.EnvOverlay stage |> Map.ofList

                // SOURCE order, exactly as recorded: `step label: "...", "..."` must
                // evaluate label first because Groovy does, and evaluation mutates
                // the shared Binding. The partitioned lists cannot say who came
                // first; ArgumentOrder can.
                let order =
                    if List.isEmpty step.ArgumentOrder then
                        (step.Positional |> List.mapi (fun i _ -> $"#{i}")) @ (step.Named |> List.map fst)
                    else
                        step.ArgumentOrder

                let renderedByKey =
                    order
                    |> List.map (fun key ->
                        let raw =
                            if key.StartsWith "#" then
                                step.Positional |> List.tryItem (int (key.Substring 1)) |> Option.defaultValue ""
                            else
                                step.Named
                                |> List.tryPick (fun (k, v) -> if k = key then Some v else None)
                                |> Option.defaultValue ""

                        key, raw, GString.renderInto scriptBinding adviseNewBinding env step key raw)

                let renderedPositional =
                    renderedByKey |> List.filter (fun (k, _, _) -> k.StartsWith "#")

                let renderedNamed =
                    renderedByKey |> List.filter (fun (k, _, _) -> not (k.StartsWith "#"))

                let interpolatedTexts =
                    renderedPositional @ renderedNamed
                    |> List.filter (fun (k, raw, _) ->
                        GString.kindOf step k = Interpolating && (GString.sourceOf step k raw).Contains "$")
                    |> List.map (fun (_, _, r) -> r)

                warnSecretInterpolation ctx step.Name interpolatedTexts

                { step with
                    Positional = renderedPositional |> List.map (fun (_, _, r) -> r)
                    Named = renderedNamed |> List.map (fun (k, _, r) -> k, r) }

            /// Jenkins' duration wording, measured on 2.568.1 (Util.getTimeSpanString):
            /// the top unit and its immediate neighbour — `3 sec`, `2 min 0 sec`,
            /// `5 min 0 sec`, and `1 mo 0 days` for thirty DAYS (months are 30 days).
            /// `sec` alone when seconds are the top unit; `day`/`days` pluralise.
            let humanizeSpan (ms: int64) : string =
                let sec = ms / 1000L
                let minutes = sec / 60L
                let hours = minutes / 60L
                let days = hours / 24L
                let months = days / 30L
                // Jenkins' year is 365 DAYS, then 30-day months — 360 days is
                // "12 mo 0 days", not "1 yr 0 mo"
                let years = days / 365L
                let dayWord (d: int64) = if d = 1L then "day" else "days"

                if years > 0L then $"{years} yr {(days % 365L) / 30L} mo"
                elif months > 0L then $"{months} mo {days % 30L} {dayWord (days % 30L)}"
                elif days > 0L then $"{days} {dayWord days} {hours % 24L} hr"
                elif hours > 0L then $"{hours} hr {minutes % 60L} min"
                elif minutes > 0L then $"{minutes} min {sec % 60L} sec"
                elif ms < 10_000L && ms % 1000L <> 0L then
                    // measured: below ten seconds Jenkins shows one decimal —
                    // "0.5 sec", "1.5 sec" — while exact seconds print plain ("3 sec")
                    let s1 = float ms / 1000.0
                    $"%.1f{s1} sec"
                else $"{sec} sec"

            let runStepInner (ctx: BranchCtx) (stage: Stage) (cwd: string) (step: Step) (deadline: int64 option) =
                // The argument and the KEY it arrived under travel together. `render`
                // needs the key to ask what quoting the source used, and deriving the
                // two separately is what let `sh` drift onto the wrong rule below.
                let scriptKey =
                    match step.Positional with
                    | _ :: _ -> "#0"
                    | [] ->
                        step.Named
                        |> List.tryPick (fun (k, _) -> if k = "script" || k = "message" then Some k else None)
                        |> Option.defaultValue "#0"

                // FG-100. Groovy expands a GString BEFORE the step is invoked — `sh`
                // included. This code previously exempted shell steps, reasoning that
                // the shell expands its own arguments and doing it twice would undo
                // what an author escaped. That is wrong about WHO expands WHAT, and it
                // was never measured.
                //
                // MEASURED (receipt `sh-gstring-interpolation`, Jenkins 2.568.1), with
                // `TARGET = 'prod'`. Every row is reachable by only one model:
                //   sh "echo double:${env.TARGET}"              -> double:prod
                //   sh "echo upper:${env.TARGET.toUpperCase()}" -> upper:PROD
                //   sh 'echo literal:${NOT_IN_ENV}.'            -> literal:.
                //   sh "echo escaped:\${NOT_IN_ENV}."           -> escaped:.
                // Groovy expands rows 1-2 — the shell can neither name `env.TARGET` nor
                // call a method — and the shell expands rows 3-4 to empty. Under the
                // old exemption row 1 reached /bin/sh raw and the build FAILED with
                // "Bad substitution" where Jenkins printed `double:prod`.
                //
                // Escaping is not lost: a Literal argument renders to itself, and an
                // escaped dollar emits a bare `$` for the shell — rows 3 and 4.
                //
                // Arguments render in SOURCE order — positional first, then the named
                // list as written — because rendering is now EVALUATION and Groovy
                // evaluates call arguments left to right. Rendering the script first
                // regardless of position broke
                // `sh label: "${x = 'ok'; x}", script: "echo $x"`: Jenkins binds x
                // from `label` before `script` reads it; Fogell raised
                // MissingProperty. The script value derives from the one rendering
                // pass rather than a second call — a placeholder's side effects run
                // once (see the increment test).
                // ONE render pass — source order, side effects once, warning included —
                // shared with the wrapper branches through renderStepArgs.
                let rendered = renderStepArgs ctx stage step

                let script =
                    match rendered.Positional with
                    | r :: _ -> Some r
                    | [] -> rendered.Named |> List.tryPick (fun (k, v) -> if k = scriptKey then Some v else None)

                let renderedNamed = rendered.Named

                let result =
                    Executor.runStep
                        { Name = step.Name
                          Script = script
                          Workspace = cwd
                          Environment = envForWith ctx.EnvOverlay stage
                          TimeoutMs =
                            match remainingMs deadline with
                            | Some ms -> Some ms
                            | None -> Some 120_000L
                          OnLine = Some emit
                          // External cancellation only — a failFast sibling. The
                          // deadline reaches the shell runner through TimeoutMs and
                          // self-working steps through DeadlineExpired, so an expired
                          // timeout is still reported as a timeout.
                          Interrupt = ctx.Interrupt
                          DeadlineExpired =
                            deadline |> Option.map (fun d -> fun () -> runClock.ElapsedMilliseconds >= d)
                          Secrets = ctx.Secrets
                          Named = renderedNamed
                          Artifacts = Some(ArtifactStore.under artifactRoot)
                          BuildKey = jobName }

                // Output arrives exactly once, via OnLine. An earlier version also
                // appended result.Stdout, so every shell line was emitted twice and
                // the differential reported a phantom divergence at line 1.
                //
                // stderr is not streamed by OnLine, so it is added here.
                for line in result.Stderr.Replace("\r\n", "\n").Split '\n' do
                    if line <> "" then emit line

                // A SUCCESSFUL step can still carry an engine-health finding — the
                // leak check reporting itself unavailable. FG-103: an unknown is said
                // somewhere; the receipt is where the engine talks about itself
                // without inventing build output Jenkins lacks. OUTSIDE the
                // non-success guard — nested there (round 2's placement), the Success
                // path it exists for could never reach it.
                if result.Status = BuildStatus.Success then
                    result.Diagnostic
                    |> Option.iter (fun d -> lock outputLock (fun () -> engineNotes.Add $"step '{step.Name}': {d}"))

                // Jenkins prints its failure reason INTO the build log. Parity
                // requires the same: a diagnostic the user cannot see is not a
                // diagnostic (JB-DUR-005 — Jenkins' own worst behaviour is an
                // opaque `exit code -1`, and we promised to be clearer, not quieter).
                if result.Status <> BuildStatus.Success then
                    // Was this step stopped because a failFast SIBLING failed?
                    // MEASURED (FG-036): Jenkins reports such a build as FAILURE,
                    // not ABORTED — the sibling's failure is the cause and the
                    // interruption is collateral. Letting the collateral abort
                    // dominate (worstOf puts Aborted above Failure) reported
                    // `aborted` where Jenkins reports `failure`.
                    // Routed through the ONE model so the shell path cannot drift from
                    // the wrapper steps again.
                    // Receipt: `parallel-failfast`.
                    let interruptedBySibling =
                        result.Status = BuildStatus.Aborted
                        && cancellationOf ctx deadline = SiblingFailed

                    if result.Status = BuildStatus.Aborted && not interruptedBySibling then
                        recordFired deadline

                    // Jenkins prints `ERROR: …` for a FAILED step. It does not for
                    // an unstable one — `junit` marks the build unstable without
                    // an ERROR line, so emitting one there is a false divergence.
                    // An ABORTED step is narrated too (Jenkins: "Sending interrupt
                    // signal to process / Terminated"), and silence on an abort is
                    // the JB-DUR-005 defect we promised to beat.
                    if result.Status = BuildStatus.Failure || result.Status = BuildStatus.Aborted then
                        result.Diagnostic |> Option.iter (fun d -> emit $"ERROR: {d}")

                    // Only a FAILED or ABORTED step halts the branch. An
                    // unstable one does not: `junit` marks the build unstable and
                    // returns normally, so Jenkins runs the following steps. It
                    // also means `retry` does not re-run an unstable body —
                    // retry catches exceptions, and unstable throws none.
                    if result.Status <> BuildStatus.Unstable then
                        ctx.Failed.Value <- true

                    if not interruptedBySibling then ctx.Sink result.Status

            /// Parse `timeout(time: 5, unit: 'SECONDS')` / `timeout(5)` into ms.
            /// Jenkins' default unit is MINUTES, which is a trap for anyone who
            /// assumes seconds; matching it matters more than being intuitive.
            let timeoutMs (step: Step) =
                let value =
                    step.Named
                    |> List.tryPick (fun (k, v) -> if k = "time" then Some v else None)
                    |> Option.orElse (List.tryHead step.Positional)
                    |> Option.bind (fun v -> match Int32.TryParse(v.Trim()) with
                                             | true, n -> Some n
                                             | _ -> None)

                let unit =
                    step.Named
                    |> List.tryPick (fun (k, v) -> if k = "unit" then Some(v.Trim().Trim('\'').ToUpperInvariant()) else None)
                    |> Option.defaultValue "MINUTES"

                // REVIEW FIX (Codex P1, PR #12): the wildcard silently mapped every
                // unrecognised unit to MINUTES, so `timeout(time: 1, unit: 'DAYS')`
                // aborted after one minute — killing a valid build 1,440x early.
                // Every java.util.concurrent.TimeUnit Jenkins accepts is mapped, and
                // an unknown unit returns None so the caller can fail closed rather
                // than invent a deadline.
                let scale =
                    match unit with
                    | "NANOSECONDS" -> Some 0.000001
                    | "MICROSECONDS" -> Some 0.001
                    | "MILLISECONDS" -> Some 1.0
                    | "SECONDS" -> Some 1_000.0
                    | "MINUTES" -> Some 60_000.0
                    | "HOURS" -> Some 3_600_000.0
                    | "DAYS" -> Some 86_400_000.0
                    | _ -> None

                match value, scale with
                | Some n, Some f -> Ok(int64 (float n * f))
                | Some _, None -> Error $"unknown timeout unit '{unit}'"
                | None, _ -> Error "timeout has no numeric time value"

            let retryCount (step: Step) =
                step.Named
                |> List.tryPick (fun (k, v) -> if k = "count" then Some v else None)
                |> Option.orElse (List.tryHead step.Positional)
                |> Option.bind (fun v -> match Int32.TryParse(v.Trim()) with
                                         | true, n -> Some n
                                         | _ -> None)
                |> Option.defaultValue 1

            /// `parallelsAlwaysFailFast()` in top-level options is equivalent to
            /// writing `failFast true` in every parallel block.
            let alwaysFailFast =
                pipeline.Options |> List.exists (fun o -> o.Name = "parallelsAlwaysFailFast")

            /// A branch stops early when it has failed, or when a failFast
            /// sibling asked it to stop. Kept separate from the global `status`:
            /// a failing branch marks the BUILD failed but must not stop its
            /// siblings (JB-FAIL-006).
            let halted (ctx: BranchCtx) =
                ctx.Failed.Value
                || (match ctx.Interrupt with
                    | Some p -> p ()
                    | None -> false)

            /// `*` wildcard matching for `when { branch }` / `when { tag }`.
            ///
            /// REVIEW FIX (Copilot, PR #13): this said "Ant-style glob", which it is
            /// not — only `*` is expanded, and Ant also has `?` and `**`. Claiming a
            /// pattern language we do not implement is the same over-claim the whole
            /// project exists to avoid. Corpus patterns are `main`, `v*`, `release/*`,
            /// which `*` covers; `?`/`**` remain unimplemented and unclaimed.
            ///
            /// An absent variable is never a match: MEASURED, Jenkins skips the stage.
            /// Receipt: `when-scm-and-equals`.
            let matchesGlob (pattern: string) (value: string option) =
                match value with
                | None -> false
                | Some v ->
                    let rx =
                        "^"
                        + (pattern.Split '*'
                           |> Array.map Text.RegularExpressions.Regex.Escape
                           |> String.concat ".*")
                        + "$"

                    Text.RegularExpressions.Regex.IsMatch(v, rx)

            /// FG-048. Evaluate a `when` condition. Returns None when the condition
            /// cannot be evaluated at all, which is NOT the same as false: running
            /// a stage Jenkins would skip and skipping one Jenkins would run are
            /// both divergences, so an unmodelled condition fails the build with a
            /// named reason instead of guessing a direction.
            let rec evalWhen (stage: Stage) (cond: WhenCondition) : bool option =
                let env = envForWith [] stage |> Map.ofList

                match cond with
                | WhenEnvironment(name, value) ->
                    Some(Map.tryFind name env = Some value)

                // MEASURED: on a plain (non-multibranch) pipeline job Jenkins
                // SKIPS a `branch` or `tag` stage, because BRANCH_NAME/TAG_NAME do
                // not exist. So absent-means-false is Jenkins' behaviour, not a
                // guess, and it is what lets these two conditions be modelled
                // instead of refused — `branch` and `tag` together account for
                // most `when` usage in the corpus.
                //
                // The glob path (variable PRESENT, pattern matched) is NOT
                // receipt-proven: this harness has no multibranch job to exercise
                // it. It is implemented from Jenkins' documented ant-style glob.
                // Receipt: `when-scm-and-equals`.
                | WhenBranch pattern -> Some(matchesGlob pattern (Map.tryFind "BRANCH_NAME" env))
                | WhenTag pattern -> Some(matchesGlob pattern (Map.tryFind "TAG_NAME" env))

                // REVIEW FIX (Codex, PR #13): both operands were stored as bare text,
                // so `equals expected: 2, actual: '2'` compared equal and ran a stage
                // Jenkins skips — Jenkins compares the underlying objects, and an
                // Integer is not a String. The parser now keeps each operand's SOURCE
                // form (quotes included), so a quoted and an unquoted 2 differ.
                // This is a source-level approximation of Jenkins' object comparison,
                // not the real thing: `equals expected: 2, actual: 2.0` would still
                // be called unequal. Stated rather than implied.
                | WhenEquals(expected, actual) ->
                    // Jenkins compares OBJECTS: Integer 2 is not String "2". An operand is
                    // a quoted literal (a String), a bare number (an Integer), or an
                    // expression like `env.X` — and an environment variable is a String.
                    //
                    // REGRESSION, caught by both reviewers: PR #13 round 5 established the
                    // type distinction by keeping each operand's source form, and my
                    // expression-resolution fix then stripped the quotes and made
                    // `equals expected: 2, actual: '2'` equal again. The fix has to
                    // preserve provenance, not just resolve.
                    let classify (raw: string) =
                        let t = raw.Trim()

                        if t.Length >= 2 && (t.StartsWith "'" || t.StartsWith "\"") && t.EndsWith(string t[0]) then
                            // A String literal. Remove ONLY the one reconstructed delimiter:
                            // `Trim('\'', '"')` stripped every quote at both ends, so
                            // `equals expected: '"foo', actual: 'foo'` compared "foo" with
                            // foo as equal and ran a stage Jenkins skips.
                            Choice1Of3(t.Substring(1, t.Length - 2))
                        elif t = "true" || t = "false" then
                            // a Boolean literal. REVIEW FIX (Codex, PR #16 round 4): bare
                            // `true` fell through to the expression branch and became a
                            // String, so `equals expected: true, actual: 'true'` compared
                            // equal and ran a stage Jenkins skips.
                            Choice3Of3 t
                        elif t.Length > 0 && (Char.IsDigit t[0] || (t[0] = '-' && t.Length > 1)) then
                            // a numeric literal — a different Groovy type entirely
                            Choice2Of3 t
                        else
                            // an expression; environment values are Strings
                            let bare = if t.StartsWith "env." then t.Substring 4 else t

                            match Map.tryFind bare env with
                            | Some v -> Choice1Of3 v
                            | None ->
                                // REVIEW FIX (Codex, PR #16 round 5): an unresolved
                                // expression became its own SOURCE TEXT, so
                                // `equals expected: 'env.MISSING', actual: env.MISSING`
                                // compared two identical strings and ran a stage Jenkins
                                // skips — Jenkins compares a String against null. Null is
                                // its own class: equal only to another null.
                                Choice2Of3 "\u0000null"

                    match classify expected, classify actual with
                    | Choice1Of3 a, Choice1Of3 b -> Some(a = b)
                    | Choice2Of3 a, Choice2Of3 b -> Some(a.Trim() = b.Trim())
                    | Choice3Of3 a, Choice3Of3 b -> Some(a = b)
                    // Different Groovy types are never equal, which is the whole point.
                    | _ -> Some false

                // Neutral: an evaluation-ORDER directive never decides whether a stage runs.
                | WhenEvaluationOption -> Some true

                // FG-048b. MEASURED on the pinned Jenkins: on a plain job — no SCM
                // changelog, no multibranch metadata, not a restart, triggered by a user —
                // every one of these is FALSE and its stage is skipped, with the build
                // succeeding. Before this they failed CLOSED, refusing up to 15 corpus
                // files outright, and a refusal is still a broken lift-and-shift.
                //
                // The BOUNDARY, stated rather than implied: only the context-absent case
                // is receipt-proven. A real multibranch build where CHANGE_ID exists, or a
                // build with an actual changelog, is NOT covered by that measurement —
                // those paths use the variable when present and are unproven until this
                // harness can produce such a build (FG-048c).
                // Receipt: `when-context-conditions`.
                | WhenBuildingTag -> Some(Map.containsKey "TAG_NAME" env)
                | WhenChangeRequest -> Some(Map.containsKey "CHANGE_ID" env)
                | WhenIsRestartedRun ->
                    // Nothing in this engine restarts a run yet, so this cannot be true.
                    Some false
                | WhenTriggeredBy _ ->
                    // MEASURED false on a user-started build, and it stays false until real
                    // cause metadata exists.
                    //
                    // REVIEW FIX (both reviewers, PR #16): reading `BUILD_CAUSE` out of the
                    // environment made the gate SPOOFABLE — a Jenkinsfile declaring
                    // `environment { BUILD_CAUSE = 'TimerTrigger' }` would open a stage
                    // Jenkins skips, and nothing else in the engine ever produces that
                    // variable. A gate whose input the gated party controls is not a gate.
                    // Receipt: `when-context-conditions`.
                    Some false

                | WhenChangeset _
                | WhenChangelog _ ->
                    // Both need an SCM changelog. There is none, and Jenkins itself warns
                    // "empty changelog, probably because this is the first build" and
                    // evaluates false.
                    Some false

                | WhenNot inner -> evalWhen stage inner |> Option.map not

                // REVIEW FIX (Codex, PR #13 round 2): an unevaluable operand used to
                // dominate, so `allOf { <false>, triggeredBy(...) }` failed the build
                // even though the false operand ALREADY decides that Jenkins skips.
                // Three-valued short-circuiting: a decisive known operand wins, and
                // only a genuinely undetermined result is reported unevaluable. This
                // shrinks the fail-closed surface without guessing anything.
                | WhenAllOf conds ->
                    let results = conds |> List.map (evalWhen stage)

                    if results |> List.contains (Some false) then Some false
                    elif results |> List.exists Option.isNone then None
                    else Some true

                | WhenAnyOf conds ->
                    let results = conds |> List.map (evalWhen stage)

                    if results |> List.contains (Some true) then Some true
                    elif results |> List.exists Option.isNone then None
                    else Some false

                | WhenExpression source ->
                    // ADR 0002: expressions stay as source text and the bounded
                    // interpreter decides what they mean. The sandbox's step
                    // vocabulary is EMPTY here — a `when` predicate has no
                    // business invoking build steps.
                    match Fogell.Groovy.Parser.Parser.parse source with
                    | Result.Error _ -> None
                    | Result.Ok script ->
                        // REVIEW FIX (Codex, PR #13): only bare names were bound, so
                        // the NORMAL Jenkins predicate `env.FOO == 'bar'` resolved
                        // `env` to null, compared null to a string, and SKIPPED a
                        // stage Jenkins runs. Both spellings are bound now.
                        let asValues = env |> Map.map (fun _ v -> VStr v)

                        let genv =
                            { Vars = asValues |> Map.add "env" (VMap asValues)
                              Funcs = Map.empty }

                        let outcome = Interpreter.run Budget.defaults Set.empty genv script

                        match outcome.Fault, outcome.Returned with
                        | Some _, _ -> None
                        | None, Some v -> Some(Value.isTruthy v)
                        // A predicate that produced no value cannot be read as
                        // false; that is the vacuous-pass shape.
                        | None, None -> None

                | WhenUnmodelled _ -> None

            /// FG-049. Does this post condition fire?
            ///
            /// MEASURED across four consecutive builds of one job, not read from
            /// documentation:
            ///   build 1 fails (no history) -> always, changed,          failure, cleanup
            ///   build 2 ok after FAILURE   -> always, changed, fixed,   success, cleanup
            ///   build 3 ok after SUCCESS   -> always,                   success, cleanup
            ///   build 4 fails after SUCCESS-> always, changed, regression, failure, cleanup
            ///
            /// The surprise is `changed` on build 1: with no previous result,
            /// Jenkins treats the result as changed.
            /// PARTIALLY UNPROVEN: only build #1 is receipt-backed (post-order-failure,
            /// post-order-success). `fixed`/`regression` need build HISTORY, which this harness
            /// cannot produce — it deletes the job around every run. Measured once by a manual
            /// four-build probe. FG-110 unblocks the receipt; FG-049b is the receipt.
            let postFires (cond: PostCondition) (result: BuildStatus) (previous: BuildStatus option) =
                match cond with
                | PostCondition.Always -> true
                | PostCondition.Cleanup -> true
                | PostCondition.Success -> result = BuildStatus.Success
                | PostCondition.Failure -> result = BuildStatus.Failure
                | PostCondition.Unstable -> result = BuildStatus.Unstable
                | PostCondition.Aborted -> result = BuildStatus.Aborted
                // We never produce NOT_BUILT, so claiming this fires would be
                // asserting behaviour we have not built.
                | PostCondition.NotBuilt -> false
                | PostCondition.Changed ->
                    match previous with
                    | None -> true
                    | Some p -> p <> result
                | PostCondition.Fixed ->
                    match previous with
                    | None -> false
                    | Some p -> (p = BuildStatus.Failure || p = BuildStatus.Unstable) && result = BuildStatus.Success
                | PostCondition.Regression ->
                    match previous with
                    | None -> false
                    | Some p -> p = BuildStatus.Success && result <> BuildStatus.Success

            /// Execution order, MEASURED: always -> changed -> fixed ->
            /// regression -> <result arm> -> cleanup. The result arms are mutually
            /// exclusive, so their order relative to each other is unobservable
            /// and is not claimed.
            /// Receipts: `post-order-failure`, `post-order-success` — which exercise the
            /// build-#1 arms only. The `fixed` and `regression` SLOTS in this ordering are
            /// NOT exercised by any receipt; they come from the four-build probe and stay
            /// UNPROVEN until FG-110 gives the harness build history.
            let postRank (cond: PostCondition) =
                match cond with
                | PostCondition.Always -> 0
                | PostCondition.Changed -> 1
                | PostCondition.Fixed -> 2
                | PostCondition.Regression -> 3
                | PostCondition.Aborted -> 4
                | PostCondition.Failure -> 5
                | PostCondition.Success -> 6
                | PostCondition.Unstable -> 7
                | PostCondition.NotBuilt -> 8
                | PostCondition.Cleanup -> 9

            /// FG-045. `options { timeout(...) }` at pipeline or stage level. MEASURED:
            /// Jenkins ABORTS the build when it expires — `finished.txt` and the following
            /// stage never appear. Fogell ignored options entirely, so such a pipeline ran
            /// UNBOUNDED and reported success; the 60-second sleep in the probe completed.
            ///
            /// A nested deadline can only tighten an inherited one, never extend it.
            /// Receipts: `options-timeout-pipeline` AND `options-timeout-stage` — the claim
            /// covers BOTH levels, so citing only the pipeline one left the stage half of
            /// the sentence unbacked.
            let deadlineFromOptions (options: Step list) (inherited: int64 option) =
                // REVIEW FIX (Codex, PR #16): an unparseable time or unsupported unit
                // turned into None here, so the declared SAFETY BOUND silently vanished and
                // the job ran unbounded — while the step form fails closed on the very same
                // error. Errors are surfaced so the caller can stop the build.
                let declared, optionError =
                    options
                    |> List.filter (fun o -> o.Name = "timeout")
                    |> List.fold
                        (fun (acc, err) o ->
                            match timeoutMs o with
                            | Ok ms -> (Some(runClock.ElapsedMilliseconds + ms, ms), err)
                            | Error e -> (acc, Some e))
                        (None, None)

                // FG-102, measured: an `options { timeout }` announces its budget in
                // the same words the step form uses, at the point it takes effect.
                // Formatted from the PARSED duration — reconstructing it from the
                // absolute deadline raced the stopwatch, and a 4-second timeout that
                // lost 1 ms in flight announced "3 sec": a load-dependent divergence
                // now that these sentences are compared.
                declared
                |> Option.iter (fun (_, ms) -> emit ("Timeout set to expire in " + humanizeSpan ms))

                let effective =
                    match declared, inherited with
                    | Some(d, _), Some i -> Some(min d i)
                    | Some(d, _), None -> Some d
                    | None, i -> i

                effective, (declared |> Option.map fst), optionError

            let rec runPostWithDeadline
                (ctx: BranchCtx)
                (cwd: string)
                (stage: Stage)
                (result: BuildStatus)
                (previous: BuildStatus option)
                (deadline: int64 option)
                =
                if not (List.isEmpty stage.Post) then
                    // REVIEW FIX (Codex, PR #13 round 3): arms were selected up front
                    // against the pre-post result, so on a SUCCESSFUL stage with
                    // `post { always { exit 1 } failure { … } success { … } }` the
                    // failing `always` left `failure` ineligible and `success` still
                    // eligible — success-only publication after a post failure, the
                    // same class of defect as the parallel-sink bug.
                    //
                    // The first attempt at this fix did not work and the receipt said
                    // so: it introduced an `effective` ref but never updated it, and
                    // `List.filter` is eager, so every predicate ran before any arm
                    // did. Eligibility is therefore decided INSIDE the loop, against a
                    // result the arms themselves update.
                    let effective = ref result

                    for cond, steps in stage.Post |> List.sortBy (fun (c, _) -> postRank c) do
                        if postFires cond effective.Value previous then
                            // A post block runs even though the stage failed — that is
                            // the point of it — so it gets a clear failure flag. A
                            // failure INSIDE post belongs to the build AND to the
                            // effective result the later arms are chosen against.
                            let postCtx =
                                { ctx with
                                    Failed = ref false
                                    Sink =
                                        fun st ->
                                            effective.Value <- BuildStatus.worstOf effective.Value st
                                            ctx.Sink st }

                            for st in steps do
                                if not (halted postCtx) then
                                    runStepDispatch postCtx cwd stage st deadline

                            if postCtx.Failed.Value then ctx.Failed.Value <- true

            and runStage (ctx: BranchCtx) (cwd: string) (inherited: int64 option) (stage: Stage) =
                // A stage's own `options { timeout(...) }` tightens whatever it inherited.
                let deadline, stageDeclaredDeadline, optionError = deadlineFromOptions stage.Options inherited

                // A declared bound we cannot understand must stop the build, not vanish.
                match optionError with
                | Some e ->
                    emit $"ERROR: stage '{stage.Name}' declares an unusable timeout option: {e}"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                | None ->

                if not (halted ctx) then
                    // FG-048. The `when` gate, before anything else runs.
                    let gate =
                        match stage.When with
                        | None -> Some true
                        | Some cond -> evalWhen stage cond

                    match gate with
                    | Some false ->
                        // MEASURED: a skipped stage leaves the build SUCCESS, and
                        // its `post` block does NOT run either. Emitting a line is
                        // deliberate — Jenkins says "Stage \"x\" skipped due to when
                        // conditional", and being quieter than Jenkins about why a
                        // stage did not run is the JB-DUR-005 defect in miniature.
                        // Receipt: `when-conditions`.
                        emit $"Stage \"{stage.Name}\" skipped due to when conditional"

                    | None ->
                        // Cannot decide. Fail closed with a named reason rather
                        // than pick a direction: guessing wrong either runs a
                        // stage Jenkins skips or skips one Jenkins runs.
                        emit $"ERROR: stage '{stage.Name}' has a `when` condition this engine cannot evaluate; refusing to guess whether it should run"
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure

                    | Some true ->
                    let stageStatus = ref BuildStatus.Success

                    let body =
                        { ctx with
                            Failed = ref false
                            Sink = fun st ->
                                    stageStatus.Value <- BuildStatus.worstOf stageStatus.Value st
                                    ctx.Sink st }

                    runStageBody body cwd deadline stage

                    if body.Failed.Value then ctx.Failed.Value <- true

                    // FG-102, measured position: a stage-declared timeout announces its
                    // expiry right after the interrupted body, BEFORE the post arm the
                    // abort selects (`cancellation-selects-post-arm`).
                    // The check is against the stage's OWN declared deadline — the
                    // effective one can be an inherited outer bound, and announcing
                    // the OUTER expiry here would double the sentence when the owner
                    // announces it too.
                    if body.Failed.Value && deadlineDidFire stageDeclaredDeadline then
                        emit "Timeout has been exceeded"

                    // Stage post is selected against the STAGE's result, and
                    // `previous` is None because this harness runs one build per
                    // job (it deletes the job around every run). `fixed` and
                    // `regression` are therefore implemented and measured but not
                    // receipt-proven — see FG-049b.
                    //
                    // REVIEW FIX (Codex, PR #13): the post context's failure flag was
                    // created fresh and then DISCARDED, so a failing `post` on an
                    // otherwise successful stage marked the build failed but left the
                    // pipeline runnable — later stages ran and a failFast parent was
                    // never told. Jenkins propagates it.
                    let postCtx = { ctx with Failed = ref false }

                    // MEASURED: a STAGE's `options { timeout }` bounds the stage's STEPS,
                    // not its `post`. Jenkins ran the `aborted` arm after the stage
                    // deadline expired (`cancellation-selects-post-arm`), while a
                    // PIPELINE-level timeout DOES bound post (`options-timeout-wraps-post`).
                    // Passing the stage's own expired deadline into post aborted every arm
                    // before it could run — which would have silently swallowed exactly the
                    // failure notifications a `post { aborted }` exists to send.
                    runPostWithDeadline postCtx cwd stage stageStatus.Value None inherited
                    if postCtx.Failed.Value then ctx.Failed.Value <- true

            /// REVIEW FIX (Codex P1, PR #12): control-flow steps nested inside
            /// another wrapper used to be routed straight at `Executor.runStep`,
            /// which does not know them — so `retry(2) { timeout(10) { sh '…' } }`
            /// failed as an unsupported step every time. Every body now re-enters
            /// this dispatcher, so wrappers compose to any depth.
            /// Guard around every step. A GString referencing a name bound NOWHERE is a
            /// failed Groovy property lookup, and Jenkins FAILS the build on it —
            /// MEASURED (receipt `gstring-unresolved-property`):
            ///   groovy.lang.MissingPropertyException: No such property: X
            ///   for class: groovy.lang.Binding
            /// The strict renderer raises; this converts the raise into that failure.
            /// Erasing the name to "" instead would RUN a command the author never
            /// wrote (`deploy ${TARGET}` → `deploy `), with the build green.
            and runStepDispatch (ctx: BranchCtx) (cwd: string) (stage: Stage) (step: Step) (deadline: int64 option) =
                try
                    runStepDispatchBody ctx cwd stage step deadline
                with
                | GString.MissingProperty name ->
                    emit $"ERROR: No such property: {name} for class: groovy.lang.Binding"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                | GString.UnsupportedExpression what ->
                    // A modelling limit, refused by name — never an invented value.
                    emit $"ERROR: cannot evaluate expression: {what}"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure

            and runStepDispatchBody (ctx: BranchCtx) (cwd: string) (stage: Stage) (step: Step) (deadline: int64 option) =
                // REVIEW FIX (Codex, PR #13 round 4): a deadline only ever became a
                // `TimeoutMs` for the shell runner. `echo`, `junit`,
                // `archiveArtifacts` and wrapper dispatch enforce nothing, so a
                // `timeout` block full of those could keep running long after Jenkins
                // would have aborted it. The deadline is now checked BEFORE every
                // dispatch, whatever the step is.
                let expired =
                    match deadline with
                    | Some d -> runClock.ElapsedMilliseconds >= d
                    | None -> false

                if expired then
                    emit $"ERROR: timeout expired before step '{step.Name}'; block aborted"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Aborted
                else

                match step.Name, step.Positional with
                // JB-FAIL-001/002: timeout and abort share one interrupt path,
                // and the interrupt is a trappable SIGTERM with a grace window.
                | "timeout", _ when not (List.isEmpty step.Block) ->
                    match timeoutMs (renderStepArgs ctx stage step) with
                    | Error why ->
                        emit $"ERROR: {why}; refusing to guess a deadline"
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    | Ok ms ->
                        // ONE deadline for the whole block. A nested timeout can
                        // only tighten it, never extend past its parent.
                        let mine = runClock.ElapsedMilliseconds + ms

                        let effective =
                            match deadline with
                            | Some outer -> min outer mine
                            | None -> mine

                        // FG-102, measured wording: the block announces its budget on
                        // entry and its expiry after the interrupt narration, so the
                        // logs COMPARE where these sentences were suppressed before.
                        emit ("Timeout set to expire in " + humanizeSpan ms)

                        for inner in step.Block do
                            if not (halted ctx) then
                                runStepDispatch ctx cwd stage inner (Some effective)

                        // OWN deadline, not the effective one: under a shorter outer
                        // bound this block's budget may be untouched when the outer
                        // expiry aborts it, and the OWNER announces that.
                        if ctx.Failed.Value && deadlineDidFire (Some mine) then
                            emit "Timeout has been exceeded"

                // JB-FAIL-005: retry(N) is N TOTAL attempts, not N retries after
                // the first, and there is no backoff. `retry(3)` around a step
                // that always fails runs it exactly three times.
                | "retry", _ when not (List.isEmpty step.Block) ->
                    let attempts = max 1 (retryCount (renderStepArgs ctx stage step))
                    let mutable attempt = 1
                    let mutable settled = false

                    while not settled && attempt <= attempts do
                        // Each attempt gets a fresh failure flag AND a throwaway
                        // status sink. MEASURED (FG-035): a body that fails once
                        // then succeeds is a SUCCESS build. Bumping the build
                        // status from the failed attempt reported `failure` with an
                        // identical workspace — the work was right, the bookkeeping
                        // was not.
                        // Receipt: `retry-succeeds`.
                        let attemptStatus = ref BuildStatus.Success

                        let attemptCtx =
                            { ctx with
                                Failed = ref false
                                Sink = fun st -> attemptStatus.Value <- BuildStatus.worstOf attemptStatus.Value st }

                        for inner in step.Block do
                            if not (halted attemptCtx) then
                                runStepDispatch attemptCtx cwd stage inner deadline

                        if not attemptCtx.Failed.Value then
                            // A retried body may still have gone unstable; that is
                            // not a retryable failure, so it must reach the build.
                            ctx.Sink attemptStatus.Value
                            settled <- true
                        elif attempt < attempts then
                            // Jenkins prints this between attempts, with no delay:
                            // retry does not back off.
                            emit "Retrying"
                            attempt <- attempt + 1
                        else
                            // Final attempt failed: now it is the build's failure.
                            ctx.Sink attemptStatus.Value
                            ctx.Failed.Value <- true
                            attempt <- attempt + 1

                // FG-041b. `withEnv(['A=1']) { … }` — block-scoped. MEASURED:
                // after the block an added variable is UNSET and a shadowed one
                // reverts, so the binding is an overlay on the inner scope only.
                // Receipt: `withenv-scoping`.
                | "withEnv", _ when not (List.isEmpty step.Block) ->
                    // The argument is a Groovy LIST literal, handed over as one
                    // raw positional: ['ADDED=x', 'SHADOWED=y']. Splitting it here
                    // keeps ADR 0002's rule that expression-shaped text stays text
                    // until something needs its meaning.
                    // REVIEW FIX (Codex, PR #13): splitting on every comma corrupted
                    // any value containing one — `withEnv(['CSV=a,b'])` bound
                    // `CSV=a` while Jenkins exposes `a,b`. Match the QUOTED list
                    // elements instead, so commas inside an element are content.
                    // Group 1 is single-quoted (LITERAL in Groovy), group 2 is
                    // double-quoted (a GString, so it interpolates). Keeping the
                    // distinction here is the same fix as for `environment { }`.
                    let bindings =
                        step.Positional
                        |> List.collect (fun raw ->
                            [ for m in Text.RegularExpressions.Regex.Matches(raw, "'([^']*)'|\"([^\"]*)\"") ->
                                if m.Groups[1].Success then m.Groups[1].Value, false
                                else m.Groups[2].Value, true ]
                            |> List.choose (fun (entry, interpolates) ->
                                match entry.IndexOf '=' with
                                | i when i > 0 ->
                                    let name = entry.Substring(0, i)
                                    let raw = entry.Substring(i + 1)

                                    // REVIEW FIX (Codex, PR #14 round 13): `withEnv`
                                    // extracts its entries with a regex and never goes
                                    // through the lexer, so the NUL-sentinel handling
                                    // that protects `"\$X"` in an `environment` block
                                    // did not apply here — `withEnv(["X=\$BUILD_NUMBER"])`
                                    // was expanded where Groovy keeps it literal. Apply
                                    // the same substitution before interpolating.
                                    let value =
                                        if interpolates then
                                            raw.Replace("\\$", "\u0000")
                                            |> GString.interpolateInto
                                                scriptBinding
                                                adviseNewBinding
                                                (envForWith ctx.EnvOverlay stage |> Map.ofList)
                                        else
                                            raw

                                    if interpolates && raw.Contains "$" then
                                        warnSecretInterpolation ctx "withEnv" [ value ]

                                    Some(name, value)
                                | _ -> None))

                    // REVIEW FIX (Codex, PR #13 round 2): `PATH+TOOLS=/opt/tools/bin`
                    // is the standard Jenkins idiom for PREPENDING to PATH. The binding
                    // was copied literally as a variable called `PATH+TOOLS`, so the
                    // wrapped process kept its old PATH and the tools were not found.
                    // REVIEW FIX (Copilot + Codex, PR #14 — both flagged it): this read
                    // the RAW concatenation with List.tryPick, i.e. the FIRST PATH,
                    // while the environment is explicitly last-wins. A pipeline PATH
                    // followed by a stage PATH produced `/tools:<pipeline-path>` —
                    // prepending onto an out-of-date PATH, which can run the wrong
                    // executable. `envForWith` already resolves last-wins, so ask it.
                    let outerPath =
                        envForWith ctx.EnvOverlay stage
                        |> List.tryPick (fun (k, v) -> if k = "PATH" then Some v else None)
                    let pathAdditions =
                        bindings |> List.filter (fun (k, _) -> k.StartsWith "PATH+")

                    let plainBindings =
                        bindings |> List.filter (fun (k, _) -> not (k.StartsWith "PATH+"))

                    // REVIEW FIX (Codex, PR #14 round 3): the base PATH was taken from
                    // the ENCLOSING scope only, so `withEnv(['PATH=/custom',
                    // 'PATH+TOOLS=/tools'])` produced `/tools:<outer-path>` and
                    // silently discarded `/custom` — the augmentation has to build on
                    // the plain PATH supplied by the SAME invocation when there is one.
                    // REVIEW FIX (Codex, PR #14 round 6): with no PATH declared
                    // anywhere, this defaulted to "" and produced `/tools:`, wiping the
                    // inherited PATH so ordinary tools in /usr/bin vanished. An earlier
                    // revision had this fallback and a later edit of mine dropped it.
                    // REVIEW FIX (Codex, PR #14 round 11): tryPick took the FIRST plain
                    // PATH in the list, but the environment is last-wins, so
                    // ['PATH=/first', 'PATH=/second', 'PATH+TOOLS=/tools'] produced
                    // `/tools:/first` and discarded the effective `/second`.
                    let basePath =
                        plainBindings
                        |> List.filter (fun (k, _) -> k = "PATH")
                        |> List.tryLast
                        |> Option.map snd
                        |> Option.orElse outerPath
                        |> Option.defaultWith (fun () ->
                            match Environment.GetEnvironmentVariable "PATH" with
                            | null -> ""
                            | p -> p)

                    let bindings =
                        if List.isEmpty pathAdditions then
                            plainBindings
                        else
                            let prefix = pathAdditions |> List.map snd |> String.concat ":"

                            (plainBindings |> List.filter (fun (k, _) -> k <> "PATH"))
                            @ [ "PATH", prefix + ":" + basePath ]

                    let inner = { ctx with EnvOverlay = ctx.EnvOverlay @ bindings }

                    for st in step.Block do
                        if not (halted inner) then
                            runStepDispatch inner cwd stage st deadline

                    if inner.Failed.Value then ctx.Failed.Value <- true

                // FG-044. `withCredentials([...]) { … }`.
                //
                // MEASURED: Jenkins binds the VALUE into the named variable, masks it in
                // the log as `****`, and unsets it after the block. It also prints
                // "Masking supported pattern matches of $VAR", which is engine narration.
                // Receipts: `credentials-string` for the BINDING (env membership, value
                // length, unset after the block), and `credentials-userpass-masking` for
                // the MASKING — that is the only case printing a secret to stdout (`****`).
                // `credentials-string` emits no output lines at all, so it could never have
                // supported the console half of this sentence.
                | "withCredentials", _ when not (List.isEmpty step.Block) ->
                    let typeName =
                        function
                        | SecretText _ -> "secret-text"
                        | UsernamePassword _ -> "username/password"
                        | SecretFile _ -> "secret-file"

                    let requests = Credentials.parseRequests (String.concat " " step.Positional)
                    let store = credentialStore ()

                    let unmodelled =
                        requests
                        |> List.choose (function
                            | BindUnmodelled(kind, _) -> Some kind
                            | _ -> None)

                    // REVIEW FIX (Copilot, PR #15): if nothing parsed, the block used to
                    // run with NO bindings at all — and emit a masking line with an empty
                    // variable list — so a build could appear to succeed with its
                    // credentials missing entirely. `withCredentials` with nothing bound
                    // is never what the author meant.
                    let parsedNothing = List.isEmpty requests

                    let missing =
                        Credentials.idsOf requests |> List.filter (fun id -> not (store.ContainsKey id))

                    if parsedNothing then
                        emit $"""ERROR: withCredentials bound nothing — could not parse any binding from '{String.concat " " step.Positional}'"""
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    elif not (List.isEmpty unmodelled) then
                        // Fail CLOSED by name. A binding kind we do not model must not
                        // yield an empty variable: the build would go green while the
                        // deploy authenticated as nobody.
                        emit $"""ERROR: unsupported credential binding kind(s) {String.concat ", " unmodelled}; refusing to bind an empty credential"""
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    elif not (List.isEmpty missing) then
                        emit $"""ERROR: credential id(s) not found: {String.concat ", " missing}"""
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    else
                        let secretDir = Path.Combine(workspaceRoot, "_secrets", jobName)

                        // REVIEW FIX (Codex, PR #15): a type mismatch used to be COERCED —
                        // a `string` request against a username/password credential got the
                        // username, a `usernamePassword` request against secret text left
                        // the username unset. That is precisely the "build goes green while
                        // the deploy authenticates as nobody" outcome this step's own
                        // comment warns about, two lines above the code that did it. A
                        // mismatch is a misconfiguration and must fail before the body runs.
                        let mismatches = System.Collections.Generic.List<string>()
                        // Non-secret variables that still have to reach the child, e.g. a
                        // usernamePassword's username.
                        let plainEnv = System.Collections.Generic.List<string * string>()

                        let bindings =
                            requests
                            |> List.collect (fun r ->
                                match r with
                                | BindText(id, v) ->
                                    match store.[id] with
                                    | SecretText value -> [ Secrets.bind secretDir v value ]
                                    | other ->
                                        mismatches.Add
                                            $"'{id}' is a {typeName other} credential but was requested as `string`"
                                        []
                                | BindUserPass(id, uv, pv) ->
                                    match store.[id] with
                                    | UsernamePassword(u, p) ->
                                        // BOTH are masked. A Codex review (PR #15) said the
                                        // username is not a secret on Jenkins and asked for
                                        // it to be exported plainly — citing a comment I had
                                        // written in the credentials-userpass case asserting
                                        // exactly that. I had never measured it. A receipt
                                        // that prints both to STDOUT settles it:
                                        //   Jenkins: user-on-stdout=****
                                        //   Jenkins: pass-on-stdout=****
                                        // Jenkins registers both values with its masker, so
                                        // masking both is parity and the "fix" broke it. My
                                        // unverified comment became the reviewer's evidence,
                                        // which is the real defect here.
                                        [ Secrets.bind secretDir uv u; Secrets.bind secretDir pv p ]
                                    | other ->
                                        mismatches.Add
                                            $"'{id}' is a {typeName other} credential but was requested as `usernamePassword`"
                                        []
                                | BindFile(id, v) ->
                                    // REVIEW FIX (both reviewers, PR #15): Jenkins binds the
                                    // requested variable to a PATH to a temporary file. The
                                    // code bound `<VAR>_CONTENT` and never `<VAR>` at all,
                                    // while the comment claimed otherwise — so every
                                    // `file()` body ran with its variable unset.
                                    match store.[id] with
                                    | SecretFile(_, bytes) ->
                                        // The requested variable holds the PATH; the bytes
                                        // are written verbatim so a binary credential is
                                        // not corrupted on the way through.
                                        [ Secrets.bindBytes secretDir v bytes ]
                                    // REVIEW FIX (Codex, PR #15 round 3): secret TEXT was
                                    // silently accepted for a `file()` request while every
                                    // other mismatch failed closed — an inconsistency that
                                    // let a misconfigured credential through the one gate
                                    // built to stop it.
                                    | other ->
                                        mismatches.Add
                                            $"'{id}' is a {typeName other} credential but was requested as `file`"
                                        []
                                | BindUnmodelled _ -> [])

                        // Jenkins narrates the masking; excluded from comparison as
                        // engine narration, but said so a reader is not left guessing.
                        if mismatches.Count > 0 then
                            // REVIEW FIX (Codex, PR #15): a mixed request creates the valid
                            // bindings BEFORE the mismatch is noticed, and this branch
                            // returned without revoking them — leaving secret files on disk
                            // outside the workspace, so workspace cleanup never removed
                            // them.
                            Secrets.revoke bindings
                            emit $"""ERROR: credential type mismatch: {String.concat "; " mismatches}"""
                            ctx.Failed.Value <- true
                            ctx.Sink BuildStatus.Failure
                        else

                        // One line naming every bound variable, matching Jenkins' shape.
                        // The wording is not compared (see the contract); the line exists
                        // so a reader of OUR log is told what is being masked.
                        // Register BEFORE the narration line, so nothing this block can
                        // print is emitted while the masker is still unaware of it. The
                        // recorded index scopes the LEAK CHECK, not the masking: only
                        // output from here on can be a leak of these values.
                        lock outputLock (fun () ->
                            for b in bindings do
                                boundSecrets.Add(b, output.Count))

                        let names = bindings |> List.map (fun b -> "$" + b.ValueVariable)
                        emit $"""Masking supported pattern matches of {String.concat " or " names}"""

                        let overlay =
                            ctx.EnvOverlay @ List.ofSeq plainEnv @ Secrets.environmentFor bindings
                        let inner = { ctx with EnvOverlay = overlay; Secrets = ctx.Secrets @ bindings }

                        for st in step.Block do
                            if not (halted inner) then
                                runStepDispatch inner cwd stage st deadline

                        if inner.Failed.Value then ctx.Failed.Value <- true

                        // Unset after the block: measured on Jenkins.
                        Secrets.revoke bindings

                // FG-047 companion. `deleteDir()` empties the CURRENT directory — the
                // workspace, or the enclosing `dir` block's cwd — without removing the
                // directory itself. It is what makes the stash test meaningful.
                | "deleteDir", _ ->
                    // Polled before AND after each top-level entry: a recursive delete can
                    // itself outlast the deadline. Getting this wrong three times running
                    // is what made FG-101 a ticket rather than another instance fix.
                    if Directory.Exists cwd then
                        let mutable outcome = Running

                        for entry in Directory.GetFileSystemEntries cwd do
                            if outcome = Running then
                                match cancellationOf ctx deadline with
                                | Running ->
                                    try
                                        if Directory.Exists entry then Directory.Delete(entry, true)
                                        else File.Delete entry

                                        outcome <- cancellationOf ctx deadline
                                    with ex ->
                                        emit $"ERROR: deleteDir could not remove {Path.GetFileName entry}: {ex.GetType().Name}"
                                        ctx.Failed.Value <- true
                                        ctx.Sink BuildStatus.Failure
                                | c -> outcome <- c

                        applyCancellation ctx "deleteDir" deadline outcome

                | "input", _ ->
                    let message =
                        step.Positional
                        |> List.tryHead
                        |> Option.orElse (
                            step.Named
                            |> List.tryPick (fun (k, v) -> if k = "message" then Some v else None))
                        |> Option.defaultValue "Proceed?"

                    // MEASURED: the confirmation label is configurable and Jenkins prints
                    // it — `ok: 'Ship it'` yields "Ship it or Abort". Hardcoding "Proceed"
                    // diverged on any pipeline that customises it.
                    // Receipt: `input-ok-label`.
                    let okLabel =
                        step.Named
                        |> List.tryPick (fun (k, v) -> if k = "ok" then Some v else None)
                        |> Option.defaultValue "Proceed"

                    // REVIEW FIX (Codex, PR #17 round 3): Jenkins evaluates a GString
                    // before showing the prompt, so `input message: "Build ${env.X}?"`
                    // displays the VALUE. Emitting the parser's raw text diverged. A
                    // single-quoted argument stays literal, which is why the parser now
                    // records which named args were single-quoted — shell steps never
                    // needed this because the shell does its own expansion.
                    // `message` may arrive positionally (`input 'Deploy ${X}?'`) or named,
                    // and Jenkins treats a single-quoted one as LITERAL either way. The
                    // named-only check interpolated every positional prompt.
                    // FG-100: the model decides the kind; this step no longer does.
                    let messageKeyName =
                        if step.Named |> List.exists (fun (k, _) -> k = "message") then "message" else "#0"

                    // Three kinds, not two. A single-quoted argument is literal text; a
                    // double-quoted one is a GString to interpolate; an UNQUOTED one is a
                    // Groovy EXPRESSION — `input message: env.TARGET` — which Jenkins
                    // evaluates and which was being displayed as its own source text.
                    //
                    // Rendered in SOURCE order, exactly as the generic step path: with
                    // rendering being evaluation, `input ok: "${x = 'Ship'; x}",
                    // message: "$x"` binds x from `ok` before `message` reads it, and
                    // rendering message-then-ok raised MissingProperty on it. One sweep
                    // in the recorded order; the prompt and label select from it.
                    let rendered = renderStepArgs ctx stage step

                    let renderedMessage =
                        match messageKeyName with
                        | "#0" -> rendered.Positional |> List.tryHead |> Option.defaultValue message
                        | key ->
                            rendered.Named
                            |> List.tryPick (fun (k, v) -> if k = key then Some v else None)
                            |> Option.defaultValue message

                    let renderedOk =
                        rendered.Named
                        |> List.tryPick (fun (k, v) -> if k = "ok" then Some v else None)
                        |> Option.defaultValue okLabel

                    emit renderedMessage
                    emit $"""{renderedOk} or Abort"""

                    match deadline with
                    | None ->
                        emit "ERROR: input requires human approval and this engine has no approver; wrap it in a timeout to get Jenkins' abort-on-expiry behaviour"
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    | Some _ ->
                        // Wait out the deadline exactly as an unanswered prompt would.
                        //
                        // REVIEW FIX (both reviewers, PR #17): the loop exited on EITHER
                        // the deadline or a sibling interrupt and then reported Aborted
                        // unconditionally — so a failFast sibling's FAILURE became an
                        // abort. That is the collateral-outranks-cause bug for the FOURTH
                        // time in this project (shell steps, stash, unstash, deleteDir),
                        // which is the argument for FG-002e being a sweep rather than a
                        // queue of instances.
                        //
                        // Polling backs off instead of spinning at 50 ms: an `input` under
                        // an hour-long timeout woke ~72,000 times for nothing.
                        // The ONE model. This loop got the cause wrong twice: once by
                        // omitting the sibling check, once by testing expiry first so a
                        // sibling failing in the final sleep lost a tie.
                        let mutable outcome = Running

                        while outcome = Running do
                            match cancellationOf ctx deadline with
                            | Running ->
                                // Full-width comparison; only the sleep narrows, and it is
                                // bounded by 250 anyway. A 30-day deadline once wrapped
                                // negative here and aborted the prompt instantly.
                                let left = defaultArg (remainingMs deadline) 0L
                                // TimeSpan, not `int` — the last remaining narrowing
                                // on a duration path, retired by FG-103 even though its
                                // 250 ms clamp made it arithmetically safe: the CLASS
                                // is banned, not the instance (it wrapped twice before).
                                System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(float (min 250L (max 10L left))))
                            | c -> outcome <- c

                        applyCancellation ctx "input" deadline outcome

                // FG-047. `stash` / `unstash`. Storage is controller-side — under the
                // artifact root, NOT the workspace — which is what makes a stash survive
                // `deleteDir()`, as measured on Jenkins. Keeping it in the workspace
                // would pass a naive test and fail the one that matters.
                | "stash", _ ->
                    let step = renderStepArgs ctx stage step
                    let store = StashStore.under (Path.Combine(artifactRoot, "_stash"))

                    let name =
                        step.Named
                        |> List.tryPick (fun (k, v) -> if k = "name" then Some v else None)
                        |> Option.orElse (List.tryHead step.Positional)

                    let includes =
                        step.Named
                        |> List.tryPick (fun (k, v) -> if k = "includes" then Some v else None)
                        |> Option.map (fun v -> v.Split ',' |> Array.toList |> List.map (fun s -> s.Trim()))
                        |> Option.defaultValue []

                    match name with
                    | None ->
                        emit "ERROR: stash requires a name"
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    | Some n ->
                        // REVIEW FIX (Codex, PR #15): this checked only ctx.Interrupt while
                        // the archive and junit predicates combine interruption WITH the
                        // deadline — so a stash inside a `timeout` could still finish past
                        // it. Same predicate everywhere now.
                        let abort () = cancellationOf ctx deadline <> Running

                        let allowEmpty =
                            step.Named
                            |> List.tryPick (fun (k, v) -> if k = "allowEmpty" then Some v else None)
                            |> Option.map (fun v -> v.Trim().ToLowerInvariant() = "true")
                            |> Option.defaultValue false

                        let excludes =
                            step.Named
                            |> List.tryPick (fun (k, v) -> if k = "excludes" then Some v else None)
                            |> Option.map (fun v -> v.Split ',' |> Array.toList |> List.map (fun s -> s.Trim()))
                            |> Option.defaultValue []

                        let saved, aborted = Stash.save store jobName cwd n includes excludes abort

                        if aborted then
                            applyCancellation ctx "stash" deadline (cancellationOf ctx deadline)
                        elif List.isEmpty saved && not allowEmpty then
                            // MEASURED: Jenkins FAILS the build here (default
                            // allowEmpty: false) — the pipeline stops and later steps do
                            // not run. Reporting success would let the build continue
                            // having silently lost the inputs it asked for, and a later
                            // `unstash` would succeed with nothing.
                            // Receipt: `stash-empty-fails`.
                            emit $"ERROR: No files included in stash ‘{n}’"
                            ctx.Failed.Value <- true
                            ctx.Sink BuildStatus.Failure
                        else
                            emit $"Stashed {saved.Length} file(s)"

                | "unstash", _ ->
                    let step = renderStepArgs ctx stage step
                    let store = StashStore.under (Path.Combine(artifactRoot, "_stash"))

                    let name =
                        step.Positional
                        |> List.tryHead
                        |> Option.orElse (
                            step.Named
                            |> List.tryPick (fun (k, v) -> if k = "name" then Some v else None))

                    match name with
                    | None ->
                        emit "ERROR: unstash requires a name"
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    | Some n ->
                        let abort () = cancellationOf ctx deadline <> Running

                        match Stash.restore store jobName cwd n abort with
                        | Result.Error e ->
                            // A missing stash FAILS. Carrying on with none of the files
                            // the build asked for is the silent-loss shape.
                            //
                            // REVIEW FIX (Codex, PR #15 round 3): an INTERRUPTED restore
                            // came back through this same branch and was reported as a
                            // plain failure, so a `timeout` whose last step is `unstash`
                            // selected post { failure } where every other timed-out step
                            // selects post { aborted }.
                            if e.StartsWith "aborted:" then
                                applyCancellation ctx "unstash" deadline (cancellationOf ctx deadline)
                            else
                                // A MISSING stash is a genuine failure, not a
                                // cancellation, and must not be classified by this model.
                                emit $"ERROR: {e}"
                                ctx.Failed.Value <- true
                                ctx.Sink BuildStatus.Failure
                        | Result.Ok _ -> ()

                | "dir", (sub :: _) ->
                    // `dir('x') { … }` — nested cwd, auto-created
                    let sub = (renderStepArgs ctx stage step).Positional |> List.head

                    match Workspace.resolveUnder cwd sub with
                    | Result.Error e ->
                        emit $"dir refused: {e.Describe}"
                        ctx.Failed.Value <- true
                        ctx.Sink BuildStatus.Failure
                    | Result.Ok target ->
                        Directory.CreateDirectory target |> ignore

                        // `target`, NOT `cwd`. Restructuring the dispatcher for the
                        // nested-wrapper fix reintroduced `cwd` here, and the body
                        // wrote to the stage root instead of the subdirectory. Both
                        // engines agreed on the file CONTENT, so only the workspace
                        // manifest's PATHS caught it — which is precisely why the
                        // manifest is (path, hash) pairs and not a content digest.
                        for inner in step.Block do
                            if not (halted ctx) then
                                runStepDispatch ctx target stage inner deadline

                | _ -> runStepInner ctx stage cwd step deadline

            and runStageBody (ctx: BranchCtx) (cwd: string) (deadline: int64 option) (stage: Stage) =
                    for step in stage.Steps do
                        if not (halted ctx) then
                            runStepDispatch ctx cwd stage step deadline

                    if stage.IsParallel && not (List.isEmpty stage.Nested) then
                        // JB-FAIL-006/007. Branches run concurrently and, by
                        // default, a failing branch does NOT stop its siblings —
                        // they run to completion and the build takes the worst
                        // result. `failFast true` interrupts them instead.
                        //
                        // Branches share the workspace, as measured on Jenkins:
                        // one `agent` means one workspace, and two branches
                        // writing the same path race. That is a Jenkins footgun
                        // we reproduce rather than silently fix, because a
                        // lift-and-shift promise means their pipeline behaves the
                        // same way here, bugs included.
                        let failFast = stage.FailFast || alwaysFailFast

                        // REVIEW FIX (Copilot, PR #12): this was a plain `bool ref`
                        // written by one branch thread and polled by others. Under
                        // the .NET memory model a non-volatile read may observe a
                        // stale value, so failFast could interrupt late or not at
                        // all — and a test would pass anyway because it usually
                        // works. A CancellationTokenSource is the synchronised
                        // signal this needs.
                        use siblingFailed = new System.Threading.CancellationTokenSource()
                        // -1 is UNSET. Zero is a real stopwatch reading — a branch that
                        // fails during millisecond zero would otherwise store the sentinel
                        // itself and read back as "never signalled".
                        let siblingFailedAt = ref -1L

                        let branches =
                            stage.Nested
                            |> List.map (fun branch ->
                                let branchCtx =
                                    { Failed = ref false
                                      // REVIEW FIX (Codex, PR #13): this sent branch
                                      // status straight to the GLOBAL sink, bypassing
                                      // the enclosing stage's. `stageStatus` therefore
                                      // stayed Success on a failed parallel, so the
                                      // stage's `post { success { … } }` ran and
                                      // `post { failure { … } }` did not — i.e. a
                                      // publish or deploy step firing on a red build.
                                      // The build status still gets it: ctx.Sink
                                      // forwards upward to `bump`.
                                      Sink = ctx.Sink
                                      EnvOverlay = ctx.EnvOverlay
                                      Secrets = ctx.Secrets
                                      // The stamp must travel WITH the predicate it
                                      // describes. A non-failFast block inherits the
                                      // parent's interrupt, so inheriting a fresh local ref
                                      // would have it read an unrelated time — or none —
                                      // and call an outer sibling's failure a deadline.
                                      SiblingFailedAt =
                                        if failFast then siblingFailedAt else ctx.SiblingFailedAt
                                      Interrupt =
                                        if failFast then
                                            Some(fun () -> siblingFailed.IsCancellationRequested)
                                        else
                                            ctx.Interrupt }

                                branchCtx,
                                System.Threading.Tasks.Task.Run(fun () ->
                                    runStage branchCtx cwd deadline branch

                                    if branchCtx.Failed.Value then
                                        // Jenkins names the branch that failed. EMITTING it
                                        // is better than suppressing Jenkins' copy: an
                                        // exclusion that a user's own output can match is a
                                        // false-PROVEN path, and this sentence is real
                                        // information a reader wants.
                                        emit $"Failed in branch {branch.Name}"
                                        // Stamp only the FIRST signal. Every failing
                                        // branch reaches here, including ones cancelled as
                                        // COLLATERAL, so an unconditional write let a later
                                        // collateral failure overwrite the original cause's
                                        // instant — a still-unwinding sibling would then see
                                        // the later stamp, call the deadline earlier, and
                                        // flip the build from failure to aborted. Exactly
                                        // the misclassification this model exists to stop,
                                        // reintroduced by the timestamp added to fix it.
                                        System.Threading.Interlocked.CompareExchange(
                                            siblingFailedAt, runClock.ElapsedMilliseconds, -1L)
                                        |> ignore

                                        siblingFailed.Cancel()))

                        // Every branch is awaited even under failFast: an
                        // interrupted branch still has a process group to reap,
                        // and abandoning it is how orphans happen (FG-032).
                        branches
                        |> List.iter (fun (_, t) ->
                            try
                                t.Wait()
                            with _ ->
                                ())

                        if branches |> List.exists (fun (bc, _) -> bc.Failed.Value) then
                            ctx.Failed.Value <- true
                    else
                        for nested in stage.Nested do
                            runStage ctx cwd deadline nested

            let root =
                { Interrupt = None
                  Failed = ref false
                  Sink = bump
                  EnvOverlay = []
                  Secrets = []
                  SiblingFailedAt = ref -1L }

            // Pipeline-level `options { timeout(...) }` bounds the WHOLE build.
            let pipelineDeadline, pipelineDeclaredDeadline, pipelineOptionError = deadlineFromOptions pipeline.Options None

            match pipelineOptionError with
            | Some e ->
                emit $"ERROR: pipeline declares an unusable timeout option: {e}"
                root.Failed.Value <- true
                bump BuildStatus.Failure
            | None -> ()

            // FG-102: the pipeline-level timeout announces its expiry ONCE, at the
            // point it is first observed to have aborted work — after the stages
            // when a stage died to it, or after the pipeline post when the post did
            // (`options-timeout-pipeline`, `options-timeout-wraps-post`).
            let mutable exceededAnnounced = false

            let announcePipelineExceeded () =
                if
                    not exceededAnnounced
                    && root.Failed.Value
                    && deadlineDidFire pipelineDeclaredDeadline
                then
                    exceededAnnounced <- true
                    emit "Timeout has been exceeded"

            for stage in pipeline.Stages do
                if root.Failed.Value then
                    // Jenkins names every stage it skips because of an earlier
                    // failure. Being quieter than Jenkins about why a stage did not
                    // run is the JB-DUR-005 defect in miniature, so we say it too.
                    emit $"Stage \"{stage.Name}\" skipped due to earlier failure(s)"
                else
                    runStage root workspace pipelineDeadline stage

            announcePipelineExceeded ()

            // Pipeline-level `post` is selected against the BUILD result, so it
            // runs after every stage. Modelled as a synthetic stage carrying only
            // the post section, which is also how the pipeline's own environment
            // reaches those steps.
            if not (List.isEmpty pipeline.Post) then
                let synthetic =
                    { Name = ""
                      Agent = None
                      Environment = []
                      EnvironmentLiteralNames = Set.empty
                      Steps = []
                      Options = []
                      When = None
                      Post = pipeline.Post
                      Nested = []
                      IsParallel = false
                      FailFast = false
                      Position = { Line = 0L; Column = 0L } }

                // REVIEW FIX (Codex, PR #16 round 5): the pipeline deadline reached
                // runStage but NOT the pipeline-level post, so a slow `post { always }`
                // ran unbounded past a timeout Jenkins enforces around it. Same
                // "one path was missed" shape as FG-002e.
                let postRoot = { root with Failed = ref false }
                runPostWithDeadline postRoot workspace synthetic status None pipelineDeadline
                if postRoot.Failed.Value then root.Failed.Value <- true

            announcePipelineExceeded ()

            let workspaceHash, files = Trace.hashWorkspace workspace

            // Fail CLOSED on a leaked secret, checked over the RAW output — before
            // normalisation, which strips exactly the diagnostic lines a secret is
            // most likely to ride out on. Verified by disabling masking and re-running
            // `publish-secret-in-pattern`: the receipt still said PROVEN, because the
            // leaking `ERROR:` line never reaches the comparison. A receipt that stays
            // green while the log leaks is worse than no receipt, so the run itself
            // refuses to produce a trace instead.
            let leakedVars =
                lock outputLock (fun () ->
                    output
                    |> Seq.mapi (fun i l ->
                        // Only bindings that existed when the line was emitted: text
                        // printed BEFORE a value was bound merely coincides with it.
                        let active =
                            boundSecrets |> Seq.filter (fun (_, from) -> from <= i) |> Seq.map fst |> List.ofSeq

                        if List.isEmpty active then [] else Secrets.detectLeaks active l)
                    |> Seq.collect id
                    |> Seq.map (fun leak -> leak.Variable)
                    |> Seq.distinct
                    |> List.ofSeq)

            if not (List.isEmpty leakedVars) then
                Result.Error(
                    $"""SECRET LEAKED to build output (variable(s): {String.concat ", " leakedVars}) — refusing to emit a trace"""
                )
            else

            Result.Ok
                { Result = BuildStatus.toWireString status
                  EngineNotes = List.ofSeq engineNotes
                  Output = Trace.normaliseOutput output
                  WorkspaceHash = workspaceHash
                  WorkspaceFiles = files
                  Concurrent = pipeline.Stages |> Pipeline.flattenStages |> List.exists (fun st -> st.IsParallel)
                  ReportedFailureReason = Trace.reportedFailureReason output }
