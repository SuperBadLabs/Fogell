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
            let timestampsOption =
                pipeline.Options |> List.tryFind (fun o -> o.Name = "timestamps")

            let timestampsArgError =
                match timestampsOption with
                | Some o when not (List.isEmpty o.Positional) || not (List.isEmpty o.Named) ->
                    Some "the timestamps() option takes no arguments"
                | _ -> None

            let declaresTimestamps = timestampsOption.IsSome && timestampsArgError.IsNone

            // STAGE-LEVEL `options { timestamps() }` is REFUSED, not ignored.
            // Jenkins 2.568.1 honours it and stamps that stage's output; Fogell
            // enables the wrapper for the whole build or not at all, so honouring
            // the pipeline form while silently dropping the stage form would
            // produce unstamped output where Jenkins stamps — a divergence the
            // engine would not announce. Refusing by name is this project's
            // stated direction for a construct it does not implement (FG-103),
            // and FG-120 carries the scoped enable/restore that would support it.
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
                  Secrets = []
                  SiblingFailedAt = ref -1L
                  // set per top-level step by runStageBody; nothing outside a
                  // stage's step list is journaled, so the root carries none
                  DurabilityKey = None
                  HumanRejected = ref false }

            let mutable scmWrapperEnv: (string * string) list = []

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

            // FAIL CLOSED on `timestamps(false)` or named arguments, BEFORE the
            // SCM block. Jenkins rejects the zero-argument option's other forms
            // when it compiles the Declarative model — after the lightweight
            // Jenkinsfile fetch and BEFORE its generated default checkout — so
            // validating after `runCheckout` left repository files in the
            // workspace and turned a compile refusal into a workspace-hash
            // divergence. Running the build at all would be failing OPEN on a
            // script the reference engine will not execute.
            if stageTimestamps then
                emit
                    "ERROR: stage-level options { timestamps() } is not implemented; Jenkins stamps that stage's output and this engine would not — refusing rather than diverging silently (FG-120)"

                root.Failed.Value <- true
                bump BuildStatus.Failure

            match timestampsArgError with
            | Some e ->
                emit $"ERROR: pipeline declares an unusable timestamps option: {e}"
                root.Failed.Value <- true
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

                // The lane's core invariant, FAIL-CLOSED for EVERY SCM case
                // (a check that rode along with the auto-checkout vanished
                // exactly when skipDefaultCheckout did): the bytes this engine
                // was handed must be the bytes the SCM serves, because Jenkins
                // executes the latter. Read them from the SCM itself.
                match WalkerGit.readRemoteJenkinsfile spec.Url spec.Branch with
                | Result.Error e ->
                    failwith $"SCM case verification unavailable ({e}) — refusing to seal against unverified bytes"
                // the subprocess reader trims trailing whitespace, so compare
                // TRIMMED on both sides (a trailing newline is not a different
                // script) with line endings normalised
                | Ok remote when remote.Replace("\r\n", "\n").Trim() <> script.Replace("\r\n", "\n").Trim() ->
                    failwith (
                        "SCM case drift: the local case body does not match the SCM's Jenkinsfile — "
                        + "sync the fixture repo (scripts/sync-scm-cases.bb) before sealing"
                    )
                | Ok _ -> ()

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
            let envForWith =
                WalkerArgs.envForWith (jenkinsProvided @ scmWrapperEnv) pipeline



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
                    emit $"Stage \"{stage.Name}\" skipped due to earlier failure(s)"
                else
                    runStage root workspace pipelineDeadline stage

            // Pipeline-level `post` is selected against the BUILD result, so it
            // runs after every stage. Modelled as a synthetic stage carrying only
            // the post section, which is also how the pipeline's own environment
            // reaches those steps.
            if not (List.isEmpty pipeline.Post) then
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
