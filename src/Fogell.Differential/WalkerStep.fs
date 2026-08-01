namespace Fogell.Differential
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir

/// FG-105. ONE step's execution: render its arguments, hand it to the
/// executor with the branch's deadline/interrupt wiring, then classify and
/// narrate the outcome through the one cancellation model. Contract: this is
/// the ONLY place a Step becomes an Executor.runStep call, and every
/// ABORT without a ProcessGroup snapshot is classified through
/// WalkerCancellation — a cancellation cause decided anywhere else is the
/// drift FG-101 closed. Plain Failure and Unstable sink directly: they are
/// step RESULTS, not cancellations, and the measured rules (no ERROR line for
/// unstable, unstable does not halt the branch) live here.
module WalkerStep =

    let runStepInner
            (runCtx: WalkerCtx)
            (envForWith: (string * string) list -> Stage -> (string * string) list)
            (workspace: string)
            (artifactRoot: string)
            (jobName: string)
            (ctx: BranchCtx)
            (stage: Stage)
            (cwd: string)
            (step: Step)
            (deadline: Deadline option)
            =
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
        let rendered = WalkerArgs.renderStepArgs runCtx envForWith ctx stage step

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
                  WorkspaceRoot = Some workspace
                  Environment = envForWith ctx.EnvOverlay stage
                  TimeoutMs =
                    match WalkerCancellation.remainingMs runCtx deadline with
                    | Some ms -> Some ms
                    | None -> Some 120_000L
                  OnLine = Some runCtx.Emit
                  // External cancellation only — a failFast sibling. The
                  // deadline reaches the shell runner through TimeoutMs and
                  // self-working steps through DeadlineExpired, so an expired
                  // timeout is still reported as a timeout.
                  Interrupt = ctx.Interrupt
                  // ties inside one poll break on the WALKER's timestamps:
                  // the sibling stamp against this step's effective deadline
                  InterruptBeatsDeadline =
                    Some(fun () ->
                        let s = ctx.SiblingFailedAt.Value

                        s >= 0L
                        && (match deadline with
                            | Some d -> s < d.AtMs
                            | None -> true))
                  DeadlineExpired =
                    deadline |> Option.map (fun d -> fun () -> runCtx.RunClock.ElapsedMilliseconds >= d.AtMs)
                  Secrets = ctx.Secrets
                  Named = renderedNamed
                  Artifacts = Some(ArtifactStore.under artifactRoot)
                  BuildKey = jobName }

        // Output arrives exactly once, via OnLine. An earlier version also
        // appended result.Stdout, so every shell line was emitted twice and
        // the differential reported a phantom divergence at line 1.
        //
        // stderr STREAMS through OnLine exactly as stdout does — the
        // comment here claimed otherwise and the loop it justified emitted
        // every stderr line a SECOND time. Comment-as-specification drift,
        // FG-104's own class, found when xtrace moved to stderr and doubled.

        // Engine-health findings reach the receipt WHATEVER the step's
        // status: on success nothing else would print them, and on failure
        // the composed ERROR line is normalised away — either way the
        // receipt stayed silent until this carried them separately (FG-103).
        result.EngineNote
        |> Option.iter (fun n -> runCtx.NoteEngine $"step '{step.Name}': {n}")

        result.DurableId |> Option.iter runCtx.AddDurableId

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
            // ONE snapshot: ProcessGroup decided the cause when the wait
            // ended, and both its narration and this classification read that
            // decision — deriving it again from timestamps let the two
            // disagree within a poll interval (Cancelling emitted, sibling
            // classified). Non-shell steps carry no snapshot and keep the
            // timestamp model.
            let interruptedBySibling =
                result.Status = BuildStatus.Aborted
                && (match result.AbortedBySibling with
                    | Some bySibling -> bySibling
                    | None -> WalkerCancellation.cancellationOf runCtx ctx deadline = Cancellation.SiblingFailed)

            if result.Status = BuildStatus.Aborted && not interruptedBySibling then
                runCtx.RecordFired deadline

                // A SELF-WORKING step (archiveArtifacts, junit) never enters
                // ProcessGroup, so nobody has narrated the cancellation for
                // it — shell steps got the line at SIGTERM time, and emitting
                // it here for them too would double it.
                if step.Name = "archiveArtifacts" || step.Name = "junit" then
                    runCtx.Emit "Cancelling nested steps due to timeout"

            // Jenkins prints `ERROR: …` for a FAILED step. It does not for
            // an unstable one — `junit` marks the build unstable without
            // an ERROR line, so emitting one there is a false divergence.
            // An ABORTED step is narrated too (Jenkins: "Sending interrupt
            // signal to process / Terminated"), and silence on an abort is
            // the JB-DUR-005 defect we promised to beat.
            if result.Status = BuildStatus.Failure || result.Status = BuildStatus.Aborted then
                result.Diagnostic |> Option.iter (fun d -> runCtx.Emit $"ERROR: {d}")

            // Only a FAILED or ABORTED step halts the branch. An
            // unstable one does not: `junit` marks the build unstable and
            // returns normally, so Jenkins runs the following steps. It
            // also means `retry` does not re-run an unstable body —
            // retry catches exceptions, and unstable throws none.
            if result.Status <> BuildStatus.Unstable then
                ctx.Failed.Value <- true

            if not interruptedBySibling then ctx.Sink result.Status
