namespace Fogell.Controller.Host

open System
open System.IO

type ControllerConfig =
    { RuntimeDatabaseUrl: string
      MaintenanceDatabaseUrl: string
      ApiToken: string
      ListenUrl: string
      StateRoot: string
      RunHostPath: string
      TrustPool: string
      MaxPipelineBytes: int
      MaxLogChunks: int
      PollMilliseconds: int
      LeaseSeconds: int }

module ControllerConfig =

    let internal validateWorkerTiming pollMilliseconds leaseSeconds =
        // Renewal is targeted at one third of the lease. A control check can
        // occur immediately before that target and then sleep for one complete
        // poll, so cap the poll at one third as well. The next check is then no
        // later than two thirds of the lease, leaving its final third for
        // scheduling delay and the fenced renewal round trip.
        let poll = int64 pollMilliseconds
        let leaseMilliseconds = int64 leaseSeconds * 1000L

        if poll * 3L <= leaseMilliseconds then
            Ok()
        else
            Error
                $"FOGELL_WORKER_POLL_MS must be no more than one third of FOGELL_WORKER_LEASE_SECONDS ({leaseMilliseconds / 3L} ms for {leaseSeconds} s)"

    let private required name =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> Error $"{name} is required"
        | value -> Ok value

    let private positiveInt name minimum maximum =
        match required name with
        | Error error -> Error error
        | Ok raw ->
            match Int32.TryParse raw with
            | true, value when value >= minimum && value <= maximum -> Ok value
            | _ -> Error $"{name} must be an integer from {minimum} through {maximum}"

    let private combine results =
        results
        |> List.choose (function Error value -> Some value | Ok _ -> None)
        |> function
            | [] -> Ok()
            | errors -> Error(String.concat "; " errors)

    let private value =
        function
        | Ok item -> item
        | Error error -> invalidOp error

    let load () =
        let runtime = required "FOGELL_DATABASE_URL"
        let maintenance = required "FOGELL_MAINTENANCE_DATABASE_URL"
        let tokenFile = required "FOGELL_API_TOKEN_FILE"
        let listen = required "FOGELL_LISTEN_URL"
        let stateRoot = required "FOGELL_STATE_ROOT"
        let runHost = required "FOGELL_RUN_HOST_PATH"
        let trustPool = required "FOGELL_LOCAL_TRUST_POOL"
        let maxPipeline = positiveInt "FOGELL_MAX_PIPELINE_BYTES" 1024 (16 * 1024 * 1024)
        let maxLogs = positiveInt "FOGELL_MAX_LOG_CHUNKS" 1 10000
        let poll = positiveInt "FOGELL_WORKER_POLL_MS" 25 60000
        let lease = positiveInt "FOGELL_WORKER_LEASE_SECONDS" 10 3600
        let workerTiming =
            match poll, lease with
            | Ok pollMilliseconds, Ok leaseSeconds ->
                validateWorkerTiming pollMilliseconds leaseSeconds
            | _ -> Ok()

        match combine [ runtime; maintenance; tokenFile; listen; stateRoot; runHost; trustPool ],
              combine
                  [ maxPipeline |> Result.map string
                    maxLogs |> Result.map string
                    poll |> Result.map string
                    lease |> Result.map string
                    workerTiming |> Result.map string ] with
        | Error error, _
        | _, Error error -> Error error
        | Ok _, Ok _ ->
            let runtimeValue = value runtime
            let maintenanceValue = value maintenance
            let tokenPath = value tokenFile |> Path.GetFullPath
            let listenValue = value listen
            let stateRootValue = value stateRoot |> Path.GetFullPath
            let runHostValue = value runHost |> Path.GetFullPath

            let uriResult =
                match Uri.TryCreate(listenValue, UriKind.Absolute) with
                | true, uri when uri.Scheme = Uri.UriSchemeHttps -> Ok()
                | true, uri when uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback -> Ok()
                | _ -> Error "FOGELL_LISTEN_URL must be HTTPS or loopback HTTP"

            if runtimeValue = maintenanceValue then
                Error "runtime and maintenance database URLs must be distinct capabilities"
            elif not (Path.IsPathFullyQualified stateRootValue) then
                Error "FOGELL_STATE_ROOT must be absolute"
            elif not (File.Exists runHostValue) then
                Error "FOGELL_RUN_HOST_PATH does not name a file"
            elif not (File.Exists tokenPath) then
                Error "FOGELL_API_TOKEN_FILE does not name a file"
            else
                match uriResult with
                | Error error -> Error error
                | Ok _ ->
                    let token = File.ReadAllText(tokenPath).TrimEnd('\r', '\n')

                    if token.Length < 32 || token <> token.Trim() then
                        Error "API token file must contain at least 32 non-padded characters"
                    else
                        Directory.CreateDirectory stateRootValue |> ignore
                        Ok
                            { RuntimeDatabaseUrl = runtimeValue
                              MaintenanceDatabaseUrl = maintenanceValue
                              ApiToken = token
                              ListenUrl = listenValue
                              StateRoot = stateRootValue
                              RunHostPath = runHostValue
                              TrustPool = value trustPool
                              MaxPipelineBytes = value maxPipeline
                              MaxLogChunks = value maxLogs
                              PollMilliseconds = value poll
                              LeaseSeconds = value lease }
