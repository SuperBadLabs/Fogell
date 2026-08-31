namespace Fogell.Controller.Host

open System
open System.Runtime.InteropServices

[<RequireQualifiedAccess>]
type internal ProcessPresence =
    | Absent
    | Present
    | Uncertain

[<RequireQualifiedAccess>]
type internal ProcessSignalResult =
    | Delivered
    | TargetAbsent
    | Uncertain

[<RequireQualifiedAccess>]
type internal ProcessGroupQuery =
    | Found of int
    | Absent
    | Uncertain

[<RequireQualifiedAccess>]
type internal ProcessGroupStopResult =
    | Extinguished
    | Persisted
    | StatusUncertain

[<RequireQualifiedAccess>]
type internal ChildExitKind =
    | Natural
    | Forced

[<RequireQualifiedAccess>]
type internal ChildHandoff =
    | NaturalTerminalAllowed
    | ReconciliationRequired

type internal ProcessIdentity =
    { ProcessId: int
      StartTime: string }

[<RequireQualifiedAccess>]
type internal ProcessMemberObservation =
    | Observed of state: char * processGroupId: int * threadCount: int
    | Absent
    | Uncertain

[<RequireQualifiedAccess>]
type internal ProcessGroupPopulation =
    | Empty
    | DefunctOnly
    | Active
    | Uncertain

[<RequireQualifiedAccess>]
type internal RecordedProcessState =
    | SameIdentity of processGroupId: int
    | Absent
    | Changed
    | Uncertain

