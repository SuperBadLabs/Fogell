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

/// FG-026b round-4 probes plant hard links where the connector reads
/// (mkfifo is declared above for FG-251).
[<DllImport("libc", SetLastError = true)>]
extern int private link(string existing, string newPath)

[<DllImport("libc", SetLastError = true)>]
extern int private mkfifo(string path, uint32 mode)

[<DllImport("libc", SetLastError = true)>]
extern int private fcntl(int descriptor, int command)

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

let private hostFootprint =
    let trimmed (path: string) = IO.Path.TrimEndingDirectorySeparator path

    testList
        "FG-232 host content root and configuration watch"
        [ test "the host roots at the apphost directory and reads configuration once" {
              let options = Fogell.Controller.Host.Program.hostOptions ()

              Expect.equal
                  (trimmed options.ContentRootPath)
                  (trimmed AppContext.BaseDirectory)
                  "the content root is the apphost directory, not the current working directory"

              Expect.sequenceEqual
                  options.Args
                  [| Fogell.Controller.Host.Program.reloadConfigOnChangeSwitch |]
                  "the only host argument turns configuration reload-on-change off"

              // The options must survive the host's own resolution: neither the
              // cwd nor an ambient content-root variable may displace them.
              let ambientRoot =
                  IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-fg232-ambient-{Guid.NewGuid():N}")

              IO.Directory.CreateDirectory ambientRoot |> ignore
              let previous = Environment.GetEnvironmentVariable "ASPNETCORE_CONTENTROOT"

              try
                  Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", ambientRoot)
                  let builder = WebApplication.CreateBuilder options
                  use app = builder.Build()

                  Expect.equal
                      (trimmed app.Environment.ContentRootPath)
                      (trimmed AppContext.BaseDirectory)
                      "the built host reports the apphost directory as its content root"

                  Expect.equal
                      app.Configuration["hostBuilder:reloadConfigOnChange"]
                      "false"
                      "the built host carries the reload-on-change switch in its configuration"
              finally
                  Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", previous)
                  IO.Directory.Delete(ambientRoot, true)
          } ]

let private controllerConfigurationVariables =
    [ "FOGELL_DATABASE_URL"; "FOGELL_MAINTENANCE_DATABASE_URL"
      "FOGELL_API_TOKEN_FILE"; "FOGELL_LISTEN_URL"; "FOGELL_STATE_ROOT"
      "FOGELL_RUN_HOST_PATH"; "FOGELL_LOCAL_TRUST_POOL"
      "FOGELL_MAX_PIPELINE_BYTES"; "FOGELL_MAX_LOG_CHUNKS"
      "FOGELL_WORKER_POLL_MS"; "FOGELL_WORKER_LEASE_SECONDS" ]

let private withControllerConfiguration f =
    let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-setsid-validation-" + Guid.NewGuid().ToString("N"))
    IO.Directory.CreateDirectory root |> ignore
    let tokenFile = IO.Path.Combine(root, "token")
    let runHost = IO.Path.Combine(root, "run-host")
    IO.File.WriteAllText(tokenFile, String.replicate 32 "t")
    IO.File.SetUnixFileMode(tokenFile, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite)
    IO.File.WriteAllText(runHost, "#!/bin/sh\nexit 0\n")
    IO.File.SetUnixFileMode(runHost, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserExecute)
    let previous = controllerConfigurationVariables |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)
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

    try f root tokenFile runHost
    finally
        previous |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))
        IO.Directory.Delete(root, true)

