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
      TimeoutMs: int64 option
      OnLine: (string -> unit) option
      /// Named arguments as written (`artifacts:`, `testResults:`, `pattern:`).
      Named: (string * string) list
      /// Where publishing steps write. None disables them by failing closed.
      Artifacts: ArtifactStore option
      /// FG-036. Polled while a shell step runs; true interrupts it. Used by
      /// `parallel(failFast: true)` to stop siblings once one branch has failed.
      Interrupt: (unit -> bool) option
      /// FG-071. Secret bindings live for this step. Output is masked against
      /// them ON THE WAY OUT — including the streaming path.
      ///
      /// REVIEW FIX (Codex P1, PR #11): the masker and leak detector existed but
      /// nothing called them, so a step running `cat "$TOKEN_FILE"` streamed the
      /// literal secret while the board claimed masking was done. A capability
      /// reachable only from its own tests is not a capability.
      Secrets: SecretBinding list
      /// Identifies this build in the artifact store.
      BuildKey: string }

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
      /// Relative paths published by `archiveArtifacts`, in sorted order.
      Archived: string list
      /// Test totals parsed by `junit`: total, failed, skipped.
      TestTotals: (int * int * int) option
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
          Archived = []
          TestTotals = None
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
            // Mask on the way out, streaming path included: a secret that reaches
            // the console before the run ends has already leaked.
            let maskText (t: string) =
                if List.isEmpty request.Secrets then t else Secrets.mask request.Secrets t

            let leakReports = System.Collections.Generic.List<string>()

            let onLine =
                match request.OnLine with
                | None -> None
                | Some f ->
                    Some(fun (line: string) ->
                        let masked = maskText line

                        // Detection runs on the MASKED text: anything still
                        // recognisable is an encoding masking cannot cover, and
                        // naming it is the whole point of FG-071.
                        for leak in Secrets.detectLeaks request.Secrets masked do
                            let note = $"WARNING: {leak.Variable} appears in output {leak.Encoding}-encoded; masking cannot cover this form"

                            if not (leakReports.Contains note) then
                                leakReports.Add note
                                f note

                        f masked)

            let runResult =
                ProcessGroup.run
                    { RunRequest.create (script, request.Workspace) with
                        Interrupt = request.Interrupt
                        Environment = request.Environment
                        TimeoutMs = request.TimeoutMs
                        OnLine = onLine }

            // REVIEW FIX (Codex, PR #13): detection lived only inside the stdout
            // streaming callback, so a step with no OnLine — or one that wrote a
            // transformed secret to STDERR — returned it with none of the warnings
            // FG-071 promises. Reverse/hex/char-split forms survive masking BY
            // DESIGN, so the warning is the entire guarantee, and it has to cover
            // every path out of the step.
            let maskedStdout = maskText runResult.Stdout
            let maskedStderr = maskText runResult.Stderr

            let bufferedLeaks =
                [ maskedStdout; maskedStderr ]
                |> List.collect (Secrets.detectLeaks request.Secrets)
                |> List.map (fun l ->
                    $"WARNING: {l.Variable} appears in output {l.Encoding}-encoded; masking cannot cover this form")
                |> List.distinct
                |> List.filter (fun note -> not (leakReports.Contains note))

            for note in bufferedLeaks do
                leakReports.Add note
                request.OnLine |> Option.iter (fun f -> f note)

            let run = runResult

            let signalName =
                function
                | 1 -> "SIGHUP"
                | 2 -> "SIGINT"
                | 9 -> "SIGKILL"
                | 15 -> "SIGTERM"
                | n -> $"signal {n}"

            let status, exitCode, diagnostic =
                match run.Outcome with
                | Completed 0 -> Success, Some 0, None
                // FG-033: name the signal and say who did not do it, so the
                // operator is not left guessing at an opaque code.
                //
                // REVIEW FIX (Codex P2 + Copilot, PR #11): this asserted the
                // termination came from outside as a CERTAINTY. It cannot. The
                // exit code is 128+N, and a script that runs `exit 137` produces
                // exactly what SIGKILL produces — `setsid --wait` has already
                // collapsed the wait status by then. Saying "likely" costs the
                // operator nothing and stops the engine from claiming knowledge
                // it does not have. The exit code is now reported alongside, so
                // the ambiguity is resolvable by whoever wrote the script.
                | Signalled s ->
                    Failure,
                    Some(128 + s),
                    Some
                        $"step process most likely terminated by {signalName s} from outside the engine \
                          (not a Fogell timeout or cancellation), or exited {128 + s} deliberately — \
                          these are indistinguishable in a wrapped exit status; if it was a signal, its effect may be incomplete"
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
              // Buffered output is masked too — a caller that reads Stdout
              // instead of streaming must not get a different secrecy guarantee.
              Stdout = maskedStdout
              // A caller with no OnLine still has to SEE the warning, or FG-071's
              // "never silent" promise depends on how the caller chose to read.
              Stderr =
                if List.isEmpty bufferedLeaks then
                    maskedStderr
                else
                    maskedStderr + String.concat "\n" bufferedLeaks + "\n"
              DurationMs = run.DurationMs
              ProcessGroupId = run.ProcessGroupId
              Termination = run.Termination
              Archived = []
              TestTotals = None
              Diagnostic = diagnostic }

    /// Read a step argument that may be positional or named, matching Jenkins'
    /// tolerance for `archiveArtifacts '*.jar'` and
    /// `archiveArtifacts artifacts: '*.jar'`.
    let private argument (request: StepRequest) (names: string list) =
        match request.Script with
        | Some v when v <> "" -> Some v
        | _ -> names |> List.tryPick (fun n -> request.Named |> List.tryPick (fun (k, v) -> if k = n then Some v else None))

    /// Jenkins accepts a comma-separated glob list in one string.
    let private patterns (raw: string) =
        raw.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.filter (fun s -> s <> "") |> Array.toList

    /// FG-042. `archiveArtifacts artifacts: '<glob>' [, allowEmptyArchive: true]`
    let private runArchive (request: StepRequest) : StepResult =
        match request.Artifacts, argument request [ "artifacts" ] with
        | None, _ ->
            { ok Failure with
                Diagnostic = Some "archiveArtifacts requires an artifact store; none configured" }
        | _, None ->
            { ok Failure with
                Diagnostic = Some "archiveArtifacts requires an 'artifacts' pattern" }
        | Some store, Some raw ->
            // Jenkins prints this banner before archiving. Emitting the same line
            // is parity; excluding it from comparison would merely hide a
            // difference the user can see.
            request.OnLine |> Option.iter (fun f -> f "Archiving artifacts")

            let abort () =
                match request.Interrupt with
                | Some p -> (try p () with _ -> false)
                | None -> false

            let published, aborted =
                Publish.archiveWithAbort store request.BuildKey request.Workspace (patterns raw) abort

            let allowEmpty =
                request.Named
                |> List.exists (fun (k, v) -> k = "allowEmptyArchive" && v.Trim().ToLowerInvariant() = "true")

            if aborted then
                // REVIEW FIX (Codex, PR #14 round 3): the previous version LOGGED the
                // abort and then fell through to a Success result, so an explicitly
                // incomplete artifact set left the build green and later stages ran.
                // That is the same "partial result reported as success" shape as the
                // parallel-sink and post-arm bugs. Detecting an interruption and
                // discarding it from the StepResult is worse than not detecting it.
                request.OnLine
                |> Option.iter (fun f ->
                    f $"ERROR: archiving aborted after {published.Length} file(s) — the step's deadline expired; the artifact set is INCOMPLETE")

                { ok Aborted with
                    Diagnostic =
                        Some
                            $"archiving aborted after {published.Length} file(s); the deadline expired and the artifact set is incomplete" }
            elif List.isEmpty published && not allowEmpty then
                // Jenkins fails the build here rather than passing quietly, and
                // a silent empty archive is the worst outcome for a user.
                { ok Failure with
                    Diagnostic = Some $"No artifacts found that match the file pattern \"{raw}\"" }
            else
                { ok Success with Archived = published }

    /// FG-043. `junit '<glob>'` / `junit testResults: '<glob>'`
    let private runJUnit (request: StepRequest) : StepResult =
        match argument request [ "testResults"; "pattern" ] with
        | None ->
            { ok Failure with
                Diagnostic = Some "junit requires a 'testResults' pattern" }
        | Some raw ->
            request.OnLine |> Option.iter (fun f -> f "Recording test results")

            match Publish.parseJUnit request.Workspace (patterns raw) with
            | Result.Error e -> { ok Failure with Diagnostic = Some e }
            | Result.Ok(total, failed, skipped) ->
                // Jenkins marks the build UNSTABLE (not failed) when tests fail:
                // the build worked, the code did not.
                let status = if failed > 0 then Unstable else Success

                { ok status with
                    TestTotals = Some(total, failed, skipped)
                    Diagnostic =
                        if failed > 0 then
                            Some $"{failed} of {total} test(s) failed"
                        else
                            None }

    /// Dispatch a requested effect. Steps Fogell does not implement fail closed
    /// with a named reason — never a silent success (ADR 0001).
    let runStep (request: StepRequest) : StepResult =
        match request.Name, request.Script with
        | "archiveArtifacts", _ -> runArchive request
        | "junit", _ -> runJUnit request
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
