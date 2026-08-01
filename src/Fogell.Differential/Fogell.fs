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
        (previousBuild: BuildStatus option)
        (freshWorkspace: bool)
        (script: string)
        : Result<Trace, string> =
        match Fogell.Pipeline.Parser.Parser.parse script with
        | Result.Error e -> Result.Error $"{Fogell.Admission.ErrorCode.toWireString e.Code} at {e.Position}: {e.Message}"
        | Result.Ok pipeline ->
            // FG-105: the run-scoped mutable state lives in WalkerCtx — one record,
            // one stated contract (see WalkerCtx.fs for its two-lock discipline).
            // These rebinds keep call sites unchanged.
            let runCtx = WalkerCtx.create ()
            let emit = runCtx.Emit
            let deadlineDidFire = runCtx.DeadlineDidFire

            let workspace = Path.Combine(workspaceRoot, jobName)
            // artifacts live OUTSIDE the workspace so archiving cannot perturb
            // the workspace hash the differential compares
            let artifactRoot = Path.Combine(workspaceRoot, "_artifacts")
            // FG-110: only a sequence's FIRST build starts clean — Jenkins does
            // not wipe the workspace between builds of one job, and neither do we.
            if freshWorkspace && Directory.Exists workspace then
                Directory.Delete(workspace, true)

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
                [ "BUILD_NUMBER", "1"
                  "BUILD_ID", "1"
                  "BUILD_DISPLAY_NAME", "#1"
                  "JOB_NAME", jobName
                  "JOB_BASE_NAME", jobName
                  "WORKSPACE", Path.Combine(workspaceRoot, jobName)
                  "EXECUTOR_NUMBER", "0"
                  "NODE_NAME", "built-in" ]

            // FG-105: env resolution and argument rendering live in WalkerArgs.
            let envForWith = WalkerArgs.envForWith jenkinsProvided pipeline

            let bump = runCtx.Bump


            // FG-105: the cancellation model lives in WalkerCancellation.


            let alwaysFailFast = WalkerRules.alwaysFailFast pipeline
            // FG-105: step execution lives in WalkerStep.
            let runStepInner =
                WalkerStep.runStepInner runCtx envForWith workspace artifactRoot jobName

            // FG-105: when-evaluation lives in WalkerWhen.
            let evalWhen = WalkerWhen.evalWhen envForWith

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
                      PreviousBuild = previousBuild }

            let root =
                { Interrupt = None
                  Failed = ref false
                  Sink = bump
                  EnvOverlay = []
                  Secrets = []
                  SiblingFailedAt = ref -1L }

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

            Result.Ok
                { Result = BuildStatus.toWireString (runCtx.Status())
                  EngineNotes = runCtx.EngineNotes()
                  Output =
                    (let idReplacements =
                        runCtx.DurableIds()
                        |> Seq.map (fun i -> $"@tmp/durable-{i}/script.sh", "@tmp/durable-<id>/script.sh")
                        |> List.ofSeq

                     Trace.normaliseOutputShaped
                         false
                         ((workspace, "${WORKSPACE}") :: idReplacements)
                         envReplacements
                         (runCtx.Output()))
                  WorkspaceHash = workspaceHash
                  WorkspaceFiles = files
                  Concurrent = pipeline.Stages |> Pipeline.flattenStages |> List.exists (fun st -> st.IsParallel)
                  ReportedFailureReason = Trace.reportedFailureReason (runCtx.Output()) }

    /// Run one Jenkinsfile as a fresh single build — the pre-FG-110 contract.
    let run (envReplacements: (string * string) list) (workspaceRoot: string) (jobName: string) (script: string) =
        runWith envReplacements workspaceRoot jobName None true script

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
        let statusOf (t: Trace) =
            match t.Result with
            | "success" -> Some BuildStatus.Success
            | "failure" -> Some BuildStatus.Failure
            | "unstable" -> Some BuildStatus.Unstable
            | "aborted" -> Some BuildStatus.Aborted
            | _ -> None

        scripts
        |> List.fold
            (fun (acc, previous, halted) script ->
                if halted then
                    (Result.Error "a prior build in this sequence failed to run" :: acc, previous, true)
                else
                    let r =
                        runWith envReplacements workspaceRoot jobName previous (List.isEmpty acc) script

                    match r with
                    | Result.Ok t -> (r :: acc, statusOf t, false)
                    | Result.Error _ -> (r :: acc, previous, true))
            ([], None, false)
        |> fun (acc, _, _) -> List.rev acc
