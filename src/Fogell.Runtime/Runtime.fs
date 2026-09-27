namespace Fogell.Runtime

open System
open System.IO
open System.Text
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir

type OutputPublicationException(message: string, inner: exn) = inherit Exception(message, inner)
type BuildOutputLimitExceededException() = inherit Exception("OUTPUT_LIMIT_EXCEEDED")
type PersistenceHooks =
    { OnOutput: string -> unit
      OnDiagnostic: ExecutionDiagnostic -> unit
      SkippedStatus: string -> int -> BuildStatus option
      OnStepStarted: string -> int -> string -> unit
      OnStepFinished: string -> int -> BuildStatus -> string option -> unit
      OnStageCommitted: string -> unit }
type RunResult = { Result: string }

module Runtime =
    let preflight source =
        Fogell.Pipeline.Parser.Parser.parse source
        |> Result.mapError (Fogell.Admission.AdmissionError.render source)

    let agentHome root jobName buildNumber =
        let key = jobName + "\000" + string buildNumber
        let hash = Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes key) |> Convert.ToHexStringLower
        Path.Combine(root, "_agent_home", hash)

    /// Run only an admitted native pipeline. Publication failures leave durable
    /// execution incomplete; they must never become an ordinary workload result.
    let runPersisted workspaceRoot jobName buildNumber (hooks: PersistenceHooks) source =
        match preflight source with
        | Error why -> Error why
        | Ok pipeline ->
            let workspace = Path.Combine(workspaceRoot, jobName)
            let home = agentHome workspaceRoot jobName buildNumber
            Directory.CreateDirectory(Path.Combine(home, "tmp")) |> ignore
            let limits = ArtifactPolicy.loadEnvironment() |> Result.defaultWith invalidOp
            let store = ArtifactStore.underWithLimits (Path.Combine(workspaceRoot, "_artifacts")) limits
            let gate = obj()
            let mutable bytes, records, overflow = 0L, 0, false
            let publish (line: string) =
                lock gate (fun () ->
                    let size = int64 (Encoding.UTF8.GetByteCount line)
                    if overflow || bytes + size > 32L * 1024L * 1024L || records >= 100000 then
                        overflow <- true
                        raise (BuildOutputLimitExceededException())
                    bytes <- bytes + size
                    records <- records + 1
                    try hooks.OnOutput line
                    with error -> raise (OutputPublicationException("output publication failed", error)))
            let diagnostic value =
                try hooks.OnDiagnostic (ExecutionDiagnostic.sanitize id value)
                with error -> raise (OutputPublicationException("diagnostic publication failed", error))
            let mutable status = BuildStatus.Success
            try
                for stage in pipeline.Stages do
                    for index, step in List.indexed stage.Steps do
                        match hooks.SkippedStatus stage.Name index with
                        | Some completed -> status <- BuildStatus.worstOf status completed
                        | None when step.Always || status = BuildStatus.Success || status = BuildStatus.Unstable ->
                            let name, script, named =
                                match step.Operation with
                                | Run command -> "run", Some command, []
                                | Echo message -> "echo", Some message, []
                                | Archive patterns -> "archive", None, ["artifacts", patterns]
                                | TestReport patterns -> "test_report", None, ["pattern", patterns]
                            hooks.OnStepStarted stage.Name index name
                            let cwd = Workspace.resolveUnder workspace step.WorkingDirectory |> Result.defaultWith (fun e -> invalidOp e.Describe)
                            if not (Directory.Exists cwd) then invalidOp "working directory does not exist"
                            let clock = Diagnostics.Stopwatch.StartNew()
                            let request: StepRequest =
                                { OnDiagnostic = Some (fun value -> diagnostic {value with Stage=stage.Name; Step=name})
                                  Name=name; Script=script; Workspace=cwd; WorkspaceRoot=Some workspace
                                  Environment=LaunchEnvironment.buildBaseline home @ ["WORKSPACE",workspace; "BUILD_NUMBER",string buildNumber] @ pipeline.Environment @ step.Environment
                                  TimeoutMs=Some(int64 step.TimeoutSeconds * 1000L)
                                  CaptureStdout=false; ReportSkipBuildWarning=false; ReportAllowEmpty=false
                                  ReportNotBefore=None; ReportSkipStageWarning=false
                                  OnLine=Some publish; OnGeneratedLine=None; OnRedactedLine=None; OnRedactedOutput=None
                                  OnRedactedAdmission=None; CreateRedactedAdmission=None; ReserveCapturedOutput=None
                                  Named=named; Artifacts=Some store; InterruptBeatsDeadline=None; Interrupt=None
                                  DeadlineExpired=Some(fun () -> clock.ElapsedMilliseconds >= int64 step.TimeoutSeconds * 1000L)
                                  Secrets=[]; MaskingSecrets=None; MaskingSecretsLock=None; BuildKey=jobName }
                            let result = Executor.runStep request
                            if overflow then raise (BuildOutputLimitExceededException())
                            let code = BuildStatus.toWireString result.Status
                            if result.Status <> BuildStatus.Success then
                                let message = result.Diagnostic |> Option.defaultValue "step failed"
                                diagnostic {ExecutionDiagnostic.create "workload_step" code message with
                                                Stage=stage.Name; Step=name; ExitCode=(result.ExitCode |> Option.map Nullable |> Option.defaultValue (Nullable()))}
                            // The diagnostic stream records test failures even when
                            // the build remains unstable. Journal StepReason is only
                            // a failure/abort explanation, never an unstable one.
                            let failureReason =
                                match result.Status with
                                | BuildStatus.Failure
                                | BuildStatus.Aborted -> result.Diagnostic
                                | _ -> None
                            hooks.OnStepFinished stage.Name index result.Status failureReason
                            status <- BuildStatus.worstOf status result.Status
                        | None -> ()
                    hooks.OnStageCommitted stage.Name
                Ok { Result=BuildStatus.toWireString status }
            with
            | :? OutputPublicationException -> reraise()
            | error ->
                let reason =
                    match error with
                    | :? BuildOutputLimitExceededException
                    | :? OutputLimitExceededException -> "OUTPUT_LIMIT_EXCEEDED"
                    | :? ArtifactLimitExceededException -> "ARTIFACT_LIMIT_EXCEEDED"
                    | _ -> "RUN_FAILED"
                diagnostic (ExecutionDiagnostic.create "infrastructure" "failure" reason)
                Error reason
