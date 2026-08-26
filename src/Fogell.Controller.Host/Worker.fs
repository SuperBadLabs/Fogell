namespace Fogell.Controller.Host

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Fogell.Domain
open Fogell.Journal
open Fogell.Store

module private Native =
    [<DllImport("libc", SetLastError = true)>]
    extern int kill(int pid, int signal)

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
    let postExitDrainByteBudget = 16 * 1024 * 1024
    let postExitDrainFrameBudget = 4096
    let postExitDrainMilliseconds = 5000

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
        if not child.HasExited then
            // setsid may not have established the process group yet.  If the
            // group signal races that exec, terminate the launcher itself so an
            // exception can never leave an unsupervised child behind.
            if Native.kill(-child.Id, 15) <> 0 then
                Native.kill(child.Id, 15) |> ignore

            if not (child.WaitForExit 2000) then
                if Native.kill(-child.Id, 9) <> 0 then
                    Native.kill(child.Id, 9) |> ignore
                child.WaitForExit 5000 |> ignore

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
            EventStream.drainToBoundary
                (openEventStream eventPath)
                state
                maxEventFrameEncodedBytes
                eventDrainByteBudget
                eventDrainFrameBudget
                postExitDrainByteBudget
                postExitDrainFrameBudget
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

    let runClaim (claim: ExecutionClaim) (stoppingToken: CancellationToken) =
        task {
            let buildKey = claim.BuildId.Value.ToString "N"
            let orgKey = claim.OrganizationId.Value.ToString "N"
            let definitionPath = Path.Combine(config.StateRoot, "definitions", orgKey, buildKey, "Jenkinsfile")
            let journalPath = Path.Combine(config.StateRoot, "journals", orgKey, buildKey + ".journal")
            let eventPath =
                Path.Combine(
                    config.StateRoot,
                    "events",
                    orgKey,
                    $"{claim.AttemptId.Value:N}-{claim.Fence.Value}.events")
            let workspaceRoot = Path.Combine(config.StateRoot, "workspaces", orgKey)
            let neutralHome = Path.Combine(config.StateRoot, "neutral-home")
            let tempRoot = Path.Combine(config.StateRoot, "tmp")
            use _eventFile = deleteEventFile eventPath

            Directory.CreateDirectory workspaceRoot |> ignore
            Directory.CreateDirectory neutralHome |> ignore
            Directory.CreateDirectory tempRoot |> ignore
            atomicDefinition definitionPath claim.PipelineSource

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

                use child = new Process()
                let start = ProcessStartInfo("/usr/bin/setsid")
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
                child.StartInfo <- start

                if not (child.Start()) then
                    store.RequireReconciliation(claim.OrganizationId, claim.AttemptId, claim.Fence, owner)
                    |> ignore
                else
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
                            // between bounded log slices. Renewal still runs for
                            // an interrupted/cancelled owner because publishing
                            // ABORTED or requeueing is itself fence-protected.
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

                        if cancelled || interrupted || leaseLost then
                            stopGroup child
                        else
                            child.WaitForExit()

                        do! completeDiagnosticDrains child drainCancellation drains

                        if not leaseLost && not cancelled && not interrupted then
                            let drainDeadline =
                                DateTimeOffset.UtcNow.AddMilliseconds(float postExitDrainMilliseconds)

                            let continueFinalDrain () =
                                refreshControl false

                                not leaseLost
                                && not cancelled
                                && not interrupted
                                && DateTimeOffset.UtcNow < drainDeadline

                            let next, completion =
                                drainFinalEvents
                                    claim
                                    eventPath
                                    eventState
                                    sequence
                                    continueFinalDrain

                            sequence <- next

                            match completion.Stop with
                            | PublicationAuthorityLost -> leaseLost <- true
                            | CumulativeBudgetExhausted ->
                                logger.LogWarning(
                                    "FG-224 post-exit event drain hit its cumulative bound for {AttemptId}; terminal publication will not wait on an unbounded writer",
                                    claim.AttemptId.Value)
                            | ControlStopped when not cancelled && not interrupted && not leaseLost ->
                                logger.LogWarning(
                                    "FG-224 post-exit event drain hit its deadline for {AttemptId}; terminal publication will not wait on an unbounded writer",
                                    claim.AttemptId.Value)
                            | EndOfStream
                            | ControlStopped -> ()

                        // Close the drain-to-terminal race with a fresh lease.
                        // PublishTerminal/RequeueOwnedAttempt still enforce the
                        // same fence, owner and restore epoch transactionally.
                        if not leaseLost then
                            refreshControl true

                        if leaseLost then
                            logger.LogError("FG-224 lease authority was lost for {AttemptId}", claim.AttemptId.Value)
                        elif cancelled then
                            store.PublishTerminal(claim.OrganizationId, claim.AttemptId, claim.Fence, owner, BuildStatus.Aborted)
                            |> ignore
                        elif interrupted then
                            store.RequeueOwnedAttempt(claim.OrganizationId, claim.AttemptId, claim.Fence, owner)
                            |> ignore
                        else
                            Journal.repairTail journalPath
                            let plan = journalPath |> Journal.read |> Resume.plan

                            match plan.Terminal with
                            | Some status ->
                                match store.PublishTerminal(claim.OrganizationId, claim.AttemptId, claim.Fence, owner, status) with
                                | Ok _ -> ()
                                | Error _ ->
                                    logger.LogError("FG-224 terminal publication was refused for {AttemptId}", claim.AttemptId.Value)
                            | None ->
                                store.RequireReconciliation(claim.OrganizationId, claim.AttemptId, claim.Fence, owner)
                                |> ignore
                    finally
                        if not child.HasExited then
                            stopGroup child
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                let mutable claimed = false

                try
                    for org in store.OrganizationIds() do
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
                                do! runClaim claim stoppingToken
                with ex ->
                    logger.LogError(ex, "FG-224 worker iteration failed")

                if not claimed && not stoppingToken.IsCancellationRequested then
                    do! Task.Delay(config.PollMilliseconds, stoppingToken)
        }
