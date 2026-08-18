namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir
// FG-160: `script { }` bodies are scripted Groovy, evaluated by the interpreter.
open Fogell.Groovy.Interpreter

/// FG-046b. A human's answer to an `input` prompt. Deliberately NOT a bool: the
/// two outcomes narrate and terminate differently (MEASURED on Jenkins 2.568.1 —
/// an approval prints NOTHING and the build carries on; a rejection prints
/// `Rejected` and the build ends ABORTED), and a bool at this boundary is how a
/// caller ends up defaulting one of them. UNPROVEN BY RECEIPT — measured by
/// `scripts/probe-input.bb`, asserted by `scripts/run-approval-lane.sh`.
type InputAnswer =
    | InputApproved of submitter: string
    | InputRejected of submitter: string

/// FG-112. Durability hooks for a persisted run. The unit of durability is the
/// TOP-LEVEL step of a stage: a wrapper (retry/timeout/withEnv) journals as one
/// unit, so resume skips or re-runs it whole — coarse-grained exactly-once,
/// stated rather than implied. None (the differential path) journals nothing.
///
/// STATED LIMIT: build-scoped Groovy BINDINGS assigned by a durably finished
/// step are NOT restored on resume. Jenkins serialises its whole CPS
/// continuation and keeps them (receipt `gstring-binding-across-steps`); this
/// journal records step OUTCOMES, not interpreter state, so a resumed attempt
/// starts with a fresh ScriptBinding. A later step referencing such a variable
/// therefore FAILS BY NAME (strict-vars raises MissingProperty) rather than
/// resolving to something else — fail-visible, never silently different.
/// Closing it means durably serialising interpreter values, which is its own
/// ticket; the host warns on every resume so the operator is not surprised.
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
      /// stage -> did a prior attempt durably commit this stage's boundary?
      /// A container stage (parallel/sequential group) has no direct steps, so
      /// this is its only evidence of having run.
      StageWasCommitted: string -> bool
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
      OnStageCommitted: string -> unit
      /// FG-114. stage -> stepIndex -> the ERROR-shaped diagnostic a FAILED (or
      /// aborted) step emitted — the REASON, made durable beside the status,
      /// because `failure` alone sent every reader to a console that no longer
      /// says why. Called after OnStepFinished, only when a reason was captured.
      OnStepReason: string -> int -> string -> unit
      /// FG-135. stage -> retry attempt N (>= 2) is starting. Journaled (and made
      /// durable) BEFORE the attempt's first step, so a resume can tell a failed
      /// prior attempt's records from the live attempt's. Implementations also
      /// switch the stage to LIVE: once a marker this process wrote supersedes
      /// the plan, ShouldExecute/SkippedStatus must stop consulting it.
      OnRetryAttempt: string -> int -> unit
      /// FG-135. stage -> the attempt the journal already shows started (1 when
      /// none). A resumed retried stage CONTINUES from this attempt — the plan's
      /// step dispositions describe exactly that attempt and no other.
      RetryAttemptsSoFar: string -> int
      /// FG-046b. stage -> stepIndex -> occurrence -> prompt -> the answer, if
      /// one has been given. Polled while an `input` waits, and expected to be
      /// CHEAP: it is called on every poll of the wait loop.
      ///
      /// The OCCURRENCE is part of the identity, not decoration: the durability
      /// key names a top-level step, so every prompt inside one wrapper shares
      /// it, and an implementation that caches by key alone hands the first
      /// human's answer to the next gate untouched.
      ///
      /// `None` — the whole field — means THERE IS NO APPROVER, and an un-timed
      /// prompt then fails closed with FG-046's named refusal rather than
      /// waiting for a human who cannot arrive. That distinction has to live in
      /// the type: inferring "journaled, therefore answerable" made a host
      /// started without an inbox hang silently and forever on a prompt nobody
      /// could see (the host buffers console output until the build ends), which
      /// is precisely the silent outcome FG-046 refuses.
      ///
      /// When present, the implementor owns two things the walker deliberately
      /// does not know about: where an answer comes from (a file, an API) and
      /// making it DURABLE before returning it. The walker acts on the answer
      /// the instant it sees one, so an answer returned but not yet recorded is
      /// an answer that can be lost — the one failure this ticket exists to
      /// prevent. Returning None from the FUNCTION forever is legitimate: it
      /// means nobody has answered yet, and the prompt waits exactly as Jenkins'
      /// does (MEASURED: a pending input survives a controller restart with the
      /// same action id and is still approvable afterwards).
      ///
      /// UNPROVEN BY RECEIPT, and unprovable by one: a receipt is a differential
      /// against real Jenkins, and the harness has no approver on EITHER side —
      /// an answered prompt cannot be driven in both engines from a Jenkinsfile
      /// alone. The measurement is `scripts/probe-input.bb` against
      /// the pinned lab (recorded in ADR 0005); the ENGINE side is proven by
      /// `scripts/run-approval-lane.sh`, which runs in the gate.
      /// The `cancellable` flag says this prompt can be STOPPED while it waits —
      /// a deadline, or a failFast sibling. Its answer must never become
      /// actionable beyond the attempt that read it: eligibility depends on time,
      /// making an answer actionable is a durable write, and time passes during
      /// the write, so no ordering of check and write closes the gap. A prompt
      /// that cannot be cancelled has no such question and its answer is
      /// actionable the moment it is durable.
      PollInputAnswer: (string -> int -> int -> bool -> string -> InputAnswer option) option
      /// FG-046b. stage -> stepIndex -> occurrence -> this prompt has stopped
      /// waiting WITHOUT an answer (a deadline, a failFast sibling, a faulted
      /// approver), so withdraw it from wherever it was advertised.
      ///
      /// Called at the moment the prompt dies rather than at the end of the
      /// build, because `retry { timeout { input … } }` starts the NEXT
      /// occurrence immediately: the inbox otherwise showed the expired prompt
      /// and the live one side by side, and an operator answering the expired id
      /// — the natural one to reach for, it was there first — had their decision
      /// silently discarded while the real gate went on waiting.
      OnInputClosed: string -> int -> int -> unit
      /// FG-046b. stage -> stepIndex -> occurrence -> an answer was read but
      /// REFUSED because the prompt had already been cancelled when it arrived.
      /// Must be made DURABLE before the caller acts on the cancellation: an
      /// unmarked late answer stays on record, and a crash before the abort is
      /// recorded lets a resumed attempt — with a fresh deadline — honour it and
      /// walk through the gate the timeout had already closed.
      OnInputAnswerVoided: string -> int -> int -> unit
      }

