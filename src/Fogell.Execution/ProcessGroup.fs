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

    let internal isExecutableFile path =
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
            |> Array.tryFind isExecutableFile)

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
      /// Exact token provenance for non-capture stdout which crossed the raw
      /// matcher. None means [Stdout] is raw/unredacted capture output.
      StdoutRedacted: RedactedText option
      /// True only when the stdout reader observed a real EOF. Capture mode may
      /// return a bounded snapshot while an escaped descendant holds the pipe.
      StdoutReachedEof: bool
      Stderr: string
      /// Exact token provenance for stderr which crossed the raw matcher.
      StderrRedacted: RedactedText option
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
      /// Engine-authored narration has not crossed [OutputRedaction]. Keep its
      /// publication provenance separate from process bytes so the caller can
      /// apply its ordinary run-wide mask. None preserves the direct-call API by
      /// falling back to [OnLine].
      OnGeneratedLine: (string -> unit) option
      /// Synchronous trace admission for engine-authored narration. Walker uses
      /// this under the same registration lock as raw matching; its external
      /// transport drains independently.
      OnGeneratedAdmission: (string -> unit) option
      /// Provenance-preserving callback used by Executor/Walker. When absent,
      /// redacted process lines fall back to decoded [OnLine] strings.
      OnRedactedLine: (RedactedText -> unit) option
      /// Synchronous trace admission invoked inside [OutputRedaction]'s lock.
      /// This closes the matcher-to-trace registration window without running
      /// a potentially slow external publisher under that lock.
      OnRedactedAdmission: (RedactedText -> unit) option
      /// FG-236. Opaque raw-output policy. Stdout and stderr each create an
      /// independent matcher before CR/LF framing.
      OutputRedaction: OutputRedactionPolicy option
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
          OnGeneratedLine = None
          OnGeneratedAdmission = None
          OnRedactedLine = None
          OnRedactedAdmission = None
          OutputRedaction = None
          SuppressStdoutEcho = false
          ReapGroup = true
          Interrupt = None
          InterruptBeatsDeadline = None
          WorkspaceRoot = None }

