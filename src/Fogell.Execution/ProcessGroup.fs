namespace Fogell.Execution

open System
open System.Diagnostics
open System.Threading

/// FG-222. Explicit process environments at the controller/build boundary.
///
/// A build receives only the small fixed compatibility baseline at run
/// entry plus pipeline/stage/withEnv/credential overlays. Controller-owned SCM
/// fetches have a separate allowlist because SSH agents, Git configuration and
/// certificate/proxy settings are controller authority and must never become
/// build input.
type ControllerScmEnvironment = private ControllerScmEnvironment of (string * string) list

module LaunchEnvironment =

    [<Literal>]
    let private FallbackPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

    let private ambient name =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some(name, value)

    let private selected names = names |> List.choose ambient

    /// The measured environment-of-necessity for Jenkins compatibility. PATH is
    /// required by ordinary shell commands. HOME is a Fogell-owned neutral path,
    /// not the controller account's home; three sealed cases require it to exist.
    /// Everything else must be declared by the build.
    let buildBaseline agentHome =
        [ "PATH", FallbackPath
          "HOME", agentHome ]

    /// Construct an opaque controller-SCM profile from already validated
    /// controller configuration. It cannot be passed to a build launcher.
    let internal controllerScmFrom environment = ControllerScmEnvironment environment

    let private isExecutable path =
        try
            if not (IO.File.Exists path) then
                false
            else
                let mode = IO.File.GetUnixFileMode path
                let executeBits =
                    IO.UnixFileMode.UserExecute
                    ||| IO.UnixFileMode.GroupExecute
                    ||| IO.UnixFileMode.OtherExecute

                mode &&& executeBits <> enum<IO.UnixFileMode> 0
        with _ ->
            false

    let private resolveExecutable
        (executable: string)
        (workingDirectory: string)
        (environment: (string * string) list)
        =
        environment
        |> List.rev
        |> List.tryPick (fun (name, value) -> if name = "PATH" then Some value else None)
        |> Option.bind (fun path ->
            path.Split(IO.Path.PathSeparator, StringSplitOptions.None)
            |> Array.map (fun directory ->
                let root =
                    if String.IsNullOrEmpty directory then
                        workingDirectory
                    elif IO.Path.IsPathRooted directory then
                        directory
                    else
                        IO.Path.Combine(workingDirectory, directory)

                IO.Path.GetFullPath(IO.Path.Combine(root, executable)))
            |> Array.tryFind isExecutable)

    let resolveBuildExecutable executable workingDirectory environment =
        resolveExecutable executable workingDirectory environment

    let resolveControllerScmExecutable executable workingDirectory (ControllerScmEnvironment environment) =
        resolveExecutable executable workingDirectory environment

    /// Controller-only SCM launch input. Additional deployment-specific helper
    /// variables may be opted in by name through FOGELL_SCM_ENV_ALLOWLIST; that
    /// control variable is parsed here and is never copied to the child itself.
    let controllerScmBaseline () =
        let standard =
            [ "PATH"
              "HOME"
              "USER"
              "LOGNAME"
              "XDG_CONFIG_HOME"
              "SSH_AUTH_SOCK"
              "SSH_ASKPASS"
              "GIT_ASKPASS"
              "GIT_SSH"
              "GIT_SSH_COMMAND"
              "GIT_CONFIG_GLOBAL"
              "GIT_CONFIG_SYSTEM"
              "GIT_SSL_CAINFO"
              "SSL_CERT_FILE"
              "SSL_CERT_DIR"
              "HTTP_PROXY"
              "HTTPS_PROXY"
              "ALL_PROXY"
              "NO_PROXY"
              "http_proxy"
              "https_proxy"
              "all_proxy"
              "no_proxy" ]

        let additional =
            match Environment.GetEnvironmentVariable "FOGELL_SCM_ENV_ALLOWLIST" with
            | null
            | "" -> []
            | value ->
                value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                |> Array.toList

        let chosen =
            standard @ additional
            |> List.distinct
            |> selected

        (if chosen |> List.exists (fun (name, _) -> name = "PATH") then
             chosen
         else
             ("PATH", FallbackPath) :: chosen)
        |> controllerScmFrom

    /// Replace ProcessStartInfo's inherited dictionary with exactly the build
    /// map. Last-wins preserves nested overlays.
    let applyBuildTo (startInfo: ProcessStartInfo) (environment: (string * string) list) =
        startInfo.Environment.Clear()

        for key, value in environment do
            startInfo.Environment[key] <- value

    /// Apply controller-only Git transport authority. The opaque argument keeps
    /// ordinary build launch sites from accidentally accepting this profile.
    let applyControllerScmTo (startInfo: ProcessStartInfo) (ControllerScmEnvironment environment) =
        startInfo.Environment.Clear()

        for key, value in environment do
            startInfo.Environment[key] <- value

