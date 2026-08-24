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

let private readLog org project build fromSequence =
    match store.ReadLog(org, project, build, fromSequence) with
    | Some chunks -> chunks
    | None -> failtest "expected the project-qualified build lineage to exist"

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

/// FG-061. Scheduling is where a cross-tenant leak or a double-claim would do
/// real damage, so every property here is tested against the live database.
let scheduling =
    testList
        "FG-061 scheduler"
        [ test "an attempt is claimed once and only once" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "claim-1" [ "b" ])

              match store.ClaimNext(org, "agent-a", "trusted-linux", [ "linux" ], 60) with
              | Ok(Some(attemptId, _, buildId, fence)) ->
                  Expect.equal attemptId a.AttemptId "the queued attempt"
                  Expect.equal buildId a.BuildId "its build"
                  Expect.isGreaterThan fence.Value 0L "fence was incremented by the offer"
              | other -> failtestf "expected a claim, got %A" other

              // second claim finds nothing: the attempt is no longer queued
              match store.ClaimNext(org, "agent-b", "trusted-linux", [ "linux" ], 60) with
              | Ok None -> ()
              | other -> failtestf "expected no second claim, got %A" other
          }

          test "16 concurrent schedulers never hand out the same attempt twice" {
              let org, project = freshProject ()

              let admitted =
                  [ 1..4 ]
                  |> List.map (fun i -> admitOk (newBuild org project $"conc-{i}" [ "b" ]))

              let claims =
                  [ 1..16 ]
                  |> List.map (fun i ->
                      async {
                          return Store(connectionString).ClaimNext(org, $"agent-{i}", "trusted-linux", [ "linux" ], 60)
                      })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              let won =
                  claims
                  |> Array.choose (function
                      | Ok(Some(a, _, _, _)) -> Some a
                      | _ -> None)

              Expect.equal won.Length admitted.Length "exactly as many claims as attempts"
              Expect.equal (Array.distinct won).Length won.Length "no attempt claimed twice"
          }

          test "a capability mismatch is not claimable" {
              let org, project = freshProject ()

              admitOk
                  { newBuild org project "caps" [ "b" ] with
                      RequiredCapabilities = [ "linux"; "gpu" ] }
              |> ignore

              match store.ClaimNext(org, "agent-a", "trusted-linux", [ "linux" ], 60) with
              | Ok None -> ()
              | other -> failtestf "an agent without 'gpu' must not claim, got %A" other

              match store.ClaimNext(org, "agent-a", "trusted-linux", [ "linux"; "gpu" ], 60) with
              | Ok(Some _) -> ()
              | other -> failtestf "an agent with both capabilities must claim, got %A" other
          }

          test "a trust-pool mismatch is not claimable" {
              let org, project = freshProject ()

              admitOk
                  { newBuild org project "pool" [ "b" ] with
                      RequiredTrustPool = "trusted-windows" }
              |> ignore

              match store.ClaimNext(org, "agent-a", "trusted-linux", [ "linux" ], 60) with
              | Ok None -> ()
              | other -> failtestf "wrong pool must not claim, got %A" other
          }

          test "claims are FIFO by admission order" {
              let org, project = freshProject ()
              let first = admitOk (newBuild org project "fifo-1" [ "b" ])
              System.Threading.Thread.Sleep 20
              let second = admitOk (newBuild org project "fifo-2" [ "b" ])

              let claim () =
                  match store.ClaimNext(org, "agent-a", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some(a, _, _, _)) -> a
                  | other -> failtestf "expected a claim, got %A" other

              Expect.equal (claim ()) first.AttemptId "oldest first"
              Expect.equal (claim ()) second.AttemptId "then the next"
          }

          test "wait diagnostics distinguish empty from capability mismatch" {
              let org, project = freshProject ()
              Expect.stringContains (store.ExplainWait(org, "trusted-linux", [ "linux" ])) "empty" "empty queue"

              admitOk
                  { newBuild org project "diag" [ "b" ] with
                      RequiredCapabilities = [ "linux"; "gpu" ] }
              |> ignore

              let mismatch = store.ExplainWait(org, "trusted-linux", [ "linux" ])
              Expect.stringContains mismatch "gpu" "names the missing capability"

              let ok = store.ExplainWait(org, "trusted-linux", [ "linux"; "gpu" ])
              Expect.stringContains ok "claimable" "reports claimable work"
          }

          test "wait diagnostics name a trust-pool mismatch" {
              let org, project = freshProject ()

              admitOk
                  { newBuild org project "poolmsg" [ "b" ] with
                      RequiredTrustPool = "trusted-windows" }
              |> ignore

              Expect.stringContains
                  (store.ExplainWait(org, "trusted-linux", [ "linux" ]))
                  "trust pool"
                  "explains the pool mismatch"
          } ]

