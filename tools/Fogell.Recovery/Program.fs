module Fogell.Recovery.Program

open System
open System.Text.Json
open Fogell.Store

// Quiescence and a verified paired backup are operator preconditions; the
// database transaction advances authority and invalidates old leases atomically.
// Connection strings stay in the environment and never appear in output.
[<EntryPoint>]
let main argv =
    match argv with
    | [| "activate-restore"; "--writers-quiesced" |] ->
        match Environment.GetEnvironmentVariable "FOGELL_MAINTENANCE_DATABASE_URL" with
        | null | "" ->
            eprintfn "FOGELL_MAINTENANCE_DATABASE_URL is required"
            2
        | connection ->
            try
                let epoch = Store(connection, connection).ActivateRestore()
                printfn "%s" (JsonSerializer.Serialize {| schema_version = 1; restore_epoch = epoch.Value |})
                0
            with _ ->
                eprintfn "restore activation failed; keep writers quiesced and inspect the database"
                1
    | _ ->
        eprintfn "usage: Fogell.Recovery activate-restore --writers-quiesced"
        2
