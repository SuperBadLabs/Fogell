namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir
open Fogell.Groovy.Interpreter

/// FG-036. What one parallel branch (or the single implicit branch of a
/// sequential pipeline) needs to know about itself.
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
      /// FG-041b. `withEnv([...]) { }` bindings, innermost last. Block-scoped:
      /// MEASURED on Jenkins, after the block an added variable is UNSET and a
      /// shadowed one reverts to its outer value.
      EnvOverlay: (string * string) list }

/// The Fogell side. Parses the same Jenkinsfile, walks its stages, executes each
/// step, and reduces the run to a [Trace] in the same canonical form.
module FogellSide =

    /// Walk a parsed declarative pipeline. This is the minimum sequencer needed
    /// to make the differential meaningful; the durable scheduler (Wave 2) is a
    /// separate concern and is not on this path.
    let run (workspaceRoot: string) (jobName: string) (script: string) : Result<Trace, string> =
        match Fogell.Pipeline.Parser.Parser.parse script with
        | Result.Error e -> Result.Error $"{Fogell.Admission.ErrorCode.toWireString e.Code} at {e.Position}: {e.Message}"
        | Result.Ok pipeline ->
            let output = System.Collections.Generic.List<string>()
            // Parallel branches append from several threads at once.
            let outputLock = obj ()

            /// MEASURED (FG-036): declarative Jenkins emits parallel branch output
            /// with NO `[branchName]` prefix — that belongs to the scripted
            /// `parallel` map form. Fogell emitted one until the receipt said
            /// otherwise, which diverged every parallel case. Inventing attribution
            /// the reference engine does not provide is not a favour, it is a
            /// divergence.
            let emit (line: string) =
                lock outputLock (fun () -> output.Add line)
            let workspace = Path.Combine(workspaceRoot, jobName)
            // artifacts live OUTSIDE the workspace so archiving cannot perturb
            // the workspace hash the differential compares
            let artifactRoot = Path.Combine(workspaceRoot, "_artifacts")
            if Directory.Exists workspace then Directory.Delete(workspace, true)
            Directory.CreateDirectory workspace |> ignore

            /// Environment visible to a step: pipeline scope, overridden by stage
            /// scope. Lexical, and stage wins — the semantics measured on Jenkins.
            let envForWith (overlay: (string * string) list) (stage: Stage) =
                (pipeline.Environment @ stage.Environment @ overlay)
                |> List.fold (fun acc (k, v) -> Map.add k v acc) Map.empty
                |> Map.toList

            let mutable status = BuildStatus.Success
            let statusLock = obj ()
            let bump (s: BuildStatus) = lock statusLock (fun () -> status <- BuildStatus.worstOf status s)

            /// One clock for the whole build, so a `timeout` deadline is an
            /// absolute point in time rather than a per-step budget.
            let runClock = Diagnostics.Stopwatch.StartNew()

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
            let remainingMs (deadline: int64 option) =
                deadline
                |> Option.map (fun d ->
                    let left = d - runClock.ElapsedMilliseconds
                    int (max 1L (min left (int64 Int32.MaxValue))))

            let runStepInner (ctx: BranchCtx) (stage: Stage) (cwd: string) (step: Step) (deadline: int64 option) =
                let script =
                    match step.Positional with
                    | s :: _ -> Some s
                    | [] ->
                        step.Named
                        |> List.tryPick (fun (k, v) -> if k = "script" || k = "message" then Some v else None)

                let result =
                    Executor.runStep
                        { Name = step.Name
                          Script = script
                          Workspace = cwd
                          Environment = envForWith ctx.EnvOverlay stage
                          TimeoutMs =
                            match remainingMs deadline with
                            | Some ms -> Some ms
                            | None -> Some 120_000
                          OnLine = Some emit
                          Interrupt = ctx.Interrupt
                          Secrets = []
                          Named = step.Named
                          Artifacts = Some(ArtifactStore.under artifactRoot)
                          BuildKey = jobName }

                // Output arrives exactly once, via OnLine. An earlier version also
                // appended result.Stdout, so every shell line was emitted twice and
                // the differential reported a phantom divergence at line 1.
                //
                // stderr is not streamed by OnLine, so it is added here.
                for line in result.Stderr.Replace("\r\n", "\n").Split '\n' do
                    if line <> "" then emit line

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
                    let interruptedBySibling =
                        result.Status = BuildStatus.Aborted
                        && (match ctx.Interrupt with
                            | Some p -> p ()
                            | None -> false)

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

            /// Ant-style glob as `when { branch }` / `when { tag }` use it. An
            /// absent variable is never a match: MEASURED, Jenkins skips the stage.
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
                | WhenBranch pattern -> Some(matchesGlob pattern (Map.tryFind "BRANCH_NAME" env))
                | WhenTag pattern -> Some(matchesGlob pattern (Map.tryFind "TAG_NAME" env))

                | WhenEquals(expected, actual) -> Some(expected.Trim() = actual.Trim())

                | WhenNot inner -> evalWhen stage inner |> Option.map not

                | WhenAllOf conds ->
                    let results = conds |> List.map (evalWhen stage)
                    if results |> List.exists Option.isNone then None
                    else Some(results |> List.forall (fun r -> r = Some true))

                | WhenAnyOf conds ->
                    let results = conds |> List.map (evalWhen stage)
                    if results |> List.exists Option.isNone then None
                    else Some(results |> List.exists (fun r -> r = Some true))

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

            let rec runPost (ctx: BranchCtx) (cwd: string) (stage: Stage) (result: BuildStatus) (previous: BuildStatus option) =
                if not (List.isEmpty stage.Post) then
                    stage.Post
                    |> List.filter (fun (cond, _) -> postFires cond result previous)
                    |> List.sortBy (fun (cond, _) -> postRank cond)
                    |> List.iter (fun (_, steps) ->
                        // A post block runs even though the stage failed — that is
                        // the point of it — so it gets a context whose failure flag
                        // is clear. A failure INSIDE post is still the build's.
                        let postCtx = { ctx with Failed = ref false }

                        for st in steps do
                            if not (halted postCtx) then
                                runStepDispatch postCtx cwd stage st None

                        if postCtx.Failed.Value then ctx.Failed.Value <- true)

            and runStage (ctx: BranchCtx) (cwd: string) (stage: Stage) =
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

                    runStageBody body cwd stage

                    if body.Failed.Value then ctx.Failed.Value <- true

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
                    runPost postCtx cwd stage stageStatus.Value None
                    if postCtx.Failed.Value then ctx.Failed.Value <- true

            /// REVIEW FIX (Codex P1, PR #12): control-flow steps nested inside
            /// another wrapper used to be routed straight at `Executor.runStep`,
            /// which does not know them — so `retry(2) { timeout(10) { sh '…' } }`
            /// failed as an unsupported step every time. Every body now re-enters
            /// this dispatcher, so wrappers compose to any depth.
            and runStepDispatch (ctx: BranchCtx) (cwd: string) (stage: Stage) (step: Step) (deadline: int64 option) =
                match step.Name, step.Positional with
                // JB-FAIL-001/002: timeout and abort share one interrupt path,
                // and the interrupt is a trappable SIGTERM with a grace window.
                | "timeout", _ when not (List.isEmpty step.Block) ->
                    match timeoutMs step with
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

                        for inner in step.Block do
                            if not (halted ctx) then
                                runStepDispatch ctx cwd stage inner (Some effective)

                // JB-FAIL-005: retry(N) is N TOTAL attempts, not N retries after
                // the first, and there is no backoff. `retry(3)` around a step
                // that always fails runs it exactly three times.
                | "retry", _ when not (List.isEmpty step.Block) ->
                    let attempts = max 1 (retryCount step)
                    let mutable attempt = 1
                    let mutable settled = false

                    while not settled && attempt <= attempts do
                        // Each attempt gets a fresh failure flag AND a throwaway
                        // status sink. MEASURED (FG-035): a body that fails once
                        // then succeeds is a SUCCESS build. Bumping the build
                        // status from the failed attempt reported `failure` with an
                        // identical workspace — the work was right, the bookkeeping
                        // was not.
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
                | "withEnv", _ when not (List.isEmpty step.Block) ->
                    // The argument is a Groovy LIST literal, handed over as one
                    // raw positional: ['ADDED=x', 'SHADOWED=y']. Splitting it here
                    // keeps ADR 0002's rule that expression-shaped text stays text
                    // until something needs its meaning.
                    // REVIEW FIX (Codex, PR #13): splitting on every comma corrupted
                    // any value containing one — `withEnv(['CSV=a,b'])` bound
                    // `CSV=a` while Jenkins exposes `a,b`. Match the QUOTED list
                    // elements instead, so commas inside an element are content.
                    let bindings =
                        step.Positional
                        |> List.collect (fun raw ->
                            [ for m in Text.RegularExpressions.Regex.Matches(raw, "'([^']*)'|\"([^\"]*)\"") ->
                                if m.Groups[1].Success then m.Groups[1].Value else m.Groups[2].Value ]
                            |> List.choose (fun entry ->
                                match entry.IndexOf '=' with
                                | i when i > 0 -> Some(entry.Substring(0, i), entry.Substring(i + 1))
                                | _ -> None))

                    let inner = { ctx with EnvOverlay = ctx.EnvOverlay @ bindings }

                    for st in step.Block do
                        if not (halted inner) then
                            runStepDispatch inner cwd stage st deadline

                    if inner.Failed.Value then ctx.Failed.Value <- true

                | "dir", (sub :: _) ->
                    // `dir('x') { … }` — nested cwd, auto-created
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

            and runStageBody (ctx: BranchCtx) (cwd: string) (stage: Stage) =
                    for step in stage.Steps do
                        if not (halted ctx) then
                            runStepDispatch ctx cwd stage step None

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

                        let branches =
                            stage.Nested
                            |> List.map (fun branch ->
                                let branchCtx =
                                    { Failed = ref false
                                      Sink = bump
                                      EnvOverlay = ctx.EnvOverlay
                                      Interrupt =
                                        if failFast then
                                            Some(fun () -> siblingFailed.IsCancellationRequested)
                                        else
                                            ctx.Interrupt }

                                branchCtx,
                                System.Threading.Tasks.Task.Run(fun () ->
                                    runStage branchCtx cwd branch

                                    if branchCtx.Failed.Value then
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
                            runStage ctx cwd nested

            let root = { Interrupt = None; Failed = ref false; Sink = bump; EnvOverlay = [] }

            for stage in pipeline.Stages do
                if root.Failed.Value then
                    // Jenkins names every stage it skips because of an earlier
                    // failure. Being quieter than Jenkins about why a stage did not
                    // run is the JB-DUR-005 defect in miniature, so we say it too.
                    emit $"Stage \"{stage.Name}\" skipped due to earlier failure(s)"
                else
                    runStage root workspace stage

            // Pipeline-level `post` is selected against the BUILD result, so it
            // runs after every stage. Modelled as a synthetic stage carrying only
            // the post section, which is also how the pipeline's own environment
            // reaches those steps.
            if not (List.isEmpty pipeline.Post) then
                let synthetic =
                    { Name = ""
                      Agent = None
                      Environment = []
                      Steps = []
                      When = None
                      Post = pipeline.Post
                      Nested = []
                      IsParallel = false
                      FailFast = false
                      Position = { Line = 0L; Column = 0L } }

                runPost { root with Failed = ref false } workspace synthetic status None

            let workspaceHash, files = Trace.hashWorkspace workspace

            Result.Ok
                { Result = BuildStatus.toWireString status
                  Output = Trace.normaliseOutput output
                  WorkspaceHash = workspaceHash
                  WorkspaceFiles = files
                  Concurrent = pipeline.Stages |> Pipeline.flattenStages |> List.exists (fun st -> st.IsParallel)
                  ReportedFailureReason = Trace.reportedFailureReason output }