/// FG-064. A client must be able to tail a running build.
let logs =
    testList
        "FG-064 progressive log"
        [ test "log chunks are readable from an offset while the build runs" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "log-1" [ "b" ])

              for i in 0..4 do
                  Expect.isTrue (store.AppendLog(org, a.BuildId, a.AttemptId, i, $"line-{i}")) $"append {i}"

              Expect.equal (readLog org project a.BuildId 0 |> List.length) 5 "all five"
              Expect.equal (readLog org project a.BuildId 3 |> List.length) 2 "tail from offset"
              Expect.equal (readLog org project a.BuildId 0 |> List.map snd |> List.head) "line-0" "ordered"
          }

          test "appending the same sequence twice is idempotent" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "log-2" [ "b" ])
              Expect.isTrue (store.AppendLog(org, a.BuildId, a.AttemptId, 0, "once")) "first"
              Expect.isFalse (store.AppendLog(org, a.BuildId, a.AttemptId, 0, "again")) "duplicate rejected"
              Expect.equal (readLog org project a.BuildId 0 |> List.length) 1 "still one chunk"
          }

          test "an attempt cannot append to another build or consume its real sequence" {
              let org, project = freshProject ()
              let first = admitOk (newBuild org project "log-bound-first" [ "b" ])
              let second = admitOk (newBuild org project "log-bound-second" [ "b" ])

              Expect.isFalse
                  (store.AppendLog(org, second.BuildId, first.AttemptId, 7, "poison"))
                  "wrong-build append is rejected"

              Expect.isEmpty (readLog org project second.BuildId 0) "wrong build received no line"

              Expect.isTrue
                  (store.AppendLog(org, first.BuildId, first.AttemptId, 7, "real"))
                  "the rejected append did not consume the attempt's sequence"

              Expect.equal (readLog org project first.BuildId 0) [ 7, "real" ] "correct lineage appends exactly once"
          }

          test "a nonexistent attempt cannot append and a valid attempt still can at the same sequence" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "log-bound-missing" [ "b" ])
              let missing = AttemptId(Guid.NewGuid())

              Expect.isFalse
                  (store.AppendLog(org, admitted.BuildId, missing, 11, "invented"))
                  "an unregistered attempt is rejected"

              Expect.isEmpty (readLog org project admitted.BuildId 0) "invented attempt produced no line"

              Expect.isTrue
                  (store.AppendLog(org, admitted.BuildId, admitted.AttemptId, 11, "registered"))
                  "the real attempt can use the same sequence"

              Expect.equal
                  (readLog org project admitted.BuildId 0)
                  [ 11, "registered" ]
                  "only the registered attempt's line is visible"
          }

          test "an attempt from another tenant cannot append to a valid local build" {
              let firstOrg, firstProject = freshProject ()
              let secondOrg, secondProject = freshProject ()
              let foreign = admitOk (newBuild firstOrg firstProject "log-bound-foreign" [ "b" ])
              let local = admitOk (newBuild secondOrg secondProject "log-bound-local" [ "b" ])

              Expect.isFalse
                  (store.AppendLog(secondOrg, foreign.BuildId, foreign.AttemptId, 17, "wrong-org"))
                  "a valid build-attempt lineage cannot be claimed by another organization"

              Expect.isTrue
                  (store.AppendLog(firstOrg, foreign.BuildId, foreign.AttemptId, 17, "foreign-owner"))
                  "the rejected tenant claim did not consume the real lineage's sequence"

              Expect.equal
                  (readLog firstOrg firstProject foreign.BuildId 0)
                  [ 17, "foreign-owner" ]
                  "the owning organization can append"

              Expect.isFalse
                  (store.AppendLog(secondOrg, local.BuildId, foreign.AttemptId, 13, "foreign"))
                  "the organization and attempt must belong to one lineage"

              Expect.isEmpty (readLog secondOrg secondProject local.BuildId 0) "foreign attempt produced no local line"

              Expect.isTrue
                  (store.AppendLog(secondOrg, local.BuildId, local.AttemptId, 13, "local"))
                  "the local lineage remains writable at that sequence"

              Expect.equal (readLog secondOrg secondProject local.BuildId 0) [ 13, "local" ] "only local data is visible"
          }

          test "composite node and attempt identities cannot splice a cross-tenant lineage" {
              let ownerOrg, ownerProject = freshProject ()
              let source = admitOk (newBuild ownerOrg ownerProject "log-composite-source" [ "b" ])
              let target = admitOk (newBuild ownerOrg ownerProject "log-composite-target" [ "b" ])
              let otherOrg, otherProject = freshProject ()

              // IDs are composite with organization_id, not globally unique. Build a
              // hostile but schema-valid second lineage that reuses all three UUIDs.
              // A join on UUID alone can splice it into the owner's request.
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use tx = conn.BeginTransaction()

              use build = conn.CreateCommand()
              build.Transaction <- tx
              build.CommandText <-
                  "INSERT INTO builds
                     (id, organization_id, project_id, number, idempotency_key, status)
                   VALUES (@b, @o, @p, 1, @key, 'queued')"
              build.Parameters.AddWithValue("b", target.BuildId.Value) |> ignore
              build.Parameters.AddWithValue("o", otherOrg.Value) |> ignore
              build.Parameters.AddWithValue("p", otherProject.Value) |> ignore
              build.Parameters.AddWithValue("key", "log-composite-hostile") |> ignore
              build.ExecuteNonQuery() |> ignore

              use node = conn.CreateCommand()
              node.Transaction <- tx
              node.CommandText <-
                  "INSERT INTO nodes
                     (id, organization_id, build_id, name, ordinal, required_trust_pool, required_capabilities, status)
                   VALUES (@n, @o, @b, 'hostile', 0, 'trusted-linux', ARRAY['linux'], 'queued')"
              node.Parameters.AddWithValue("n", source.NodeId.Value) |> ignore
              node.Parameters.AddWithValue("o", otherOrg.Value) |> ignore
              node.Parameters.AddWithValue("b", target.BuildId.Value) |> ignore
              node.ExecuteNonQuery() |> ignore

              use attempt = conn.CreateCommand()
              attempt.Transaction <- tx
              attempt.CommandText <-
                  "INSERT INTO attempts
                     (id, organization_id, node_id, ordinal, state, restore_epoch)
                   SELECT @a, @o, @n, 0, 'queued', restore_epoch
                   FROM controller_metadata WHERE singleton"
              attempt.Parameters.AddWithValue("a", source.AttemptId.Value) |> ignore
              attempt.Parameters.AddWithValue("o", otherOrg.Value) |> ignore
              attempt.Parameters.AddWithValue("n", source.NodeId.Value) |> ignore
              attempt.ExecuteNonQuery() |> ignore
              tx.Commit()

              Expect.isFalse
                  (store.AppendLog(ownerOrg, target.BuildId, source.AttemptId, 19, "spliced"))
                  "composite identities cannot be joined through another organization"

              Expect.isEmpty (readLog ownerOrg ownerProject target.BuildId 0) "target build received no spliced line"

              Expect.isTrue
                  (store.AppendLog(ownerOrg, source.BuildId, source.AttemptId, 19, "owned"))
                  "the rejected splice did not consume the owner's sequence"

              Expect.equal (readLog ownerOrg ownerProject source.BuildId 0) [ 19, "owned" ] "owner lineage remains exact"
          }

          test "status, logs and cancellation require the exact project lineage" {
              let org, ownerProject = freshProject ()
              let wrongProject = ProjectId(Guid.NewGuid())
              store.CreateProject(org, $"org-{org.Value}", wrongProject, "wrong")
              let admitted = admitOk (newBuild org ownerProject "project-bound" [ "b" ])

              Expect.equal
                  (store.BuildSnapshot(org, wrongProject, admitted.BuildId))
                  None
                  "status cannot cross a valid project boundary"

              Expect.equal
                  (store.ReadLog(org, wrongProject, admitted.BuildId, 0))
                  None
                  "logs cannot cross a valid project boundary"

              Expect.equal
                  (store.ReadLog(org, ownerProject, admitted.BuildId, 0))
                  (Some [])
                  "a real build with no log is distinct from a missing lineage"

              Expect.equal
                  (store.ReadLog(org, ownerProject, BuildId(Guid.NewGuid()), 0))
                  None
                  "an unknown build is not an empty log"

              Expect.equal
                  (store.RequestCancellation(org, wrongProject, admitted.BuildId))
                  NoSuchBuild
                  "wrong-project cancellation is rejected"

              match store.BuildSnapshot(org, ownerProject, admitted.BuildId) with
              | Some(_, requested) -> Expect.isFalse requested "wrong route had no cancellation side effect"
              | None -> failtest "owner snapshot missing"

              Expect.isTrue
                  (store.AppendLog(org, admitted.BuildId, admitted.AttemptId, 2, "two"))
                  "first sparse chunk appended"

              Expect.isTrue
                  (store.AppendLog(org, admitted.BuildId, admitted.AttemptId, 4, "four"))
                  "second sparse chunk appended"

              Expect.equal
                  (store.ReadLog(org, ownerProject, admitted.BuildId, 3))
                  (Some [ 4, "four" ])
                  "qualified reads preserve the offset and cursor input"

              Expect.equal
                  (store.ReadLog(org, ownerProject, admitted.BuildId, 5))
                  (Some [])
                  "a real build remains distinguishable past its final chunk"

              Expect.equal
                  (store.RequestCancellation(org, ownerProject, admitted.BuildId))
                  CancellationAccepted
                  "correct project route remains cancellable"
          }

          test "cancellation is recorded and visible in the snapshot" {
              let org, project = freshProject ()
              let a = admitOk (newBuild org project "cancel" [ "b" ])

              match store.BuildSnapshot(org, project, a.BuildId) with
              | Some(_, requested) -> Expect.isFalse requested "not requested yet"
              | None -> failtest "snapshot missing"

              Expect.equal (store.RequestCancellation(org, project, a.BuildId)) CancellationAccepted "recorded"
              Expect.equal
                  (store.RequestCancellation(org, project, a.BuildId))
                  AlreadyRequested
                  "a retry is idempotent, not an error"

              match store.BuildSnapshot(org, project, a.BuildId) with
              | Some(_, requested) -> Expect.isTrue requested "now requested"
              | None -> failtest "snapshot missing"
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
                (testSequenced (testList "Fogell.Store" [ migrations; admission; fencing; scheduling; logs ]))
