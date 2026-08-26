namespace Fogell.Controller.Host

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Fogell.Domain
open Fogell.Journal
open Fogell.Store

type LocalWorker(config: ControllerConfig, store: Store, logger: ILogger<LocalWorker>) =
    inherit BackgroundService()

    let owner = $"local:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"
    let diagnosticDrainBufferBytes = 64 * 1024
    let diagnosticDrainGraceMilliseconds = 2000
    let eventReadBufferBytes = 64 * 1024
    let maxEventFrameEncodedBytes = 1024 * 1024
    // One poll is control-plane work as well as log work. Bounding both axes
    // prevents a noisy child or a large existing backlog from postponing lease
    // renewal and cancellation behind an EOF that may never arrive.
    let eventDrainByteBudget = 256 * 1024
    let eventDrainFrameBudget = 16
    let processGroupProbeMilliseconds = 50
    let processGroupTermChecks = 40
    let processGroupKillChecks = 100
    // This cursor belongs to this BackgroundService instance. ExecuteAsync has
    // a single claim loop, so no shared mutable state or cross-worker lock is
    // needed to rotate the first organization probed on subsequent scans.
    let mutable lastClaimedOrganization: OrganizationId option = None

    let atomicDefinition (path: string) (bytes: byte array) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        if File.Exists path then
            if not (CryptographicOperations.FixedTimeEquals(File.ReadAllBytes path, bytes)) then
                invalidOp "materialized pipeline definition differs from durable source"
        else
            let temporary = path + $".{Guid.NewGuid():N}.tmp"
            File.WriteAllBytes(temporary, bytes)
            File.Move(temporary, path)

    let stopGroup (child: Process) =
        let probeLeader () =
            try
                if child.HasExited then ProcessPresence.Absent else ProcessPresence.Present
            with _ ->
                ProcessPresence.Uncertain

        let signalLeader signal =
            match probeLeader () with
            | ProcessPresence.Absent -> ProcessSignalResult.TargetAbsent
            | ProcessPresence.Present -> ProcessGroup.signalLeader child.Id signal
            | ProcessPresence.Uncertain -> ProcessSignalResult.Uncertain

        try
            ProcessGroup.ensureExtinguished
                processGroupTermChecks
                processGroupKillChecks
                probeLeader
                (fun () -> ProcessGroup.probeGroup child.Id)
                (ProcessGroup.signalGroup child.Id)
                signalLeader
                (fun () -> Thread.Sleep processGroupProbeMilliseconds)
        with _ ->
            ProcessGroupStopResult.StatusUncertain

    let deleteEventFile (eventPath: string) =
        { new IDisposable with
            member _.Dispose() =
                try
                    File.Delete eventPath
                with
                | :? FileNotFoundException -> ()
                | :? DirectoryNotFoundException -> () }

    let openEventStream (eventPath: string) () =
        if File.Exists eventPath then
            Some(
                new FileStream(
                    eventPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite ||| FileShare.Delete,
                    eventReadBufferBytes,
                    FileOptions.SequentialScan)
                :> Stream)
        else
            None

    let eventBody frame =
        match frame with
        | Oversized -> "controller refused an oversized child log frame"
        | Encoded bytes ->
            let payloadLength =
                if bytes.Length > 0 && bytes[bytes.Length - 1] = byte '\r' then
                    bytes.Length - 1
                else
                    bytes.Length

            try
                Text.Encoding.ASCII.GetString(bytes, 0, payloadLength)
                |> Convert.FromBase64String
                |> Text.Encoding.UTF8.GetString
            with _ ->
                "controller refused a malformed child log frame"

    let eventPublisher (claim: ExecutionClaim) (nextSequence: int ref) frame =
        let appended =
            store.AppendLogFenced(
                claim.OrganizationId,
                claim.BuildId,
                claim.AttemptId,
                claim.Fence,
                owner,
                nextSequence.Value,
                eventBody frame)

        if appended then
            nextSequence.Value <- nextSequence.Value + 1

        appended

    let drainEvents (claim: ExecutionClaim) (eventPath: string) (state: EventDrainState) sequence =
        let nextSequence = ref sequence
        let mutable batch =
            { BytesProcessed = 0
              FramesProcessed = 0
              ReachedEof = false
              AuthorityLost = false }

        // Read only a bounded prefix appended since the last poll. FileShare.Delete
        // lets final cleanup unlink the per-fence file even if a killed descendant
        // briefly retains a descriptor.
        match openEventStream eventPath () with
        | None -> ()
        | Some stream ->
            use stream = stream
            batch <-
                EventStream.drainBatch
                    stream
                    state
                    maxEventFrameEncodedBytes
                    eventDrainByteBudget
                    eventDrainFrameBudget
                    (eventPublisher claim nextSequence)

        nextSequence.Value, batch

    let drainFinalEvents
        (claim: ExecutionClaim)
        (eventPath: string)
        (state: EventDrainState)
        sequence
        continueControl
        =
        let nextSequence = ref sequence
        let completion =
            EventStream.drainExtinguishedBoundary
                (openEventStream eventPath)
                state
                maxEventFrameEncodedBytes
                eventDrainByteBudget
                eventDrainFrameBudget
                continueControl
                (eventPublisher claim nextSequence)

        nextSequence.Value, completion

    let completeDiagnosticDrains
        (child: Process)
        (drainCancellation: CancellationTokenSource)
        (drains: Task)
        =
        task {
            let! first = Task.WhenAny(drains, Task.Delay diagnosticDrainGraceMilliseconds)

            if Object.ReferenceEquals(first, drains) then
                do! drains
            else
                drainCancellation.Cancel()
                child.StandardOutput.Dispose()
                child.StandardError.Dispose()
                let! closed = Task.WhenAny(drains, Task.Delay diagnosticDrainGraceMilliseconds)

                if Object.ReferenceEquals(closed, drains) then
                    try
                        do! drains
                    with
                    | :? OperationCanceledException -> ()
                    | :? ObjectDisposedException -> ()
                    | :? IOException -> ()
                else
                    // Observe a later fault without keeping the capacity-one
                    // worker hostage to a descriptor retained outside the child
                    // process group.
                    drains.ContinueWith(
                        Action<Task>(fun finished ->
                            if finished.IsFaulted then
                                finished.Exception |> ignore),
                        TaskContinuationOptions.ExecuteSynchronously)
                    |> ignore
        }

    let runReadyClaim (claim: ExecutionClaim) (stoppingToken: CancellationToken) =
        task {
            let buildKey = claim.BuildId.Value.ToString "N"
            let orgKey = claim.OrganizationId.Value.ToString "N"
            let definitionPath = Path.Combine(config.StateRoot, "definitions", orgKey, buildKey, "Jenkinsfile")
            let journalPath = WorkerPaths.journalPath config.StateRoot claim.OrganizationId claim.AttemptId
            let eventPath =
                Path.Combine(
                    config.StateRoot,
                    "events",
                    orgKey,
                    $"{claim.AttemptId.Value:N}-{claim.Fence.Value}.events")
            let containmentPath =
                Path.Combine(
                    config.StateRoot,
                    "containment",
                    orgKey,
                    $"{claim.AttemptId.Value:N}-{claim.Fence.Value}")
            let workspaceRoot = Path.Combine(config.StateRoot, "workspaces", orgKey)
            let neutralHome = Path.Combine(config.StateRoot, "neutral-home")
            let tempRoot = Path.Combine(config.StateRoot, "tmp")
            use _eventFile = deleteEventFile eventPath

            let mutable materialized = false

            try
                // Definition identity is checked before any per-execution state is
                // created. A deterministic mismatch must not escape while the
                // attempt is merely offered: lease recovery would otherwise queue
                // the same poisoned FIFO row forever.
                atomicDefinition definitionPath claim.PipelineSource
                Directory.CreateDirectory workspaceRoot |> ignore
                Directory.CreateDirectory neutralHome |> ignore
                Directory.CreateDirectory tempRoot |> ignore
                Directory.CreateDirectory containmentPath |> ignore
                materialized <- true
            with ex ->
                logger.LogError(
                    ex,
                    "FG-224 definition materialization failed for {AttemptId}; reconciliation is required",
                    claim.AttemptId.Value)

                if
                    not
                        (store.RequireReconciliation(
                            claim.OrganizationId,
                            claim.AttemptId,
                            claim.Fence,
                            owner))
                then
                    logger.LogWarning(
                        "FG-224 materialization quarantine lost authority for {AttemptId}",
                        claim.AttemptId.Value)

            if not materialized then
                return ()

            match
                store.BeginExecution(
                    claim.OrganizationId,
                    claim.AttemptId,
                    claim.Fence,
                    owner,
                    config.LeaseSeconds)
            with
            | Error error ->
                logger.LogWarning(
                    "FG-224 claim {AttemptId} lost before execution start: {Reason}",
                    claim.AttemptId.Value,
                    error)
            | Ok ExecutionCancelledBeforeStart ->
                logger.LogInformation(
                    "FG-224 claim {AttemptId} was cancelled before child launch",
                    claim.AttemptId.Value)
            | Ok ExecutionStarted ->
                let mutable sequence = store.NextLogSequence(claim.OrganizationId, claim.AttemptId)
                let eventState =
                    { Offset = 0L
                      Tail = Array.empty
                      DiscardingOversizedFrame = false }
                let mutable cancelled = false
                let mutable interrupted = false
                let mutable leaseLost = false
                let mutable terminalDrainConfirmed = false

                use child = new Process()
                let start = ProcessStartInfo(config.SetsidPath)
                start.ArgumentList.Add config.RunHostPath
                start.ArgumentList.Add definitionPath
                start.ArgumentList.Add workspaceRoot
                start.ArgumentList.Add buildKey
                start.ArgumentList.Add journalPath
                start.UseShellExecute <- false
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                start.CreateNoWindow <- true
                start.Environment.Clear()
                start.Environment["PATH"] <- "/usr/bin:/bin"
                start.Environment["HOME"] <- neutralHome
                start.Environment["TMPDIR"] <- tempRoot
                start.Environment["FOGELL_EVENT_FILE"] <- eventPath
                start.Environment["FOGELL_EXPECTED_PARENT_PID"] <- string Environment.ProcessId
                start.Environment["FOGELL_PROCESS_GROUP_REGISTRY"] <- containmentPath
                // Project-scoped numbering is durable admission truth. Passing
                // only the build UUID made the child fall back to build 1 for
                // every run, so BUILD_NUMBER/BUILD_ID could select or overwrite
                // another build's external resources.
                start.Environment["FOGELL_BUILD_NUMBER"] <- string claim.BuildNumber
                child.StartInfo <- start

                let launchResult =
                    WorkerLaunch.tryStart
                        (fun () -> stoppingToken.IsCancellationRequested)
                        child.Start

                let launched =
                    match launchResult with
                    | WorkerLaunch.Launched -> true
                    | WorkerLaunch.LaunchSuppressed ->
                        // BeginExecution already made the attempt Running, but
                        // the cancellation check proved no child was launched.
                        // Verified extinction therefore permits an immediate
                        // fenced requeue instead of waiting for lease expiry.
                        let requeued =
                            try
                                store.RequeueOwnedAttempt(
                                    claim.OrganizationId,
                                    claim.AttemptId,
                                    claim.Fence,
                                    owner)
                            with ex ->
                                logger.LogError(
                                    ex,
                                    "FG-224 shutdown requeue failed for {AttemptId}",
                                    claim.AttemptId.Value)
                                false

                        if requeued then
                            logger.LogInformation(
                                "FG-224 shutdown suppressed launch and requeued {AttemptId}",
                                claim.AttemptId.Value)
                        else
                            logger.LogWarning(
                                "FG-224 shutdown requeue lost authority for {AttemptId}",
                                claim.AttemptId.Value)

                        false
                    | WorkerLaunch.LaunchFailed error ->
                        // Attempt durable disposition before diagnostics: even a
                        // fallible logger cannot strand known launch failure.
                        let reconciled =
                            try
                                store.RequireReconciliation(
                                    claim.OrganizationId,
                                    claim.AttemptId,
                                    claim.Fence,
                                    owner)
                            with ex ->
                                logger.LogError(
                                    ex,
                                    "FG-224 launch-failure reconciliation threw for {AttemptId}",
                                    claim.AttemptId.Value)
                                false

                        match error with
                        | Some ex ->
                            logger.LogError(
                                ex,
                                "FG-224 trusted launcher failed for {AttemptId}; reconciliation is required",
                                claim.AttemptId.Value)
                        | None ->
                            logger.LogError(
                                "FG-224 trusted launcher returned false for {AttemptId}; reconciliation is required",
                                claim.AttemptId.Value)

                        if not reconciled then
                            logger.LogWarning(
                                "FG-224 launch-failure reconciliation lost authority for {AttemptId}",
                                claim.AttemptId.Value)

                        false

                if launched then
                    let mutable dispositionRecorded = false

                    let cleanupExecution =
                        ProcessGroup.once (fun () ->
                            // Stop the outer Run.Host group first. Its inherited
                            // stdin writers close, waking per-step watchdogs that
                            // survive their inner setsid boundary. Then verify and
                            // finish every identity-bound registry entry. Memoize
                            // the entire pass: signalling numeric ids again from
                            // finally could target later pid reuse.
                            let outer = stopGroup child
                            let inner =
                                ProcessGroup.stopRegisteredGroups
                                    processGroupTermChecks
                                    processGroupKillChecks
                                    containmentPath
                                    (fun () -> Thread.Sleep processGroupProbeMilliseconds)
                            let result = ProcessGroup.combineStopResults [ outer; inner ]

                            if result = ProcessGroupStopResult.Extinguished then
                                try Directory.Delete(containmentPath, false) with _ -> ()

                            result)

                    let requireReconciliation () =
                        if not dispositionRecorded then
                            dispositionRecorded <- true
                            store.RequireReconciliation(claim.OrganizationId, claim.AttemptId, claim.Fence, owner)
                            |> ignore

                    try
                        // Run.Host publishes protocol logs through FOGELL_EVENT_FILE.
                        // Its inherited stdout/stderr are diagnostic only and are
                        // deliberately discarded. CopyToAsync uses a fixed transfer
                        // buffer; the old whole-stream readers retained arbitrary
                        // child output even though nobody consumed it.
                        use drainCancellation = new CancellationTokenSource()
                        let stdoutDrain =
                            child.StandardOutput.BaseStream.CopyToAsync(
                                Stream.Null,
                                diagnosticDrainBufferBytes,
                                drainCancellation.Token)
                        let stderrDrain =
                            child.StandardError.BaseStream.CopyToAsync(
                                Stream.Null,
                                diagnosticDrainBufferBytes,
                                drainCancellation.Token)
                        let drains = Task.WhenAll(stdoutDrain, stderrDrain)
                        let mutable nextRenewal = DateTimeOffset.UtcNow.AddSeconds(float config.LeaseSeconds / 3.0)

                        let refreshControl forceRenewal =
                            // Cancellation and shutdown are checked before AND
                            // between bounded log slices. Renewal still runs while
                            // deciding the fenced reconciliation transition.
                            if stoppingToken.IsCancellationRequested then
                                interrupted <- true
                            elif store.BuildCancellationRequested(claim.OrganizationId, claim.BuildId) then
                                cancelled <- true

                            if
                                not leaseLost
                                && (forceRenewal || DateTimeOffset.UtcNow >= nextRenewal)
                            then
                                leaseLost <-
                                    not
                                        (store.RenewLease(
                                            claim.OrganizationId,
                                            claim.AttemptId,
                                            claim.Fence,
                                            owner,
                                            config.LeaseSeconds))
                                nextRenewal <- DateTimeOffset.UtcNow.AddSeconds(float config.LeaseSeconds / 3.0)

                        while not child.HasExited && not cancelled && not interrupted && not leaseLost do
                            do! Task.Delay(config.PollMilliseconds)
                            refreshControl false

                            if not cancelled && not interrupted && not leaseLost then
                                let next, batch = drainEvents claim eventPath eventState sequence
                                sequence <- next
                                leaseLost <- batch.AuthorityLost
                                refreshControl false

                        if not cancelled && not interrupted && not leaseLost then
                            child.WaitForExit()

                        let exitKind =
                            if cancelled || interrupted || leaseLost then
                                ChildExitKind.Forced
                            else
                                ChildExitKind.Natural

                        // This proves only the outer Run.Host group. Step runners
                        // may have made nested sessions, so extinction permits a
                        // terminal result only on the unforced natural-exit path.
                        let groupStop = cleanupExecution ()

                        do! completeDiagnosticDrains child drainCancellation drains

                        if
                            ProcessGroup.handoff exitKind groupStop = ChildHandoff.NaturalTerminalAllowed
                            && not leaseLost
                            && not cancelled
                            && not interrupted
                        then
                            let continueFinalDrain () =
                                refreshControl false

                                not leaseLost
                                && not cancelled
                                && not interrupted

                            let next, completion =
                                drainFinalEvents
                                    claim
                                    eventPath
                                    eventState
                                    sequence
                                    continueFinalDrain

                            sequence <- next
                            terminalDrainConfirmed <-
                                EventStream.terminalPublicationAllowed completion

                            match completion.Stop with
                            | EndOfStream -> ()
                            | PublicationAuthorityLost -> leaseLost <- true
                            | ControlStopped -> ()
                            | StreamChangedAfterExtinction ->
                                logger.LogError(
                                    "FG-224 event stream changed after producer extinction for {AttemptId}; reconciliation is required",
                                    claim.AttemptId.Value)
                            | IncompleteFrameAtEndOfStream ->
                                logger.LogError(
                                    "FG-224 event stream ended with an incomplete frame for {AttemptId}; reconciliation is required",
                                    claim.AttemptId.Value)

                        let finalExitKind =
                            if cancelled || interrupted || leaseLost || not terminalDrainConfirmed then
                                ChildExitKind.Forced
                            else
                                exitKind

                        match ProcessGroup.handoff finalExitKind groupStop with
                        | ChildHandoff.ReconciliationRequired ->
                            logger.LogError(
                                "FG-224 execution for {AttemptId} requires reconciliation after {ExitKind} exit and outer cleanup {StopResult}",
                                claim.AttemptId.Value,
                                finalExitKind,
                                groupStop)
                            requireReconciliation ()
                        | ChildHandoff.NaturalTerminalAllowed ->
                            // Close the drain-to-terminal race with a fresh lease.
                            refreshControl true

                            if cancelled || interrupted || leaseLost then
                                // A control change after natural leader exit is
                                // still ambiguous with nested step sessions.
                                requireReconciliation ()
                            else
                                Journal.repairTail journalPath
                                let plan = journalPath |> Journal.read |> Resume.plan

                                match plan.Terminal with
                                | Some status ->
                                    match store.PublishTerminal(claim.OrganizationId, claim.AttemptId, claim.Fence, owner, status) with
                                    | Ok _ -> dispositionRecorded <- true
                                    | Error _ ->
                                        logger.LogError("FG-224 terminal publication was refused for {AttemptId}", claim.AttemptId.Value)
                                        requireReconciliation ()
                                | None ->
                                    requireReconciliation ()
                    finally
                        if not dispositionRecorded then
                            // Exceptions after child start are forced stops. The
                            // memoized cleanup prevents a second signal pass.
                            cleanupExecution () |> ignore
                            requireReconciliation ()
        }

    let runClaim (claim: ExecutionClaim) (stoppingToken: CancellationToken) =
        task {
            // Recheck after the offer and before definition materialization or
            // BeginExecution. If an operator removes either trusted executable
            // after startup, the offered lease may expire safely; the attempt
            // never enters running and therefore needs no reconciliation.
            if ControllerConfig.executionLaunchersReady config then
                do! runReadyClaim claim stoppingToken
            else
                logger.LogError(
                    "FG-224 execution launchers became unavailable before claim {AttemptId}; the attempt was not started",
                    claim.AttemptId.Value)
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                let mutable claimed = false

                try
                    if not (ControllerConfig.executionLaunchersReady config) then
                        logger.LogError(
                            "FG-224 execution launchers are unavailable; no work will be claimed")
                    else
                        for org in
                            store.OrganizationIds()
                            |> WorkerScheduling.organizationsAfter lastClaimedOrganization do
                            if not claimed && not stoppingToken.IsCancellationRequested then
                                store.RequeueExpiredLocalAttempts org |> ignore

                                match
                                    store.ClaimNextExecution(
                                        org,
                                        owner,
                                        config.TrustPool,
                                        [ "linux" ],
                                        config.LeaseSeconds)
                                with
                                | Error error -> logger.LogError("FG-224 claim refused: {Reason}", error)
                                | Ok None -> ()
                                | Ok(Some claim) ->
                                    claimed <- true
                                    lastClaimedOrganization <- Some org
                                    do! runClaim claim stoppingToken
                with ex ->
                    logger.LogError(ex, "FG-224 worker iteration failed")

                if not claimed && not stoppingToken.IsCancellationRequested then
                    do! Task.Delay(config.PollMilliseconds, stoppingToken)
        }
