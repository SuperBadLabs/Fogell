module Fogell.Retention.Program

open System
open System.Text.Json
open Fogell.Retention

[<EntryPoint>]
let main args =
    try
        let connection=Environment.GetEnvironmentVariable "FOGELL_MAINTENANCE_DATABASE_URL"
        if String.IsNullOrWhiteSpace connection then invalidArg "environment" "FOGELL_MAINTENANCE_DATABASE_URL required"
        if args=[|"migrate"|] then
            match Fogell.Store.Migrations.run connection with
            | Ok migrations -> printfn "{\"migrated\":%d}" migrations.Length;0
            | Error _ -> eprintfn "{\"error\":\"migration_failed\"}";1
        else
            if args.Length%2<>0 then invalidArg "args" "options must be key/value pairs"
            let known=set["--state-root";"--organization";"--keep-builds";"--max-bytes";"--max-age-seconds"
                          "--max-candidates";"--max-entries";"--max-operations";"--budget-seconds"]
            let keys=args |> Array.chunkBySize 2 |> Array.map Array.head
            if keys |> Array.exists(fun key->not(known.Contains key)) then invalidArg "args" "unknown option"
            if (keys |> Array.distinct |> Array.length)<>keys.Length then invalidArg "args" "duplicate option"
            let options=args |> Array.chunkBySize 2 |> Array.map(fun pair->pair[0],pair[1]) |> Map.ofArray
            let required name=options |> Map.tryFind name |> Option.defaultWith(fun ()->invalidArg name "required option")
            let number name fallback=options |> Map.tryFind name |> Option.map Int64.Parse |> Option.defaultValue fallback
            let bounded name fallback minimum maximum=
                let value=number name fallback
                if value<minimum || value>maximum then invalidArg name "out of range"
                value
            let integer name fallback minimum=int(bounded name fallback minimum 1000000L)
            let policy={KeepBuilds=integer "--keep-builds" 100L 0L;MaxBytes=bounded "--max-bytes" 1073741824L 0L Int64.MaxValue
                        MaxAgeSeconds=bounded "--max-age-seconds" 2592000L 0L Int64.MaxValue;MaxCandidates=integer "--max-candidates" 1000L 1L
                        MaxEntries=integer "--max-entries" 10000L 1L;MaxOperations=integer "--max-operations" 256L 1L
                        BudgetSeconds=int(bounded "--budget-seconds" 30L 1L 3600L)}
            let result=Retention.sweep connection (required "--state-root") (Guid.Parse(required "--organization")) policy ignore
            printfn "%s" (JsonSerializer.Serialize result)
            if result.Held>0 then 2 else 0
    with error ->
        // No connection string, filesystem content or SQL parameters in output.
        eprintfn "%s" (JsonSerializer.Serialize {|error=error.GetType().Name;message=(if error :? ArgumentException then "invalid_options" else "retention_refused")|})
        1
