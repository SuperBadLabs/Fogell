module Fogell.Controller.Api.Tests

open System
open System.Net
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Fogell.Domain
open Fogell.Store
open Fogell.Controller.Api
open Fogell.Controller.Host
open Fogell.Execution

[<DllImport("libc")>]
extern uint32 private geteuid()

let private effectiveIdentityIsRoot () = geteuid() = 0u

let private connectionString =
    match Environment.GetEnvironmentVariable "FOGELL_TEST_DATABASE_URL" with
    | null | "" -> "Host=127.0.0.1;Port=55440;Username=fogell;Database=fogell"
    | v -> v

let private available =
    try
        use c = new Npgsql.NpgsqlConnection(connectionString)
        c.Open()
        true
    with _ -> false

/// A token exactly at the documented minimum, so the tests exercise the real
/// boundary rather than something comfortably over it.
let private token = String.replicate 32 "k"

let private store = Store(connectionString)
let private maxPipelineBytes = 1024
let private stateRoot =
    IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-controller-api-{Guid.NewGuid():N}")

do IO.Directory.CreateDirectory stateRoot |> ignore

let private startServer maxLogChunks =
    let auth =
        match Authorization.configure token with
        | Ok c -> c
        | Error e -> failwith e

    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
    builder.WebHost.ConfigureKestrel(fun options ->
        options.Limits.MaxRequestBodySize <- Nullable())
    |> ignore
    builder.Logging.ClearProviders() |> ignore
    let app = builder.Build()

    Router.map
        { Store = store
          Auth = auth
          TrustPool = "trusted-linux"
          StateRoot = stateRoot
          MaxPipelineBytes = maxPipelineBytes
          MaxLogChunks = maxLogChunks }
        app
    |> ignore

    app.StartAsync() |> Async.AwaitTask |> Async.RunSynchronously

    let address =
        app.Urls |> Seq.head

    app, address

let private freshProject () =
    let org = OrganizationId(Guid.NewGuid())
    let project = ProjectId(Guid.NewGuid())
    store.CreateProject(org, $"org-{org.Value}", project, "p")
    org, project

let private pipeline =
    "pipeline {\n  agent any\n  stages {\n    stage('Build') {\n      steps { echo 'hi' }\n    }\n  }\n}\n"

let private client = new HttpClient()

/// ByteArrayContent can re-derive Content-Length while HttpClient prepares the
/// request, even after the header was cleared. This fixture deliberately cannot
/// report a length, so HTTP/1.1 must exercise real chunk framing and Kestrel's
/// unknown-length body path.
type private UnknownLengthContent(body: byte array) =
    inherit HttpContent()

    override _.SerializeToStream(stream, _context, cancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()
        stream.Write(body, 0, body.Length)

    override _.SerializeToStreamAsync(stream, _context) =
        stream.WriteAsync(body, 0, body.Length)

    override _.TryComputeLength(length: byref<int64>) =
        length <- 0L
        false

let private send (method: HttpMethod) (url: string) (bearer: string option) (idem: string option) (body: string option) =
    let req = new HttpRequestMessage(method, url)
    bearer |> Option.iter (fun t -> req.Headers.TryAddWithoutValidation("authorization", $"Bearer {t}") |> ignore)
    idem |> Option.iter (fun k -> req.Headers.TryAddWithoutValidation("idempotency-key", k) |> ignore)
    body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/x-jenkinsfile"))
    let r = client.Send req
    let text = r.Content.ReadAsStringAsync().Result
    int r.StatusCode, text

let private sendBytes (url: string) (idem: string) (body: byte array) =
    use req = new HttpRequestMessage(HttpMethod.Post, url)
    req.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}") |> ignore
    req.Headers.TryAddWithoutValidation("idempotency-key", idem) |> ignore
    req.Content <- new ByteArrayContent(body)
    req.Content.Headers.TryAddWithoutValidation("content-type", "application/x-jenkinsfile") |> ignore
    use response = client.Send req
    int response.StatusCode, response.Content.ReadAsStringAsync().Result

let private sendChunked (url: string) (idem: string) (body: byte array) =
    use req = new HttpRequestMessage(HttpMethod.Post, url)
    req.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}") |> ignore
    req.Headers.TryAddWithoutValidation("idempotency-key", idem) |> ignore
    req.Headers.TransferEncodingChunked <- Nullable true
    req.Content <- new UnknownLengthContent(body)
    req.Content.Headers.TryAddWithoutValidation("content-type", "application/x-jenkinsfile") |> ignore
    use response = client.Send req
    int response.StatusCode, response.Content.ReadAsStringAsync().Result

let private getArtifact (url: string) bearer =
    use request = new HttpRequestMessage(HttpMethod.Get, url)
    bearer
    |> Option.iter (fun value ->
        request.Headers.TryAddWithoutValidation("authorization", $"Bearer {value}") |> ignore)
    use response = client.Send request
    let body = response.Content.ReadAsByteArrayAsync().Result
    let contentType = response.Content.Headers.ContentType |> Option.ofObj |> Option.map string
    let contentLength = response.Content.Headers.ContentLength |> Option.ofNullable
    let disposition = response.Content.Headers.ContentDisposition |> Option.ofObj |> Option.map string
    let noSniff =
        match response.Headers.TryGetValues "X-Content-Type-Options" with
        | true, values -> values |> Seq.toList
        | _ -> []
    int response.StatusCode, body, contentType, contentLength, disposition, noSniff

let private stateRootReadiness =
    let status databaseReady capabilitiesReady launchersReady stateRootReady =
        Fogell.Controller.Host.Program.readinessStatus
            (fun () -> databaseReady)
            (fun () -> capabilitiesReady)
            (fun () -> launchersReady)
            (fun () -> stateRootReady)

    testList
        "FG-224 runtime state-root readiness"
        [ test "a removed root stays unavailable until the operator restores it" {
              let root =
                  IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-runtime-state-{Guid.NewGuid():N}")

              let evaluated = ResizeArray<string>()
              let check name result () =
                  evaluated.Add name
                  result

              Expect.equal
                  (Fogell.Controller.Host.Program.readinessStatus
                      (check "database" true)
                      (check "capabilities" true)
                      (check "launchers" false)
                      (check "state-root" true))
                  503
                  "an earlier readiness failure is unavailable"
              Expect.sequenceEqual
                  evaluated
                  [| "database"; "capabilities"; "launchers" |]
                  "ordered readiness short-circuits before the durable state-root probe"

              let mutable now = 0L
              let mutable probes = 0
              let mutable cachedReady = true
              let readinessCache =
                  ControllerConfig.StateRootReadinessCache(
                      ControllerConfig.stateRootProbeIntervalMilliseconds,
                      (fun () -> now),
                      (fun () ->
                          probes <- probes + 1
                          cachedReady))

              let cachedStatus () =
                  Fogell.Controller.Host.Program.readinessStatus
                      (fun () -> true)
                      (fun () -> true)
                      (fun () -> true)
                      (fun () -> readinessCache.Cached())

              for poll in 0L..39L do
                  now <- poll * 25L
                  Expect.equal (cachedStatus ()) 200 "healthy readiness uses the cached state-root result"

              Expect.equal probes 1 "forty minimum-interval requests perform one durable probe"
              cachedReady <- false
              now <- 1000L
              Expect.equal (cachedStatus ()) 503 "the cadence boundary observes a failed probe"
              Expect.equal probes 2 "one elapsed cadence performs one additional probe"
              cachedReady <- true
              now <- 1999L
              Expect.equal (cachedStatus ()) 503 "failure remains cached for less than one cadence"
              now <- 2000L
              Expect.equal (cachedStatus ()) 200 "readiness recovers at the next cadence boundary"
              Expect.equal probes 3 "recovery performs exactly one additional probe"

              use startCalls = new Threading.ManualResetEventSlim(false)
              use probeEntered = new Threading.ManualResetEventSlim(false)
              use releaseProbe = new Threading.ManualResetEventSlim(false)
              let probeCountGate = obj ()
              let mutable concurrentProbes = 0
              let concurrentCache =
                  ControllerConfig.StateRootReadinessCache(
                      ControllerConfig.stateRootProbeIntervalMilliseconds,
                      (fun () -> 0L),
                      (fun () ->
                          lock probeCountGate (fun () -> concurrentProbes <- concurrentProbes + 1)
                          probeEntered.Set()
                          releaseProbe.Wait()
                          true))

              let concurrentCalls =
                  [| 1..32 |]
                  |> Array.map (fun _ ->
                      Threading.Tasks.Task.Run(fun () ->
                          startCalls.Wait()
                          concurrentCache.Cached()))

              startCalls.Set()
              Expect.isTrue (probeEntered.Wait 2000) "one concurrent request enters the durable probe"
              Threading.Thread.Sleep 50
              releaseProbe.Set()
              Expect.isTrue
                  (Threading.Tasks.Task.WaitAll(
                      concurrentCalls |> Array.map (fun call -> call :> Threading.Tasks.Task),
                      2000))
                  "all concurrent readiness callers receive the cached result"
              Expect.isTrue
                  (concurrentCalls |> Array.forall (fun call -> call.Result))
                  "the shared cached result remains ready"
              Expect.equal concurrentProbes 1 "concurrent callers cannot stampede the durable probe"

              try
                  IO.Directory.CreateDirectory root |> ignore
                  Expect.isTrue (ControllerConfig.stateRootReadyAt root) "the available root accepts a durable probe"
                  Expect.isEmpty
                      (IO.Directory.GetFileSystemEntries root)
                      "the successful probe leaves no sentinel"
                  Expect.equal
                      (status true true true true)
                      200
                      "all runtime dependencies produce ready"

                  IO.Directory.Delete root
                  Expect.isFalse (ControllerConfig.stateRootReadyAt root) "a removed root is unavailable"
                  Expect.isFalse
                      (IO.Directory.Exists root)
                      "the runtime probe never recreates a missing configured root"
                  Expect.equal
                      (status true true true false)
                      503
                      "the endpoint fails closed when only the state root is unavailable"

                  IO.Directory.CreateDirectory root |> ignore
                  Expect.isTrue (ControllerConfig.stateRootReadyAt root) "an operator-restored root recovers"
                  Expect.equal
                      (status true true true true)
                      200
                      "readiness recovers without restarting the controller"
                  Expect.isEmpty
                      (IO.Directory.GetFileSystemEntries root)
                      "the recovery probe leaves no sentinel"
              finally
                  if IO.Directory.Exists root then
                      IO.Directory.Delete(root, true)
          }

          test "a failed write probe is unavailable, cleaned, and recoverable" {
              let root =
                  IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-runtime-unwritable-{Guid.NewGuid():N}")

              let operatorData = IO.Path.Combine(root, "operator-data")

              try
                  IO.Directory.CreateDirectory root |> ignore
                  IO.File.WriteAllText(operatorData, "preserve me")

                  let partialWriteThenFail probePath =
                      IO.File.WriteAllText(probePath, "partial probe")
                      raise (UnauthorizedAccessException "simulated read-only state volume")

                  Expect.isFalse
                      (ControllerConfig.stateRootReadyAtWith partialWriteThenFail root)
                      "a write or flush failure makes the state dependency unavailable"
                  Expect.equal
                      (status true true true false)
                      503
                      "the endpoint fails closed on the write-probe result"
                  Expect.sequenceEqual
                      (IO.Directory.GetFileSystemEntries root)
                      [| operatorData |]
                      "failure cleanup removes the partial probe without changing operator data"
                  Expect.equal (IO.File.ReadAllText operatorData) "preserve me" "existing durable state is untouched"

                  IO.File.SetUnixFileMode(root, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserExecute)

                  if not (effectiveIdentityIsRoot ()) then
                      Expect.isFalse
                          (ControllerConfig.stateRootReadyAt root)
                          "the production probe follows the service identity on a read-only root"
                      Expect.sequenceEqual
                          (IO.Directory.GetFileSystemEntries root)
                          [| operatorData |]
                          "a refused production probe leaves no artifact"

                  IO.File.SetUnixFileMode(
                      root,
                      IO.UnixFileMode.UserRead
                      ||| IO.UnixFileMode.UserWrite
                      ||| IO.UnixFileMode.UserExecute)

                  Expect.isTrue (ControllerConfig.stateRootReadyAt root) "the real probe succeeds after recovery"
                  Expect.equal
                      (status true true true true)
                      200
                      "readiness recovers after write access returns"
                  Expect.sequenceEqual
                      (IO.Directory.GetFileSystemEntries root)
                      [| operatorData |]
                      "the successful recovery probe also leaves no artifact"
              finally
                  if IO.Directory.Exists root then
                      IO.File.SetUnixFileMode(
                          root,
                          IO.UnixFileMode.UserRead
                          ||| IO.UnixFileMode.UserWrite
                          ||| IO.UnixFileMode.UserExecute)
                      IO.Directory.Delete(root, true)
          } ]

