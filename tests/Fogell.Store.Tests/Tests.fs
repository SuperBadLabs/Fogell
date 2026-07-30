module Fogell.Store.Tests

open System
open Expecto
open Fogell.Domain
open Fogell.Store

/// Real PostgreSQL, never a mock. ADR 0007's properties are database
/// properties: a fake would prove nothing about a unique constraint arbitrating
/// a concurrent race.
let private connectionString =
    match Environment.GetEnvironmentVariable "FOGELL_TEST_DATABASE_URL" with
    | null
    | "" -> "Host=127.0.0.1;Port=55440;Username=fogell;Database=fogell"
    | v -> v

let private available =
    try
        use c = new Npgsql.NpgsqlConnection(connectionString)
        c.Open()
        true
    with _ ->
        false

let private store = Store(connectionString)

let private freshProject () =
    let org = OrganizationId(Guid.NewGuid())
    let project = ProjectId(Guid.NewGuid())
    store.CreateProject(org, $"org-{org.Value}", project, "p")
    org, project

let private newBuild org project key stages =
    { OrganizationId = org
      ProjectId = project
      IdempotencyKey = key
      StageNames = stages
      RequiredTrustPool = "trusted-linux"
      RequiredCapabilities = [ "linux" ] }

let private admitOk input =
    match store.AdmitBuild input with
    | Ok a -> a
    | Error e -> failtestf "admission failed: %s" e

let migrations =
    testList
        "FG-020 migrations"
        [ test "migrate is idempotent and records a checksum" {
              match store.Migrate() with
              | Error e -> failtestf "migrate failed: %s" e
              | Ok first ->
                  Expect.isNonEmpty first "at least one migration"

                  match store.Migrate() with
                  | Error e -> failtestf "second migrate failed: %s" e
                  | Ok second ->
                      Expect.isTrue
                          (second |> List.forall (fun m -> m.AlreadyPresent))
                          "a second run applies nothing"
          }

          test "concurrent controllers install each migration exactly once" {
              // The advisory lock is the point: without it both callers see
              // "not applied" and both apply.
              let results =
                  [ 1..8 ]
                  |> List.map (fun _ -> async { return Store(connectionString).Migrate() })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              Expect.isTrue (results |> Array.forall Result.isOk) "every caller succeeded"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT count(*) FROM schema_migrations WHERE version = '0001'"
              Expect.equal (cmd.ExecuteScalar() :?> int64) 1L "exactly one ledger row"
          } ]

let admission =
    testList
        "FG-021 atomic idempotent admission"
        [ test "admission commits build, node, attempt, event and outbox together" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "k-1" [ "build"; "test" ])
              Expect.isFalse a.WasExisting "first admission"
              Expect.equal a.Number 1 "build numbering starts at 1"
              Expect.equal (store.CountEvents(org, a.BuildId, "build.admitted")) 1 "one event"
              Expect.equal (store.CountOutbox org) 1 "one outbox message"
          }

          test "replaying a key returns the original ids and emits nothing new" {
              let org, project = freshProject ()
              let first = admitOk (newBuild org project "same" [ "build" ])
              let again = admitOk (newBuild org project "same" [ "build" ])
              Expect.isTrue again.WasExisting "recognised as existing"
              Expect.equal again.BuildId first.BuildId "same build"
              Expect.equal again.AttemptId first.AttemptId "same attempt"
              Expect.equal (store.CountEvents(org, first.BuildId, "build.admitted")) 1 "still one event"
              Expect.equal (store.CountOutbox org) 1 "still one outbox row"
          }

          test "16 concurrent submissions of one key produce exactly one build" {
              let org, project = freshProject ()

              let results =
                  [ 1..16 ]
                  |> List.map (fun _ ->
                      async { return Store(connectionString).AdmitBuild(newBuild org project "race" [ "build" ]) })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              let succeeded = results |> Array.choose (function Ok a -> Some a | Error _ -> None)
              Expect.isNonEmpty succeeded "at least one winner"

              let distinct = succeeded |> Array.map (fun a -> a.BuildId) |> Array.distinct
              Expect.equal distinct.Length 1 "all callers agree on one build id"
              Expect.equal (store.CountEvents(org, distinct.[0], "build.admitted")) 1 "exactly one event"
          }

          test "build numbers increment per project" {
              let org, project = freshProject ()
              Expect.equal (admitOk (newBuild org project "n1" [ "b" ])).Number 1 "first"
              Expect.equal (admitOk (newBuild org project "n2" [ "b" ])).Number 2 "second"
              Expect.equal (admitOk (newBuild org project "n3" [ "b" ])).Number 3 "third"
          }

          test "a build with no stages is refused" {
              let org, project = freshProject ()

              match store.AdmitBuild(newBuild org project "empty" []) with
              | Error _ -> ()
              | Ok _ -> failtest "expected refusal"
          }

          test "a blank idempotency key is refused" {
              let org, project = freshProject ()

              match store.AdmitBuild(newBuild org project "  " [ "b" ]) with
              | Error _ -> ()
              | Ok _ -> failtest "expected refusal"
          } ]

