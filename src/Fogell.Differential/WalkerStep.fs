namespace Fogell.Differential
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir
// FG-174: a step's return value is published typed, so `returnStatus` yields an Integer.
open Fogell.Groovy.Interpreter

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

        // FG-174. THE FLAGS ARE READ BEFORE THE STEP RUNS, not after it.
        //
        // They used to be read only after `Executor.runStep` returned, which made
        // `returnStdout` a post-hoc reinterpretation of a step that had already streamed
        // its output. The review that found it named the class exactly: an unsupported
        // option ADMITTED after a narrow refusal. `StepValueUse` refuses a VALUE USE, so
        // `def out = sh(returnStdout: true, …)` was rejected — but a STATEMENT-position
        // `sh script: 'printf value', returnStdout: true` is not a value use, so nothing
        // refused it and Fogell printed `value` where Jenkins prints only the xtrace.
        // A divergence admitted quietly, which is the shape of every finding on this
        // branch. Deciding capture BEFORE dispatch is what makes the option affect the
        // run instead of the report.
        // WHAT THIS CALL RETURNS comes from `WalkerRules.returnContract`, the same
        // function the static refusal reads. This end deciding for itself is what
        // produced two findings: the flags treated as universal, then as orthogonal.
        //
        // AND THE VALUE'S TYPE DECIDES, NOT ITS RENDERED TEXT. `returnStatus: true` and
        // `returnStatus: 'true'` both render to "true", and Jenkins treats them
        // differently — see `WalkerRules.returnFlag`. The type survives in two places
        // depending on how the step was reached: `ExpressionArgs` records a stage-level
        // argument written UNQUOTED, and inside a `script` block the typed `HostedArgs`
        // value is the argument itself.
        let writtenAsLiteralBoolean (key: string) =
            match ctx.HostedArgs with
            | Some(_, named) ->
                named
                |> List.exists (fun (k, v) ->
                    k = key
                    && match v with
                       | VBool _ -> true
                       | _ -> false)
            | None -> step.ExpressionArgs.Contains key

        let flagState (key: string) =
            renderedNamed
            |> List.tryPick (fun (k, v) -> if k = key then Some v else None)
            |> Option.map (fun v -> WalkerRules.returnFlag (writtenAsLiteralBoolean key) v)

        // A REJECTED FLAG STOPS THE STEP, and stops it BEFORE it runs. Jenkins refuses
        // `returnStatus: 1` at instantiation with an empty workspace, so a Fogell that
        // ran the shell and then complained would already have done the work Jenkins
        // never started. Only the shell steps are checked: on any other step these are
        // unknown parameters that Jenkins ignores with a warning, and refusing them here
        // would be stricter than Jenkins for no gain.
        let flagRejection =
            if not (WalkerRules.stepsHonouringReturnFlags.Contains step.Name) then
                None
            else
                [ "returnStdout"; "returnStatus" ]
                |> List.tryPick (fun key ->
                    match flagState key with
                    | Some(WalkerRules.FlagRejected why) -> Some $"step '{step.Name}' argument `{key}` {why}"
                    | _ -> None)

        let flagged (key: string) = flagState key = Some WalkerRules.FlagOn

        let contract =
            WalkerRules.returnContract step.Name (flagged "returnStdout") (flagged "returnStatus")

        // CAPTURE KEYS ON THE REQUEST, NOT ON WHO WINS. durable-task calls
        // `captureOutput()` because `returnStdout` was ASKED FOR, so with both flags set
        // the output is still captured and the STATUS is what comes back. Receipt:
        // script-sh-return-both.
        let wantsStdout =
            WalkerRules.stepsHonouringReturnFlags.Contains step.Name && flagged "returnStdout"

        let wantsStatus = contract = WalkerRules.ExitStatus

        let result =
            match flagRejection with
            // REFUSED WITHOUT RUNNING. `Executor.runStep` is not reached, so no shell
            // starts and no file is written — which is the observable part: Jenkins
            // leaves the workspace EMPTY here, so an engine that ran the shell first and
            // complained afterwards would still differ on the workspace hash.
            | Some why -> Executor.refusedBeforeRunning why
            | None ->

            Executor.runStep
                { Name = step.Name
                  Script = script
                  Workspace = cwd
                  WorkspaceRoot = Some workspace
                  Environment = envForWith ctx.EnvOverlay stage
                  // FG-174. `returnStdout` CAPTURES instead of printing — Jenkins' console
                  // shows the xtrace and not the program's output, because durable-task
                  // calls `captureOutput()`. `returnStatus` does NOT capture, so it is
                  // deliberately not part of this condition.
                  CaptureStdout = wantsStdout
                  // FG-196. An undeclared deadline is UNBOUNDED — the oracle's
                  // default. A 120 s constant sat here and aborted any step
                  // outliving two minutes, invisible to every case that
                  // finishes in seconds. MEASURED: receipt `undeclared-deadline-unbounded`
                  // sleeps past the old constant and both engines succeed. None is
                  // not fail-open: interrupt and failFast still arrive through
                  // Interrupt, and the executor waits interrupt-only.
                  TimeoutMs = WalkerCancellation.remainingMs runCtx deadline
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
        // FG-174. `returnStdout` / `returnStatus` semantics, measured against pinned
        // Jenkins 2.568.1:
        //   returnStdout -> stdout VERBATIM, trailing newline INCLUDED. `printf 'value\n'`
        //     through `od -c` gives `[ v a l u e \n ]`, which is why pipelines call
        //     `.trim()`. Stripping it "helpfully" would silently change every script that
        //     already does. It also CAPTURES: the console shows the xtrace and not the
        //     output. Receipt: script-sh-returnstdout.
        //   returnStatus -> the exit code, and the build DOES NOT FAIL. Getting this wrong
        //     turns a deliberate status check into a failed build; getting it wrong the
        //     other way hides a real failure. Receipt: script-sh-returnstatus.
        //
        // The flags themselves are read ABOVE, before dispatch — see the note there for
        // why reading them here was the defect rather than the style.

        // THE FLAG ALONE DOES NOT LICENSE SUPPRESSION — the step must actually have
        // produced an exit status. `returnStatus` converts a SHELL EXIT into a value
        // instead of a failure; it says nothing about a WRAPPER INTERRUPTION, which is
        // not the script's answer to anything.
        //
        // Found by the pre-push verifier, which ran it:
        //   timeout(time: 1, unit: 'SECONDS') { sh script: 'sleep 5', returnStatus: true }
        //   sh 'echo after > after.txt'
        // reported SUCCESS and wrote `after.txt`. A safety bound defeated, which ranks
        // with a bypassed approval here — and the same test as `script-timeout-bound`,
        // which passed only because its `sh` carried no flag.
        //
        // `ExitCode.IsSome` IS the test, not a proxy for one: `Executor` sets it from
        // `Completed code` and `Signalled` — the two ways a shell reports its own
        // status — and leaves it None for `TimedOut` and `Cancelled`, which are the
        // engine stopping the step from outside. A non-shell step never has one either,
        // so `error(message: 'x', returnStatus: true)` cannot borrow the suppression.
        // Deriving it from the status instead would need this to re-enumerate a mapping
        // that already exists, and drift from it the next time a case is added.
        //
        // `Signalled` is admitted deliberately: durable-task records 128+N in its
        // wrapper file, so Jenkins hands that back as the status. ANALYSIS, not
        // measured — the timeout arm below is what has a receipt.
        let statusAvailable = result.ExitCode.IsSome
        let statusIsTheAnswer = wantsStatus && statusAvailable

        // PUBLISHED STRAIGHT FROM THE CONTRACT, so precedence is decided once. The
        // status arm is FIRST because `returnContract` already resolved the combination —
        // testing `wantsStdout` first is what returned a String for
        // `sh(returnStdout: true, returnStatus: true)`, where Jenkins returns Integer 7.
        ctx.HostedResult
        |> Option.iter (fun slot ->
            match contract with
            // NO FABRICATED ZERO. `defaultArg result.ExitCode 0` handed an interrupted
            // step the value 0 — a killed step reporting success to the script, which is
            // the false-success shape ADR 0001 calls worse than an explicit rejection.
            // The build fails here anyway; publishing null keeps the two consistent
            // rather than relying on that.
            | WalkerRules.ExitStatus when statusAvailable -> slot.Value <- VInt(int64 result.ExitCode.Value)
            // THE RAW CAPTURE, not the masked `Stdout`. A pipeline that captures a
            // credential must receive the credential; masking belongs to what PRINTS.
            // Measured: Jenkins returns a 12-character token where this returned `****`.
            // The fallback is the masked text and is unreachable for a capturing shell
            // step — it exists so a future non-shell producer cannot silently publish
            // nothing.
            | WalkerRules.CapturedStdout ->
                slot.Value <- VStr(defaultArg result.CapturedStdoutRaw result.Stdout)
            | _ -> slot.Value <- VNull)

        result.EngineNote
        |> Option.iter (fun n -> runCtx.NoteEngine $"step '{step.Name}': {n}")

        result.DurableId |> Option.iter runCtx.AddDurableId

        // Jenkins prints its failure reason INTO the build log. Parity
        // requires the same: a diagnostic the user cannot see is not a
        // diagnostic (JB-DUR-005 — Jenkins' own worst behaviour is an
        // opaque `exit code -1`, and we promised to be clearer, not quieter).
        // `returnStatus` ASKED for the code, so a non-zero exit is the ANSWER rather than a
        // failure — Jenkins runs the following steps and reports success. It asked about a
        // SHELL EXIT though, so an abort with no exit code propagates: see
        // `statusIsTheAnswer` above and receipt `script-returnstatus-timeout`.
        if result.Status <> BuildStatus.Success && not statusIsTheAnswer then
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
                result.Diagnostic
                |> Option.iter (fun d ->
                    // FG-114: the reason exists HERE as a string; the console copy
                    // is consumed to a boolean downstream, so the durable record
                    // captures now
                    ctx.LastDiagnostic.Value <- Some d
                    runCtx.Emit $"ERROR: {d}")

            // Only a FAILED or ABORTED step halts the branch. An
            // unstable one does not: `junit` marks the build unstable and
            // returns normally, so Jenkins runs the following steps. It
            // also means `retry` does not re-run an unstable body —
            // retry catches exceptions, and unstable throws none.
            if result.Status <> BuildStatus.Unstable then
                ctx.Failed.Value <- true

            if not interruptedBySibling then ctx.Sink result.Status