let private databaseStartupBoundary =
    let runtime =
        Ok
            { User = "fogell_runtime"
              IsSuperuser = false
              BypassesRls = false }

    let maintenance =
        Ok
            { User = "fogell_maintenance"
              IsSuperuser = false
              BypassesRls = false }

    testList
        "FG-224 database startup boundary"
        [ test "the live database-pair proof gates otherwise valid identities" {
              let calls = ResizeArray<string>()

              Expect.equal
                  (Fogell.Controller.Host.Program.databaseStartupError
                      (fun () -> calls.Add "pair"; false)
                      (fun () -> calls.Add "migrate"; Ok [])
                      (fun () -> calls.Add "runtime"; runtime)
                      (fun () -> calls.Add "maintenance"; maintenance)
                      (fun () -> calls.Add "capabilities"; true))
                  (Some Fogell.Controller.Host.Program.DatabaseStartupError.PairMismatch)
                  "different databases refuse before migration or role metadata"
              Expect.sequenceEqual calls [ "pair" ] "pair mismatch short-circuits every mutating or downstream check"

              calls.Clear()
              Expect.equal
                  (Fogell.Controller.Host.Program.databaseStartupError
                      (fun () -> calls.Add "pair"; true)
                      (fun () -> calls.Add "migrate"; Ok [])
                      (fun () -> calls.Add "runtime"; runtime)
                      (fun () -> calls.Add "maintenance"; maintenance)
                      (fun () -> calls.Add "capabilities"; true))
                  None
                  "same database with separate least-privilege roles proceeds"
              Expect.sequenceEqual
                  calls
                  [ "pair"; "migrate"; "runtime"; "maintenance"; "capabilities" ]
                  "startup preserves pair-proof, migration, and validation order"

              calls.Clear()
              Expect.equal
                  (Fogell.Controller.Host.Program.databaseStartupError
                      (fun () -> calls.Add "pair"; true)
                      (fun () -> calls.Add "migrate"; Error "boom")
                      (fun () -> calls.Add "runtime"; runtime)
                      (fun () -> calls.Add "maintenance"; maintenance)
                      (fun () -> calls.Add "capabilities"; true))
                  (Some Fogell.Controller.Host.Program.DatabaseStartupError.MigrationFailed)
                  "migration failure remains a distinct startup refusal"
              Expect.sequenceEqual calls [ "pair"; "migrate" ] "migration failure short-circuits validation"

              let adminBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              adminBuilder.Database <- "postgres"
              let otherName = $"fogell_program_pair_{Guid.NewGuid():N}"
              let otherBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              otherBuilder.Database <- otherName

              use admin = new Npgsql.NpgsqlConnection(adminBuilder.ConnectionString)
              admin.Open()
              use create = admin.CreateCommand()
              create.CommandText <- $"CREATE DATABASE {otherName}"
              create.ExecuteNonQuery() |> ignore

              try
                  Expect.equal
                      (Fogell.Controller.Host.Program.databaseStartupErrorForStores
                          (Store(connectionString, otherBuilder.ConnectionString))
                          (Store(connectionString))
                          (Store(otherBuilder.ConnectionString)))
                      (Some Fogell.Controller.Host.Program.DatabaseStartupError.PairMismatch)
                      "the production Store wiring executes the live pair challenge before migration"

                  use untouched = new Npgsql.NpgsqlConnection(otherBuilder.ConnectionString)
                  untouched.Open()
                  use inspect = untouched.CreateCommand()
                  inspect.CommandText <- "SELECT to_regclass('public.schema_migrations') IS NULL"
                  Expect.isTrue
                      (inspect.ExecuteScalar() :?> bool)
                      "a mismatched maintenance database remains completely unmigrated"
              finally
                  Npgsql.NpgsqlConnection.ClearAllPools()
                  use terminate = admin.CreateCommand()
                  terminate.CommandText <-
                      "SELECT pg_terminate_backend(pid)
                         FROM pg_stat_activity
                        WHERE datname = @database AND pid <> pg_backend_pid()"
                  terminate.Parameters.AddWithValue("database", otherName) |> ignore
                  terminate.ExecuteNonQuery() |> ignore
                  use drop = admin.CreateCommand()
                  drop.CommandText <- $"DROP DATABASE {otherName}"
                  drop.ExecuteNonQuery() |> ignore
          } ]