/// FG-105. What the orchestration cluster needs from the run, stated as data.
/// A new dependency is a new field — visible in review — not a new capture.
type OrchestrationDeps =
    { RunCtx: WalkerCtx
      EnvForWith: (string * string) list -> Stage -> (string * string) list
      RunStepInner: BranchCtx -> Stage -> string -> Step -> Deadline option -> unit
      EvalWhen: Stage -> WhenCondition -> bool option
      AlwaysFailFast: bool
      /// FG-053(b). `options { skipStagesAfterUnstable() }`. Needed HERE and
      /// not only in the pipeline's own stage loop: nested sequential stages
      /// run through `runStage` from inside this module, and enforcing the
      /// policy only at top level let a nested sibling run after its
      /// predecessor went unstable. MEASURED on Jenkins 2.568.1 — it skips
      /// the nested sibling AND the following top-level stage, with the same
      /// sentence for both.
      /// Receipt: `options-skip-after-unstable-nested` (and
      /// `options-skip-after-unstable` for the top-level spelling).
      SkipStagesAfterUnstable: bool
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
        let skipStagesAfterUnstable = deps.SkipStagesAfterUnstable
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

        // FG-160. The step names a `script { }` body may call. DERIVED FROM THE DISPATCH
        // TABLE below plus the two the inner runner handles, deliberately CLOSED: a name
        // outside it is refused by `Sandbox.admitCall`, the script faults, and the build
        // fails with a reason. That is the fail-closed direction — an open vocabulary
        // would let an unimplemented name reach the walker's dispatch and silently do
        // nothing.
        // Defined in `WalkerRules` so a test can hold the arity table against it; this
        // local binding keeps every call site below unchanged. `dir`, `timeout`, `retry`
        // and `withEnv` are in it because FG-172 taught their arms to run a hosted body.
        let scriptStepVocabulary = WalkerRules.scriptStepVocabulary
        /// FG-172. The SIGNATURE a hosted step must be called with, checked centrally
        /// before dispatch.
        ///
        /// WHY CENTRAL AND NOT PER-ARM. The first attempt validated `withEnv` inside its
        /// own arm and only when the first argument was a list — so `withEnv('A=1')`, the
        /// WRONG SHAPE entirely, fell past it: no list, no raw entries, empty bindings, and
        /// the body RAN while Jenkins rejects the call outright (`EnvStep(List<String>)`).
        /// Every newly admitted hosted step would otherwise get its own signature bypass,
        /// one per arm, discovered one review round at a time. Raised by the pre-push
        /// verifier as the eighth of its class, with exactly that argument.
        ///
        /// A step absent from this table is not validated here — its arm is responsible,
        /// as before. Adding a hosted step SHOULD add a line here.
        /// FG-177. ONE PLACE THAT DECIDES WHAT SHAPE A HOSTED CALL IS IN.
        ///
        /// Two things, in this order, because the order is the whole subtlety:
        ///   1. the CPS rule — positional AND named together is rejected by Jenkins for
        ///      EVERY step, whatever its parameters;
        ///   2. NORMALISATION — a sole required parameter written by name becomes the
        ///      positional form, so no arm and no dispatch path has to learn both
        ///      spellings. `dir(path: 'sub')` is `dir('sub')`.
        ///
        /// Doing (2) before (1) would be wrong in a way worth naming: normalising
        /// `sh(script: 'x', returnStatus: true)` moves `script` to the positional slot and
        /// leaves `returnStatus` named, MANUFACTURING the mixed shape that (1) rejects —
        /// and every existing `sh(script:…, returnStatus:…)` receipt would start failing.
        /// So the rule reads the call as WRITTEN, and only then is it rewritten.
        ///
        /// The result feeds the `Step` record, the signature arms AND `HostedArgs`, so a
        /// wrapper reading its typed argument sees the normalised form too — which is
        /// where `withEnv(overrides: […])` was actually failing, not just in validation.
        let normaliseHostedCall (name: string) (positional: Value list) (named: (string * Value) list) =
            if not (List.isEmpty positional) && not (List.isEmpty named) then
                let shown = positional |> List.map Value.toDisplay |> String.concat ", "

                Error
                    $"`{name}` takes positional arguments OR named ones, not both; Jenkins rejects `[{shown}]` with 'Expected named arguments'"
            else
                match positional, Map.tryFind name WalkerRules.soleRequiredParameter with
                | [], Some param ->
                    match named |> List.partition (fun (k, _) -> k = param) with
                    | [ (_, v) ], rest -> Ok([ v ], rest)
                    | _ -> Ok(positional, named)
                | _ -> Ok(positional, named)

        let hostedSignatureError (name: string) (positional: Value list) (named: (string * Value) list) =
            let wrongShape expected = Some $"`{name}` {expected}"

            // The CPS mixed-argument rule used to live here; it moved into
            // `normaliseHostedCall` below, because normalisation has to happen AFTER it
            // and both belong to the same "what shape is this call" question.
            // POSITIONAL **OR** NAMED, NEVER BOTH — and this is not a per-step rule, which
            // is why it sits ahead of the match rather than in an arm.
            //
            // Jenkins' CPS `DSL.parseArgs` throws `Expected named arguments but got …`
            // whenever a named map arrives beside positional arguments, whatever the step
            // is. MEASURED on TWO different steps to establish that it is step-INDEPENDENT
            // rather than assuming it from one, and held by receipt
            // `script-mixed-positional-named`: `sh('exit 7', returnStatus: true)` and
            // `archiveArtifacts('*.txt', fingerprint: true)` both make Jenkins fail with
            // the SAME workspace hash — only the earlier stage's file — while Fogell ran
            // each and reported success.
            //
            // It was first written as a `timeout`-only arm, which was the fifteenth
            // finding of this class: the right rule in the wrong scope. `timeout`'s own
            // mixed branch is GONE rather than left beside this one, because a dead
            // branch that can never fire reads as a second opinion.
            match name with
            | "withEnv" ->
                match positional, named with
                | [ VList items ], [] ->
                    items
                    |> List.map Value.toDisplay
                    |> List.tryFind (fun e -> e.IndexOf '=' <= 0)
                    |> Option.map (fun bad ->
                        $"withEnv entry {bad} is not NAME=VALUE; Jenkins rejects an override without '='")
                | _ -> wrongShape "takes exactly one list argument of NAME=VALUE strings"
            | "dir" ->
                match positional, named with
                | [ _ ], [] -> None
                | _ -> wrongShape "takes exactly one path argument"
            | "retry" ->
                // THE TYPE, not just the arity — and NOT the sign. MEASURED against pinned
                // Jenkins rather than assumed, because the review that raised this said
                // Jenkins "rejects the count" for both shapes and that is only half true:
                //   retry(0)       -> Jenkins SUCCEEDS and runs the body once (clamped).
                //                     Receipt: script-retry-zero.receipt.txt.
                //   retry('nope')  -> Jenkins FAILS, IllegalArgumentException from RetryStep.
                //                     UNPROVEN in-repo: that probe diverges on Jenkins'
                //                     Java stack trace, so it cannot be a suite case.
                // A first fix refused non-positive counts too and made Fogell STRICTER than
                // Jenkins, which is a false refusal — the opposite error, still a
                // divergence. Probed both before settling on this.
                //
                // Negative counts are UNTESTED and treated like 0; `runWithRetry` clamps to
                // one attempt, which is what Jenkins does with 0.
                //
                // NAMED `count:` IS VALID JENKINS, and refusing it was a FALSE REFUSAL —
                // the opposite error to everything else in this function, and the one
                // `retry(0)` already taught. MEASURED: `script { retry(count: 2) { … } }`
                // succeeds on Jenkins and failed here, with the ordinary stage-level
                // reader (`WalkerRules.retryCountOpt`) already accepting the same spelling.
                // An arm stricter than the reader it guards is a refusal with no rule
                // behind it. Raised in review on PR #53.
                let countArg =
                    match positional, named |> List.tryFind (fun (k, _) -> k = "count") with
                    | [ v ], None -> Some v
                    | [], Some(_, v) -> Some v
                    // Both spellings at once is not a shape Jenkins takes either.
                    | _ -> None

                match countArg with
                | None -> wrongShape "takes one attempt count, either positionally or as `count:`"
                | Some(VInt _) ->
                    // `conditions:` is real Jenkins and NOT implemented here. Refusing it
                    // by name beats accepting it and retrying unconditionally, which would
                    // retry on failures the pipeline asked to leave alone.
                    match named |> List.filter (fun (k, _) -> k <> "count") with
                    | [] -> None
                    | (k, _) :: _ -> wrongShape $"does not support `{k}`; only the attempt count is implemented"
                | Some other -> wrongShape $"needs an integer attempt count, not `{Value.toDisplay other}`"
            | "timeout" ->
                // MEASURED on the pinned lab, not inferred from the other arms, and held
                // by receipt `script-timeout-two-positionals`: Jenkins accepts ONE
                // positional (minutes) or named arguments, and `timeout(1, 2)` raises
                // `IllegalArgumentException: Expected named arguments but got [1, 2]`. Fogell read the FIRST positional as one
                // minute, silently dropped the `2`, ran the body and reported SUCCESS.
                //
                // The probe also decided WHERE this belongs: Jenkins ran the earlier
                // STAGE before failing, so this is a RUNTIME signature rejection and not
                // a compile-time one like a duplicated named argument. Refusing at
                // dispatch is therefore right here, where it was wrong there.
                //
                // ARITY ONLY. `timeoutMs` already reads `time`/`unit` and a bare
                // positional, and the TYPE of a single argument is left alone
                // deliberately — refusing a shape Jenkins accepts is the false-refusal
                // error `retry(0)` taught, and nothing here has been measured.
                // EITHER FORM, NEVER BOTH. Measured on the pinned lab, and the mixed
                // shape had to be probed separately — an arity-only check passed
                // `timeout(1, unit: 'SECONDS')`, `timeoutMs` then combined the positional
                // duration with the named unit, and the body RAN where Jenkins rejects
                // the call and leaves the workspace empty. Raised in review on PR #53.
                match positional, named with
                | _ :: _ :: _, _ ->
                    let shown = positional |> List.map Value.toDisplay |> String.concat ", "
                    wrongShape $"takes one positional argument or named ones; Jenkins rejects `[{shown}]` with 'Expected named arguments'"
                | _ -> None
            | _ ->
                // DEFAULT DENY ON ARITY, rather than a thirteenth hand-written arm.
                //
                // Reaching here means the step has no arm of its own — `sh`, `echo`,
                // `git` and the rest. That used to be a silent pass, so
                // `script { sh('echo ran > ran.txt', 'ignored') }` ran the FIRST
                // positional and dropped the second, while Jenkins rejects the call and
                // leaves the workspace EMPTY. Measured. Two more arms would have closed
                // `sh` and `script` and left the next step open, which is the trend
                // FG-177 exists to end.
                //
                // ONE positional is the Jenkins shape: DescribableModel maps a single
                // positional onto the sole required parameter, and named arguments
                // otherwise. CHECKED AGAINST THE CLOSED VOCABULARY rather than assumed —
                // `sh`, `echo`, `archiveArtifacts`, `junit`, `checkout`, `git`, `stash`,
                // `unstable`, `unstash`, `deleteDir` — none of which takes two.
                //
                // WHAT THIS STILL DOES NOT CATCH, said plainly because the last thing
                // this class needs is another comment claiming more than it delivers:
                // it is an ARITY rule only, so no unknown NAMED argument is rejected
                // anywhere. That needs the per-step schema in FG-177.
                //
                // THIS PASSAGE ALSO NAMED `deleteDir('x')` AS STILL ADMITTED WITH AN
                // ARGUMENT, and that stopped being true in the very change described
                // below it: `positionalArity` records `deleteDir` at ZERO, and receipt
                // `script-deletedir-argument` holds the refusal. A caveat that outlives
                // its defect is the same drift as a claim outrunning its evidence, and
                // this one sat inside a paragraph warning against precisely that, which
                // is how it survived several reads.
                // PER-STEP, from `WalkerRules.positionalArity` — FG-177's first slice.
                // A blanket "zero or one" could not express a ZERO-argument step, and
                // `deleteDir('ignored')` duly passed it: the arm ignored the argument,
                // Fogell DELETED THE WORKSPACE and continued, where Jenkins keeps the
                // files and fails. Measured. A default cannot distinguish a step with a
                // sole required parameter from one with none, so the answer is data.
                let allowed = Map.tryFind name WalkerRules.positionalArity |> Option.defaultValue 1

                if List.length positional > allowed then
                    let shown = positional |> List.map Value.toDisplay |> String.concat ", "

                    if allowed = 0 then
                        wrongShape $"takes no positional arguments; Jenkins rejects `[{shown}]`"
                    else
                        wrongShape $"takes at most {allowed} positional argument; Jenkins rejects `[{shown}]`"
                else
                    None

        /// FG-172. Block-taking steps whose walker arm can run a HOSTED body. Defined in
        /// `WalkerRules` so a test can hold it against the set of wrappers that actually
        /// have a signature case — see the note there.
        let scriptWrappersWithHostedBody = WalkerRules.scriptWrappersWithHostedBody

        /// FG-160. Steps DELIBERATELY absent from the vocabulary above, with the reason —
        /// the sandbox's generic denial says a name was not admissible, not why THIS one
        /// never will be.
        ///
        /// `input`: DURABLE APPROVAL REPLAY is keyed on the top-level step (`Resume.fs`),
        /// and a nested hosted step has no durable identity of its own. REPRODUCED by
        /// the pre-push verifier — journal `step-started Gate 0 script` plus an
        /// `input-decision … approved alice`, rewind before `step-finished`, and the
        /// resume exits 3 with `needs-reconciliation`, LOSING AN APPROVAL A HUMAN ALREADY
        /// GAVE. That is the guarantee FG-046b exists to protect and the one no receipt
        /// can cover, so it fails closed until nested effects carry journal identity.
        let scriptStepsRefusedWithReason =
            Map.ofList
                [ yield
                      "input",
                      "durable approval replay is keyed on the top-level step, so an approval given inside a script block would be lost on resume (FG-046b); use `input` as a stage step instead"

                  // EVERY BLOCK-TAKING STEP, BOTH SPELLINGS. `findWrapperCalls` refuses
                  // these WITH a body because the replay cannot carry one — but the
                  // BODY-LESS spelling stayed admitted, and `dir("child")` with no body
                  // CREATED THE DIRECTORY and exited 0 where Jenkins fails with "dir step
                  // must be called with a body". I refused one half of a shape and left
                  // the other running: the same defect as scoping a fix to the level in
                  // front of me, which this ticket has now done three times. Raised by the
                  // pre-push verifier, which named it the SECOND of its class — a script
                  // shape Jenkins rejects being accepted as success.
                  //
                  // THE RULE AS IT STANDS after FG-172, replacing an earlier note that
                  // still said every block-taking step was absent: a wrapper is admitted
                  // ONLY when its walker arm accepts a `HostedBody`. `dir`, `timeout` and
                  // `retry` and `withEnv` do, each with a differential case — `withEnv`
                  // through TYPED arguments on `BranchCtx.HostedArgs`, because rendering its
                  // list to display text produced `[A=1]` and bound nothing.
                  //
                  // `withCredentials` does NOT, and not for the marshalling reason this
                  // note used to give: its argument is a DSL OF NESTED CALLS
                  // (`string(credentialsId: …)`), which the interpreter would try to
                  // EVALUATE — those names are neither steps nor `Sandbox.builtins`, so the
                  // script faults at the inner call before marshalling arises. Fail-closed,
                  // and a bigger piece than typed args.
                  //
                  // A body-less `dir` is refused by the `dir` ARM before it creates the
                  // directory, not by this list: the rule is Jenkins', and `dir('x')` alone
                  // is just as wrong at stage level.
                  for w in [ "withCredentials" ] do
                      yield
                          w,
                          $"`{w}` takes a block, and its walker arm cannot yet run one from a script, so the body would be silently dropped and its wrapper semantics lost; Jenkins also rejects the body-less spelling. Use it as a stage step around the `script` block instead" ]
        let postFires = WalkerRules.postFires
        let postRank = WalkerRules.postRank
        let cancellationOf = WalkerCancellation.cancellationOf runCtx
        let applyCancellation = WalkerCancellation.applyCancellation runCtx
        let remainingMs = WalkerCancellation.remainingMs runCtx
        let deadlineFromOptions = WalkerCancellation.deadlineFromOptions runCtx
        let renderStepArgs = WalkerArgs.renderStepArgs runCtx envForWith
        let warnSecretInterpolation = WalkerArgs.warnSecretInterpolation runCtx
        let adviseNewBinding = WalkerArgs.adviseNewBinding runCtx

        // FG-053(b). The retry LOOP, shared by the `retry(N) { }` STEP and by a
        // stage's `options { retry(N) }`. Extracted rather than copied: this
        // session has spent several rounds on the same wrong idea living in two
        // places, and every measured subtlety below — N total attempts with no
        // backoff, a fresh failure flag per attempt, a human REJECTION that must
        // not be re-asked, a nested rejection propagated to an enclosing retry —
        // would otherwise have to be reproduced correctly a second time.
        // Receipts: `retry-succeeds`, `retry-timeout-retries`.
        let runWithRetry
            (ctx: BranchCtx)
            (attempts: int)
            (startAttempt: int)
            (onAttempt: int -> unit)
            (runBody: BranchCtx -> unit)
            =
            let attempts = max 1 attempts
            // FG-135. A RESUMED retried stage continues from the attempt the journal
            // shows started rather than replaying the loop from attempt 1: the
            // resume plan's dispositions describe the LATEST attempt only, and prior
            // attempts need no re-observation — the loop only ever advances past an
            // attempt that completed as a failure. Non-journaled callers pass 1.
            let startAttempt = max 1 startAttempt

            // FAIL CLOSED when the journal shows an attempt this run's declared
            // budget cannot hold: the loop below would simply never run, and a
            // stage that crashed mid-attempt-N would complete SUCCESS with its
            // recorded failure never replayed. Reachable only if the rendered
            // retry count shrinks across runs under an unchanged digest (an
            // env-derived count) — low odds, silent-success consequence, so it
            // refuses by name (the verifier's construction on this diff).
            if startAttempt > attempts then
                emit
                    $"ERROR: the journal shows retry attempt {startAttempt} already started, but this run's declared budget is {attempts} attempt(s); the counts disagree, so the stage fails closed rather than completing silently"

                ctx.Sink BuildStatus.Failure
                ctx.Failed.Value <- true

            let mutable attempt = startAttempt
            let mutable settled = false

            while not settled && attempt <= attempts do
                // Journal the attempt marker BEFORE its first step, and only for
                // attempts this run STARTS: the resumed attempt's own marker is
                // already on disk, and re-writing it would make the plan's
                // last-marker-wins fold read it as superseding the very records
                // it belongs to.
                if attempt > startAttempt then
                    onAttempt attempt
                // Each attempt gets a fresh failure flag AND a throwaway status
                // sink. MEASURED (FG-035): a body that fails once then succeeds is
                // a SUCCESS build. Bumping the build status from the failed attempt
                // reported `failure` with an identical workspace — the work was
                // right, the bookkeeping was not.
                // Receipt: `retry-succeeds` (and `options-stage-retry` for the
                // stage-option spelling of the same loop). The citation was on this
                // claim before the extraction and did not travel with it — the
                // audit caught the gap, which is the one thing a refactor most
                // easily loses.
                let attemptStatus = ref BuildStatus.Success

                // FG-114. Freshened THROUGH the shared ref, not replaced like
                // `Failed`: the dispatch loop journals whatever this ref holds at
                // the step's final finish, so a write here must stay visible
                // there. Without it, a superseded attempt's diagnostic (a shell
                // failure the retry absorbed) journals as the reason for a final
                // disposition it does not explain — e.g. a human rejection on the
                // last attempt. Only the final attempt's own capture may survive.
                ctx.LastDiagnostic.Value <- None

                let attemptCtx =
                    { ctx with
                        Failed = ref false
                        // fresh per attempt, like Failed: this attempt's own
                        // rejection, not one inherited from an earlier one
                        HumanRejected = ref false
                        Sink = fun st -> attemptStatus.Value <- BuildStatus.worstOf attemptStatus.Value st }

                // FG-186. A CATCHABLE fault escaping the body is an attempt failure —
                // Jenkins retries a body that throws, and a retry that cannot recover
                // from the faults it exists to absorb is not a retry. A refusal or an
                // exhausted budget re-raises untouched: catching Fogell's own
                // modelling gaps would diverge silently from an engine that has no
                // such gap. The fault is HELD so the FINAL attempt can re-raise it —
                // an uncaught fault must fail the build with its own diagnostic, not
                // a generic exhausted-retries one.
                let mutable attemptFault = None

                match Interpreter.catchRetryable (fun () -> runBody attemptCtx) with
                | Some f ->
                    attemptFault <- Some f
                    attemptStatus.Value <- BuildStatus.worstOf attemptStatus.Value BuildStatus.Failure
                    attemptCtx.Failed.Value <- true
                | None -> ()

                if attemptFault.IsSome && attempt >= attempts then
                    // final attempt: the fault continues out with its own identity
                    Interpreter.reraiseFault attemptFault.Value

                if not attemptCtx.Failed.Value then
                    // A retried body may still have gone unstable; that is not a
                    // retryable failure, so it must reach the build.
                    ctx.Sink attemptStatus.Value
                    settled <- true
                elif attemptCtx.HumanRejected.Value then
                    // FG-046b. A human who REJECTED a deployment gate was asked
                    // again by the next attempt, and an approval there could carry
                    // the whole build to success. The person said no; that is not a
                    // retryable failure.
                    //
                    // Tested on the REJECTION, not on the aborted status, and the
                    // difference is measured rather than reasoned: a nested
                    // `timeout` expiring also aborts the attempt, and Jenkins
                    // RETRIES that one — three attempts, two `Retrying` lines
                    // (receipt `retry-timeout-retries`). Reading the status stopped
                    // both, silently costing every `retry { timeout { … } }`
                    // pipeline its remaining attempts.
                    ctx.Sink attemptStatus.Value
                    ctx.Failed.Value <- true
                    // PROPAGATED, and this line is the whole nested case: the
                    // attempt's ref is fresh precisely so one attempt's rejection
                    // does not leak into the next, but that also severs it from an
                    // ENCLOSING retry. Without this, `retry(3) { retry(2) { input … } }`
                    // had the outer scope see an ordinary failed attempt, print
                    // `Retrying`, and put the prompt back in front of someone who
                    // had already declined it.
                    ctx.HumanRejected.Value <- true
                    settled <- true
                elif attempt < attempts then
                    // Jenkins prints this between attempts, with no delay: retry
                    // does not back off.
                    emit "Retrying"
                    attempt <- attempt + 1
                else
                    // Final attempt failed: now it is the build's failure.
                    ctx.Sink attemptStatus.Value
                    ctx.Failed.Value <- true
                    attempt <- attempt + 1

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
                        // PER STAGE, parent and every nested child alike: each
                        // that previously ran replays its OWN status and owes
                        // its OWN post (the controller may have died before or
                        // during it, and post is unjournaled — the stated
                        // at-least-once limit; dropping the arm is data loss).
                        // a container's post selects against the worst status
                        // its SUBTREE recorded — its own (zero) steps say nothing
                        let subtreeStatus (root: Stage) =
                            Pipeline.flattenStages [ root ]
                            |> List.collect (fun d -> d.Steps |> List.mapi (fun i _ -> d.Name, i))
                            |> List.choose (fun (n, i) -> hooks.SkippedStatus n i)
                            |> List.fold BuildStatus.worstOf BuildStatus.Success

                        // REVERSED: flattenStages is preorder, so replaying in
                        // its order ran a parent's post before its children's —
                        // Jenkins finishes the inner stage (and its post) first.
                        // Each stage's replayed outcome, keyed by name. A single
                        // accumulator leaked ACROSS SIBLINGS — a later sibling's
                        // failure reached an earlier sibling's status-selective
                        // post, which never ran under it. The children-first walk
                        // folds only a stage's OWN nested outcomes.
                        let outcomes = System.Collections.Generic.Dictionary<string, BuildStatus>()

                        for st in List.rev (Pipeline.flattenStages [ stage ]) do
                            let mutable entered = false
                            let mutable replayed = BuildStatus.Success

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

                            // A container stage has NO direct steps: its
                            // durable evidence of having run is its committed
                            // boundary, not a step record.
                            // A container's evidence of having run is its own
                            // commit OR any durable record in its subtree: a
                            // child can finish a step and the process die before
                            // the parent's StageCommitted is written.
                            let committed =
                                List.isEmpty st.Steps
                                && (hooks.StageWasCommitted st.Name
                                    || Pipeline.flattenStages [ st ]
                                       |> List.exists (fun d ->
                                           d.Steps |> List.mapi (fun i _ -> i) |> List.exists (fun i ->
                                               (hooks.SkippedStatus d.Name i).IsSome)))

                            let childrenWorst =
                                st.Nested
                                |> List.fold
                                    (fun acc (c: Stage) ->
                                        match outcomes.TryGetValue c.Name with
                                        | true, v -> BuildStatus.worstOf acc v
                                        | _ -> acc)
                                    BuildStatus.Success

                            let status =
                                BuildStatus.worstOf
                                    (if entered then replayed else subtreeStatus st)
                                    childrenWorst

                            let mutable outcome = status

                            if (entered || committed) && not (List.isEmpty st.Post) then
                                // observe what the POST itself sinks: `junit` can
                                // sink Unstable without failing, and a container's
                                // arm must see that too — the failure flag alone
                                // is not the post's outcome.
                                let postObserved = ref BuildStatus.Success

                                let postCtx =
                                    { ctx with
                                        Failed = ref false
                                        Sink =
                                            fun st ->
                                                postObserved.Value <- BuildStatus.worstOf postObserved.Value st
                                                ctx.Sink st }

                                runPostWithDeadline postCtx cwd st status previousBuild inherited
                                outcome <- BuildStatus.worstOf outcome postObserved.Value

                                if postCtx.Failed.Value then
                                    ctx.Failed.Value <- true
                                    outcome <- BuildStatus.worstOf outcome BuildStatus.Failure

                            outcomes[st.Name] <- outcome
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

                // FG-053(b). Stage-level `options { retry(N) }` re-runs THIS STAGE'S
                // STEPS, through the same `runWithRetry` the `retry(N) { }` step
                // uses. MEASURED on Jenkins 2.568.1 with a stage failing once then
                // succeeding: `Retrying` between attempts, later stages run, build
                // SUCCEEDS; and with a stage that always fails: N attempts, N-1
                // `Retrying` lines, later stages skipped, pipeline `post` still runs,
                // build fails. Workspace state PERSISTS across attempts — the probe
                // counts through a file that survives.
                //
                // Sharing the loop rather than copying it is deliberate: it carries
                // measured subtleties a second implementation would have to
                // rediscover, including that a human REJECTION is not retried while a
                // nested `timeout` abort IS.
                // Receipts: `options-stage-retry`, `options-stage-retry-exhausted`.
                // DURABILITY GAP, FG-135, and the guard below is what answers it.
                // Persisted steps are keyed by (stage, index) with no attempt
                // dimension, so WITHOUT that guard a crash after a FAILED attempt
                // would journal `step-finished` for the step and a resumed build
                // would skip it as durably finished — printing `Retrying` while
                // never re-running it — and an `input` before it would share the
                // identity (stage, index, occurrence) across attempts, letting an
                // earlier approval be reused.
                //
                // An earlier version of this comment said the degraded durable path
                // was "shipped knowingly". It is not: that judgement was reversed
                // when the approval consequence surfaced, and this paragraph
                // outlived it by one commit — describing behaviour the lines below
                // now prevent.
                // FG-135. Does any step of this stage — wrappers included — name
                // `input`? The journal's attempt dimension makes retry durable, but
                // an `input` inside a retried stage still shares its identity
                // `(stage, index, occurrence)` across attempts, and replaying a
                // human's approval is the guarantee FG-046b exists to hold — so that
                // one combination keeps the fail-closed refusal. `script { }` bodies
                // need no scan: `input` is out of the script vocabulary (FG-160c).
                let rec anyInput (steps: Step list) =
                    steps |> List.exists (fun s -> s.Name = "input" || anyInput s.Block)

                match stage.Options |> List.tryFind (fun o -> o.Name = "retry"), persistence with
                | Some _, Some _ when not (List.isEmpty stage.Nested) ->
                    // FG-135. JOURNALED RETRY IS FOR LEAF STAGES ONLY. Nested and
                    // parallel content runs INSIDE runStageBody — inside the retry
                    // loop — and its steps journal under the NESTED stages' names,
                    // which the parent's retry-attempt marker does not supersede: a
                    // resumed later attempt would read a failed earlier attempt's
                    // nested `step-finished` as durably finished, the exact defect
                    // this ticket closed one level down. The records alone cannot
                    // map a nested name to its retried parent, so this refuses by
                    // name rather than guessing.
                    emit
                        $"ERROR: stage \"{stage.Name}\" declares options {{ retry }} and holds nested or parallel stages, and this run is journaled; nested steps' durable records carry no attempt dimension, so a resume could replay a superseded attempt's outcomes — refusing rather than risking it"

                    body.Failed.Value <- true
                    body.Sink BuildStatus.Failure
                | Some _, Some _ when anyInput stage.Steps ->
                    // THE NARROWED FG-135 REFUSAL: attempts have durable identity
                    // now, so what remains refused is exactly the approval hazard,
                    // by name.
                    emit
                        $"ERROR: stage \"{stage.Name}\" declares options {{ retry }} and contains an input step, and this run is journaled; an approval's identity does not carry an attempt dimension, so a prior attempt's answer could satisfy a later prompt — refusing rather than replaying a human's decision"

                    // Through `body`, NOT `ctx`. `stageStatus` is accumulated by
                    // `body.Sink`, and stage `post` selects its arm from it — so
                    // sinking straight to `ctx` left `stageStatus` at Success and ran
                    // `post { success }` on a FAILED build. Both refusals added on
                    // this branch had it.
                    body.Failed.Value <- true
                    body.Sink BuildStatus.Failure
                | Some o, _ when
                    not (List.isEmpty stage.Post)
                    && (WalkerRules.retryCountOpt (renderStepArgs body stage o) |> Option.defaultValue 1) > 1
                    ->
                    // REFUSED, FG-137. A RUNTIME fail-closed refusal, NOT a
                    // compile-shaped one: this guard runs inside `runStage` when the
                    // stage is reached, so earlier stages have already produced side
                    // effects. An earlier version of this comment called it
                    // compile-shaped and cited FG-129 — I had just called that rule
                    // "mechanical" and applied it without checking its precondition.
                    //
                    // UNPROVEN BY RECEIPT for its own reason: this engine
                    // DELIBERATELY diverges here, refusing a pipeline Jenkins runs, so
                    // no case can be PROVEN against it by construction.
                    // Measured, not suspected: Jenkins runs
                    // a retried stage's `post` ONCE PER ATTEMPT. The probe traced
                    //   jenkins: + echo tick / Retrying / + echo tick   (two ticks)
                    //   fogell:  Retrying / + echo tick                 (one)
                    // with the workspace hashes differing on `postticks.txt`, because
                    // this wraps `runStageBody` alone while the stage `post` runs once
                    // after the loop. Post side effects are notifications and
                    // artifacts, so running them N-1 times too few is a real loss, not
                    // a cosmetic one.
                    //
                    // Fixing it means moving the post invocation inside the attempt —
                    // it currently sits after the loop with its own status and timeout
                    // handling — which is a restructure this refusal buys time for.
                    //
                    // ONLY when the count is >1. `retry(1)` is ONE total attempt, so
                    // post-per-attempt and post-once are the same thing and there is
                    // nothing to under-run. The first version refused every retry+post
                    // stage including that one — over-refusing a pipeline Jenkins runs
                    // correctly, which is the FG-126 trap in a new costume and one I
                    // introduced while trying to avoid a divergence.
                    emit
                        $"ERROR: stage \"{stage.Name}\" combines options {{ retry }} with a post block, which Jenkins runs once PER ATTEMPT and this engine runs once (FG-137) — refusing rather than under-running post side effects"

                    body.Failed.Value <- true
                    body.Sink BuildStatus.Failure
                // (Under persistence this arm is subsumed by the FG-135 leaf-only
                // guard above, which refuses ANY journaled retry+nested stage; it
                // still owns the non-journaled case.)
                | Some _, _ when skipStagesAfterUnstable && not (List.isEmpty stage.Nested) ->
                    // REFUSED COMBINATION, FG-136 — runtime fail-closed, like FG-137
                    // above and for the same reason: this runs when the stage is
                    // reached, not at compile time. UNPROVEN BY RECEIPT because the
                    // engine deliberately refuses a pipeline Jenkins runs.
                    // `runWithRetry` gives each attempt a
                    // THROWAWAY status sink and publishes the attempt's status only
                    // after the body returns, so while a retried stage is running
                    // `runCtx.Status()` is still Success. The nested skip below reads
                    // that global status — so a nested child going unstable inside a
                    // retried parent would NOT skip its sibling, silently diverging
                    // from the behaviour `options-skip-after-unstable-nested`
                    // measured.
                    //
                    // Neither receipt catches it: they cover nested skip and stage
                    // retry separately, and the composed shape has no case. Refusing
                    // rather than threading an attempt-local status accessor through
                    // `BranchCtx` on a guess — the fix wants its own measurement and
                    // a composed receipt, which is FG-136.
                    emit
                        $"ERROR: stage \"{stage.Name}\" combines options {{ retry }} with nested stages under skipStagesAfterUnstable, whose interaction this engine does not model yet (FG-136) — refusing rather than skipping the wrong stage"

                    // Through `body`, NOT `ctx`. `stageStatus` is accumulated by
                    // `body.Sink`, and stage `post` selects its arm from it — so
                    // sinking straight to `ctx` left `stageStatus` at Success and ran
                    // `post { success }` on a FAILED build. Both refusals added on
                    // this branch had it.
                    body.Failed.Value <- true
                    body.Sink BuildStatus.Failure
                | Some o, None ->
                    runWithRetry body (retryCount (renderStepArgs body stage o)) 1 ignore (fun attemptCtx ->
                        runStageBody attemptCtx cwd deadline stage)
                | Some o, Some hooks ->
                    // FG-135. THE DURABLE RETRY PATH. The resume plan's dispositions
                    // describe the latest attempt only (superseded ones are dropped
                    // at the marker), so the loop CONTINUES from the attempt the
                    // journal shows started, and each attempt this run begins is
                    // journaled before its first step. The hooks flip the stage to
                    // LIVE at the first marker they write, so a later attempt's
                    // steps stop consulting a plan that no longer describes them.
                    runWithRetry
                        body
                        (retryCount (renderStepArgs body stage o))
                        (hooks.RetryAttemptsSoFar stage.Name)
                        (fun n -> hooks.OnRetryAttempt stage.Name n)
                        (fun attemptCtx -> runStageBody attemptCtx cwd deadline stage)
                | None, _ -> runStageBody body cwd deadline stage

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
            // FG-053(b). `unstable('msg')` marks the build UNSTABLE and CONTINUES —
            // the stage's remaining steps and every later stage still run. MEASURED
            // on Jenkins 2.568.1: it prints `WARNING: msg`, the build ends
            // `unstable`, and with no `skipStagesAfterUnstable()` the following
            // stage runs normally.
            //
            // Implemented here because `skipStagesAfterUnstable` CANNOT BE REACHED
            // without it: this engine had no way to produce UNSTABLE from inside a
            // pipeline at all — line 838 of Fogell.fs parses a trace RESULT STRING,
            // and the existing `unstable { }` cases are POST CONDITIONS. Shipping
            // the option against an unreachable state would have been a branch that
            // looks implemented and cannot be exercised, which is the FG-129 shape.
            // Receipt: `options-unstable-runs-on`.
            | "unstable", _ ->
                let rendered = renderStepArgs ctx stage step

                // positional `unstable('msg')` or named `unstable(message: 'msg')`;
                // both are valid Groovy for a one-parameter step, and refusing the
                // named form is the mistake `ansiColor` already made once.
                // EXACT ARITY, like `retryCountOpt` and `ansiColorMap`. Taking the
                // first positional and ignoring the rest accepted `unstable('a','b')`
                // and `unstable(message: 'a', bogus: true)` — malformed Jenkinsfiles
                // running with arguments silently discarded. Fixed for `retry` on
                // this branch and not, until now, for the step beside it.
                let msg =
                    match rendered.Positional, rendered.Named with
                    | [ m ], [] -> m
                    | [], [ ("message", m) ] -> m
                    | _ -> ""

                // A BLANK message FAILS THE BUILD AT THIS STEP, it does not mark
                // unstable. UNPROVEN BY RECEIPT, and for its OWN reason rather than
                // FG-129's: result and workspace AGREE (both fail, both leave
                // `before.txt`), and the only divergence is Jenkins' Java stack trace
                // — `Caused: hudson.remoting.ProxyException ...` — which this engine
                // does not emit. Matching a plugin's exception layout is the
                // over-fitting the comparison contract already refuses for compiler
                // error text. Measured on Jenkins 2.568.1: `unstable(message: '')`
                // COMPILES — so earlier steps in the stage run — and then throws when
                // the step executes. That is a different shape from a MISSING
                // message, which Jenkins refuses at compile time before anything
                // runs; the two are handled in different places for that reason.
                if msg.Trim() = "" then
                    emit "ERROR: the unstable step requires a non-empty message"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                else
                    emit $"WARNING: {msg}"
                    ctx.Sink BuildStatus.Unstable

            // JB-FAIL-001/002: timeout and abort share one interrupt path,
            // and the interrupt is a trappable SIGTERM with a grace window.
            | "timeout", _ when not (List.isEmpty step.Block) || ctx.HostedBody.IsSome ->
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

                    // FG-172. A hosted body runs under the SAME effective deadline the
                    // block below would impose — the bound is the wrapper's entire purpose,
                    // and handing over a body without it is a safety bound defeated, which
                    // this project ranks with a bypassed approval.
                    match ctx.HostedBody with
                    | Some runBody ->
                        // The deadline travels on the dispatch, not on the context, so it
                        // is threaded through the host cell by the runner rather than here.
                        runBody { ctx with HostedBody = None; HostedDeadline = Some effective } cwd
                    | None ->
                        for inner in step.Block do
                            if not (halted ctx) then
                                runStepDispatch ctx cwd stage inner (Some effective)

                    // OWN deadline, not the effective one: under a shorter outer
                    // bound this block's budget may be untouched when the outer
                    // expiry aborts it, and the OWNER announces that.
                    if ctx.Failed.Value && deadlineDidFire (Some mine) then
                        emit "Timeout has been exceeded"

            // JB-FAIL-005: retry(N) is N TOTAL attempts, not N retries after the
            // first, and there is no backoff. `retry(3)` around a step that always
            // fails runs it exactly three times. The loop itself is `runWithRetry`,
            // shared with a stage's `options { retry(N) }`.
            | "retry", _ when not (List.isEmpty step.Block) || ctx.HostedBody.IsSome ->
                let hosted = ctx.HostedBody

                // FG-135 deliberately does NOT reach the retry STEP: at stage level
                // the step is one durability unit (a crash inside refuses resume by
                // name, the FG-171-measured contract), so its attempts need no
                // durable identity and journal no markers.
                runWithRetry ctx (retryCount (renderStepArgs ctx stage step)) 1 ignore (fun attemptCtx ->
                    // FG-172. EACH ATTEMPT re-runs the body, which is the point of `retry`
                    // and the reason the interpreter hands it over as a THUNK rather than
                    // pre-evaluated: the batch model had already run it once by the time
                    // the wrapper saw it, so N attempts were impossible to express.
                    match hosted with
                    | Some runBody -> runBody { attemptCtx with HostedBody = None } cwd
                    | None ->
                        for inner in step.Block do
                            if not (halted attemptCtx) then
                                runStepDispatch attemptCtx cwd stage inner deadline)

            // FG-041b. `withEnv(['A=1']) { … }` — block-scoped. MEASURED:
            // after the block an added variable is UNSET and a shadowed one
            // reverts, so the binding is an overlay on the inner scope only.
            // Receipt: `withenv-scoping`.
            // FG-172. `|| ctx.HostedBody.IsSome`: a hosted body leaves `Block` EMPTY, so a
            // guard keyed on `Block` alone never matched and the call fell through to the
            // fallback — the wrapper silently did nothing while the build reported success.
            // Measured on `script { withEnv([...]) { sh ... } }`, which produced no output
            // at all. `dir` escaped this only because its guard keys on the positional
            // argument instead; the same trap waits on every other Block-keyed arm.
            | "withEnv", _ when not (List.isEmpty step.Block) || ctx.HostedBody.IsSome ->
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
                // FG-172. A HOSTED call already has its list EVALUATED, so take it typed
                // rather than re-parsing display text. `Value.toDisplay` of a Groovy list
                // is `[A=1]` — no quotes — and the regex below matches quoted elements, so
                // the hosted path bound NOTHING and `withEnv` silently did nothing.
                //
                // No interpolation on this path: the interpreter has already expanded any
                // GString, and interpolating a second time is the defect that re-rendered
                // an approval prompt earlier in this ticket.
                let hostedBindings =
                    match ctx.HostedArgs with
                    | Some(VList items :: _, _) ->
                        items
                        |> List.choose (fun v ->
                            let entry = Value.toDisplay v

                            match entry.IndexOf '=' with
                            | i when i > 0 -> Some(entry.Substring(0, i), entry.Substring(i + 1))
                            | _ -> None)
                        |> Some
                    | _ -> None

                // EVERY ENTRY MUST BE `NAME=VALUE`, on BOTH paths.
                //
                // `List.choose` dropped a malformed entry silently, so
                // `withEnv(['BADENTRY']) { … }` ran its body and reported success. Seen at
                // STAGE level against the pinned Jenkins, which raises
                // `IllegalArgumentException` from `EnvStep` and fails the build. UNPROVEN
                // in-repo, and said so rather than dressed up: the probe DIVERGES on
                // output text — Jenkins prints a Java stack trace no engine can match — so
                // it cannot join a suite that requires 128/128. Result and workspace DO
                // agree now, which is the part that matters. That was a
                // PRE-EXISTING false success in the ordinary path, not something the script
                // bridge introduced; the hosted path merely inherited it. Fixed here, at
                // the rule, rather than in the hosted branch that happened to surface it —
                // the same call the body-less `dir` needed. Raised by the pre-push
                // verifier as the seventh of its class.
                let malformed (entry: string) = entry.IndexOf '=' <= 0

                let hostedMalformed =
                    match ctx.HostedArgs with
                    | Some(VList items :: _, _) -> items |> List.map Value.toDisplay |> List.filter malformed
                    | _ -> []

                let rawMalformed =
                    if Option.isSome hostedBindings then
                        []
                    else
                        step.Positional
                        |> List.collect (fun raw ->
                            [ for m in Text.RegularExpressions.Regex.Matches(raw, "'([^']*)'|\"([^\"]*)\"") ->
                                if m.Groups[1].Success then m.Groups[1].Value else m.Groups[2].Value ])
                        |> List.filter malformed

                match hostedMalformed @ rawMalformed with
                | bad :: _ ->
                    emit
                        $"ERROR: withEnv entry {bad} is not NAME=VALUE; Jenkins rejects an override without '='"

                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                | [] ->

                let bindings =
                    match hostedBindings with
                    | Some b -> b
                    | None ->

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

                // FG-172. A HOSTED body gets the same `inner` — the overlay is the whole
                // point of the wrapper, and handing over `ctx` would run the body without
                // the bindings while reporting success. `HostedBody = None` on the way in,
                // so a nested wrapper inside the body cannot re-run this one's block.
                match ctx.HostedBody with
                | Some runBody -> runBody { inner with HostedBody = None } cwd
                | None ->
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

                // FG-046b. An approver requires all three: a JOURNALED run, a
                // durability key for this step to answer against, and a hook that
                // actually has somewhere to take an answer FROM. A host started
                // without an approvals inbox satisfies the first two and still has
                // no approver — inferring one from the first two alone made such a
                // run wait forever on a prompt no human could see. The
                // differential path has none of the three and keeps the refusal.
                let approver =
                    match persistence, ctx.DurabilityKey with
                    | Some hooks, Some(stageKey, stepIndex) ->
                        hooks.PollInputAnswer
                        |> Option.map (fun poll ->
                            // taken ONCE, here, and reused for every poll: this
                            // prompt's identity within its durability key. Drawn
                            // at the moment the prompt is reached, so a resumed
                            // attempt re-running the same body reaches it in the
                            // same order and draws the same ordinal.
                            let occurrence = runCtx.NextInputOccurrence(stageKey, stepIndex)

                            // BOUNDED: an answer to a deadlined prompt stays
                            // provisional until the recheck below rules on it
                            // CANCELLABLE covers both ways this prompt can be
                            // stopped while it waits. A deadline was the obvious
                            // one; a failFast SIBLING is the other, and treating
                            // only the deadline as cancellable left an unbounded
                            // prompt in a failFast branch writing an immediately
                            // actionable answer that a crash could replay after
                            // the sibling had already failed.
                            let cancellable = deadline.IsSome || ctx.Interrupt.IsSome

                            // MASKED before it leaves the engine. The console
                            // copy is masked by Emit; this one is written to a
                            // FILE in an inbox that may be shared across builds,
                            // and the output leak-guard cannot see it because it
                            // is not output. Same run-scoped set either way.
                            let publishedPrompt = runCtx.MaskSecrets renderedMessage

                            (fun () -> poll stageKey stepIndex occurrence cancellable publishedPrompt),
                            (fun () -> hooks.OnInputClosed stageKey stepIndex occurrence),
                            (fun () -> hooks.OnInputAnswerVoided stageKey stepIndex occurrence))
                    | _ -> None

                // FG-046b (Codex P1). `submitter: 'release-team'` restricts WHO may
                // approve, and this engine cannot authenticate anyone: the inbox
                // protocol takes a self-declared `<who>`, so honouring the option
                // would mean anyone able to write to the inbox could pass a gate
                // Jenkins reserves for a named user or group. Enforcing it on a
                // name the approver chose for themselves is not enforcement, it
                // is theatre with an audit trail.
                //
                // So it fails closed BY NAME, and only where an answer could
                // otherwise be accepted — an unattended `timeout` run reaches its
                // deadline exactly as it does on Jenkins for anyone unauthorised,
                // so that path is left alone.
                // Two DIFFERENT options, refused for two different reasons — the
                // first version of this guard lumped them together and refused a
                // pipeline Jenkins allows. `submitter` RESTRICTS who may approve.
                // `submitterParameter` does not restrict anything: it names the
                // variable that receives the approving user's id. Both are
                // refused here, but a refusal that misstates what an option does
                // is a claim-accuracy defect in its own right.
                let unsupportedOption =
                    step.Named
                    |> List.tryPick (fun (k, _) ->
                        if k = "submitter" then
                            Some(
                                k,
                                "restricts approval to named users and this engine cannot authenticate a submitter — the approval protocol takes a self-declared name, so honouring the restriction would widen it to anyone who can answer"
                            )
                        elif k = "submitterParameter" then
                            Some(
                                k,
                                "binds the approving user's id into the build and this engine has no authenticated identity to bind — a self-declared name recorded as one would be a lie the pipeline goes on to use"
                            )
                        else
                            None)

                match approver, unsupportedOption with
                | Some _, Some(option, why) ->
                    // FG-114: the DISTINCT per-option reason scenario O could
                    // never assert, captured for the durable record
                    ctx.LastDiagnostic.Value <- Some $"input `{option}` {why}"
                    emit $"ERROR: input `{option}` {why}"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                | _ ->

                match approver, deadline with
                | None, None ->
                    // FG-114: the reason scenario E could never assert, captured
                    ctx.LastDiagnostic.Value <-
                        Some "input requires human approval and this engine has no approver; wrap it in a timeout to get Jenkins' abort-on-expiry behaviour"

                    emit "ERROR: input requires human approval and this engine has no approver; wrap it in a timeout to get Jenkins' abort-on-expiry behaviour"
                    ctx.Failed.Value <- true
                    ctx.Sink BuildStatus.Failure
                | Some(poll, withdraw, voidAnswer), _ ->
                    // Wait for the answer, the deadline, or a failFast sibling —
                    // whichever comes first. With no deadline this waits
                    // indefinitely, which is what Jenkins does and what an
                    // approver makes safe to reproduce.
                    let mutable outcome = Cancellation.Running
                    let mutable answer = None
                    // reported at its own site; it must NOT be routed through
                    // applyCancellation, which would name a failFast sibling
                    // that does not exist — the collateral-outranks-cause
                    // misclassification this walker has fought five times
                    let mutable approverFaulted = false

                    while outcome = Cancellation.Running && answer.IsNone && not approverFaulted do
                        // CANCELLATION IS CHECKED FIRST, both kinds, and an
                        // answer first OBSERVED after it is refused.
                        //
                        // The earlier version preferred an answer over an
                        // expired deadline, reasoning that a human who answered
                        // before expiry had answered. Nothing here can know
                        // that: the poll observes a FILE, the sleep can overshoot
                        // the deadline by its own interval, and an answer written
                        // in that window is indistinguishable from one written
                        // before. That is the same defect as every claim this
                        // project has had to retract — a comment asserting more
                        // than the code can establish — and here it would let a
                        // late approval defeat the safety bound the pipeline
                        // author wrote down. A `timeout` wins its ties; the human
                        // can answer the next build.
                        match cancellationOf ctx deadline with
                        | Cancellation.Running ->
                            // An approver that FAULTS is not an approver, and a
                            // fault here used to escape the step entirely: inside
                            // a parallel branch it faulted the branch task, which
                            // was swallowed, and the build could finish
                            // successfully having never been approved. It is a
                            // named failure now — the one thing a gate must never
                            // do is let the build past without an answer.
                            let polled =
                                try
                                    Ok(poll ())
                                with ex ->
                                    Result.Error ex

                            match polled with
                            | Result.Error ex ->
                                emit $"ERROR: input approver failed: {ex.GetType().Name}: {ex.Message}"
                                ctx.Failed.Value <- true
                                ctx.Sink BuildStatus.Failure
                                approverFaulted <- true
                            | Ok(Some a) ->
                                // RECHECKED, because a poll is not
                                // instantaneous: it reads the inbox, appends the
                                // answer to the journal and FSYNCS it, so on slow
                                // or stalled storage a poll that began inside the
                                // deadline can return outside it. Checking only
                                // BEFORE the poll left exactly the hole the
                                // pre-poll fix was meant to close, one layer down.
                                // The human's answer is durable either way — it is
                                // on record, it is simply too late for this build,
                                // and a `timeout` still wins its ties.
                                match cancellationOf ctx deadline with
                                | Cancellation.Running ->
                                    // ACCEPTED for THIS attempt, and nothing is
                                    // written to make it actionable for another
                                    // one. Promotion used to happen here, and a
                                    // promotion is itself a durable write that can
                                    // straddle the very deadline it rules on —
                                    // rechecking after it only moves the window,
                                    // which is the chase that produced four
                                    // findings. A cancellable prompt's answer
                                    // stays provisional in the journal: usable
                                    // now, never replayable later. FG-046c is what
                                    // would restore the resume, by recording an
                                    // ABSOLUTE deadline a later attempt can judge.
                                    answer <- Some a
                                | c ->
                                    // VOIDED durably before the cancellation is
                                    // applied. The answer is already on record —
                                    // the approver journals it as it reads it —
                                    // so refusing it in memory alone leaves a
                                    // usable approval behind: a kill before the
                                    // abort is recorded, and the resumed attempt
                                    // finds it, honours it under a FRESH
                                    // deadline, and the gate the timeout closed
                                    // opens anyway. The record stays for the
                                    // audit trail; the void says it was never
                                    // acted on.
                                    voidAnswer ()
                                    outcome <- c
                            | Ok None ->
                                let left = defaultArg (remainingMs deadline) 250L
                                System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(float (min 250L (max 10L left))))
                        | c -> outcome <- c

                    match answer with
                    | Some(InputApproved _) ->
                        // MEASURED on Jenkins 2.568.1: approving prints NOTHING —
                        // the console goes straight from the prompt to the next
                        // step, with no "Approved by ..." line. Emitting one here
                        // would be a divergence invented out of politeness.
                        // UNPROVEN BY RECEIPT (the harness cannot answer a prompt
                        // on the Jenkins side): measured by probe, asserted by
                        // scripts/run-approval-lane.sh.
                        ()
                    | Some(InputRejected _) ->
                        // MEASURED: a human abort ends the build ABORTED with
                        // `Rejected` as the reason line. Two stated residuals, both
                        // visible in the probe's console: Jenkins follows it with an
                        // `ErrorAction$ErrorId: <uuid>` line naming a Java class,
                        // which is engine-internal mimicry and is NOT emitted; and
                        // Jenkins prints both at END of build, after the pipeline
                        // teardown, whereas this emits at the step. Neither is
                        // observable in a receipt (the harness has no approver), so
                        // both are recorded rather than matched by guesswork.
                        // UNPROVEN BY RECEIPT, same reason as the approval path;
                        // asserted by scripts/run-approval-lane.sh.
                        emit "Rejected"
                        ctx.Failed.Value <- true
                        ctx.HumanRejected.Value <- true
                        ctx.Sink BuildStatus.Aborted
                    // WITHDRAWN the moment it dies, not at the end of the
                    // build: a retry starts the next occurrence immediately, and
                    // an inbox showing both is an invitation to answer the dead
                    // one. An answered prompt needs no withdrawal — the approver
                    // consumed it when it recorded the answer.
                    // NOT withdrawn on a fault, deliberately. The approver can
                    // fault AFTER reading a valid answer — a journal append or
                    // fsync throwing — and withdrawal deletes the decision file,
                    // which at that instant holds the human's only answer, not
                    // yet durable anywhere. Destroying it to tidy up is the exact
                    // loss this ticket exists to prevent. The build fails closed;
                    // the answer stays for a later attempt to adopt, and the
                    // stale marker is swept when the build reaches a terminal
                    // record.
                    | None when approverFaulted -> ()
                    | None ->
                        withdraw ()
                        applyCancellation ctx "input" deadline outcome
                | None, Some _ ->
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
                // BEFORE the directory is created. Jenkins rejects a body-less `dir`
                // before establishing any context, and creating `child/` and THEN failing
                // leaves a side effect behind on a build that should not have touched the
                // workspace. Ordering matters here in a way the receipt cannot see: the
                // manifest hashes FILES, so an extra EMPTY directory is invisible to it —
                // which is also why the case below no longer claims to prove agreement on
                // side effects. Raised by the pre-push verifier, which was careful to note
                // this is a side-effect blind spot rather than another false success.
                | Result.Ok _ when Option.isNone ctx.HostedBody && List.isEmpty step.Block ->
                    emit "ERROR: dir step must be called with a body"
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
                    // FG-172. A HOSTED body is Groovy, not a `Step list`, so it is run
                    // through the runner the script host supplied — and it is handed
                    // `target`, the directory this arm just established, for the same
                    // reason the loop below takes `target` rather than `cwd`. That
                    // distinction cost a defect once already: the body wrote to the stage
                    // root and only the workspace manifest's PATHS caught it.
                    // The body-less case is refused ABOVE, before the directory exists,
                    // so only the two real shapes reach here.
                    match ctx.HostedBody with
                    | Some runBody -> runBody { ctx with HostedBody = None } target
                    | None ->
                        for inner in step.Block do
                            if not (halted ctx) then
                                runStepDispatch ctx target stage inner deadline

            // FG-160. `script { … }` — SCRIPTED GROOVY, handed to the engine that
            // understands it. The body arrives verbatim (`ScriptBody`) because parsing it
            // as Declarative steps read `if (cond)` as a step named `if`, which is why
            // this never ran.
            //
            // THE REFUSALS COME FIRST, and they are why this is safe to ship. Steps run
            // LIVE through `Interpreter.runHosted` (FG-172) — the batch model this comment
            // used to describe is gone — but the walker's dispatch still returns UNIT, so
            // a step call yields null and a body reading a step's RETURN VALUE would decide
            // branches wrongly. `StepValueUse.find` names every such position and refuses.
            // FG-174 carries implementing `returnStdout`/`returnStatus`, which is what
            // would let that refusal be lifted.
            | "script", _ when step.ScriptBody.IsSome ->
                let src = Option.get step.ScriptBody

                // WHERE HOSTED STEPS RUN, as a cell rather than a capture. A
                // wrapper's body re-enters the interpreter, which calls back here —
                // and those inner steps must run in the directory and overlay the
                // WRAPPER established, not the ones that existed when the script
                // started. Capturing `ctx`/`cwd` in this closure is exactly the bug
                // `dir('x') { sh 'pwd' }` had under the batch model, in a new place.
                //
                // DECLARED HERE, ABOVE `fail`, AND THAT PLACEMENT IS THE FG-182 FIX — see
                // the note on `fail` for what reading `ctx` instead cost.
                let hostAt = ref (ctx, cwd)

                let fail (why: string) =
                    // FG-182. THE ACTIVE CONTEXT, NOT THE SCRIPT'S OWN. `runWithRetry`
                    // gives every attempt a fresh `Failed` ref and a throwaway status
                    // sink — deliberately, so that a body which fails once and then
                    // succeeds is a SUCCESS build (FG-035) — and `runBodyIn` points
                    // `hostAt` at that attempt while its body runs. Marking the captured
                    // `ctx` walked past both: the OUTER flag was set, the attempt still
                    // looked clean, and `halted` therefore admitted the NEXT call in the
                    // same closure. MEASURED: `retry(1) { sh('invalid', 'extra'); sh
                    // 'touch ran-after-invalid.txt' }` leaves Jenkins' workspace EMPTY —
                    // it stops the closure at the invalid invocation — and wrote the file
                    // here. A durable effect from a call Jenkins refused, which is the
                    // ADR 0001 class, not a bookkeeping slip. Receipt
                    // `script-retry-halts-attempt-on-refusal`, run against this code
                    // REVERTED and diverging on the workspace hash — both engines FAIL this
                    // build, so a result-only case would have proven nothing.
                    //
                    // Outside a wrapper body this cell still holds `(ctx, cwd)`, so the
                    // argument and parse refusals below are unchanged. Raised in review
                    // on PR #53.
                    let atCtx = fst hostAt.Value
                    // FG-114: the refusal's reason, captured for the durable record
                    atCtx.LastDiagnostic.Value <- Some why
                    emit $"ERROR: {why}"
                    atCtx.Failed.Value <- true
                    atCtx.Sink BuildStatus.Failure

                // `script` TAKES NO ARGUMENTS — only its implicit closure. The guard
                // above checks `ScriptBody` alone, so `script('ignored') { … }` ran the
                // body while Jenkins rejects the argument and leaves the workspace EMPTY.
                // Measured; raised in review on PR #53. The body running at all is the
                // defect: side effects from a pipeline Jenkins never started.
                if not (List.isEmpty step.Positional && List.isEmpty step.Named) then
                    fail "step 'script' takes no arguments, only its block; Jenkins rejects the call"
                else

                match Fogell.Groovy.Parser.Parser.parse src with
                | Result.Error e -> fail $"script block did not parse as Groovy: {e}"
                | Result.Ok body ->
                    // ONE MESSAGE PER KIND. A single sentence covered all three refusals
                    // and said every one of them "uses the return value" and that Fogell
                    // "performs a script's steps after the script runs" — false for an
                    // `env` mutation, and stale for ALL of them since FG-172 made steps run
                    // live. A refusal that misreports its own reason sends an author to fix
                    // the wrong thing, and this project counts an overclaim as a defect in
                    // itself. Raised by the pre-push verifier.
                    let describe (kind: string) (u: StepValueUse.Use) =
                        match kind with
                        | "value" ->
                            $"script block uses the return value of `{u.Step}` in {u.Where}; the walker's step \
                              dispatch returns no value, so Fogell cannot supply one. Refusing rather than \
                              binding null"
                        | "env" ->
                            $"script block assigns `{u.Step}` in {u.Where}; Jenkins applies that to every later \
                              step, and Fogell's environment overlay does not yet cross the script boundary. \
                              Refusing rather than accepting an assignment that does nothing"
                        | _ ->
                            $"script block calls `{u.Step}` with {u.Where}; that wrapper's walker arm cannot run \
                              a hosted body yet, so the block would be dropped and its semantics lost"

                    match
                        // The env pre-scan is GONE, not merely supplemented: it missed
                        // assignments inside closures, and the interpreter now reports every
                        // one that executes through `SetEnv`. A scanner that catches some
                        // shapes reads as coverage while leaving the rest silent.
                        (StepValueUse.find
                             scriptStepVocabulary.Contains
                             (fun n so st -> WalkerRules.returnContract n so st <> WalkerRules.NoValue)
                             body
                         |> List.map (describe "value"))
                        // DERIVED, not a hand-kept list: a block-taking step is refused
                        // unless its arm has been taught to run a hosted body. Add one to
                        // the vocabulary without teaching its arm and this fires, rather
                        // than the step running with its block silently dropped — which is
                        // what happened to `dir` before FG-172 and is the failure this
                        // guard exists for. Taught today: `dir`, `timeout`, `retry`,
                        // `withEnv`. The predicate is not vacuous — `withCredentials` is
                        // still outside the vocabulary and denied by the sandbox — and it
                        // stays honest as arms are taught, because it is derived rather
                        // than listed.
                        @ (StepValueUse.findWrapperCalls
                               (fun n ->
                                   scriptStepVocabulary.Contains n
                                   && not (scriptWrappersWithHostedBody.Contains n))
                               body
                           |> List.map (describe "wrapper"))
                    with
                    | _ :: _ as reasons ->
                        for r in reasons do
                            fail r
                    | [] ->
                        let envMap = envForWith ctx.EnvOverlay stage |> Map.ofList
                        let asValues = envMap |> Map.map (fun _ v -> VStr v)

                        // Both spellings bound, as `when { expression { … } }` learned to:
                        // a bare name AND `env.NAME`.
                        // FG-188. TOP-LEVEL `def` HELPERS ARE IN SCOPE. The source outside
                        // `pipeline { }` was discarded by the parser, so a helper declared
                        // there was invisible here and calling it failed as an unknown name
                        // — measured: Jenkins runs `def greet(v) { … }` from the preamble and
                        // Fogell failed with an EMPTY workspace. It is the corpus's commonest
                        // escape construct, 56 files.
                        //
                        // A PREAMBLE THAT DOES NOT PARSE IS IGNORED, NOT FATAL, and that is
                        // deliberate: it holds shebangs, annotations and imports that this
                        // Groovy subset does not model, and refusing a pipeline because its
                        // `@Library` line is unsupported would reject work Jenkins runs. Only
                        // the FUNCTIONS are taken; nothing else from it executes.
                        let preambleFuncs =
                            if System.String.IsNullOrWhiteSpace ctx.Preamble then
                                []
                            else
                                match Fogell.Groovy.Parser.Parser.parse ctx.Preamble with
                                | Result.Ok script ->
                                    script
                                    |> List.choose (function
                                        | Fogell.Groovy.SFunc(n, ps, body) -> Some(n, (ps, body))
                                        | _ -> None)
                                | Result.Error _ -> []

                        // FG-195. OVERLOADS RESOLVE BY ARITY NOW — `def pick()` beside
                        // `def pick(v)` is an ordinary pair and the interpreter picks per
                        // call — so only a SAME-ARITY duplicate fails closed here: Groovy
                        // rejects a duplicate method signature at compile time, and Fogell
                        // will not guess which body such a call means. This refusal was
                        // any-duplicate until the signature model landed.
                        let duplicateSignatures =
                            preambleFuncs
                            |> List.countBy (fun (n, (ps, _)) -> n, List.length ps)
                            |> List.filter (fun (_, count) -> count > 1)
                            |> List.map fst

                        match duplicateSignatures with
                        | (dup, arity) :: _ ->
                            fail
                                $"script block: the preamble declares '{dup}' more than once with {arity} parameter(s); Groovy rejects the duplicate signature and Fogell will not guess which body a call means"
                        | [] ->

                        let genv =
                            preambleFuncs
                            |> List.fold
                                (fun acc (n, (ps, body)) -> Env.withFunc n ps body acc)
                                (Env.ofValues (asValues |> Map.add "env" (VMap(ref asValues))))

                        // STRICT VARIABLE READS. `Interpreter.run` is the lax mode where an
                        // unbound name reads as null — kept for consumers modelling scripted
                        // Groovy's laxer contexts — and a script block is not one of them:
                        // Jenkins raises MissingPropertyException, which this repo already
                        // measured in the `gstring-unresolved-property` receipt. With the lax
                        // mode `script { sh "echo bare:${MISSING}" }` wrote `bare:null` and
                        // exited 0 where Jenkins FAILS the build. Raised by the pre-push
                        // verifier; a success where Jenkins fails is the governing defect.
                        // FG-172. LIVE, via the callback seam: the host performs each step
                        // where the script reaches it, instead of collecting requests and
                        // replaying them afterwards.
                        //
                        // WHAT THE HOST DOES TODAY, corrected from a note that still said
                        // "no observable change yet" long after there was one: hosted
                        // WRAPPER BODIES run here (`dir`, `timeout`, `retry`), and an `env`
                        // assignment is reported at the moment it executes and refused.
                        // Still refused: a step's RETURN VALUE (dispatch yields unit),
                        // `input` (durable identity is per top-level step, FG-171), and
                        // and `withCredentials`, whose nested-call argument DSL the
                        // interpreter cannot evaluate.
                        // `hostAt` is declared with `fail`, above — see the FG-182 note there.

                        // Point the host at a wrapper's context for the duration of its
                        // body, then restore. `try/finally` because a step inside the body
                        // can fail the build, and leaving the cell pointing into an
                        // abandoned wrapper would silently run the REST of the script in
                        // the wrong directory.
                        let runBodyIn (thunk: unit -> unit) (inner: BranchCtx) (wd: string) =
                            let saved = hostAt.Value
                            hostAt.Value <- (inner, wd)

                            try
                                thunk ()
                            finally
                                hostAt.Value <- saved

                        let host: PerformStep =
                            { Perform =
                                fun name rawPositional rawNamed runBody ->
                                    // SHAPE FIRST, then everything downstream sees one
                                    // form — the Step record, the signature arms and the
                                    // typed `HostedArgs` a wrapper reads.
                                    match normaliseHostedCall name rawPositional rawNamed with
                                    | Error why ->
                                        fail $"script block: {why}"
                                        VNull
                                    | Ok(positional, named) ->

                                    let called =
                                        { Name = name
                                          Positional = positional |> List.map Value.toDisplay
                                          Named = named |> List.map (fun (k, v) -> k, Value.toDisplay v)
                                          Block = []
                                          // ALREADY EVALUATED by the interpreter, so no
                                          // further interpolation: see the literal marking
                                          // that fixed the re-rendered approval prompt.
                                          LiteralNamedArgs = named |> List.map fst |> Set.ofList
                                          LiteralPositionalArgs =
                                            positional |> List.mapi (fun i _ -> i) |> Set.ofList
                                          InterpolationSource = []
                                          ExpressionArgs = Set.empty
                                          ArgumentOrder =
                                            (positional |> List.mapi (fun i _ -> $"#{i}"))
                                            @ (named |> List.map fst)
                                          RawArgs = ""
                                          ScriptBody = None
                                          Position = step.Position }

                                    match hostedSignatureError name positional named with
                                    | Some why ->
                                        fail $"script block: {why}"
                                        VNull
                                    | None ->

                                    let atCtx, atCwd = hostAt.Value

                                    let dispatchCtx =
                                        match runBody with
                                        | Some thunk ->
                                            { atCtx with
                                                HostedBody = Some(runBodyIn thunk)
                                                HostedArgs = Some(positional, named) }
                                        // CLEARED for a plain step, not merely left alone: a
                                        // stale runner inherited from an enclosing wrapper
                                        // would let a body-less call run some other call's
                                        // block.
                                        | None ->
                                            { atCtx with
                                                HostedBody = None
                                                HostedArgs = Some(positional, named) }

                                    // The deadline a hosted `timeout` established wins over
                                    // the one captured when the script started; without this
                                    // the bound is announced and not applied.
                                    let effectiveDeadline =
                                        match atCtx.HostedDeadline with
                                        | Some d -> Some d
                                        | None -> deadline

                                    // FG-174. A fresh slot PER CALL: reusing one would let a
                                    // step that returns nothing hand back the previous
                                    // step's value, which is worse than null because it
                                    // looks plausible.
                                    let slot = ref VNull

                                    if not (halted dispatchCtx) then
                                        // FG-176. OBSERVED, not sunk directly: a SHELL step's
                                        // failure surfaces to the script as the catchable,
                                        // retryable fault Jenkins raises (AbortException),
                                        // instead of silently marking the branch failed where
                                        // no try/catch could ever see it. Every other status —
                                        // aborts included, whose FG-101 classification must
                                        // win — re-sinks exactly as before, and every NON-shell
                                        // step keeps the old fail-loud path until its own
                                        // measurement moves it: a Fogell refusal caught by a
                                        // script would recover from a gap in this engine while
                                        // Jenkins ran the real step.
                                        let observedStatus = ref BuildStatus.Success
                                        let observedFailed = ref false

                                        let observing =
                                            { dispatchCtx with
                                                HostedResult = Some slot
                                                Failed = observedFailed
                                                Sink =
                                                    fun s ->
                                                        observedStatus.Value <- BuildStatus.worstOf observedStatus.Value s }

                                        runStepDispatch observing atCwd stage called effectiveDeadline

                                        if
                                            observedStatus.Value = BuildStatus.Failure
                                            // `sh` alone: `bat` is outside the script
                                            // vocabulary today, so a bat arm here would
                                            // be dead code wearing a parity claim
                                            && name = "sh"
                                        then
                                            Interpreter.raiseStepFailed name
                                        else
                                            dispatchCtx.Sink observedStatus.Value

                                            if observedFailed.Value then
                                                dispatchCtx.Failed.Value <- true

                                    // WHAT THE STEP PUT THERE — `sh(returnStdout: true)` its
                                    // stdout, `sh(returnStatus: true)` its exit code as an
                                    // Integer, and `VNull` for everything else, which is
                                    // still every step that does not opt in.
                                    slot.Value
                              // FG-178. Read through `hostAt`, which POINTS AT THE
                              // WRAPPER while its body runs — so `withEnv`'s overlay is
                              // what the body evaluates against. Reading `ctx` here would
                              // capture the script's own context and reproduce the defect.
                              CurrentEnv = fun () -> envForWith (fst hostAt.Value).EnvOverlay stage
                              // FG-184. Answered from the ONE table that says which steps
                              // host a body — the same set `runStepDispatch` uses — so the
                              // interpreter's normalisation and the walker's dispatch
                              // cannot disagree about what a block-taking step is.
                              TakesBlock =
                                fun name -> WalkerRules.scriptWrappersWithHostedBody.Contains name
                              SetEnv =
                                fun name _ ->
                                    // THE REFUSAL ITSELF, at the moment the assignment runs.
                                    // Jenkins applies `env.X` to every later step; Fogell's
                                    // overlay does not yet cross the script boundary, so
                                    // accepting it would silently do nothing.
                                    fail
                                        $"script block assigns `env.{name}`; Jenkins applies that to every later step, and Fogell's environment overlay does not yet cross the script boundary. Refusing rather than accepting an assignment that does nothing" }

                        let outcome =
                            Interpreter.runHosted host Budget.defaults scriptStepVocabulary genv body

                        match outcome.Fault with
                        | Some fault ->
                            match fault with
                            | Denied d when scriptStepsRefusedWithReason.ContainsKey d.Attempted ->
                                fail
                                    $"script block calls `{d.Attempted}`, which Fogell refuses inside a script: {scriptStepsRefusedWithReason.[d.Attempted]}"
                            | StepFailed _ ->
                                // FG-176. The failing step already narrated its own ERROR at
                                // dispatch; an uncaught escape must fail the build exactly as
                                // the old direct sink did — quietly here, or the console
                                // carries the failure twice where Jenkins prints it once.
                                let activeCtx, _ = hostAt.Value
                                activeCtx.Failed.Value <- true
                                activeCtx.Sink BuildStatus.Failure
                            | _ -> fail $"script block: {fault}"
                        | None ->
                            // NOTHING TO REPLAY: the steps ran as the script reached them.
                            // `Outcome.Effects` is empty in hosted mode by construction.
                            //
                            // DURABILITY: these steps go through the dispatcher without
                            // their own `OnStepStarted`/`OnStepFinished`, so the whole
                            // block is ONE durability unit. A crash mid-block REFUSES
                            // resume by name — measured for FG-171, whose row had claimed
                            // a re-run; the FG-171 scenario in run-restart-lane.sh pins
                            // both the refusal and that no child effect duplicates.
                            // Per-child identity needs the journal format change FG-135
                            // carries.
                            ()

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
                                                ctx.Sink st
                                        // FG-114: FRESH per step, so a step whose
                                        // own path emits nothing cannot inherit
                                        // its predecessor's reason
                                        LastDiagnostic = ref None
                                        // FG-046b: the key this step's durable
                                        // records are written under, so a step
                                        // that needs to consult them (`input`)
                                        // can name itself without the walker
                                        // threading an index through every
                                        // wrapper body it may be nested in
                                        DurabilityKey = Some(stage.Name, i) }

                                runStepDispatch observing cwd stage step deadline
                                hooks.OnStepFinished stage.Name i observed.Value

                                // FG-114: the reason travels only beside a failed
                                // or aborted finish — a green step's diagnostics
                                // are narration, not explanation
                                if observed.Value = BuildStatus.Failure || observed.Value = BuildStatus.Aborted then
                                    observing.LastDiagnostic.Value
                                    |> Option.iter (fun r -> hooks.OnStepReason stage.Name i r))



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
                                { Preamble = ctx.Preamble
                                  Failed = ref false
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
                                  HostedBody = None
                                  HostedDeadline = None
                                  HostedArgs = None
                                  HostedResult = None
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
                                        ctx.Interrupt
                                  // FG-046b: NOT inherited. The branch is about to
                                  // run its own stage body, which sets the key for
                                  // each step it executes; carrying the parent's
                                  // key in would name the PARALLEL step that owns
                                  // the branches, so an `input` inside two branches
                                  // would answer to one shared key.
                                  DurabilityKey = None
                                  LastDiagnostic = ref None
                                  // INHERITED, unlike the key: a human declining a
                                  // gate in any branch is a rejection of the
                                  // enclosing attempt, and an enclosing `retry`
                                  // must see it rather than ask them again.
                                  HumanRejected = ctx.HumanRejected }

                            branchCtx,
                            System.Threading.Tasks.Task.Run(fun () ->
                                // FG-046b (Codex P1). A branch that THROWS skips the
                                // epilogue below, so under failFast it never stamped
                                // the instant nor cancelled the token — and the join
                                // cannot stand in for that, because the waiter awaits
                                // branches IN ORDER: a fault in the second branch is
                                // not even observed while the first is still running
                                // the side-effecting steps failFast exists to
                                // interrupt. The signal has to come from the branch
                                // itself, at the moment it dies.
                                //
                                // The failure is marked here too, so `bc.Failed` is
                                // true by the time any sibling polls it; the exception
                                // still propagates, and the waiter still names it.
                                try
                                    runStage branchCtx cwd deadline branch
                                with _ ->
                                    branchCtx.Failed.Value <- true
                                    // named exactly as the ordinary failure path
                                    // names it — a reader wants to know WHICH
                                    // branch died either way
                                    emit $"Failed in branch {branch.Name}"

                                    if failFast then
                                        System.Threading.Interlocked.CompareExchange(
                                            siblingFailedAt, runClock.ElapsedMilliseconds, -1L)
                                        |> ignore

                                        siblingFailed.Cancel()

                                    reraise ()

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
                    |> List.iter (fun (bc, t) ->
                        try
                            t.Wait()
                        with ex ->
                            // FG-046b (Codex P1). This used to swallow the
                            // exception and move on, while the ONLY failure
                            // signal read below is `bc.Failed` — which a fault
                            // never sets. So a branch that threw vanished: the
                            // build could report SUCCESS having silently skipped
                            // whatever that branch was doing, and for an `input`
                            // branch that means shipping without the approval.
                            //
                            // A branch's ordinary failure never arrives here (it
                            // is recorded through Failed/Sink), so anything that
                            // does is an ENGINE fault, and the honest response to
                            // one is to fail the build by name.
                            let root =
                                match ex with
                                | :? AggregateException as agg ->
                                    agg.Flatten().InnerExceptions
                                    |> Seq.tryHead
                                    |> Option.defaultValue ex
                                | _ -> ex

                            emit $"ERROR: parallel branch failed: {root.GetType().Name}: {root.Message}"
                            bc.Failed.Value <- true
                            bc.Sink BuildStatus.Failure)

                    if branches |> List.exists (fun (bc, _) -> bc.Failed.Value) then
                        ctx.Failed.Value <- true
                else
                    for nested in stage.Nested do
                        if skipStagesAfterUnstable && runCtx.Status() = BuildStatus.Unstable then
                            emit
                                $"Stage \"{nested.Name}\" skipped due to earlier stage(s) marking the build as unstable"
                        else
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