/// FG-031/FG-032. Process-group lifecycle containment.
///
/// The mechanism: every step is launched through `setsid`, so the child leads a
/// new session and its pid IS its process-group id. Signalling `-pgid` then
/// reaches the step and every descendant it spawned, which is the difference
/// between terminating a step and orphaning its children.
///
/// ADR 0008 is explicit that this is *lifecycle* containment, not a hostile
/// multi-tenant boundary. A determined workload can leave the group with its own
/// `setsid`; untrusted multi-tenant work needs VM-level isolation.
type Outcome =
    | Completed of exitCode: int
    /// FG-033. The step's process was terminated by a signal from OUTSIDE this
    /// engine — an operator, an OOM killer, a container stop.
    ///
    /// Jenkins takes ~10 minutes to conclude anything here and then reports
    /// `exit code -1` with no mention of why (JB-DUR-005). Fogell owns the
    /// process, so it knows immediately and names the signal.
    | Signalled of signal: int
    | TimedOut
    | Cancelled

type Termination =
    { /// True when SIGTERM alone was enough — i.e. the step had a chance to
      /// clean up, which is the contract scripts rely on (ADR 0005).
      GracefulExit: bool
      /// True when SIGKILL was needed after the grace period elapsed.
      Escalated: bool
      /// Descendants still alive after the group was reaped. Should be zero:
      /// Jenkins leaves `nohup`ed children running and we promised to beat that.
      /// -1 means the check itself was UNAVAILABLE (the /proc read failed) — an
      /// unknown is reported as unknown, never as a clean zero.
      LeakedProcesses: int }

/// Why [waitForProcessExit] returned — decided ONCE, when the wait ends.
type internal WaitEnd =
    | Waiting
    | Exited
    | Expired
    | Interrupted

type RunResult =
    { Outcome: Outcome
      Stdout: string
      Stderr: string
      DurationMs: int64
      ProcessGroupId: int option
      Termination: Termination option
      /// A cleanup step that FAILED — e.g. the secret-bearing shebang script that
      /// could not be deleted. Silence here would leave the file on disk while the
      /// security contract claims cleanup is guaranteed; the caller surfaces this
      /// as an engine note.
      CleanupFailure: string option
      /// The generated durable-script id for THIS run, when a shebang script was
      /// materialised — the caller canonicalises exactly this id, never a shape.
      DurableId: string option }

type RunRequest =
    { Command: string
      WorkingDirectory: string
      Environment: (string * string) list
      /// Milliseconds. int64 because a Jenkins `timeout(time: 30, unit: 'DAYS')`
      /// exceeds Int32.MaxValue ms and must not be silently shortened.
      TimeoutMs: int64 option
      /// How long a step may take to honour SIGTERM before it is killed.
      GraceMs: int
      /// Called with each output line as it arrives, so a running build streams
      /// rather than materialising at the end (FG-040 / JB-LOG-002 parity).
      OnLine: (string -> unit) option
      /// FG-174. `sh(returnStdout: true)` CAPTURES stdout instead of printing it.
      ///
      /// Seen on pinned Jenkins: the xtrace line still appears in the console — Jenkins
      /// prints `+ printf value` — while the program's own output does NOT. UNPROVEN
      /// in-repo, and said so: the probe diverges because Fogell cannot yet reproduce it,
      /// so it has no receipt until the trace moves off stdout. So this
      /// suppresses the ECHO of stdout only; stderr, which is where `sh -x` writes the
      /// trace, keeps streaming. Both sinks are still filled, so the captured value is
      /// unaffected.
      ///
      /// Without this the compared output gained lines Jenkins never produced, and the
      /// captured value picked up the trace as well — measured by lifting the refusal and
      /// running it, not by reading the code.
      SuppressStdoutEcho: bool
      /// Set when the step's group should be reaped even on success. Jenkins does
      /// NOT do this — measured: `nohup`ed children survive both success and
      /// abort, and JENKINS_NODE_COOKIE=dontKillMe is moot because nothing is
      /// killed. FG-032 beats that, with an opt-out.
      ReapGroup: bool
      /// FG-036. Polled while the step runs; when it returns true the step is
      /// interrupted from OUTSIDE — a `failFast` sibling failing, or an operator
      /// abort. It takes the same SIGTERM -> grace -> SIGKILL path a timeout
      /// takes, because JB-FAIL-003 measured Jenkins using ONE interrupt
      /// mechanism for both, and a script's trap handler cannot tell them apart.
      /// The outcome is [Cancelled], not [TimedOut]: the step did not run out of
      /// time, so reporting a timeout would misattribute the cause.
      Interrupt: (unit -> bool) option
      /// When BOTH the deadline and an interrupt are observable in the same poll,
      /// this decides which event was actually EARLIER — the caller owns the
      /// timestamps (its clock stamps the sibling failure and its deadline), so the
      /// tie cannot be broken here. None means deadline-first, the plain reading.
      InterruptBeatsDeadline: (unit -> bool) option
      /// See [StepRequest.WorkspaceRoot] — where the @tmp scaffolding roots.
      WorkspaceRoot: string option }

    static member create(command, workingDirectory) =
        { Command = command
          WorkingDirectory = workingDirectory
          Environment = []
          TimeoutMs = None
          GraceMs = 2_000
          OnLine = None
          SuppressStdoutEcho = false
          ReapGroup = true
          Interrupt = None
          InterruptBeatsDeadline = None
          WorkspaceRoot = None }

