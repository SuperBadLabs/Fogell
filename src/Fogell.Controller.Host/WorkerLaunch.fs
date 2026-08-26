namespace Fogell.Controller.Host

/// Keeps the fallible OS launch edge injectable. Process.Start may return false
/// or throw after durable BeginExecution; both outcomes must converge on the
/// same fenced reconciliation callback rather than escaping to lease expiry.
module internal WorkerLaunch =

    let tryStart (start: unit -> bool) (onFailure: exn option -> unit) =
        try
            if start () then
                true
            else
                onFailure None
                false
        with ex ->
            onFailure (Some ex)
            false
