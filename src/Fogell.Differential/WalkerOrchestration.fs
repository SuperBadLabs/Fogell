namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir

/// FG-112. Durability hooks for a persisted run. The unit of durability is the
/// TOP-LEVEL step of a stage: a wrapper (retry/timeout/withEnv) journals as one
/// unit, so resume skips or re-runs it whole — coarse-grained exactly-once,
/// stated rather than implied. None (the differential path) journals nothing.
///
/// STATED LIMIT: `post` steps are NOT journaled and re-run on every resume —
/// at-least-once for post effects. Closing that needs post-scoped keys and a
/// re-selection story (the arms select against a status resume must
/// reconstruct); it is FG-046b/FG-082 territory, not silently claimed here.
type PersistenceHooks =
    { /// True when this attempt RESUMES an interrupted journal — what
      /// `when { isRestartedRun() }` evaluates to.
      IsRestartedRun: bool
      /// stage -> stepIndex -> run it? False = durably finished in a prior
      /// attempt: the step is skipped SILENTLY (its output already happened;
      /// replaying narration would double it).
      ShouldExecute: string -> int -> bool
      /// stage -> stepIndex -> the status a durably finished step RECORDED, so
      /// the skip path can replay it — without this, a resume after
      /// `step-finished failure` (but before BuildFinished) flips the build to
      /// success and runs stages the failure should have halted.
      SkippedStatus: string -> int -> BuildStatus option
      /// stage -> stepIndex -> stepName. Written (and made durable) BEFORE the
      /// step runs.
      OnStepStarted: string -> int -> string -> unit
      /// stage -> stepIndex -> the step's worst sunk status.
      OnStepFinished: string -> int -> BuildStatus -> unit
      /// The stage boundary — the journal's group-commit point.
      OnStageCommitted: string -> unit }

/// FG-105. What the orchestration cluster needs from the run, stated as data.
/// A new dependency is a new field — visible in review — not a new capture.
type OrchestrationDeps =
    { RunCtx: WalkerCtx
      EnvForWith: (string * string) list -> Stage -> (string * string) list
      RunStepInner: BranchCtx -> Stage -> string -> Step -> Deadline option -> unit
      EvalWhen: Stage -> WhenCondition -> bool option
      AlwaysFailFast: bool
      WorkspaceRoot: string
      ArtifactRoot: string
      JobName: string
      Credentials: unit -> Map<string, Credential>
      /// FG-110. The PREVIOUS build's terminal result, when the harness kept the
      /// job across builds — what `changed`/`fixed`/`regression` select against.
      /// None on a first build (and for every single-build case), exactly as
      /// measured: `changed` FIRES on build #1, `fixed`/`regression` cannot.
      PreviousBuild: BuildStatus option
      /// FG-110. This build's number within its job — 1 for every single-build
      /// case. Scopes per-BUILD state (stashes) so a sequence cannot read a
      /// prior build's leftovers where Jenkins would say the stash is missing.
      BuildNumber: int
      /// FG-052. The job's SCM, when the job is SCM-defined — what
      /// `checkout scm` checks out. None for inline-script jobs, where
      /// `checkout scm` REFUSES exactly as Jenkins errors there.
      Scm: ScmSpec option
      /// FG-112. Durability hooks; None journals nothing (the differential path).
      Persistence: PersistenceHooks option }