module ProcessGroup =

    /// Incremental equivalent of StreamReader.ReadLine: CR, LF and CRLF frame
    /// lines, while EOF publishes a final unterminated non-empty line. Keeping
    /// this after the raw masker is the ordering guarantee FG-236 requires.
    type internal RawLineFramer(publish: string -> unit) =
        let line = Text.StringBuilder()
        let mutable afterCr = false

        member _.Push(text: string) =
            for c in text do
                if afterCr && c = '\n' then
                    afterCr <- false
                else
                    afterCr <- false

                    if c = '\r' || c = '\n' then
                        publish (line.ToString())
                        line.Clear() |> ignore
                        afterCr <- c = '\r'
                    else
                        line.Append c |> ignore

        member _.Complete() =
            if line.Length > 0 then
                publish (line.ToString())
                line.Clear() |> ignore

    /// The same framing grammar with per-character redaction provenance kept
    /// beside every published line.
    type internal RedactedLineFramer(publish: RedactedText -> unit) =
        let line = RedactedTextBuilder()
        let mutable lineLength = 0
        let mutable afterCr = false

        member _.Push(value: RedactedText) =
            for index = 0 to value.Text.Length - 1 do
                let c = value.Text[index]

                if afterCr && c = '\n' then
                    afterCr <- false
                else
                    afterCr <- false

                    if c = '\r' || c = '\n' then
                        publish (line.ToRedactedText())
                        line.Clear()
                        lineLength <- 0
                        afterCr <- c = '\r'
                    else
                        if value.TokenCharacters[index] then
                            line.AppendProtected(string c)
                        else
                            line.AppendRaw c

                        lineLength <- lineLength + 1

        member _.Complete() =
            if lineLength > 0 then
                publish (line.ToRedactedText())
                line.Clear()
                lineLength <- 0

    [<RequireQualifiedAccess>]
    type internal ProcessIdentityState =
        | Matching of char
        | Absent
        | Changed
        | Uncertain

    type internal LinuxProcessStat =
        { State: char
          ParentProcessId: int
          ProcessGroupId: int
          ThreadCount: int
          StartTime: string }

    [<RequireQualifiedAccess>]
    type internal LinuxProcessObservation =
        | Present of LinuxProcessStat
        | Absent
        | Uncertain

    type internal RegisteredGroupIdentity =
        { GroupId: int
          LeaderStartTime: string
          AnchorPid: int
          AnchorStartTime: string }

    let internal parseLinuxProcessStat (value: string) =
        let close = value.LastIndexOf ')'

        if close < 0 || close + 2 >= value.Length then
            None
        else
            let fields =
                value.Substring(close + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries)

            match fields with
            | fields when fields.Length > 19 && fields[0].Length = 1 ->
                match Int32.TryParse fields[1], Int32.TryParse fields[2], Int32.TryParse fields[17] with
                | (true, parentProcessId), (true, processGroupId), (true, threadCount) ->
                    Some
                        { State = fields[0].[0]
                          ParentProcessId = parentProcessId
                          ProcessGroupId = processGroupId
                          ThreadCount = threadCount
                          StartTime = fields[19] }
                | _ -> None
            | _ -> None

    let private observeLinuxProcess pid =
        try
            match IO.File.ReadAllText($"/proc/{pid}/stat") |> parseLinuxProcessStat with
            | Some stat -> LinuxProcessObservation.Present stat
            | None -> LinuxProcessObservation.Uncertain
        with
        | :? IO.FileNotFoundException
        | :? IO.DirectoryNotFoundException -> LinuxProcessObservation.Absent
        | _ -> LinuxProcessObservation.Uncertain

    let internal classifyLiveGroupMembers pgid excludedIdentity observations =
        let mutable live = 0
        let mutable uncertain = false

        for pid, observation in observations do
            let isExcludedIdentity =
                match excludedIdentity, observation with
                | Some(excludedPid, excludedStartTime), LinuxProcessObservation.Present stat ->
                    pid = excludedPid && stat.StartTime = excludedStartTime
                | _ -> false

            if not isExcludedIdentity then
                match observation with
                | LinuxProcessObservation.Present stat when stat.ProcessGroupId = pgid ->
                    if (stat.State = 'Z' || stat.State = 'X' || stat.State = 'x')
                       && stat.ThreadCount <= 1 then
                        ()
                    else
                        // A thread-group leader may be defunct while sibling
                        // threads remain executable; num_threads distinguishes
                        // that live group from a final zombie remnant.
                        live <- live + 1
                | LinuxProcessObservation.Uncertain -> uncertain <- true
                | _ -> ()

        if uncertain then -1 else live

    // All adopted-child waitpid calls share this lock. A PID cannot be reused
    // until its zombie has been reaped; serializing our only reapers therefore
    // keeps the identity/group observation adjacent to the PID-specific wait.
    // Never use waitpid(-1): another concurrent step owns its direct children.
    let private registeredChildReapLock = obj ()

    let internal reapObservedRegisteredMember
        pgid
        candidatePid
        expectedStartTime
        requiredParentPid
        observe
        reap
        =
        lock registeredChildReapLock (fun () ->
            match observe () with
            | LinuxProcessObservation.Present stat
                when stat.ProcessGroupId = pgid
                     && stat.ParentProcessId = requiredParentPid
                     && (expectedStartTime
                         |> Option.forall (fun expected -> stat.StartTime = expected))
                     && (stat.State = 'Z' || stat.State = 'X' || stat.State = 'x')
                     && stat.ThreadCount <= 1 ->
                reap ()
                true
            | _ -> false)

    let internal observeLiveGroupCandidate pgid pid queryGroup observe =
        match queryGroup pid with
        | Native.ProcessGroupQuery.Found group when group = pgid ->
            match observe pid with
            | LinuxProcessObservation.Present stat when stat.ProcessGroupId <> pgid ->
                // Membership changed between getpgid and the stat read. The
                // numeric PID may have been reused, so this scan cannot prove
                // that the original group is empty.
                Some(pid, LinuxProcessObservation.Uncertain)
            | observation -> Some(pid, observation)
        | Native.ProcessGroupQuery.Found _
        | Native.ProcessGroupQuery.Absent -> None
        | Native.ProcessGroupQuery.Uncertain ->
            Some(pid, LinuxProcessObservation.Uncertain)

    let internal scanLiveGroupCandidates pgid pids queryGroup observe =
        let observations = ResizeArray<int * LinuxProcessObservation>()
        let initiallyForeign = ResizeArray<int>()

        let observeMatched pid =
            match observe pid with
            | LinuxProcessObservation.Present stat when stat.ProcessGroupId <> pgid ->
                pid, LinuxProcessObservation.Uncertain
            | observation -> pid, observation

        for pid in pids do
            match queryGroup pid with
            | Native.ProcessGroupQuery.Found group when group = pgid ->
                observations.Add(observeMatched pid)
            | Native.ProcessGroupQuery.Found _ ->
                // A process may join this group after the candidate filter
                // first sees it. Re-query these misses at the scan boundary.
                initiallyForeign.Add pid
            | Native.ProcessGroupQuery.Absent -> ()
            | Native.ProcessGroupQuery.Uncertain ->
                observations.Add(pid, LinuxProcessObservation.Uncertain)

        for pid in initiallyForeign do
            match queryGroup pid with
            | Native.ProcessGroupQuery.Found group when group = pgid ->
                observations.Add(observeMatched pid)
            | Native.ProcessGroupQuery.Found _
            | Native.ProcessGroupQuery.Absent -> ()
            | Native.ProcessGroupQuery.Uncertain ->
                observations.Add(pid, LinuxProcessObservation.Uncertain)

        observations |> Seq.toList

    let private scanLiveGroupMembers pgid excludedIdentity =
        try
            // The wait loops call this repeatedly. Use getpgid as the cheap
            // candidate filter and read stat only for possible members; ESRCH
            // is an ordinary exit race, while every other query failure keeps
            // the whole observation fail-closed.
            IO.Directory.GetDirectories "/proc"
            |> Array.choose (fun directory ->
                match Int32.TryParse(IO.Path.GetFileName directory) with
                | true, pid -> Some pid
                | _ -> None)
            |> fun pids ->
                scanLiveGroupCandidates pgid pids Native.queryProcessGroup observeLinuxProcess
            |> classifyLiveGroupMembers pgid excludedIdentity
        with _ ->
            -1

    let internal signalRegisteredGroup
        (identity: RegisteredGroupIdentity)
        observeLeader
        observeAnchor
        sendGroup
        signal
        =
        let matches expectedStartTime = function
            | LinuxProcessObservation.Present stat ->
                stat.StartTime = expectedStartTime
                && stat.ProcessGroupId = identity.GroupId
                && not ((stat.State = 'Z' || stat.State = 'X' || stat.State = 'x') && stat.ThreadCount <= 1)
            | _ -> false

        if matches identity.AnchorStartTime (observeAnchor ())
           || matches identity.LeaderStartTime (observeLeader ()) then
            sendGroup signal
        else
            false

    let internal signalRegisteredAnchor
        (identity: RegisteredGroupIdentity)
        observeAnchor
        sendAnchor
        signal
        =
        match observeAnchor () with
        | LinuxProcessObservation.Present stat
            when stat.StartTime = identity.AnchorStartTime
                 && stat.ProcessGroupId = identity.GroupId
                 && not ((stat.State = 'Z' || stat.State = 'X' || stat.State = 'x') && stat.ThreadCount <= 1) ->
            sendAnchor signal
        | _ -> false

    let internal attemptEscalation exitedOnTerm sendKill waitAfterKill =
        if exitedOnTerm then
            false
        else
            let delivered = sendKill ()

            if delivered then
                waitAfterKill ()

            delivered

    let internal deliverOrObserveExtinction sendSignal observeExtinct =
        sendSignal () || observeExtinct ()

    /// The marker write happens before the child stops itself. A SIGCONT sent
    /// merely because the marker arrived can therefore be lost while the child
    /// is still running, after which it executes SIGSTOP and wedges forever.
    /// Wait for the identity-bound /proc state transition under one strict bound.
    let internal waitForIdentityStop
        maxRunningChecks
        (observe: unit -> ProcessIdentityState)
        (pause: unit -> unit)
        =
        if maxRunningChecks < 0 then
            invalidArg (nameof maxRunningChecks) "running-state check count cannot be negative"

        let rec wait remaining =
            match observe () with
            | ProcessIdentityState.Matching('T' | 't') -> true
            | ProcessIdentityState.Matching _ when remaining > 0 ->
                pause ()
                wait (remaining - 1)
            | _ -> false

        wait maxRunningChecks

    [<RequireQualifiedAccess>]
    type internal LauncherFormationState =
        | SameIdentity of processGroup: int
        | Absent
        | Changed
        | Uncertain

    [<RequireQualifiedAccess>]
    type internal LauncherFormationDecision =
        | GroupFormed
        | Disappeared
        | TerminateLauncher
        | Refused

    /// The EOF guard can wake after fork but before the child has executed
    /// `setsid`. Its numeric pid is not yet a safe negative-pgid signal target at
    /// that instant. Keep the SAME start-time-bound identity under observation
    /// until it becomes its own group, disappears, or the bounded proof fails.
    /// The out-of-process shell guard below mirrors this decision table because
    /// it must survive the managed owner being SIGKILLed.
    let internal waitForIdentityGroup
        pid
        maxPendingChecks
        (observe: unit -> LauncherFormationState)
        (pause: unit -> unit)
        =
        if maxPendingChecks < 0 then
            invalidArg (nameof maxPendingChecks) "pending-state check count cannot be negative"

        let rec wait remaining =
            match observe () with
            | LauncherFormationState.SameIdentity processGroup when processGroup = pid ->
                LauncherFormationDecision.GroupFormed
            | LauncherFormationState.SameIdentity _ when remaining > 0 ->
                pause ()
                wait (remaining - 1)
            | LauncherFormationState.SameIdentity _ ->
                LauncherFormationDecision.TerminateLauncher
            | LauncherFormationState.Absent -> LauncherFormationDecision.Disappeared
            | _ -> LauncherFormationDecision.Refused

        wait maxPendingChecks

    /// Count processes still in the group. Reads /proc directly rather than
    /// shelling out, so the check cannot itself spawn something.
    /// -1 means UNKNOWN: the /proc read itself failed. FG-103 — returning 0 there
    /// made the leak check a gate that could not fail: a broken /proc reported
    /// "nothing survived" while FG-032's headline claim rested on this number, and
    /// both unit tests asserting 0 passed against a completely dead reader.
    /// Unknown fails CLOSED everywhere downstream: the group is treated as still
    /// populated, and the diagnostic says the check was unavailable rather than
    /// inventing a clean bill.
    let private survivorsIn (pgid: int) : int = scanLiveGroupMembers pgid None

    /// Wait until the group is EMPTY, up to `budgetMs`.
    let private waitForGroupExit (pgid: int) (budgetMs: int) : bool =
        let sw = Stopwatch.StartNew()
        let mutable gone = survivorsIn pgid = 0

        while not gone && sw.ElapsedMilliseconds < int64 budgetMs do
            Thread.Sleep 20
            gone <- survivorsIn pgid = 0

        gone

    let private survivorsInExceptAnchor (identity: RegisteredGroupIdentity) : int =
        scanLiveGroupMembers
            identity.GroupId
            (Some(identity.AnchorPid, identity.AnchorStartTime))

    let private waitForGroupExitExceptAnchor identity budgetMs =
        let sw = Stopwatch.StartNew()
        let mutable gone = survivorsInExceptAnchor identity = 0

        while not gone && sw.ElapsedMilliseconds < int64 budgetMs do
            Thread.Sleep 20
            gone <- survivorsInExceptAnchor identity = 0

        gone

    let private waitForRegisteredGroupExit identity budgetMs =
        let sw = Stopwatch.StartNew()

        let reapKnownGroupChildren () =
            try
                IO.Directory.GetDirectories "/proc"
                |> Array.iter (fun directory ->
                    match Int32.TryParse(IO.Path.GetFileName directory) with
                    | true, pid ->
                        match Native.queryProcessGroup pid with
                        | Native.ProcessGroupQuery.Found group when group = identity.GroupId ->
                            // PR_SET_CHILD_SUBREAPER makes orphaned group
                            // members our children. Revalidate membership under
                            // the shared reaper lock before PID-specific waitpid,
                            // so an already-reaped/reused PID from a concurrent
                            // step cannot have its exit status stolen.
                            reapObservedRegisteredMember
                                identity.GroupId
                                pid
                                None
                                Environment.ProcessId
                                (fun () -> observeLinuxProcess pid)
                                (fun () -> Native.tryReapChild pid |> ignore)
                            |> ignore
                        | _ -> ()
                    | _ -> ())
            with _ -> ()

        let rec wait () =
            // Once the session leader exits, PR_SET_CHILD_SUBREAPER reparents
            // its orphaned group members to Run.Host. Reap every adopted PID
            // observed in this registered group; PID-specific waitpid never
            // harvests an unrelated step. The following group ESRCH is the
            // atomic boundary: while the numeric group exists, a same-session
            // process can still join it.
            reapKnownGroupChildren ()

            match Native.probeProcessGroup identity.GroupId with
            | Native.ProcessGroupPresence.Absent -> true
            | _ when sw.ElapsedMilliseconds < int64 budgetMs ->
                Thread.Sleep 20
                wait ()
            | _ -> false

        wait ()

    let private registeredGroupLeakCount identity =
        match Native.probeProcessGroup identity.GroupId with
        | Native.ProcessGroupPresence.Absent -> 0
        | _ ->
            match survivorsIn identity.GroupId with
            | count when count > 0 -> count
            | _ -> -1

    let private releaseIdentityAnchor identity =
        // The anchor is SIGSTOPped before registration. TERM remains pending
        // while stopped, so CONT lets it take the default TERM action without
        // introducing another long-lived helper or inherited descriptor.
        reapObservedRegisteredMember
            identity.GroupId
            identity.AnchorPid
            (Some identity.AnchorStartTime)
            Environment.ProcessId
            (fun () -> observeLinuxProcess identity.AnchorPid)
            (fun () -> Native.tryReapChild identity.AnchorPid |> ignore)
        |> ignore

        if Native.probeProcessGroup identity.GroupId = Native.ProcessGroupPresence.Absent then
            true
        else
            let extinct () =
                Native.probeProcessGroup identity.GroupId = Native.ProcessGroupPresence.Absent

            let termDeliveredOrGone =
                deliverOrObserveExtinction
                    (fun () ->
                        signalRegisteredAnchor
                            identity
                            (fun () -> observeLinuxProcess identity.AnchorPid)
                            (fun signal -> Native.signalProcess identity.AnchorPid signal)
                            Native.SIGTERM)
                    extinct

            if not termDeliveredOrGone then
                false
            elif extinct () then
                true
            else
                deliverOrObserveExtinction
                    (fun () ->
                        signalRegisteredAnchor
                            identity
                            (fun () -> observeLinuxProcess identity.AnchorPid)
                            (fun signal -> Native.signalProcess identity.AnchorPid signal)
                            Native.SIGCONT)
                    extinct
                && waitForRegisteredGroupExit identity 2_000

    let private terminateGroupWithAnchor identity graceMs =
        let sendGroup signal =
            signalRegisteredGroup
                identity
                (fun () -> observeLinuxProcess identity.GroupId)
                (fun () -> observeLinuxProcess identity.AnchorPid)
                (fun signum -> Native.signalGroup identity.GroupId signum)
                signal

        let termDelivered = sendGroup Native.SIGTERM
        let usersExited =
            termDelivered && waitForGroupExitExceptAnchor identity graceMs
        let exitedOnTerm = usersExited && releaseIdentityAnchor identity

        let escalated =
            attemptEscalation
                exitedOnTerm
                (fun () -> sendGroup Native.SIGKILL)
                (fun () -> waitForRegisteredGroupExit identity 2_000 |> ignore)

        { GracefulExit = exitedOnTerm
          Escalated = escalated
          LeakedProcesses = registeredGroupLeakCount identity }

    let private reapWithAnchor identity graceMs =
        if survivorsInExceptAnchor identity = 0
           && releaseIdentityAnchor identity then
            { GracefulExit = true
              Escalated = false
              LeakedProcesses = 0 }
        else
            terminateGroupWithAnchor identity graceMs

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
        let requiredQuietSamples = 2

        if elapsedMilliseconds () < int64 budgetMs then
            let mutable previous = snapshot ()
            let mutable consecutiveQuietSamples = 0

            while
                consecutiveQuietSamples < requiredQuietSamples
                && elapsedMilliseconds () < int64 budgetMs do
                let remaining = max 0L (int64 budgetMs - elapsedMilliseconds ())
                let sleepMs = int (min 40L remaining)

                if sleepMs > 0 then
                    sleep sleepMs

                let current = snapshot ()

                if current = previous then
                    consecutiveQuietSamples <- consecutiveQuietSamples + 1
                else
                    consecutiveQuietSamples <- 0

                previous <- current

    let internal preCaptureFallbackScript =
        "wait \"$fogell_inner\" 2>/dev/null || true; exit 125; "

    /// Run one command in its own process group.
    let run (request: RunRequest) : RunResult =
        let sw = Stopwatch.StartNew()

        let containmentDirectory =
            match Environment.GetEnvironmentVariable "FOGELL_PROCESS_GROUP_REGISTRY" with
            | null
            | "" -> None
            | value when OperatingSystem.IsLinux() -> Some(IO.Path.GetFullPath value)
            | _ -> invalidOp "process-group registry containment is supported only on Linux"

        let processStateAndStartTime pid =
            match observeLinuxProcess pid with
            | LinuxProcessObservation.Present stat ->
                ProcessIdentityState.Matching stat.State, Some stat.StartTime
            | LinuxProcessObservation.Absent -> ProcessIdentityState.Absent, None
            | LinuxProcessObservation.Uncertain -> ProcessIdentityState.Uncertain, None

        let observeIdentity pid expectedStartTime =
            match processStateAndStartTime pid with
            | ProcessIdentityState.Matching state, Some actual when actual = expectedStartTime ->
                ProcessIdentityState.Matching state
            | ProcessIdentityState.Matching _, Some _ -> ProcessIdentityState.Changed
            | observation, _ -> observation

        let groupRecordPath pgid =
            containmentDirectory
            |> Option.map (fun directory -> IO.Path.Combine(directory, $"{pgid}.group"))

        let persistGroup pgid startTime anchor anchorStartTime =
            match containmentDirectory with
            | None -> ()
            | Some directory ->
                IO.Directory.CreateDirectory directory |> ignore
                let target = IO.Path.Combine(directory, $"{pgid}.group")
                let temporary = target + $".{Guid.NewGuid():N}.tmp"
                IO.File.WriteAllText(temporary, $"{pgid} {startTime} {anchor} {anchorStartTime}\n")
                IO.File.Move(temporary, target)

        let forgetExtinguishedGroup pgid =
            if Native.probeProcessGroup pgid = Native.ProcessGroupPresence.Absent then
                groupRecordPath pgid
                |> Option.iter (fun path ->
                    try IO.File.Delete path with _ -> ())

        // `setsid --wait` keeps a parent around to collect the exit status, but
        // that parent is what .NET reports as the process id — and ITS group is
        // ours, not the child's. So the session leader reports its own pid on
        // stderr as the first line, and that is the real process-group id.
        let pgidMarker = "__FOGELL_PGID "

        let launcherIdentityPath =
            IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-launch-{Guid.NewGuid():N}.identity")

        use _launcherIdentityCleanup =
            { new IDisposable with
                member _.Dispose() =
                    for path in [ launcherIdentityPath; launcherIdentityPath + ".tmp" ] do
                        try IO.File.Delete path with _ -> () }

        let psi = ProcessStartInfo("/bin/sh")
        // Keep an explicit wait-status-preserving parent outside the new group.
        // This makes marker provenance observable (proc.Id is the outer waiter,
        // not the session leader) without `setsid --fork --wait`: util-linux maps
        // a signal-killed child to the bare signal number, whereas the shell's
        // wait status preserves the established 128+signal diagnostic contract.
        // fd 9 explicitly carries the controller-owned liveness pipe into the
        // async guard: POSIX shells otherwise replace an asynchronous command's
        // stdin with /dev/null. The inner session closes it before user code, so
        // only Run.Host can keep the guard armed.
        psi.ArgumentList.Add "-c"
        psi.ArgumentList.Add(
            "fogell_payload=$1; fogell_inner_argv0=$2; fogell_inner_argv1=$3; "
            + "fogell_registry=$4; fogell_launch_gate=$5; fogell_launch_ready=$6; fogell_guard_observed=$7; "
            + "fogell_guard_stopped=$8; fogell_cleanup_release=$9; "
            + "fogell_watchdog_term_file=${10}; fogell_watchdog_kill_release=${11}; "
            + "fogell_launcher_identity=${12}; exec 9<&0; "
            + "(if ! IFS= read -r fogell_self_stat < /proc/self/stat; then exit 125; fi; "
            + "fogell_self_pid=${fogell_self_stat%% *}; fogell_self_tail=${fogell_self_stat##*) }; set -- $fogell_self_tail; "
            + "if [ \"$#\" -le 19 ]; then exit 125; fi; shift 19; fogell_self_start=$1; "
            + "if ! printf '%s %s\\n' \"$fogell_self_pid\" \"$fogell_self_start\" > \"$fogell_launcher_identity.tmp\" "
            + "|| ! /bin/mv \"$fogell_launcher_identity.tmp\" \"$fogell_launcher_identity\"; then exit 125; fi; "
            + "if [ -n \"$fogell_launch_gate\" ]; then while [ ! -e \"$fogell_launch_gate\" ]; do /bin/sleep 0.01; done; fi; "
            + "exec /usr/bin/setsid /bin/sh -c \"$fogell_payload\" \"$fogell_inner_argv0\" \"$fogell_inner_argv1\" \"$fogell_registry\" 9<&- </dev/null) & "
            + "fogell_inner=$!; "
            + "fogell_launcher_start=; fogell_capture_checks=200; "
            + "while [ -z \"$fogell_launcher_start\" ] && [ \"$fogell_capture_checks\" -ge 0 ]; do "
            + "if [ -r \"$fogell_launcher_identity\" ] "
            + "&& IFS=' ' read -r fogell_identity_pid fogell_identity_start < \"$fogell_launcher_identity\" "
            + "&& [ \"$fogell_identity_pid\" = \"$fogell_inner\" ] && [ -n \"$fogell_identity_start\" ]; then "
            + "fogell_launcher_start=$fogell_identity_start; "
            + "elif [ ! -e \"/proc/$fogell_inner\" ]; then break; fi; "
            + "if [ -z \"$fogell_launcher_start\" ]; then fogell_capture_checks=$((fogell_capture_checks - 1)); /bin/sleep 0.01; fi; "
            + "done; "
            + "/bin/rm -f \"$fogell_launcher_identity\" \"$fogell_launcher_identity.tmp\"; "
            + "if [ -z \"$fogell_launcher_start\" ]; then "
            // Without the child-published start ticks there is no authority to
            // signal either the numeric PID or its same-number group. Waiting
            // through the shell job table cannot hit a reused process.
            + preCaptureFallbackScript
            + "fi; "
            + "(trap '' HUP INT TERM; "
            + "if ! IFS= read -r fogell_guard_command || [ \"$fogell_guard_command\" != disarm ]; then "
            + "fogell_record=\"$fogell_registry/$fogell_inner.group\"; fogell_safe=0; fogell_kill_launcher=0; "
            + "if [ -n \"$fogell_registry\" ] && [ -r \"$fogell_record\" ] "
            + "&& IFS=' ' read -r fogell_record_pgid fogell_leader_start fogell_anchor fogell_anchor_start < \"$fogell_record\" "
            + "&& [ \"$fogell_record_pgid\" = \"$fogell_inner\" ] && [ \"$fogell_anchor\" -gt 1 ] 2>/dev/null "
            + "&& [ -r \"/proc/$fogell_anchor/stat\" ]; then "
            + "fogell_stat=$(/bin/cat \"/proc/$fogell_anchor/stat\" 2>/dev/null) || fogell_stat=; "
            + "fogell_tail=${fogell_stat##*) }; set -- $fogell_tail; "
            + "if [ \"$3\" = \"$fogell_inner\" ]; then shift 19; [ \"$1\" = \"$fogell_anchor_start\" ] && fogell_safe=1; fi; "
            + "elif [ -n \"$fogell_launcher_start\" ]; then "
            + "fogell_checks=200; fogell_polling=1; "
            + "while [ \"$fogell_polling\" -eq 1 ]; do "
            + "if [ ! -r \"/proc/$fogell_inner/stat\" ] || ! IFS= read -r fogell_stat < \"/proc/$fogell_inner/stat\"; then break; fi; "
            + "fogell_tail=${fogell_stat##*) }; set -- $fogell_tail; "
            + "if [ \"$#\" -le 19 ]; then break; fi; "
            + "fogell_pgrp=$3; shift 19; fogell_actual_start=$1; "
            + "if [ \"$fogell_actual_start\" != \"$fogell_launcher_start\" ]; then break; fi; "
            + "if [ \"$fogell_pgrp\" = \"$fogell_inner\" ]; then fogell_safe=1; break; fi; "
            + "if [ -n \"$fogell_guard_observed\" ]; then printf observed > \"$fogell_guard_observed\"; fogell_guard_observed=; fi; "
            + "if [ \"$fogell_checks\" -le 0 ]; then "
            + "if /bin/kill -STOP \"$fogell_inner\" 2>/dev/null; then "
            + "fogell_stop_checks=200; "
            + "while [ \"$fogell_stop_checks\" -ge 0 ]; do "
            + "if [ ! -r \"/proc/$fogell_inner/stat\" ] || ! IFS= read -r fogell_frozen_stat < \"/proc/$fogell_inner/stat\"; then break; fi; "
            + "fogell_frozen_tail=${fogell_frozen_stat##*) }; set -- $fogell_frozen_tail; "
            + "if [ \"$#\" -le 19 ]; then break; fi; "
            + "fogell_frozen_state=$1; fogell_frozen_pgrp=$3; shift 19; fogell_frozen_start=$1; "
            + "if [ \"$fogell_frozen_start\" != \"$fogell_launcher_start\" ]; then break; fi; "
            + "if [ \"$fogell_frozen_state\" = T ] || [ \"$fogell_frozen_state\" = t ]; then "
            + "if [ \"$fogell_frozen_pgrp\" = \"$fogell_inner\" ]; then fogell_safe=1; else fogell_kill_launcher=1; fi; "
            + "if [ -n \"$fogell_guard_stopped\" ]; then printf '%s' \"$fogell_frozen_pgrp\" > \"$fogell_guard_stopped\"; fi; "
            + "if [ -n \"$fogell_cleanup_release\" ]; then while [ ! -e \"$fogell_cleanup_release\" ]; do /bin/sleep 0.01; done; fi; "
            + "break; fi; "
            + "fogell_stop_checks=$((fogell_stop_checks - 1)); /bin/sleep 0.01; "
            + "done; fi; break; fi; "
            + "fogell_checks=$((fogell_checks - 1)); /bin/sleep 0.01; "
            + "done; "
            + "fi; "
            + "fogell_identity_matches() { "
            + "fogell_check_pid=$1; fogell_check_start=$2; "
            + "[ \"$fogell_check_pid\" -gt 1 ] 2>/dev/null && [ -n \"$fogell_check_start\" ] "
            + "&& [ -r \"/proc/$fogell_check_pid/stat\" ] || return 1; "
            + "fogell_check_stat=$(/bin/cat \"/proc/$fogell_check_pid/stat\" 2>/dev/null) || return 1; "
            + "fogell_check_tail=${fogell_check_stat##*) }; set -- $fogell_check_tail; "
            + "[ \"$#\" -gt 19 ] && [ \"$3\" = \"$fogell_inner\" ] || return 1; "
            // POSIX shells parse `$18` as `${1}8`. Braces are required here:
            // field 20 is the thread count, and a Z/X/x group leader with live
            // sibling threads must remain signal-authorizing evidence.
            + "fogell_check_state=$1; fogell_check_threads=${18}; shift 19; "
            + "[ \"$1\" = \"$fogell_check_start\" ] "
            + "&& { [ \"$fogell_check_state\" != Z ] && [ \"$fogell_check_state\" != X ] "
            + "&& [ \"$fogell_check_state\" != x ] || [ \"$fogell_check_threads\" -gt 1 ] 2>/dev/null; }; "
            + "}; "
            + "fogell_group_authorized() { "
            + "fogell_identity_matches \"${fogell_anchor:-0}\" \"${fogell_anchor_start:-}\" "
            + "|| fogell_identity_matches \"$fogell_inner\" \"${fogell_leader_start:-$fogell_launcher_start}\"; "
            + "}; "
            + "fogell_launcher_authorized() { "
            + "[ \"$fogell_inner\" -gt 1 ] 2>/dev/null && [ -n \"$fogell_launcher_start\" ] "
            + "&& [ -r \"/proc/$fogell_inner/stat\" ] || return 1; "
            + "fogell_launcher_stat=$(/bin/cat \"/proc/$fogell_inner/stat\" 2>/dev/null) || return 1; "
            + "fogell_launcher_tail=${fogell_launcher_stat##*) }; set -- $fogell_launcher_tail; "
            + "[ \"$#\" -gt 19 ] || return 1; fogell_launcher_state=$1; shift 19; "
            + "[ \"$1\" = \"$fogell_launcher_start\" ] "
            + "&& { [ \"$fogell_launcher_state\" = T ] || [ \"$fogell_launcher_state\" = t ]; }; "
            + "}; "
            // This watchdog inherits the build environment, including PATH.
            // Keep every cleanup executable absolute: if a trusted Linux
            // utility is unavailable the shell advances immediately to the
            // next signal instead of letting build-controlled code delay KILL.
            + "if [ \"$fogell_safe\" -eq 1 ]; then "
            + "if fogell_group_authorized; then /bin/kill -TERM -- -$fogell_inner 2>/dev/null || true; "
            + "if [ -n \"$fogell_watchdog_term_file\" ]; then printf term > \"$fogell_watchdog_term_file\"; fi; "
            + "if [ -n \"$fogell_watchdog_kill_release\" ]; then while [ ! -e \"$fogell_watchdog_kill_release\" ]; do /bin/sleep 0.01; done; fi; "
            + "fi; /bin/sleep 0.2; "
            + "if fogell_group_authorized; then /bin/kill -KILL -- -$fogell_inner 2>/dev/null || true; fi; /bin/sleep 0.2; "
            + "elif [ \"$fogell_kill_launcher\" -eq 1 ] && fogell_launcher_authorized; then "
            + "/bin/kill -KILL \"$fogell_inner\" 2>/dev/null || true; /bin/sleep 0.2; "
            + "fi; "
            + "fi) <&9 >/dev/null 2>&1 & fogell_guard=$!; "
            + "if [ -n \"$fogell_launch_ready\" ]; then printf '%s' \"$fogell_inner\" > \"$fogell_launch_ready\"; fi; "
            + "exec 9<&-; "
            + "wait $fogell_inner; fogell_rc=$?; "
            + "exit $fogell_rc")
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
        let registeredPrelude =
            "fogell_anchor=0; "
            + "if [ -n \"$2\" ]; then /bin/sleep 2147483647 </dev/null >/dev/null 2>&1 & fogell_anchor=$!; kill -STOP \"$fogell_anchor\"; fi; "
            + $"printf '%%s%%s %%s\n' '{pgidMarker}' \"$$\" \"$fogell_anchor\" >&2; kill -STOP \"$$\"; "

        (match request.Command.StartsWith "#!" with
         | true ->
             psi.ArgumentList.Add
                 $"{registeredPrelude}exec \"$1\"{mergeStderr}"
         | false ->
             psi.ArgumentList.Add
                 $"{registeredPrelude}exec /bin/sh -xe \"$1\"{mergeStderr}")

        // These are $1..$4 of the outer waiter, and therefore command/$0/$1/$2
        // of the inner shell launched by setsid.
        psi.ArgumentList.Add "fogell-launcher"
        psi.ArgumentList.Add(defaultArg shebangFile request.Command)
        psi.ArgumentList.Add(defaultArg containmentDirectory "")
        // Test-only controller seams. They are read before the child environment
        // is cleared and travel positionally, so a build cannot set them through
        // withEnv/environment. Production leaves all of them empty.
        psi.ArgumentList.Add(defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "FOGELL_TEST_PRE_SETSID_RELEASE_FILE")) "")
        psi.ArgumentList.Add(defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "FOGELL_TEST_PRE_SETSID_READY_FILE")) "")
        psi.ArgumentList.Add(defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "FOGELL_TEST_PRE_SETSID_OBSERVED_FILE")) "")
        psi.ArgumentList.Add(defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "FOGELL_TEST_PRE_SETSID_STOPPED_FILE")) "")
        psi.ArgumentList.Add(defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "FOGELL_TEST_PRE_SETSID_CLEANUP_RELEASE_FILE")) "")
        psi.ArgumentList.Add(defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "FOGELL_TEST_WATCHDOG_TERM_FILE")) "")
        psi.ArgumentList.Add(defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "FOGELL_TEST_WATCHDOG_KILL_RELEASE_FILE")) "")
        psi.ArgumentList.Add launcherIdentityPath

        psi.WorkingDirectory <- request.WorkingDirectory
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.RedirectStandardInput <- true
        psi.UseShellExecute <- false

        LaunchEnvironment.applyBuildTo psi request.Environment

        match containmentDirectory with
        | Some _ when not (Native.enableChildSubreaper ()) ->
            invalidOp "could not establish Linux child-subreaper ownership for process-group anchors"
        | _ -> ()

        use proc = new Process()
        proc.StartInfo <- psi
        proc.EnableRaisingEvents <- true

        let stdout = Text.StringBuilder()
        let stderr = Text.StringBuilder()
        let stdoutRedacted = RedactedTextBuilder()
        let stderrRedacted = RedactedTextBuilder()
        let completionOptions = Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        let reportedPgid = Tasks.TaskCompletionSource<int * int>(completionOptions)
        let stdoutClosed = Tasks.TaskCompletionSource<unit>(completionOptions)
        let stderrClosed = Tasks.TaskCompletionSource<unit>(completionOptions)
        let processExited = Tasks.TaskCompletionSource<unit>(completionOptions)
        let stdoutCallbackGate = obj ()
        let stderrCallbackGate = obj ()
        let lineCallbackGate = obj ()
        let mutable lineCallbackTail: Tasks.Task = Tasks.Task.CompletedTask
        let mutable lineCallbacksOpen = true

        proc.Exited.Add(fun _ -> processExited.TrySetResult(()) |> ignore)

        let enqueueAction action =
            match action with
            | None -> ()
            | Some callback ->
                lock lineCallbackGate (fun () ->
                    if lineCallbacksOpen then
                        // Process invokes DataReceived handlers serially. Running a
                        // hostile user callback in that handler therefore prevents an
                        // already-written following line from reaching the buffer and
                        // makes the pre-signal snapshot misclassify it. Keep delivery
                        // serialized, but move it onto an asynchronous continuation so
                        // reader ingestion and EOF can advance independently.
                        lineCallbackTail <-
                            lineCallbackTail.ContinueWith(
                                Action<Tasks.Task>(fun previous ->
                                    // Keep a failed output sink sticky. Awaiting only
                                    // the final continuation is sufficient because
                                    // every successor first propagates its antecedent.
                                    previous.GetAwaiter().GetResult()
                                    callback ()),
                                CancellationToken.None,
                                Tasks.TaskContinuationOptions.None,
                                Tasks.TaskScheduler.Default))

        let enqueueLine callback line =
            enqueueAction (callback |> Option.map (fun publish -> fun () -> publish line))

        let publishLine line = enqueueLine request.OnLine line

        let publishRedactedLine line =
            match request.OnRedactedAdmission, request.OnRedactedLine with
            | Some admit, _ -> admit line
            | None, Some publish -> enqueueAction (Some(fun () -> publish line))
            | None, None -> enqueueLine request.OnLine line.Text

        let publishGeneratedLine line =
            match request.OnGeneratedAdmission with
            | Some admit ->
                match request.OutputRedaction with
                | Some policy -> policy.Synchronize(fun () -> admit line)
                | None -> admit line
            | None -> enqueueLine (request.OnGeneratedLine |> Option.orElse request.OnLine) line

        let closeAndGetLineCallbackTail () =
            lock lineCallbackGate (fun () ->
                lineCallbacksOpen <- false
                lineCallbackTail)

        let emit (sink: Text.StringBuilder) (line: string) =
            if line <> null then
                lock sink (fun () -> sink.AppendLine line |> ignore)
                publishLine line

        let emitRedacted (sink: Text.StringBuilder) (taggedSink: RedactedTextBuilder) (line: RedactedText) =
            lock sink (fun () ->
                sink.AppendLine line.Text |> ignore
                taggedSink.AppendLine line)

            publishRedactedLine line

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
                // Serialize the reader callback through buffer append and callback
                // enqueue, so EOF is a real reader-drained barrier. User callback
                // execution itself is deliberately outside this reader.
                lock stdoutCallbackGate (fun () ->
                    match e.Data with
                    | null -> stdoutClosed.TrySetResult(()) |> ignore
                    | line -> emit stdout line))

        let handleStderrLine (line: string) =
            if line.StartsWith(pgidMarker, StringComparison.Ordinal) then
                // the leader's own pid: the real group id. Never surfaced to the
                // caller as build output.
                match
                    line.Substring(pgidMarker.Length).Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries)
                with
                | [| rawPid; rawAnchor |] ->
                    match Int32.TryParse rawPid, Int32.TryParse rawAnchor with
                    | (true, pid), (true, anchor) -> reportedPgid.TrySetResult(pid, anchor) |> ignore
                    | _ -> ()
                | _ -> ()
            else
                emit stderr line

        let handleRedactedStderrLine (line: RedactedText) =
            if line.Text.StartsWith(pgidMarker, StringComparison.Ordinal) then
                handleStderrLine line.Text
            else
                emitRedacted stderr stderrRedacted line

        proc.ErrorDataReceived.Add(fun e ->
            lock stderrCallbackGate (fun () ->
                match e.Data with
                | null -> stderrClosed.TrySetResult(()) |> ignore
                | line -> handleStderrLine line))

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

        let startRedactingReader
            (reader: IO.StreamReader)
            callbackGate
            (closed: Tasks.TaskCompletionSource<unit>)
            publish
            stripInitialControlFrame
            =
            let policy = request.OutputRedaction.Value

            task {
                let masker = policy.CreateMatcher()
                let framer = RedactedLineFramer publish
                let controlPrefix = Text.StringBuilder()
                let mutable controlResolved = not stripInitialControlFrame
                let mutable reachedEof = false

                let feedBuildOutput text =
                    if not (String.IsNullOrEmpty text) then
                        framer.Push(masker.PushRedacted text)

                let feed text =
                    if controlResolved then
                        feedBuildOutput text
                    else
                        controlPrefix.Append text |> ignore
                        let buffered = controlPrefix.ToString()
                        let newline = buffered.IndexOf '\n'

                        if newline >= 0 then
                            let controlLine = buffered.Substring(0, newline).TrimEnd '\r'
                            controlResolved <- true

                            if controlLine.StartsWith(pgidMarker, StringComparison.Ordinal) then
                                // Parse the private bootstrap frame before any
                                // credential form can alter its pid fields.
                                handleStderrLine controlLine
                                feedBuildOutput (buffered.Substring(newline + 1))
                            else
                                feedBuildOutput buffered

                            controlPrefix.Clear() |> ignore
                        elif buffered.Length > pgidMarker.Length + 64 then
                            // The bootstrap frame is fixed and short. If it is
                            // malformed, stop granting it unbounded private state
                            // and treat the bytes as ordinary build output.
                            controlResolved <- true
                            feedBuildOutput buffered
                            controlPrefix.Clear() |> ignore

                try
                    let chunk = Array.zeroCreate<char> 4096
                    let mutable reading = true

                    while reading do
                        let! n = reader.ReadAsync(chunk, 0, chunk.Length)

                        if n > 0 then
                            lock callbackGate (fun () ->
                                policy.Synchronize(fun () -> feed (String(chunk, 0, n))))
                        else
                            reading <- false

                    reachedEof <- true

                    lock callbackGate (fun () ->
                        policy.Synchronize(fun () ->
                            if not controlResolved && controlPrefix.Length > 0 then
                                feedBuildOutput (controlPrefix.ToString())
                                controlPrefix.Clear() |> ignore

                            framer.Push(masker.CompleteRedacted())
                            framer.Complete()))
                finally
                    // On an exceptional/cut-off read, never flush an ambiguous
                    // pending prefix. A real EOF is the sole authority to do so.
                    if not reachedEof then
                        controlPrefix.Clear() |> ignore

                    closed.TrySetResult(()) |> ignore
            }
            :> Tasks.Task

        let redactingStdoutReader =
            match request.OutputRedaction, request.SuppressStdoutEcho with
            | Some _, false ->
                Some(
                    startRedactingReader
                        proc.StandardOutput
                        stdoutCallbackGate
                        stdoutClosed
                        (emitRedacted stdout stdoutRedacted)
                        false)
            | _ -> None

        let redactingStderrReader =
            match request.OutputRedaction with
            | Some _ ->
                Some(startRedactingReader proc.StandardError stderrCallbackGate stderrClosed handleRedactedStderrLine true)
            | None -> None

        let capturedStdout =
            if request.SuppressStdoutEcho then
                let reader = proc.StandardOutput
                // A char buffer, not a byte one: `StreamReader.Read` decodes, so a chunk
                // boundary can never split a multi-byte character. Reading bytes here to
                // make the comment above literally true would introduce that bug.
                Some(
                    task {
                        let chunk = Array.zeroCreate<char> 4096
                        let mutable reading = true

                        while reading do
                            let! n = reader.ReadAsync(chunk, 0, chunk.Length)

                            if n > 0 then
                                lock captureBuffer (fun () -> captureBuffer.Append(chunk, 0, n) |> ignore)
                            else
                                reading <- false
                    }
                    :> Tasks.Task)
            else
                if redactingStdoutReader.IsNone then
                    proc.BeginOutputReadLine()

                None

        if redactingStderrReader.IsNone then
            proc.BeginErrorReadLine()

        let stdoutReaderCompleted: Tasks.Task =
            match capturedStdout with
            | Some task -> task
            | None -> redactingStdoutReader |> Option.defaultValue (stdoutClosed.Task :> Tasks.Task)

        let stderrReaderCompleted: Tasks.Task =
            redactingStderrReader |> Option.defaultValue (stderrClosed.Task :> Tasks.Task)

        let allReadersCompleted =
            Tasks.Task.WhenAll [| stdoutReaderCompleted; stderrReaderCompleted |]

        // Capture mode intentionally permits an escaped holder of the raw stdout
        // pipe and retains the bytes already read. Stderr still publishes through
        // DataReceived/OnLine, so its EOF is load-bearing before the callback-tail
        // snapshot: otherwise an escaped stderr writer could enqueue after return.
        let callbackReadersCompleted: Tasks.Task =
            if request.SuppressStdoutEcho then
                stderrReaderCompleted
            else
                allReadersCompleted

        let mutable registeredGroup: RegisteredGroupIdentity option = None

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
                let group, anchor = reportedPgid.Task.Result

                try
                    let startTime =
                        match processStateAndStartTime group with
                        | ProcessIdentityState.Matching _, Some value -> value
                        | _ -> invalidOp $"could not bind process group {group} to its Linux start time"

                    let anchorStartTime =
                        match containmentDirectory with
                        | None when anchor = 0 -> "-"
                        | Some _ when anchor > 1 && Native.processGroupOf anchor = Some group ->
                            match processStateAndStartTime anchor with
                            | ProcessIdentityState.Matching _, Some value -> value
                            | _ -> invalidOp $"could not bind process-group anchor {anchor} to its Linux start time"
                        | _ -> invalidOp $"process group {group} did not report a valid containment anchor"

                    persistGroup group startTime anchor anchorStartTime

                    match containmentDirectory with
                    | Some _ ->
                        let anchorStopped =
                            waitForIdentityStop
                                200
                                (fun () -> observeIdentity anchor anchorStartTime)
                                (fun () -> Thread.Sleep 10)

                        if not anchorStopped then
                            invalidOp $"process-group anchor {anchor} did not reach its identity-bound stopped state"

                        registeredGroup <-
                            Some
                                { GroupId = group
                                  LeaderStartTime = startTime
                                  AnchorPid = anchor
                                  AnchorStartTime = anchorStartTime }
                    | None -> ()

                    let stopped =
                        waitForIdentityStop
                            200
                            (fun () -> observeIdentity group startTime)
                            (fun () -> Thread.Sleep 10)

                    if not stopped then
                        invalidOp $"process group {group} did not reach an identity-bound stopped state"

                    // Release only the identity-checked leader. The stopped
                    // anchor must remain stopped so a later group TERM stays
                    // pending on it throughout the user's grace window.
                    if not (Native.signalProcess group Native.SIGCONT) then
                        invalidOp $"could not release registered process group {group}"

                    Some group
                with _ ->
                    // Closing the sole writer wakes the out-of-group watchdog;
                    // the stopped child cannot execute user code before cleanup.
                    proc.StandardInput.Close()
                    reraise ()
            else
                // A missing marker cannot leave a stopped, unregistered child.
                // EOF delegates cleanup to the wrapper's watchdog.
                proc.StandardInput.Close()
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

        let waitForTaskWithin (clock: Stopwatch) (budgetMs: int) (task: Tasks.Task) =
            let remaining = max 0 (budgetMs - int clock.ElapsedMilliseconds)

            if task.IsCompleted then
                true
            elif remaining <= 0 then
                false
            else
                let completed =
                    Tasks.Task.WhenAny(
                        [| task
                           Tasks.Task.Delay remaining |])
                        .GetAwaiter()
                        .GetResult()

                obj.ReferenceEquals(completed, task)

        let waitForReaderCompletion (budgetMs: int) =
            // EOF/task completion, not an unchanged-length guess. A descendant
            // can hold a pipe open indefinitely, so completion is still bounded;
            // expiry keeps every byte already appended and never blocks the run.
            let clock = Stopwatch.StartNew()
            let allReachedEof = waitForTaskWithin clock budgetMs allReadersCompleted
            clock, allReachedEof, callbackReadersCompleted.IsCompletedSuccessfully

        let waitForLineCallbackCompletion budgetMs clock callbackReadersReachedEof : exn option =
            // Close callback admission under the same lock as enqueue before
            // snapshotting the tail, so an escaped writer cannot append later.
            let tail = closeAndGetLineCallbackTail ()

            let processOutputCallbackPresent =
                Option.isSome request.OnLine
                || Option.isSome request.OnRedactedLine
                || Option.isSome request.OnRedactedAdmission

            if processOutputCallbackPresent && not callbackReadersReachedEof then
                Some(
                    TimeoutException(
                        $"OnLine reader did not reach EOF within the shared {budgetMs}ms output-drain budget"))
            elif waitForTaskWithin clock budgetMs tail then
                try
                    tail.GetAwaiter().GetResult()
                    None
                with error ->
                    Some error
            else
                Some(
                    TimeoutException(
                        $"OnLine callback tail did not complete within the shared {budgetMs}ms output-drain budget"))

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

            // A reader callback already appending is knowable and should not be
            // left behind the snapshot. User callbacks execute on their separate
            // serialized tail, so they cannot stall ingestion. The quiet sample
            // still covers reader callbacks Process has queued but not invoked yet.
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

        let outcome, termination, outputCompletionClock, outputCompletionBudget, callbackReadersReachedEof =
            if finished then
                let code = proc.ExitCode

                let t =
                    if request.ReapGroup then
                        pgid
                        |> Option.map (fun g ->
                            match registeredGroup with
                            | Some identity -> reapWithAnchor identity request.GraceMs
                            | None -> reap g request.GraceMs)
                    else
                        None
                // A production containment anchor intentionally outlives the
                // direct leader. Reap the group before requiring reader EOF;
                // otherwise that controller-owned survivor makes every normal
                // Run.Host shell look like an escaped callback writer. Readers
                // stay active during reaping, then share the existing 500ms bound.
                let completionClock, _, callbackReadersReachedEof = waitForReaderCompletion 500


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

                outcome, t, completionClock, 500, callbackReadersReachedEof
            else
                // Jenkins' interrupt narration, in its words and its order — measured
                // on 2.568.1 — so the two logs COMPARE (FG-102) instead of each
                // engine's version being suppressed. `Terminated` then arrives from
                // the shell itself on both engines. The cause is the SNAPSHOT taken
                // when the wait ended, never a fresh sample.
                if waitEnd = WaitEnd.Expired then
                    publishGeneratedLine "Cancelling nested steps due to timeout"

                publishGeneratedLine "Sending interrupt signal to process"

                settleBeforeSignal 300
                let outputBeforeSignal = lock stdout (fun () -> stdout.ToString())
                let stderrBeforeSignal = lock stderr (fun () -> stderr.ToString())
                let t =
                    pgid
                    |> Option.map (fun g ->
                        match registeredGroup with
                        | Some identity -> terminateGroupWithAnchor identity request.GraceMs
                        | None -> terminateGroup g request.GraceMs)
                let completionClock, _, callbackReadersReachedEof = waitForReaderCompletion 300

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
                    publishGeneratedLine "Terminated"
                // Distinguish the two ways a step can fail to finish. Both take
                // the same signal path; only the reported cause differs.
                (if waitEnd = WaitEnd.Interrupted then Cancelled else TimedOut), t, completionClock, 300, callbackReadersReachedEof

        // Defer propagation until after process-group guard and secret-bearing
        // script cleanup. The run still fails closed, but never trades an output
        // publication failure for a leaked containment guard or durable script.
        let lineCallbackFailure =
            waitForLineCallbackCompletion
                outputCompletionBudget
                outputCompletionClock
                callbackReadersReachedEof

        let readerFailure =
            if allReadersCompleted.IsFaulted then
                try
                    allReadersCompleted.GetAwaiter().GetResult()
                    None
                with error ->
                    Some error
            else
                None

        // The guard outlives the inner leader. This closes the interval between
        // leader exit and group reaping: if Run.Host dies there, stdin reaches
        // EOF and the guard kills surviving descendants. Disarm only after
        // extinction is verified. Outside controller containment, ReapGroup=false
        // retains its historical opt-out by explicitly disarming the guard.
        let mayDisarmGuard =
            match containmentDirectory, pgid with
            | None, _ -> true
            | Some _, Some group ->
                Native.probeProcessGroup group = Native.ProcessGroupPresence.Absent
            | Some _, None -> false

        let mutable guardDisarmed = false

        try
            if mayDisarmGuard then
                proc.StandardInput.WriteLine "disarm"
                proc.StandardInput.Flush()
                guardDisarmed <- true

            proc.StandardInput.Close()
        with _ ->
            ()

        // Keep the anchor-bound record available until the disarm command is in
        // the pipe. If Run.Host dies before that point, the guard still has the
        // provenance it needs; after the flush it will read `disarm`, not EOF.
        if guardDisarmed then
            pgid |> Option.iter forgetExtinguishedGroup

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
        // Capture completion is sampled BEFORE the buffer. If EOF races this
        // snapshot, false is conservative and the public copy withholds an
        // ambiguous suffix; true proves every append preceded the snapshot.
        let stdoutReachedEof = stdoutReaderCompleted.IsCompletedSuccessfully

        let capturedText =
            capturedStdout
            |> Option.map (fun _ -> lock captureBuffer (fun () -> captureBuffer.ToString()))

        let bufferedStdout = lock stdout (fun () -> stdout.ToString())
        let bufferedStderr = lock stderr (fun () -> stderr.ToString())
        let bufferedStdoutRedacted =
            match request.OutputRedaction, request.SuppressStdoutEcho with
            | Some _, false -> Some(lock stdout (fun () -> stdoutRedacted.ToRedactedText()))
            | _ -> None

        let bufferedStderrRedacted =
            request.OutputRedaction
            |> Option.map (fun _ -> lock stderr (fun () -> stderrRedacted.ToRedactedText()))

        let result =
            { Outcome = outcome
              Stdout = defaultArg capturedText bufferedStdout
              StdoutRedacted = bufferedStdoutRedacted
              StdoutReachedEof = stdoutReachedEof
              Stderr = bufferedStderr
              StderrRedacted = bufferedStderrRedacted
              DurationMs = sw.ElapsedMilliseconds
              ProcessGroupId = pgid
              Termination = termination
              CleanupFailure = cleanupFailure
              DurableId = mintedDurableId }

        match readerFailure |> Option.orElse lineCallbackFailure with
        | None -> result
        | Some error ->
            Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
            Unchecked.defaultof<RunResult>