let private executionLauncherValidation =

    testList
        "FG-224 trusted setsid launcher"
        [ test "the exact trusted launcher is shared into worker configuration" {
              withControllerConfiguration (fun _ _ _ ->
                  match ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher with
                  | Error error -> failtestf "trusted host launcher was refused: %s" error
                  | Ok config ->
                      Expect.equal config.SetsidPath "/usr/bin/setsid" "worker consumes the validated exact identity"
                      Expect.isTrue (ControllerConfig.executionLaunchersReady config) "both configured launchers are executable")
          }

          test "a whitespace-only local trust pool refuses startup" {
              withControllerConfiguration (fun _ _ _ ->
                  Environment.SetEnvironmentVariable("FOGELL_LOCAL_TRUST_POOL", " \t ")

                  Expect.equal
                      (ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher)
                      (Error "FOGELL_LOCAL_TRUST_POOL is required")
                      "startup fails before every admission can be rejected by the Store")
          }

          test "a missing trusted launcher refuses startup" {
              withControllerConfiguration (fun root _ runHost ->
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
              withControllerConfiguration (fun root _ runHost ->
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

let private tokenFileIntegrity =
    let withRoot f =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-fg251-token-{Guid.NewGuid():N}")
        IO.Directory.CreateDirectory root |> ignore

        try f root
        finally IO.Directory.Delete(root, true)

    let writeToken (root: string) (name: string) (mode: IO.UnixFileMode) (content: string) =
        let path = IO.Path.Combine(root, name)
        IO.File.WriteAllText(path, content)
        IO.File.SetUnixFileMode(path, mode)
        path

    let userRead = IO.UnixFileMode.UserRead
    let userWrite = IO.UnixFileMode.UserWrite

    testList
        "FG-251 descriptor-bound API token file"
        [ testCase "0400 and 0600 service-owned regular files are accepted" <| fun _ ->
              withRoot (fun root ->
                  for name, mode in [ "read-only", userRead; "owner-writable", userRead ||| userWrite ] do
                      let expected = String.replicate 32 name
                      let path = writeToken root name mode expected
                      Expect.equal
                          (ControllerConfig.readTokenFileSecurely path)
                          (Ok expected)
                          $"{name} is a secure deployment mode")

          testCase "the production configuration path enforces metadata and consumes descriptor bytes" <| fun _ ->
              withControllerConfiguration (fun _ tokenFile _ ->
                  IO.File.SetUnixFileMode(tokenFile, userRead ||| userWrite ||| IO.UnixFileMode.OtherRead)

                  Expect.equal
                      (ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher)
                      (Error "FOGELL_API_TOKEN_FILE mode must be 0400 or 0600")
                      "production startup selects the descriptor-bound reader")

              withControllerConfiguration (fun root tokenFile _ ->
                  let original = String.replicate 32 "o"
                  let replacement = String.replicate 32 "r"
                  IO.File.WriteAllText(tokenFile, original)
                  IO.File.SetUnixFileMode(tokenFile, userRead ||| userWrite)
                  let moved = IO.Path.Combine(root, "loader-opened-inode")

                  let swappingReader path =
                      ControllerConfig.readTokenFileSecurelyWith
                          (fun _ ->
                              IO.File.Move(path, moved)
                              writeToken root "token" (userRead ||| userWrite) replacement |> ignore)
                          ignore
                          path

                  match
                      ControllerConfig.loadWithSetsidLauncherAndTokenReader
                          swappingReader
                          ControllerConfig.trustedSetsidLauncher
                  with
                  | Error error -> failtestf "loader refused descriptor-bound bytes: %s" error
                  | Ok config ->
                      Expect.equal config.ApiToken original "the loader consumes its reader result without reopening the path")

          testCase "permissive modes are refused" <| fun _ ->
              withRoot (fun root ->
                  for name, forbiddenBit in
                      [ "user-executable", IO.UnixFileMode.UserExecute
                        "group-readable", IO.UnixFileMode.GroupRead
                        "group-writable", IO.UnixFileMode.GroupWrite
                        "group-executable", IO.UnixFileMode.GroupExecute
                        "world-readable", IO.UnixFileMode.OtherRead
                        "world-writable", IO.UnixFileMode.OtherWrite
                        "world-executable", IO.UnixFileMode.OtherExecute
                        "set-user-id", IO.UnixFileMode.SetUser
                        "set-group-id", IO.UnixFileMode.SetGroup
                        "sticky", IO.UnixFileMode.StickyBit ] do
                      let mode = userRead ||| userWrite ||| forbiddenBit
                      let path = writeToken root name mode (String.replicate 32 "p")
                      Expect.equal
                          (ControllerConfig.readTokenFileSecurely path)
                          (Error "FOGELL_API_TOKEN_FILE mode must be 0400 or 0600")
                          $"{name} cannot carry the global operator bearer")

          testCase "symlinks, directories, FIFOs, and oversized files refuse promptly" <| fun _ ->
              withRoot (fun root ->
                  let target = writeToken root "target" (userRead ||| userWrite) (String.replicate 32 "t")
                  let link = IO.Path.Combine(root, "link")
                  IO.File.CreateSymbolicLink(link, target) |> ignore
                  Expect.equal
                      (ControllerConfig.readTokenFileSecurely link)
                      (Error "FOGELL_API_TOKEN_FILE must name a readable regular non-symlink file")
                      "O_NOFOLLOW rejects the final link"

                  Expect.equal
                      (ControllerConfig.readTokenFileSecurely root)
                      (Error "FOGELL_API_TOKEN_FILE must name a regular non-symlink file")
                      "a directory is not token material"

                  let fifo = IO.Path.Combine(root, "fifo")
                  Expect.equal (mkfifo(fifo, 0x180u)) 0 "the FIFO fixture is created"
                  let fifoRead = System.Threading.Tasks.Task.Run(fun () -> ControllerConfig.readTokenFileSecurely fifo)
                  let completedPromptly = fifoRead.Wait(TimeSpan.FromSeconds 2.0)

                  if not completedPromptly then
                      // Reap a mutant that blocks in open(2): a writer releases
                      // its read-side open so no thread-pool worker is stranded.
                      use writer = new IO.FileStream(fifo, IO.FileMode.Open, IO.FileAccess.Write, IO.FileShare.ReadWrite)
                      Expect.isTrue (fifoRead.Wait(TimeSpan.FromSeconds 2.0)) "the blocked FIFO mutant is reaped"

                  Expect.isTrue
                      completedPromptly
                      "O_NONBLOCK prevents an attacker-controlled FIFO from holding startup"
                  Expect.equal
                      fifoRead.Result
                      (Error "FOGELL_API_TOKEN_FILE must name a regular non-symlink file")
                      "the promptly opened FIFO is classified as non-regular"

                  let oversized =
                      writeToken
                          root
                          "oversized"
                          (userRead ||| userWrite)
                          (String.replicate (ControllerConfig.maxApiTokenFileBytes + 1) "x")
                  Expect.equal
                      (ControllerConfig.readTokenFileSecurely oversized)
                      (Error $"FOGELL_API_TOKEN_FILE must be at most {ControllerConfig.maxApiTokenFileBytes} bytes")
                      "startup reads no unbounded token file")

          testCase "malformed and non-UTF-8 token encodings are refused" <| fun _ ->
              withRoot (fun root ->
                  let invalidPath = IO.Path.Combine(root, "invalid-utf8")
                  IO.File.WriteAllBytes(invalidPath, Array.append (Array.create 32 0x61uy) [| 0xFFuy |])
                  IO.File.SetUnixFileMode(invalidPath, userRead ||| userWrite)
                  Expect.equal
                      (ControllerConfig.readTokenFileSecurely invalidPath)
                      (Error "FOGELL_API_TOKEN_FILE could not be decoded")
                      "replacement fallback cannot normalize malformed credential bytes"

                  let utf16Path = IO.Path.Combine(root, "utf16")
                  let utf16 =
                      Array.append
                          (Encoding.Unicode.GetPreamble())
                          (Encoding.Unicode.GetBytes(String.replicate 32 "u"))
                  IO.File.WriteAllBytes(utf16Path, utf16)
                  IO.File.SetUnixFileMode(utf16Path, userRead ||| userWrite)
                  Expect.equal
                      (ControllerConfig.readTokenFileSecurely utf16Path)
                      (Error "FOGELL_API_TOKEN_FILE could not be decoded")
                      "BOM detection cannot silently admit a different token encoding")

          testCase "flag tables and statx metadata fail closed independent of ambient privileges" <| fun _ ->
              Expect.equal
                  (ControllerConfig.tokenFileOpenFlags LinuxOpenFlags.asmGeneric)
                  (0x800 ||| 0x80000 ||| LinuxOpenFlags.asmGeneric.NoFollow)
                  "the generic ABI contributes its own no-follow bit"
              Expect.equal
                  (ControllerConfig.tokenFileOpenFlags LinuxOpenFlags.armLineage)
                  (0x800 ||| 0x80000 ||| LinuxOpenFlags.armLineage.NoFollow)
                  "the arm lineage contributes its distinct no-follow bit"

              let mutable status = Unchecked.defaultof<ControllerConfig.LinuxStatx>
              status.Mask <- 0x20Bu
              status.Mode <- 0x8180us
              status.UserId <- geteuid () + 1u
              status.GroupId <- geteuid () + 2u
              status.Size <- 32UL
              Expect.equal
                  (ControllerConfig.tokenFileMetadataFromStatx status)
                  (Ok
                      { Mode = 0x8180us
                        Owner = geteuid () + 1u
                        Size = 32UL })
                  "the ABI record maps stx_uid, not the commonly-equal stx_gid"

              status.Mask <- 0x203u
              Expect.equal
                  (ControllerConfig.tokenFileMetadataFromStatx status)
                  (Error "FOGELL_API_TOKEN_FILE metadata could not be read from the opened file")
                  "a kernel record that omits stx_uid is refused"

              let metadata: ControllerConfig.TokenFileMetadata =
                  { Mode = 0x8180us
                    Owner = geteuid () + 1u
                    Size = 32UL }
              Expect.equal
                  (ControllerConfig.validateTokenFileMetadata (geteuid ()) metadata)
                  (Error "FOGELL_API_TOKEN_FILE must be owned by the service identity")
                  "a correctly-shaped file owned by another identity is refused"

              let specialMode =
                  { metadata with
                      Mode = 0x8980us
                      Owner = geteuid () }
              Expect.equal
                  (ControllerConfig.validateTokenFileMetadata (geteuid ()) specialMode)
                  (Error "FOGELL_API_TOKEN_FILE mode must be 0400 or 0600")
                  "set-id and sticky bits are not hidden by a 0777-only mask"

          testCase "a pathname replacement after open cannot substitute token bytes" <| fun _ ->
              withRoot (fun root ->
                  let original = String.replicate 32 "o"
                  let replacement = String.replicate 32 "r"
                  let path = writeToken root "token" (userRead ||| userWrite) original
                  let moved = IO.Path.Combine(root, "opened-inode")
                  let mutable descriptorFlags = -1

                  let result =
                      ControllerConfig.readTokenFileSecurelyWith
                          (fun descriptor ->
                              descriptorFlags <- fcntl(descriptor, 1)
                              IO.File.Move(path, moved)
                              writeToken
                                  root
                                  "token"
                                  (userRead ||| userWrite ||| IO.UnixFileMode.OtherRead)
                                  replacement
                              |> ignore)
                          ignore
                          path

                  Expect.equal result (Ok original) "validation and reading stay on the opened inode"
                  Expect.equal (descriptorFlags &&& 1) 1 "O_CLOEXEC marks the token descriptor close-on-exec"
                  Expect.equal (IO.File.ReadAllText path) replacement "the pathname now names different insecure bytes")

          testCase "growth after metadata validation is still bounded" <| fun _ ->
              withRoot (fun root ->
                  let path = writeToken root "growing" (userRead ||| userWrite) (String.replicate 32 "g")

                  let result =
                      ControllerConfig.readTokenFileSecurelyWith
                          ignore
                          (fun () ->
                              use append =
                                  new IO.FileStream(path, IO.FileMode.Append, IO.FileAccess.Write, IO.FileShare.ReadWrite)
                              append.Write(Array.create<byte> ControllerConfig.maxApiTokenFileBytes 0x78uy))
                          path

                  Expect.equal
                      result
                      (Error $"FOGELL_API_TOKEN_FILE must be at most {ControllerConfig.maxApiTokenFileBytes} bytes")
                      "the descriptor reader retains one extra byte and rejects in-place growth") ]

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
                    HttpMethod.Get, $"{baseUrl}/api/v1/organizations/{o}/scheduler/explain"
                    HttpMethod.Get, $"{baseUrl}/api/v1/organizations/{o}/effects/uncertain" ]

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

              let multiOrg, multiProject = freshProject ()
              let multiBuildsUrl =
                  $"{baseUrl}/api/v1/organizations/{multiOrg.Value}/projects/{multiProject.Value}/builds"
              let _, multiAdmission =
                  send HttpMethod.Post multiBuildsUrl (Some token) (Some "artifact-multi-node") (Some pipeline)
              let multiBuild =
                  Text.RegularExpressions.Regex.Match(multiAdmission, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
                  |> BuildId
              let multiAttempt =
                  Text.RegularExpressions.Regex.Match(multiAdmission, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
                  |> AttemptId
              let multiOwner = "artifact-multi-owner"
              let multiFence =
                  match store.OfferAttempt(multiOrg, multiAttempt, multiOwner, 60) with
                  | Ok value -> value
                  | Error error -> failtestf "offer failed: %s" error
              Expect.isTrue
                  (store.AcceptAttempt(multiOrg, multiAttempt, multiFence, multiOwner))
                  "multi-node migration attempt accepted"
              match store.PublishTerminal(multiOrg, multiAttempt, multiFence, multiOwner, Success) with
              | Ok () -> ()
              | Error error -> failtestf "terminal publication failed: %s" error

              use multiConnection = new Npgsql.NpgsqlConnection(connectionString)
              multiConnection.Open()
              use multiTransaction = multiConnection.BeginTransaction()
              use addLegacyNode = multiConnection.CreateCommand()
              addLegacyNode.Transaction <- multiTransaction
              addLegacyNode.CommandText <-
                  "SELECT set_config('fogell.organization_id', @org_text, true);
                   INSERT INTO nodes
                       (id, organization_id, build_id, name, ordinal,
                        required_trust_pool, required_capabilities, status)
                   VALUES
                       (@node, @org, @build, 'legacy-second-node', 1,
                        'trusted-linux', ARRAY['linux'], 'success');
                   INSERT INTO attempts
                       (id, organization_id, node_id, ordinal, state, fence,
                        restore_epoch, result)
                   SELECT @attempt, @org, @node, 0, 'terminal', 0,
                          restore_epoch, 'success'
                     FROM controller_metadata WHERE singleton"
              addLegacyNode.Parameters.AddWithValue("org_text", string multiOrg.Value) |> ignore
              addLegacyNode.Parameters.AddWithValue("org", multiOrg.Value) |> ignore
              addLegacyNode.Parameters.AddWithValue("build", multiBuild.Value) |> ignore
              addLegacyNode.Parameters.AddWithValue("node", Guid.NewGuid()) |> ignore
              addLegacyNode.Parameters.AddWithValue("attempt", Guid.NewGuid()) |> ignore
              addLegacyNode.ExecuteNonQuery() |> ignore
              multiTransaction.Commit()
              let mutable ambiguousMigrationCalled = false
              Expect.equal
                  (store.MigrateLegacyArtifactSnapshot(
                      multiOrg,
                      multiProject,
                      multiBuild,
                      multiAttempt,
                      fun () ->
                          ambiguousMigrationCalled <- true
                          Ok()))
                  (Ok false)
                  "build-keyed legacy output is not assigned to one attempt of a multi-node build"
              Expect.isFalse
                  ambiguousMigrationCalled
                  "ambiguous multi-node migration performs no filesystem callback"

              let handoffOrg, handoffProject = freshProject ()
              let handoffBuildsUrl =
                  $"{baseUrl}/api/v1/organizations/{handoffOrg.Value}/projects/{handoffProject.Value}/builds"
              let _, handoffAdmission =
                  send HttpMethod.Post handoffBuildsUrl (Some token) (Some "artifact-retry-handoff") (Some pipeline)
              let handoffBuild =
                  Text.RegularExpressions.Regex.Match(handoffAdmission, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
                  |> BuildId
              let handoffParent =
                  Text.RegularExpressions.Regex.Match(handoffAdmission, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value
                  |> Guid.Parse
                  |> AttemptId
              let handoffBuildKey = handoffBuild.Value.ToString "N"
              let handoffWorkspace = IO.Path.Combine(stateRoot, "artifact-handoff", handoffBuildKey)
              let parentOnly = IO.Path.Combine(handoffWorkspace, "parent-only.bin")
              IO.Directory.CreateDirectory handoffWorkspace |> ignore
              IO.File.WriteAllBytes(parentOnly, [| 1uy; 2uy; 3uy |])
              let handoffArtifactRoot =
                  IO.Path.Combine(
                      stateRoot,
                      "workspaces",
                      handoffOrg.Value.ToString "N",
                      "_artifacts")
              Expect.sequenceEqual
                  (Publish.archive
                      (ArtifactStore.under handoffArtifactRoot)
                      handoffBuildKey
                      handoffWorkspace
                      [ "parent-only.bin" ])
                  [ "parent-only.bin" ]
                  "the pre-upgrade parent leaves one build-keyed artifact"
              let handoffOwner = "artifact-handoff-owner"
              let handoffFence =
                  match store.OfferAttempt(handoffOrg, handoffParent, handoffOwner, 60) with
                  | Ok value -> value
                  | Error error -> failtestf "offer failed: %s" error
              Expect.isTrue
                  (store.AcceptAttempt(handoffOrg, handoffParent, handoffFence, handoffOwner))
                  "retry-handoff parent accepted"
              match store.PublishTerminal(handoffOrg, handoffParent, handoffFence, handoffOwner, Success) with
              | Ok () -> ()
              | Error error -> failtestf "terminal publication failed: %s" error
              let handoffChild = AttemptId(Guid.NewGuid())
              match store.DecideRetry(handoffOrg, handoffParent, 2, handoffChild) with
              | Ok _ -> ()
              | Error error -> failtestf "retry decision failed: %A" error
              let handoffClaim =
                  match
                      store.ClaimNextExecution(
                          handoffOrg,
                          "artifact-handoff-worker",
                          "trusted-linux",
                          [ "linux" ],
                          60)
                  with
                  | Ok(Some claim) -> claim
                  | result -> failtestf "retry execution claim failed: %A" result
              Expect.equal handoffClaim.AttemptId handoffChild "the queued retry is claimed"
              Expect.equal
                  handoffClaim.RetryOf
                  (Some handoffParent)
                  "the execution claim carries its exact artifact parent"
              Expect.equal
                  (ArtifactSnapshots.prepareRetry
                      stateRoot
                      handoffOrg.Value
                      handoffBuild.Value
                      (handoffClaim.RetryOf |> Option.map _.Value))
                  (Ok())
                  "retry preparation freezes leftover build-keyed bytes to the parent attempt"
              let handoffParentSnapshot =
                  IO.Path.Combine(
                      stateRoot,
                      "workspaces",
                      handoffOrg.Value.ToString "N",
                      "_artifact-snapshots",
                      handoffParent.Value.ToString "N")
              Expect.isTrue
                  (IO.File.Exists(IO.Path.Combine(handoffParentSnapshot, "parent-only.bin")))
                  "the legacy file belongs to the parent snapshot"
              Expect.isFalse
                  (IO.Directory.Exists(IO.Path.Combine(handoffArtifactRoot, handoffBuildKey)))
                  "retry staging starts empty after parent handoff"
              IO.File.WriteAllBytes(IO.Path.Combine(handoffWorkspace, "retry-only.bin"), [| 4uy; 5uy; 6uy |])
              Expect.sequenceEqual
                  (Publish.archive
                      (ArtifactStore.under handoffArtifactRoot)
                      handoffBuildKey
                      handoffWorkspace
                      [ "retry-only.bin" ])
                  [ "retry-only.bin" ]
                  "the retry archives only its own requested file"
              let handoffChildSnapshot =
                  match
                      ArtifactSnapshots.finalize
                          stateRoot
                          handoffOrg.Value
                          handoffBuild.Value
                          handoffChild.Value
                  with
                  | Ok path -> path
                  | Error error -> failtestf "retry snapshot failed: %s" error
              Expect.isTrue
                  (IO.File.Exists(IO.Path.Combine(handoffChildSnapshot, "retry-only.bin")))
                  "the retry snapshot contains its own artifact"
              Expect.isFalse
                  (IO.File.Exists(IO.Path.Combine(handoffChildSnapshot, "parent-only.bin")))
                  "the retry snapshot cannot inherit a parent-only legacy artifact"

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

              if not (effectiveIdentityIsRoot ()) then
                  let unavailableOrg, unavailableProject = freshProject ()
                  let unavailableBuildsUrl =
                      $"{baseUrl}/api/v1/organizations/{unavailableOrg.Value}/projects/{unavailableProject.Value}/builds"
                  let _, unavailableAdmission =
                      send HttpMethod.Post unavailableBuildsUrl (Some token) (Some "artifact-unavailable") (Some pipeline)
                  let unavailableBuild =
                      Text.RegularExpressions.Regex.Match(unavailableAdmission, "\"build_id\":\"([^\"]+)\"").Groups[1].Value
                      |> Guid.Parse
                      |> BuildId
                  let unavailableAttempt =
                      Text.RegularExpressions.Regex.Match(unavailableAdmission, "\"attempt_id\":\"([^\"]+)\"").Groups[1].Value
                      |> Guid.Parse
                      |> AttemptId
                  let unavailableOwner = "artifact-unavailable-owner"
                  let unavailableFence =
                      match store.OfferAttempt(unavailableOrg, unavailableAttempt, unavailableOwner, 60) with
                      | Ok value -> value
                      | Error error -> failtestf "offer failed: %s" error
                  Expect.isTrue
                      (store.AcceptAttempt(
                          unavailableOrg,
                          unavailableAttempt,
                          unavailableFence,
                          unavailableOwner))
                      "unavailable-storage attempt accepted"
                  match
                      store.PublishTerminal(
                          unavailableOrg,
                          unavailableAttempt,
                          unavailableFence,
                          unavailableOwner,
                          Success)
                  with
                  | Ok () -> ()
                  | Error error -> failtestf "terminal publication failed: %s" error
                  match
                      ArtifactSnapshots.finalize
                          stateRoot
                          unavailableOrg.Value
                          unavailableBuild.Value
                          unavailableAttempt.Value
                  with
                  | Ok _ -> ()
                  | Error error -> failtestf "unavailable-storage snapshot failed: %s" error
                  let unavailableWorkspace =
                      IO.Path.Combine(
                          stateRoot,
                          "workspaces",
                          unavailableOrg.Value.ToString "N")
                  let originalMode = IO.File.GetUnixFileMode unavailableWorkspace
                  let mutable unavailableCode = 0
                  try
                      IO.File.SetUnixFileMode(unavailableWorkspace, enum<IO.UnixFileMode> 0)
                      let code, _ =
                          send HttpMethod.Get
                              $"{unavailableBuildsUrl}/{unavailableBuild.Value}/attempts/{unavailableAttempt.Value}/artifacts/missing.bin"
                              (Some token) None None
                      unavailableCode <- code
                  finally
                      IO.File.SetUnixFileMode(unavailableWorkspace, originalMode)
                  Expect.equal
                      unavailableCode
                      503
                      "a workspace open failure remains retryable storage unavailability, not a false 404"

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

/// FG-026b. The closed-world registry, the single dispatch path, the
/// file-drop destination simulator and the four crash windows, driven against
/// real attempts on live PostgreSQL. The trigger arms run the production worker
/// scan and never call a Store reconciliation member themselves.
let effectDispatch =
    let dropRoot =
        IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-fg026b-drop-{Guid.NewGuid():N}")

    do IO.Directory.CreateDirectory dropRoot |> ignore
    // The operator-created marker that pins the destination.
    do IO.File.WriteAllBytes(IO.Path.Combine(dropRoot, EffectProducerConfig.fileDropRootMarker), [||])

    let abortSentinel = "fg026b-window-abort"
    let abort () = raise (InvalidOperationException abortSentinel)

    let admitClaimWith (beginExecution: bool) (org: OrganizationId) (project: ProjectId) (key: string) (owner: string) =
        let admitted =
            match
                store.AdmitBuild
                    { OrganizationId = org
                      ProjectId = project
                      IdempotencyKey = key
                      PipelineSource = Text.Encoding.UTF8.GetBytes $"pipeline:{key}"
                      StageNames = [ "effect" ]
                      RequiredTrustPool = "trusted-linux"
                      RequiredCapabilities = [ "linux" ] }
            with
            | Ok admitted -> admitted
            | Error error -> failtestf "admission failed: %s" error

        let claim =
            match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
            | Ok(Some claim) when claim.AttemptId = admitted.AttemptId -> claim
            | other -> failtestf "execution claim did not return the admitted attempt: %A" other

        if beginExecution then
            match store.BeginExecution(org, claim.AttemptId, claim.Fence, owner, 60) with
            | Ok ExecutionStarted -> ()
            | other -> failtestf "execution start failed: %A" other

        claim

    let admitClaim org project key owner = admitClaimWith true org project key owner

    let runningClaim key owner =
        let org, project = freshProject ()
        org, admitClaim org project key owner

    /// Offered but not yet started: the state in which a fence can still move.
    let offeredClaim key owner =
        let org, project = freshProject ()
        org, admitClaimWith false org project key owner

    let ledgerRow (org: OrganizationId) (attempt: AttemptId) (key: string) =
        use conn = new Npgsql.NpgsqlConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT state, uncertain_from FROM effect_checkpoints
             WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("k", key) |> ignore
        use reader = cmd.ExecuteReader()

        if reader.Read() then
            Some(reader.GetString 0, (if reader.IsDBNull 1 then None else Some(reader.GetString 1)))
        else
            None

    let ledgerRows (org: OrganizationId) (attempt: AttemptId) =
        use conn = new Npgsql.NpgsqlConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT count(*) FROM effect_checkpoints WHERE organization_id = @o AND attempt_id = @a"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.ExecuteScalar() :?> int64

    let expireLease (org: OrganizationId) (attempt: AttemptId) =
        use conn = new Npgsql.NpgsqlConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "UPDATE attempts SET lease_expires_at = clock_timestamp() - interval '1 second'
             WHERE organization_id = @o AND id = @a"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        Expect.equal (cmd.ExecuteNonQuery()) 1 "the attempt's lease was aged"

    let receiptCount (org: OrganizationId) =
        let directory = IO.Path.Combine(dropRoot, org.Value.ToString "N")

        if IO.Directory.Exists directory then
            IO.Directory.GetFiles(directory, "*.receipt").Length
        else
            0

    /// The real file-drop connector with its invocation counted. The counter
    /// is the number of times the destination was driven; the receipt count is
    /// what the destination holds.
    let counted (claim: ExecutionClaim) (terminal: BuildStatus) =
        let invocations = ref 0
        let real = FileDropReceipt.invocation dropRoot claim terminal

        invocations,
        { real with
            Invoke =
                fun () ->
                    invocations.Value <- invocations.Value + 1
                    real.Invoke() }

    let effectKey (claim: ExecutionClaim) =
        EffectProducer.effectKey EffectProducer.FileDropReceipt (FileDropReceipt.identity claim)

    let withHook (window: EffectKillWindow) =
        match window with
        | EffectKillWindow.AfterPrepare -> { EffectDispatch.noHooks with AfterPrepare = abort }
        | EffectKillWindow.AfterInvoke -> { EffectDispatch.noHooks with AfterInvoke = abort }
        | EffectKillWindow.AfterApply -> { EffectDispatch.noHooks with AfterApply = abort }
        | EffectKillWindow.AfterConfirm -> { EffectDispatch.noHooks with AfterConfirm = abort }

    let abortedRun (store: Store) authority hooks invocation =
        try
            EffectDispatch.run store authority hooks invocation |> ignore
            failtest "the window hook did not abort the dispatch"
        with :? InvalidOperationException as ex when ex.Message = abortSentinel ->
            ()

    /// The production worker against the shared test store, with the file-drop
    /// simulator enabled and no kill hook. Its scan is the lease-expiry trigger.
    let newWorkerWithLease (leaseSeconds: int) =
        let stateRoot = IO.Path.Combine(dropRoot, "worker-state")
        IO.Directory.CreateDirectory stateRoot |> ignore

        let workerConfig: ControllerConfig =
            { RuntimeDatabaseUrl = connectionString
              MaintenanceDatabaseUrl = connectionString + ";Application Name=fg026b-maintenance"
              ApiToken = token
              ListenUrl = "http://127.0.0.1:0"
              StateRoot = stateRoot
              RunHostPath = "/bin/true"
              SetsidPath = ControllerConfig.trustedSetsidLauncher
              TrustPool = "trusted-linux"
              MaxPipelineBytes = 1024
              MaxLogChunks = 100
              PollMilliseconds = 50
              LeaseSeconds = leaseSeconds
              EffectProducers = { FileDropRoot = Some dropRoot; KillAt = None } }

        new LocalWorker(
            workerConfig,
            store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalWorker>.Instance)

    let newWorker () = newWorkerWithLease 60

    let withEffectEnvironment (root: string option) (kill: string option) f =
        let names = [ "FOGELL_EFFECT_FILE_DROP_ROOT"; "FOGELL_EFFECT_KILL_AT" ]
        let previous = names |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)
        Environment.SetEnvironmentVariable("FOGELL_EFFECT_FILE_DROP_ROOT", Option.toObj root)
        Environment.SetEnvironmentVariable("FOGELL_EFFECT_KILL_AT", Option.toObj kill)

        try
            f ()
        finally
            previous |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

    testList
        "FG-026b effect dispatch"
        [ test "the registry is closed: every EffectProducer case is registered exactly once with a unique name" {
              let cases =
                  Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<EffectProducer>)
                  |> Array.map (fun case ->
                      Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, [||]) :?> EffectProducer)
                  |> List.ofArray

              Expect.equal (List.sort EffectProducer.all) (List.sort cases) "EffectProducer.all lists every declared case"
              Expect.equal (List.distinct EffectProducer.all) EffectProducer.all "no producer is registered twice"

              let names = EffectProducer.all |> List.map EffectProducer.name
              Expect.equal (List.distinct names) names "producer names are unique"

              for name in names do
                  Expect.isTrue
                      (name |> Seq.forall (fun c -> Char.IsLower c || Char.IsDigit c || c = '-'))
                      $"producer name '{name}' is a stable lowercase code"

              Expect.equal
                  (EffectProducer.effectKey EffectProducer.FileDropReceipt "abc")
                  "file-drop-receipt:abc"
                  "the ledger key is the producer name and the attempt-scoped identity"
          }

          test "effect producer configuration refuses a kill hook without a destination and a destination that is relative, missing, or inside the state root" {
              let stateRoot = IO.Path.Combine(dropRoot, "state")
              IO.Directory.CreateDirectory stateRoot |> ignore
              let nested = IO.Path.Combine(stateRoot, "nested")
              IO.Directory.CreateDirectory nested |> ignore
              let destination = IO.Path.Combine(dropRoot, "destination")
              IO.Directory.CreateDirectory destination |> ignore

              withEffectEnvironment None None (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Ok EffectProducerConfig.disabled)
                      "both variables absent is the production default: no producer enabled")

              withEffectEnvironment None (Some "invoke") (fun () ->
                  match EffectProducerConfig.loadFromEnvironment stateRoot with
                  | Error error -> Expect.stringContains error "requires FOGELL_EFFECT_FILE_DROP_ROOT" "a kill hook needs a simulator"
                  | Ok config -> failtestf "kill hook without a destination was accepted: %A" config)

              withEffectEnvironment (Some "relative/drop") None (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Error "FOGELL_EFFECT_FILE_DROP_ROOT must be absolute")
                      "relative destinations are refused")

              withEffectEnvironment (Some(IO.Path.Combine(dropRoot, "missing"))) None (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Error "FOGELL_EFFECT_FILE_DROP_ROOT must name an existing directory")
                      "a missing destination is never created by the controller")

              withEffectEnvironment (Some nested) None (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Error "FOGELL_EFFECT_FILE_DROP_ROOT must be disjoint from FOGELL_STATE_ROOT")
                      "a destination inside the state root is controller state, not an external effect")

              withEffectEnvironment (Some dropRoot) None (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Error "FOGELL_EFFECT_FILE_DROP_ROOT must be disjoint from FOGELL_STATE_ROOT")
                      "a destination that contains the state root is refused as well")

              withEffectEnvironment (Some destination) None (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Error $"FOGELL_EFFECT_FILE_DROP_ROOT must contain the operator-created {EffectProducerConfig.fileDropRootMarker} marker file")
                      "an unpinned destination (no operator marker) is refused: the controller never creates the root")

              IO.File.WriteAllBytes(IO.Path.Combine(destination, EffectProducerConfig.fileDropRootMarker), [||])

              withEffectEnvironment (Some destination) (Some "teardown") (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Error "FOGELL_EFFECT_KILL_AT must be one of prepare, invoke, apply, confirm")
                      "an unknown window is refused")

              withEffectEnvironment (Some destination) (Some "apply") (fun () ->
                  Expect.equal
                      (EffectProducerConfig.loadFromEnvironment stateRoot)
                      (Ok
                          { FileDropRoot = Some(IO.Path.GetFullPath destination)
                            KillAt = Some EffectKillWindow.AfterApply })
                      "a valid destination with a named window enables the simulator and arms the hook")

              Expect.equal
                  (EffectProducerConfig.killWindowNames |> List.map fst)
                  [ "prepare"; "invoke"; "apply"; "confirm" ]
                  "the four windows are the four ledger transitions"
          }

          test "with no producer enabled the terminal path makes no Store call" {
              let unreachable = Store("Host=127.0.0.1;Port=9;Username=nobody;Database=nowhere;Timeout=1")
              let org, claim = runningClaim "fg026b-disabled" "fg026b-disabled-owner"
              let authority = EffectAuthority.ofClaim "fg026b-disabled-owner" claim

              Expect.equal
                  (EffectDispatch.runRegistered unreachable authority EffectDispatch.noHooks EffectProducerConfig.disabled claim Success)
                  []
                  "no producer, no dispatch, no database round trip"

              Expect.equal (ledgerRows org claim.AttemptId) 0L "the ledger holds nothing for the attempt"
              Expect.equal (receiptCount org) 0 "the destination holds nothing"
          }

          test "a registered file-drop receipt runs prepare, invoke, apply and confirm once, and an exact same-attempt replay is a no-op" {
              let owner = "fg026b-once-owner"
              let org, claim = runningClaim "fg026b-once" owner
              let authority = EffectAuthority.ofClaim owner claim
              let invocations, invocation = counted claim Success

              Expect.equal
                  (EffectDispatch.run store authority EffectDispatch.noHooks invocation)
                  (DispatchOutcome.Confirmed false)
                  "first dispatch confirms"
              Expect.equal invocations.Value 1 "the destination was driven once"
              Expect.equal (receiptCount org) 1 "one receipt"
              Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("confirmed", None)) "ledger confirmed"
              Expect.sequenceEqual
                  (IO.File.ReadAllBytes(FileDropReceipt.receiptPath dropRoot claim))
                  invocation.Payload
                  "the receipt is the exact canonical payload"
              Expect.equal
                  (Text.Encoding.UTF8.GetString invocation.Payload)
                  $"{{\"build\":\"{claim.BuildId.Value}\",\"attempt\":\"{claim.AttemptId.Value}\",\"fence\":{claim.Fence.Value},\"pipeline_sha256\":\"{claim.PipelineSha256}\",\"journal_terminal\":\"Success\"}}"
                  "the receipt names the build, attempt, fence, pipeline digest and journal terminal"

              for replay in 1..2 do
                  Expect.equal
                      (EffectDispatch.run store authority EffectDispatch.noHooks invocation)
                      (DispatchOutcome.Confirmed true)
                      $"replay {replay} is recognised as already confirmed"

              Expect.equal invocations.Value 1 "no replay drives the destination again"
              Expect.equal (receiptCount org) 1 "still one receipt"

              let registered =
                  EffectDispatch.runRegistered
                      store
                      authority
                      EffectDispatch.noHooks
                      { FileDropRoot = Some dropRoot; KillAt = None }
                      claim
                      Success
              Expect.equal
                  registered
                  [ EffectProducer.FileDropReceipt, DispatchOutcome.Confirmed true ]
                  "the registered path reaches the same row through the same key and payload"
          }

          test "payload substitution on the same attempt-scoped key is refused before any invocation" {
              let owner = "fg026b-subst-owner"
              let org, claim = runningClaim "fg026b-subst" owner
              let authority = EffectAuthority.ofClaim owner claim
              let invocations, invocation = counted claim Success

              // A prepared row with no invocation yet: a different payload under
              // the same key is refused and still nothing is invoked.
              abortedRun store authority (withHook EffectKillWindow.AfterPrepare) invocation
              Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("prepared", None)) "prepared, nothing invoked"
              let substitutedInvocations, substituted = counted claim Failure
              match EffectDispatch.run store authority EffectDispatch.noHooks substituted with
              | DispatchOutcome.Refused reason -> Expect.stringContains reason "payload bytes" "the refusal names the digest mismatch"
              | other -> failtestf "substituted payload was not refused: %A" other
              Expect.equal (invocations.Value, substitutedInvocations.Value) (0, 0) "neither invocation ran"
              Expect.equal (receiptCount org) 0 "no receipt"

              // The original payload resumes under the same live authority and
              // confirms; the substitute is still refused afterwards.
              Expect.equal
                  (EffectDispatch.run store authority EffectDispatch.noHooks invocation)
                  (DispatchOutcome.Confirmed false)
                  "the exact payload completes"
              match EffectDispatch.run store authority EffectDispatch.noHooks substituted with
              | DispatchOutcome.Refused _ -> ()
              | other -> failtestf "substituted payload after confirmation was not refused: %A" other
              Expect.equal (invocations.Value, substitutedInvocations.Value) (1, 0) "only the exact payload ever drove the destination"
              Expect.sequenceEqual
                  (IO.File.ReadAllBytes(FileDropReceipt.receiptPath dropRoot claim))
                  invocation.Payload
                  "the receipt bytes are the confirmed payload"

              // A flipped byte in the destination is not evidence: a replay sees
              // the ledger confirmed and never re-invokes, while a fresh confirm
              // read would have refused it.
              let path = FileDropReceipt.receiptPath dropRoot claim
              let tampered = IO.File.ReadAllBytes path
              tampered.[0] <- tampered.[0] ^^^ 1uy
              IO.File.WriteAllBytes(path, tampered)
              Expect.equal (invocation.Confirm()) (Ok false) "the connector's evidence read refuses flipped bytes"
              Expect.equal
                  (EffectDispatch.run store authority EffectDispatch.noHooks invocation)
                  (DispatchOutcome.Confirmed true)
                  "a confirmed row replays without touching the destination"
              Expect.equal invocations.Value 1 "tampering after confirmation never triggers a re-invocation"
          }

          test "stale fence, wrong owner, expired lease and pre-restore epoch are each refused before any invocation" {
              let refused label (org: OrganizationId) (claim: ExecutionClaim) authority =
                  let invocations, invocation = counted claim Success

                  match EffectDispatch.run store authority EffectDispatch.noHooks invocation with
                  | DispatchOutcome.Refused reason ->
                      Expect.stringContains reason "stale fence, wrong owner, expired lease, pre-restore epoch" $"{label} names the authority refusal"
                  | other -> failtestf "%s was not refused: %A" label other

                  Expect.equal invocations.Value 0 $"{label}: nothing invoked"
                  Expect.equal (ledgerRows org claim.AttemptId) 0L $"{label}: no ledger row"
                  Expect.equal (receiptCount org) 0 $"{label}: no receipt"

              let fenceOrg, fenceClaim = offeredClaim "fg026b-stale-fence" "fg026b-fence-owner"
              let newerFence =
                  match store.OfferAttempt(fenceOrg, fenceClaim.AttemptId, "fg026b-fence-owner", 60) with
                  | Ok fence -> fence
                  | Error error -> failtestf "re-offer failed: %s" error
              Expect.isGreaterThan newerFence.Value fenceClaim.Fence.Value "the fence advanced"
              refused "stale fence" fenceOrg fenceClaim (EffectAuthority.ofClaim "fg026b-fence-owner" fenceClaim)

              let ownerOrg, ownerClaim = runningClaim "fg026b-wrong-owner" "fg026b-owner-a"
              refused "wrong owner" ownerOrg ownerClaim (EffectAuthority.ofClaim "fg026b-owner-b" ownerClaim)

              let leaseOrg, leaseClaim = runningClaim "fg026b-expired-lease" "fg026b-lease-owner"
              expireLease leaseOrg leaseClaim.AttemptId
              refused "expired lease" leaseOrg leaseClaim (EffectAuthority.ofClaim "fg026b-lease-owner" leaseClaim)

              let epochOrg, epochClaim = runningClaim "fg026b-pre-restore" "fg026b-epoch-owner"
              store.ActivateRestore() |> ignore
              refused "pre-restore epoch" epochOrg epochClaim (EffectAuthority.ofClaim "fg026b-epoch-owner" epochClaim)
          }

          test "an abort in each of the four windows leaves the ledger in that window's state and the destination with that window's receipt" {
              let expectations =
                  [ EffectKillWindow.AfterPrepare, ("prepared", 0)
                    EffectKillWindow.AfterInvoke, ("prepared", 1)
                    EffectKillWindow.AfterApply, ("applied", 1)
                    EffectKillWindow.AfterConfirm, ("confirmed", 1) ]

              for window, (state, receipts) in expectations do
                  let owner = $"fg026b-window-owner-%A{window}"
                  let org, claim = runningClaim $"fg026b-window-%A{window}" owner
                  let authority = EffectAuthority.ofClaim owner claim
                  let invocations, invocation = counted claim Success

                  abortedRun store authority (withHook window) invocation
                  Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some(state, None)) $"%A{window}: ledger state"
                  Expect.equal (receiptCount org) receipts $"%A{window}: receipt count"
                  Expect.equal invocations.Value receipts $"%A{window}: invocation count"

                  // Resume under the same live authority: prepared work is driven
                  // through the idempotent connector, applied work only re-reads
                  // evidence, confirmed work replays as a no-op. The destination
                  // never holds more than one receipt.
                  let resumed = EffectDispatch.run store authority EffectDispatch.noHooks invocation
                  let expectedResume, expectedInvocations =
                      match window with
                      | EffectKillWindow.AfterPrepare -> DispatchOutcome.Confirmed false, 1
                      | EffectKillWindow.AfterInvoke -> DispatchOutcome.Confirmed false, 2
                      | EffectKillWindow.AfterApply -> DispatchOutcome.Confirmed false, 1
                      | EffectKillWindow.AfterConfirm -> DispatchOutcome.Confirmed true, 1
                  Expect.equal resumed expectedResume $"%A{window}: resume outcome"
                  Expect.equal invocations.Value expectedInvocations $"%A{window}: invocations after resume"
                  Expect.equal (receiptCount org) 1 $"%A{window}: exactly one receipt after resume"
                  Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("confirmed", None)) $"%A{window}: confirmed after resume"
          }

          test "an invocation failure or absent destination evidence is reported uncertain, leaves the row for reconciliation, and is never re-driven from applied" {
              let owner = "fg026b-uncertain-owner"
              let org, claim = runningClaim "fg026b-uncertain" owner
              let authority = EffectAuthority.ofClaim owner claim
              let _, real = counted claim Success

              let failing = { real with Invoke = fun () -> Error "destination refused the connection" }
              match EffectDispatch.run store authority EffectDispatch.noHooks failing with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "invocation failed after preparation" "names the window"
              | other -> failtestf "failed invocation was not uncertain: %A" other
              Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("prepared", None)) "the row stays prepared"

              let throwing = { real with Invoke = fun () -> failwith "connector threw" }
              match EffectDispatch.run store authority EffectDispatch.noHooks throwing with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "connector threw" "an exception is data, not a crash"
              | other -> failtestf "throwing invocation was not uncertain: %A" other

              let blind = ref 0
              let invokedBlind =
                  { real with
                      Invoke =
                          fun () ->
                              blind.Value <- blind.Value + 1
                              real.Invoke()
                      Confirm = fun () -> Ok false }
              match EffectDispatch.run store authority EffectDispatch.noHooks invokedBlind with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "evidence absent" "names the missing evidence"
              | other -> failtestf "absent evidence was not uncertain: %A" other
              Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("applied", None)) "the row is applied and awaits reconciliation"
              Expect.equal blind.Value 1 "driven once"

              match EffectDispatch.run store authority EffectDispatch.noHooks invokedBlind with
              | DispatchOutcome.Uncertain _ -> ()
              | other -> failtestf "applied row without evidence was not uncertain: %A" other
              Expect.equal blind.Value 1 "an applied row is never re-driven; only its evidence is re-read"

              let unreadable = { real with Confirm = fun () -> Error "destination unreadable" }
              match EffectDispatch.run store authority EffectDispatch.noHooks unreadable with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "unreadable" "names the evidence failure"
              | other -> failtestf "unreadable evidence was not uncertain: %A" other

              // The real evidence is present, so the honest read confirms.
              Expect.equal (EffectDispatch.run store authority EffectDispatch.noHooks real) (DispatchOutcome.Confirmed false) "evidence present confirms"
              Expect.equal (receiptCount org) 1 "one receipt throughout"
          }

          test "a retry child has a fresh attempt-scoped ledger identity and the parent's confirmed row is untouched" {
              let owner = "fg026b-retry-owner"
              let org, parent = runningClaim "fg026b-retry" owner
              let parentAuthority = EffectAuthority.ofClaim owner parent
              let parentInvocations, parentInvocation = counted parent Failure
              Expect.equal
                  (EffectDispatch.run store parentAuthority EffectDispatch.noHooks parentInvocation)
                  (DispatchOutcome.Confirmed false)
                  "parent confirms"
              match store.PublishTerminal(org, parent.AttemptId, parent.Fence, owner, Failure) with
              | Ok() -> ()
              | Error error -> failtestf "parent publication failed: %s" error

              let childId = AttemptId(Guid.NewGuid())
              match store.DecideRetry(org, parent.AttemptId, 2, childId) with
              | Ok _ -> ()
              | Error error -> failtestf "retry decision failed: %A" error
              let child =
                  match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some claim) when claim.AttemptId = childId -> claim
                  | other -> failtestf "the retry child was not claimed: %A" other
              Expect.equal child.RetryOf (Some parent.AttemptId) "the child carries its parent"
              match store.BeginExecution(org, child.AttemptId, child.Fence, owner, 60) with
              | Ok ExecutionStarted -> ()
              | other -> failtestf "child execution start failed: %A" other

              let childInvocations, childInvocation = counted child Success
              Expect.notEqual (effectKey child) (effectKey parent) "the child's key is a new attempt-scoped identity"
              Expect.equal
                  (EffectDispatch.run store (EffectAuthority.ofClaim owner child) EffectDispatch.noHooks childInvocation)
                  (DispatchOutcome.Confirmed false)
                  "the child prepares and confirms afresh"
              Expect.equal (childInvocations.Value, parentInvocations.Value) (1, 1) "each attempt drove its own destination once"
              Expect.equal (ledgerRow org parent.AttemptId (effectKey parent)) (Some("confirmed", None)) "the parent row is untouched"
              Expect.equal (ledgerRow org child.AttemptId (effectKey child)) (Some("confirmed", None)) "the child row is confirmed"
              Expect.equal (receiptCount org) 2 "two attempts, two receipts: cross-attempt idempotency is the connector's contract, not the ledger's"

              // The parent is terminal: its authority is gone, and its confirmed
              // row can neither be replayed nor re-prepared by anyone.
              match EffectDispatch.run store parentAuthority EffectDispatch.noHooks parentInvocation with
              | DispatchOutcome.Refused _ -> ()
              | other -> failtestf "a terminal parent's authority was accepted: %A" other
              Expect.equal parentInvocations.Value 1 "nothing drove the parent's destination again"
          }

          test "terminal publication is allowed only when every registered producer confirmed" {
              let producer = EffectProducer.FileDropReceipt

              Expect.equal
                  (WorkerControl.effectDispatchDisposition [])
                  WorkerControl.EffectDispatchDisposition.PublishAllowed
                  "an empty registry confirms vacuously: the terminal path without a producer is unchanged"
              Expect.equal
                  (WorkerControl.effectDispatchDisposition [ producer, DispatchOutcome.Confirmed false ])
                  WorkerControl.EffectDispatchDisposition.PublishAllowed
                  "a fresh confirmation allows publication"
              Expect.equal
                  (WorkerControl.effectDispatchDisposition [ producer, DispatchOutcome.Confirmed true ])
                  WorkerControl.EffectDispatchDisposition.PublishAllowed
                  "a replayed confirmation allows publication"

              for outcome in [ DispatchOutcome.Refused "stale"; DispatchOutcome.Uncertain "evidence absent" ] do
                  Expect.equal
                      (WorkerControl.effectDispatchDisposition [ producer, DispatchOutcome.Confirmed false; producer, outcome ])
                      (WorkerControl.EffectDispatchDisposition.ReconcileRequired "effect_dispatch_unconfirmed")
                      $"%A{outcome} fails closed into reconciliation with a stable reason"
          }

          test "the startup pass classifies every organization once and turns a throwing pass into a reported error" {
              let first = OrganizationId(Guid.NewGuid())
              let second = OrganizationId(Guid.NewGuid())
              let reports = Collections.Generic.List<OrganizationId * Result<EffectCheckpoint list, string>>()

              Program.reconcileEffectsAtStartup
                  (fun () -> [ first; second ])
                  (fun org -> if org = first then Ok [] else failwith "database unavailable")
                  (fun org outcome -> reports.Add((org, outcome)))

              Expect.equal
                  (List.ofSeq reports)
                  [ first, Ok []; second, Error "database unavailable" ]
                  "each organization is reported exactly once and a throw does not stop the pass"

              let failedListing = Collections.Generic.List<OrganizationId * Result<EffectCheckpoint list, string>>()
              Program.reconcileEffectsAtStartup
                  (fun () -> failwith "no organizations")
                  (fun _ -> failtest "no organization may be reconciled when the listing failed")
                  (fun org outcome -> failedListing.Add((org, outcome)))
              Expect.equal
                  (List.ofSeq failedListing)
                  [ OrganizationId Guid.Empty, Error "no organizations" ]
                  "a failed listing is reported once and refuses nothing"
          }

          test "ControllerConfig carries the effect producer configuration and refuses a kill hook without a destination at startup" {
              let root = IO.Path.Combine(dropRoot, $"config-{Guid.NewGuid():N}")
              let destination = IO.Path.Combine(root, "drop")
              IO.Directory.CreateDirectory destination |> ignore
              IO.File.WriteAllBytes(IO.Path.Combine(destination, EffectProducerConfig.fileDropRootMarker), [||])
              let tokenFile = IO.Path.Combine(root, "token")
              let runHost = IO.Path.Combine(root, "run-host")
              IO.File.WriteAllText(tokenFile, String.replicate 32 "t")
              // FG-251: the token file must be a service-owned regular file at 0400/0600.
              IO.File.SetUnixFileMode(tokenFile, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite)
              IO.File.WriteAllText(runHost, "#!/bin/sh\nexit 0\n")
              IO.File.SetUnixFileMode(runHost, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserExecute)

              let required =
                  [ "FOGELL_DATABASE_URL", "Host=runtime;Database=fogell"
                    "FOGELL_MAINTENANCE_DATABASE_URL", "Host=maintenance;Database=fogell"
                    "FOGELL_API_TOKEN_FILE", tokenFile
                    "FOGELL_LISTEN_URL", "http://127.0.0.1:18083"
                    "FOGELL_STATE_ROOT", IO.Path.Combine(root, "state")
                    "FOGELL_RUN_HOST_PATH", runHost
                    "FOGELL_LOCAL_TRUST_POOL", "trusted-linux"
                    "FOGELL_MAX_PIPELINE_BYTES", "1024"
                    "FOGELL_MAX_LOG_CHUNKS", "100"
                    "FOGELL_WORKER_POLL_MS", "50"
                    "FOGELL_WORKER_LEASE_SECONDS", "60" ]
              let previous = required |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable name)

              try
                  for name, value in required do
                      Environment.SetEnvironmentVariable(name, value)

                  withEffectEnvironment None None (fun () ->
                      match ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher with
                      | Ok config -> Expect.equal config.EffectProducers EffectProducerConfig.disabled "no producer without the variables"
                      | Error error -> failtestf "configuration refused: %s" error)

                  withEffectEnvironment (Some destination) (Some "confirm") (fun () ->
                      match ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher with
                      | Ok config ->
                          Expect.equal
                              config.EffectProducers
                              { FileDropRoot = Some(IO.Path.GetFullPath destination)
                                KillAt = Some EffectKillWindow.AfterConfirm }
                              "the simulator destination and armed window reach the worker configuration"
                      | Error error -> failtestf "configuration refused: %s" error)

                  withEffectEnvironment None (Some "prepare") (fun () ->
                      match ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher with
                      | Ok _ -> failtest "a kill hook without a destination reached a configured controller"
                      | Error error -> Expect.stringContains error "requires FOGELL_EFFECT_FILE_DROP_ROOT" "startup names the refusal")

                  withEffectEnvironment (Some(IO.Path.Combine(root, "state"))) None (fun () ->
                      match ControllerConfig.loadWithSetsidLauncher ControllerConfig.trustedSetsidLauncher with
                      | Ok _ -> failtest "the state root itself was accepted as an external destination"
                      | Error error -> Expect.stringContains error "disjoint from FOGELL_STATE_ROOT" "startup names the containment refusal")
              finally
                  previous |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))
          }

          test "the production lease-expiry trigger classifies an abort in the prepare, invoke and apply windows as tenant-scoped uncertainty with an operator surface and never re-invokes; a confirmed row survives lease loss unlisted" {
              use worker = newWorker ()

              let surface (org: OrganizationId) (attempt: AttemptId) =
                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <-
                      "SELECT (SELECT count(*) FROM events e
                                WHERE e.organization_id = @o AND e.attempt_id = @a AND e.kind = 'effect.uncertain'),
                              (SELECT count(*) FROM outbox o
                                WHERE o.organization_id = @o AND o.topic = 'effect.uncertain'
                                  AND o.body->>'attempt' = @a::text),
                              (SELECT string_agg(e.payload->>'reason' || '/' || (e.payload->>'uncertain_from'), ',')
                                 FROM events e
                                WHERE e.organization_id = @o AND e.attempt_id = @a AND e.kind = 'effect.uncertain'),
                              (SELECT state FROM attempts WHERE organization_id = @o AND id = @a)"
                  cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                  cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
                  use reader = cmd.ExecuteReader()
                  Expect.isTrue (reader.Read()) "surface row"
                  let events = reader.GetInt64 0
                  let outbox = reader.GetInt64 1
                  let reasons = if reader.IsDBNull 2 then "" else reader.GetString 2
                  let attemptState = reader.GetString 3
                  reader.Close()
                  events, outbox, reasons, attemptState

              let windows =
                  [ EffectKillWindow.AfterPrepare, Some("prepared", 0)
                    EffectKillWindow.AfterInvoke, Some("prepared", 1)
                    EffectKillWindow.AfterApply, Some("applied", 1)
                    EffectKillWindow.AfterConfirm, None ]

              // FG026B_NO_MANUAL_STORE_BEGIN
              // Only the real dispatch, an aged lease, and the production
              // worker scan appear below; the source audit refuses a manual
              // reconciliation call inside this fence.
              for window, classification in windows do
                  // The production owner shape: the periodic requeue only reaps
                  // leases held by a local controller identity.
                  let owner = $"local:fg026b-trigger:%A{window}"
                  let org, claim = runningClaim $"fg026b-trigger-%A{window}" owner
                  let authority = EffectAuthority.ofClaim owner claim
                  let invocations, invocation = counted claim Success
                  let key = effectKey claim

                  abortedRun store authority (withHook window) invocation
                  let invocationsAtAbort = invocations.Value
                  let receiptsAtAbort = receiptCount org
                  expireLease org claim.AttemptId

                  let ran = worker.ScanOrganization(org, Threading.CancellationToken.None).Result
                  Expect.isFalse ran $"%A{window}: the scan claimed nothing in this organization"

                  let events, outbox, reasons, attemptState = surface org claim.AttemptId
                  Expect.equal attemptState "reconciliation_required" $"%A{window}: the attempt lost its lease to the production requeue"

                  match classification with
                  | Some(origin, receipts) ->
                      Expect.equal (ledgerRow org claim.AttemptId key) (Some("uncertain", Some origin)) $"%A{window}: classified uncertain with its origin"
                      Expect.equal (events, outbox) (1L, 1L) $"%A{window}: exactly one effect.uncertain event and outbox row"
                      Expect.equal reasons $"lease_expired/{origin}" $"%A{window}: the surface names the trigger and the origin"
                      Expect.equal (receiptCount org) receipts $"%A{window}: the destination is untouched by classification"
                      Expect.contains
                          (store.ListUncertainEffects org |> List.map (fun c -> c.EffectKey))
                          key
                          $"%A{window}: the tenant listing carries the row"
                  | None ->
                      Expect.equal (ledgerRow org claim.AttemptId key) (Some("confirmed", None)) $"%A{window}: a confirmed row is final across lease loss"
                      Expect.equal (events, outbox) (0L, 0L) $"%A{window}: nothing to surface"
                      Expect.equal (receiptCount org) 1 $"%A{window}: one receipt"
                      Expect.isEmpty (store.ListUncertainEffects org) $"%A{window}: not listed"

                  Expect.equal invocations.Value invocationsAtAbort $"%A{window}: the trigger invoked nothing"
                  Expect.equal (receiptCount org) receiptsAtAbort $"%A{window}: the trigger wrote nothing"

                  // The old authority is gone: neither a resume nor a second scan
                  // drives the destination, and the surface is not duplicated.
                  match EffectDispatch.run store authority EffectDispatch.noHooks invocation with
                  | DispatchOutcome.Refused _ -> ()
                  | other -> failtestf "%A: resume under lost authority was not refused: %A" window other
                  worker.ScanOrganization(org, Threading.CancellationToken.None).Result |> ignore
                  let eventsAgain, outboxAgain, _, _ = surface org claim.AttemptId
                  Expect.equal (eventsAgain, outboxAgain) (events, outbox) $"%A{window}: a second scan publishes nothing more"
                  Expect.equal invocations.Value invocationsAtAbort $"%A{window}: still nothing re-invoked"
                  Expect.equal (receiptCount org) receiptsAtAbort $"%A{window}: still nothing written"
              // FG026B_NO_MANUAL_STORE_END
          }

          test "the reconciliation cadence classifies a stale row within two lease periods while the scan loop never runs" {
              // Codex finding 1 on #424: the scan loop runs one claim to
              // completion, so a pass bound to it waits for an unrelated build.
              // Here the scan loop is never started at all; only the periodic
              // loop runs, on the shortest lease the configuration allows.
              use worker = newWorkerWithLease 10
              let owner = "local:fg026b-cadence:owner"
              let org, claim = runningClaim "fg026b-cadence" owner
              let authority = EffectAuthority.ofClaim owner claim
              let invocations, invocation = counted claim Success
              let key = effectKey claim

              abortedRun store authority (withHook EffectKillWindow.AfterInvoke) invocation
              expireLease org claim.AttemptId
              Expect.equal (ledgerRow org claim.AttemptId key) (Some("prepared", None)) "stale prepared row before the cadence runs"

              use stop = new Threading.CancellationTokenSource()
              let loop = worker.ReconciliationLoop stop.Token
              let deadline = DateTime.UtcNow.AddSeconds 20.0
              let mutable classified = false

              while not classified && DateTime.UtcNow < deadline do
                  Threading.Thread.Sleep 250
                  classified <- ledgerRow org claim.AttemptId key = Some("uncertain", Some "prepared")

              stop.Cancel()
              loop.Wait(TimeSpan.FromSeconds 10.0) |> ignore
              Expect.isTrue loop.IsCompletedSuccessfully "the cadence loop stops with its token and does not fault"
              Expect.isTrue classified "classified within two lease periods without any claim finishing"
              Expect.equal invocations.Value 1 "the cadence invoked nothing"
              Expect.equal (receiptCount org) 1 "the cadence wrote nothing"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <-
                  "SELECT (SELECT count(*) FROM events WHERE organization_id = @o AND attempt_id = @a AND kind = 'effect.uncertain'),
                          (SELECT count(*) FROM outbox WHERE organization_id = @o AND topic = 'effect.uncertain' AND body->>'attempt' = @a::text)"
              cmd.Parameters.AddWithValue("o", org.Value) |> ignore
              cmd.Parameters.AddWithValue("a", claim.AttemptId.Value) |> ignore
              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "surface row"
              Expect.equal (reader.GetInt64 0, reader.GetInt64 1) (1L, 1L) "exactly one event and one outbox row from the cadence"
          }

          test "a destination that is no longer the pinned root refuses before preparation and is never recreated after it" {
              // Codex finding 3 on #424: CreateDirectory on a vanished root
              // would recreate it on whatever filesystem is underneath.
              let owner = "fg026b-unpinned-owner"
              let org, claim = runningClaim "fg026b-unpinned" owner
              let authority = EffectAuthority.ofClaim owner claim
              let root = IO.Path.Combine(dropRoot, $"unpinned-{Guid.NewGuid():N}")
              IO.Directory.CreateDirectory root |> ignore
              let marker = IO.Path.Combine(root, EffectProducerConfig.fileDropRootMarker)
              IO.File.WriteAllBytes(marker, [||])
              let invocations = ref 0
              let real = FileDropReceipt.invocation root claim Success
              let counted =
                  { real with
                      Invoke =
                          fun () ->
                              invocations.Value <- invocations.Value + 1
                              real.Invoke() }

              // Unmounted before the effect starts: refused, no ledger row.
              IO.File.Delete marker
              match EffectDispatch.run store authority EffectDispatch.noHooks counted with
              | DispatchOutcome.Refused reason -> Expect.stringContains reason "not pinned" "names the missing marker"
              | other -> failtestf "an unpinned destination was not refused: %A" other
              Expect.equal (ledgerRows org claim.AttemptId) 0L "nothing was prepared"
              Expect.equal invocations.Value 0 "nothing was invoked"

              // Pinned at preparation, gone by invocation: the row stays
              // prepared for the trigger, nothing is written, and the root is
              // not brought back.
              IO.File.WriteAllBytes(marker, [||])
              let vanish () = IO.Directory.Delete(root, true)
              match EffectDispatch.run store authority { EffectDispatch.noHooks with AfterPrepare = vanish } counted with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "not pinned" "the invocation refused the replaced destination"
              | other -> failtestf "a destination lost after preparation was not uncertain: %A" other
              Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("prepared", None)) "the row awaits the trigger"
              Expect.equal invocations.Value 1 "the invocation was attempted once"
              Expect.isFalse (IO.Directory.Exists root) "the connector did not recreate the root"

              // Remounted: the same authority and payload complete through the
              // idempotent connector; the receipt lands on the pinned root.
              IO.Directory.CreateDirectory root |> ignore
              IO.File.WriteAllBytes(marker, [||])
              Expect.equal (EffectDispatch.run store authority EffectDispatch.noHooks counted) (DispatchOutcome.Confirmed false) "confirms once the root is pinned again"
              Expect.isTrue (IO.File.Exists(FileDropReceipt.receiptPath root claim)) "the receipt is on the pinned root"
              Expect.equal (counted.Confirm()) (Ok true) "evidence reads true while pinned"
              IO.File.Delete marker
              Expect.equal (counted.Confirm()) (Ok false) "an unpinned root offers no evidence, whatever bytes it holds"
          }

          test "an oversized pre-written receipt is no evidence: Confirm checks the length on the open descriptor and never reads it" {
              // Codex finding 5 on #424: ReadAllBytes on the predictable path
              // would allocate whatever a same-UID writer left there.
              let owner = "fg026b-oversized-owner"
              let org, claim = runningClaim "fg026b-oversized" owner
              let authority = EffectAuthority.ofClaim owner claim
              let invocations, invocation = counted claim Success
              let path = FileDropReceipt.receiptPath dropRoot claim
              IO.Directory.CreateDirectory(IO.Path.GetDirectoryName path) |> ignore

              // A 3 GiB sparse file: reading it whole would exceed the byte-array
              // limit and surface as an exception, so an evidence read that
              // touches the bytes cannot answer Ok false.
              do
                  use oversized = IO.File.Create path
                  oversized.SetLength(3L * 1024L * 1024L * 1024L)

              let before = GC.GetTotalAllocatedBytes false
              Expect.equal (invocation.Confirm()) (Ok false) "a receipt of the wrong length is not evidence"
              Expect.isLessThan (GC.GetTotalAllocatedBytes false - before) (64L * 1024L * 1024L) "the oversized file was not read into memory"

              match EffectDispatch.run store authority EffectDispatch.noHooks invocation with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "evidence absent" "the outcome is uncertain, never confirmed, and not an exception"
              | other -> failtestf "an oversized pre-written receipt produced %A" other
              Expect.equal invocations.Value 1 "the idempotent write saw the existing file and left it alone"
              Expect.equal (IO.FileInfo(path).Length) (3L * 1024L * 1024L * 1024L) "the pre-written file is untouched"
              Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("applied", None)) "applied, awaiting the trigger"

              // Same length, different bytes: still no evidence.
              IO.File.WriteAllBytes(path, Array.create invocation.Payload.Length 0x20uy)
              Expect.equal (invocation.Confirm()) (Ok false) "equal length with different bytes is refused byte-exactly"
              IO.File.WriteAllBytes(path, invocation.Payload)
              Expect.equal (invocation.Confirm()) (Ok true) "the exact bytes confirm"
          }

          test "a root that vanishes after the pin creates nothing at the configured path" {
              // Codex finding 6 on #424: a recursive CreateDirectory after the pin
              // would rebuild the organization directory on the underlying
              // filesystem. Everything after the pin is relative to the opened
              // root descriptor, so a dead root fails and recreates nothing.
              let owner = "fg026b-descriptor-owner"
              let org, claim = runningClaim "fg026b-descriptor" owner
              let authority = EffectAuthority.ofClaim owner claim
              let root = IO.Path.Combine(dropRoot, $"vanishing-{Guid.NewGuid():N}")
              IO.Directory.CreateDirectory root |> ignore
              IO.File.WriteAllBytes(IO.Path.Combine(root, EffectProducerConfig.fileDropRootMarker), [||])
              let organizationDirectory = IO.Path.Combine(root, org.Value.ToString "N")

              // The pin succeeded (root opened, marker seen through it); the
              // mount then vanishes before anything is created.
              let vanish () = IO.Directory.Delete(root, true)
              let invocation = FileDropReceipt.invocationWith vanish ignore root claim Success

              match EffectDispatch.run store authority EffectDispatch.noHooks invocation with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "pinned destination" "the write failed on the dead descriptor"
              | other -> failtestf "a root lost after the pin produced %A" other
              Expect.isFalse (IO.Directory.Exists root) "the configured path was not recreated"
              Expect.isFalse (IO.Directory.Exists organizationDirectory) "no organization directory exists at the configured path"
              Expect.isFalse (IO.File.Exists(FileDropReceipt.receiptPath root claim)) "no receipt exists anywhere under the configured path"
              Expect.equal (ledgerRow org claim.AttemptId (effectKey claim)) (Some("prepared", None)) "the row awaits the trigger"

              // A symlink standing in for the root is not the pinned root.
              let elsewhere = IO.Path.Combine(dropRoot, $"elsewhere-{Guid.NewGuid():N}")
              IO.Directory.CreateDirectory elsewhere |> ignore
              IO.File.WriteAllBytes(IO.Path.Combine(elsewhere, EffectProducerConfig.fileDropRootMarker), [||])
              IO.File.CreateSymbolicLink(root, elsewhere) |> ignore
              match EffectDispatch.run store authority EffectDispatch.noHooks (FileDropReceipt.invocation root claim Success) with
              | DispatchOutcome.Refused reason -> Expect.stringContains reason "without following links" "a linked root is refused before preparation is touched"
              | other -> failtestf "a symlinked root produced %A" other
              Expect.isFalse (IO.Directory.Exists(IO.Path.Combine(elsewhere, org.Value.ToString "N"))) "nothing was written through the link"
          }

          test "a FIFO planted at the receipt path or the marker path is refused within a bounded time and never blocks the worker" {
              // Codex round 4 (P1): a blocking open of a planted FIFO would park
              // the worker forever. Every read-side open is O_NONBLOCK and is
              // followed by a regular-file check.
              let owner = "fg026b-fifo-owner"
              let org, claim = runningClaim "fg026b-fifo" owner
              let authority = EffectAuthority.ofClaim owner claim
              let root = IO.Path.Combine(dropRoot, $"fifo-{Guid.NewGuid():N}")
              IO.Directory.CreateDirectory root |> ignore
              let marker = IO.Path.Combine(root, EffectProducerConfig.fileDropRootMarker)
              IO.File.WriteAllBytes(marker, [||])
              let invocations = ref 0
              let real = FileDropReceipt.invocation root claim Success
              let counted =
                  { real with
                      Invoke =
                          fun () ->
                              invocations.Value <- invocations.Value + 1
                              real.Invoke() }

              let bounded label (action: unit -> 'a) =
                  let watch = Diagnostics.Stopwatch.StartNew()
                  let result = action ()
                  Expect.isLessThan watch.ElapsedMilliseconds 1000L $"{label} returned within a second"
                  result

              // A FIFO where the receipt should be: the write leaves it alone
              // (idempotent existing entry), Confirm refuses it as not a regular
              // file, the outcome is Uncertain, and nothing waited on a writer.
              let receiptPath = FileDropReceipt.receiptPath root claim
              IO.Directory.CreateDirectory(IO.Path.GetDirectoryName receiptPath) |> ignore
              Expect.equal (mkfifo (receiptPath, 0o600u)) 0 "a FIFO was planted at the receipt path"
              Expect.equal (bounded "Confirm on a FIFO receipt" counted.Confirm) (Ok false) "a FIFO is no evidence"
              match bounded "dispatch over a FIFO receipt" (fun () -> EffectDispatch.run store authority EffectDispatch.noHooks counted) with
              | DispatchOutcome.Uncertain reason -> Expect.stringContains reason "evidence absent" "uncertain, never confirmed"
              | other -> failtestf "a FIFO receipt produced %A" other
              Expect.equal invocations.Value 1 "the write saw the existing entry and did not replace it"
              Expect.isTrue (IO.File.Exists receiptPath) "the planted FIFO is still there: nothing was replaced or unlinked"
              IO.File.Delete receiptPath

              // A FIFO where the marker should be: the pin is refused before
              // preparation, in bounded time, and nothing is invoked.
              IO.File.Delete marker
              Expect.equal (mkfifo (marker, 0o600u)) 0 "a FIFO was planted at the marker path"
              match bounded "pin over a FIFO marker" (fun () -> FileDropReceipt.pinned root) with
              | Error reason -> Expect.stringContains reason "not a regular file" "the marker must be a regular file"
              | Ok() -> failtest "a FIFO marker pinned the root"
              match bounded "dispatch over a FIFO marker" (fun () -> EffectDispatch.run store authority EffectDispatch.noHooks counted) with
              | DispatchOutcome.Refused reason -> Expect.stringContains reason "not a regular file" "refused before anything is touched"
              | other -> failtestf "a FIFO marker produced %A" other
              Expect.equal invocations.Value 1 "nothing was invoked under an unpinned root"

              // A directory or a symlink standing in for the marker is refused
              // the same way.
              IO.File.Delete marker
              IO.Directory.CreateDirectory marker |> ignore
              match FileDropReceipt.pinned root with
              | Error reason -> Expect.stringContains reason "not a regular file" "a directory is not the marker"
              | Ok() -> failtest "a directory marker pinned the root"
              IO.Directory.Delete marker
              let elsewhere = IO.Path.Combine(root, "real-marker")
              IO.File.WriteAllBytes(elsewhere, [||])
              IO.File.CreateSymbolicLink(marker, elsewhere) |> ignore
              match FileDropReceipt.pinned root with
              | Error reason -> Expect.stringContains reason "cannot be opened for reading" "a linked marker is refused without following it"
              | Ok() -> failtest "a symlinked marker pinned the root"
              IO.File.Delete marker
              Expect.equal (link (elsewhere, marker)) 0 "a hard link was planted at the marker path"
              match FileDropReceipt.pinned root with
              | Error reason -> Expect.stringContains reason "more than one link" "a hard-linked marker is refused"
              | Ok() -> failtest "a hard-linked marker pinned the root"
          }

          test "the receipt's directory entry is made durable in order: temp data, rename, organization directory, after the root gained the organization entry" {
              // Codex round 4 (P1): fsync of the temp file alone leaves the
              // rename and the new organization directory undurable while the
              // ledger records applied/confirmed.
              let owner = "fg026b-durable-owner"
              let org, claim = runningClaim "fg026b-durable" owner
              let authority = EffectAuthority.ofClaim owner claim
              let steps = Collections.Generic.List<string>()
              let invocation = FileDropReceipt.invocationWith ignore steps.Add dropRoot claim Success

              Expect.equal (EffectDispatch.run store authority EffectDispatch.noHooks invocation) (DispatchOutcome.Confirmed false) "confirms"
              Expect.equal
                  (List.ofSeq steps)
                  [ "root-fsynced"; "temp-fsynced"; "renamed"; "organization-fsynced" ]
                  "the root is synced after the organization entry, the temp data before the rename, the organization directory after it"

              // A replay drives nothing and syncs nothing.
              steps.Clear()
              Expect.equal (EffectDispatch.run store authority EffectDispatch.noHooks invocation) (DispatchOutcome.Confirmed true) "replay"
              Expect.isEmpty steps "a confirmed replay performs no destination I/O"
          }

          test "startup refuses a drop root or marker that is writable but not readable, by name" {
              // Codex round 4 (P2): the old probe proved write/exec on the root
              // and existence of the marker; dispatch then failed every build on
              // a 0300 root or a 0200 marker. Startup now performs the runtime
              // opens. (Root bypasses permission bits, so the arms are skipped
              // under an effective uid of 0.)
              let stateRoot = IO.Path.Combine(dropRoot, $"unreadable-state-{Guid.NewGuid():N}")
              IO.Directory.CreateDirectory stateRoot |> ignore
              let root = IO.Path.Combine(dropRoot, $"unreadable-{Guid.NewGuid():N}")
              IO.Directory.CreateDirectory root |> ignore
              let marker = IO.Path.Combine(root, EffectProducerConfig.fileDropRootMarker)
              IO.File.WriteAllBytes(marker, [||])
              let restore () =
                  IO.File.SetUnixFileMode(root, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite ||| IO.UnixFileMode.UserExecute)
                  if IO.File.Exists marker then
                      IO.File.SetUnixFileMode(marker, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite)

              try
                  Expect.isOk (EffectProducerConfig.validateFileDropRoot stateRoot root) "a readable, writable, pinned root is accepted"

                  if not (effectiveIdentityIsRoot ()) then
                      IO.File.SetUnixFileMode(root, IO.UnixFileMode.UserWrite ||| IO.UnixFileMode.UserExecute)
                      match EffectProducerConfig.validateFileDropRoot stateRoot root with
                      | Error reason -> Expect.stringContains reason "is not readable as the pinned directory" "a 0300 root is refused by name"
                      | Ok accepted -> failtestf "a 0300 root was accepted: %s" accepted
                      restore ()

                      IO.File.SetUnixFileMode(marker, IO.UnixFileMode.UserWrite)
                      match EffectProducerConfig.validateFileDropRoot stateRoot root with
                      | Error reason -> Expect.stringContains reason "readable regular" "a 0200 marker is refused by name"
                      | Ok accepted -> failtestf "a 0200 marker was accepted: %s" accepted
                      restore ()

                  // An oversized marker is refused at startup even when readable.
                  do
                      use sparse = IO.File.Open(marker, IO.FileMode.Open, IO.FileAccess.Write)
                      sparse.SetLength(DestinationDescriptor.markerMaxBytes + 1L)
                  match EffectProducerConfig.validateFileDropRoot stateRoot root with
                  | Error reason -> Expect.stringContains reason "larger than" "an oversized marker is refused by name"
                  | Ok accepted -> failtestf "an oversized marker was accepted: %s" accepted

                  // A FIFO marker is refused at startup, in bounded time.
                  IO.File.Delete marker
                  Expect.equal (mkfifo (marker, 0o600u)) 0 "a FIFO was planted at the marker path"
                  let watch = Diagnostics.Stopwatch.StartNew()
                  match EffectProducerConfig.validateFileDropRoot stateRoot root with
                  | Error reason -> Expect.stringContains reason "not a regular file" "a FIFO marker is refused by name"
                  | Ok accepted -> failtestf "a FIFO marker was accepted: %s" accepted
                  Expect.isLessThan watch.ElapsedMilliseconds 1000L "startup validation did not block on the FIFO"
              finally
                  restore ()
          }

          test "startup decides drop-root disjointness physically: a symlinked ancestor into the state root is refused, a genuine sibling passes" {
              // Codex round 5 (P2): the lexical StartsWith on GetFullPath cannot
              // see `/srv/alias -> /srv/state` with root `/srv/alias/drop`.
              let area = IO.Path.Combine(dropRoot, $"physical-{Guid.NewGuid():N}")
              let stateRoot = IO.Path.Combine(area, "state")
              let inside = IO.Path.Combine(stateRoot, "drop")
              IO.Directory.CreateDirectory inside |> ignore
              IO.File.WriteAllBytes(IO.Path.Combine(inside, EffectProducerConfig.fileDropRootMarker), [||])
              let alias = IO.Path.Combine(area, "alias")
              IO.Directory.CreateSymbolicLink(alias, stateRoot) |> ignore
              let throughAlias = IO.Path.Combine(alias, "drop")

              Expect.isFalse
                  ((IO.Path.GetFullPath throughAlias).StartsWith(IO.Path.GetFullPath stateRoot + "/"))
                  "the lexical check alone would accept the aliased path"
              match EffectProducerConfig.validateFileDropRoot stateRoot throughAlias with
              | Error reason -> Expect.stringContains reason "physically inside" "the walk finds the state root above the drop root"
              | Ok accepted -> failtestf "a symlinked ancestor into the state root was accepted: %s" accepted

              match EffectProducerConfig.validateFileDropRoot stateRoot stateRoot with
              | Error reason -> Expect.stringContains reason "disjoint" "the state root itself is still refused lexically first"
              | Ok accepted -> failtestf "the state root itself was accepted: %s" accepted

              // The reverse: the drop root physically containing the state root
              // through an alias of the state root.
              let outer = IO.Path.Combine(area, "outer")
              let nestedState = IO.Path.Combine(outer, "nested-state")
              IO.Directory.CreateDirectory nestedState |> ignore
              IO.File.WriteAllBytes(IO.Path.Combine(outer, EffectProducerConfig.fileDropRootMarker), [||])
              let stateAlias = IO.Path.Combine(area, "state-alias")
              IO.Directory.CreateSymbolicLink(stateAlias, nestedState) |> ignore
              match EffectProducerConfig.validateFileDropRoot stateAlias outer with
              | Error reason -> Expect.stringContains reason "physically contains" "the reverse walk finds the drop root above the state root"
              | Ok accepted -> failtestf "a drop root containing the aliased state root was accepted: %s" accepted

              // A genuine sibling passes.
              let sibling = IO.Path.Combine(area, "sibling")
              IO.Directory.CreateDirectory sibling |> ignore
              IO.File.WriteAllBytes(IO.Path.Combine(sibling, EffectProducerConfig.fileDropRootMarker), [||])
              Expect.equal (EffectProducerConfig.validateFileDropRoot stateRoot sibling) (Ok(IO.Path.GetFullPath sibling)) "a disjoint sibling is accepted"
          }

          test "GET effects/uncertain lists one organization's uncertain effects read-only, denies other organizations their view of it, and refuses a malformed organization" {
              let app, baseUrl = startServer 1000

              try
                  use worker = newWorker ()
                  let owner = "local:fg026b-route:owner"
                  let org, project = freshProject ()
                  let claim = admitClaim org project "fg026b-route" owner
                  let authority = EffectAuthority.ofClaim owner claim
                  let invocations, invocation = counted claim Success
                  let key = effectKey claim
                  let url (o: OrganizationId) = $"{baseUrl}/api/v1/organizations/{o.Value}/effects/uncertain"

                  let emptyCode, emptyBody = send HttpMethod.Get (url org) (Some token) None None
                  Expect.equal emptyCode 200 "an organization with nothing uncertain lists an empty set"
                  Expect.equal
                      emptyBody
                      $"{{\"organization_id\":\"{org.Value}\",\"effects\":[],\"next_cursor\":null}}"
                      "the empty listing names the organization and no effects"

                  abortedRun store authority (withHook EffectKillWindow.AfterInvoke) invocation
                  expireLease org claim.AttemptId
                  worker.ScanOrganization(org, Threading.CancellationToken.None).Result |> ignore

                  let code, body = send HttpMethod.Get (url org) (Some token) None None
                  Expect.equal code 200 "the tenant listing is served"
                  use document = JsonDocument.Parse body
                  let root = document.RootElement
                  Expect.equal (root.GetProperty("organization_id").GetString()) (org.Value.ToString()) "the response names the organization"
                  let effects = root.GetProperty("effects").EnumerateArray() |> List.ofSeq
                  Expect.equal effects.Length 1 "exactly one uncertain effect"
                  let effect = effects.Head
                  Expect.equal (effect.GetProperty("attempt_id").GetString()) (claim.AttemptId.Value.ToString()) "attempt_id"
                  Expect.equal (effect.GetProperty("effect_key").GetString()) key "effect_key"
                  Expect.equal (effect.GetProperty("fence").GetInt64()) claim.Fence.Value "fence"
                  Expect.equal (effect.GetProperty("authority_owner").GetString()) owner "authority_owner"
                  Expect.equal (effect.GetProperty("uncertain_from").GetString()) "prepared" "uncertain_from names the window"
                  Expect.equal
                      (effect.GetProperty("payload_sha256").GetString())
                      (Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData invocation.Payload))
                      "payload_sha256 is the digest of the exact prepared bytes"
                  Expect.equal
                      (effect.GetProperty("restore_epoch").GetInt64())
                      (store.CurrentRestoreEpoch().Value)
                      "restore_epoch is the epoch the effect was prepared under"
                  let uncertainAt = DateTimeOffset.Parse(effect.GetProperty("uncertain_at").GetString(), Globalization.CultureInfo.InvariantCulture)
                  Expect.isTrue (uncertainAt.Offset = TimeSpan.Zero && uncertainAt > DateTimeOffset.UtcNow.AddMinutes -5.0) "uncertain_at is a recent UTC instant"
                  Expect.equal (effect.EnumerateObject() |> Seq.length) 8 "the wire shape has exactly the eight documented fields"

                  let otherOrg, _ = freshProject ()
                  let otherCode, otherBody = send HttpMethod.Get (url otherOrg) (Some token) None None
                  Expect.equal otherCode 200 "another organization is served its own view"
                  Expect.equal
                      otherBody
                      $"{{\"organization_id\":\"{otherOrg.Value}\",\"effects\":[],\"next_cursor\":null}}"
                      "another organization sees nothing of this tenant's uncertainty"

                  let malformedCode, malformedBody =
                      send HttpMethod.Get $"{baseUrl}/api/v1/organizations/not-a-uuid/effects/uncertain" (Some token) None None
                  Expect.equal malformedCode 400 "a malformed organization is refused"
                  Expect.stringContains malformedBody "malformed_identifier" "with the stable code"

                  let unauthorizedCode, unauthorizedBody = send HttpMethod.Get (url org) None None None
                  Expect.equal unauthorizedCode 401 "no bearer, no listing"
                  Expect.isFalse (unauthorizedBody.Contains key) "an unauthenticated caller learns no effect key"

                  let postCode, _ = send HttpMethod.Post (url org) (Some token) None (Some "{}")
                  Expect.equal postCode 405 "the surface is read-only: nothing replays, resolves or dismisses through it"

                  // Codex finding 4 on #424: the listing is bounded. A second
                  // stranded effect in the same organization makes two pages
                  // of one.
                  let secondClaim = admitClaim org project "fg026b-route-2" owner
                  let secondInvocations, secondInvocation = counted secondClaim Success
                  abortedRun store (EffectAuthority.ofClaim owner secondClaim) (withHook EffectKillWindow.AfterInvoke) secondInvocation
                  expireLease org secondClaim.AttemptId
                  worker.ScanOrganization(org, Threading.CancellationToken.None).Result |> ignore

                  let firstPageCode, firstPageBody = send HttpMethod.Get $"{url org}?limit=1" (Some token) None None
                  Expect.equal firstPageCode 200 "a bounded page is served"
                  use firstPage = JsonDocument.Parse firstPageBody
                  let firstEffects = firstPage.RootElement.GetProperty("effects").EnumerateArray() |> List.ofSeq
                  Expect.equal firstEffects.Length 1 "limit bounds the page"
                  Expect.equal (firstEffects.Head.GetProperty("attempt_id").GetString()) (claim.AttemptId.Value.ToString()) "the row that entered the uncertain set first comes first"
                  let nextCursor = firstPage.RootElement.GetProperty("next_cursor").GetString()
                  Expect.isNotNull nextCursor "a full page with more behind it carries a cursor"

                  let secondPageCode, secondPageBody =
                      send HttpMethod.Get $"{url org}?limit=1&cursor={Uri.EscapeDataString nextCursor}" (Some token) None None
                  Expect.equal secondPageCode 200 "the cursor continues the listing"
                  use secondPage = JsonDocument.Parse secondPageBody
                  let secondEffects = secondPage.RootElement.GetProperty("effects").EnumerateArray() |> List.ofSeq
                  Expect.equal secondEffects.Length 1 "the second page holds the remaining row"
                  Expect.equal (secondEffects.Head.GetProperty("attempt_id").GetString()) (secondClaim.AttemptId.Value.ToString()) "no row is skipped or repeated"
                  Expect.equal (secondPage.RootElement.GetProperty("next_cursor").ValueKind) JsonValueKind.Null "the last page carries no cursor"

                  let bothCode, bothBody = send HttpMethod.Get $"{url org}?limit=2" (Some token) None None
                  Expect.equal bothCode 200 "a page wide enough lists both"
                  use both = JsonDocument.Parse bothBody
                  Expect.equal (both.RootElement.GetProperty("effects").GetArrayLength()) 2 "both rows on one page"
                  Expect.equal (both.RootElement.GetProperty("next_cursor").ValueKind) JsonValueKind.Null "and no cursor when nothing is behind it"

                  let crossOrgCode, crossOrgBody =
                      send HttpMethod.Get $"{url otherOrg}?cursor={Uri.EscapeDataString nextCursor}" (Some token) None None
                  Expect.equal crossOrgCode 400 "a cursor issued for one organization is refused for another"
                  Expect.stringContains crossOrgBody "invalid_cursor" "with the stable code"
                  Expect.isFalse (crossOrgBody.Contains key) "and leaks nothing"

                  let garbageCode, garbageBody = send HttpMethod.Get $"{url org}?cursor=not-a-cursor" (Some token) None None
                  Expect.equal garbageCode 400 "a malformed cursor is refused"
                  Expect.stringContains garbageBody "invalid_cursor" "with the stable code"

                  // Codex round 5: well-formed base64 with a hostile payload is
                  // the same 400, decided before any database statement.
                  let forged (fields: string list) =
                      Uri.EscapeDataString(
                          Convert.ToBase64String(
                              Text.Encoding.UTF8.GetBytes(String.concat "|" ("fg026b-2" :: org.Value.ToString() :: fields))))
                  let ticks = string DateTime.UtcNow.Ticks
                  for label, cursor in
                      [ "NUL key", forged [ ticks; ticks; claim.AttemptId.Value.ToString(); "file-drop-receipt:\000" ]
                        "oversized key", forged [ ticks; ticks; claim.AttemptId.Value.ToString(); String.replicate 300 "k" ]
                        "non-GUID attempt", forged [ ticks; ticks; "attempt"; key ]
                        "garbage timestamp", forged [ "now"; ticks; claim.AttemptId.Value.ToString(); key ] ] do
                      let tamperedCode, tamperedBody = send HttpMethod.Get $"{url org}?cursor={cursor}" (Some token) None None
                      Expect.equal tamperedCode 400 $"a tampered cursor with {label} is refused"
                      Expect.stringContains tamperedBody "invalid_cursor" $"{label}: with the stable code, not a 500"

                  for badLimit in [ "0"; "1001"; "abc"; "-5" ] do
                      let badCode, badBody = send HttpMethod.Get $"{url org}?limit={badLimit}" (Some token) None None
                      Expect.equal badCode 400 $"limit={badLimit} is refused"
                      Expect.stringContains badBody "invalid_limit" $"limit={badLimit} names the refusal"

                  Expect.equal (invocations.Value, secondInvocations.Value) (1, 1) "listing never invokes"
                  Expect.equal (receiptCount org) 2 "listing never writes: one receipt per stranded attempt"
              finally
                  app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
          }

          test "effect dispatch teardown" {
              if IO.Directory.Exists dropRoot then IO.Directory.Delete(dropRoot, true)
          } ]

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
                          hostFootprint
                          executionLauncherValidation
                          tokenFileIntegrity
                          authorization
                          effectDispatch
                          endpoints ]))
