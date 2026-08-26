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

    type private RecordedIdentity =
        | IdentityMatches
        | IdentityAbsent
        | IdentityUncertain

    let private recordedIdentity pid expectedStartTime expectedProcessGroup =
        try
            let value = IO.File.ReadAllText($"/proc/{pid}/stat")
            let close = value.LastIndexOf ')'

            if close < 0 || close + 2 >= value.Length then
                IdentityUncertain
            else
                let fields =
                    value.Substring(close + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries)

                if fields.Length <= 19 then
                    IdentityUncertain
                elif
                    fields[19] = expectedStartTime
                    && fields[2] = string expectedProcessGroup
                then
                    IdentityMatches
                else
                    // The numeric pid has been reused. Never signal the new owner.
                    IdentityUncertain
        with
        | :? IO.FileNotFoundException
        | :? IO.DirectoryNotFoundException -> IdentityAbsent
        | _ -> IdentityUncertain

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