/// FG-105. Stage/post orchestration and wrapper/block dispatch — the walker's
/// recursive core, moved WHOLE so the mutual recursion (stage -> steps ->
/// wrapper bodies -> nested stages -> post) stays in one reviewable unit.
/// Contract: run-scoped state through WalkerCtx, decisions through
/// WalkerRules/WalkerCancellation/WalkerWhen, step execution through
/// WalkerStep. The bindings below are the complete list of what the cluster
/// used to CAPTURE from run() — that is the boundary this record makes
/// reviewable. The cluster ALSO calls module-level services directly, exactly
/// as it did inside the closure: Fogell.Execution (Credentials, Secrets,
/// Stash, Workspace, Executor), GString, filesystem IO for dir/deleteDir/
/// stash bookkeeping, and the process environment for PATH augmentation.
module WalkerOrchestration =

    /// Returns (runStage, runPostWithDeadline) — the two entry points run()
    /// drives: one per top-level stage, one for the pipeline-level post.
    let makeRunners (deps: OrchestrationDeps) =
        let runCtx = deps.RunCtx
        let emit = runCtx.Emit
        let runClock = runCtx.RunClock
        let mkDeadline = runCtx.MkDeadline
        let deadlineDidFire = runCtx.DeadlineDidFire
        let scriptBinding = runCtx.ScriptBinding
        let envForWith = deps.EnvForWith
        let runStepInner = deps.RunStepInner
        let evalWhen = deps.EvalWhen
        let alwaysFailFast = deps.AlwaysFailFast
        let workspaceRoot = deps.WorkspaceRoot
        let artifactRoot = deps.ArtifactRoot
        let jobName = deps.JobName
        let credentialStore = deps.Credentials
        let previousBuild = deps.PreviousBuild
        let persistence = deps.Persistence
        // Jenkins scopes a stash to the BUILD that saved it — receipt
        // `stash-not-carried`: build 2's unstash of build 1's stash FAILS.
        let stashKey = $"{deps.JobName}#build-{deps.BuildNumber}"
        let humanizeSpan = WalkerRules.humanizeSpan
        let timeoutMs = WalkerRules.timeoutMs
        let retryCount = WalkerRules.retryCount
        let halted = WalkerRules.halted
        let postFires = WalkerRules.postFires
        let postRank = WalkerRules.postRank
        let cancellationOf = WalkerCancellation.cancellationOf runCtx
        let applyCancellation = WalkerCancellation.applyCancellation runCtx
        let remainingMs = WalkerCancellation.remainingMs runCtx
        let deadlineFromOptions = WalkerCancellation.deadlineFromOptions runCtx
        let renderStepArgs = WalkerArgs.renderStepArgs runCtx envForWith
        let warnSecretInterpolation = WalkerArgs.warnSecretInterpolation runCtx
        let adviseNewBinding = WalkerArgs.adviseNewBinding runCtx

        let rec runPostWithDeadline
            (ctx: BranchCtx)
            (cwd: string)
            (stage: Stage)
            (result: BuildStatus)
            (previous: BuildStatus option)
            (deadline: Deadline option)
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

        and runStage (ctx: BranchCtx) (cwd: string) (inherited: Deadline option) (stage: Stage) =
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

                    // FG-112: a restart-sensitive gate (isRestartedRun and kin)
                    // can evaluate DIFFERENTLY on the resumed attempt — but a
                    // status this stage (or ANY nested child — parallel branch,
                    // sequential group) durably RECORDED already happened, and
                    // when-skipping the parent must not skip the consequence.
                    match persistence with
                    | Some hooks ->
                        let mutable entered = false
                        let mutable replayed = BuildStatus.Success

                        for st in Pipeline.flattenStages [ stage ] do
                            st.Steps
                            |> List.iteri (fun i _ ->
                                match hooks.SkippedStatus st.Name i with
                                | Some recorded ->
                                    entered <- true
                                    replayed <- BuildStatus.worstOf replayed recorded
                                    ctx.Sink recorded

                                    if
                                        recorded = BuildStatus.Failure
                                        || recorded = BuildStatus.Aborted
                                    then
                                        ctx.Failed.Value <- true
                                | None -> ())

                        // A stage that PREVIOUSLY RAN but is gated off on this
                        // attempt still owes its `post`: the controller may have
                        // died before or during it, and post is not journaled
                        // (the stated at-least-once limit) — running it again is
                        // that semantics, silently dropping it is data loss.
                        if entered && not (List.isEmpty stage.Post) then
                            let postCtx = { ctx with Failed = ref false }
                            runPostWithDeadline postCtx cwd stage replayed previousBuild inherited
                            if postCtx.Failed.Value then ctx.Failed.Value <- true
                    | None -> ()

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

                // Stage post is selected against the STAGE's result; `previous`
                // is the prior build's terminal result when the FG-110 sequence
                // lane kept the job (None on a first build and for every
                // single-build case). `fixed`/`regression` are receipt-proven by
                // the `post-history` sequence (FG-049b closed).
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
                runPostWithDeadline postCtx cwd stage stageStatus.Value previousBuild inherited
                if postCtx.Failed.Value then ctx.Failed.Value <- true

                // MEASURED position (`cancellation-selects-post-arm`): the
                // stage-declared expiry announces AFTER the post arm the abort
                // selected — Jenkins prints `+ echo right` first, then the
                // sentence. Own declared deadline only: the effective bound can
                // be an inherited outer one, whose OWNER announces it.
                if body.Failed.Value && deadlineDidFire stageDeclaredDeadline then
                    emit "Timeout has been exceeded"

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
        and runStepDispatch (ctx: BranchCtx) (cwd: string) (stage: Stage) (step: Step) (deadline: Deadline option) =
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

        and runStepDispatchBody (ctx: BranchCtx) (cwd: string) (stage: Stage) (step: Step) (deadline: Deadline option) =
            // REVIEW FIX (Codex, PR #13 round 4): a deadline only ever became a
            // `TimeoutMs` for the shell runner. `echo`, `junit`,
            // `archiveArtifacts` and wrapper dispatch enforce nothing, so a
            // `timeout` block full of those could keep running long after Jenkins
            // would have aborted it. The deadline is now checked BEFORE every
            // dispatch, whatever the step is.
            let expired =
                match deadline with
                | Some d -> runClock.ElapsedMilliseconds >= d.AtMs
                | None -> false

            if expired then
                // FG-102, through the ONE cancellation model: an expiry observed
                // between steps still races a failFast sibling, and classifying it
                // here by clock alone called a sibling's failure a timeout —
                // cancellationOf owns that ordering decision.
                match cancellationOf ctx deadline with
                | Cancellation.Running ->
                    // the deadline passed between the check and classification's
                    // reread — treat as expired, the plain reading
                    applyCancellation ctx $"step '{step.Name}'" deadline Cancellation.DeadlineExpired
                | c -> applyCancellation ctx $"step '{step.Name}'" deadline c
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
                    let mine = mkDeadline (runClock.ElapsedMilliseconds + ms)

                    let effective =
                        match deadline with
                        | Some outer -> if outer.AtMs <= mine.AtMs then outer else mine
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
                    runCtx.BindSecrets bindings

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
                    let mutable outcome = Cancellation.Running

                    for entry in Directory.GetFileSystemEntries cwd do
                        if outcome = Cancellation.Running then
                            match cancellationOf ctx deadline with
                            | Cancellation.Running ->
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
                    let mutable outcome = Cancellation.Running

                    while outcome = Cancellation.Running do
                        match cancellationOf ctx deadline with
                        | Cancellation.Running ->
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

            // FG-052. `checkout scm` — the bare `scm` positional is a binding
            // OBJECT, never rendered (rendering would raise unknown-name). The
            // job's SCM comes from OrchestrationDeps; an inline-script job has
            // none and Jenkins errors there too. Explicit checkout([...]) maps
            // (8 corpus files) are not modelled yet — refused by name.
            | "checkout", [ "scm" ] when step.Named.IsEmpty ->
                match deps.Scm with
                | Some spec ->
                    // an EXPLICIT `checkout scm` does not re-wrap later stages
                    // in GIT_* env (only the Declarative auto-checkout does) —
                    // the returned sha is deliberately dropped
                    WalkerGit.runCheckout
                        runCtx
                        ctx
                        cwd
                        deadline
                        (envForWith ctx.EnvOverlay stage)
                        artifactRoot
                        jobName
                        deps.BuildNumber
                        spec
                    |> ignore
                | None ->
                    emit "ERROR: checkout scm is only available when the pipeline came from SCM"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure

            | "checkout", _ ->
                emit "ERROR: checkout with an explicit SCM configuration is not modelled (only `checkout scm`)"
                ctx.Failed.Value <- true
                ctx.Sink BuildStatus.Failure

            // FG-111/FG-052. The `git` step — a real clone/fetch plus the git
            // plugin's measured narration, in WalkerGit. An absent `branch`
            // defaults to `master`: MEASURED (receipt `git-step-default-branch` —
            // Jenkins rev-parses refs/remotes/origin/master and re-branches as
            // master), the form 13 of the 228 corpus files use.
            | "git", _ ->
                let step = renderStepArgs ctx stage step

                let url =
                    step.Positional
                    |> List.tryHead
                    |> Option.orElse (step.Named |> List.tryPick (fun (k, v) -> if k = "url" then Some v else None))

                let branch =
                    step.Named
                    |> List.tryPick (fun (k, v) -> if k = "branch" then Some v else None)
                    |> Option.defaultValue "master"

                // A credentialsId this engine cannot honour must REFUSE, not
                // silently clone unauthenticated while narrating "No credentials
                // specified" — wrong twice (FG-103: name the unknown).
                let credentialsId =
                    step.Named |> List.tryPick (fun (k, v) -> if k = "credentialsId" then Some v else None)

                match url, credentialsId with
                | None, _ ->
                    emit "ERROR: git step requires a url"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                | _, Some c ->
                    emit $"ERROR: git step credentialsId '{c}' is not modelled (the lane has no credentialed remote to measure against)"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                | Some u, None ->
                    WalkerGit.runStep
                        runCtx
                        ctx
                        cwd
                        deadline
                        (envForWith ctx.EnvOverlay stage)
                        artifactRoot
                        jobName
                        deps.BuildNumber
                        u
                        branch

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
                    let abort () = cancellationOf ctx deadline <> Cancellation.Running

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

                    let saved, aborted = Stash.save store stashKey cwd n includes excludes abort

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
                    let abort () = cancellationOf ctx deadline <> Cancellation.Running

                    match Stash.restore store stashKey cwd n abort with
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

        and runStageBody (ctx: BranchCtx) (cwd: string) (deadline: Deadline option) (stage: Stage) =
                stage.Steps
                |> List.iteri (fun i step ->
                    if not (halted ctx) then
                        match persistence with
                        | None -> runStepDispatch ctx cwd stage step deadline
                        | Some hooks ->
                            if not (hooks.ShouldExecute stage.Name i) then
                                // replay the recorded outcome: skipping the
                                // EXECUTION must not skip the CONSEQUENCE
                                match hooks.SkippedStatus stage.Name i with
                                | Some st ->
                                    ctx.Sink st

                                    if st = BuildStatus.Failure || st = BuildStatus.Aborted then
                                        ctx.Failed.Value <- true
                                | None -> ()
                            else
                                hooks.OnStepStarted stage.Name i step.Name

                                // observe THIS step's worst sunk status without
                                // disturbing the branch's own sink
                                let observed = ref BuildStatus.Success

                                let observing =
                                    { ctx with
                                        Sink =
                                            fun st ->
                                                observed.Value <- BuildStatus.worstOf observed.Value st
                                                ctx.Sink st }

                                runStepDispatch observing cwd stage step deadline
                                hooks.OnStepFinished stage.Name i observed.Value)



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

                // the group-commit boundary — AFTER nested/parallel content, so
                // "everything before it is durable" is actually true of it, and
                // UNCONDITIONAL: this is a durability point, not a success
                // signal — a halted stage's finished records still need their
                // fsync under EveryStage policy
                match persistence with
                | Some hooks -> hooks.OnStageCommitted stage.Name
                | None -> ()

        runStage, runPostWithDeadline
