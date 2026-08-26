namespace Fogell.Controller.Host

open System
open System.IO
open Fogell.Domain

/// Durable worker paths whose identity must follow an execution attempt, not
/// the build shared by every attempt in a retry lineage.
module WorkerPaths =
    let journalPath stateRoot (organizationId: OrganizationId) (attemptId: AttemptId) =
        Path.Combine(
            stateRoot,
            "journals",
            organizationId.Value.ToString "N",
            "attempts",
            attemptId.Value.ToString "N" + ".journal")

    /// A fence-specific event file is recovery evidence until terminal truth is
    /// durably published. Every reconciliation path preserves its exact bytes.
    let deleteEventFileAfterTerminalPublication (terminalPublished: unit -> bool) eventPath =
        { new IDisposable with
            member _.Dispose() =
                if terminalPublished () then
                    try
                        File.Delete eventPath
                    with
                    | :? FileNotFoundException -> ()
                    | :? DirectoryNotFoundException -> () }