module internal ProcessGroup =

    [<Literal>]
    let private permissionDenied = 1

    [<Literal>]
    let private noSuchProcess = 3

    [<DllImport("libc", SetLastError = true)>]
    extern int private kill(int pid, int signal)

    [<DllImport("libc", SetLastError = true)>]
    extern int private getpgid(int pid)

    let internal classifyProbe result errorNumber =
        if result = 0 || errorNumber = permissionDenied then
            ProcessPresence.Present
        elif errorNumber = noSuchProcess then
            ProcessPresence.Absent
        else
            ProcessPresence.Uncertain

    let internal classifySignal result errorNumber =
        if result = 0 then
            ProcessSignalResult.Delivered
        elif errorNumber = noSuchProcess then
            ProcessSignalResult.TargetAbsent
        else
            ProcessSignalResult.Uncertain

    let internal classifyGroupQuery result errorNumber =
        if result >= 0 then
            ProcessGroupQuery.Found result
        elif errorNumber = noSuchProcess then
            ProcessGroupQuery.Absent
        else
            ProcessGroupQuery.Uncertain

    let private queryProcessGroup pid =
        let result = getpgid pid
        classifyGroupQuery result (Marshal.GetLastWin32Error())

    let once (action: unit -> 'value) =
        let mutable cached = None

        fun () ->
            match cached with
            | Some value -> value
            | None ->
                let value = action ()
                cached <- Some value
                value

    let combineStopResults results =
        if results |> Seq.exists ((=) ProcessGroupStopResult.Persisted) then
            ProcessGroupStopResult.Persisted
        elif results |> Seq.exists ((=) ProcessGroupStopResult.StatusUncertain) then
            ProcessGroupStopResult.StatusUncertain
        else
            ProcessGroupStopResult.Extinguished

    /// An outer setsid group is sufficient evidence only for a Run.Host that
    /// exited naturally. Individual steps may establish nested sessions, so no
    /// successful outer-group probe can make a forced stop safe to replay.
    let handoff exitKind stopResult =
        match exitKind, stopResult with
        | ChildExitKind.Natural, ProcessGroupStopResult.Extinguished ->
            ChildHandoff.NaturalTerminalAllowed
        | _ -> ChildHandoff.ReconciliationRequired

    let signalGroup processGroupId signal =
        if not (OperatingSystem.IsLinux()) then
            ProcessSignalResult.Uncertain
        else
            let result = kill(-processGroupId, signal)
            classifySignal result (Marshal.GetLastWin32Error())

    let signalLeader processId signal =
        if not (OperatingSystem.IsLinux()) then
            ProcessSignalResult.Uncertain
        else
            let result = kill(processId, signal)
            classifySignal result (Marshal.GetLastWin32Error())

    let private readProcessStat pid =
        let value = IO.File.ReadAllText($"/proc/{pid}/stat")
        let close = value.LastIndexOf ')'

        if close < 0 || close + 2 >= value.Length then
            None
        else
            let fields =
                value.Substring(close + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries)

            if fields.Length <= 19 then None else Some fields

    /// A zombie cannot execute code, write a journal, or perform an external
    /// effect. LinuxKit may retain it indefinitely when container pid 1 does
    /// not reap, so kill(-pgid, 0) is not an extinction oracle. getpgid filters
    /// candidates before stat is read; an uncertain membership query or an
    /// unreadable target-group stat remains fail-closed.
    let internal classifyGroupMembers processGroupId observations =
        let mutable live = false
        let mutable defunct = false
        let mutable uncertain = false

        for observation in observations do
            match observation with
            | ProcessMemberObservation.Observed(state, group, threads) when group = processGroupId ->
                if (state = 'Z' || state = 'X' || state = 'x') && threads <= 1 then
                    defunct <- true
                else
                    // Linux may report a dead thread-group leader as Z while
                    // num_threads still proves executable sibling threads.
                    live <- true
            | ProcessMemberObservation.Uncertain -> uncertain <- true
            | _ -> ()

        if live then
            ProcessGroupPopulation.Active
        elif uncertain then
            ProcessGroupPopulation.Uncertain
        elif defunct then
            ProcessGroupPopulation.DefunctOnly
        else
            ProcessGroupPopulation.Empty

    let internal observeGroupCandidate processGroupId pid queryGroup readObservation =
        match queryGroup pid with
        | ProcessGroupQuery.Found group when group = processGroupId -> readObservation pid
        | ProcessGroupQuery.Found _
        | ProcessGroupQuery.Absent -> ProcessMemberObservation.Absent
        | ProcessGroupQuery.Uncertain -> ProcessMemberObservation.Uncertain

    let private observeGroupPopulation processGroupId =
        IO.Directory.GetDirectories "/proc"
        |> Array.choose (fun directory ->
            match Int32.TryParse(IO.Path.GetFileName directory) with
            | true, pid -> Some pid
            | _ -> None)
        |> Array.map (fun pid ->
            observeGroupCandidate processGroupId pid queryProcessGroup (fun candidate ->
                try
                    match readProcessStat candidate with
                    | Some fields when fields[0].Length = 1 ->
                        match Int32.TryParse fields[2], Int32.TryParse fields[17] with
                        | (true, group), (true, threads) ->
                            ProcessMemberObservation.Observed(fields[0].[0], group, threads)
                        | _ -> ProcessMemberObservation.Uncertain
                    | _ -> ProcessMemberObservation.Uncertain
                with
                | :? IO.FileNotFoundException
                | :? IO.DirectoryNotFoundException -> ProcessMemberObservation.Absent
                | _ -> ProcessMemberObservation.Uncertain))
        |> classifyGroupMembers processGroupId

    let internal probeKernelGroup processGroupId =
        if not (OperatingSystem.IsLinux()) || processGroupId <= 1 then
            ProcessPresence.Uncertain
        else
            let result = kill(-processGroupId, 0)
            classifyProbe result (Marshal.GetLastWin32Error())

    let probeGroup processGroupId =
        if not (OperatingSystem.IsLinux()) || processGroupId <= 1 then
            ProcessPresence.Uncertain
        else
            let kernelProbe () = probeKernelGroup processGroupId

            match kernelProbe () with
            | ProcessPresence.Absent -> ProcessPresence.Absent
            | ProcessPresence.Uncertain -> ProcessPresence.Uncertain
            | ProcessPresence.Present ->
                try
                    match observeGroupPopulation processGroupId with
                    | ProcessGroupPopulation.Active -> ProcessPresence.Present
                    | ProcessGroupPopulation.DefunctOnly -> ProcessPresence.Absent
                    | ProcessGroupPopulation.Uncertain -> ProcessPresence.Uncertain
                    | ProcessGroupPopulation.Empty ->
                        // A group can disappear during the scan. Only a second
                        // ESRCH proves that an empty observation was that race;
                        // otherwise a hidden/reused numeric PGID is uncertain.
                        match kernelProbe () with
                        | ProcessPresence.Absent -> ProcessPresence.Absent
                        | _ -> ProcessPresence.Uncertain
                with _ ->
                    ProcessPresence.Uncertain

    /// Capture the Linux process birth identity immediately after Start. The
    /// launcher may not have called setsid yet, so PGID is deliberately not
    /// part of the capture; every later observation returns its current PGID.
    let tryCaptureIdentity pid =
        if not (OperatingSystem.IsLinux()) || pid <= 1 then
            None
        else
            try
                readProcessStat pid
                |> Option.map (fun fields ->
                    { ProcessId = pid
                      StartTime = fields[19] })
            with _ ->
                None

    let private observeIdentity (identity: ProcessIdentity) =
        try
            match readProcessStat identity.ProcessId with
            | None -> RecordedProcessState.Uncertain
            | Some fields when fields[19] <> identity.StartTime ->
                // The numeric PID was reused. It proves the recorded process is
                // gone but grants no authority over the replacement.
                RecordedProcessState.Changed
            | Some fields ->
                match Int32.TryParse fields[2], Int32.TryParse fields[17] with
                | (true, processGroup), (true, threads)
                    when (fields[0] = "Z" || fields[0] = "X" || fields[0] = "x")
                         && threads <= 1 ->
                    RecordedProcessState.Absent
                | (true, processGroup), (true, _) -> RecordedProcessState.SameIdentity processGroup
                | _ -> RecordedProcessState.Uncertain
        with
        | :? IO.FileNotFoundException
        | :? IO.DirectoryNotFoundException -> RecordedProcessState.Absent
        | _ -> RecordedProcessState.Uncertain

    /// Extinction means both the original setsid launcher and every member of
    /// its eventual process group are absent. Checking both closes the startup
    /// race where the launcher has not established its group yet.
    let ensureExtinguished
        termChecks
        killChecks
        (probeLeader: unit -> ProcessPresence)
        (probeProcessGroup: unit -> ProcessPresence)
        (sendGroupSignal: int -> ProcessSignalResult)
        (sendLeaderSignal: int -> ProcessSignalResult)
        (pause: unit -> unit)
        =
        if termChecks < 0 then invalidArg (nameof termChecks) "TERM check count cannot be negative"
        if killChecks < 0 then invalidArg (nameof killChecks) "KILL check count cannot be negative"

        let presence () = probeLeader(), probeProcessGroup()

        let classifyFinal () =
            match presence () with
            | ProcessPresence.Absent, ProcessPresence.Absent ->
                ProcessGroupStopResult.Extinguished
            | ProcessPresence.Uncertain, _
            | _, ProcessPresence.Uncertain ->
                ProcessGroupStopResult.StatusUncertain
            | _ -> ProcessGroupStopResult.Persisted

        let rec waitForExtinction remaining =
            match presence () with
            | ProcessPresence.Absent, ProcessPresence.Absent -> true
            | _ when remaining = 0 -> false
            | _ ->
                pause ()
                waitForExtinction (remaining - 1)

        if waitForExtinction 0 then
            ProcessGroupStopResult.Extinguished
        else
            sendGroupSignal 15 |> ignore
            sendLeaderSignal 15 |> ignore

            if waitForExtinction termChecks then
                ProcessGroupStopResult.Extinguished
            else
                sendGroupSignal 9 |> ignore
                sendLeaderSignal 9 |> ignore

                if waitForExtinction killChecks then
                    ProcessGroupStopResult.Extinguished
                else
                    classifyFinal ()

    /// Apply the extinction state machine without ever probing or signalling a
    /// numeric outer PGID as authority by itself. A matching birth identity may
    /// signal its launcher before setsid, and may signal the group only after it
    /// actually leads that group. Once the identity is absent or changed, group
    /// absence can prove extinction but group presence is ambiguous with reuse.
    let internal ensureIdentityBoundExtinguished
        termChecks
        killChecks
        processId
        (observe: unit -> RecordedProcessState)
        (probeProcessGroup: unit -> ProcessPresence)
        (sendProcessGroupSignal: int -> ProcessSignalResult)
        (sendProcessSignal: int -> ProcessSignalResult)
        (pause: unit -> unit)
        =
        let probeLeader () =
            match observe () with
            | RecordedProcessState.SameIdentity _ -> ProcessPresence.Present
            | RecordedProcessState.Absent
            | RecordedProcessState.Changed -> ProcessPresence.Absent
            | RecordedProcessState.Uncertain -> ProcessPresence.Uncertain

        let probeBoundGroup () =
            match observe () with
            | RecordedProcessState.SameIdentity processGroup when processGroup = processId ->
                probeProcessGroup ()
            | RecordedProcessState.SameIdentity _ -> ProcessPresence.Absent
            | RecordedProcessState.Absent
            | RecordedProcessState.Changed ->
                match probeProcessGroup () with
                | ProcessPresence.Absent -> ProcessPresence.Absent
                | _ -> ProcessPresence.Uncertain
            | RecordedProcessState.Uncertain -> ProcessPresence.Uncertain

        let signalBoundGroup signal =
            match observe () with
            | RecordedProcessState.SameIdentity processGroup when processGroup = processId ->
                sendProcessGroupSignal signal
            | RecordedProcessState.SameIdentity _ -> ProcessSignalResult.TargetAbsent
            | RecordedProcessState.Absent
            | RecordedProcessState.Changed ->
                match probeProcessGroup () with
                | ProcessPresence.Absent -> ProcessSignalResult.TargetAbsent
                | _ -> ProcessSignalResult.Uncertain
            | RecordedProcessState.Uncertain -> ProcessSignalResult.Uncertain

        let signalBoundLeader signal =
            match observe () with
            | RecordedProcessState.SameIdentity _ -> sendProcessSignal signal
            | RecordedProcessState.Absent -> ProcessSignalResult.TargetAbsent
            | RecordedProcessState.Changed
            | RecordedProcessState.Uncertain -> ProcessSignalResult.Uncertain

        ensureExtinguished
            termChecks
            killChecks
            probeLeader
            probeBoundGroup
            signalBoundGroup
            signalBoundLeader
            pause

    let stopIdentityBoundGroup termChecks killChecks identity pause =
        ensureIdentityBoundExtinguished
            termChecks
            killChecks
            identity.ProcessId
            (fun () -> observeIdentity identity)
            (fun () -> probeGroup identity.ProcessId)
            (signalGroup identity.ProcessId)
            (signalLeader identity.ProcessId)
            pause

    type private RecordedIdentity =
        | IdentityMatches
        | IdentityAbsent
        | IdentityChanged
        | IdentityUncertain

    let private recordedIdentity pid expectedStartTime expectedProcessGroup =
        match
            observeIdentity
                { ProcessId = pid
                  StartTime = expectedStartTime }
        with
        | RecordedProcessState.SameIdentity processGroup when processGroup = expectedProcessGroup ->
            IdentityMatches
        | RecordedProcessState.Absent -> IdentityAbsent
        | RecordedProcessState.Changed -> IdentityChanged
        | RecordedProcessState.SameIdentity _
        | RecordedProcessState.Uncertain -> IdentityUncertain

    /// Registered inner groups have two independently recorded birth
    /// identities: the original leader and the stopped anchor. Re-observe both
    /// immediately before every probe and signal; a prior match is evidence,
    /// never reusable authority for a later numeric PID/PGID operation.
    let internal ensureRegisteredGroupExtinguished
        termChecks
        killChecks
        processGroupId
        (observeLeader: unit -> RecordedProcessState)
        (observeAnchor: unit -> RecordedProcessState)
        (probeProcessGroup: unit -> ProcessPresence)
        (sendProcessGroupSignal: int -> ProcessSignalResult)
        (sendLeaderSignal: int -> ProcessSignalResult)
        (sendAnchorSignal: int -> ProcessSignalResult)
        (pause: unit -> unit)
        =
        let isMatch = function
            | RecordedProcessState.SameIdentity group when group = processGroupId -> true
            | _ -> false

        let classifyIdentities leader anchor =
            if isMatch leader || isMatch anchor then
                ProcessPresence.Present
            else
                match leader, anchor with
                | (RecordedProcessState.Absent | RecordedProcessState.Changed),
                  (RecordedProcessState.Absent | RecordedProcessState.Changed) -> ProcessPresence.Absent
                | _ -> ProcessPresence.Uncertain

        let probeRecorded () = classifyIdentities (observeLeader ()) (observeAnchor ())

        let probeBoundGroup () =
            match probeRecorded () with
            | ProcessPresence.Present -> probeProcessGroup ()
            | ProcessPresence.Absent ->
                match probeProcessGroup () with
                | ProcessPresence.Absent -> ProcessPresence.Absent
                | _ -> ProcessPresence.Uncertain
            | ProcessPresence.Uncertain -> ProcessPresence.Uncertain

        let signalBoundGroup signal =
            if isMatch (observeAnchor ()) || isMatch (observeLeader ()) then
                sendProcessGroupSignal signal
            else
                match probeProcessGroup () with
                | ProcessPresence.Absent -> ProcessSignalResult.TargetAbsent
                | _ -> ProcessSignalResult.Uncertain

        let signalBoundMember signal =
            if isMatch (observeAnchor ()) then
                sendAnchorSignal signal
            elif isMatch (observeLeader ()) then
                sendLeaderSignal signal
            else
                match probeProcessGroup () with
                | ProcessPresence.Absent -> ProcessSignalResult.TargetAbsent
                | _ -> ProcessSignalResult.Uncertain

        ensureExtinguished
            termChecks
            killChecks
            probeRecorded
            probeBoundGroup
            signalBoundGroup
            signalBoundMember
            pause

    /// Stop every inner setsid group durably registered before its user code was
    /// released. Records include Linux process start time, and each in-scope
    /// signal is preceded by a fresh identity/group observation rather than by
    /// bare numeric-PID authority.
    let stopRegisteredGroups termChecks killChecks (directory: string) pause =
        if not (OperatingSystem.IsLinux()) then
            ProcessGroupStopResult.StatusUncertain
        elif not (IO.Directory.Exists directory) then
            ProcessGroupStopResult.Extinguished
        else
            IO.Directory.EnumerateFiles(directory, "*.group")
            |> Seq.map (fun record ->
                try
                    let fields =
                        IO.File.ReadAllText(record).Split(
                            [| ' '; '\t'; '\r'; '\n' |],
                            StringSplitOptions.RemoveEmptyEntries)

                    match fields with
                    | [| rawPid; leaderStartTime; rawAnchor; anchorStartTime |] ->
                        match Int32.TryParse rawPid, Int32.TryParse rawAnchor with
                        | (true, pid), (true, anchor) when pid > 1 && anchor > 1 ->
                            let observeRecorded identityPid identityStartTime () =
                                match recordedIdentity identityPid identityStartTime pid with
                                | IdentityMatches -> RecordedProcessState.SameIdentity pid
                                | IdentityAbsent -> RecordedProcessState.Absent
                                | IdentityChanged -> RecordedProcessState.Changed
                                | IdentityUncertain -> RecordedProcessState.Uncertain

                            let stopThrough () =
                                let result =
                                    ensureRegisteredGroupExtinguished
                                        termChecks
                                        killChecks
                                        pid
                                        (observeRecorded pid leaderStartTime)
                                        (observeRecorded anchor anchorStartTime)
                                        (fun () -> probeGroup pid)
                                        (signalGroup pid)
                                        (signalLeader pid)
                                        (signalLeader anchor)
                                        pause

                                if result = ProcessGroupStopResult.Extinguished then
                                    try IO.File.Delete record with _ -> ()

                                result

                            stopThrough ()
                        | _ -> ProcessGroupStopResult.StatusUncertain
                    | _ -> ProcessGroupStopResult.StatusUncertain
                with _ ->
                    ProcessGroupStopResult.StatusUncertain)
            |> combineStopResults
