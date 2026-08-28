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
