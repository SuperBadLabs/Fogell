namespace Fogell.Controller.Api

open System
open System.IO

/// Attempt-keyed publication for archived output. The build-keyed directory is
/// mutable retry staging; the attempt directory is the stable public identity.
module ArtifactSnapshots =
    let finalize stateRoot (organizationId: Guid) (buildId: Guid) (attemptId: Guid) =
        try
            let workspaceRoot =
                Path.Combine(stateRoot, "workspaces", organizationId.ToString "N")
            let staging =
                Path.Combine(workspaceRoot, "_artifacts", buildId.ToString "N")
            let snapshots = Path.Combine(workspaceRoot, "_artifact-snapshots")
            let target = Path.Combine(snapshots, attemptId.ToString "N")
            Directory.CreateDirectory snapshots |> ignore

            let verifyPublished operationError =
                match Directory.Exists staging, Directory.Exists target with
                | false, true -> Ok target
                | true, true ->
                    Error "artifact snapshot and mutable staging directory both exist"
                | true, false ->
                    Error(
                        defaultArg
                            operationError
                            "artifact snapshot publication left mutable staging in place")
                | false, false ->
                    Error(
                        defaultArg
                            operationError
                            "artifact snapshot publication produced no snapshot directory")

            match Directory.Exists staging, Directory.Exists target with
            | true, false ->
                try
                    Directory.Move(staging, target)
                    verifyPublished None
                with ex ->
                    // A concurrent recovery/adoption caller may have completed
                    // the same atomic move after the pre-check. That is the
                    // idempotent success state; every other collision remains
                    // an error rather than guessing which bytes won.
                    verifyPublished (Some ex.Message)
            | false, false ->
                try
                    Directory.CreateDirectory target |> ignore
                    verifyPublished None
                with ex ->
                    verifyPublished (Some ex.Message)
            | false, true
            | true, true ->
                // The two existence reads are not atomic. Recheck the actual
                // post-state before accepting idempotence or reporting a real
                // staging/snapshot collision.
                verifyPublished None
        with ex ->
            Error ex.Message

    /// Before a retry starts, freeze any build-keyed bytes left by its exact
    /// parent. The child then archives into a newly created staging directory
    /// and cannot inherit parent-only files from the pre-attempt layout.
    let prepareRetry stateRoot organizationId buildId parentAttemptId =
        match parentAttemptId with
        | None -> Ok()
        | Some parent ->
            finalize stateRoot organizationId buildId parent
            |> Result.map ignore
