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

    /// Freeze one attempt's archived outputs outside the build-keyed staging
    /// directory. A later retry reuses the build key, but never this immutable
    /// attempt identity. The operation is idempotent across a crash after the
    /// rename and before terminal database publication.
    let finalizeArtifactSnapshot workspaceRoot buildKey (attemptId: AttemptId) =
        try
            let staging = Path.Combine(workspaceRoot, "_artifacts", buildKey)
            let snapshots = Path.Combine(workspaceRoot, "_artifact-snapshots")
            let target = Path.Combine(snapshots, attemptId.Value.ToString "N")
            Directory.CreateDirectory snapshots |> ignore

            match Directory.Exists staging, Directory.Exists target with
            | true, false ->
                Directory.Move(staging, target)
                Ok target
            | false, false ->
                Directory.CreateDirectory target |> ignore
                Ok target
            | false, true -> Ok target
            | true, true ->
                Error "artifact snapshot and mutable staging directory both exist"
        with ex ->
            Error ex.Message

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
