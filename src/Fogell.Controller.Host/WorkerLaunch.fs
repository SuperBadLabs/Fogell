namespace Fogell.Controller.Host

/// Keeps the fallible OS launch edge injectable. The cancellation check is
/// deliberately adjacent to Process.Start so graceful shutdown cannot begin
/// new user effects after a claim has crossed durable BeginExecution.
module internal WorkerLaunch =

    type LaunchResult =
        | LaunchSuppressed
        | Launched
        | LaunchFailed of exn option

    /// Return launch truth; durable fenced disposition belongs to the worker.
    /// Keeping callbacks out of this boundary prevents a reconciliation failure
    /// from being mistaken for, or recursively re-reported as, a start failure.
    let tryStart (isCancellationRequested: unit -> bool) (start: unit -> bool) =
        if isCancellationRequested () then
            LaunchSuppressed
        else
            try
                if start () then Launched else LaunchFailed None
            with ex ->
                LaunchFailed(Some ex)