let private executionLauncherValidation =
    let variables =
        [ "FOGELL_DATABASE_URL"; "FOGELL_MAINTENANCE_DATABASE_URL"
          "FOGELL_API_TOKEN_FILE"; "FOGELL_LISTEN_URL"; "FOGELL_STATE_ROOT"
          "FOGELL_RUN_HOST_PATH"; "FOGELL_LOCAL_TRUST_POOL"
          "FOGELL_MAX_PIPELINE_BYTES"; "FOGELL_MAX_LOG_CHUNKS"
          "FOGELL_WORKER_POLL_MS"; "FOGELL_WORKER_LEASE_SECONDS" ]

    let withConfiguration f =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-setsid-validation-" + Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory root |> ignore
        let tokenFile = IO.Path.Combine(root, "token")
        let runHost = IO.Path.Combine(root, "run-host")
        IO.File.WriteAllText(tokenFile, String.replicate 32 "t")
        IO.File.WriteAllText(runHost, "#!/bin/sh\nexit 0\n")
        IO.File.SetUnixFileMode(runHost, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserExecute)
        let previous = variables |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)
        let set name value = Environment.SetEnvironmentVariable(name, value)

        set "FOGELL_DATABASE_URL" "Host=runtime;Database=fogell"
        set "FOGELL_MAINTENANCE_DATABASE_URL" "Host=maintenance;Database=fogell"
        set "FOGELL_API_TOKEN_FILE" tokenFile
        set "FOGELL_LISTEN_URL" "http://127.0.0.1:18083"
        set "FOGELL_STATE_ROOT" (IO.Path.Combine(root, "state"))
        set "FOGELL_RUN_HOST_PATH" runHost
        set "FOGELL_LOCAL_TRUST_POOL" "trusted-linux"
        set "FOGELL_MAX_PIPELINE_BYTES" "1024"
        set "FOGELL_MAX_LOG_CHUNKS" "100"
        set "FOGELL_WORKER_POLL_MS" "50"
        set "FOGELL_WORKER_LEASE_SECONDS" "60"

        try f root runHost
        finally
            previous |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))
            IO.Directory.Delete(root, true)

    testList
        "FG-224 trusted setsid launcher"
        [ test "the exact trusted launcher is shared into worker configuration" {
              withConfiguration (fun _ _ ->
                  match ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher with
                  | Error error -> failtestf "trusted host launcher was refused: %s" error
                  | Ok config ->
                      Expect.equal config.SetsidPath "/usr/bin/setsid" "worker consumes the validated exact identity"
                      Expect.isTrue (ControllerConfig.executionLaunchersReady config) "both configured launchers are executable")
          }

          test "a whitespace-only local trust pool refuses startup" {
              withConfiguration (fun _ _ ->
                  Environment.SetEnvironmentVariable("FOGELL_LOCAL_TRUST_POOL", " \t ")

                  Expect.equal
                      (ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher)
                      (Error "FOGELL_LOCAL_TRUST_POOL is required")
                      "startup fails before every admission can be rejected by the Store")
          }

          test "a missing trusted launcher refuses startup" {
              withConfiguration (fun root runHost ->
                  let missing = IO.Path.Combine(root, "missing-setsid")
                  Expect.isFalse
                      (ControllerConfig.executionLaunchersReadyAt runHost missing)
                      "readiness and the pre-claim guard reject the missing launcher"
                  match ControllerConfig.loadWithSetsidLauncher missing with
                  | Ok _ -> failtest "missing setsid launcher reached a configured controller"
                  | Error error ->
                      Expect.equal error "trusted setsid launcher does not name an executable file" "stable refusal")
          }

          test "reconciliation preserves event bytes while terminal publication deletes them" {
              let root = IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-event-retention-{Guid.NewGuid():N}")
              IO.Directory.CreateDirectory root |> ignore
              let eventPath = IO.Path.Combine(root, "attempt.events")
              let exactBytes = [| 0uy; 1uy; 2uy; 255uy |]

              try
                  IO.File.WriteAllBytes(eventPath, exactBytes)
                  let preserve =
                      WorkerPaths.deleteEventFileAfterTerminalPublication
                          (fun () -> false)
                          eventPath
                  preserve.Dispose()
                  Expect.isTrue (IO.File.Exists eventPath) "reconciliation retains the fence-specific event file"
                  Expect.sequenceEqual (IO.File.ReadAllBytes eventPath) exactBytes "retained recovery bytes stay exact"

                  let remove =
                      WorkerPaths.deleteEventFileAfterTerminalPublication
                          (fun () -> true)
                          eventPath
                  remove.Dispose()
                  Expect.isFalse (IO.File.Exists eventPath) "durable terminal publication permits cleanup"
              finally
                  IO.Directory.Delete(root, true)
          }

          test "a non-executable trusted launcher refuses startup" {
              withConfiguration (fun root runHost ->
                  let nonExecutable = IO.Path.Combine(root, "non-executable-setsid")
                  IO.File.WriteAllText(nonExecutable, "not executable")
                  IO.File.SetUnixFileMode(nonExecutable, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite)
                  Expect.isFalse
                      (ControllerConfig.executionLaunchersReadyAt runHost nonExecutable)
                      "readiness and the pre-claim guard reject the non-executable launcher"
                  match ControllerConfig.loadWithSetsidLauncher nonExecutable with
                  | Ok _ -> failtest "non-executable setsid launcher reached a configured controller"
                  | Error error ->
                      Expect.equal error "trusted setsid launcher does not name an executable file" "stable refusal")

              let mutable startEffects = 0
              let suppressed =
                  WorkerLaunch.tryStart
                      (fun () -> true)
                      (fun () ->
                          startEffects <- startEffects + 1
                          true)
              Expect.equal suppressed WorkerLaunch.LaunchSuppressed "shutdown suppresses launch at the OS edge"
              Expect.equal startEffects 0 "a pre-cancelled launch creates no user effect"

              let thrown =
                  WorkerLaunch.tryStart
                      (fun () -> false)
                      (fun () -> raise (InvalidOperationException "simulated exec failure"))
              match thrown with
              | WorkerLaunch.LaunchFailed(Some (:? InvalidOperationException as error)) ->
                  Expect.equal error.Message "simulated exec failure" "the failure result preserves the cause"
              | other -> failtestf "launch exception was not preserved: %A" other

              Expect.equal
                  (WorkerLaunch.tryStart (fun () -> false) (fun () -> false))
                  (WorkerLaunch.LaunchFailed None)
                  "a false Process.Start result is explicit failure"
              Expect.equal
                  (WorkerLaunch.tryStart (fun () -> false) (fun () -> true))
                  WorkerLaunch.Launched
                  "a successful Process.Start result is explicit"
          } ]

/// FG-060. Authorization is proven BEFORE anything else, because every other
/// test would otherwise be running against an open endpoint.
let authorization =
    testList
        "FG-060 authorization"
        [ test "a token under the minimum is refused at STARTUP, not per-request" {
              match Authorization.configure "tooshort" with
              | Error m ->
                  Expect.stringContains m "32" "states the minimum"
                  Expect.stringContains m "not authentication" "says why it matters"
              | Ok _ -> failtest "a weak token must not configure"
          }

          test "an empty token is refused" {
              match Authorization.configure "" with
              | Error _ -> ()
              | Ok _ -> failtest "empty must not configure"
          }

          test "a token at exactly the minimum is accepted" {
              match Authorization.configure (String.replicate 32 "a") with
              | Ok _ -> ()
              | Error e -> failtestf "32 bytes must be accepted: %s" e
          }

          test "the correct token authorizes; wrong, absent and malformed do not" {
              let cfg =
                  match Authorization.configure token with
                  | Ok c -> c
                  | Error e -> failtest e

              Expect.isTrue (Authorization.authorize cfg (Some $"Bearer {token}")) "correct"
              Expect.isFalse (Authorization.authorize cfg None) "absent"
              Expect.isFalse (Authorization.authorize cfg (Some "Bearer wrong")) "wrong value"
              Expect.isFalse (Authorization.authorize cfg (Some token)) "missing Bearer prefix"
              Expect.isFalse (Authorization.authorize cfg (Some $"Basic {token}")) "wrong scheme"
              Expect.isFalse (Authorization.authorize cfg (Some($"Bearer {token}x"))) "trailing byte"
          } ]

