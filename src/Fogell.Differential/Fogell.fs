namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir

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
      Sink: BuildStatus -> unit }

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
            let envFor (stage: Stage) =
                (pipeline.Environment @ stage.Environment)
                |> List.fold (fun acc (k, v) -> Map.add k v acc) Map.empty
                |> Map.toList

            let mutable status = BuildStatus.Success
            let statusLock = obj ()
            let bump (s: BuildStatus) = lock statusLock (fun () -> status <- BuildStatus.worstOf status s)

            let runStepWithRef = ref (fun (_: BranchCtx) (_: Stage) (_: string) (_: Step) (_: int option) -> ())
            let runStepWith ctx stage cwd step budget = runStepWithRef.Value ctx stage cwd step budget

            let runStepInner (ctx: BranchCtx) (stage: Stage) (cwd: string) (step: Step) (budget: int option option) =
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
                          Environment = envFor stage
                          TimeoutMs = defaultArg budget (Some 120_000)
                          OnLine = Some emit
                          Interrupt = ctx.Interrupt
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

                value
                |> Option.map (fun n ->
                    match unit with
                    | "SECONDS" -> n * 1_000
                    | "MILLISECONDS" -> n
                    | "HOURS" -> n * 3_600_000
                    | _ -> n * 60_000)

            let retryCount (step: Step) =
                step.Named
                |> List.tryPick (fun (k, v) -> if k = "count" then Some v else None)
                |> Option.orElse (List.tryHead step.Positional)
                |> Option.bind (fun v -> match Int32.TryParse(v.Trim()) with
                                         | true, n -> Some n
                                         | _ -> None)
                |> Option.defaultValue 1

            runStepWithRef.Value <- fun ctx stage cwd step budget ->
                runStepInner ctx stage cwd step (budget |> Option.map Some)

            let runStep ctx (stage: Stage) (cwd: string) (step: Step) = runStepInner ctx stage cwd step None

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

            let rec runStage (ctx: BranchCtx) (cwd: string) (stage: Stage) =
                if not (halted ctx) then
                    for step in stage.Steps do
                        if not (halted ctx) then
                            match step.Name, step.Positional with
                            // JB-FAIL-001/002: timeout and abort share one
                            // interrupt path, and the interrupt is a trappable
                            // SIGTERM with a grace window.
                            | "timeout", _ when not (List.isEmpty step.Block) ->
                                let budget = timeoutMs step

                                for inner in step.Block do
                                    if not (halted ctx) then
                                        runStepWith ctx stage cwd inner budget

                            // JB-FAIL-005: retry(N) is N TOTAL attempts, not N
                            // retries after the first, and there is no backoff.
                            // `retry(3)` around a step that always fails runs it
                            // exactly three times.
                            | "retry", _ when not (List.isEmpty step.Block) ->
                                let attempts = max 1 (retryCount step)
                                let mutable attempt = 1
                                let mutable settled = false

                                while not settled && attempt <= attempts do
                                    // Each attempt gets a fresh failure flag AND a
                                    // throwaway status sink. MEASURED (FG-035): a
                                    // body that fails once then succeeds is a
                                    // SUCCESS build on Jenkins. Bumping the build
                                    // status from the failed attempt reported
                                    // `failure` with an identical workspace — the
                                    // work was right, the bookkeeping was not.
                                    let attemptStatus = ref BuildStatus.Success

                                    let attemptCtx =
                                        { ctx with
                                            Failed = ref false
                                            Sink = fun s -> attemptStatus.Value <- BuildStatus.worstOf attemptStatus.Value s }

                                    for inner in step.Block do
                                        if not (halted attemptCtx) then
                                            runStepWith attemptCtx stage cwd inner None

                                    if not attemptCtx.Failed.Value then
                                        // A retried body may still have gone
                                        // unstable; that is not a retryable
                                        // failure, so it must reach the build.
                                        ctx.Sink attemptStatus.Value
                                        settled <- true
                                    elif attempt < attempts then
                                        // Jenkins prints this between attempts, with
                                        // no delay: retry does not back off.
                                        emit "Retrying"
                                        attempt <- attempt + 1
                                    else
                                        // Final attempt failed: now it is the
                                        // build's failure.
                                        ctx.Sink attemptStatus.Value
                                        ctx.Failed.Value <- true
                                        attempt <- attempt + 1

                            | "dir", (sub :: _) ->
                                // `dir('x') { … }` — nested cwd, auto-created
                                match Workspace.resolveUnder cwd sub with
                                | Result.Error e ->
                                    emit $"dir refused: {e.Describe}"
                                    ctx.Failed.Value <- true
                                    bump BuildStatus.Failure
                                | Result.Ok target ->
                                    Directory.CreateDirectory target |> ignore
                                    for inner in step.Block do
                                        runStep ctx stage target inner
                            | _ -> runStep ctx stage cwd step

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
                        let anyFailed = ref false

                        let branches =
                            stage.Nested
                            |> List.map (fun branch ->
                                let branchCtx =
                                    { Failed = ref false
                                      Sink = bump
                                      Interrupt =
                                        if failFast then
                                            Some(fun () -> anyFailed.Value)
                                        else
                                            ctx.Interrupt }

                                branchCtx,
                                System.Threading.Tasks.Task.Run(fun () ->
                                    runStage branchCtx cwd branch

                                    if branchCtx.Failed.Value then
                                        anyFailed.Value <- true))

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

            let root = { Interrupt = None; Failed = ref false; Sink = bump }

            for stage in pipeline.Stages do
                runStage root workspace stage

            let workspaceHash, files = Trace.hashWorkspace workspace

            Result.Ok
                { Result = BuildStatus.toWireString status
                  Output = Trace.normaliseOutput output
                  WorkspaceHash = workspaceHash
                  WorkspaceFiles = files
                  Concurrent = pipeline.Stages |> Pipeline.flattenStages |> List.exists (fun st -> st.IsParallel)
                  ReportedFailureReason = Trace.reportedFailureReason output }
