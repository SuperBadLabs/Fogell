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
      /// FG-174. `sh(returnStdout: true)` captures stdout rather than printing it.
      /// The trace on stderr still streams, which is what Jenkins does.
      CaptureStdout: bool
      /// FG-177. `junit(skipMarkingBuildUnstable: true)` leaves a build successful
      /// when parsed reports contain failures. The walker resolves this from the
      /// typed/literal argument before dispatch; the executor never guesses a
      /// boolean from the rendered `Named` strings.
      JUnitSkipMarkingBuildUnstable: bool
      /// `junit(allowEmptyResults: true)` permits either no matching reports or
      /// a matched aggregate containing no recognized result. The walker owns
      /// the typed boolean boundary and supplies the decision before scanning.
      JUnitAllowEmptyResults: bool
      /// FG-220. When present, `skipOldReports: true` filters against this
      /// build-start Unix-millisecond origin. None is the default/explicit-false
      /// path and performs no timestamp read.
      JUnitSkipOldReportsSince: int64 option
      /// FG-177. `junit(skipMarkingStageUnstable: true)` suppresses the
      /// pipeline-node/stage UNSTABLE decoration independently of the returned
      /// summary. As with the build flag, the walker supplies a typed decision;
      /// this layer never reconstructs a boolean from rendered argument text.
      JUnitSkipMarkingStageUnstable: bool
      /// The WORKSPACE root (not the step's cwd): durable-task roots its script
      /// scaffolding at the workspace's @tmp sibling even inside `dir()`, and the
      /// executed script's $0 is observable.
      WorkspaceRoot: string option
      /// Historical public callback. Process bytes are decoded only after raw
      /// masking; executor-generated shell narration is masked before delivery.
      OnLine: (string -> unit) option
      /// Ordinary executor-generated output callback for a caller such as the
      /// Walker which already owns run-wide publication masking.
      OnGeneratedLine: (string -> unit) option
      /// Shell stdout/stderr already canonicalized by the raw matcher. When
      /// absent, direct callers retain the historical [OnLine] callback.
      OnRedactedLine: (string -> unit) option
      /// Provenance-preserving shell callback. Walker uses this instead of
      /// inferring tokens from visible four-star runs.
      OnRedactedOutput: (RedactedText -> unit) option
      /// Synchronous trace admission used only when the caller can defer its
      /// external transport. It runs under the registration/matcher lock.
      OnRedactedAdmission: (RedactedText -> unit) option
      /// Factory form of synchronous admission. ProcessGroup invokes it once
      /// for stdout and once for stderr so late publication remasking retains
      /// the same independent-stream identity as the raw matchers.
      CreateRedactedAdmission: (unit -> RedactedAdmission) option
      /// Named arguments as written (`artifacts:`, `testResults:`, `pattern:`).
      Named: (string * string) list
      /// Where publishing steps write. None disables them by failing closed.
      Artifacts: ArtifactStore option
      /// FG-036. Polled while a shell step runs; true interrupts it. Used by
      /// `parallel(failFast: true)` to stop siblings once one branch has failed.
      ///
      /// Breaks the both-in-one-poll tie between [TimeoutMs] and [Interrupt]; see
      /// [RunRequest.InterruptBeatsDeadline].
      InterruptBeatsDeadline: (unit -> bool) option
      /// EXTERNAL cancellation only. A `timeout` deadline travels in [TimeoutMs]
      /// and, for steps that do their own work, in [DeadlineExpired].
      ///
      /// REVIEW FIX (Codex, PR #14 round 6): folding the deadline into this
      /// predicate made `ProcessGroup.run` classify an expired timeout as
      /// `Cancelled`, so the diagnostic read "step was cancelled" instead of naming
      /// the timeout — losing the distinction FG-033 exists to preserve.
      Interrupt: (unit -> bool) option
      /// Polled by steps that perform their own work (archive, junit) so a
      /// `timeout` bounds them too. Kept separate from [Interrupt] so the reported
      /// CAUSE stays correct.
      DeadlineExpired: (unit -> bool) option
      /// FG-071. Lexically active bindings for this step.
      ///
      /// REVIEW FIX (Codex P1, PR #11): the masker and leak detector existed but
      /// nothing called them, so a step running `cat "$TOKEN_FILE"` streamed the
      /// literal secret while the board claimed masking was done. A capability
      /// reachable only from its own tests is not a capability.
      Secrets: SecretBinding list
      /// FG-236. Optional monotonic run-wide inventory for raw shell output.
      /// A provider keeps a shell live to credentials registered later by a
      /// parallel branch; direct executor callers fall back to [Secrets].
      MaskingSecrets: (unit -> SecretBinding list) option
      /// Optional lock shared with [MaskingSecrets] registration. When present,
      /// raw matching, framing, and callback admission linearize under it.
      MaskingSecretsLock: obj option
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
      /// JUnit's accumulated test duration in seconds, preserving JVM float
      /// width and per-addition rounding. Distinct from wall-clock DurationMs.
      TestDuration: single option
      /// A warning contribution attached to the current pipeline stage rather
      /// than folded into the build result. JUnit is the first producer: failed
      /// reports contribute UNSTABLE unless `skipMarkingStageUnstable` was set.
      /// Failure/abort arms never decorate a stage through this channel.
      StageWarning: BuildStatus option
      /// FG-174. The captured stdout UNMASKED, and ONLY when `CaptureStdout` asked for
      /// it — None on every other step, so the raw text exists nowhere it was not
      /// requested.
      ///
      /// `Stdout` above is MASKED, which is right for everything that prints or is
      /// compared, and wrong for the one consumer that is not printing: a value handed
      /// back to the pipeline. MEASURED, and held by receipt `credentials-returnstdout` —
      /// `def t = sh(script: 'printf %s "$TOKEN"', returnStdout: true)` gives Jenkins
      /// `t.length() == 12` and gave Fogell `4`, because `****` is what it captured. A
      /// pipeline that captures a credential and passes it to the next command therefore
      /// authenticated with the mask. Raised in review on PR #53.
      ///
      /// THIS IS NOT A HOLE IN MASKING. Jenkins masks the LOG, not the value, and every
      /// path out of the engine still masks: `echo` masks its message (FG-044b), a shell
      /// argument carrying it is masked on the way to the console, and the receipt only
      /// ever sees `Stdout`. What changes is that the interpreter's own variable holds
      /// what the program actually wrote, which is the only thing that makes
      /// `returnStdout` usable with a credential at all.
      CapturedStdoutRaw: string option
      Diagnostic: string option
      /// FG-103: the engine reporting on its OWN checks — a leak scan that could
      /// not run, survivors it found — separate from the step's failure reason so
      /// it can reach the receipt whatever the step's status. Folding it into
      /// Diagnostic hid it: on success nothing printed, on failure the composed
      /// ERROR line was normalised away, and the receipt stayed silent either way.
      EngineNote: string option
      /// For an Aborted shell step: WHICH event ended it, from ProcessGroup's one
      /// wait-end snapshot. Some true = external interrupt (a failFast sibling),
      /// Some false = the deadline. The walker classifies AND narrates from this
      /// single source — deriving the cause a second time from timestamps let the
      /// two disagree inside one poll interval.
      AbortedBySibling: bool option
      /// See [RunResult.DurableId].
      DurableId: string option }

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
          TestDuration = None
          StageWarning = None
          CapturedStdoutRaw = None
          Diagnostic = None
          EngineNote = None
          AbortedBySibling = None
          DurableId = None }

    /// FG-174. A step REFUSED before it ran: no process, no output, no workspace change.
    ///
    /// Built here rather than by the caller so it cannot drift from `ok` — a hand-rolled
    /// record that quietly gained a field would be a `StepResult` nobody else produces.
    /// `ExitCode` stays None, which is load-bearing: `returnStatus` suppression keys on
    /// an exit code EXISTING, so a refusal can never be converted into a status answer.
    let refusedBeforeRunning (why: string) =
        { ok Failure with Diagnostic = Some why }

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
            let secretsForOutput =
                match request.MaskingSecrets with
                | Some bindings -> bindings
                | None -> fun () -> request.Secrets

            let outputRedaction =
                match request.MaskingSecrets with
                | Some bindings -> Some(Secrets.outputRedactionLive bindings request.MaskingSecretsLock)
                | None -> Secrets.outputRedaction request.Secrets

            let leakReports = System.Collections.Generic.List<string>()

            let decodedRedactedLine = request.OnRedactedLine |> Option.orElse request.OnLine

            let generatedLine =
                match request.OnGeneratedLine with
                | Some emit -> Some emit
                | None ->
                    request.OnLine
                    |> Option.map (fun emit ->
                        fun line -> emit (Secrets.mask (secretsForOutput ()) line))

            let deliverRedacted tagged decoded =
                match tagged, decoded with
                | None, None -> None
                | tagged, decoded ->
                    Some(fun (line: RedactedText) ->
                        // With a policy, ProcessGroup has already redacted the
                        // raw stream before framing this line. Re-masking is not
                        // idempotent for credentials such as `*`.
                        let masked = line.Text

                        // Detection runs on the MASKED text: anything still
                        // recognisable is an encoding masking cannot cover, and
                        // naming it is the whole point of FG-071.
                        for leak in Secrets.detectUnregisteredLeaksRedacted (secretsForOutput ()) line do
                            let note = $"WARNING: {leak.Variable} appears in output {leak.Encoding}-encoded; masking cannot cover this form"

                            if not (leakReports.Contains note) then
                                generatedLine |> Option.iter (fun emit -> leakReports.Add note; emit note)

                        match tagged, decoded with
                        | Some publish, _ -> publish line
                        | None, Some publish -> publish masked
                        | None, None -> ())

            let taggedRedactedLine = request.OnRedactedAdmission |> Option.orElse request.OnRedactedOutput
            let onLine = deliverRedacted taggedRedactedLine decodedRedactedLine

            let createRedactedAdmission =
                request.CreateRedactedAdmission
                |> Option.map (fun create ->
                    fun () ->
                        let stream = create ()
                        let admit = deliverRedacted (Some stream.Admit) decodedRedactedLine |> Option.defaultValue ignore
                        { Admit = admit
                          Complete = stream.Complete })

            let synchronousAdmission =
                Option.isSome request.OnRedactedAdmission
                || Option.isSome createRedactedAdmission

            let runResult =
                ProcessGroup.run
                    { RunRequest.create (script, request.Workspace) with
                        Interrupt = request.Interrupt
                        InterruptBeatsDeadline = request.InterruptBeatsDeadline
                        WorkspaceRoot = request.WorkspaceRoot
                        Environment = request.Environment
                        TimeoutMs = request.TimeoutMs
                        // Secret-free direct calls keep the historical string
                        // callback. Once a policy exists, process bytes must use
                        // the provenance-preserving callback below.
                        OnLine = if Option.isSome outputRedaction then None else request.OnLine
                        // Some ignore is intentionally distinct from None: a
                        // raw-only caller has no ordinary sink, so generated
                        // narration must not fall back to the raw callback.
                        OnGeneratedLine =
                            if synchronousAdmission then None
                            else Some(defaultArg generatedLine ignore)
                        OnGeneratedAdmission =
                            if synchronousAdmission then
                                Some(defaultArg generatedLine ignore)
                            else
                                None
                        OnRedactedLine =
                            if synchronousAdmission then None else onLine
                        OnRedactedAdmission =
                            if Option.isSome request.OnRedactedAdmission then onLine else None
                        CreateRedactedAdmission = createRedactedAdmission
                        OutputRedaction = outputRedaction
                        SuppressStdoutEcho = request.CaptureStdout }

            // REVIEW FIX (Codex, PR #13): detection lived only inside the stdout
            // streaming callback, so a step with no OnLine — or one that wrote a
            // transformed secret to STDERR — returned it with none of the warnings
            // FG-071 promises. Reverse/hex/char-split forms survive masking BY
            // DESIGN, so the warning is the entire guarantee, and it has to cover
            // every path out of the step.
            let recheckAlreadyRedacted value =
                match outputRedaction with
                | Some policy -> policy.MaskAlreadyRedacted value
                | None -> value

            let requireProvenance stream =
                function
                | Some value -> value
                | None -> invalidOp $"{stream} crossed raw redaction without token provenance"

            let maskedStdoutValue =
                match outputRedaction with
                | Some policy when request.CaptureStdout && runResult.StdoutReachedEof ->
                    policy.MaskRedacted runResult.Stdout
                | Some policy when request.CaptureStdout ->
                    policy.MaskAvailablePrefixRedacted runResult.Stdout
                | Some _ ->
                    runResult.StdoutRedacted
                    |> requireProvenance "stdout"
                    |> recheckAlreadyRedacted
                | _ -> RedactedText.Raw runResult.Stdout

            let maskedStdout = maskedStdoutValue.Text

            // Stderr and non-capture stdout crossed the raw matcher, but its
            // live inventory is sampled before each chunk. A sibling can bind
            // after that snapshot and before the queued line callback runs.
            // Recheck the returned buffers against the final inventory just as
            // WalkerCtx does at its publication boundary. Capture stdout is the
            // sole intentionally raw sink and is handled by the policy above.
            let maskedStderrValue =
                match outputRedaction with
                | Some _ ->
                    runResult.StderrRedacted
                    |> requireProvenance "stderr"
                    |> recheckAlreadyRedacted
                | None -> RedactedText.Raw runResult.Stderr

            let maskedStderr = maskedStderrValue.Text

            let bufferedSecrets = secretsForOutput ()

            let bufferedLeaks =
                [ maskedStdoutValue; maskedStderrValue ]
                |> List.collect (Secrets.detectUnregisteredLeaksRedacted bufferedSecrets)
                |> List.map (fun l ->
                    $"WARNING: {l.Variable} appears in output {l.Encoding}-encoded; masking cannot cover this form")
                |> List.distinct
                |> List.filter (fun note -> not (leakReports.Contains note))

            let maskedBufferedLeaks =
                bufferedLeaks
                |> List.map (Secrets.mask bufferedSecrets)

            for note in bufferedLeaks do
                leakReports.Add note
                generatedLine |> Option.iter (fun f -> f note)

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
                    // FG-196 made TimeoutMs = None the ordinary case, and the wait loop
                    // cannot produce TimedOut without a budget — but a diagnostic must
                    // not invent "0 ms" if that ever changes, so the absence is named
                    // rather than defaulted.
                    let budget =
                        match request.TimeoutMs with
                        | Some ms -> $"its {ms} ms timeout"
                        | None -> "a timeout, yet carried NO budget — this should be unreachable; report it"

                    Aborted,
                    None,
                    Some(
                        let t = run.Termination

                        let how =
                            match t with
                            | Some x when x.Escalated -> "SIGTERM was not honoured within the grace period, so the process group was killed"
                            | Some _ -> "the process group exited on SIGTERM"
                            | None -> "the process group could not be signalled"

                        $"step exceeded {budget}; {how}")
                | Cancelled -> Aborted, None, Some "step was cancelled"

            // FG-032/FG-103: a leak is a defect and an unavailable check is an
            // unknown — both are ENGINE findings, carried beside the step's own
            // failure reason so they reach the receipt whatever the status is.
            let baseEngineNote =
                match run.Termination with
                | Some t when t.LeakedProcesses < 0 ->
                    Some "leak check unavailable: the /proc scan failed, group state unknown"
                | Some t when t.LeakedProcesses > 0 ->
                    Some $"{t.LeakedProcesses} process(es) survived group reaping"
                | Some _ -> None
                | None ->
                    // No termination record AND no group id means the containment
                    // check never ran at all — the pgid marker was not captured, so
                    // nothing could be reaped or scanned. FG-103: that is an unknown,
                    // not a clean bill.
                    match run.ProcessGroupId with
                    | None -> Some "containment check unavailable: the process-group id was never captured"
                    | Some _ -> None

            let engineNote =
                match baseEngineNote, run.CleanupFailure with
                | Some a, Some b -> Some $"{a}; {b}"
                | Some a, None -> Some a
                | None, b -> b

            { Status = status
              ExitCode = exitCode
              // Buffered output is masked too — a caller that reads Stdout
              // instead of streaming must not get a different secrecy guarantee.
              Stdout = maskedStdout
              // A caller with no OnLine still has to SEE the warning, or FG-071's
              // "never silent" promise depends on how the caller chose to read.
              Stderr =
                if List.isEmpty maskedBufferedLeaks then
                    maskedStderr
                else
                    maskedStderr + String.concat "\n" maskedBufferedLeaks + "\n"
              DurationMs = run.DurationMs
              ProcessGroupId = run.ProcessGroupId
              Termination = run.Termination
              Archived = []
              TestTotals = None
              TestDuration = None
              StageWarning = None
              // ONLY when asked. An unconditional copy would keep the unmasked text
              // alive on every step for no consumer.
              CapturedStdoutRaw = if request.CaptureStdout then Some run.Stdout else None
              Diagnostic = diagnostic
              EngineNote = engineNote
              AbortedBySibling =
                match run.Outcome with
                | Cancelled -> Some true
                | TimedOut -> Some false
                | _ -> None
              DurableId = run.DurableId }

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

            // Either cause stops the archive; the diagnostic names neither, because
            // this layer cannot tell a deadline from a failed failFast sibling.
            let abort () =
                let fired (f: (unit -> bool) option) =
                    match f with
                    | Some p -> (try p () with _ -> false)
                    | None -> false

                fired request.Interrupt || fired request.DeadlineExpired

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
                // REVIEW FIX (Codex, PR #14 round 4), two errors in one place:
                //  * emitting here AND returning a Diagnostic printed the failure
                //    twice for one event, because runStepInner emits it as well;
                //  * it asserted the DEADLINE expired, but under `parallel` failFast
                //    the interrupt comes from a failed sibling. The engine does not
                //    know which, so it must not name one.
                { ok Aborted with
                    Diagnostic =
                        Some
                            $"archiving interrupted after {published.Length} file(s); the artifact set is INCOMPLETE" }
            elif List.isEmpty published then
                // Jenkins' archive advisory, measured on 2.568.1 in its three
                // variants (typographic quotes and all) — printed whether or not the
                // empty archive is allowed, BEFORE the outcome line. A substring
                // suppression used to hide the Jenkins side of this; both engines
                // speak it now and the sentences compare (FG-102).
                let advisory =
                    let q (x: string) = "\u2018" + x + "\u2019"

                    // MEASURED (receipt `archive-multi-pattern-advisory`): for a
                    // comma-separated list Jenkins validates the
                    // individual Ant masks, advising on the FIRST unmatched one —
                    // `missing/**,other-*.zip` advises on `missing/**` alone (the
                    // Configuration-error line keeps the full list).
                    let raw =
                        raw.Split(',')
                        |> Array.map (fun m -> m.Trim())
                        |> Array.tryFind (fun m -> m <> "")
                        |> Option.defaultValue raw

                    if raw.EndsWith "/**" then
                        let starstar = q "**"
                        $"{q raw} doesn\u2019t match anything, but {starstar} does. Perhaps that\u2019s what you mean?"
                    elif raw.Contains "/" then
                        // the deepest EXISTING literal prefix decides the clause —
                        // measured: `existing/missing/*.zip` with `existing` present
                        // says "\u2018existing\u2019 exists but not \u2018existing/missing/*.zip\u2019",
                        // while a missing FIRST segment says "even \u2018base\u2019 doesn\u2019t exist"
                        let literalSegs =
                            raw.Split('/')
                            |> Array.takeWhile (fun seg -> not (seg.Contains "*" || seg.Contains "?"))

                        let deepestExisting =
                            literalSegs
                            |> Array.scan (fun acc seg -> if acc = "" then seg else acc + "/" + seg) ""
                            |> Array.skip 1
                            |> Array.takeWhile (fun prefix ->
                                System.IO.Directory.Exists(System.IO.Path.Combine(request.Workspace, prefix))
                                || System.IO.File.Exists(System.IO.Path.Combine(request.Workspace, prefix)))
                            |> Array.tryLast

                        match deepestExisting with
                        | Some prefix -> $"{q raw} doesn\u2019t match anything: {q prefix} exists but not {q raw}"
                        | None when literalSegs.Length > 0 ->
                            $"{q raw} doesn\u2019t match anything: even {q literalSegs.[0]} doesn\u2019t exist"
                        | None -> $"{q raw} doesn\u2019t match anything"
                    else
                        $"{q raw} doesn\u2019t match anything"

                request.OnLine |> Option.iter (fun f -> f advisory)

                if not allowEmpty then
                    // Jenkins fails the build here rather than passing quietly, and
                    // a silent empty archive is the worst outcome for a user.
                    { ok Failure with
                        Diagnostic = Some $"No artifacts found that match the file pattern \"{raw}\"" }
                else
                    // MEASURED (receipt `archive-allow-empty-boolean`, Jenkins 2.568.1):
                    // `allowEmptyArchive: true` PERMITS the empty archive but still says
                    // so — and the build runs on. Passing silently would hide a broken
                    // glob from the very person who opted into tolerating it.
                    request.OnLine
                    |> Option.iter (fun f -> f $"No artifacts found that match the file pattern \"{raw}\". Configuration error?")

                    { ok Success with Archived = published }
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

            let abort () =
                let fired (f: (unit -> bool) option) =
                    match f with
                    | Some p -> (try p () with _ -> false)
                    | None -> false

                fired request.Interrupt || fired request.DeadlineExpired

            let noReports = "No test report files were found. Configuration error?"
            let noResults = "None of the test reports contained any result"
            let missingTestName = JUnitDiagnostics.MissingTestNameMessage
            let missingIdentity = "Cannot invoke \"String.lastIndexOf(int)\" because \"this.className\" is null"

            let emptySummary (messages: string list) =
                messages
                |> List.iter (fun message -> request.OnLine |> Option.iter (fun emit -> emit message))

                { ok Success with
                    TestTotals = Some(0, 0, 0)
                    TestDuration = Some 0.0f }

            match
                Publish.parseJUnitWithAbort
                    request.Workspace
                    (patterns raw)
                    request.JUnitSkipOldReportsSince
                    abort
            with
            // REVIEW FIX (Codex, PR #14 round 10): every error became Failure, so a
            // `timeout` ending in `junit` selected `post { failure }` where a shell or
            // archive timeout selects `post { aborted }`. The cause is preserved and
            // mapped to the matching result.
            | Result.Error Interrupted ->
                { ok Aborted with
                    Diagnostic = Some "junit aborted: the step was interrupted while reading test reports" }
            | Result.Error NoReports ->
                if request.JUnitAllowEmptyResults then
                    // The pinned plugin emits both messages: the parser permits
                    // the missing glob, then the aggregate summary is also empty.
                    emptySummary [ noReports; noResults ]
                else
                    request.OnLine |> Option.iter (fun emit -> emit noReports)
                    { ok Failure with Diagnostic = Some noReports }
            | Result.Error(MissingTestName relative) ->
                // Unlike FG-211's later null-className failure, Jenkins prints
                // a report-specific wrapper before its exception envelope. The
                // wrapper is ordinary compared output; the exact root cause stays
                // the diagnostic owned by the hosted step boundary.
                let reportPath =
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(request.Workspace, relative))
                request.OnLine |> Option.iter (fun emit -> emit $"Failed to read {reportPath}")
                { ok Failure with Diagnostic = Some missingTestName }
            | Result.Error MissingIdentity ->
                request.OnLine |> Option.iter (fun emit -> emit missingIdentity)
                { ok Failure with Diagnostic = Some missingIdentity }
            | Result.Error(Unreadable m) -> { ok Failure with Diagnostic = Some m }
            | Result.Ok(total, _, _, _) when total = 0 ->
                if request.JUnitAllowEmptyResults then
                    emptySummary [ noResults ]
                else
                    request.OnLine |> Option.iter (fun emit -> emit noResults)
                    { ok Failure with Diagnostic = Some noResults }
            | Result.Ok(total, failed, skipped, duration) ->
                // Jenkins marks the build UNSTABLE (not failed) when tests fail:
                // the build worked, the code did not.
                // The build and stage flags are two typed inputs to one measured
                // matrix. A failed report contributes a stage warning unless stage
                // marking is skipped; that surviving warning contributes UNSTABLE
                // to the build unless build marking is skipped too. Reports are
                // still parsed and their counts returned in every combination.
                // Interrupts and unreadable reports have already exited through the
                // failure arms above and neither flag can suppress them.
                let marksStageUnstable =
                    failed > 0 && not request.JUnitSkipMarkingStageUnstable

                let status =
                    if marksStageUnstable && not request.JUnitSkipMarkingBuildUnstable then
                        Unstable
                    else
                        Success

                { ok status with
                    TestTotals = Some(total, failed, skipped)
                    TestDuration = duration
                    StageWarning = if marksStageUnstable then Some Unstable else None
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
            // FG-044b. Masking lived ONLY on the shell path, so `echo "$TOKEN"` published
            // the credential verbatim while Jenkins prints `****`. Any path that emits
            // output has to mask, or the guarantee is "we mask, except where we forgot".
            let masked =
                if List.isEmpty request.Secrets then message else Secrets.mask request.Secrets message

            let leaks =
                Secrets.detectLeaks request.Secrets masked
                |> List.map (fun l ->
                    $"WARNING: {l.Variable} appears in output {l.Encoding}-encoded; masking cannot cover this form")
                |> List.distinct

            for note in leaks do
                request.OnLine |> Option.iter (fun f -> f note)

            // A MULTI-LINE MESSAGE IS MULTIPLE LOG LINES. Jenkins writes the message to
            // the build log, so an embedded newline becomes a line break there; this
            // handed `OnLine` one record containing a `\n`, and everything downstream
            // that counts or compares lines then saw one line where Jenkins has two.
            //
            // MEASURED (`script-sh-returnstdout`): `echo "withnl:[${out}]"` over a
            // captured "value\n" gives Jenkins seven output lines and Fogell six. Found
            // only once `returnStdout` could produce a multi-line value at all — no
            // existing case echoes one — but the rule is JENKINS', not the capture
            // path's, so it is fixed here for every caller rather than at the one that
            // exposed it. `String.concat` on the way into `Stdout` below keeps the text
            // itself byte-identical.
            request.OnLine
            |> Option.iter (fun f ->
                for line in masked.Split '\n' do
                    f line)

            { ok Success with
                Stdout = masked + "\n"
                // Only when nobody streamed them. The differential runner ALWAYS supplies
                // OnLine and then re-emits every Stderr line, so returning them here too
                // printed each warning TWICE for a single leak.
                Stderr =
                    match request.OnLine, leaks with
                    | None, (_ :: _) -> String.concat "\n" leaks + "\n"
                    | _ -> "" }
        // FG-178. `echo()` WITH NO MESSAGE PRINTS `null`, and it does NOT fail.
        //
        // Review reported that Jenkins REJECTS the call and asked for a required-argument
        // check. MEASURED instead of implemented, and held by receipt
        // `script-echo-no-message`; the report was wrong on the Jenkins
        // half: `script { echo(); sh 'echo ran > ran.txt' }` SUCCEEDS on Jenkins with the
        // shell running, and the console shows the literal `null` — Groovy stringifying a
        // null message. Fogell agreed on result and workspace and printed NOTHING, so the
        // only divergence was the missing line.
        //
        // Enforcing a required argument here would have been a FALSE REFUSAL of a
        // pipeline Jenkins accepts — the second review finding on this branch that was
        // materially wrong about Jenkins, and the second caught by probing before
        // implementing. `Stdout` alone was not enough: the differential compares what
        // STREAMED, and nothing called `OnLine`.
        | "echo", None ->
            request.OnLine |> Option.iter (fun f -> f "null")
            { ok Success with Stdout = "null\n" }
        | name, _ ->
            { ok Failure with
                Diagnostic = Some $"step '{name}' is not implemented; unsupported behaviour fails closed" }
