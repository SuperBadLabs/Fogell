namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir


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

    let internal runWith
        (envReplacements: (string * string) list)
        (workspaceRoot: string)
        (jobName: string)
        (buildNumber: int)
        (previousBuild: BuildStatus option)
        (freshWorkspace: bool)
        (scm: ScmSpec option)
        (persistence: PersistenceHooks option)
        (script: string)
        : Result<Trace, string> =
        match Fogell.Pipeline.Parser.Parser.parse script with
        | Result.Error e -> Result.Error $"{Fogell.Admission.ErrorCode.toWireString e.Code} at {e.Position}: {e.Message}"
        | Result.Ok pipeline ->
            // FG-105: the run-scoped mutable state lives in WalkerCtx — one record,
            // one stated contract (see WalkerCtx.fs for its two-lock discipline).
            // These rebinds keep call sites unchanged.
            let runCtx = WalkerCtx.create ()

            // FG-053. The SCRIPT decides whether a timestamp-shaped prefix is
            // engine decoration or the build's own output — nothing in a line's
            // shape can tell those apart, so normalisation is told rather than
            // left to guess.
            // ZERO-ARGUMENT, and a name-only check accepted anything. Jenkins'
            // Declarative `timestamps()` takes no arguments and REJECTS
            // `timestamps(false)` or named parameters when it compiles the
            // model, so accepting them here would run a build Jenkins refuses —
            // failing OPEN on a script the reference engine will not execute.
            // EVERY entry, not the first. `options` is a Step LIST and the parser
            // keeps them all, so `options { timestamps(); timestamps(false) }`
            // arrives with both — `tryFind` saw the valid one, accepted it, and
            // ran a build Jenkins refuses to compile.
            let timestampsOptions =
                pipeline.Options |> List.filter (fun o -> o.Name = "timestamps")

            let timestampsArgError =
                if
                    timestampsOptions
                    |> List.exists (fun o -> not (List.isEmpty o.Positional) || not (List.isEmpty o.Named))
                then
                    Some "the timestamps() option takes no arguments"
                else
                    None

            let declaresTimestamps = not (List.isEmpty timestampsOptions) && timestampsArgError.IsNone

            let ansiColorOptions =
                pipeline.Options |> List.filter (fun o -> o.Name = "ansiColor")

            // Jenkins' ansiColor step exposes ONE parameter, the colour map name.
            // `ansiColor('xterm', 'vga')` ran here with TERM=xterm and the extra
            // argument silently dropped — the same fail-open shape the timestamps
            // arg check closed, and every entry is validated for the same reason
            // `tryFind` was wrong there.
            // `ansiColor('xterm')` AND `ansiColor(colorMapName: 'xterm')` are both
            // valid — the parameter has a name and Groovy lets it be passed
            // either way. A positional-only check REFUSED the named form, which
            // is worse than the fail-open it replaced: it rejects a Jenkinsfile
            // Jenkins accepts.
            let ansiColorMap (o: Step) =
                match o.Positional, o.Named with
                | [ m ], [] -> Some m
                | [], [ ("colorMapName", m) ] -> Some m
                | _ -> None

            let ansiColorArgError =
                if List.length ansiColorOptions > 1 then
                    // Declarative rejects a repeated option at compile time; we
                    // silently applied the first and ignored the rest.
                    Some "the ansiColor option is declared more than once"
                elif ansiColorOptions |> List.exists (fun o -> (ansiColorMap o).IsNone) then
                    Some "the ansiColor(<colorMapName>) option takes exactly one argument, positional or named colorMapName"
                else
                    None

            // STAGE-LEVEL `options { timestamps() }` is REFUSED, not ignored.
            // Jenkins 2.568.1 honours it and stamps that stage's output; Fogell
            // enables the wrapper for the whole build or not at all, so honouring
            // the pipeline form while silently dropping the stage form would
            // produce unstamped output where Jenkins stamps — a divergence the
            // engine would not announce. Refusing by name is this project's
            // stated direction for a construct it does not implement (FG-103),
            // and FG-120 carries the scoped enable/restore that would support it.
            // FG-053. Options are classified in THREE ways, and the default is
            // refusal. An earlier version of this allowlist held every name Jenkins
            // accepts, which conflated "Jenkins knows this NAME" with "Fogell
            // implements this BEHAVIOUR" — so `checkoutToSubdirectory('src')`, which
            // moves the automatic checkout under a subdirectory, was accepted and
            // silently ignored. That is the divergence this project refuses by name
            // rather than commits quietly (FG-103). Caught by the pre-push verifier.
            //
            // HONOURED: the engine implements the semantics.
            // PIPELINE scope. `retry` is deliberately ABSENT: FG-053(b) implements
            // it for a STAGE's options only, and Jenkins' pipeline-level `retry`
            // retries the WHOLE PIPELINE — a different feature this engine does not
            // have. Listing it here accepted `options { retry(3) }` at pipeline
            // scope and ran ONE attempt, silently, which is the name-vs-scope
            // conflation PR #38 fixed for `ansiColor` and I reproduced in the very
            // next ticket. The corpus has ZERO pipeline-level `retry` (measured:
            // 0 pipeline / 2 stage), so refusing it costs nothing today.
            let honouredOptions =
                set [ "timeout"; "timestamps"; "ansiColor"; "skipDefaultCheckout"; "parallelsAlwaysFailFast"
                      "skipStagesAfterUnstable" ]

            // INERT: retention and queueing policy with no observable effect on ONE
            // build, which is all a receipt can see. Accepted with the reason stated.
            // Receipt: `options-accept-and-ignore`.
            let inertOptions =
                set [ "buildDiscarder"; "disableConcurrentBuilds"; "quietPeriod"; "rateLimitBuilds" ]

            // Everything else Jenkins accepts is REFUSED, because accepting it would
            // mean running a build whose semantics this engine does not reproduce.
            // FG-053(b) has since implemented `skipStagesAfterUnstable` (pipeline
            // scope) and `retry` (STAGE scope only — Jenkins' pipeline-level `retry`
            // retries the whole pipeline, which this engine does not do), so both
            // have moved out of this set. What remains refused is everything Jenkins
            // accepts whose semantics are not reproduced here.
            let supportedOptions = Set.union honouredOptions inertOptions

            // SCOPE, measured against the lab and UNPROVEN BY RECEIPT for the FG-129
            // reason above: Jenkins enumerates a DIFFERENT valid set for a stage
            // `options` block than for the pipeline one and refuses pipeline-only
            // names there — `stage { options { buildDiscarder(...) } }` is
            // jenkins=failure. Fogell refuses far more narrowly than that (only
            // `timeout` survives at stage scope, see below), so an explicit
            // pipeline-only SET is not needed to get the refusal right; it existed
            // here, was read by nothing after the stage rule tightened, and is
            // deleted rather than left as a binding that looks load-bearing.

            let refusedPipelineOptions =
                pipeline.Options
                |> List.map (fun o -> o.Name)
                |> List.filter (fun n -> not (supportedOptions.Contains n))
                |> List.distinct

            let stageOptionNames =
                pipeline.Stages
                |> Pipeline.flattenStages
                |> List.collect (fun st -> st.Options |> List.map (fun o -> o.Name))
                |> List.distinct

            // HONOURED IS PER (NAME, SCOPE), NOT PER NAME. The previous version
            // allowed any supported name at stage scope, but `ansiColor` is read
            // ONLY from `pipeline.Options`, so `stage { options { ansiColor('xterm') } }`
            // ran to success with TERM=dumb instead of xterm — silently, which is
            // the whole failure mode this classification exists to stop. Same for
            // `skipDefaultCheckout`, also read pipeline-only. Caught by the
            // pre-push verifier, which built the scratch pipeline and read TERM.
            //
            // `timeout` and `retry` are the options honoured at stage scope: the
            // orchestrator calls `deadlineFromOptions stage.Options` for the first
            // and `runWithRetry` over `runStageBody` for the second (FG-053(b)).
            // Stage `timestamps` is refused separately by FG-120. Everything else in
            // a stage block is refused — including names Jenkins accepts there —
            // because this engine reads none of them.
            // `retry` joins `timeout` here now that FG-053(b) implements it: the
            // stage's steps run through the shared retry loop. It was refused by
            // FG-053(a) precisely so it could not run with the wrong semantics
            // silently, and that refusal is what this ticket lifts.
            let stageHonouredOptions = set [ "timeout"; "retry" ]

            // `timestamps` is EXCLUDED here so the FG-120 branch below owns it. The
            // generic list caught it first and reported "unknown option type(s):
            // timestamps" — a name Jenkins knows, that this engine knows, and that
            // is refused for a specific reason the reader is then denied. A
            // diagnostic that misclassifies is worse than a vague one.
            let refusedStageOptions =
                stageOptionNames
                |> List.filter (fun n -> not (stageHonouredOptions.Contains n) && n <> "timestamps")

            // Two refusal SITES, pipeline and stage — NOT two accurate categories,
            // and an earlier comment here claimed they were. They are not: a
            // pipeline `retry` is a name Jenkins knows and this engine does not
            // implement, and it went out as "unknown"; a genuinely unknown name in a
            // stage block went out as "not honoured at stage scope". Both refusals
            // are CORRECT — the wording was what lied, in the commit that split them
            // to stop exactly that.
            //
            // Both now say UNSUPPORTED, which is true of every case either list can
            // hold. Telling unknown from known-but-unimplemented from wrong-scope
            // needs the Jenkins-known set per scope, which is FG-133; claiming the
            // distinction without making it is how this went wrong twice.
            let unknownOptionNames = refusedPipelineOptions |> List.distinct

            let stageScopeRefusals = refusedStageOptions |> List.distinct

            // FG-053(b). Pipeline-level only (Jenkins does not accept it at stage
            // scope). ZERO-ARGUMENT: reading it by mere presence meant
            // `skipStagesAfterUnstable(false)` ENABLED the skip — the option saying
            // the opposite of what it does. That is the `parallelsAlwaysFailFast(false)`
            // defect I filed as FG-130 and then reproduced here in the same session;
            // an argument-bearing form is refused rather than guessed at.
            let skipStagesOptions =
                pipeline.Options |> List.filter (fun o -> o.Name = "skipStagesAfterUnstable")

            // FG-053(b). A stage `retry` with a missing or non-integer count is a
            // COMPILE refusal, not a default. UNPROVEN BY RECEIPT for the FG-129
            // reason — no compile-shaped refusal is sealable — and measured on
            // Jenkins 2.568.1:
            // `options { retry('nope') }` gives
            // `Expecting "int" but got "nope" of type class java.lang.String`
            // and jenkins=failure, where defaulting to one attempt ran the stage
            // and reported fogell=success — an invalid Jenkinsfile performing side
            // effects. Same shape as `timestampsArgError` beside it.
            let stageRetryArgError =
                pipeline.Stages
                |> Pipeline.flattenStages
                |> List.collect (fun st -> st.Options |> List.filter (fun o -> o.Name = "retry"))
                |> List.tryPick (fun o ->
                    match WalkerRules.retryCountOpt o with
                    | Some _ -> None
                    | None -> Some "the retry(<count>) option needs one positive integer count")

            // FG-053(b). `unstable()` with NO message is a COMPILE refusal — MEASURED
            // on Jenkins 2.568.1: `Missing required parameter: "message"`, nothing
            // runs, empty workspace. UNPROVEN BY RECEIPT (FG-129).
            //
            // A BLANK message is different and is handled at the STEP, not here:
            // `unstable(message: '')` compiles, so `+ echo before` RUNS and the build
            // then fails at the step. Collapsing the two would have been wrong in one
            // direction or the other.
            // RECURSIVELY, into wrapper bodies. Scanning `st.Steps` alone missed
            // `timeout { sh '...'; unstable() }` — the body lives in `Step.Block` —
            // so the refusal was bypassed, `before.txt` WAS created, and the comment
            // below claiming "nothing runs, empty workspace" was false for exactly
            // the shape a wrapper produces. Same top-level-only mistake as the
            // options scan and `Parser.fs`'s first-vs-all, in a third place.
            let rec flattenSteps (steps: Step list) =
                steps |> List.collect (fun st -> st :: flattenSteps st.Block)

            // EVERY step list a build can execute: stage steps, stage `post` arms,
            // and pipeline `post` arms — `Post` is a SEPARATE FIELD from `Steps`, so
            // a scan made recursive into `Step.Block` still missed
            // `post { always { unstable() } }`, which ran the stage body and left
            // workspace side effects before failing at runtime.
            //
            // Fourth variant of the same incompleteness on this branch: one section
            // of many (`tryPick`), top-level stages only, `st.Steps` without
            // `Step.Block`, and now steps without `post`. Each time the traversal
            // covered the shape in front of me and the comment described all of them.
            let postSteps (post: (PostCondition * Step list) list) =
                post |> List.collect (fun (_, steps) -> flattenSteps steps)

            let unstableArgError =
                (pipeline.Stages
                 |> Pipeline.flattenStages
                 |> List.collect (fun st -> flattenSteps st.Steps @ postSteps st.Post))
                @ postSteps pipeline.Post
                |> List.filter (fun st -> st.Name = "unstable")
                |> List.tryPick (fun st ->
                    // ARITY is compile-shaped too, and I had inferred it was runtime.
                    // UNPROVEN BY RECEIPT (FG-129: no compile-shaped refusal is
                    // sealable), measured on Jenkins 2.568.1 — `unstable('a','b')` gives
                    // `Arguments to "unstable" must be explicitly named.` with an
                    // EMPTY workspace, where routing it through the blank-message
                    // runtime path had already run the preceding step. Three
                    // outcomes, not two:
                    //   no message      -> compile refusal (Missing required parameter)
                    //   extra arguments -> compile refusal (must be explicitly named)
                    //   blank message   -> COMPILES, prior steps run, then throws
                    match st.Positional, st.Named with
                    | [ _ ], [] -> None
                    | [], [ ("message", _) ] -> None
                    | [], [] -> Some "the unstable step requires a message"
                    | _ -> Some "the unstable step takes exactly one message argument")

            let skipStagesArgError =
                if
                    skipStagesOptions
                    |> List.exists (fun o -> not (List.isEmpty o.Positional) || not (List.isEmpty o.Named))
                then
                    Some "the skipStagesAfterUnstable() option takes no arguments"
                else
                    None

            let skipAfterUnstable = not (List.isEmpty skipStagesOptions) && skipStagesArgError.IsNone

            let stageTimestamps =
                pipeline.Stages
                |> Pipeline.flattenStages
                |> List.exists (fun st -> st.Options |> List.exists (fun o -> o.Name = "timestamps"))

            let emit = runCtx.Emit
            let bump = runCtx.Bump
            let deadlineDidFire = runCtx.DeadlineDidFire

            let workspace = Path.Combine(workspaceRoot, jobName)
            // artifacts live OUTSIDE the workspace so archiving cannot perturb
            // the workspace hash the differential compares
            let artifactRoot = Path.Combine(workspaceRoot, "_artifacts")
            // FG-110: only a sequence's FIRST build starts clean — Jenkins does
            // not wipe the workspace between builds of one job, and neither do we.
            if freshWorkspace && Directory.Exists workspace then
                Directory.Delete(workspace, true)

            // a NEW job has no build history — mirror the harness's doDelete
            // (the record otherwise survives from a previous run of this case)
            if freshWorkspace then
                WalkerGit.resetHistory artifactRoot jobName

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
                // FG-110: in a sequence these must INCREMENT — Jenkins' do, and
                // `when { environment name: 'BUILD_NUMBER' ... }` selects on them.
                [ "BUILD_NUMBER", string buildNumber
                  "BUILD_ID", string buildNumber
                  "BUILD_DISPLAY_NAME", $"#{buildNumber}"
                  "JOB_NAME", jobName
                  "JOB_BASE_NAME", jobName
                  "WORKSPACE", Path.Combine(workspaceRoot, jobName)
                  "EXECUTOR_NUMBER", "0"
                  "NODE_NAME", "built-in" ]

            // FG-105: env resolution and argument rendering live in WalkerArgs.
            let root =
                { Interrupt = None
                  Failed = ref false
                  Sink = bump
                  EnvOverlay = []
                  HostedBody = None
                  Secrets = []
                  SiblingFailedAt = ref -1L
                  // set per top-level step by runStageBody; nothing outside a
                  // stage's step list is journaled, so the root carries none
                  DurabilityKey = None
                  HumanRejected = ref false }

            let mutable scmWrapperEnv: (string * string) list = []

            // EVERY SCM case, BEFORE anything can set `root.Failed`. This is the
            // lane's core invariant: the bytes this engine was handed must be the
            // bytes the SCM serves, because Jenkins executes the latter.
            //
            // It lived inside `match scm with Some spec when not root.Failed.Value`,
            // so the FG-053 option refusals — which set `root.Failed` above — made a
            // refused SCM case skip verification entirely and seal against unverified
            // bytes. The comment on this check already recorded it vanishing once
            // before, "exactly when skipDefaultCheckout did"; guarding it on a
            // failure flag is how it keeps happening. Unconditional now: a check that
            // any later short-circuit can switch off is not fail-closed.
            match scm with
            | Some spec ->
                match WalkerGit.readRemoteJenkinsfile spec.Url spec.Branch with
                | Result.Error e ->
                    failwith $"SCM case verification unavailable ({e}) — refusing to seal against unverified bytes"
                // the subprocess reader trims trailing whitespace, so compare TRIMMED
                // on both sides (a trailing newline is not a different script) with
                // line endings normalised
                | Ok remote when remote.Replace("\r\n", "\n").Trim() <> script.Replace("\r\n", "\n").Trim() ->
                    failwith (
                        "SCM case drift: the local case body does not match the SCM's Jenkinsfile — "
                        + "sync the fixture repo (scripts/sync-scm-cases.bb) before sealing"
                    )
                | Ok _ -> ()
            | None -> ()


            match scm with
            | Some spec when not root.Failed.Value ->
                // MEASURED only for `agent any` at pipeline level (receipt
                // `checkout-scm-basic`: one up-front auto-checkout). Under
                // `agent none`/stage agents Jenkins places the default checkout
                // at each applicable stage-agent entry instead — UNPROVEN here,
                // so that shape refuses by name rather than checking out where
                // Jenkins would not (FG-103).
                let stageAgents =
                    pipeline.Stages
                    |> Pipeline.flattenStages
                    |> List.exists (fun st -> st.Agent.IsSome)

                if pipeline.Agent <> AgentAny || stageAgents then
                    emit
                        "ERROR: an SCM-defined pipeline without a top-level `agent any` (or with stage-level agents) is not modelled — default-checkout placement differs and is unmeasured"

                    root.Failed.Value <- true
                    bump BuildStatus.Failure
            | _ -> ()

            // REFUSED, and NOT YET TERMINAL — say what this does, because the
            // sentence here previously claimed "fail closed" and "running the
            // build at all would be failing OPEN" while the code does exactly
            // that. A reviewer reproduced it: `timestamps(false)` with a
            // `post { always { sh ... } }` exits FAILURE and still runs the post
            // step, creating its file. Jenkins rejects the model before any
            // stage or post runs, so this executes side effects for a script the
            // reference engine never would.
            //
            // What IS right here is the POSITION: before the SCM block, because
            // Jenkins refuses after the lightweight Jenkinsfile fetch and BEFORE
            // its generated default checkout — validating after `runCheckout`
            // left repository files behind and turned a compile refusal into a
            // workspace-hash divergence.
            //
            // Making it terminal is a control-flow change to the walker and is
            // FG-121, with the reproduction recorded there. No corpus file
            // declares any of these forms; every one is a script Jenkins itself
            // rejects.
            // A COMPILE-SHAPED refusal, distinct from a build failure: Jenkins
            // rejects the model before any stage or post runs, so neither may
            // run here. `root.Failed` alone left pipeline `post` executing and
            // creating files for a script the reference engine never accepts —
            // reproduced by the verifier with `timestamps(false)` and a
            // `post { always { sh ... } }`.
            let mutable compileRejected = false

            if not (List.isEmpty unknownOptionNames) then
                emit ("ERROR: pipeline declares option(s) this engine does not support: " + String.concat ", " unknownOptionNames)
                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure

            if not (List.isEmpty stageScopeRefusals) then
                emit (
                    "ERROR: stage declares option(s) this engine does not support at stage scope: "
                    + String.concat ", " stageScopeRefusals
                    + " — refusing rather than running them with the wrong semantics"
                )

                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure

            if stageTimestamps then
                emit
                    "ERROR: stage-level options { timestamps() } is not implemented; Jenkins stamps that stage's output and this engine would not — refusing rather than diverging silently (FG-120)"

                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure

            match ansiColorArgError with
            | Some e ->
                emit $"ERROR: pipeline declares an unusable ansiColor option: {e}"
                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure
            | None -> ()

            match unstableArgError with
            | Some e ->
                emit $"ERROR: a stage declares an unusable unstable step: {e}"
                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure
            | None -> ()

            match stageRetryArgError with
            | Some e ->
                emit $"ERROR: a stage declares an unusable retry option: {e}"
                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure
            | None -> ()

            match skipStagesArgError with
            | Some e ->
                emit $"ERROR: pipeline declares an unusable skipStagesAfterUnstable option: {e}"
                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure
            | None -> ()

            match timestampsArgError with
            | Some e ->
                emit $"ERROR: pipeline declares an unusable timestamps option: {e}"
                root.Failed.Value <- true
                compileRejected <- true
                bump BuildStatus.Failure
            | None -> ()

            match scm with
            | Some spec when not root.Failed.Value ->
                // BEFORE the option can apply. Jenkins must FETCH and PARSE the
                // Jenkinsfile before a Declarative option exists to activate, so
                // its own provenance line is unprefixed even when the script
                // declares `timestamps()`. Stamping it here would make Fogell
                // report `all` against Jenkins' `partial` and fail a case whose
                // output and workspace agree — a divergence invented by the
                // wrapper's placement rather than by either engine.
                emit $"Obtained Jenkinsfile from git {spec.Url}"

                // `options { skipDefaultCheckout() }` suppresses the Declarative
                // auto-checkout; the Obtained line still prints (the definition
                // was still loaded from SCM). A positional `false` RE-ENABLES it
                // — the option's argument decides, not its presence.
                // Receipt: `checkout-scm-skip-default`.
                let skipDefault =
                    pipeline.Options
                    |> List.exists (fun o ->
                        o.Name = "skipDefaultCheckout"
                        && (o.Positional.IsEmpty || o.Positional.Head.Trim() <> "false"))

                if not skipDefault then

                    // Jenkins-provided env ONLY: the auto-checkout runs BEFORE
                    // the withEnv wrapper (measured order — the Obtained line
                    // precedes everything), so pipeline `environment {}` must
                    // not reach it. The top-level timeout does NOT bound it and
                    // its banner prints AFTER the checkout — MEASURED (receipt
                    // `checkout-scm-timeout-env`) — hence deadline None here
                    // and the deadline computation BELOW this block.
                    let sha =
                        WalkerGit.runCheckout
                            runCtx
                            root
                            workspace
                            None
                            jenkinsProvided
                            artifactRoot
                            jobName
                            buildNumber
                            spec

                    if root.Failed.Value then
                        bump BuildStatus.Failure
                    else
                        // The wrapper env the checkout returns — MEASURED values
                        // (receipt `checkout-scm-timeout-env`): full sha,
                        // origin/-prefixed branch, the remote url — overlaid on
                        // every user stage and the pipeline post, exactly the
                        // withEnv wrapper Jenkins inserts.
                        scmWrapperEnv <-
                            match sha with
                            | Some s ->
                                [ "GIT_COMMIT", s
                                  "GIT_BRANCH", $"origin/{spec.Branch}"
                                  "GIT_URL", spec.Url ]
                            | None -> []
            | _ -> ()

            // FG-053. HERE: after SCM provenance AND after the auto-checkout,
            // before the stage walk. Jenkins cannot activate a Declarative
            // option until it has fetched and parsed the Jenkinsfile, so its
            // provenance line and its default checkout are BOTH unprefixed and
            // stamping begins with the build's own step output.
            //
            // MEASURED, not reasoned — receipt `options-timestamps-scm` reports
            // PARTIAL (1/21) on BOTH engines. An earlier draft of this comment
            // said "before checkout" and "everything from the checkout onward is
            // stamped"; the checkout runs above, so that was wrong about its own
            // placement even while the code was right.
            //
            // Enabling at context creation stamped the provenance line too and
            // made Fogell read `all` against Jenkins' `partial` — a divergence
            // the wrapper's placement invented, on a case whose output and
            // workspace agreed.
            if declaresTimestamps then
                runCtx.EnableTimestamps()


            // The SCM wrapper values sit at the BASE layer — after the
            // Jenkins-provided variables, BEFORE pipeline/stage declarations —
            // so a declared GIT_COMMIT overrides the wrapper (measured
            // semantics: declarations apply INSIDE the wrapper) and `when`
            // conditions see the wrapper values like any other env.
            // FG-053. `ansiColor('<map>')` sets TERM to the map name for the
            // scope it wraps. MEASURED, and it is the reason this option is NOT
            // the inert accept-and-ignore an earlier commit here called it:
            //   jenkins=+ echo TERM=[xterm]    fogell=+ echo TERM=[]
            // Receipt: `options-ansicolor`.
            // A case that checked only plain output passed while any pipeline
            // reading TERM diverged.
            //
            // It joins the BASE layer beside the SCM wrapper values, for the
            // same reason they are there: a declared `environment { TERM = ... }`
            // must override it, because a declaration applies INSIDE the wrapper.
            let ansiColorEnv =
                match ansiColorOptions with
                | [ o ] when ansiColorArgError.IsNone ->
                    match ansiColorMap o with
                    | Some m -> [ "TERM", m.Trim().Trim('\'', '"') ]
                    | None -> []
                | _ -> []

            let envForWith =
                WalkerArgs.envForWith (jenkinsProvided @ scmWrapperEnv @ ansiColorEnv) pipeline



            // FG-105: the cancellation model lives in WalkerCancellation.


            let alwaysFailFast = WalkerRules.alwaysFailFast pipeline
            // FG-105: step execution lives in WalkerStep.
            let runStepInner =
                WalkerStep.runStepInner runCtx envForWith workspace artifactRoot jobName

            // FG-105: when-evaluation lives in WalkerWhen.
            let evalWhen =
                WalkerWhen.evalWhen
                    (persistence |> Option.map (fun h -> h.IsRestartedRun) |> Option.defaultValue false)
                    envForWith

            let deadlineFromOptions = WalkerCancellation.deadlineFromOptions runCtx

            // FG-105: stage/post orchestration and wrapper dispatch live in
            // WalkerOrchestration; run() is the conductor wiring the units.
            let runStage, runPostWithDeadline =
                WalkerOrchestration.makeRunners
                    { RunCtx = runCtx
                      EnvForWith = envForWith
                      RunStepInner = runStepInner
                      EvalWhen = evalWhen
                      AlwaysFailFast = alwaysFailFast
                      SkipStagesAfterUnstable = skipAfterUnstable
                      WorkspaceRoot = workspaceRoot
                      ArtifactRoot = artifactRoot
                      JobName = jobName
                      Credentials = credentialStore
                      PreviousBuild = previousBuild
                      BuildNumber = buildNumber
                      Scm = scm
                      Persistence = persistence }

            // Pipeline-level `options { timeout(...) }` bounds the WHOLE build.
            let pipelineDeadline, pipelineDeclaredDeadline, pipelineOptionError = deadlineFromOptions pipeline.Options None

            match pipelineOptionError with
            | Some e ->
                emit $"ERROR: pipeline declares an unusable timeout option: {e}"
                root.Failed.Value <- true
                // COMPILE-shaped, like every other option refusal. This branch set
                // `root.Failed` alone, and the pipeline `post` guard tests
                // `compileRejected`, so `timeout(time: 1, unit: 'NOPE')` skipped the
                // stage and then RAN `post { always { ... } }`, creating files for a
                // Jenkinsfile Jenkins refuses to compile. FG-121 fixed precisely this
                // for the timestamps and ansiColor refusals and this one was left
                // behind — the same defect, in the same block, one branch down.
                compileRejected <- true
                bump BuildStatus.Failure
            | None -> ()

            // FG-102: the pipeline-level timeout announces its expiry ONCE, at the
            // point it is first observed to have aborted work — after the stages
            // when a stage died to it, or after the pipeline post when the post did
            // (`options-timeout-pipeline`, `options-timeout-wraps-post`).
            // FG-052. An SCM-defined job narrates its Jenkinsfile provenance
            // FIRST, then Declarative auto-inserts a checkout stage before any
            // user stage (measured — the stage annotations are excluded from
            // comparison; the checkout narration inside it is compared).
            let mutable exceededAnnounced = false

            // ONE announcement, after the pipeline post: the post runs under the
            // already-expired deadline and narrates its own cancellations first —
            // announcing before it reversed Jenkins' cluster order.
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
                    //
                    // EXCEPT after a COMPILE rejection, where saying it is being
                    // LOUDER than Jenkins: it refuses the model before any stage
                    // exists to skip, so it emits no such line and this one is pure
                    // invention. It was also half of the divergence on the
                    // unknown-option case, which FG-129 recorded as entirely the
                    // banner limit — a self-inflicted difference filed as somebody
                    // else's problem.
                    if not compileRejected then
                        emit $"Stage \"{stage.Name}\" skipped due to earlier failure(s)"
                elif skipAfterUnstable && runCtx.Status() = BuildStatus.Unstable then
                    // FG-053(b). `options { skipStagesAfterUnstable() }` stops the
                    // build at the first stage that went UNSTABLE, and says so with
                    // its OWN sentence — not the failure one. MEASURED on Jenkins
                    // 2.568.1 by running the same pipeline WITH and WITHOUT the
                    // option, which is what makes it a measurement of the option
                    // rather than of unstable handling generally:
                    //   with:    Stage "three" skipped due to earlier stage(s) marking the build as unstable
                    //   without: + echo three
                    // Both end `unstable`, both run pipeline `post`. The skipped
                    // stage's file is ABSENT from the workspace, so the hash checks
                    // the skip happened rather than was merely announced.
                    // Receipts: `options-skip-after-unstable`,
                    // `options-unstable-runs-on` (the control).
                    emit $"Stage \"{stage.Name}\" skipped due to earlier stage(s) marking the build as unstable"
                else
                    runStage root workspace pipelineDeadline stage

            // Pipeline-level `post` is selected against the BUILD result, so it
            // runs after every stage. Modelled as a synthetic stage carrying only
            // the post section, which is also how the pipeline's own environment
            // reaches those steps.
            if not (List.isEmpty pipeline.Post) && not compileRejected then
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
                runPostWithDeadline postRoot workspace synthetic (runCtx.Status()) previousBuild pipelineDeadline
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
                runCtx.OutputWithActiveSecrets()
                |> List.collect (fun (l, active) -> if List.isEmpty active then [] else Secrets.detectLeaks active l)
                |> List.map (fun leak -> leak.Variable)
                |> List.distinct

            if not (List.isEmpty leakedVars) then
                Result.Error(
                    $"""SECRET LEAKED to build output (variable(s): {String.concat ", " leakedVars}) — refusing to emit a trace"""
                )
            else

            let outputLines =
                let idReplacements =
                    runCtx.DurableIds()
                    |> Seq.map (fun i -> $"@tmp/durable-{i}/script.sh", "@tmp/durable-<id>/script.sh")
                    |> List.ofSeq

                Trace.normaliseOutputShaped
                    declaresTimestamps
                    false
                    ((workspace, "${WORKSPACE}") :: idReplacements)
                    envReplacements
                    (runCtx.Output())

            Result.Ok
                { Result = BuildStatus.toWireString (runCtx.Status())
                  EngineNotes = runCtx.EngineNotes()
                  Output = outputLines
                  WorkspaceHash = workspaceHash
                  WorkspaceFiles = files
                  Concurrent = pipeline.Stages |> Pipeline.flattenStages |> List.exists (fun st -> st.IsParallel)
                  Timestamps =
                      // same denominator as Jenkins.fs: the COMPARED output
                      // gated on the declaration, for the reason stated in Jenkins.fs
                      ((if declaresTimestamps then
                            runCtx.Output() |> Seq.filter Trace.hasTimestampPrefix |> Seq.length
                        else
                            0),
                       List.length outputLines)
                  ReportedFailureReason = Trace.reportedFailureReasonWhen declaresTimestamps (runCtx.Output()) }

    /// Run one Jenkinsfile as a fresh single build — the pre-FG-110 contract.
    let run (envReplacements: (string * string) list) (workspaceRoot: string) (jobName: string) (script: string) =
        try
            runWith envReplacements workspaceRoot jobName 1 None true None None script
        with ex ->
            Result.Error ex.Message

    /// FG-112. Run one build with durability hooks — the restart lane's entry.
    /// Same walker, same semantics; the hooks journal top-level steps.
    let runPersisted
        (envReplacements: (string * string) list)
        (workspaceRoot: string)
        (jobName: string)
        (freshWorkspace: bool)
        (hooks: PersistenceHooks)
        (script: string)
        =
        try
            // The journal key is (stage name, step index) — a flat map. Two
            // stages sharing a name (top-level, nested, or parallel branches)
            // would collide, and a collision records a never-run step as
            // durably done. REFUSED by name up front (FG-103), not keyed
            // around: unique names are the corpus norm and the stated limit.
            match Fogell.Pipeline.Parser.Parser.parse script with
            | Result.Error _ -> () // the run itself reports parse errors
            | Ok p ->
                let dupes =
                    p.Stages
                    |> Pipeline.flattenStages
                    |> List.countBy (fun st -> st.Name)
                    |> List.filter (fun (_, n) -> n > 1)
                    |> List.map fst

                if not (List.isEmpty dupes) then
                    failwith (
                        "persisted runs require globally unique stage names; duplicated: "
                        + String.concat ", " dupes
                    )

                // the journal's wire format delimits with tabs and newlines; a
                // stage name carrying either would TEAR the record and truncate
                // every later read — refused by name, not escaped around
                let unsafe =
                    p.Stages
                    |> Pipeline.flattenStages
                    |> List.filter (fun st ->
                        st.Name.Contains '\t' || st.Name.Contains '\n' || st.Name.Contains '\r')
                    |> List.map (fun st ->
                        st.Name.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"))

                if not (List.isEmpty unsafe) then
                    failwith (
                        "persisted runs cannot journal stage names containing tabs, newlines, or carriage returns: "
                        + String.concat ", " unsafe
                    )

            runWith envReplacements workspaceRoot jobName 1 None freshWorkspace None (Some hooks) script
        with ex ->
            Result.Error ex.Message

    /// FG-052. Run one build of an SCM-DEFINED job: the script is what the
    /// harness pushed to the SCM (the same bytes Jenkins obtains), and the spec
    /// is what `checkout scm` checks out.
    let runScm
        (envReplacements: (string * string) list)
        (workspaceRoot: string)
        (jobName: string)
        (scm: ScmSpec)
        (script: string)
        =
        try
            runWith envReplacements workspaceRoot jobName 1 None true (Some scm) None script
        with ex ->
            Result.Error ex.Message

    /// FG-110. Run a SEQUENCE of builds of one job: the workspace persists
    /// across builds (build 1 starts clean) and each build's terminal result is
    /// the next build's `previous` — what `changed`/`fixed`/`regression` select
    /// against. Fail closed: a build the harness could not run at all stops the
    /// sequence, and every later build reports that error rather than running
    /// against an invented history.
    let runMany
        (envReplacements: (string * string) list)
        (workspaceRoot: string)
        (jobName: string)
        (scripts: string list)
        : Result<Trace, string> list =
        // FG-103: an unmappable terminal result must HALT the sequence by name,
        // never quietly become None — None means "no history" (build-#1
        // semantics: changed fires, fixed/regression cannot), which is exactly
        // the wrong thing to invent for build k+1.
        let statusOf (t: Trace) : Result<BuildStatus, string> =
            match t.Result with
            | "success" -> Ok BuildStatus.Success
            | "failure" -> Ok BuildStatus.Failure
            | "unstable" -> Ok BuildStatus.Unstable
            | "aborted" -> Ok BuildStatus.Aborted
            | other -> Result.Error $"build produced a result this sequence cannot carry forward: '{other}'"

        scripts
        |> List.fold
            (fun (acc, previous, halted) script ->
                match halted with
                | Some why -> (Result.Error $"sequence halted: {why}" :: acc, previous, halted)
                | None ->
                    let r =
                        try
                            runWith
                                envReplacements
                                workspaceRoot
                                jobName
                                (List.length acc + 1)
                                previous
                                (List.isEmpty acc)
                                None
                                None
                                script
                        with ex ->
                            Result.Error ex.Message

                    match r with
                    | Result.Ok t ->
                        match statusOf t with
                        | Ok st -> (r :: acc, Some st, None)
                        | Result.Error why -> (r :: acc, previous, Some why)
                    | Result.Error why -> (r :: acc, previous, Some $"a prior build failed to run ({why})"))
            ([], None, None)
        |> fun (acc, _, _) -> List.rev acc
