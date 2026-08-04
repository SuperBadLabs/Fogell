namespace Fogell.Differential

open System
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir

/// FG-036. What one parallel branch (or the single implicit branch of a
/// sequential pipeline) needs to know about itself.
/// FG-101. Why a step stopped. `Running` means it did not.
type Cancellation =
    | Running
    | DeadlineExpired
    | SiblingFailed

/// A live deadline: WHEN it fires, and WHICH declaration it is. The token exists
/// because expiry ownership is announced per declaring scope, and two scopes can
/// declare the same absolute millisecond — while nudging the VALUE to
/// disambiguate (the previous attempt) shifted real execution. The min-chain
/// keeps the winning record whole, so the token that arrives at a cancellation
/// site is by construction the scope whose bound actually fired.
type Deadline = { AtMs: int64; Token: int }

/// FG-052. The SCM an SCM-defined job carries (CpsScmFlowDefinition): what
/// `checkout scm` checks out, and where the harness pushed the Jenkinsfile.
type ScmSpec = { Url: string; Branch: string }

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
      EnvOverlay: (string * string) list
      /// FG-046b. The (stage, top-level step index) this branch is currently
      /// executing — the key durability records are written under. Carried on
      /// the BRANCH, not in run-scoped state, because parallel branches execute
      /// different keys at the same instant and one mutable would hand a branch
      /// its sibling's key. None where nothing is journaled: the differential
      /// path, and `post` (whose steps this design does not record — see
      /// PersistenceHooks' stated limit), so an `input` there has no durable
      /// approval and behaves exactly as it did before this ticket.
      DurabilityKey: (string * int) option
      /// FG-046b. A human REJECTED an `input` in this scope. Distinct from the
      /// Aborted status, and it has to be: MEASURED on 2.568.1, a nested
      /// `timeout` expiring inside `retry(3)` is RETRIED — three attempts, two
      /// `Retrying` lines, ABORTED at the end (receipt
      /// `retry-timeout-retries`). Both interruptions produce Aborted, so a
      /// retry rule written against the STATUS cannot tell them apart, and the
      /// first version of it stopped retrying timeouts too. A rejection is the
      /// one interruption that must not be re-attempted: asking someone who
      /// declined until they agree is not a retry policy.
      HumanRejected: bool ref }

/// FG-105. The walker's decision rules, extracted from the `run` closure so
/// each is reviewable and unit-testable in isolation. Contract: nothing in
/// this module touches RUN-scoped walker state — no emit, no status, no
/// clocks, no deadline registry — and nothing here has side effects. `halted`
/// is the one non-pure READER: it polls the branch's own signals (the Failed
/// ref and the interrupt predicate handed to it in BranchCtx) and decides
/// nothing beyond them. A function needing more does not belong here.
module WalkerRules =
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
        elif ms >= 1000L && ms < 10_000L && ms % 1000L <> 0L then
            // measured: tenths, TRUNCATED, from one second — 1999 ms is
            // "1.9 sec"; exact seconds print plain ("3 sec")
            $"{ms / 1000L}.{(ms % 1000L) / 100L} sec"
        elif ms >= 100L && ms < 1000L then
            // measured: HUNDREDTHS below one second, trailing zero dropped —
            // 150 ms is "0.15 sec", 500 ms "0.5 sec"
            let h = ms / 10L
            if h % 10L = 0L then $"0.{h / 10L} sec" else $"0.{h} sec"
        elif ms < 100L then $"{ms} ms"
        else $"{sec} sec"

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

    /// The count a `retry` carries, or None when it is missing or not an integer.
    ///
    /// FG-053(b). Separate from [retryCount] because a STAGE OPTION must be able to
    /// REFUSE a malformed count: `options { retry('nope') }` is refused by Jenkins
    /// at compile time (`Expecting "int" but got "nope"`), where falling back to a
    /// default ran the stage and reported SUCCESS — an invalid Jenkinsfile
    /// performing side effects. The step spelling keeps its default; only the
    /// option validates, because that is where the measurement is.
    let retryCountOpt (step: Step) : int option =
        step.Named
        |> List.tryPick (fun (k, v) -> if k = "count" then Some v else None)
        |> Option.orElse (List.tryHead step.Positional)
        |> Option.bind (fun v -> match Int32.TryParse(v.Trim()) with
                                 | true, n when n > 0 -> Some n
                                 | _ -> None)

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
    let alwaysFailFast (pipeline: Pipeline) =
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
    /// Receipts: `post-order-failure`, `post-order-success` (build-#1 arms) and,
    /// since FG-110 gave the harness build history, the `post-history` sequence —
    /// `post-history.b2` exercises the `fixed` slot, `post-history.b4` the
    /// `regression` slot, and `post-history.b3` proves `changed` stays QUIET on a
    /// same-result build. The four-build probe's table is receipt-backed in full.
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