module ProcessGroup =

    /// Count processes still in the group. Reads /proc directly rather than
    /// shelling out, so the check cannot itself spawn something.
    /// -1 means UNKNOWN: the /proc read itself failed. FG-103 — returning 0 there
    /// made the leak check a gate that could not fail: a broken /proc reported
    /// "nothing survived" while FG-032's headline claim rested on this number, and
    /// both unit tests asserting 0 passed against a completely dead reader.
    /// Unknown fails CLOSED everywhere downstream: the group is treated as still
    /// populated, and the diagnostic says the check was unavailable rather than
    /// inventing a clean bill.
    let private survivorsIn (pgid: int) : int =
        try
            IO.Directory.GetDirectories "/proc"
            |> Array.choose (fun d ->
                match Int32.TryParse(IO.Path.GetFileName d) with
                | true, pid -> Some pid
                | _ -> None)
            |> Array.filter (fun pid ->
                match Native.processGroupOf pid with
                | Some g -> g = pgid
                | None -> false)
            |> Array.length
        with _ ->
            -1

    /// Wait until the group is EMPTY, up to `budgetMs`.
    ///
    /// This deliberately counts group membership rather than asking whether the
    /// leader pid still exists. The leader is usually the first to exit — a step
    /// that backgrounds a daemon leaves the group populated while the leader is
    /// long gone — so a leader-existence check reports success and leaves the
    /// daemon running. That is exactly the Jenkins behaviour FG-032 exists to
    /// beat, and the first version of this function reproduced it.
    let private waitForGroupExit (pgid: int) (budgetMs: int) : bool =
        let sw = Stopwatch.StartNew()
        let mutable gone = survivorsIn pgid = 0

        while not gone && sw.ElapsedMilliseconds < int64 budgetMs do
            Thread.Sleep 20
            gone <- survivorsIn pgid = 0

        gone

    /// SIGTERM the group, wait out the grace period, then SIGKILL. This is the
    /// contract measured on Jenkins (JB-FAIL-003): the interrupt is a trappable
    /// TERM with a grace window, and scripts install handlers expecting it.
    let terminateGroup (pgid: int) (graceMs: int) : Termination =
        let termDelivered = Native.signalGroup pgid Native.SIGTERM
        let exitedOnTerm = termDelivered && waitForGroupExit pgid graceMs

        let escalated =
            if exitedOnTerm then
                false
            else
                Native.signalGroup pgid Native.SIGKILL |> ignore
                waitForGroupExit pgid 2_000 |> ignore
                true

        { GracefulExit = exitedOnTerm
          Escalated = escalated
          LeakedProcesses = survivorsIn pgid }

    /// Reap whatever remains of a group after the step's direct child exited.
    /// A step that backgrounds a daemon leaves it in the group; Jenkins lets it
    /// survive, we do not.
    let reap (pgid: int) (graceMs: int) : Termination =
        if survivorsIn pgid = 0 then
            { GracefulExit = true
              Escalated = false
              LeakedProcesses = 0 }
        else
            terminateGroup pgid graceMs

    /// Residual quiet sampling used only before an abort signal. The caller owns
    /// the clock so time already spent draining active callbacks consumes this
    /// SAME budget. Keeping the sleeper injectable makes the exact upper bound a
    /// deterministic law rather than a wall-clock assertion in the test suite.
    let internal settleOutputUntilQuiet
        (budgetMs: int)
        (elapsedMilliseconds: unit -> int64)
        (sleep: int -> unit)
        (snapshot: unit -> int * int)
        =
        let mutable settled = false

        while not settled && elapsedMilliseconds () < int64 budgetMs do
            let before = snapshot ()
            let remaining = max 0L (int64 budgetMs - elapsedMilliseconds ())
            let sleepMs = int (min 40L remaining)

            if sleepMs > 0 then
                sleep sleepMs

            let after = snapshot ()
            settled <- after = before

    /// Run one command in its own process group.
    let run (request: RunRequest) : RunResult =
        let sw = Stopwatch.StartNew()

        // `setsid --wait` keeps a parent around to collect the exit status, but
        // that parent is what .NET reports as the process id — and ITS group is
        // ours, not the child's. So the session leader reports its own pid on
        // stderr as the first line, and that is the real process-group id.
        let pgidMarker = "__FOGELL_PGID "

        let psi = ProcessStartInfo("/bin/sh")
        // Keep an explicit wait-status-preserving parent outside the new group.
        // This makes marker provenance observable (proc.Id is the outer waiter,
        // not the session leader) without `setsid --fork --wait`: util-linux maps
        // a signal-killed child to the bare signal number, whereas the shell's
        // wait status preserves the established 128+signal diagnostic contract.
        psi.ArgumentList.Add "-c"
        psi.ArgumentList.Add "/usr/bin/setsid /bin/sh -c \"$1\" \"$2\" \"$3\"; exit $?"
        psi.ArgumentList.Add "fogell-wait-wrapper" // $0 for the outer waiter
        // `-xe`, exactly as Jenkins' durable-task runs a shell step: `-x` makes the
        // trace an EMITTED, COMPARED artifact on both engines (retiring the last
        // wording-only suppression and FG-002c's continuation gap with it), and
        // `-e` is the errexit semantics the receipts were already measured against.
        // `2>&1` merges the streams IN THE SHELL: the trace goes to stderr, and
        // .NET's two async pipe readers deliver cross-stream events in racy order —
        // output lines overtook their own trace. One pipe is kernel-ordered, and it
        // is also exactly what Jenkins' console is. The pgid marker stays on the
        // OUTER stderr, printed before anything is redirected.
        //
        // A SHEBANG script is the exception, as it is on Jenkins: durable-task
        // executes the named interpreter directly and injects no flags, so a
        // `#!/bin/bash` script runs under bash untraced. The script goes to a file
        // (its interpreter line only works from one) and is executed as itself.
        // Owner-only from the first byte — the script text can carry a rendered
        // credential, and a 0644 window in shared /tmp is a disclosure. Deletion is
        // guaranteed by the disposable below, exception paths included.
        let mutable mintedDurableId: string option = None

        let shebangFile =
            if true then // EVERY script materialises — see the wrapper comment
                // In the workspace's `@tmp` SIBLING — Jenkins' own durable-script
                // location: executable where builds execute (hardened hosts mount
                // /tmp noexec) and already excluded from the workspace hash as
                // scaffolding, so no user-creatable basename is ever excluded.
                // `script.sh` inside a per-step directory under `@tmp` — the same
                // OBSERVABLE identity durable-task gives a script (`$0` basename
                // `script.sh`), so basename-dependent scripts take the same path on
                // both engines; the random parent keeps parallel steps apart and
                // `@tmp` keeps it out of the workspace hash.
                // durable-task's exact layout: <workspace>@tmp/durable-<8hex>/script.sh,
                // rooted at the WORKSPACE even inside dir() — the full $0 is observable
                let root =
                    let r = defaultArg request.WorkspaceRoot request.WorkingDirectory
                    // trim only REDUNDANT separators: "/" must stay the filesystem
                    // root, not become "" and turn the whole path relative
                    if r.Length > 1 then r.TrimEnd '/' else r
                let hex = Guid.NewGuid().ToString("N").Substring(0, 8)
                mintedDurableId <- Some hex
                let tmpDir = IO.Path.Combine(root + "@tmp", $"durable-{hex}")

                IO.Directory.CreateDirectory tmpDir |> ignore
                let f = IO.Path.Combine(tmpDir, "script.sh")

                // the EXECUTE bit follows durable-task: only a shebang script gets
                // it (only a shebang script is exec'd) — an ordinary script runs
                // under `sh -xe` and a `[ -x \"$0\" ]` probe must say what Jenkins says
                let mode =
                    if request.Command.StartsWith "#!" then
                        IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite ||| IO.UnixFileMode.UserExecute
                    else
                        IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite

                try
                    use stream = IO.File.Open(f, IO.FileStreamOptions(Mode = IO.FileMode.CreateNew, Access = IO.FileAccess.Write, UnixCreateMode = mode))
                    use writer = new IO.StreamWriter(stream)
                    writer.Write request.Command
                    Some f
                with e ->
                    // a partial secret-bearing file must not outlive a failed write —
                    // the cleanup disposable is registered only after creation succeeds
                    (try IO.File.Delete f with _ -> ())
                    raise e
            else
                None

        // Deletion is attempted on the normal path with the failure CAPTURED (it
        // becomes an engine note); the disposable is the exception-path backstop,
        // where the run is already failing loudly.
        let mutable cleanupFailure: string option = None

        let deleteShebang (f: string) =
            try
                if IO.File.Exists f then IO.File.Delete f
                let d = IO.Path.GetDirectoryName f
                if IO.Directory.Exists d then IO.Directory.Delete(d, false)
            with e ->
                cleanupFailure <- Some $"could not delete shebang script {f}: {e.Message}"

        use _cleanupShebang =
            { new IDisposable with
                member _.Dispose() = shebangFile |> Option.iter deleteShebang }

        // The payload travels as a POSITIONAL argument (`$1`), not an environment
        // variable: reserving any env name collided with a pipeline exporting it —
        // Jenkins passes such a variable through to the script untouched, and so
        // does this now.
        // EVERY script materialises to the durable path and runs as durable-task
        // runs it: a shebang script executes itself, everything else runs under
        // `sh -xe <path>` — so `$0` is the script path on both engines, not
        // `/bin/sh` here and `script.sh` there. The path travels positionally.
        // FG-174. `2>&1` IS WHY THE TRACE WAS ON STDOUT — not `sh`. This was recorded on
        // the board as "Fogell's `sh -x` writes its trace to stdout", which named the
        // symptom and made the fix sound like it changed shell invocation for every step
        // and every receipt. It does not: `sh -x` writes to STDERR exactly as Jenkins'
        // does, and the wrapper here MERGES the two streams.
        //
        // The merge exists so that interleaving is exact — one pipe cannot reorder
        // against itself, and the console shows the trace and the output in the order
        // the script produced them. That reason DOES NOT APPLY when stdout is captured:
        // nothing of stdout reaches the console, so there is no interleaving left to
        // preserve. Splitting the streams only there gives the Jenkins shape — the trace
        // still prints, the program's own output does not — and leaves every other step
        // byte-identical, which is what keeps the receipts valid.
        let mergeStderr = if request.SuppressStdoutEcho then "" else " 2>&1"

        (match request.Command.StartsWith "#!" with
         | true -> psi.ArgumentList.Add $"printf '%%s%%s\n' '{pgidMarker}' \"$$\" >&2; exec \"$1\"{mergeStderr}"
         | false -> psi.ArgumentList.Add $"printf '%%s%%s\n' '{pgidMarker}' \"$$\" >&2; exec /bin/sh -xe \"$1\"{mergeStderr}")

        // These are $1..$3 of the outer waiter, and therefore command/$0/$1 of
        // the inner shell launched by setsid.
        psi.ArgumentList.Add "fogell-launcher"
        psi.ArgumentList.Add(defaultArg shebangFile request.Command)

        psi.WorkingDirectory <- request.WorkingDirectory
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false

        LaunchEnvironment.applyBuildTo psi request.Environment

        use proc = new Process()
        proc.StartInfo <- psi
        proc.EnableRaisingEvents <- true

        let stdout = Text.StringBuilder()
        let stderr = Text.StringBuilder()
        let completionOptions = Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        let reportedPgid = Tasks.TaskCompletionSource<int>(completionOptions)
        let stdoutClosed = Tasks.TaskCompletionSource<unit>(completionOptions)
        let stderrClosed = Tasks.TaskCompletionSource<unit>(completionOptions)
        let processExited = Tasks.TaskCompletionSource<unit>(completionOptions)
        let stdoutCallbackGate = obj ()
        let stderrCallbackGate = obj ()

        proc.Exited.Add(fun _ -> processExited.TrySetResult(()) |> ignore)

        let emit (sink: Text.StringBuilder) (line: string) =
            if line <> null then
                lock sink (fun () -> sink.AppendLine line |> ignore)
                request.OnLine |> Option.iter (fun f -> f line)

        // CAPTURED STDOUT IS READ AS ONE STREAM, NOT REASSEMBLED FROM LINES.
        //
        // The first version of this filled the same StringBuilder through `AppendLine`,
        // and MEASURED AGAINST JENKINS it was wrong: `printf value` emits five bytes and
        // no terminator, but a line-based sink re-terminates every line, so the captured
        // value came back as "value\n". `sh(script: 'printf value', returnStdout: true)`
        // then differed from Jenkins in a way no amount of `.trim()` in the pipeline
        // would reveal, and the `raw:[...]` assertion in `script-sh-returnstdout` is what
        // caught it: Jenkins printed one line where Fogell printed two.
        //
        // A line reader CANNOT fix this — `OutputDataReceived` never says whether the
        // final line carried a terminator, so the information is gone before the sink
        // sees it. `ReadToEndAsync` still DECODES to a string; what it preserves is the
        // terminator, not the raw bytes, and an earlier version of this comment said
        // "as bytes" and overstated it (raised in review on PR #53). Capture mode therefore skips the line reader for stdout entirely,
        // which costs nothing: nothing is echoing those lines anyway. stderr keeps its
        // event reader, so the xtrace still streams while this runs, and the two are read
        // CONCURRENTLY — reading one to completion before draining the other is how a
        // full pipe buffer deadlocks a process that writes to both.
        if not request.SuppressStdoutEcho then
            proc.OutputDataReceived.Add(fun e ->
                // Process may dispatch EOF while an earlier user callback is
                // still running. Serialize the whole callback, not only buffer
                // mutation, so EOF is a real queue-drained barrier.
                lock stdoutCallbackGate (fun () ->
                    match e.Data with
                    | null -> stdoutClosed.TrySetResult(()) |> ignore
                    | line -> emit stdout line))

        proc.ErrorDataReceived.Add(fun e ->
            lock stderrCallbackGate (fun () ->
                match e.Data with
                | null -> stderrClosed.TrySetResult(()) |> ignore
                | line when line.StartsWith pgidMarker ->
                    // the leader's own pid: the real group id. Never surfaced to the
                    // caller as build output.
                    match Int32.TryParse(line.Substring(pgidMarker.Length).Trim()) with
                    | true, pid -> reportedPgid.TrySetResult pid |> ignore
                    | _ -> ()
                | line -> emit stderr line))

        proc.Start() |> ignore

        // `Exited` is a process-handle notification. It does not wait for
        // redirected pipes to close, unlike Process.WaitForExit(): an escaped
        // descendant may inherit a write end and must not wedge step completion.
        if proc.HasExited then
            processExited.TrySetResult(()) |> ignore

        // FG-181. The capture accumulates INCREMENTALLY into a buffer this scope owns,
        // rather than being handed to `ReadToEndAsync` and read out of the task's Result.
        // The two differ only when the read does not finish, and that is exactly the case
        // that was wrong: a descendant which escaped the process group holds the inherited
        // write end open, `ReadToEndAsync` cannot complete, the bounded wait below expires,
        // and the task's Result is unavailable — so the fallback substituted "" and threw
        // away bytes that HAD ALREADY ARRIVED. MEASURED: `setsid sleep 10 & printf token`
        // gave Jenkins `raw:[token]` and Fogell `raw:[]`, with BOTH ENGINES REPORTING
        // SUCCESS, which is a wrong value under a green build. With the buffer the same
        // case truncates instead of erasing, and `token` is there because it arrived long
        // before the bound. Receipt `script-capture-escaped-descendant`, which was run
        // against this code REVERTED and diverges on both the value and the workspace
        // hash — a capture case that cannot fail proves nothing about capture.
        //
        // Started BEFORE the stderr reader and never awaited until the wait is over, so
        // both pipes drain concurrently — see the capture note above.
        //
        // The buffer is locked on BOTH sides: this task may still be reading when the
        // snapshot below is taken, precisely in the case the ticket is about.
        let captureBuffer = Text.StringBuilder()

        let capturedStdout =
            if request.SuppressStdoutEcho then
                let reader = proc.StandardOutput
                // A char buffer, not a byte one: `StreamReader.Read` decodes, so a chunk
                // boundary can never split a multi-byte character. Reading bytes here to
                // make the comment above literally true would introduce that bug.
                Some(
                    Tasks.Task.Run(fun () ->
                        let chunk = Array.zeroCreate<char> 4096
                        let mutable n = reader.Read(chunk, 0, chunk.Length)

                        while n > 0 do
                            lock captureBuffer (fun () -> captureBuffer.Append(chunk, 0, n) |> ignore)
                            n <- reader.Read(chunk, 0, chunk.Length)))
            else
                proc.BeginOutputReadLine()
                None

        proc.BeginErrorReadLine()

        let stdoutReaderCompleted: Tasks.Task =
            match capturedStdout with
            | Some task -> task
            | None -> stdoutClosed.Task :> Tasks.Task

        let allReadersCompleted =
            Tasks.Task.WhenAll [| stdoutReaderCompleted; stderrClosed.Task :> Tasks.Task |]

        // Wait for the leader marker or definitive stderr EOF, with the same
        // two-second upper bound as before. Deriving it from proc.Id is WRONG:
        // that is setsid's pid, in our own group. EOF is load-bearing for a
        // very short command: it proves no late marker callback remains, instead
        // of paying a flat 50ms and hoping the callback catches up.
        let pgid =
            Tasks.Task.WhenAny(
                [| reportedPgid.Task :> Tasks.Task
                   stderrClosed.Task :> Tasks.Task
                   Tasks.Task.Delay 2_000 |])
                .GetAwaiter()
                .GetResult()
            |> ignore

            if reportedPgid.Task.IsCompletedSuccessfully then
                Some reportedPgid.Task.Result
            else
                None

        // NOTE: never use the parameterless WaitForExit() with redirected
        // output. It waits for the *pipes* to close, and a backgrounded
        // grandchild inherits them — so a step that spawns a daemon would hang
        // the executor forever. Wait on the process handle only, then give the
        // async readers a bounded window to flush.
        let interrupted () =
            match request.Interrupt with
            | Some p -> (try p () with _ -> false)
            | None -> false

        // The CAUSE is decided once, at the moment the wait ends — re-sampling
        // `interrupted()` afterwards raced a sibling failing just after a deadline
        // expiry, flipping both the narration and the reported outcome.
        let waitForProcessExit (budgetMs: int64 option) =
            let expired () =
                // measured against `sw`, which started at the TOP of this run —
                // a clock created here would exclude script creation, launch, and
                // the up-to-two-second pgid wait, quietly extending every budget
                match budgetMs with
                | Some ms -> sw.ElapsedMilliseconds >= ms
                | None -> false

            let nextWaitMilliseconds () =
                let remaining =
                    budgetMs
                    |> Option.map (fun ms -> max 0L (ms - sw.ElapsedMilliseconds))

                // The interrupt contract is a predicate, not a wait handle, so
                // only interruptible steps still need a bounded re-sample. An
                // ordinary step sleeps on the process handle until exit or its
                // exact remaining deadline; there is no 10ms exit poll.
                match request.Interrupt, remaining with
                | Some _, Some ms -> int (min 10L ms)
                | Some _, None -> 10
                | None, Some ms -> int (min (int64 Int32.MaxValue) ms)
                | None, None -> Timeout.Infinite

            let mutable cause =
                if processExited.Task.IsCompleted || proc.HasExited then
                    WaitEnd.Exited
                else
                    WaitEnd.Waiting

            while cause = WaitEnd.Waiting do
                if processExited.Task.IsCompleted || proc.HasExited then
                    cause <- WaitEnd.Exited
                else
                    match expired (), interrupted () with
                    | false, false ->
                        let delay = Tasks.Task.Delay(nextWaitMilliseconds ())

                        let completed =
                            Tasks.Task.WhenAny(
                                [| processExited.Task :> Tasks.Task
                                   delay |])
                                .GetAwaiter()
                                .GetResult()

                        if Object.ReferenceEquals(completed, processExited.Task) then
                            cause <- WaitEnd.Exited
                    | false, true ->
                        // the LOCAL budget clock starts after launch overhead, so it
                        // can lag the walker's absolute deadline — an interrupt seen
                        // first here may still be the LATER event. The caller's
                        // ordering is authoritative in both directions.
                        let interruptFirst =
                            match request.InterruptBeatsDeadline with
                            | Some beats -> (try beats () with _ -> true)
                            | None -> true

                        cause <-
                            if interruptFirst || request.TimeoutMs.IsNone then
                                WaitEnd.Interrupted
                            else
                                WaitEnd.Expired
                    | true, _ ->
                        // EVERY observed expiry consults the caller's timestamps, not
                        // only a both-at-once tie: the sibling STAMP is written before
                        // its cancel signal fires, so the interrupt predicate can lag
                        // an event that was genuinely earlier.
                        let interruptFirst =
                            match request.InterruptBeatsDeadline with
                            | Some beats -> (try beats () with _ -> false)
                            | None -> false

                        cause <- if interruptFirst then WaitEnd.Interrupted else WaitEnd.Expired

            cause

        let waitForReaderCompletion (budgetMs: int) =
            // EOF/task completion, not an unchanged-length guess. A descendant
            // can hold a pipe open indefinitely, so completion is still bounded;
            // expiry keeps every byte already appended and never blocks the run.
            if not allReadersCompleted.IsCompleted then
                Tasks.Task.WhenAny(
                    [| allReadersCompleted
                       Tasks.Task.Delay budgetMs |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

        let settleBeforeSignal (budgetMs: int) =
            // RESIDUAL, deliberately not described as event-driven. Process does
            // not expose the instant at which bytes become queued DataReceived
            // callbacks, so pre/post-signal causality still needs the old bounded
            // quiet sample. This runs only on abort paths; ordinary completion no
            // longer pays it. The snapshots below remain the classification boundary.
            let clock = Stopwatch.StartNew()

            let drainActiveCallback gate =
                let remaining = max 0 (budgetMs - int clock.ElapsedMilliseconds)

                if Monitor.TryEnter(gate, remaining) then
                    Monitor.Exit gate

            // A callback already executing is knowable and should not be left
            // behind the snapshot. The shared clock keeps even hostile OnLine
            // callbacks inside the same upper bound. The quiet sample still
            // covers callbacks which Process has queued but not invoked yet.
            drainActiveCallback stdoutCallbackGate
            drainActiveCallback stderrCallbackGate

            settleOutputUntilQuiet
                budgetMs
                (fun () -> clock.ElapsedMilliseconds)
                Thread.Sleep
                (fun () ->
                    lock stdout (fun () -> stdout.Length), lock stderr (fun () -> stderr.Length))

        let waitEnd = waitForProcessExit request.TimeoutMs
        let finished = waitEnd = WaitEnd.Exited

        let outcome, termination =
            if finished then
                waitForReaderCompletion 500
                let code = proc.ExitCode

                let t =
                    if request.ReapGroup then
                        pgid |> Option.map (fun g -> reap g request.GraceMs)
                    else
                        None

                // On Linux a process killed by signal N exits 128+N.
                //
                // REVIEW FIXES (Codex P2 + Copilot, PR #11), both correct:
                //  * the range stopped at 164, covering signals 1..36, but Linux
                //    has signals up to 64 (exit 192), so a high-numbered signal was
                //    silently reported as an ordinary exit code;
                //  * this is a HEURISTIC and cannot be otherwise here. `setsid
                //    --wait` propagates "the same return", so the wait-status bit
                //    that distinguishes "killed by signal 9" from "exited 137" is
                //    already gone by the time .NET reports ExitCode. A script that
                //    deliberately calls `exit 137` is indistinguishable from one
                //    that was SIGKILLed. The diagnostic must therefore say "likely",
                //    and FG-033's claim is corrected accordingly — see the board.
                let outcome =
                    if code > 128 && code <= 192 then Signalled(code - 128) else Completed code

                outcome, t
            else
                // Jenkins' interrupt narration, in its words and its order — measured
                // on 2.568.1 — so the two logs COMPARE (FG-102) instead of each
                // engine's version being suppressed. `Terminated` then arrives from
                // the shell itself on both engines. The cause is the SNAPSHOT taken
                // when the wait ended, never a fresh sample.
                if waitEnd = WaitEnd.Expired then
                    request.OnLine |> Option.iter (fun f -> f "Cancelling nested steps due to timeout")

                request.OnLine |> Option.iter (fun f -> f "Sending interrupt signal to process")

                settleBeforeSignal 300
                let outputBeforeSignal = lock stdout (fun () -> stdout.ToString())
                let stderrBeforeSignal = lock stderr (fun () -> stderr.ToString())
                let t = pgid |> Option.map (fun g -> terminateGroup g request.GraceMs)
                waitForReaderCompletion 300

                // On Jenkins the wrapper shell survives to print `Terminated` for its
                // killed child; Fogell's SIGTERM reaches the WHOLE group, so nobody is
                // usually left alive to say it — the engine says it, in the same
                // position. UNLESS the script trapped the signal and said it itself:
                // synthesising unconditionally doubled the line for a trapping shell.
                let shellSaidIt =
                    let since (sink: Text.StringBuilder) (before: string) =
                        (lock sink (fun () -> sink.ToString())).Substring before.Length

                    [ since stdout outputBeforeSignal; since stderr stderrBeforeSignal ]
                    |> List.exists (fun afterSignal ->
                        afterSignal.Replace("\r\n", "\n").Split '\n'
                        |> Array.exists (fun line -> line.Trim() = "Terminated"))

                if not shellSaidIt then
                    request.OnLine |> Option.iter (fun f -> f "Terminated")
                // Distinguish the two ways a step can fail to finish. Both take
                // the same signal path; only the reported cause differs.
                (if waitEnd = WaitEnd.Interrupted then Cancelled else TimedOut), t

        sw.Stop()

        shebangFile |> Option.iter deleteShebang

        // Reader completion has already received its single bounded wait above. Never
        // wait on the capture task again here: an escaped descendant can retain the pipe,
        // and a second five-second wait made that earlier bound fictional. Snapshot the
        // incrementally-owned buffer; completed reads are exact and incomplete reads
        // retain every character which arrived before the bound.
        //
        // "BEST-EFFORT" NOW MEANS WHAT IT SAYS, and it took two goes. The bound was always
        // right; what was salvaged when it expired was not. First the fallback read
        // `stdout.ToString()`, which in capture mode is ALWAYS "" — the line handler is
        // not attached at all, so that sink is never filled — while the comment called the
        // value best-effort, implying partial output survived. It did not. Making the
        // fallback an explicit "" was honest about that and still WRONG (FG-181): bytes
        // that arrived before the bound are real captured output, and discarding them
        // hands the pipeline a wrong value under a build that reports success. The buffer
        // above keeps them, so an expired wait TRUNCATES what was captured rather than
        // ERASING it, and the timeout is no longer the difference between `token` and "".
        //
        // WHY NOT SIMPLY WAIT LONGER: the bound is what stops an escaped descendant from
        // holding the engine open forever, which is the one failure mode worse than a
        // wrong value. Truncation removes the need to choose between them.
        let capturedText =
            capturedStdout
            |> Option.map (fun _ -> lock captureBuffer (fun () -> captureBuffer.ToString()))

        let bufferedStdout = lock stdout (fun () -> stdout.ToString())
        let bufferedStderr = lock stderr (fun () -> stderr.ToString())

        { Outcome = outcome
          Stdout = defaultArg capturedText bufferedStdout
          Stderr = bufferedStderr
          DurationMs = sw.ElapsedMilliseconds
          ProcessGroupId = pgid
          Termination = termination
          CleanupFailure = cleanupFailure
          DurableId = mintedDurableId }