let fencing =
    testList
        "FG-022 fences and publication"
        [ test "the exact current holder may publish" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "pub-1" [ "b" ])

              match store.OfferAttempt(org, a.AttemptId, "agent-a", 60) with
              | Error e -> failtestf "offer failed: %s" e
              | Ok fence ->
                  Expect.isTrue (store.AcceptAttempt(org, a.AttemptId, fence, "agent-a")) "accepted"

                  match store.PublishTerminal(org, a.AttemptId, fence, "agent-a", Success) with
                  | Ok() ->
                      match store.AttemptState(org, a.AttemptId) with
                      | Some(state, _, result) ->
                          Expect.equal state "terminal" "terminal state"
                          Expect.equal result (Some "success") "result recorded"
                      | None -> failtest "attempt vanished"
                  | Error e -> failtestf "publication refused: %s" e
          }

          test "exactly one winner among 16 concurrent publishers" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "race-pub" [ "b" ])
              let fence = match store.OfferAttempt(org, a.AttemptId, "agent-a", 60) with
                          | Ok f -> f
                          | Error e -> failtestf "offer failed: %s" e
              store.AcceptAttempt(org, a.AttemptId, fence, "agent-a") |> ignore

              let results =
                  [ 1..16 ]
                  |> List.map (fun _ ->
                      async {
                          return Store(connectionString).PublishTerminal(org, a.AttemptId, fence, "agent-a", Success)
                      })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              let winners = results |> Array.filter Result.isOk |> Array.length
              Expect.equal winners 1 "exactly one publication succeeds"
              Expect.equal (store.CountEvents(org, a.BuildId, "attempt.terminal")) 1 "exactly one terminal event"
          }

          test "a stale fence is refused" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "stale" [ "b" ])
              let first = match store.OfferAttempt(org, a.AttemptId, "agent-a", 60) with
                          | Ok f -> f
                          | Error e -> failtestf "%s" e
              // a second offer bumps the fence; the first holder is now stale
              store.OfferAttempt(org, a.AttemptId, "agent-b", 60) |> ignore

              match store.PublishTerminal(org, a.AttemptId, first, "agent-a", Success) with
              | Error _ -> ()
              | Ok() -> failtest "a stale fence must not publish"
          }

          test "the wrong owner is refused even with the right fence" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "owner" [ "b" ])
              let fence = match store.OfferAttempt(org, a.AttemptId, "agent-a", 60) with
                          | Ok f -> f
                          | Error e -> failtestf "%s" e

              match store.PublishTerminal(org, a.AttemptId, fence, "agent-b", Success) with
              | Error _ -> ()
              | Ok() -> failtest "wrong owner must not publish"
          }

          test "an expired lease is refused (clock_timestamp, not now)" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "expiry" [ "b" ])
              // a zero-second lease is already expired by the time we publish
              let fence = match store.OfferAttempt(org, a.AttemptId, "agent-a", 0) with
                          | Ok f -> f
                          | Error e -> failtestf "%s" e
              System.Threading.Thread.Sleep 50

              match store.PublishTerminal(org, a.AttemptId, fence, "agent-a", Success) with
              | Error _ -> ()
              | Ok() -> failtest "an expired lease must not publish"
          }

          test "a restore invalidates pre-restore authority" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "restore" [ "b" ])
              let fence = match store.OfferAttempt(org, a.AttemptId, "agent-a", 300) with
                          | Ok f -> f
                          | Error e -> failtestf "%s" e
              store.AcceptAttempt(org, a.AttemptId, fence, "agent-a") |> ignore

              let before = store.CurrentRestoreEpoch()
              let after = store.ActivateRestore()
              Expect.isGreaterThan after.Value before.Value "epoch advanced"

              match store.PublishTerminal(org, a.AttemptId, fence, "agent-a", Success) with
              | Error _ -> ()
              | Ok() -> failtest "a pre-restore agent must not publish"

              match store.AttemptState(org, a.AttemptId) with
              | Some(state, _, _) ->
                  Expect.equal state "reconciliation_required" "attempt awaits reconciliation"
              | None -> failtest "attempt vanished"
          }

          test "publishing twice is refused the second time" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "twice" [ "b" ])
              let fence = match store.OfferAttempt(org, a.AttemptId, "agent-a", 60) with
                          | Ok f -> f
                          | Error e -> failtestf "%s" e
              store.AcceptAttempt(org, a.AttemptId, fence, "agent-a") |> ignore
              Expect.isTrue (Result.isOk (store.PublishTerminal(org, a.AttemptId, fence, "agent-a", Success))) "first"

              match store.PublishTerminal(org, a.AttemptId, fence, "agent-a", Failure) with
              | Error _ -> ()
              | Ok() -> failtest "terminal is final"
          }

          test "each offer increments the fence" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "fences" [ "b" ])
              let f1 = match store.OfferAttempt(org, a.AttemptId, "agent-a", 60) with Ok f -> f | Error e -> failtestf "%s" e
              let f2 = match store.OfferAttempt(org, a.AttemptId, "agent-b", 60) with Ok f -> f | Error e -> failtestf "%s" e
              Expect.equal f2.Value (f1.Value + 1L) "monotonic"
          } ]

[<EntryPoint>]
let main argv =
    if not available then
        eprintfn "skipped: no PostgreSQL at %s (set FOGELL_TEST_DATABASE_URL)" connectionString
        0
    else
        match store.Migrate() with
        | Error e ->
            eprintfn $"migrate failed: {e}"
            1
        | Ok _ ->
            // These tests share one database, and ActivateRestore bumps a GLOBAL
            // epoch row that every other attempt is validated against. Run
            // sequentially: in parallel, the restore test invalidates the
            // concurrent-publisher test's lease mid-flight and it sees zero
            // winners instead of one. The store is correct; the harness was not.
            runTestsWithCLIArgs
                []
                argv
                (testSequenced (testList "Fogell.Store" [ migrations; admission; fencing ]))
