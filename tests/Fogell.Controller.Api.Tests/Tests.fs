module Fogell.Controller.Api.Tests

open System
open System.Net
open System.Net.Http
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

          test "a malformed project is 400 on status, logs and cancellation" {
              let org = Guid.NewGuid()
              let build = Guid.NewGuid()
              let root = $"{baseUrl}/api/v1/organizations/{org}/projects/not-a-project/builds/{build}"

              for method, route in
                  [ HttpMethod.Get, root
                    HttpMethod.Get, $"{root}/logs"
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

          test "teardown" { app.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously }
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
            runTestsWithCLIArgs [] argv (testSequenced (testList "Fogell.Controller.Api" [ executionLauncherValidation; authorization; endpoints ]))
