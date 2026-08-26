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

    let probeGroup processGroupId =
        if not (OperatingSystem.IsLinux()) then
            ProcessPresence.Uncertain
        else
            let result = kill(-processGroupId, 0)
            classifyProbe result (Marshal.GetLastWin32Error())

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
                match Int32.TryParse fields[2] with
                | true, processGroup -> RecordedProcessState.SameIdentity processGroup
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
        | RecordedProcessState.SameIdentity _
        | RecordedProcessState.Changed
        | RecordedProcessState.Uncertain -> IdentityUncertain

    /// Stop every inner setsid group durably registered before its user code was
    /// released. Records include Linux process start time, so a stale record can
    /// never authorize signalling a reused numeric pid/process-group id.
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
                            let stopThrough identityPid identityStartTime =
                                let result =
                                    ensureExtinguished
                                        termChecks
                                        killChecks
                                        (fun () ->
                                            match recordedIdentity identityPid identityStartTime pid with
                                            | IdentityMatches -> ProcessPresence.Present
                                            | IdentityAbsent -> ProcessPresence.Absent
                                            | IdentityUncertain -> ProcessPresence.Uncertain)
                                        (fun () -> probeGroup pid)
                                        (signalGroup pid)
                                        (signalLeader identityPid)
                                        pause

                                if result = ProcessGroupStopResult.Extinguished then
                                    try IO.File.Delete record with _ -> ()

                                result

                            match recordedIdentity anchor anchorStartTime pid with
                            | IdentityMatches -> stopThrough anchor anchorStartTime
                            | IdentityAbsent ->
                                // Before leader exit its own identity remains a
                                // safe fallback. After leader exit the anchor is
                                // the only continuous provenance; without either,
                                // a populated numeric group is ambiguous with reuse.
                                match recordedIdentity pid leaderStartTime pid with
                                | IdentityMatches -> stopThrough pid leaderStartTime
                                | IdentityAbsent ->
                                    match probeGroup pid with
                                    | ProcessPresence.Absent ->
                                        try IO.File.Delete record with _ -> ()
                                        ProcessGroupStopResult.Extinguished
                                    | _ -> ProcessGroupStopResult.StatusUncertain
                                | IdentityUncertain -> ProcessGroupStopResult.StatusUncertain
                            | IdentityUncertain -> ProcessGroupStopResult.StatusUncertain
                        | _ -> ProcessGroupStopResult.StatusUncertain
                    | _ -> ProcessGroupStopResult.StatusUncertain
                with _ ->
                    ProcessGroupStopResult.StatusUncertain)
            |> combineStopResults
