module Fogell.Controller.Api.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Fogell.Domain
open Fogell.Store
open Fogell.Controller.Api

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

let private startServer () =
    let auth =
        match Authorization.configure token with
        | Ok c -> c
        | Error e -> failwith e

    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
    builder.Logging.ClearProviders() |> ignore
    let app = builder.Build()

    Router.map
        { Store = store
          Auth = auth
          DefaultTrustPool = "trusted-linux" }
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

let private send (method: HttpMethod) (url: string) (bearer: string option) (idem: string option) (body: string option) =
    let req = new HttpRequestMessage(method, url)
    bearer |> Option.iter (fun t -> req.Headers.TryAddWithoutValidation("authorization", $"Bearer {t}") |> ignore)
    idem |> Option.iter (fun k -> req.Headers.TryAddWithoutValidation("idempotency-key", k) |> ignore)
    body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/x-jenkinsfile"))
    let r = client.Send req
    let text = r.Content.ReadAsStringAsync().Result
    int r.StatusCode, text

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
    let app, baseUrl = startServer ()

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

          test "a missing idempotency key is refused with a named code" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let code, body = send HttpMethod.Post url (Some token) None (Some pipeline)
              Expect.equal code 400 "refused"
              Expect.stringContains body "idempotency_key_required" "named code"
          }

          test "a malformed pipeline is rejected with its code AND source position" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let code, body = send HttpMethod.Post url (Some token) (Some "bad-1") (Some "node { sh 'make' }")
              Expect.equal code 422 "unprocessable"
              Expect.stringContains body "no_pipeline_block" "named code"
              Expect.stringContains body "\"position\"" "carries a position"
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

          // Corrected expectation: my first version asserted 409 on a repeat
          // cancel. That is wrong — a retried cancellation after a client timeout
          // must not look like an error, since the requested state is already the
          // actual state. A real conflict is cancelling a FINISHED build.
          test "cancel is idempotent; cancelling a finished build conflicts" {
              let org, project = freshProject ()
              let url = $"{baseUrl}/api/v1/organizations/{org.Value}/projects/{project.Value}/builds"
              let _, body = send HttpMethod.Post url (Some token) (Some "can-1") (Some pipeline)
              let buildId = Text.RegularExpressions.Regex.Match(body, "\"build_id\":\"([^\"]+)\"").Groups[1].Value

              let code1, b1 = send HttpMethod.Post $"{url}/{buildId}/cancel" (Some token) None None
              Expect.equal code1 202 "accepted"
              Expect.stringContains b1 "\"already_requested\":false" "first request"

              let code2, b2 = send HttpMethod.Post $"{url}/{buildId}/cancel" (Some token) None None
              Expect.equal code2 202 "a retry is idempotent, not an error"
              Expect.stringContains b2 "\"already_requested\":true" "reports that it was already requested"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use terminal = conn.CreateCommand()
              terminal.CommandText <-
                  "UPDATE builds SET status = 'succeeded', cancellation_requested = false
                    WHERE organization_id = @o AND project_id = @p AND id = @b"
              terminal.Parameters.AddWithValue("o", org.Value) |> ignore
              terminal.Parameters.AddWithValue("p", project.Value) |> ignore
              terminal.Parameters.AddWithValue("b", Guid.Parse buildId) |> ignore
              Expect.equal (terminal.ExecuteNonQuery()) 1 "terminal control updates the exact build"

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
            runTestsWithCLIArgs [] argv (testSequenced (testList "Fogell.Controller.Api" [ authorization; endpoints ]))
