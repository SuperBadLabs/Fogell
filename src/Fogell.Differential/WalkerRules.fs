namespace Fogell.Differential

open System
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir
// FG-172: BranchCtx carries typed hosted arguments, so the interpreter's Value is in scope.
open Fogell.Groovy.Interpreter

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
      /// FG-188. The pipeline source OUTSIDE `pipeline { }`, carried so a `script { }`
      /// body can see top-level `def` helpers. Raw text, parsed at the point of use: the
      /// walker's rule module has no business holding a Groovy AST, and a `script` block
      /// is rare enough that parsing it there costs nothing measurable.
      Preamble: string
      Interrupt: (unit -> bool) option
      /// Branch-local failure. Deliberately NOT the build status: a failed
      /// branch fails the build but does not halt its siblings.
      Failed: bool ref
      /// FG-114. The LAST ERROR-shaped diagnostic this branch emitted, captured
      /// where the string exists so a failed step's REASON can be journaled —
      /// `Trace.isDiagnosticLine` consumes the console copy down to a boolean,
      /// and a durable build record whose only explanation of a failure is the
      /// word `failure` sent every reader to a log that no longer says why.
      /// Freshened per hooked step by the dispatch loop, so a step cannot
      /// inherit its predecessor's reason.
      LastDiagnostic: string option ref
      /// Where this branch's step statuses go. Normally the build status; inside
      /// a `retry` attempt, a throwaway sink — a failed attempt that is retried
      /// must not leave a permanent mark on the build, and the build status is a
      /// monotone worst-of that could never be walked back.
      Sink: BuildStatus -> unit
      /// Stage-local decoration which must not change the build result. Jenkins
      /// represents this as a WarningAction on the step FlowNode; Fogell carries
      /// the one runtime consequence it can observe directly: stage post
      /// selection. Kept separate from Sink because JUnit build-result
      /// suppression leaves the build SUCCESS while its enclosing stage remains
      /// UNSTABLE.
      ///
      /// A stage body replaces this with a sink that updates its own stage result
      /// and forwards to an enclosing stage. The run root ignores it. Persistence
      /// observes and replays it separately from the step's build status.
      StageSink: BuildStatus -> unit
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
      /// FG-172. The body of a HOSTED wrapper — a `script { }` block's
      /// `dir('x') { … }`, whose body is Groovy rather than a `Step list`.
      ///
      /// Takes the context the wrapper established, because that is the whole
      /// difficulty: the body re-enters the interpreter, which calls back into the
      /// walker, and those inner steps must run in the wrapper's directory and
      /// overlay rather than the ones captured when the script started. The wrapper
      /// hands over what it set up; the runner points the host at it for the
      /// duration and restores it afterwards.
      ///
      /// `None` for every ordinary step, where the body is `step.Block` and this
      /// question does not arise.
      HostedBody: (BranchCtx -> string -> unit) option
      /// FG-172. The deadline a hosted body must run under.
      ///
      /// Carried on the context ONLY because a deadline normally travels as a dispatch
      /// ARGUMENT, and a hosted body's inner steps are dispatched by the script host — which
      /// has no way to be told. Without it `timeout(1) { … }` inside a `script` would run its
      /// body unbounded while announcing a budget: a safety bound defeated, which this
      /// project ranks alongside a bypassed approval.
      HostedDeadline: Deadline option
      /// FG-172. The arguments of a HOSTED call, still TYPED.
      ///
      /// `Step` holds strings by design — ADR 0002 says the interpreter decides what an
      /// expression means, and the parser must not pre-empt it. But by the time a script
      /// block calls a step the interpreter HAS decided: `withEnv(['A=1'])` is a list of
      /// one string, and rendering it to display text turned it into `[A=1]`, which the
      /// arm's list parser could not read at all.
      ///
      /// So the typed form rides HERE rather than on `Step`: this layer already depends on
      /// the interpreter, `Step` stays a string record, and an arm that wants structure
      /// asks for it while every other arm is untouched. `None` for an ordinary step,
      /// where there is no evaluated form to offer.
      HostedArgs: (Value list * (string * Value) list) option
      /// FG-174. Where a step puts its RETURN VALUE for a hosted caller.
      ///
      /// A ref cell rather than a return type because `runStepDispatch` returns unit and
      /// every wrapper arm calls it — threading a value back through all of them to serve
      /// one caller is the invasive change, and this is the same shape `HostedBody` already
      /// uses. `None` outside a script block, where nothing is listening.
      ///
      /// The inner option is deliberately UNSET until a modelled producer publishes.
      /// `VNull` is a genuine measured answer, so it cannot also be the initial marker
      /// for modelled JUnit and SCM-map producers. Wrapper-body values
      /// are captured by the interpreter around the existing unit body thunk.
      ///
      /// TYPED, not text: `returnStatus` must yield a Groovy INTEGER or `if (code == 0)`
      /// compares an Integer to a String and is quietly false — a wrong answer of exactly
      /// the kind this ticket exists to prevent.
      HostedResult: Value option ref option
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
    /// FG-174. The steps whose CONTRACT includes `returnStdout` / `returnStatus`.
    ///
    /// These are durable-task shell steps, and the flags are THEIR options — not a
    /// general mechanism any step can opt into. Treating them as universal was the
    /// twelfth finding of the admitted-option class: `def got = echo(message: 'hello',
    /// returnStdout: true)` handed the script `"hello\n"`, where Jenkins' `echo` returns
    /// null and merely warns about the unknown parameter. A pipeline branching on
    /// `got == null` then takes the OTHER branch and skips work Jenkins runs, while the
    /// build reports success — a false success, and one no amount of output comparison
    /// would show, since the skipped work leaves no trace to compare.
    ///
    /// ONE definition, read by BOTH the static refusal (`StepValueUse`, through a
    /// predicate, so the interpreter layer holds no step names) and the runtime that
    /// publishes the value (`WalkerStep`). Two copies of this set is how the two ends
    /// disagree about which steps answer, and that disagreement is precisely the shape
    /// of the defect above.
    ///
    /// `bat` IS UNPROVEN HERE and is listed on contract, not on evidence: it is the
    /// same durable-task shell step with the same two options, but there is no Windows
    /// differential lane, so no receipt covers it and this engine does not claim Windows
    /// support. Listing it keeps the two ends agreeing if that lane is ever built;
    /// nothing in the suite exercises it today, and it should not be read as coverage.
    let stepsHonouringReturnFlags = set [ "sh"; "bat" ]

    /// FG-174. WHAT A CALL RETURNS — the whole contract in one function, because three
    /// review rounds running it was NOT one.
    ///
    /// The flags went in as independent booleans, and each round found another way that
    /// assumption is wrong: they are not universal (finding 12), and they are not
    /// ORTHOGONAL either (finding 13). MEASURED on a disposable 2.568.1 container and
    /// held by receipt `script-sh-return-both`:
    /// `sh(script: 'exit 7', returnStdout: true, returnStatus: true)` returns Integer 7
    /// and the build continues. Fogell returned the STDOUT, so a following `if (code == 7)`
    /// compared a String to an Integer, took the other arm, and skipped work Jenkins runs
    /// while reporting success.
    ///
    /// So `returnStatus` WINS. Stating it as a total function over (step, flags) is the
    /// point: a boolean at each call site cannot express precedence, and the two readers
    /// were free to resolve the combination differently — which is exactly what they did.
    type UnsupportedReturn =
        | OutsideHostedVocabulary

    type ReturnContract =
        /// The callback really returns Groovy null. This is a modelled value, not the
        /// absence of a model.
        | GenuineNull
        /// A Groovy Integer. `if (code == 0)` must compare Integer to Integer, or the
        /// branch is quietly false.
        | ExitStatus
        /// stdout, byte-verbatim, trailing newline included.
        | CapturedStdout
        /// A block-taking hosted step returns the value produced by its body closure.
        /// The interpreter preserves the Value directly; the walker only decides
        /// when and how often the existing body thunk runs.
        | BodyResult
        /// A nominal, immutable projection of the three pinned integer count
        /// properties on Jenkins' TestResultSummary. Every other object operation
        /// remains refused by the interpreter.
        | JUnitSummary
        /// The closed one-remote map returned by the pinned Git plugin for `git`
        /// and SCM-defined `checkout scm`. The interpreter owns the nominal map
        /// surface; this contract only admits the producer into a value position.
        | ScmMap
        /// Jenkins returns a value, but Fogell does not yet carry a closed model for it.
        | UnsupportedValue of UnsupportedReturn

    /// FG-172. Block-taking steps whose walker arm can run a HOSTED body — Groovy from a
    /// `script { }` rather than a `Step list`. Everything else block-taking is refused.
    let scriptWrappersWithHostedBody = set [ "dir"; "timeout"; "retry"; "withEnv" ]

    /// FG-160. The steps a `script { }` block may call at all. Defined here so a test can
    /// hold `positionalArity` against it — a step admitted to the vocabulary with no
    /// arity entry is the same silent-pass hole `timeout` fell through.
    let scriptStepVocabulary =
        set
            [ "sh"; "echo"; "archiveArtifacts"; "junit"; "checkout"; "deleteDir"; "git"
              "stash"; "unstable"; "unstash"; "dir"; "timeout"; "retry"; "withEnv" ]

    /// How a fresh terminal status returned by a hosted step is delivered to
    /// scripted Groovy. Only the two pinned AbortException paths are catchable;
    /// every other vocabulary member remains a catch-opaque status halt. This
    /// exhaustive policy replaces the orchestration layer's ad-hoc name check.
    type HostedStatusFailureDelivery =
        | CatchableStepFailure
        | DeferredStatusHalt

    let hostedStatusFailureDelivery name =
        if not (scriptStepVocabulary.Contains name) then
            None
        elif name = "sh" || name = "unstash" then
            Some CatchableStepFailure
        else
            Some DeferredStatusHalt

    /// FG-177 slice 1. Jenkins has two measured unknown-key binding policies.
    type UnknownNamedBinding =
        | WarnAndContinue of bindingClass: string
        | ConstructorMapThrow

    /// One closed call contract for every step the hosted interpreter may dispatch.
    /// `PrimaryParameter` is a promotion name, not a requiredness assertion: Jenkins
    /// accepts `echo()` and body-only `retry()` even though both advertise a primary.
    type StepDescriptor =
        { MaxPositionals: int
          PrimaryParameter: string option
          RequiresPrimary: bool
          MissingPrimaryException: Fogell.Groovy.Interpreter.BindingExceptionClass option
          NamedKeys: Set<string>
          UnsupportedNamedKeys: Set<string>
          UnknownNamed: UnknownNamedBinding
          ReturnValue: ReturnContract
          ShapeText: string
          Check: (Fogell.Groovy.Interpreter.Value list -> (string * Fogell.Groovy.Interpreter.Value) list -> string option) }

    let private noCheck = fun (_: Fogell.Groovy.Interpreter.Value list) (_: (string * Fogell.Groovy.Interpreter.Value) list) -> None

    /// Validation diagnostics must be bounded independently of a value graph's
    /// expanded textual size. Scalars retain their useful spelling; collections
    /// use a type marker so an invalid hosted call cannot spend exponential time
    /// rendering a small, heavily aliased DAG before it reports the shape error.
    let private diagnosticValue =
        function
        | Fogell.Groovy.Interpreter.VList _ -> "<list>"
        | Fogell.Groovy.Interpreter.VMap _ -> "<map>"
        | Fogell.Groovy.Interpreter.VRange _ -> "<range>"
        | Fogell.Groovy.Interpreter.VJUnitSummary _ -> "<junit-test-result-summary>"
        | Fogell.Groovy.Interpreter.VScmMap _ -> "<scm-map>"
        | Fogell.Groovy.Interpreter.VScmKeySet _ -> "<scm-key-set>"
        | Fogell.Groovy.Interpreter.VClosure _ -> "<closure>"
        | Fogell.Groovy.Interpreter.VFunc(name, _, _) -> $"<function {name}>"
        | value -> Fogell.Groovy.Interpreter.Value.toDisplay value

    let stepDescriptors: Map<string, StepDescriptor> =
        let open' = diagnosticValue
        let row max primary required missingPrimaryException named unsupported unknown returnValue shape check =
            { MaxPositionals = max
              PrimaryParameter = primary
              RequiresPrimary = required
              MissingPrimaryException = missingPrimaryException
              NamedKeys = Set.ofList named
              UnsupportedNamedKeys = Set.ofList unsupported
              UnknownNamed = unknown
              ReturnValue = returnValue
              ShapeText = shape
              Check = check }
        let warn className = WarnAndContinue className

        Map.ofList
            [ "sh",
              row 1 (Some "script") true (Some Fogell.Groovy.Interpreter.IllegalArgumentException)
                  [ "script"; "encoding"; "label"; "returnStatus"; "returnStdout" ]
                  []
                  (warn "org.jenkinsci.plugins.workflow.steps.durable_task.ShellStep")
                  GenuineNull
                  "takes at most 1 positional argument" noCheck
              "echo",
              row 1 (Some "message") false None [ "message" ] [] ConstructorMapThrow GenuineNull
                  "takes at most one message argument" noCheck
              "archiveArtifacts",
              row 1 (Some "artifacts") true (Some Fogell.Groovy.Interpreter.IllegalArgumentException)
                  [ "artifacts"; "allowEmptyArchive"; "caseSensitive"; "defaultExcludes"
                    "excludes"; "fingerprint"; "followSymlinks"; "onlyIfSuccessful" ]
                  []
                  (warn "hudson.tasks.ArtifactArchiver")
                  GenuineNull
                  "takes exactly one artifact pattern" noCheck
              "junit",
              row 1 (Some "testResults") true (Some Fogell.Groovy.Interpreter.NullPointerException)
                  [ "testResults"; "allowEmptyResults"; "checksName"; "healthScaleFactor"
                    "keepLongStdio"; "keepProperties"; "keepTestNames"
                    "skipMarkingBuildUnstable"; "skipMarkingStageUnstable"
                    "skipOldReports"; "skipPublishingChecks"; "stdioRetention"
                    "testDataPublishers" ]
                  []
                  (warn "hudson.tasks.junit.pipeline.JUnitResultsStep")
                  JUnitSummary
                  "takes exactly one test-results pattern" noCheck
              "checkout",
              row 1 (Some "scm") true (Some Fogell.Groovy.Interpreter.NullPointerException) [ "scm"; "changelog"; "poll" ] []
                  (warn "org.jenkinsci.plugins.workflow.steps.scm.GenericSCMStep")
                  ScmMap
                  "takes exactly one SCM argument" noCheck
              "deleteDir",
              row 0 None false None [] []
                  (warn "org.jenkinsci.plugins.workflow.steps.DeleteDirStep")
                  GenuineNull
                  "takes no positional arguments" noCheck
              "git",
              row 1 (Some "url") false None [ "url"; "branch"; "changelog"; "credentialsId"; "poll" ] []
                  (warn "jenkins.plugins.git.GitStep")
                  ScmMap
                  "takes at most one repository URL" noCheck
              "stash",
              row 1 (Some "name") true (Some Fogell.Groovy.Interpreter.IllegalArgumentException)
                  [ "name"; "allowEmpty"; "excludes"; "includes"; "useDefaultExcludes" ]
                  []
                  (warn "org.jenkinsci.plugins.workflow.support.steps.stash.StashStep")
                  GenuineNull
                  "takes exactly one stash name" noCheck
              "unstable",
              row 1 (Some "message") true (Some Fogell.Groovy.Interpreter.IllegalArgumentException) [ "message" ] [] ConstructorMapThrow GenuineNull
                  "takes exactly one message" noCheck
              "unstash",
              row 1 (Some "name") true (Some Fogell.Groovy.Interpreter.IllegalArgumentException) [ "name" ] [] ConstructorMapThrow GenuineNull
                  "takes exactly one stash name" noCheck
              "withEnv",
              row 1 (Some "overrides") true (Some Fogell.Groovy.Interpreter.IllegalArgumentException) [ "overrides" ] [] ConstructorMapThrow
                  BodyResult
                  "takes exactly one list argument of NAME=VALUE strings"
                  (fun positional _ ->
                      match positional with
                      | [ Fogell.Groovy.Interpreter.VList items ] ->
                          items.Value
                          |> List.map open'
                          |> List.tryFind (fun entry -> entry.IndexOf '=' <= 0)
                          |> Option.map (fun bad ->
                              $"withEnv entry {bad} is not NAME=VALUE; Jenkins rejects an override without '='")
                      | _ -> Some "`withEnv` takes exactly one list argument of NAME=VALUE strings")
              "dir",
              row 1 (Some "path") true (Some Fogell.Groovy.Interpreter.NullPointerException) [ "path" ] [] ConstructorMapThrow
                  BodyResult
                  "takes exactly one path argument"
                  (fun positional _ ->
                      match positional with
                      | [ _ ] -> None
                      | _ -> Some "`dir` takes exactly one path argument")
              "retry",
              row 1 (Some "count") false None [ "count" ] [ "conditions" ]
                  (warn "org.jenkinsci.plugins.workflow.steps.RetryStep")
                  BodyResult
                  "takes at most one attempt count"
                  (fun positional _ ->
                      match positional with
                      | [] -> None
                      | [ Fogell.Groovy.Interpreter.VInt _ ] -> None
                      | [ Fogell.Groovy.Interpreter.VInteger _ ] -> None
                      | [ Fogell.Groovy.Interpreter.VArithmeticInteger _ ] -> None
                      | [ other ] -> Some $"`retry` needs an integer attempt count, not `{open' other}`"
                      | _ -> Some "`retry` takes at most one attempt count")
              "timeout",
              row 1 (Some "time") false None [ "time"; "unit"; "activity" ] []
                  (warn "org.jenkinsci.plugins.workflow.steps.TimeoutStep")
                  BodyResult
                  "takes at most one positional time argument or named time/unit/activity arguments" noCheck ]

    /// Compatibility views are derived from the descriptor, never independently kept.
    let positionalArity = stepDescriptors |> Map.map (fun _ descriptor -> descriptor.MaxPositionals)

    let soleRequiredParameter =
        stepDescriptors
        |> Map.toList
        |> List.choose (fun (name, descriptor) -> descriptor.PrimaryParameter |> Option.map (fun key -> name, key))
        |> Map.ofList

    let hostedSignatures =
        stepDescriptors |> Map.filter (fun name _ -> scriptWrappersWithHostedBody.Contains name)

    type HostedCallWarning =
        { BindingClass: string
          UnknownKeys: string list }

    type ValidatedHostedCall =
        { Positional: Fogell.Groovy.Interpreter.Value list
          Named: (string * Fogell.Groovy.Interpreter.Value) list
          Warnings: HostedCallWarning list }

    type HostedCallFailure =
        | EngineRefusal of string
        | JenkinsBindingThrow of exceptionClass: Fogell.Groovy.Interpreter.BindingExceptionClass * detail: string * warnings: HostedCallWarning list

    /// The one hosted-call boundary. Unknowns are classified before primary promotion,
    /// so `echo(message: 'x', unknown: true)` can never normalize into a valid echo.
    let validateHostedCall
        (name: string)
        (positional: Fogell.Groovy.Interpreter.Value list)
        (named: (string * Fogell.Groovy.Interpreter.Value) list)
        : Result<ValidatedHostedCall, HostedCallFailure> =
        // Kept lazy because descriptor lookup and several binding failures do
        // not need values at all. When a shape diagnostic does, its rendering is
        // bounded by argument count rather than recursively expanded aliases.
        let shown () = positional |> List.map diagnosticValue |> String.concat ", "

        match Map.tryFind name stepDescriptors with
        | None -> Error(EngineRefusal $"hosted step `{name}` has no descriptor")
        | Some _ when not (List.isEmpty positional) && not (List.isEmpty named) ->
            let displayed = shown ()
            Error(
                EngineRefusal
                    $"`{name}` takes positional arguments OR named ones, not both; Jenkins rejects `[{displayed}]` with 'Expected named arguments'"
            )
        | Some descriptor ->
            let unsupported =
                named
                |> List.map fst
                |> List.filter descriptor.UnsupportedNamedKeys.Contains

            let allowed = Set.union descriptor.NamedKeys descriptor.UnsupportedNamedKeys

            let unknown =
                named
                |> List.map fst
                |> List.filter (fun key -> not (allowed.Contains key))

            match unsupported, descriptor.UnknownNamed, unknown with
            | first :: _, _, _ ->
                Error(EngineRefusal $"`{name}` named key `{first}:` is recognized by Jenkins but not implemented by Fogell")
            | [], ConstructorMapThrow, first :: _ ->
                let parameter = defaultArg descriptor.PrimaryParameter "constructor"
                Error(
                    JenkinsBindingThrow(
                        Fogell.Groovy.Interpreter.IllegalArgumentException,
                        $"`{name}` binds a map containing unknown key `{first}:` to `{parameter}`",
                        []
                    )
                )
            | _ ->
                let warnings =
                    match descriptor.UnknownNamed, unknown with
                    | WarnAndContinue bindingClass, _ :: _ ->
                        [ { BindingClass = bindingClass; UnknownKeys = unknown } ]
                    | _ -> []

                let normalisedPositional, normalisedNamed =
                    match positional, descriptor.PrimaryParameter with
                    | [], Some primary ->
                        match named |> List.partition (fun (key, _) -> key = primary) with
                        | [ pair ], rest ->
                            let only = snd pair
                            [ only ], rest
                        | _ -> positional, named
                    | _ -> positional, named

                if List.length normalisedPositional > descriptor.MaxPositionals then
                    let displayed = shown ()
                    Error(EngineRefusal $"`{name}` {descriptor.ShapeText}; Jenkins rejects `[{displayed}]`")
                elif descriptor.RequiresPrimary && List.isEmpty normalisedPositional then
                    let primary = defaultArg descriptor.PrimaryParameter "its primary parameter"
                    match descriptor.MissingPrimaryException with
                    | Some exceptionClass -> Error(JenkinsBindingThrow(exceptionClass, $"`{name}` requires `{primary}`", warnings))
                    | None -> Error(EngineRefusal $"descriptor for `{name}` requires `{primary}` but has no measured binding exception class")
                else
                    match descriptor.Check normalisedPositional normalisedNamed with
                    | Some reason -> Error(EngineRefusal reason)
                    | None ->
                        Ok
                            { Positional = normalisedPositional
                              Named = normalisedNamed
                              Warnings = warnings }

    /// FG-174. Whether a return flag is set — and, when it is written in a form Fogell
    /// will not guess at, a reason to refuse BEFORE the step runs.
    type FlagState =
        | FlagOn
        | FlagOff
        /// Named, and rejected with this reason. Never "assume off": that is how
        /// `returnStatus: 1` ran the shell and reported success.
        | FlagRejected of string

    /// These are BOOLEAN setters on durable-task's `ShellStep`, and the value's TYPE
    /// decides — which the rendered TEXT cannot, since `returnStatus: true` and
    /// `returnStatus: 'true'` both render to "true".
    ///
    /// MEASURED on the pinned lab by scratch probe — and UNPROVEN in-repo, deliberately:
    /// neither shape can become a receipt, because Fogell refuses where Jenkins RUNS the
    /// doomed shell (first case) or answers with a Java stack trace no engine can match
    /// (second). Both agree on terminal result AND workspace hash; they differ only in
    /// output text, which is exactly the shape FG-129 describes. What the probes found,
    /// after the previous version compared `Trim().ToLowerInvariant()` against "true":
    ///   - `returnStatus: ' true '` — Jenkins treats it as FALSE, so `exit 7` FAILS the
    ///     build. Fogell trimmed, suppressed the exit, and ran the following step.
    ///   - `returnStatus: 1` — Jenkins REJECTS the call before running anything:
    ///     `IllegalArgumentException: Could not instantiate {script=…, returnStatus=1}
    ///     for ShellStep`, workspace empty. Fogell ran the shell and reported success.
    /// Both are false successes, and the second is one Jenkins refuses outright.
    ///
    /// SO ONLY A LITERAL BOOLEAN COUNTS, and everything else is REFUSED rather than
    /// coerced. That is deliberately NARROWER than Jenkins, which would accept the string
    /// `'true'` through `Boolean.valueOf`. The narrowing is declared rather than hidden,
    /// and it is measured to cost nothing: across the 228-file corpus every one of the
    /// 134 uses is the literal `true` form (117 `returnStdout: true`, 11
    /// `returnStatus: true`, plus 6 written without a space). Reproducing Java's coercion
    /// from rendered text would mean guessing at semantics I would then have to keep in
    /// step, to serve a spelling nobody writes — and guessing WRONG in the permissive
    /// direction is what this whole class of finding has been.
    ///
    /// `isLiteralBoolean` is the caller's answer to "was this written as a bare `true`
    /// or `false`, not as text": `Step.ExpressionArgs` at stage level, and the typed
    /// `HostedArgs` value inside a `script` block.
    let returnFlag (isLiteralBoolean: bool) (rendered: string) : FlagState =
        match isLiteralBoolean, rendered with
        | true, "true" -> FlagOn
        | true, "false" -> FlagOff
        | true, other ->
            FlagRejected
                $"expects a boolean and got `{other}`; Jenkins rejects the call before running the step (IllegalArgumentException from ShellStep)"
        | false, other ->
            FlagRejected
                $"expects a boolean literal and got the text `{other}`; Fogell refuses rather than reproduce Jenkins' string coercion, which treats `' true '` as FALSE"

    /// `returnStdout`/`returnStatus` are LITERAL-true here; a non-literal flag cannot be
    /// decided statically and its caller must refuse the value use, which fails safe.
    /// Every hosted default comes from its descriptor. `bat` is outside that vocabulary
    /// and keeps the durable-task parity contract documented above.
    let returnContract (stepName: string) (returnStdout: bool) (returnStatus: bool) : ReturnContract =
        if stepsHonouringReturnFlags.Contains stepName then
            if returnStatus then ExitStatus
            elif returnStdout then CapturedStdout
            else GenuineNull
        else
            stepDescriptors
            |> Map.tryFind stepName
            |> Option.map (fun descriptor -> descriptor.ReturnValue)
            |> Option.defaultValue (UnsupportedValue OutsideHostedVocabulary)

    let returnValueIsModelled = function
        | GenuineNull
        | ExitStatus
        | CapturedStdout
        | BodyResult
        | JUnitSummary
        | ScmMap -> true
        | UnsupportedValue _ -> false

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

    /// The count a `retry` carries, or None when it is missing, not an integer,
    /// not positive, or accompanied by ANY other argument — the doc said only
    /// "missing or not an integer" after the arity check was added to the body.
    ///
    /// FG-053(b). Separate from [retryCount] because a STAGE OPTION must be able to
    /// REFUSE a malformed count: `options { retry('nope') }` is refused by Jenkins
    /// at compile time (`Expecting "int" but got "nope"`), where falling back to a
    /// default ran the stage and reported SUCCESS — an invalid Jenkinsfile
    /// performing side effects. The step spelling keeps its default; only the
    /// option validates, because that is where the measurement is.
    let retryCountOpt (step: Step) : int option =
        // EXACT ARITY, not "a count can be found somewhere in here". Reading the
        // named `count` or the FIRST positional and ignoring the rest accepted
        // `retry(2, 3)` and `retry(count: 2, bogus: true)` — discarding arguments
        // and running the stage, while the diagnostic claimed it required "one
        // positive integer count". Mirrors `ansiColorMap`, which allows exactly the
        // positional and named spellings of its one parameter and nothing else.
        let value =
            match step.Positional, step.Named with
            | [ v ], [] -> Some v
            | [], [ ("count", v) ] -> Some v
            | _ -> None

        value
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