let endpoints =
    let app, baseUrl = startServer 1000

    testList
        "FG-060 endpoints"
        [ test "every route denies an unauthenticated caller" {
              let org, project = freshProject ()
              let o, p = org.Value, project.Value
              let b = Guid.NewGuid()

              let routes =
                  [ HttpMethod.Post, $"{baseUrl}/api/v1/organizations/{o}/projects/{p}/builds"
                    HttpMethod.Get, $"{baseUrl}/api/v1/organizations/{o}/projects/{p}/builds/{b}"
                    HttpMethod.Get, $"{baseUrl}/api/v1/organizations/{o}/projects/{p}/builds/{b}/logs"
                    HttpMethod.Get, $"{baseUrl}/api/v1/organizations/{o}/projects/{p}/builds/{b}/attempts/{Guid.NewGuid()}/artifacts/output.bin"
                    HttpMethod.Post, $"{baseUrl}/api/v1/organizations/{o}/projects/{p}/builds/{b}/cancel"
                    HttpMethod.Get, $"{baseUrl}/api/v1/organizations/{o}/scheduler/explain" ]

              for method, url in routes do
                  let code, body = send method url None (Some "k") (Some pipeline)
                  Expect.equal code 401 $"{method} {url} must deny"
                  Expect.stringContains body "unauthorized" "names the reason"
          }

          test "submit admits a build and returns 201, then 200 on replay" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"

              let code1, body1 = send HttpMethod.Post url (Some token) (Some "api-1") (Some pipeline)
              Expect.equal code1 201 "created"
              Expect.stringContains body1 "\"was_existing\":false" "fresh admission"

              let code2, body2 = send HttpMethod.Post url (Some token) (Some "api-1") (Some pipeline)
              Expect.equal code2 200 "replay is 200, not a second 201"
              Expect.stringContains body2 "\"was_existing\":true" "recognised as existing"
          }

          test "execution preflight refuses before build and idempotency admission" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let unsupported =
                  "pipeline { agent any tools { maven 'm3' } stages { stage('Build') { steps { echo 'hi' } } } }"

              let buildCount () =
                  use connection = new Npgsql.NpgsqlConnection(connectionString)
                  connection.Open()
                  use command = connection.CreateCommand()
                  command.CommandText <-
                      "SELECT count(*) FROM builds WHERE organization_id=@organization AND project_id=@project"
                  command.Parameters.AddWithValue("organization", org.Value) |> ignore
                  command.Parameters.AddWithValue("project", project.Value) |> ignore
                  Convert.ToInt32(command.ExecuteScalar())

              Expect.equal (buildCount ()) 0 "fixture begins without a build"

              for attempt in 1..2 do
                  let code, body =
                      send HttpMethod.Post url (Some token) (Some "unsupported-retry") (Some unsupported)
                  Expect.equal code 422 $"unsupported attempt {attempt} is not durably admitted"
                  use payload = JsonDocument.Parse body
                  Expect.equal
                      (payload.RootElement.GetProperty("code").GetString())
                      "execution_unsupported"
                      $"unsupported attempt {attempt} has the stable API code"
                  Expect.stringContains
                      (payload.RootElement.GetProperty("message").GetString())
                      "unsupported_tools"
                      $"unsupported attempt {attempt} preserves the shared preflight reason"
                  Expect.equal (buildCount ()) 0 $"unsupported attempt {attempt} creates no build"

              let supportedCode, supportedBody =
                  send HttpMethod.Post url (Some token) (Some "unsupported-retry") (Some pipeline)
              Expect.equal supportedCode 201 "the rejected key remains available to supported source"
              Expect.stringContains supportedBody "\"was_existing\":false" "supported source is a fresh admission"
              Expect.equal (buildCount ()) 1 "only the supported request creates a build"

              let replayCode, replayBody =
                  send HttpMethod.Post url (Some token) (Some "unsupported-retry") (Some pipeline)
              Expect.equal replayCode 200 "the supported admission still replays normally"
              Expect.stringContains replayBody "\"was_existing\":true" "the replay finds the one supported build"
              Expect.equal (buildCount ()) 1 "replay creates no second build"
          }

          test "unbounded input is refused before build and idempotency admission" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let unbounded =
                  "pipeline { agent any stages { stage('Gate') { steps { input message: 'Deploy?' } } } }"

              let buildCount () =
                  use connection = new Npgsql.NpgsqlConnection(connectionString)
                  connection.Open()
                  use command = connection.CreateCommand()
                  command.CommandText <-
                      "SELECT count(*) FROM builds WHERE organization_id=@organization AND project_id=@project"
                  command.Parameters.AddWithValue("organization", org.Value) |> ignore
                  command.Parameters.AddWithValue("project", project.Value) |> ignore
                  Convert.ToInt32(command.ExecuteScalar())

              for attempt in 1..2 do
                  let code, body =
                      send HttpMethod.Post url (Some token) (Some "approval-boundary") (Some unbounded)
                  Expect.equal code 422 $"attempt {attempt} is rejected before admission"
                  use payload = JsonDocument.Parse body
                  Expect.equal
                      (payload.RootElement.GetProperty("code").GetString())
                      "execution_unsupported"
                      $"attempt {attempt} keeps the public capability code"
                  Expect.stringStarts
                      (payload.RootElement.GetProperty("message").GetString())
                      "unsupported_input_approval:"
                      $"attempt {attempt} names the exact controller limitation"
                  Expect.equal (buildCount ()) 0 $"attempt {attempt} creates no durable build"

              let accepted, acceptedBody =
                  send HttpMethod.Post url (Some token) (Some "approval-boundary") (Some pipeline)
              Expect.equal accepted 201 "the refused request did not bind its idempotency key"
              Expect.stringContains acceptedBody "\"was_existing\":false" "the replacement is a fresh admission"
              Expect.equal (buildCount ()) 1 "only the supported replacement was admitted"

              let legacyOrg, legacyProject = freshProject ()
              let legacyKey = "legacy-unbounded-input"
              let legacyInput: NewBuild =
                  { OrganizationId = legacyOrg
                    ProjectId = legacyProject
                    IdempotencyKey = legacyKey
                    PipelineSource = Encoding.UTF8.GetBytes unbounded
                    StageNames = [ "Gate" ]
                    RequiredTrustPool = "trusted-linux"
                    RequiredCapabilities = [ "linux" ] }

              match store.AdmitBuild legacyInput with
              | Error why -> failtestf "legacy approval admission seed failed: %s" why
              | Ok _ -> ()

              let legacyUrl =
                  $"{baseUrl}/api/v1/organizations/{legacyOrg.Value}/projects/{legacyProject.Value}/builds"
              let replayCode, replayBody =
                  send HttpMethod.Post legacyUrl (Some token) (Some legacyKey) (Some unbounded)
              Expect.equal replayCode 200 "an exact legacy admission still replays before fresh preflight"
              Expect.stringContains replayBody "\"was_existing\":true" "legacy replay returns its durable identity"
          }

          test "persisted journal-key preflight refuses before build and idempotency admission" {
              let cases =
                  [ "duplicate flattened stage name",
                    "journal-duplicate",
                    "pipeline { agent any stages { stage('same') { steps { echo 'first' } } stage('outer') { parallel { stage('same') { steps { echo 'second' } } } } } }",
                    "persisted runs require globally unique stage names; duplicated: same"
                    "tab in stage name",
                    "journal-tab",
                    "pipeline { agent any stages { stage('bad\\tname') { steps { echo 'hi' } } } }",
                    "persisted runs cannot journal stage names containing tabs, newlines, or carriage returns: bad\\tname"
                    "newline in stage name",
                    "journal-newline",
                    "pipeline { agent any stages { stage('bad\\nname') { steps { echo 'hi' } } } }",
                    "persisted runs cannot journal stage names containing tabs, newlines, or carriage returns: bad\\nname"
                    "carriage return in stage name",
                    "journal-carriage-return",
                    "pipeline { agent any stages { stage('bad\\rname') { steps { echo 'hi' } } } }",
                    "persisted runs cannot journal stage names containing tabs, newlines, or carriage returns: bad\\rname" ]

              for label, key, source, expectedReason in cases do
                  let org, project = freshProject ()
                  let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"

                  let buildCount () =
                      use connection = new Npgsql.NpgsqlConnection(connectionString)
                      connection.Open()
                      use command = connection.CreateCommand()
                      command.CommandText <-
                          "SELECT count(*) FROM builds WHERE organization_id=@organization AND project_id=@project"
                      command.Parameters.AddWithValue("organization", org.Value) |> ignore
                      command.Parameters.AddWithValue("project", project.Value) |> ignore
                      Convert.ToInt32(command.ExecuteScalar())

                  for attempt in 1..2 do
                      let code, body = send HttpMethod.Post url (Some token) (Some key) (Some source)
                      Expect.equal code 422 $"{label}: attempt {attempt} is not durably admitted"
                      use payload = JsonDocument.Parse body
                      Expect.equal
                          (payload.RootElement.GetProperty("code").GetString())
                          "execution_unsupported"
                          $"{label}: attempt {attempt} has the stable API code"
                      Expect.equal
                          (payload.RootElement.GetProperty("message").GetString())
                          expectedReason
                          $"{label}: attempt {attempt} preserves the canonical persisted preflight reason"
                      Expect.equal (buildCount ()) 0 $"{label}: attempt {attempt} creates no build"

                  let supportedCode, supportedBody =
                      send HttpMethod.Post url (Some token) (Some key) (Some pipeline)
                  Expect.equal supportedCode 201 $"{label}: the rejected key remains available"
                  Expect.stringContains supportedBody "\"was_existing\":false" $"{label}: valid source is fresh"
                  Expect.equal (buildCount ()) 1 $"{label}: only valid source creates a build"
          }

          test "legacy journal-unsafe admission replays before fresh-source preflight" {
              let unsafeSource =
                  "pipeline { agent any stages { stage('legacy\\tstage') { steps { echo 'hi' } } } }"

              let legacyOrg, legacyProject = freshProject ()
              let legacyKey = "legacy-unsafe-replay"
              let legacyInput: NewBuild =
                  { OrganizationId = legacyOrg
                    ProjectId = legacyProject
                    IdempotencyKey = legacyKey
                    PipelineSource = Encoding.UTF8.GetBytes unsafeSource
                    StageNames = [ "legacy\tstage" ]
                    RequiredTrustPool = "trusted-linux"
                    RequiredCapabilities = [ "linux" ] }

              let seeded =
                  match store.AdmitBuild legacyInput with
                  | Ok admission -> admission
                  | Error why -> failtestf "legacy direct seed failed: %s" why

              let countBuilds (org: OrganizationId) (project: ProjectId) =
                  use connection = new Npgsql.NpgsqlConnection(connectionString)
                  connection.Open()
                  use command = connection.CreateCommand()
                  command.CommandText <-
                      "SELECT count(*) FROM builds WHERE organization_id=@organization AND project_id=@project"
                  command.Parameters.AddWithValue("organization", org.Value) |> ignore
                  command.Parameters.AddWithValue("project", project.Value) |> ignore
                  Convert.ToInt32(command.ExecuteScalar())

              let legacyUrl =
                  $"{baseUrl}/api/v1/organizations/{legacyOrg.Value}/projects/{legacyProject.Value}/builds"
              let replayCode, replayBody =
                  send HttpMethod.Post legacyUrl (Some token) (Some legacyKey) (Some unsafeSource)
              Expect.equal replayCode 200 "an exact legacy replay keeps its original admission contract"
              Expect.stringContains replayBody (string seeded.BuildId.Value) "the original build is returned"
              Expect.stringContains replayBody "\"was_existing\":true" "the response is an idempotent replay"
              Expect.equal (countBuilds legacyOrg legacyProject) 1 "exact replay creates no build"
              Expect.equal (store.CountEvents(legacyOrg, seeded.BuildId, "build.admitted")) 1 "no replay event"
              Expect.equal (store.CountOutbox legacyOrg) 1 "no replay outbox message"

              let changedCode, changedBody =
                  send HttpMethod.Post legacyUrl (Some token) (Some legacyKey)
                      (Some(unsafeSource.Replace("echo 'hi'", "echo 'changed'")))
              Expect.equal changedCode 409 "the legacy key cannot substitute different bytes"
              Expect.stringContains changedBody "idempotency_conflict" "conflict keeps its stable API code"
              Expect.equal (countBuilds legacyOrg legacyProject) 1 "conflict creates no build"

              let freshOrg, freshProjectId = freshProject ()
              let freshUrl =
                  $"{baseUrl}/api/v1/organizations/{freshOrg.Value}/projects/{freshProjectId.Value}/builds"
              let freshCode, freshBody =
                  send HttpMethod.Post freshUrl (Some token) (Some "fresh-unsafe") (Some unsafeSource)
              Expect.equal freshCode 422 "the compatibility probe does not admit fresh unsafe source"
              Expect.stringContains freshBody "execution_unsupported" "fresh refusal keeps its stable API code"
              Expect.equal (countBuilds freshOrg freshProjectId) 0 "fresh unsafe source creates no build"

              let malformedOrg, malformedProject = freshProject ()
              let malformedKey = "legacy-malformed-replay"
              let malformedBytes = [| 0xffuy; 0xfeuy |]
              let malformedInput: NewBuild =
                  { OrganizationId = malformedOrg
                    ProjectId = malformedProject
                    IdempotencyKey = malformedKey
                    PipelineSource = malformedBytes
                    StageNames = [ "legacy-stage" ]
                    RequiredTrustPool = "trusted-linux"
                    RequiredCapabilities = [ "linux" ] }

              let malformedSeeded =
                  match store.AdmitBuild malformedInput with
                  | Ok admission -> admission
                  | Error why -> failtestf "malformed legacy direct seed failed: %s" why

              let malformedUrl =
                  $"{baseUrl}/api/v1/organizations/{malformedOrg.Value}/projects/{malformedProject.Value}/builds"
              let malformedReplayCode, malformedReplayBody =
                  sendBytes malformedUrl malformedKey malformedBytes
              Expect.equal malformedReplayCode 200 "exact malformed legacy bytes replay before UTF-8 decoding"
              Expect.stringContains malformedReplayBody (string malformedSeeded.BuildId.Value) "malformed replay returns its durable build"
              Expect.stringContains malformedReplayBody "\"was_existing\":true" "malformed replay is idempotent"

              let malformedConflictCode, malformedConflictBody =
                  sendBytes malformedUrl malformedKey [| 0xffuy; 0xfduy |]
              Expect.equal malformedConflictCode 409 "changed malformed bytes conflict before UTF-8 decoding"
              Expect.stringContains malformedConflictBody "idempotency_conflict" "malformed conflict has the stable code"
              Expect.equal (countBuilds malformedOrg malformedProject) 1 "malformed conflict creates no build"
          }

          test "one idempotency key cannot substitute different Jenkinsfile bytes" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let firstCode, _ = send HttpMethod.Post url (Some token) (Some "api-source-bound") (Some pipeline)
              Expect.equal firstCode 201 "control admitted"
              let changed = pipeline.Replace("echo 'hi'", "echo 'different'")
              let conflictCode, conflict =
                  send HttpMethod.Post url (Some token) (Some "api-source-bound") (Some changed)
              Expect.equal conflictCode 409 "substitution conflicts"
              Expect.stringContains conflict "idempotency_conflict" "stable conflict code"

              let malformedCode, malformedBody =
                  send HttpMethod.Post url (Some token) (Some "api-source-bound") (Some "pipeline {")
              Expect.equal malformedCode 409 "a bound key conflicts before parser classification"
              Expect.stringContains malformedBody "idempotency_conflict" "malformed replacement has the stable conflict code"

              let emptyCode, emptyBody =
                  send HttpMethod.Post url (Some token) (Some "api-source-bound") (Some "")
              Expect.equal emptyCode 409 "a bound key conflicts before empty-source classification"
              Expect.stringContains emptyBody "idempotency_conflict" "empty replacement has the stable conflict code"

              let invalidUtf8Code, invalidUtf8Body =
                  sendBytes url "api-source-bound" [| 0xffuy; 0xfeuy |]
              Expect.equal invalidUtf8Code 409 "a bound key conflicts before UTF-8 decoding"
              Expect.stringContains invalidUtf8Body "idempotency_conflict" "invalid-byte replacement has the stable conflict code"

              let freshMalformedCode, freshMalformedBody =
                  send HttpMethod.Post url (Some token) (Some "fresh-malformed") (Some "pipeline {")
              Expect.equal freshMalformedCode 422 "a fresh malformed request still reaches the parser"
              Expect.stringContains freshMalformedBody "malformed_syntax" "fresh malformed request keeps its parser code"

              let freshEmptyCode, freshEmptyBody =
                  send HttpMethod.Post url (Some token) (Some "fresh-empty") (Some "")
              Expect.equal freshEmptyCode 422 "a fresh empty request still reaches the parser"
              Expect.stringContains freshEmptyBody "empty_source" "fresh empty request keeps its parser code"

              let freshInvalidUtf8Code, freshInvalidUtf8Body =
                  sendBytes url "fresh-invalid-utf8" [| 0xffuy; 0xfeuy |]
              Expect.equal freshInvalidUtf8Code 400 "fresh invalid bytes still reach strict UTF-8 decoding"
              Expect.stringContains freshInvalidUtf8Body "invalid_utf8" "fresh invalid bytes keep their decoding code"
          }

          test "concurrent mixed API submissions leave AdmitBuild as the one-key arbiter" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let key = "api-mixed-race"
              let sourceA = pipeline
              let sourceB = pipeline.Replace("echo 'hi'", "echo 'other'")
              use gate = new Threading.ManualResetEventSlim(false)

              let requests =
                  [| 0..7 |]
                  |> Array.map (fun index ->
                      Threading.Tasks.Task.Run(fun () ->
                          gate.Wait()
                          let source = if index % 2 = 0 then sourceA else sourceB
                          source, send HttpMethod.Post url (Some token) (Some key) (Some source)))

              gate.Set()
              requests
              |> Array.map (fun request -> request :> Threading.Tasks.Task)
              |> Threading.Tasks.Task.WaitAll

              let results = requests |> Array.map (fun request -> request.Result)
              let accepted =
                  results
                  |> Array.choose (fun (source, (code, body)) ->
                      if code = 200 || code = 201 then Some(source, code, body) else None)
              let conflicts = results |> Array.filter (fun (_, (code, _)) -> code = 409)

              Expect.equal accepted.Length 4 "all requests matching the winning bytes are admitted or replayed"
              Expect.equal conflicts.Length 4 "all requests carrying the losing bytes conflict"
              Expect.equal (accepted |> Array.filter (fun (_, code, _) -> code = 201) |> Array.length) 1 "one request creates"
              Expect.equal (accepted |> Array.filter (fun (_, code, _) -> code = 200) |> Array.length) 3 "three exact requests replay"
              Expect.equal (accepted |> Array.map (fun (source, _, _) -> source) |> Array.distinct |> Array.length) 1 "only one source wins"

              let buildIds =
                  accepted
                  |> Array.map (fun (_, _, body) ->
                      Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value)
                  |> Array.distinct
              Expect.equal buildIds.Length 1 "all successful responses return the same durable build"

              use connection = new Npgsql.NpgsqlConnection(connectionString)
              connection.Open()
              use count = connection.CreateCommand()
              count.CommandText <-
                  "SELECT count(*) FROM builds WHERE organization_id=@organization AND project_id=@project"
              count.Parameters.AddWithValue("organization", org.Value) |> ignore
              count.Parameters.AddWithValue("project", project.Value) |> ignore
              Expect.equal (Convert.ToInt32(count.ExecuteScalar())) 1 "the mixed race creates exactly one build"
          }

          test "request placement override and oversized source both fail before admission" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              use request = new HttpRequestMessage(HttpMethod.Post, url)
              request.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}") |> ignore
              request.Headers.TryAddWithoutValidation("idempotency-key", "placement-denied") |> ignore
              request.Headers.TryAddWithoutValidation("fogell-trust-pool", "privileged") |> ignore
              request.Content <- new StringContent(pipeline, Encoding.UTF8, "application/x-jenkinsfile")
              use response = client.Send request
              let denied = response.Content.ReadAsStringAsync().Result
              Expect.equal (int response.StatusCode) 400 "placement header denied"
              Expect.stringContains denied "placement_override_forbidden" "stable placement code"

              let oversized = String('x', maxPipelineBytes + 1)
              let tooLargeCode, tooLarge =
                  send HttpMethod.Post url (Some token) (Some "oversized") (Some oversized)
              Expect.equal tooLargeCode 413 "raw byte limit enforced"
              Expect.stringContains tooLarge "pipeline_too_large" "stable size code"
          }

          test "chunked bodies retain the exact source limit and classify the sentinel byte as 413" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let padding = String(' ', maxPipelineBytes - Encoding.UTF8.GetByteCount pipeline)
              let exact = Encoding.UTF8.GetBytes(pipeline + padding)

              Expect.equal exact.Length maxPipelineBytes "fixture reaches the byte boundary exactly"
              let exactCode, exactBody = sendChunked url "chunked-exact" exact
              Expect.equal
                  exactCode
                  201
                  $"an unknown-length body at the public limit is admitted; response={exactBody}"
              Expect.stringContains exactBody "\"was_existing\":false" "exact-limit source reached admission"

              let overflow = Array.append exact [| byte ' ' |]
              let overflowCode, overflowBody = sendChunked url "chunked-overflow" overflow
              Expect.equal
                  overflowCode
                  413
                  $"the one-byte sentinel is classified by the router, not faulted by Kestrel; response={overflowBody}"
              Expect.stringContains overflowBody "pipeline_too_large" "chunked overflow keeps the stable error code"
          }

          test "a missing idempotency key is refused with a named code" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let code, body = send HttpMethod.Post url (Some token) None (Some pipeline)
              Expect.equal code 400 "refused"
              use payload = JsonDocument.Parse body
              let root = payload.RootElement
              Expect.equal (root.GetProperty("code").GetString()) "idempotency_key_required" "named code"

              Expect.equal
                  (root.GetProperty("message").GetString())
                  "an Idempotency-Key header is required so a retry cannot create a second build"
                  "ordinary API error text remains unchanged"

              Expect.equal (root.GetProperty("position").ValueKind) JsonValueKind.Null "ordinary error has no position"
          }

          test "a malformed pipeline renders its exact excerpt" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let source = "  node {\r\n\tsh 'make'\r\n}"
              let code, body = send HttpMethod.Post url (Some token) (Some "bad-1") (Some source)
              Expect.equal code 422 "unprocessable"
              use payload = JsonDocument.Parse body
              let root = payload.RootElement
              Expect.equal (root.GetProperty("code").GetString()) "no_pipeline_block" "named code"
              Expect.equal (root.GetProperty("position").GetString()) "1:1" "typed position remains separate"

              Expect.equal
                  (root.GetProperty("message").GetString())
                  "no_pipeline_block at 1:1: no declarative `pipeline { }` block found\n  node {\n^"
                  "the public admission response carries the exact bounded diagnostic"

              Expect.equal
                  (root.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq)
                  (set [ "code"; "message"; "position" ])
                  "the established error-response wire shape is unchanged"
          }

          test "status reflects the admitted build" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let _, body = send HttpMethod.Post url (Some token) (Some "st-1") (Some pipeline)
              let buildId = Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value

              let code, s =
                  send HttpMethod.Get $"{url}/{buildId}" (Some token) None None

              Expect.equal code 200 "found"
              Expect.stringContains s "\"status\":\"queued\"" "queued"
              Expect.stringContains s "\"cancellation_requested\":false" "not cancelled"
          }

          test "status, logs and cancellation are bound to the route project" {
              let org, project = freshProject ()
              let wrongProject = ProjectId(Guid.NewGuid())
              store.CreateProject(org, $"org-{org.Value}", wrongProject, "wrong")
              let ownerUrl = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let wrongUrl = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{wrongProject.Value}/builds"
              let _, body = send HttpMethod.Post ownerUrl (Some token) (Some "bound-api") (Some pipeline)
              let buildId = Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
              let attemptId = Text.RegularExpressions.Regex.Match(body, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value

              Expect.isTrue
                  (store.AppendLog(org, BuildId(Guid.Parse buildId), AttemptId(Guid.Parse attemptId), 3, "owned"))
                  "control log append is non-vacuous"

              let wrongRoutes =
                  [ HttpMethod.Get, $"{wrongUrl}/{buildId}"
                    HttpMethod.Get, $"{wrongUrl}/{buildId}/logs"
                    HttpMethod.Post, $"{wrongUrl}/{buildId}/cancel" ]

              for method, route in wrongRoutes do
                  let code, response = send method route (Some token) None None
                  Expect.equal code 404 $"{method} rejects a valid but wrong project"
                  Expect.stringContains response "not_found" "wrong lineage is not disclosed"

              let statusCode, statusBody = send HttpMethod.Get $"{ownerUrl}/{buildId}" (Some token) None None
              Expect.equal statusCode 200 "correct status route remains valid"
              Expect.stringContains statusBody "\"cancellation_requested\":false" "wrong cancel had no side effect"

              let logCode, logBody = send HttpMethod.Get $"{ownerUrl}/{buildId}/logs?from=3" (Some token) None None
              Expect.equal logCode 200 "correct logs route remains valid"
              Expect.stringContains logBody "owned" "correct project sees its log"
              Expect.stringContains logBody "\"next_sequence\":4" "cursor is computed from the qualified read"
          }

          test "empty and unknown logs remain distinguishable" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let _, body = send HttpMethod.Post url (Some token) (Some "empty-log-api") (Some pipeline)
              let buildId = Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value

              let emptyCode, emptyBody = send HttpMethod.Get $"{url}/{buildId}/logs?from=7" (Some token) None None
              Expect.equal emptyCode 200 "a legitimate empty log is successful"
              Expect.stringContains emptyBody "\"chunks\":[]" "empty chunks are explicit"
              Expect.stringContains emptyBody "\"next_sequence\":7" "empty log retains the requested cursor"

              let missingCode, missingBody =
                  send HttpMethod.Get $"{url}/{Guid.NewGuid()}/logs" (Some token) None None

              Expect.equal missingCode 404 "an unknown build is not an empty log"
              Expect.stringContains missingBody "not_found" "unknown build is named"
          }

          test "a malformed log cursor is refused instead of silently reading from zero" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let _, body = send HttpMethod.Post url (Some token) (Some "bad-cursor-api") (Some pipeline)
              let buildId = Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
              let code, response = send HttpMethod.Get $"{url}/{buildId}/logs?from=banana" (Some token) None None
              Expect.equal code 400 "invalid cursor"
              Expect.stringContains response "invalid_log_cursor" "stable cursor code"
          }

          test "archived binary bytes are retrievable at a stable authenticated attempt URL" {
              let org, project = freshProject ()
              let buildsUrl = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let _, admission =
                  send HttpMethod.Post buildsUrl (Some token) (Some "artifact-api") (Some pipeline)
              let build =
                  Text.RegularExpressions.Regex.Match(admission, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
              let attempt =
                  Text.RegularExpressions.Regex.Match(admission, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
                  |> AttemptId
              let buildKey = build.ToString "N"
              let workspace = IO.Path.Combine(stateRoot, "artifact-source", buildKey)
              let relative = "dist/result #1.bin"
              let source = IO.Path.Combine(workspace, relative)
              let payload = [| 0uy; 255uy; byte 'A'; 13uy; 10uy; byte 'B' |]
              IO.Directory.CreateDirectory(IO.Path.GetDirectoryName source) |> ignore
              IO.File.WriteAllBytes(source, payload)

              let artifactRoot =
                  IO.Path.Combine(
                      stateRoot,
                      "workspaces",
                      org.Value.ToString "N",
                      "_artifacts")

              Expect.sequenceEqual
                  (Publish.archive (ArtifactStore.under artifactRoot) buildKey workspace [ relative ])
                  [ relative ]
                  "the production archiver publishes the requested relative path"

              let workspaceRoot = IO.Path.Combine(stateRoot, "workspaces", org.Value.ToString "N")
              let snapshot =
                  IO.Path.Combine(workspaceRoot, "_artifact-snapshots", attempt.Value.ToString "N")

              let encodedName = Uri.EscapeDataString "result #1.bin"
              let artifactUrl = $"{buildsUrl}/{build}/attempts/{attempt.Value}/artifacts/dist/{encodedName}"

              let pendingCode, pendingBody, _, _, _, _ = getArtifact artifactUrl (Some token)
              Expect.equal pendingCode 409 "mutable output is unavailable before terminal publication"
              Expect.isFalse (pendingBody = payload) "a queued build cannot disclose archived bytes"
              Expect.stringContains
                  (Text.Encoding.UTF8.GetString pendingBody)
                  "artifact_not_ready"
                  "the nonterminal refusal has a stable code"

              let owner = "artifact-api-owner"
              let fence =
                  match store.OfferAttempt(org, attempt, owner, 60) with
                  | Ok value -> value
                  | Error error -> failtestf "offer failed: %s" error
              Expect.isTrue (store.AcceptAttempt(org, attempt, fence, owner)) "artifact attempt accepted"
              match store.PublishTerminal(org, attempt, fence, owner, Success) with
              | Ok () -> ()
              | Error error -> failtestf "terminal publication failed: %s" error

              let retryAttempt = AttemptId(Guid.NewGuid())

              for retrievalNumber in 1..2 do
                  let code, downloaded, contentType, contentLength, disposition, noSniff =
                      getArtifact artifactUrl (Some token)
                  Expect.equal code 200 $"stable retrieval {retrievalNumber} succeeds"
                  Expect.sequenceEqual downloaded payload $"retrieval {retrievalNumber} is byte-exact"
                  Expect.equal contentType (Some "application/octet-stream") "binary content is never interpreted"
                  Expect.equal contentLength (Some(int64 payload.Length)) "the transport declares the exact byte count"
                  Expect.isTrue
                      (disposition |> Option.exists (fun value -> value.Contains "result%20%231.bin"))
                      "the attachment name is safely URL-encoded"
                  Expect.contains noSniff "nosniff" "clients are told not to infer an active content type"

                  if retrievalNumber = 1 then
                      Expect.isTrue
                          (IO.Directory.Exists snapshot)
                          "the first terminal read adopts the legacy build-keyed artifact directory"
                      Expect.isFalse
                          (IO.Directory.Exists(IO.Path.Combine(artifactRoot, buildKey)))
                          "legacy mutable staging no longer aliases the published attempt"
                      Expect.equal
                          (ArtifactSnapshots.finalize stateRoot org.Value build attempt.Value)
                          (Ok snapshot)
                          "snapshot publication replays after a crash between rename and terminal truth"

                      let retryStaging = IO.Path.Combine(artifactRoot, buildKey)
                      IO.Directory.CreateDirectory retryStaging |> ignore
                      let ordinaryMissingCode, ordinaryMissingBody =
                          send HttpMethod.Get
                              $"{buildsUrl}/{build}/attempts/{attempt.Value}/artifacts/not-archived.bin"
                              (Some token) None None
                      Expect.equal ordinaryMissingCode 404 "a missing file in an existing snapshot stays a normal 404"
                      Expect.stringContains ordinaryMissingBody "artifact_not_found" "the ordinary miss is named"
                      Expect.isTrue
                          (IO.Directory.Exists retryStaging)
                          "an ordinary miss does not invoke legacy migration or collide with retry staging"

                      match store.DecideRetry(org, attempt, 2, retryAttempt) with
                      | Ok _ -> ()
                      | Error error -> failtestf "retry decision failed: %A" error

                      let retryPayload = [| 6uy; 5uy; 4uy; 3uy; 2uy; 1uy |]
                      IO.File.WriteAllBytes(source, retryPayload)
                      Expect.sequenceEqual
                          (Publish.archive (ArtifactStore.under artifactRoot) buildKey workspace [ relative ])
                          [ relative ]
                          "the retry can overwrite only its mutable build-key staging path"

              let retryCode, retryBody, _, _, _, _ =
                  getArtifact
                      $"{buildsUrl}/{build}/attempts/{retryAttempt.Value}/artifacts/dist/{encodedName}"
                      (Some token)
              Expect.equal retryCode 409 "the queued retry has no published artifact snapshot"
              Expect.isFalse (retryBody = payload) "the queued retry cannot borrow its parent's bytes"

              let unauthenticated, leaked, _, _, _, _ = getArtifact artifactUrl None
              Expect.equal unauthenticated 401 "artifact retrieval is authenticated"
              Expect.isFalse (leaked = payload) "an unauthenticated response contains no artifact bytes"

              let wrongProject = ProjectId(Guid.NewGuid())
              store.CreateProject(org, $"org-{org.Value}", wrongProject, "artifact-wrong-project")
              let wrongCode, wrongBody =
                  send HttpMethod.Get
                      $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{wrongProject.Value}/builds/{build}/attempts/{attempt.Value}/artifacts/dist/{encodedName}"
                      (Some token) None None
              Expect.equal wrongCode 404 "a physical artifact cannot bypass project ownership"
              Expect.stringContains wrongBody "not_found" "the ownership refusal reveals no artifact path"

              let missingCode, missingBody =
                  send HttpMethod.Get
                      $"{buildsUrl}/{build}/attempts/{attempt.Value}/artifacts/dist/missing.bin"
                      (Some token) None None
              Expect.equal missingCode 404 "an absent artifact is not an empty success"
              Expect.stringContains missingBody "artifact_not_found" "the missing artifact has a stable code"

              let outside = IO.Path.Combine(stateRoot, $"outside-{buildKey}.bin")
              IO.File.WriteAllBytes(outside, [| 9uy; 8uy; 7uy |])
              let escape =
                  IO.Path.Combine(
                      stateRoot,
                      "workspaces",
                      org.Value.ToString "N",
                      "_artifact-snapshots",
                      attempt.Value.ToString "N",
                      "dist",
                      "escape.bin")
              IO.File.CreateSymbolicLink(escape, outside) |> ignore
              let escapeCode, escapeBody =
                  send HttpMethod.Get
                      $"{buildsUrl}/{build}/attempts/{attempt.Value}/artifacts/dist/escape.bin"
                      (Some token) None None
              Expect.equal escapeCode 404 "a final symlink is not downloadable"
              Expect.stringContains escapeBody "artifact_not_found" "the symlink target is not disclosed"

              let savedSnapshot = snapshot + ".saved"
              IO.Directory.Move(snapshot, savedSnapshot)
              let outsideSnapshot = IO.Path.Combine(stateRoot, $"outside-snapshot-{buildKey}")
              let outsideArtifact = IO.Path.Combine(outsideSnapshot, relative)
              IO.Directory.CreateDirectory(IO.Path.GetDirectoryName outsideArtifact) |> ignore
              IO.File.WriteAllBytes(outsideArtifact, payload)
              IO.Directory.CreateSymbolicLink(snapshot, outsideSnapshot) |> ignore
              let rootEscapeCode, rootEscapeBytes, _, _, _, _ = getArtifact artifactUrl (Some token)
              Expect.equal rootEscapeCode 404 "a symlinked attempt snapshot root is not downloadable"
              Expect.isFalse (rootEscapeBytes = payload) "a replaced snapshot root cannot disclose outside bytes"

              // The upgrade adoption callback runs while the terminal attempt
              // is locked. A retry must not reopen the build-key staging path
              // until that callback has frozen the parent's snapshot.
              let lockOrg, lockProject = freshProject ()
              let lockBuildsUrl =
                  $"{baseUrl}/api/v1/organizations/{lockOrg.Value}/projects/{lockProject.Value}/builds"
              let _, lockAdmission =
                  send HttpMethod.Post lockBuildsUrl (Some token) (Some "artifact-lock") (Some pipeline)
              let lockBuild =
                  Text.RegularExpressions.Regex.Match(lockAdmission, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
                  |> BuildId
              let lockAttempt =
                  Text.RegularExpressions.Regex.Match(lockAdmission, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
                  |> AttemptId
              let lockOwner = "artifact-lock-owner"
              let lockFence =
                  match store.OfferAttempt(lockOrg, lockAttempt, lockOwner, 60) with
                  | Ok value -> value
                  | Error error -> failtestf "offer failed: %s" error
              Expect.isTrue
                  (store.AcceptAttempt(lockOrg, lockAttempt, lockFence, lockOwner))
                  "migration-race attempt accepted"
              match store.PublishTerminal(lockOrg, lockAttempt, lockFence, lockOwner, Success) with
              | Ok () -> ()
              | Error error -> failtestf "terminal publication failed: %s" error

              use migrationEntered = new Threading.ManualResetEventSlim(false)
              use releaseMigration = new Threading.ManualResetEventSlim(false)
              let migration =
                  Threading.Tasks.Task.Run(fun () ->
                      store.MigrateLegacyArtifactSnapshot(
                          lockOrg,
                          lockProject,
                          lockBuild,
                          lockAttempt,
                          fun () ->
                              migrationEntered.Set()
                              releaseMigration.Wait()
                              ArtifactSnapshots.finalize
                                  stateRoot
                                  lockOrg.Value
                                  lockBuild.Value
                                  lockAttempt.Value
                              |> Result.map ignore))
              Expect.isTrue
                  (migrationEntered.Wait(TimeSpan.FromSeconds 5.0))
                  "the migration callback enters while holding the lineage locks"
              let lockRetryAttempt = AttemptId(Guid.NewGuid())
              let competingRetry =
                  Threading.Tasks.Task.Run(fun () ->
                      store.DecideRetry(lockOrg, lockAttempt, 2, lockRetryAttempt))
              let retryFinishedWhileMigrationHeld = competingRetry.Wait(TimeSpan.FromMilliseconds 200.0)
              releaseMigration.Set()
              Expect.isFalse
                  retryFinishedWhileMigrationHeld
                  "DecideRetry waits until legacy snapshot adoption commits"
              match migration.Result with
              | Ok true -> ()
              | result -> failtestf "legacy migration failed: %A" result
              match competingRetry.Result with
              | Ok _ -> ()
              | Error error -> failtestf "retry decision failed after migration: %A" error

              let concurrentOrg = Guid.NewGuid()
              let concurrentBuild = Guid.NewGuid()
              let concurrentAttempt = Guid.NewGuid()
              let concurrentStaging =
                  IO.Path.Combine(
                      stateRoot,
                      "workspaces",
                      concurrentOrg.ToString "N",
                      "_artifacts",
                      concurrentBuild.ToString "N")
              IO.Directory.CreateDirectory concurrentStaging |> ignore
              IO.File.WriteAllText(IO.Path.Combine(concurrentStaging, "race.txt"), "one immutable winner")
              use startFinalizers = new Threading.ManualResetEventSlim(false)
              let concurrentFinalizers =
                  [| for _ in 1..16 ->
                         Threading.Tasks.Task.Run(fun () ->
                             startFinalizers.Wait()
                             ArtifactSnapshots.finalize
                                 stateRoot
                                 concurrentOrg
                                 concurrentBuild
                                 concurrentAttempt) |]
              startFinalizers.Set()
              Threading.Tasks.Task.WaitAll(
                  concurrentFinalizers
                  |> Array.map (fun finalizer -> finalizer :> Threading.Tasks.Task))
              let concurrentTarget =
                  IO.Path.Combine(
                      stateRoot,
                      "workspaces",
                      concurrentOrg.ToString "N",
                      "_artifact-snapshots",
                      concurrentAttempt.ToString "N")
              for finalizer in concurrentFinalizers do
                  Expect.equal
                      finalizer.Result
                      (Ok concurrentTarget)
                      "concurrent snapshot publication is idempotent after the atomic move"

              let snapshotParent = IO.Path.GetDirectoryName snapshot
              let savedSnapshotParent = snapshotParent + ".saved"
              IO.Directory.Move(snapshotParent, savedSnapshotParent)
              let outsideSnapshotParent = IO.Path.Combine(stateRoot, $"outside-snapshot-parent-{buildKey}")
              let outsideAttemptArtifact =
                  IO.Path.Combine(outsideSnapshotParent, attempt.Value.ToString "N", relative)
              IO.Directory.CreateDirectory(IO.Path.GetDirectoryName outsideAttemptArtifact) |> ignore
              IO.File.WriteAllBytes(outsideAttemptArtifact, payload)
              IO.Directory.CreateSymbolicLink(snapshotParent, outsideSnapshotParent) |> ignore
              let parentEscapeCode, parentEscapeBytes, _, _, _, _ = getArtifact artifactUrl (Some token)
              Expect.equal parentEscapeCode 404 "a symlinked snapshot parent is not downloadable"
              Expect.isFalse
                  (parentEscapeBytes = payload)
                  "a snapshot-parent replacement cannot disclose outside bytes"
          }

          test "artifact path validation rejects traversal and ambiguous segments" {
              for invalid in [ ""; " "; "/absolute"; "../secret"; "a/../secret"; "a/./b"; "a//b"; "a\u0000b" ] do
                  let shown = invalid.Replace("\u0000", "<NUL>")
                  Expect.isNone
                      (Router.artifactPathSegments invalid)
                      $"invalid artifact path is refused: {shown}"

              Expect.equal
                  (Router.artifactPathSegments "reports/linux/output.bin")
                  (Some [| "reports"; "linux"; "output.bin" |])
                  "a nested relative artifact path remains admissible"
          }

          test "an unknown build is 404, not 500" {
              let org, project = freshProject ()
              let code, body =
                  send HttpMethod.Get
                      $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds/{Guid.NewGuid()}"
                      (Some token) None None

              Expect.equal code 404 "not found"
              Expect.stringContains body "not_found" "named"
          }

          test "a non-UUID identifier is 400, not 500" {
              let code, body =
                  send HttpMethod.Get
                      $"{baseUrl}/api/v1/organizations/not-a-uuid/projects/also-not/builds/{Guid.NewGuid()}"
                      (Some token) None None

              Expect.equal code 400 "bad request"
              Expect.stringContains body "malformed_identifier" "named"
          }

          test "a malformed project is 400 on status, logs, artifacts and cancellation" {
              let org = Guid.NewGuid()
              let build = Guid.NewGuid()
              let root = $"{baseUrl}/api/v1/organizations/{org}/projects/not-a-project/builds/{build}"

              for method, route in
                  [ HttpMethod.Get, root
                    HttpMethod.Get, $"{root}/logs"
                    HttpMethod.Get, $"{root}/attempts/{Guid.NewGuid()}/artifacts/output.bin"
                    HttpMethod.Post, $"{root}/cancel" ] do
                  let code, body = send method route (Some token) None None
                  Expect.equal code 400 $"{method} rejects a malformed project"
                  Expect.stringContains body "malformed_identifier" "named"
          }

          test "logs are readable from an offset and report next_sequence" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let _, body = send HttpMethod.Post url (Some token) (Some "log-api") (Some pipeline)
              let buildId = Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
              let attemptId = Text.RegularExpressions.Regex.Match(body, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value

              for i in 0..2 do
                  store.AppendLog(
                      org, BuildId(Guid.Parse buildId), AttemptId(Guid.Parse attemptId), i, $"line-{i}")
                  |> ignore

              let code, all = send HttpMethod.Get $"{url}/{buildId}/logs" (Some token) None None
              Expect.equal code 200 "ok"
              Expect.stringContains all "line-0" "from the start"
              Expect.stringContains all "\"next_sequence\":3" "tail cursor"

              let _, tail = send HttpMethod.Get $"{url}/{buildId}/logs?from=2" (Some token) None None
              Expect.stringContains tail "line-2" "the tail"
              Expect.isFalse (tail.Contains "line-0") "offset respected"
          }

          test "one-chunk pages cross retry attempts without duplicate or skipped local sequence ties" {
              let smallApp, smallBaseUrl = startServer 1

              try
                  let org, project = freshProject ()
                  let url = $"{smallBaseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
                  let _, body = send HttpMethod.Post url (Some token) (Some "log-retry-api") (Some pipeline)
                  use admission = JsonDocument.Parse body
                  let buildId = BuildId(Guid.Parse(admission.RootElement.GetProperty("build_id").GetString()))
                  let parent = AttemptId(Guid.Parse(admission.RootElement.GetProperty("attempt_id").GetString()))

                  Expect.isTrue (store.AppendLog(org, buildId, parent, 0, "parent-zero")) "parent log appended"

                  let owner = "log-retry-api-owner"
                  let fence =
                      match store.OfferAttempt(org, parent, owner, 60) with
                      | Ok fence -> fence
                      | Error error -> failtestf "offer failed: %s" error

                  Expect.isTrue (store.AcceptAttempt(org, parent, fence, owner)) "parent accepted"

                  match store.PublishTerminal(org, parent, fence, owner, Failure) with
                  | Error error -> failtestf "terminal publication failed: %s" error
                  | Ok () -> ()

                  let child = AttemptId(Guid.NewGuid())

                  match store.DecideRetry(org, parent, 2, child) with
                  | Error error -> failtestf "retry decision failed: %A" error
                  | Ok _ -> ()

                  let stateRoot = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-worker-path-proof")
                  let parentJournal = WorkerPaths.journalPath stateRoot org parent
                  let childJournal = WorkerPaths.journalPath stateRoot org child
                  let restartedChildJournal = WorkerPaths.journalPath stateRoot org child
                  let legacyBuildJournal =
                      IO.Path.Combine(
                          stateRoot,
                          "journals",
                          org.Value.ToString "N",
                          buildId.Value.ToString "N" + ".journal")

                  Expect.notEqual childJournal parentJournal "retry child cannot reopen its failed parent's journal"
                  Expect.equal restartedChildJournal childJournal "the same child resumes its own deterministic journal"
                  Expect.notEqual childJournal legacyBuildJournal "attempt namespace cannot ambiguously adopt a legacy build journal"

                  Expect.isTrue (store.AppendLog(org, buildId, child, 0, "child-zero")) "retry log appended"

                  let page from =
                      let code, response = send HttpMethod.Get $"{url}/{buildId.Value}/logs?from={from}" (Some token) None None
                      Expect.equal code 200 $"page {from} is readable"
                      JsonDocument.Parse response

                  use first = page 0
                  Expect.equal (first.RootElement.GetProperty("next_sequence").GetInt32()) 1 "first cursor advances once"
                  Expect.equal (first.RootElement.GetProperty("chunks").GetArrayLength()) 1 "limit is exact"
                  Expect.equal (first.RootElement.GetProperty("chunks").[0].GetProperty("body").GetString()) "parent-zero" "first attempt appears once"

                  use second = page 1
                  Expect.equal (second.RootElement.GetProperty("next_sequence").GetInt32()) 2 "retry cursor advances once"
                  Expect.equal (second.RootElement.GetProperty("chunks").GetArrayLength()) 1 "limit remains exact"
                  Expect.equal (second.RootElement.GetProperty("chunks").[0].GetProperty("body").GetString()) "child-zero" "retry appears once"

                  use exhausted = page 2
                  Expect.equal (exhausted.RootElement.GetProperty("next_sequence").GetInt32()) 2 "empty page retains cursor"
                  Expect.equal (exhausted.RootElement.GetProperty("chunks").GetArrayLength()) 0 "both chunks were consumed exactly once"
              finally
                  smallApp.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
          }

          // Corrected expectation: my first version asserted 409 on a repeat
          // cancel. That is wrong — a retried cancellation after a client timeout
          // must not look like an error, since the requested state is already the
          // actual state. A real conflict is cancelling a FINISHED build.
          test "cancel is idempotent; cancelling a finished build conflicts" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let _, body = send HttpMethod.Post url (Some token) (Some "can-1") (Some pipeline)
              let buildId = Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
              let attemptId = Text.RegularExpressions.Regex.Match(body, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value

              let code1, b1 = send HttpMethod.Post $"{url}/{buildId}/cancel" (Some token) None None
              Expect.equal code1 202 "accepted"
              Expect.stringContains b1 "\"already_requested\":false" "first request"

              let code2, b2 = send HttpMethod.Post $"{url}/{buildId}/cancel" (Some token) None None
              Expect.equal code2 202 "a retry is idempotent, not an error"
              Expect.stringContains b2 "\"already_requested\":true" "reports that it was already requested"

              let fence =
                  match store.OfferAttempt(org, AttemptId(Guid.Parse attemptId), "api-terminal", 60) with
                  | Ok value -> value
                  | Error error -> failtestf "offer failed: %s" error
              Expect.isTrue
                  (store.AcceptAttempt(org, AttemptId(Guid.Parse attemptId), fence, "api-terminal"))
                  "accepted"
              match store.PublishTerminal(org, AttemptId(Guid.Parse attemptId), fence, "api-terminal", Success) with
              | Ok _ -> ()
              | Error error -> failtestf "terminal roll-up failed: %s" error

              let terminalCode, terminalBody =
                  send HttpMethod.Post $"{url}/{buildId}/cancel" (Some token) None None

              Expect.equal terminalCode 409 "a terminal build conflicts"
              Expect.stringContains terminalBody "already_terminal" "terminal outcome is named"

              let missingCode, missingBody =
                  send HttpMethod.Post $"{url}/{Guid.NewGuid()}/cancel" (Some token) None None

              Expect.equal missingCode 404 "an unknown build is not found"
              Expect.stringContains missingBody "not_found" "named"
          }

          test "explain names missing capabilities rather than saying 'waiting'" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              send HttpMethod.Post url (Some token) (Some "exp-1") (Some pipeline) |> ignore

              let explainUrl = $"{baseUrl}/api/v1/organizations/{org.Value}/scheduler/explain"

              let _, matching = send HttpMethod.Get $"{explainUrl}?capability=linux" (Some token) None None
              Expect.stringContains matching "claimable" "work is claimable"

              let _, mismatch = send HttpMethod.Get explainUrl (Some token) None None
              Expect.stringContains mismatch "linux" "names what is missing"
          }

          test "teardown" {
              app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
              if IO.Directory.Exists stateRoot then IO.Directory.Delete(stateRoot, true)
          }
        ]

[<EntryPoint>]
let main argv =
    if not available then
        eprintfn "skipped: no PostgreSQL at %s" connectionString
        0
    else
        match store.Migrate() with
        | Error e ->
            eprintfn $"migrate failed: {e}"
            1
        | Ok _ ->
            runTestsWithCLIArgs
                []
                argv
                (testSequenced
                    (testList
                        "Fogell.Controller.Api"
                        [ stateRootReadiness
                          databaseStartupBoundary
                          executionLauncherValidation
                          authorization
                          endpoints ]))
