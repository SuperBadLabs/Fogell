namespace Fogell.Execution

open Fogell.Domain

/// FG-040. Executes a step and maps its outcome onto a build status.
///
/// This is the layer the interpreter's *requested effects* land in. The
/// interpreter decides what should happen; nothing until here actually does it.
type StepRequest =
    { Name: string
      Script: string option
      /// An ALREADY-CREATED directory the step runs in.
      ///
      /// Steps within one attempt share a workspace — step 2 reads what step 1
      /// wrote — so workspace creation belongs to the attempt, not the step.
      /// An earlier version had each step call Workspace.createFresh, which
      /// correctly refused the existing directory and failed every `sh` step.
      /// Use [Workspace.createFresh] once per attempt, then pass it here.
      Workspace: string
      Environment: (string * string) list
      TimeoutMs: int option
      OnLine: (string -> unit) option }

type StepResult =
    { Status: BuildStatus
      ExitCode: int option
      Stdout: string
      Stderr: string
      DurationMs: int64
      /// The step's process-group id, so callers can assert on containment.
      ProcessGroupId: int option
      /// Populated for shell steps so callers can assert on containment.
      Termination: Termination option
      Diagnostic: string option }

module Executor =

    let private ok status =
        { Status = status
          ExitCode = None
          Stdout = ""
          Stderr = ""
          DurationMs = 0L
          ProcessGroupId = None
          Termination = None
          Diagnostic = None }

    /// Run a `sh`-shaped step. Exit code maps to status, and the diagnostic
    /// names *why* on any non-success — never a bare code.
    ///
    /// Beating Jenkins here (JB-DUR-005): when a step's process disappears,
    /// Jenkins takes ~10 minutes to conclude anything and then reports
    /// `exit code -1` with no mention of a restart. Fogell owns the process, so
    /// the diagnostic always says what happened.
    let runShell (request: StepRequest) (script: string) : StepResult =
        if not (System.IO.Directory.Exists request.Workspace) then
            { ok Failure with
                Diagnostic = Some $"workspace '{request.Workspace}' does not exist; create it once per attempt" }
        else
            let run =
                ProcessGroup.run
                    { RunRequest.create (script, request.Workspace) with
                        Environment = request.Environment
                        TimeoutMs = request.TimeoutMs
                        OnLine = request.OnLine }

            let status, exitCode, diagnostic =
                match run.Outcome with
                | Completed 0 -> Success, Some 0, None
                | Completed code ->
                    Failure,
                    Some code,
                    Some $"script returned exit code {code}"
                | TimedOut ->
                    let budget = defaultArg request.TimeoutMs 0

                    Aborted,
                    None,
                    Some(
                        let t = run.Termination

                        let how =
                            match t with
                            | Some x when x.Escalated -> "SIGTERM was not honoured within the grace period, so the process group was killed"
                            | Some _ -> "the process group exited on SIGTERM"
                            | None -> "the process group could not be signalled"

                        $"step exceeded its {budget} ms timeout; {how}")
                | Cancelled -> Aborted, None, Some "step was cancelled"

            // FG-032: a leak is a defect, and it is reported rather than ignored.
            let diagnostic =
                match run.Termination with
                | Some t when t.LeakedProcesses > 0 ->
                    let leak = $"{t.LeakedProcesses} process(es) survived group reaping"

                    Some(
                        match diagnostic with
                        | Some d -> $"{d}; {leak}"
                        | None -> leak)
                | Some _
                | None -> diagnostic

            { Status = status
              ExitCode = exitCode
              Stdout = run.Stdout
              Stderr = run.Stderr
              DurationMs = run.DurationMs
              ProcessGroupId = run.ProcessGroupId
              Termination = run.Termination
              Diagnostic = diagnostic }

    /// Dispatch a requested effect. Steps Fogell does not implement fail closed
    /// with a named reason — never a silent success (ADR 0001).
    let runStep (request: StepRequest) : StepResult =
        match request.Name, request.Script with
        | ("sh" | "bat"), Some script -> runShell request script
        | ("sh" | "bat"), None ->
            { ok Failure with
                Diagnostic = Some $"step '{request.Name}' requires a script argument" }
        | "echo", Some message ->
            request.OnLine |> Option.iter (fun f -> f message)

            { ok Success with Stdout = message + "\n" }
        | "echo", None -> { ok Success with Stdout = "\n" }
        | name, _ ->
            { ok Failure with
                Diagnostic = Some $"step '{name}' is not implemented; unsupported behaviour fails closed" }
