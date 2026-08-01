namespace Fogell.Differential

open System
open Fogell.Domain
open Fogell.Ir

/// FG-105. The walker's cancellation and deadline model, extracted whole: ONE
/// predicate, ONE classification, ONE application site — the FG-101 invariant —
/// now in a unit a review can hold at once. Contract: everything here routes
/// its state through WalkerCtx (clock, fired-set, emit) and its per-branch
/// signals through BranchCtx; nothing else is touched.
module WalkerCancellation =

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
    let cancellationOf (runCtx: WalkerCtx) (ctx: BranchCtx) (deadline: Deadline option) : Cancellation =
        let expiredNow =
            match deadline with
            | Some d -> runCtx.RunClock.ElapsedMilliseconds >= d.AtMs
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
        let deadlineAt = deadline |> Option.map (fun d -> d.AtMs) |> Option.defaultValue Int64.MaxValue
        let siblingObserved = siblingNow || siblingAt >= 0L

        if not (expiredNow || siblingObserved) then Cancellation.Running
        elif siblingObserved && siblingAt >= 0L && siblingAt < deadlineAt then Cancellation.SiblingFailed
        elif expiredNow then Cancellation.DeadlineExpired
        else Cancellation.SiblingFailed

    /// Emit the reason, mark the branch failed, and sink the status the CAUSE
    /// dictates. Every cancellable step routes through this so the classification
    /// cannot drift per-step again.
    let applyCancellation (runCtx: WalkerCtx) (ctx: BranchCtx) (what: string) (deadline: Deadline option) (c: Cancellation) =
        match c with
        | Cancellation.Running -> ()
        | Cancellation.SiblingFailed ->
            runCtx.Emit $"ERROR: {what} interrupted: a failFast sibling failed"
            ctx.Failed.Value <- true
        | Cancellation.DeadlineExpired ->
            runCtx.RecordFired deadline
            // Jenkins narrates the cancellation BEFORE the step's own abort
            // line. Shell steps get this line from ProcessGroup at SIGTERM
            // time; this is the path for steps with no process — input,
            // stash, deleteDir waits (FG-102, measured wording).
            runCtx.Emit "Cancelling nested steps due to timeout"
            runCtx.Emit $"ERROR: {what} aborted: the step's deadline expired"
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
    let remainingMs (runCtx: WalkerCtx) (deadline: Deadline option) =
        deadline |> Option.map (fun d -> max 1L (d.AtMs - runCtx.RunClock.ElapsedMilliseconds))

    /// FG-045. `options { timeout(...) }` at pipeline or stage level. MEASURED:
    /// Jenkins ABORTS the build when it expires — `finished.txt` and the following
    /// stage never appear. Fogell ignored options entirely, so such a pipeline ran
    /// UNBOUNDED and reported success; the 60-second sleep in the probe completed.
    ///
    /// A nested deadline can only tighten an inherited one, never extend it.
    /// Receipts: `options-timeout-pipeline` AND `options-timeout-stage` — the claim
    /// covers BOTH levels, so citing only the pipeline one left the stage half of
    /// the sentence unbacked.
    let deadlineFromOptions (runCtx: WalkerCtx) (options: Step list) (inherited: Deadline option) =
        // REVIEW FIX (Codex, PR #16): an unparseable time or unsupported unit
        // turned into None here, so the declared SAFETY BOUND silently vanished and
        // the job ran unbounded — while the step form fails closed on the very same
        // error. Errors are surfaced so the caller can stop the build.
        let declared, optionError =
            options
            |> List.filter (fun o -> o.Name = "timeout")
            |> List.fold
                (fun (acc, err) o ->
                    match WalkerRules.timeoutMs o with
                    | Ok ms -> (Some(runCtx.MkDeadline (runCtx.RunClock.ElapsedMilliseconds + ms), ms), err)
                    | Error e -> (acc, Some e))
                (None, None)

        // FG-102, measured: an `options { timeout }` announces its budget in
        // the same words the step form uses, at the point it takes effect.
        // Formatted from the PARSED duration — reconstructing it from the
        // absolute deadline raced the stopwatch, and a 4-second timeout that
        // lost 1 ms in flight announced "3 sec": a load-dependent divergence
        // now that these sentences are compared.
        declared
        |> Option.iter (fun (_, ms) -> runCtx.Emit ("Timeout set to expire in " + WalkerRules.humanizeSpan ms))

        let effective =
            match declared, inherited with
            | Some(d, _), Some i -> Some(min d i)
            | Some(d, _), None -> Some d
            | None, i -> i

        effective, (declared |> Option.map fst), optionError
