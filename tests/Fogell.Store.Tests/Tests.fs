module Fogell.Store.Tests

open System
open System.Security.Cryptography
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

let private tenantTables =
    [ "organizations", "id"
      "projects", "organization_id"
      "builds", "organization_id"
      "nodes", "organization_id"
      "attempts", "organization_id"
      "events", "organization_id"
      "outbox", "organization_id"
      "log_chunks", "organization_id"
      "effect_checkpoints", "organization_id"
      "retry_decisions", "organization_id"
      "build_definitions", "organization_id" ]

let private freshProject () =
    let org = OrganizationId(Guid.NewGuid())
    let project = ProjectId(Guid.NewGuid())
    store.CreateProject(org, $"org-{org.Value}", project, "p")
    org, project

let private newBuild org project key stages =
    let stageList = String.concat "," stages
    let source = $"pipeline:{stageList}"

    { OrganizationId = org
      ProjectId = project
      IdempotencyKey = key
      PipelineSource = Text.Encoding.UTF8.GetBytes source
      StageNames = stages
      RequiredTrustPool = "trusted-linux"
      RequiredCapabilities = [ "linux" ] }

let private admitOk input =
    match store.AdmitBuild input with
    | Ok a -> a
    | Error e -> failtestf "admission failed: %s" e

let private runningAttempt org project key owner leaseSeconds =
    let admitted = admitOk (newBuild org project key [ "effect" ])

    let fence =
        match store.OfferAttempt(org, admitted.AttemptId, owner, leaseSeconds) with
        | Ok value -> value
        | Error error -> failtestf "offer failed: %s" error

    if leaseSeconds > 0 then
        Expect.isTrue
            (store.AcceptAttempt(org, admitted.AttemptId, fence, owner))
            "effect attempt accepted"

    admitted, fence

let private prepareEffectOk org attempt fence owner effectKey payload =
    match store.PrepareEffect(org, attempt, fence, owner, effectKey, payload) with
    | Ok outcome -> outcome
    | Error error -> failtestf "effect preparation failed: %s" error

let private advanceEffectOk org attempt fence owner effectKey payload advance =
    match store.AdvanceEffect(org, attempt, fence, owner, effectKey, payload, advance) with
    | Ok outcome -> outcome
    | Error error -> failtestf "effect advancement failed: %s" error

let private expectError message result =
    match result with
    | Error _ -> ()
    | Ok value -> failtestf "%s; unexpectedly succeeded with %A" message value

let private checkpointCount (org: OrganizationId) (attempt: AttemptId) =
    use conn = new Npgsql.NpgsqlConnection(connectionString)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "SELECT count(*) FROM effect_checkpoints
         WHERE organization_id = @o AND attempt_id = @a"
    cmd.Parameters.AddWithValue("o", org.Value) |> ignore
    cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
    cmd.ExecuteScalar() :?> int64 |> int

let private checkpointCountForOrg (org: OrganizationId) =
    use conn = new Npgsql.NpgsqlConnection(connectionString)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT count(*) FROM effect_checkpoints WHERE organization_id = @o"
    cmd.Parameters.AddWithValue("o", org.Value) |> ignore
    cmd.ExecuteScalar() :?> int64 |> int

let private terminalAttempt org project key =
    let admitted = admitOk (newBuild org project key [ "retry" ])

    let fence =
        match store.OfferAttempt(org, admitted.AttemptId, $"retry-agent-{key}", 60) with
        | Ok value -> value
        | Error error -> failtestf "offer failed: %s" error

    Expect.isTrue
        (store.AcceptAttempt(org, admitted.AttemptId, fence, $"retry-agent-{key}"))
        "retry parent accepted"

    match
        store.PublishTerminal(
            org,
            admitted.AttemptId,
            fence,
            $"retry-agent-{key}",
            Failure)
    with
    | Ok() -> admitted
    | Error error -> failtestf "terminal publication failed: %s" error

let private decideRetryOk org parent limit child =
    match store.DecideRetry(org, parent, limit, child) with
    | Ok outcome -> outcome
    | Error error -> failtestf "retry decision failed: %A" error

let private retryDecisionCount (org: OrganizationId) (parent: AttemptId) =
    use conn = new Npgsql.NpgsqlConnection(connectionString)
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "SELECT count(*) FROM retry_decisions
         WHERE organization_id = @o AND parent_attempt_id = @a"
    cmd.Parameters.AddWithValue("o", org.Value) |> ignore
    cmd.Parameters.AddWithValue("a", parent.Value) |> ignore
    cmd.ExecuteScalar() :?> int64 |> int

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
          }

          test "runtime and maintenance capabilities prove the same live database" {
              let aliasBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              aliasBuilder.ApplicationName <- $"fogell-pair-alias-{Guid.NewGuid():N}"

              Expect.isTrue
                  (Store(aliasBuilder.ConnectionString, connectionString).DatabasePairMatches())
                  "connection-string aliases to one database are accepted"

              let constrainedBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              constrainedBuilder.MaxPoolSize <- 1
              constrainedBuilder.Timeout <- 1
              Expect.isTrue
                  (Store(constrainedBuilder.ConnectionString, constrainedBuilder.ConnectionString).DatabasePairMatches())
                  "a caller's one-connector pool cannot starve the two-session proof"

              let multiplexedBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              multiplexedBuilder.Multiplexing <- true
              Expect.isTrue
                  (Store(multiplexedBuilder.ConnectionString, multiplexedBuilder.ConnectionString).DatabasePairMatches())
                  "caller multiplexing cannot erase transaction advisory-lock identity"

              let concurrent =
                  [ 1..16 ]
                  |> List.map (fun _ ->
                      async {
                          return Store(connectionString, aliasBuilder.ConnectionString).DatabasePairMatches()
                      })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              Expect.isTrue
                  (concurrent |> Array.forall id)
                  "concurrent startup probes against one database cannot reject each other"

              let adminBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              adminBuilder.Database <- "postgres"
              let otherName = $"fogell_pair_{Guid.NewGuid():N}"
              let otherBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              otherBuilder.Database <- otherName

              use admin = new Npgsql.NpgsqlConnection(adminBuilder.ConnectionString)
              admin.Open()
              use create = admin.CreateCommand()
              create.CommandText <- $"CREATE DATABASE {otherName}"
              create.ExecuteNonQuery() |> ignore

              try
                  Expect.isTrue
                      (Store(otherBuilder.ConnectionString, otherBuilder.ConnectionString).DatabasePairMatches())
                      "the pair proof is schema-independent on an empty database"

                  use empty = new Npgsql.NpgsqlConnection(otherBuilder.ConnectionString)
                  empty.Open()
                  use inspectEmpty = empty.CreateCommand()
                  inspectEmpty.CommandText <- "SELECT to_regclass('public.schema_migrations') IS NULL"
                  Expect.isTrue
                      (inspectEmpty.ExecuteScalar() :?> bool)
                      "the pair proof itself creates no schema"

                  match Store(otherBuilder.ConnectionString).Migrate() with
                  | Error why -> failtestf "other database migration failed: %s" why
                  | Ok _ -> ()

                  Expect.isFalse
                      (Store(connectionString, otherBuilder.ConnectionString).DatabasePairMatches())
                      "two fully migrated databases are not mistaken for one target"
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
          }

          test "effect ledger migration 0003 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0003'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0003 row"
              | value ->
                  Expect.equal
                      (string value)
                      "1ce854dd3de720521eac6afc7322f2b098b644f07460a959b0df8edcf9f319c6"
                      "migration 0003 exact source checksum"
          }

          test "retry decision migration 0004 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0004'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0004 row"
              | value ->
                  Expect.equal
                      (string value)
                      "cea314ca6fdbb18dd5fea9d3edb1efff2c9d61acee9bec4f68d4048c2e980096"
                      "migration 0004 exact source checksum"
          }

          test "tenant isolation migration 0005 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0005'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0005 row"
              | value ->
                  Expect.equal
                      (string value)
                      "af4f5fadfbccfdbba78b785753386b09f9385e1ac88167d18960b03d9635f920"
                      "migration 0005 exact source checksum"
          }

          test "runnable controller migration 0006 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0006'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0006 row"
              | value ->
                  Expect.equal
                      (string value)
                      "26daeb1153b1a71b945bc897aee2ce1dba9994bc53aa1bdf206e416aa284a7aa"
                      "migration 0006 exact source checksum"
          }

          test "build-wide log cursor migration 0008 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0008'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0008 row"
              | value ->
                  Expect.equal
                      (string value)
                      "a164a5a46fe18d9712508bb7222016badb9fef4cd51c4516662b591a912bede9"
                      "migration 0008 exact source checksum"
          }

          test "organization root repair migration 0009 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0009'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0009 row"
              | value ->
                  Expect.equal
                      (string value)
                      "ceae309c185b96abe221bbfba8dddb7783de80f8b3c393543253886f89397bf7"
                      "migration 0009 exact source checksum"
          }

          test "attempt restore epoch guard migration 0010 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0010'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0010 row"
              | value ->
                  Expect.equal
                      (string value)
                      "9e5c3641ba7a4b4f0c62f0fc91c4bbc0d4acebae7987c5bc70048a6a95927ecb"
                      "migration 0010 exact source checksum"
          }

          test "retry lineage reopening migration 0011 is checksum-pinned" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = '0011'"

              match cmd.ExecuteScalar() with
              | null -> failtest "migration ledger has no 0011 row"
              | value ->
                  Expect.equal
                      (string value)
                      "16ceebc066113d5fc2c7b223c6332bb48fc57021c9a64e76d940ea9557fc415d"
                      "migration 0011 exact source checksum"
          }

          test "migration 0009 repairs preexisting roots as a NOBYPASSRLS schema owner" {
              let databaseName = $"fogell_root_upgrade_{Guid.NewGuid():N}"
              let roleName = $"fogell_migration_owner_{Guid.NewGuid():N}"
              let organization = Guid.NewGuid()
              let project = Guid.NewGuid()
              let build = Guid.NewGuid()
              let node = Guid.NewGuid()
              let parent = Guid.NewGuid()
              let child = Guid.NewGuid()
              let adminBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              adminBuilder.Database <- "postgres"
              let targetBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              targetBuilder.Database <- databaseName
              let ownerConnectionString = $"{targetBuilder.ConnectionString};Options=-c role={roleName}"

              use admin = new Npgsql.NpgsqlConnection(adminBuilder.ConnectionString)
              admin.Open()
              use createRole = admin.CreateCommand()
              createRole.CommandText <-
                  $"CREATE ROLE {roleName} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS"
              createRole.ExecuteNonQuery() |> ignore
              use createDatabase = admin.CreateCommand()
              createDatabase.CommandText <- $"CREATE DATABASE {databaseName} OWNER {roleName}"
              createDatabase.ExecuteNonQuery() |> ignore

              try
                  use target = new Npgsql.NpgsqlConnection(ownerConnectionString)
                  target.Open()

                  use identity = target.CreateCommand()
                  identity.CommandText <-
                      "SELECT current_user, rolsuper, rolbypassrls
                         FROM pg_roles WHERE rolname = current_user"
                  use identityRow = identity.ExecuteReader()
                  Expect.isTrue (identityRow.Read()) "migration owner identity exists"
                  Expect.equal (identityRow.GetString 0) roleName "migration executes as the schema owner"
                  Expect.isFalse (identityRow.GetBoolean 1) "migration owner is not superuser"
                  Expect.isFalse (identityRow.GetBoolean 2) "migration owner cannot bypass RLS"
                  identityRow.Close()

                  let migrationSet = Migrations.all ()

                  let apply (_, sql: string) =
                      use command = target.CreateCommand()
                      command.CommandText <- sql
                      command.ExecuteNonQuery() |> ignore

                  migrationSet
                  |> List.filter (fun (version, _) -> version <= "0004")
                  |> List.iter apply

                  use seed = target.CreateCommand()
                  seed.CommandText <-
                      "INSERT INTO organizations (id, slug)
                       VALUES (@organization, 'pre-forced-rls');
                       INSERT INTO projects (id, organization_id, slug)
                       VALUES (@project, @organization, 'upgrade');
                       INSERT INTO builds
                           (id, organization_id, project_id, number, idempotency_key, status)
                       VALUES (@build, @organization, @project, 1, 'pre-0011-retry', 'failure');
                       INSERT INTO nodes
                           (id, organization_id, build_id, name, ordinal,
                            required_trust_pool, required_capabilities, status)
                       VALUES
                           (@node, @organization, @build, 'pipeline', 0,
                            'trusted-linux', ARRAY['linux'], 'failure');
                       INSERT INTO attempts
                           (id, organization_id, node_id, ordinal, state, fence,
                            restore_epoch, result)
                       VALUES
                           (@parent, @organization, @node, 0, 'terminal', 1, 0, 'failure');
                       INSERT INTO attempts
                           (id, organization_id, node_id, ordinal, retry_of, state,
                            fence, restore_epoch)
                       VALUES
                           (@child, @organization, @node, 1, @parent, 'queued', 0, 0);
                       INSERT INTO retry_decisions
                           (organization_id, parent_attempt_id, parent_node_id,
                            parent_ordinal, parent_retry_of, parent_restore_epoch,
                            attempt_limit, outcome, child_attempt_id, dead_letter_reason)
                       VALUES
                           (@organization, @parent, @node, 0, NULL, 0,
                            2, 'child_created', @child, NULL)"
                  seed.Parameters.AddWithValue("organization", organization) |> ignore
                  seed.Parameters.AddWithValue("project", project) |> ignore
                  seed.Parameters.AddWithValue("build", build) |> ignore
                  seed.Parameters.AddWithValue("node", node) |> ignore
                  seed.Parameters.AddWithValue("parent", parent) |> ignore
                  seed.Parameters.AddWithValue("child", child) |> ignore
                  seed.ExecuteNonQuery() |> ignore

                  let legacyMigrations =
                      migrationSet
                      |> List.filter (fun (version, _) -> version <= "0008")

                  legacyMigrations
                  |> List.filter (fun (version, _) -> version >= "0005")
                  |> List.iter apply

                  use ledger = target.CreateCommand()
                  ledger.CommandText <-
                      "CREATE TABLE schema_migrations (
                           version text PRIMARY KEY,
                           checksum text NOT NULL,
                           applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
                       )"
                  ledger.ExecuteNonQuery() |> ignore

                  let checksum (sql: string) =
                      use hash = SHA256.Create()
                      hash.ComputeHash(Text.Encoding.UTF8.GetBytes sql)
                      |> Convert.ToHexString
                      |> fun value -> value.ToLowerInvariant()

                  for version, sql in legacyMigrations do
                      use record = target.CreateCommand()
                      record.CommandText <-
                          "INSERT INTO schema_migrations (version, checksum) VALUES (@version, @checksum)"
                      record.Parameters.AddWithValue("version", version) |> ignore
                      record.Parameters.AddWithValue("checksum", checksum sql) |> ignore
                      record.ExecuteNonQuery() |> ignore

                  use hidden = target.CreateCommand()
                  hidden.CommandText <- "SELECT count(*) FROM organizations"
                  Expect.equal (hidden.ExecuteScalar() :?> int64) 0L "FORCE RLS hides unscoped organizations from their owner"

                  use missing = target.CreateCommand()
                  missing.CommandText <- "SELECT count(*) FROM organization_work_roots"
                  Expect.equal (missing.ExecuteScalar() :?> int64) 0L "legacy migration 0006 missed the preexisting organization"

                  match Migrations.run ownerConnectionString with
                  | Error error -> failtestf "restricted-owner upgrade failed: %s" error
                  | Ok applied ->
                      Expect.equal
                          (applied
                           |> List.filter (fun item -> not item.AlreadyPresent)
                           |> List.map (fun item -> item.Version))
                          [ "0009"; "0010"; "0011"; "0012"; "0013" ]
                          "only the forward repair and invariant guard migrations are pending"

                  use repaired = target.CreateCommand()
                  repaired.CommandText <-
                      "SELECT count(*) FROM organization_work_roots WHERE organization_id = @organization"
                  repaired.Parameters.AddWithValue("organization", organization) |> ignore
                  Expect.equal (repaired.ExecuteScalar() :?> int64) 1L "the missed legacy root is repaired exactly once"

                  use tenantTx = target.BeginTransaction()
                  use tenant = target.CreateCommand()
                  tenant.Transaction <- tenantTx
                  tenant.CommandText <-
                      "SELECT set_config('fogell.organization_id', @organization, true)"
                  tenant.Parameters.AddWithValue("organization", organization.ToString()) |> ignore
                  tenant.ExecuteScalar() |> ignore

                  use retryRepair = target.CreateCommand()
                  retryRepair.Transaction <- tenantTx
                  retryRepair.CommandText <-
                      "SELECT n.status, b.status, a.state,
                              (SELECT count(*) FROM events
                                WHERE organization_id = @organization
                                  AND build_id = @build
                                  AND kind = 'build.reopened'),
                              (SELECT count(*) FROM outbox
                                WHERE organization_id = @organization
                                  AND topic = 'build.reopened'
                                  AND body->>'build' = @build_text)
                         FROM nodes n
                         JOIN builds b
                           ON b.organization_id = n.organization_id AND b.id = n.build_id
                         JOIN attempts a
                           ON a.organization_id = n.organization_id AND a.id = @child
                        WHERE n.organization_id = @organization AND n.id = @node"
                  retryRepair.Parameters.AddWithValue("organization", organization) |> ignore
                  retryRepair.Parameters.AddWithValue("build", build) |> ignore
                  retryRepair.Parameters.AddWithValue("build_text", build.ToString()) |> ignore
                  retryRepair.Parameters.AddWithValue("child", child) |> ignore
                  retryRepair.Parameters.AddWithValue("node", node) |> ignore
                  use retryRepairRow = retryRepair.ExecuteReader()
                  Expect.isTrue (retryRepairRow.Read()) "pre-0011 retry lineage remains visible"
                  Expect.equal (retryRepairRow.GetString 0) "queued" "migration reopens the legacy node"
                  Expect.equal (retryRepairRow.GetString 1) "queued" "migration reopens the legacy build"
                  Expect.equal (retryRepairRow.GetString 2) "queued" "migration preserves the queued child"
                  Expect.equal (retryRepairRow.GetInt64 3) 1L "migration emits one repair event"
                  Expect.equal (retryRepairRow.GetInt64 4) 1L "migration emits one repair outbox record"
                  retryRepairRow.Close()
                  tenantTx.Commit()

                  Expect.equal
                      (Store(ownerConnectionString).RequestCancellation(
                          OrganizationId organization,
                          ProjectId project,
                          BuildId build))
                      CancellationAccepted
                      "the repaired pre-0011 build accepts cancellation"

                  use protection = target.CreateCommand()
                  protection.CommandText <-
                      "SELECT relrowsecurity, relforcerowsecurity
                         FROM pg_class WHERE oid = 'organizations'::regclass"
                  use protectionRow = protection.ExecuteReader()
                  Expect.isTrue (protectionRow.Read()) "organizations protection metadata exists"
                  Expect.isTrue (protectionRow.GetBoolean 0) "tenant RLS is enabled again before commit"
                  Expect.isTrue (protectionRow.GetBoolean 1) "tenant RLS is forced again before commit"
                  protectionRow.Close()

                  use retryProtection = target.CreateCommand()
                  retryProtection.CommandText <-
                      "SELECT count(*)::integer, bool_and(relrowsecurity), bool_and(relforcerowsecurity)
                         FROM pg_class
                        WHERE relname = ANY(ARRAY[
                            'retry_decisions', 'attempts', 'nodes',
                            'builds', 'events', 'outbox'])"
                  use retryProtectionRow = retryProtection.ExecuteReader()
                  Expect.isTrue (retryProtectionRow.Read()) "retry repair protection metadata exists"
                  Expect.equal (retryProtectionRow.GetInt32 0) 6 "all six repair tables are present"
                  Expect.isTrue (retryProtectionRow.GetBoolean 1) "tenant RLS is re-enabled on every repair table"
                  Expect.isTrue (retryProtectionRow.GetBoolean 2) "tenant RLS is re-forced on every repair table"
                  retryProtectionRow.Close()

                  use stillHidden = target.CreateCommand()
                  stillHidden.CommandText <- "SELECT count(*) FROM organizations"
                  Expect.equal (stillHidden.ExecuteScalar() :?> int64) 0L "the owner is fail-closed again after repair"
              finally
                  Npgsql.NpgsqlConnection.ClearAllPools()
                  use terminate = admin.CreateCommand()
                  terminate.CommandText <-
                      "SELECT pg_terminate_backend(pid)
                         FROM pg_stat_activity
                        WHERE datname = @database AND pid <> pg_backend_pid()"
                  terminate.Parameters.AddWithValue("database", databaseName) |> ignore
                  terminate.ExecuteNonQuery() |> ignore
                  use dropDatabase = admin.CreateCommand()
                  dropDatabase.CommandText <- $"DROP DATABASE {databaseName}"
                  dropDatabase.ExecuteNonQuery() |> ignore
                  use dropRole = admin.CreateCommand()
                  dropRole.CommandText <- $"DROP ROLE {roleName}"
                  dropRole.ExecuteNonQuery() |> ignore
          }

          test "migration 0008 upgrades populated sparse and colliding legacy logs" {
              let databaseName = $"fogell_log_upgrade_{Guid.NewGuid():N}"
              let adminBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              adminBuilder.Database <- "postgres"
              let targetBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              targetBuilder.Database <- databaseName

              use admin = new Npgsql.NpgsqlConnection(adminBuilder.ConnectionString)
              admin.Open()
              use create = admin.CreateCommand()
              create.CommandText <- $"CREATE DATABASE {databaseName}"
              create.ExecuteNonQuery() |> ignore

              try
                  use target = new Npgsql.NpgsqlConnection(targetBuilder.ConnectionString)
                  target.Open()

                  Migrations.all ()
                  |> List.filter (fun (version, _) -> version <= "0007")
                  |> List.iter (fun (_, sql) ->
                      use apply = target.CreateCommand()
                      apply.CommandText <- sql
                      apply.ExecuteNonQuery() |> ignore)

                  let org = Guid.NewGuid()
                  let project = Guid.NewGuid()
                  let populatedBuild = Guid.NewGuid()
                  let emptyBuild = Guid.NewGuid()
                  let node = Guid.NewGuid()
                  let parent = Guid.NewGuid()
                  let retry = Guid.NewGuid()

                  use seedTx = target.BeginTransaction()
                  use seed = target.CreateCommand()
                  seed.Transaction <- seedTx
                  seed.CommandText <-
                      "SELECT set_config('fogell.organization_id', @org_text, true);
                       INSERT INTO organizations (id, slug) VALUES (@org, 'upgrade-org');
                       INSERT INTO projects (id, organization_id, slug)
                       VALUES (@project, @org, 'upgrade-project');
                       INSERT INTO builds
                              (id, organization_id, project_id, number, idempotency_key, status)
                       VALUES (@build, @org, @project, 1, 'populated', 'queued'),
                              (@empty, @org, @project, 2, 'empty', 'queued');
                       INSERT INTO nodes
                              (id, organization_id, build_id, name, ordinal,
                               required_trust_pool, status)
                       VALUES (@node, @org, @build, 'legacy', 0, 'trusted-linux', 'queued');
                       INSERT INTO attempts
                              (id, organization_id, node_id, ordinal, state)
                       VALUES (@parent, @org, @node, 0, 'queued'),
                              (@retry, @org, @node, 1, 'queued');
                       INSERT INTO log_chunks
                              (organization_id, build_id, attempt_id, sequence, body, created_at)
                       VALUES (@org, @build, @parent, 0, 'zero',
                               '2026-01-01T00:00:00Z'::timestamptz),
                              (@org, @build, @parent, 4, 'four',
                               '2026-01-01T00:00:01Z'::timestamptz),
                              (@org, @build, @retry, 0, 'retry-zero',
                               '2026-01-01T00:00:02Z'::timestamptz)"
                  seed.Parameters.AddWithValue("org_text", string org) |> ignore
                  seed.Parameters.AddWithValue("org", org) |> ignore
                  seed.Parameters.AddWithValue("project", project) |> ignore
                  seed.Parameters.AddWithValue("build", populatedBuild) |> ignore
                  seed.Parameters.AddWithValue("empty", emptyBuild) |> ignore
                  seed.Parameters.AddWithValue("node", node) |> ignore
                  seed.Parameters.AddWithValue("parent", parent) |> ignore
                  seed.Parameters.AddWithValue("retry", retry) |> ignore
                  seed.ExecuteNonQuery() |> ignore
                  seedTx.Commit()

                  let migration8 =
                      Migrations.all ()
                      |> List.find (fun (version, _) -> version = "0008")
                      |> snd

                  use apply8 = target.CreateCommand()
                  apply8.CommandText <- migration8
                  apply8.ExecuteNonQuery() |> ignore

                  use readTx = target.BeginTransaction()
                  use scopeRead = target.CreateCommand()
                  scopeRead.Transaction <- readTx
                  scopeRead.CommandText <- "SELECT set_config('fogell.organization_id', @org_text, true)"
                  scopeRead.Parameters.AddWithValue("org_text", string org) |> ignore
                  scopeRead.ExecuteScalar() |> ignore
                  use select = target.CreateCommand()
                  select.Transaction <- readTx
                  select.CommandText <-
                      "SELECT body, build_sequence
                         FROM log_chunks
                        WHERE organization_id = @org AND build_id = @build
                        ORDER BY build_sequence"
                  select.Parameters.AddWithValue("org", org) |> ignore
                  select.Parameters.AddWithValue("build", populatedBuild) |> ignore
                  use rows = select.ExecuteReader()

                  let mapping =
                      [ while rows.Read() do
                            yield rows.GetString 0, rows.GetInt32 1 ]

                  rows.Close()
                  Expect.equal mapping [ "zero", 0; "four", 4; "retry-zero", 5 ] "sparse values survive and retry tie is lifted"

                  use counters = target.CreateCommand()
                  counters.Transaction <- readTx
                  counters.CommandText <-
                      "SELECT id, next_log_sequence
                         FROM builds
                        WHERE organization_id = @org AND id IN (@build, @empty)
                        ORDER BY number"
                  counters.Parameters.AddWithValue("org", org) |> ignore
                  counters.Parameters.AddWithValue("build", populatedBuild) |> ignore
                  counters.Parameters.AddWithValue("empty", emptyBuild) |> ignore
                  use counterRows = counters.ExecuteReader()
                  Expect.isTrue (counterRows.Read()) "populated build counter exists"
                  Expect.equal (counterRows.GetGuid 0, counterRows.GetInt32 1) (populatedBuild, 6) "counter follows lifted maximum"
                  Expect.isTrue (counterRows.Read()) "empty build counter exists"
                  Expect.equal (counterRows.GetGuid 0, counterRows.GetInt32 1) (emptyBuild, 0) "empty build stays at zero"
                  Expect.isFalse (counterRows.Read()) "only the two seeded builds exist"
                  counterRows.Close()
                  readTx.Commit()

                  use collisionTx = target.BeginTransaction()
                  use collision = target.CreateCommand()
                  collision.Transaction <- collisionTx
                  collision.CommandText <-
                      "SELECT set_config('fogell.organization_id', @org_text, true);
                       INSERT INTO log_chunks
                              (organization_id, build_id, attempt_id, sequence, build_sequence, body)
                       VALUES (@org, @build, @retry, 99, 5, 'collision')"
                  collision.Parameters.AddWithValue("org_text", string org) |> ignore
                  collision.Parameters.AddWithValue("org", org) |> ignore
                  collision.Parameters.AddWithValue("build", populatedBuild) |> ignore
                  collision.Parameters.AddWithValue("retry", retry) |> ignore

                  let uniqueRejected =
                      try
                          collision.ExecuteNonQuery() |> ignore
                          false
                      with :? Npgsql.PostgresException as error ->
                          error.SqlState = "23505"

                  Expect.isTrue uniqueRejected "build-wide cursor uniqueness is enforced"
                  collisionTx.Rollback()

                  use rls = target.CreateCommand()
                  rls.CommandText <-
                      "SELECT relname, relrowsecurity, relforcerowsecurity
                         FROM pg_class
                        WHERE relname IN ('builds', 'log_chunks')
                        ORDER BY relname"
                  use rlsRows = rls.ExecuteReader()
                  let protections =
                      [ while rlsRows.Read() do
                            yield rlsRows.GetString 0, rlsRows.GetBoolean 1, rlsRows.GetBoolean 2 ]

                  rlsRows.Close()
                  Expect.equal protections [ "builds", true, true; "log_chunks", true, true ] "migration restores forced RLS"
              finally
                  Npgsql.NpgsqlConnection.ClearAllPools()
                  use terminate = admin.CreateCommand()
                  terminate.CommandText <-
                      "SELECT pg_terminate_backend(pid)
                         FROM pg_stat_activity
                        WHERE datname = @database AND pid <> pg_backend_pid()"
                  terminate.Parameters.AddWithValue("database", databaseName) |> ignore
                  terminate.ExecuteNonQuery() |> ignore
                  use drop = admin.CreateCommand()
                  drop.CommandText <- $"DROP DATABASE {databaseName}"
                  drop.ExecuteNonQuery() |> ignore
          } ]

let tenantIsolation =
    testList
        "FG-028 forced tenant isolation"
        [ test "runtime readiness requires every table and sequence privilege, not maintenance capabilities" {
              let roleName = $"fogell_runtime_{Guid.NewGuid():N}"
              let maintenanceSequenceName = $"fogell_maintenance_{Guid.NewGuid():N}"

              let admin sql =
                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- sql
                  cmd.ExecuteNonQuery() |> ignore

              admin $"CREATE ROLE {roleName} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS"
              admin $"CREATE SEQUENCE {maintenanceSequenceName}"

              try
                  admin
                      $"GRANT USAGE ON SCHEMA public TO {roleName};
                        GRANT SELECT ON controller_metadata TO {roleName};
                        GRANT SELECT ON
                          organizations, projects, builds, nodes, attempts,
                          events, outbox, log_chunks, effect_checkpoints, retry_decisions,
                          build_definitions
                        TO {roleName};
                        GRANT SELECT ON organization_work_roots TO {roleName};
                        GRANT USAGE ON events_id_seq, outbox_id_seq, log_chunks_id_seq TO {roleName}"

                  // Deliberately disable Npgsql's pool reset and force one physical
                  // connection. Otherwise a session-scoped set_config mutant is
                  // cleaned up by the client and this test falsely credits the
                  // Store for transaction-local isolation PostgreSQL never got.
                  let runtimeConnectionString =
                      $"{connectionString};Options=-c role={roleName};No Reset On Close=true;Maximum Pool Size=1"
                  let runtimeStore = Store(runtimeConnectionString, connectionString)

                  Expect.isFalse
                      (runtimeStore.RuntimeCapabilities())
                      "one of four required table privileges is not the full table capability"

                  admin
                      $"GRANT INSERT, UPDATE, DELETE ON
                          organizations, projects, builds, nodes, attempts,
                          events, outbox, log_chunks, effect_checkpoints, retry_decisions,
                          build_definitions
                        TO {roleName}"

                  Expect.isFalse
                      (runtimeStore.RuntimeCapabilities())
                      "sequence USAGE without SELECT is not the full sequence capability"

                  admin $"GRANT SELECT ON events_id_seq, outbox_id_seq, log_chunks_id_seq TO {roleName}"

                  Expect.isFalse
                      (runtimeStore.RuntimeCapabilities())
                      "locking the metadata row is a required runtime capability"

                  admin $"GRANT UPDATE(singleton) ON controller_metadata TO {roleName}"

                  Expect.isTrue
                      (runtimeStore.RuntimeCapabilities())
                      "the runtime role has the exact controller surface without a maintenance-only sequence"

                  use capabilityConn = new Npgsql.NpgsqlConnection(connectionString)
                  capabilityConn.Open()
                  use capability = capabilityConn.CreateCommand()
                  capability.CommandText <-
                      $"SELECT has_sequence_privilege('{roleName}', 'public.{maintenanceSequenceName}', 'USAGE')"
                  Expect.isFalse
                      (capability.ExecuteScalar() :?> bool)
                      "readiness did not require granting the maintenance-only sequence to runtime"
              finally
                  Npgsql.NpgsqlConnection.ClearAllPools()
                  admin $"DROP SEQUENCE {maintenanceSequenceName}"
                  admin $"DROP OWNED BY {roleName}; DROP ROLE {roleName}"
          }

          test "a real NOBYPASSRLS runtime role is fail-closed and Store sets only transaction-local context" {
              let roleName = $"fogell_runtime_{Guid.NewGuid():N}"

              let admin sql =
                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- sql
                  cmd.ExecuteNonQuery() |> ignore

              admin $"CREATE ROLE {roleName} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS"

              try
                  admin
                      $"GRANT USAGE ON SCHEMA public TO {roleName};
                        GRANT SELECT, UPDATE(singleton) ON controller_metadata TO {roleName};
                        GRANT SELECT, INSERT, UPDATE, DELETE ON
                          organizations, projects, builds, nodes, attempts,
                          events, outbox, log_chunks, effect_checkpoints, retry_decisions,
                          build_definitions
                        TO {roleName};
                        GRANT SELECT ON organization_work_roots TO {roleName};
                        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {roleName}"

                  // Deliberately disable Npgsql's pool reset and force one physical
                  // connection. Otherwise a session-scoped set_config mutant is
                  // cleaned up by the client and this test falsely credits the
                  // Store for transaction-local isolation PostgreSQL never got.
                  let runtimeConnectionString =
                      $"{connectionString};Options=-c role={roleName};No Reset On Close=true;Maximum Pool Size=1"
                  let runtimeStore = Store(runtimeConnectionString, connectionString)

                  Expect.isTrue
                      (runtimeStore.RuntimeCapabilities())
                      "the runtime role has the exact controller database surface"

                  do
                      use identityConn = new Npgsql.NpgsqlConnection(runtimeConnectionString)
                      identityConn.Open()
                      use identity = identityConn.CreateCommand()
                      identity.CommandText <-
                          "SELECT current_user, rolsuper, rolbypassrls
                           FROM pg_roles WHERE rolname = current_user"
                      use identityReader = identity.ExecuteReader()
                      Expect.isTrue (identityReader.Read()) "runtime identity exists"
                      Expect.equal (identityReader.GetString 0) roleName "the connection actually assumed the application role"
                      Expect.isFalse (identityReader.GetBoolean 1) "application role is not superuser"
                      Expect.isFalse (identityReader.GetBoolean 2) "application role cannot bypass RLS"

                  let seed key =
                      let org = OrganizationId(Guid.NewGuid())
                      let project = ProjectId(Guid.NewGuid())
                      runtimeStore.CreateProject(org, $"org-{org.Value}", project, $"project-{key}")
                      let admitted =
                          match runtimeStore.AdmitBuild(newBuild org project key [ "tenant-proof" ]) with
                          | Ok value -> value
                          | Error error -> failtestf "runtime admission failed: %s" error
                      let owner = $"agent-{key}"
                      let fence =
                          match runtimeStore.OfferAttempt(org, admitted.AttemptId, owner, 60) with
                          | Ok value -> value
                          | Error error -> failtestf "runtime offer failed: %s" error
                      Expect.isTrue
                          (runtimeStore.AcceptAttempt(org, admitted.AttemptId, fence, owner))
                          "runtime role accepted its attempt"
                      match runtimeStore.PrepareEffect(org, admitted.AttemptId, fence, owner, "publish", [| 1uy; 2uy |]) with
                      | Error error -> failtestf "runtime effect preparation failed: %s" error
                      | Ok _ -> ()
                      Expect.isTrue
                          (runtimeStore.AppendLog(org, admitted.BuildId, admitted.AttemptId, 0, key))
                          "runtime role appended its log"
                      match runtimeStore.PublishTerminal(org, admitted.AttemptId, fence, owner, Failure) with
                      | Error error -> failtestf "runtime terminal publication failed: %s" error
                      | Ok () -> ()
                      match runtimeStore.DecideRetry(org, admitted.AttemptId, 2, AttemptId(Guid.NewGuid())) with
                      | Error error -> failtestf "runtime retry decision failed: %A" error
                      | Ok _ -> ()
                      org, admitted

                  let orgA, admissionA = seed "tenant-a"
                  let orgB, admissionB = seed "tenant-b"

                  Expect.containsAll
                      (runtimeStore.OrganizationIds())
                      [ orgA; orgB ]
                      "the UUID-only registry lets a restarted runtime discover tenant scopes"

                  let count (conn: Npgsql.NpgsqlConnection) (tx: Npgsql.NpgsqlTransaction option) table predicate =
                      use cmd = conn.CreateCommand()
                      tx |> Option.iter (fun value -> cmd.Transaction <- value)
                      cmd.CommandText <- $"SELECT count(*) FROM {table} {predicate}"
                      cmd.ExecuteScalar() :?> int64

                  use runtime = new Npgsql.NpgsqlConnection(runtimeConnectionString)
                  runtime.Open()

                  for table, _ in tenantTables do
                      Expect.equal (count runtime None table "") 0L $"{table} exposes no rows without context"

                  Expect.isGreaterThan
                      (count runtime None "organization_work_roots" "")
                      1L
                      "only UUID work roots are deliberately global"

                  use tenantTx = runtime.BeginTransaction()
                  use setTenant = runtime.CreateCommand()
                  setTenant.Transaction <- tenantTx
                  setTenant.CommandText <- "SELECT set_config('fogell.organization_id', @o, true)"
                  setTenant.Parameters.AddWithValue("o", orgA.Value.ToString()) |> ignore
                  setTenant.ExecuteScalar() |> ignore

                  for table, tenantColumn in tenantTables do
                      Expect.isGreaterThan
                          (count runtime (Some tenantTx) table "")
                          0L
                          $"{table} exposes the selected tenant's rows"
                      Expect.equal
                          (count runtime (Some tenantTx) table $"WHERE {tenantColumn} <> '{orgA.Value}'::uuid")
                          0L
                          $"{table} cannot expose another tenant"

                  tenantTx.Commit()

                  for table, _ in tenantTables do
                      Expect.equal
                          (count runtime None table "")
                          0L
                          $"{table} context did not leak past commit or pooled reuse"

                  use malformedTx = runtime.BeginTransaction()
                  use setMalformed = runtime.CreateCommand()
                  setMalformed.Transaction <- malformedTx
                  setMalformed.CommandText <- "SELECT set_config('fogell.organization_id', 'not-a-uuid', true)"
                  setMalformed.ExecuteScalar() |> ignore

                  let malformedRejected =
                      try
                          count runtime (Some malformedTx) "attempts" "" |> ignore
                          false
                      with :? Npgsql.PostgresException as error ->
                          error.SqlState = "22P02"

                  Expect.isTrue malformedRejected "malformed context fails closed instead of exposing rows"
                  malformedTx.Rollback()

                  use hostileTx = runtime.BeginTransaction()
                  use setHostileTenant = runtime.CreateCommand()
                  setHostileTenant.Transaction <- hostileTx
                  setHostileTenant.CommandText <- "SELECT set_config('fogell.organization_id', @o, true)"
                  setHostileTenant.Parameters.AddWithValue("o", orgA.Value.ToString()) |> ignore
                  setHostileTenant.ExecuteScalar() |> ignore
                  use crossTenantWrite = runtime.CreateCommand()
                  crossTenantWrite.Transaction <- hostileTx
                  crossTenantWrite.CommandText <-
                      "INSERT INTO outbox (organization_id, topic, body)
                       VALUES (@other, 'cross-tenant', '{}'::jsonb)"
                  crossTenantWrite.Parameters.AddWithValue("other", orgB.Value) |> ignore

                  let crossTenantRejected =
                      try
                          crossTenantWrite.ExecuteNonQuery() |> ignore
                          false
                      with :? Npgsql.PostgresException as error ->
                          error.SqlState = "42501"

                  Expect.isTrue crossTenantRejected "WITH CHECK rejects a cross-tenant write"
                  hostileTx.Rollback()
                  runtime.Close()

                  let crossLineageRejected sql =
                      use conn = new Npgsql.NpgsqlConnection(connectionString)
                      conn.Open()
                      use cmd = conn.CreateCommand()
                      cmd.CommandText <- sql
                      cmd.Parameters.AddWithValue("o", orgA.Value) |> ignore
                      cmd.Parameters.AddWithValue("b", admissionA.BuildId.Value) |> ignore
                      cmd.Parameters.AddWithValue("a", admissionB.AttemptId.Value) |> ignore

                      try
                          cmd.ExecuteNonQuery() |> ignore
                          false
                      with :? Npgsql.PostgresException as error ->
                          error.SqlState = "23503"

                  Expect.isTrue
                      (crossLineageRejected
                          "INSERT INTO events (organization_id, build_id, attempt_id, kind)
                           VALUES (@o, @b, @a, 'cross-lineage')")
                      "event cannot substitute another tenant's attempt"
                  Expect.isTrue
                      (crossLineageRejected
                          "INSERT INTO log_chunks
                                  (organization_id, build_id, attempt_id, sequence, build_sequence, body)
                           VALUES (@o, @b, @a, 99, 99, 'cross-lineage')")
                      "log cannot substitute another tenant's attempt"

                  use catalog = new Npgsql.NpgsqlConnection(connectionString)
                  catalog.Open()
                  use rls = catalog.CreateCommand()
                  rls.CommandText <-
                      "SELECT count(*)
                       FROM pg_class c
                       WHERE c.relname = ANY(@tables)
                         AND c.relrowsecurity
                         AND c.relforcerowsecurity"
                  rls.Parameters.AddWithValue("tables", tenantTables |> List.map fst |> List.toArray) |> ignore
                  Expect.equal
                      (rls.ExecuteScalar() :?> int64)
                      (int64 tenantTables.Length)
                      "every tenant table enables and forces RLS"

                  use policies = catalog.CreateCommand()
                  policies.CommandText <-
                      "SELECT count(*) FROM pg_policies
                       WHERE tablename = ANY(@tables)
                         AND policyname LIKE '%tenant_isolation'"
                  policies.Parameters.AddWithValue("tables", tenantTables |> List.map fst |> List.toArray) |> ignore
                  Expect.equal
                      (policies.ExecuteScalar() :?> int64)
                      (int64 tenantTables.Length)
                      "every tenant table has an isolation policy"

                  use lineage = catalog.CreateCommand()
                  lineage.CommandText <-
                      "SELECT count(*) FROM pg_constraint
                       WHERE conname IN ('events_attempt_tenant_fk', 'log_chunks_attempt_tenant_fk')"
                  Expect.equal (lineage.ExecuteScalar() :?> int64) 2L "remaining attempt lineage is tenant-composite"

                  use forbidden = new Npgsql.NpgsqlConnection(runtimeConnectionString)
                  forbidden.Open()
                  use changeEpoch = forbidden.CreateCommand()
                  changeEpoch.CommandText <-
                      "UPDATE controller_metadata SET restore_epoch = restore_epoch + 1 WHERE singleton"
                  let runtimeEpochWriteRejected =
                      try
                          changeEpoch.ExecuteNonQuery() |> ignore
                          false
                      with :? Npgsql.PostgresException as error ->
                          error.SqlState = "42501"
                  Expect.isTrue runtimeEpochWriteRejected "runtime role cannot alter the global restore epoch"
                  forbidden.Close()

                  let beforeRestore = runtimeStore.CurrentRestoreEpoch()
                  let afterRestore = runtimeStore.ActivateRestore()
                  Expect.equal afterRestore.Value (beforeRestore.Value + 1L) "maintenance connection performs global restore"
              finally
                  Npgsql.NpgsqlConnection.ClearAllPools()
                  admin $"DROP OWNED BY {roleName}; DROP ROLE {roleName}"
          }

          test "a NOBYPASSRLS maintenance restore invalidates queued attempts without cycling" {
              let roleName = $"fogell_restore_maintenance_{Guid.NewGuid():N}"
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "restricted-restore" [ "build" ])
              let outboxBefore = store.CountOutbox org

              let admin sql =
                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- sql
                  cmd.ExecuteNonQuery() |> ignore

              admin $"CREATE ROLE {roleName} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS"

              try
                  admin
                      $"GRANT USAGE ON SCHEMA public TO {roleName};
                        GRANT SELECT, UPDATE (restore_epoch) ON controller_metadata TO {roleName};
                        GRANT SELECT ON organization_work_roots TO {roleName};
                        GRANT SELECT, UPDATE ON attempts, nodes, builds TO {roleName};
                        GRANT SELECT, UPDATE ON effect_checkpoints TO {roleName};
                        GRANT INSERT ON events, outbox TO {roleName};
                        GRANT USAGE ON SEQUENCE events_id_seq, outbox_id_seq TO {roleName}"

                  let maintenanceBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
                  maintenanceBuilder.Options <- $"-c role={roleName}"
                  let restrictedStore = Store(connectionString, maintenanceBuilder.ConnectionString)

                  use identityConn = new Npgsql.NpgsqlConnection(maintenanceBuilder.ConnectionString)
                  identityConn.Open()
                  use identity = identityConn.CreateCommand()
                  identity.CommandText <-
                      "SELECT current_user, rolsuper, rolbypassrls
                         FROM pg_roles WHERE rolname = current_user"
                  use identityRow = identity.ExecuteReader()
                  Expect.isTrue (identityRow.Read()) "maintenance identity exists"
                  Expect.equal (identityRow.GetString 0) roleName "restore runs as the restricted maintenance role"
                  Expect.isFalse (identityRow.GetBoolean 1) "maintenance role is not superuser"
                  Expect.isFalse (identityRow.GetBoolean 2) "maintenance role cannot bypass forced RLS"
                  identityRow.Close()
                  identityConn.Close()

                  let before = restrictedStore.CurrentRestoreEpoch()
                  let after = restrictedStore.ActivateRestore()
                  Expect.equal after.Value (before.Value + 1L) "restore epoch and invalidation commit together"

                  Expect.equal
                      (restrictedStore.AttemptState(org, admitted.AttemptId))
                      (Some("reconciliation_required", 0L, None))
                      "the old-epoch queued attempt is invalidated through tenant-scoped RLS"
                  Expect.equal
                      (restrictedStore.BuildSnapshot(org, project, admitted.BuildId))
                      (Some("reconciliation_required", false))
                      "the restore rolls public build truth forward with the attempt"
                  Expect.equal
                      (restrictedStore.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                      1
                      "the restore emits one durable reason event"
                  Expect.equal
                      (restrictedStore.CountOutbox org)
                      (outboxBefore + 1)
                      "the restore emits one durable reconciliation outbox row"

                  use truth = new Npgsql.NpgsqlConnection(connectionString)
                  truth.Open()
                  use reason = truth.CreateCommand()
                  reason.CommandText <-
                      "SELECT n.status, e.payload->>'reason', o.topic, o.body->>'reason',
                              o.body->>'build', o.body->>'attempt'
                         FROM nodes n
                         JOIN events e
                           ON e.organization_id = n.organization_id
                          AND e.build_id = n.build_id
                          AND e.attempt_id = @a
                          AND e.kind = 'attempt.reconciliation_required'
                         JOIN outbox o
                           ON o.organization_id = e.organization_id
                          AND o.topic = 'build.reconciliation_required'
                          AND o.body->>'attempt' = e.attempt_id::text
                        WHERE n.organization_id = @o AND n.id = @n"
                  reason.Parameters.AddWithValue("o", org.Value) |> ignore
                  reason.Parameters.AddWithValue("n", admitted.NodeId.Value) |> ignore
                  reason.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
                  use reasonRow = reason.ExecuteReader()
                  Expect.isTrue (reasonRow.Read()) "restore node, event, and outbox truth is atomically observable"
                  Expect.equal (reasonRow.GetString 0) "reconciliation_required" "restore rolls the node forward"
                  Expect.equal (reasonRow.GetString 1) "restore_epoch_advanced" "event records the stable restore reason"
                  Expect.equal (reasonRow.GetString 2) "build.reconciliation_required" "outbox names the transition"
                  Expect.equal (reasonRow.GetString 3) "restore_epoch_advanced" "outbox records the same reason"
                  Expect.equal (reasonRow.GetString 4) (admitted.BuildId.Value.ToString()) "outbox binds the build"
                  Expect.equal (reasonRow.GetString 5) (admitted.AttemptId.Value.ToString()) "outbox binds the attempt"
                  reasonRow.Close()

                  restrictedStore.ActivateRestore() |> ignore
                  Expect.equal
                      (restrictedStore.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                      1
                      "a later restore does not duplicate the attempt transition event"
                  Expect.equal
                      (restrictedStore.CountOutbox org)
                      (outboxBefore + 1)
                      "a later restore does not duplicate the attempt transition outbox row"

                  for cycle in 1..2 do
                      Expect.equal
                          (restrictedStore.ClaimNextExecution(
                              org,
                              $"local:post-restore-{cycle}",
                              "trusted-linux",
                              [ "linux" ],
                              60))
                          (Ok None)
                          "the invalidated attempt is never re-offered"

                  Expect.equal
                      (restrictedStore.RequeueExpiredLocalAttempts org)
                      0
                      "lease recovery cannot turn reconciliation back into queued work"
              finally
                  Npgsql.NpgsqlConnection.ClearAllPools()
                  admin $"DROP OWNED BY {roleName}; DROP ROLE {roleName}"
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

          test "idempotency binds the exact source and placement fingerprint" {
              let org, project = freshProject ()
              let original = newBuild org project "definition-bound" [ "build" ]
              let first = admitOk original
              let replay = admitOk original
              Expect.equal replay.BuildId first.BuildId "exact bytes replay"

              let substituted =
                  { original with
                      PipelineSource = Text.Encoding.UTF8.GetBytes "pipeline:substituted" }

              match store.AdmitBuild substituted with
              | Error error -> Expect.stringContains error "different pipeline" "payload substitution named"
              | Ok _ -> failtest "one idempotency key accepted different source bytes"

              let moved = { original with RequiredTrustPool = "privileged" }
              match store.AdmitBuild moved with
              | Error error -> Expect.stringContains error "placement policy" "placement substitution named"
              | Ok _ -> failtest "one idempotency key accepted a different placement policy"

              Expect.equal (store.CountEvents(org, first.BuildId, "build.admitted")) 1 "one admission event"
              Expect.equal (store.CountOutbox org) 1 "one admission outbox row"
          }

          test "16 mixed concurrent payloads converge on one immutable definition" {
              let org, project = freshProject ()
              let baseInput = newBuild org project "mixed-race" [ "build" ]
              let sourceA = Text.Encoding.UTF8.GetBytes "pipeline:A"
              let sourceB = Text.Encoding.UTF8.GetBytes "pipeline:B"

              let results =
                  [ 0..15 ]
                  |> List.map (fun index ->
                      async {
                          let source = if index % 2 = 0 then sourceA else sourceB
                          return Store(connectionString).AdmitBuild({ baseInput with PipelineSource = source })
                      })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              let accepted = results |> Array.choose (function Ok value -> Some value | Error _ -> None)
              let refused = results.Length - accepted.Length
              Expect.equal accepted.Length 8 "all exact replays of the winning bytes succeed"
              Expect.equal refused 8 "every conflicting payload is refused"
              Expect.equal (accepted |> Array.map (fun value -> value.BuildId) |> Array.distinct |> Array.length) 1 "one build"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use count = conn.CreateCommand()
              count.CommandText <-
                  "SELECT count(*)
                     FROM build_definitions
                    WHERE organization_id = @o"
              count.Parameters.AddWithValue("o", org.Value) |> ignore
              Expect.equal (count.ExecuteScalar() :?> int64) 1L "one durable definition"
          }

          test "a new admission is one whole-pipeline execution unit" {
              let org, project = freshProject ()
              let input = newBuild org project "whole-pipeline" [ "build"; "test"; "deploy" ]
              let admitted = admitOk input

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use count = conn.CreateCommand()
              count.CommandText <-
                  "SELECT count(*), min(name)
                     FROM nodes
                    WHERE organization_id = @o AND build_id = @b"
              count.Parameters.AddWithValue("o", org.Value) |> ignore
              count.Parameters.AddWithValue("b", admitted.BuildId.Value) |> ignore
              use reader = count.ExecuteReader()
              Expect.isTrue (reader.Read()) "node aggregate exists"
              Expect.equal (reader.GetInt64 0) 1L "the runner is scheduled exactly once"
              Expect.equal (reader.GetString 1) "pipeline" "unit is named honestly"
          }

          test "admission shares the restore epoch lock before creating an attempt" {
              let org, project = freshProject ()
              let marker = $"FG021_RESTORE_{Guid.NewGuid():N}"
              let builder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              builder.ApplicationName <- marker

              use blocker = new Npgsql.NpgsqlConnection(connectionString)
              blocker.Open()
              use blockerTx = blocker.BeginTransaction()
              use lockEpoch = blocker.CreateCommand()
              lockEpoch.Transaction <- blockerTx
              lockEpoch.CommandText <-
                  "SELECT restore_epoch FROM controller_metadata WHERE singleton FOR UPDATE"
              lockEpoch.ExecuteScalar() |> ignore

              let admission =
                  Async.StartAsTask(async {
                      return
                          Store(builder.ConnectionString).AdmitBuild(
                              newBuild org project "admission-restore-lock" [ "build" ])
                  })

              use observer = new Npgsql.NpgsqlConnection(connectionString)
              observer.Open()

              let rec blocked remaining =
                  use waiting = observer.CreateCommand()
                  waiting.CommandText <-
                      "SELECT EXISTS (
                           SELECT 1 FROM pg_stat_activity
                           WHERE application_name = @marker
                             AND state = 'active'
                             AND wait_event_type = 'Lock'
                             AND query LIKE '%controller_metadata%'
                             AND query LIKE '%FOR SHARE%'
                       )"
                  waiting.Parameters.AddWithValue("marker", marker) |> ignore

                  if waiting.ExecuteScalar() :?> bool then true
                  elif remaining = 0 then false
                  else
                      Threading.Thread.Sleep 20
                      blocked (remaining - 1)

              let barrier = blocked 100

              use bump = blocker.CreateCommand()
              bump.Transaction <- blockerTx
              bump.CommandText <-
                  "UPDATE controller_metadata
                      SET restore_epoch = restore_epoch + 1
                    WHERE singleton"
              Expect.equal (bump.ExecuteNonQuery()) 1 "restore epoch advanced behind its exclusive lock"

              use invalidate = blocker.CreateCommand()
              invalidate.Transaction <- blockerTx
              invalidate.CommandText <-
                  "UPDATE attempts
                      SET state = 'reconciliation_required', lease_owner = NULL, lease_expires_at = NULL
                    WHERE restore_epoch < (SELECT restore_epoch FROM controller_metadata WHERE singleton)
                      AND state IN ('queued', 'offered', 'accepted', 'running', 'finalizing', 'cancelling')"
              invalidate.ExecuteNonQuery() |> ignore
              blockerTx.Commit()

              Expect.isTrue barrier "admission waited at the restore serialization point"
              Expect.isTrue (admission.Wait(TimeSpan.FromSeconds 5.0)) "blocked admission completed"

              let admitted =
                  match admission.Result with
                  | Ok value -> value
                  | Error error -> failtestf "post-restore admission failed: %s" error

              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("queued", 0L, None))
                  "the post-restore attempt remains current queued work"

              use epoch = observer.CreateCommand()
              epoch.CommandText <-
                  "SELECT a.restore_epoch = m.restore_epoch
                     FROM attempts a
                     CROSS JOIN controller_metadata m
                    WHERE a.organization_id = @o AND a.id = @a AND m.singleton"
              epoch.Parameters.AddWithValue("o", org.Value) |> ignore
              epoch.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              Expect.isTrue
                  (epoch.ExecuteScalar() :?> bool)
                  "admission used the committed restore epoch instead of inserting stale authority"
          }

          test "the database rejects a stale restore epoch from every attempt creator" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "stale-direct-attempt" [ "build" ])
              let rejectedId = Guid.NewGuid()

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use tx = conn.BeginTransaction()
              use tenant = conn.CreateCommand()
              tenant.Transaction <- tx
              tenant.CommandText <- "SELECT set_config('fogell.organization_id', @org, true)"
              tenant.Parameters.AddWithValue("org", org.Value.ToString()) |> ignore
              tenant.ExecuteScalar() |> ignore

              use current = conn.CreateCommand()
              current.Transaction <- tx
              current.CommandText <- "SELECT restore_epoch FROM controller_metadata WHERE singleton"
              let staleEpoch = (current.ExecuteScalar() :?> int64) - 1L

              use insert = conn.CreateCommand()
              insert.Transaction <- tx
              insert.CommandText <-
                  "INSERT INTO attempts
                         (id, organization_id, node_id, ordinal, state, restore_epoch)
                   VALUES (@a, @o, @n, 1, 'queued', @e)"
              insert.Parameters.AddWithValue("a", rejectedId) |> ignore
              insert.Parameters.AddWithValue("o", org.Value) |> ignore
              insert.Parameters.AddWithValue("n", admitted.NodeId.Value) |> ignore
              insert.Parameters.AddWithValue("e", staleEpoch) |> ignore

              let staleRejected =
                  try
                      insert.ExecuteNonQuery() |> ignore
                      false
                  with :? Npgsql.PostgresException as error ->
                      error.SqlState = "23514"
                      && error.MessageText.Contains("does not match current controller restore_epoch")

              Expect.isTrue staleRejected "the trigger rejects stale authority even outside Store"
              tx.Rollback()

              use absent = conn.CreateCommand()
              absent.CommandText <- "SELECT count(*) FROM attempts WHERE id = @a"
              absent.Parameters.AddWithValue("a", rejectedId) |> ignore
              Expect.equal (absent.ExecuteScalar() :?> int64) 0L "the rejected stale attempt leaves no row"
          }

          test "an attempt insert that wins the epoch lock is invalidated by the following restore" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "insert-before-restore" [ "build" ])
              let directAttempt = AttemptId(Guid.NewGuid())
              let marker = $"FG021_INSERT_FIRST_{Guid.NewGuid():N}"

              use writer = new Npgsql.NpgsqlConnection(connectionString)
              writer.Open()
              use writerTx = writer.BeginTransaction()
              use tenant = writer.CreateCommand()
              tenant.Transaction <- writerTx
              tenant.CommandText <- "SELECT set_config('fogell.organization_id', @org, true)"
              tenant.Parameters.AddWithValue("org", org.Value.ToString()) |> ignore
              tenant.ExecuteScalar() |> ignore

              use insert = writer.CreateCommand()
              insert.Transaction <- writerTx
              insert.CommandText <-
                  "INSERT INTO attempts
                         (id, organization_id, node_id, ordinal, state, restore_epoch)
                   SELECT @a, @o, @n, 1, 'queued', restore_epoch
                     FROM controller_metadata WHERE singleton"
              insert.Parameters.AddWithValue("a", directAttempt.Value) |> ignore
              insert.Parameters.AddWithValue("o", org.Value) |> ignore
              insert.Parameters.AddWithValue("n", admitted.NodeId.Value) |> ignore
              Expect.equal (insert.ExecuteNonQuery()) 1 "direct insertion holds the trigger's epoch share lock"

              let maintenanceBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              maintenanceBuilder.ApplicationName <- marker
              let restore =
                  Async.StartAsTask(async {
                      return Store(connectionString, maintenanceBuilder.ConnectionString).ActivateRestore()
                  })

              use observer = new Npgsql.NpgsqlConnection(connectionString)
              observer.Open()

              let rec blocked remaining =
                  use waiting = observer.CreateCommand()
                  waiting.CommandText <-
                      "SELECT EXISTS (
                           SELECT 1 FROM pg_stat_activity
                            WHERE application_name = @marker
                              AND state = 'active'
                              AND wait_event_type = 'Lock'
                              AND query LIKE 'UPDATE controller_metadata%'
                       )"
                  waiting.Parameters.AddWithValue("marker", marker) |> ignore

                  if waiting.ExecuteScalar() :?> bool then true
                  elif remaining = 0 then false
                  else
                      Threading.Thread.Sleep 20
                      blocked (remaining - 1)

              let restoreWaited = blocked 100
              writerTx.Commit()

              Expect.isTrue restoreWaited "restore waits for the insert-side epoch share lock"
              Expect.isTrue (restore.Wait(TimeSpan.FromSeconds 5.0)) "restore completes after admission commits"
              restore.Result |> ignore

              Expect.equal
                  (store.AttemptState(org, directAttempt))
                  (Some("reconciliation_required", 0L, None))
                  "the committed old-epoch attempt is invalidated by the serialized restore"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("reconciliation_required", false))
                  "the serialized restore also rolls public build truth forward"

              use event = observer.CreateCommand()
              event.CommandText <-
                  "SELECT count(*), min(payload->>'reason')
                     FROM events
                    WHERE organization_id = @o AND attempt_id = @a
                      AND kind = 'attempt.reconciliation_required'"
              event.Parameters.AddWithValue("o", org.Value) |> ignore
              event.Parameters.AddWithValue("a", directAttempt.Value) |> ignore
              use eventRow = event.ExecuteReader()
              Expect.isTrue (eventRow.Read()) "direct attempt has reconciliation truth"
              Expect.equal (eventRow.GetInt64 0) 1L "the direct attempt emits one transition event"
              Expect.equal (eventRow.GetString 1) "restore_epoch_advanced" "the transition names the restore reason"
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

          test "16 concurrent distinct keys allocate every project build number exactly once" {
              let org, project = freshProject ()
              use gate = new Threading.ManualResetEventSlim(false)

              let admissions =
                  [| 1..16 |]
                  |> Array.map (fun index ->
                      Threading.Tasks.Task.Run(fun () ->
                          gate.Wait()
                          Store(connectionString).AdmitBuild(
                              newBuild org project $"number-race-{index}" [ "build" ])))

              gate.Set()
              admissions
              |> Array.map (fun task -> task :> Threading.Tasks.Task)
              |> Threading.Tasks.Task.WaitAll

              let results = admissions |> Array.map (fun task -> task.Result)
              let errors = results |> Array.choose (function Error error -> Some error | Ok _ -> None)
              let errorSummary = String.concat "; " errors
              Expect.isEmpty errors $"all distinct-key admissions succeed: {errorSummary}"

              let accepted = results |> Array.choose (function Ok value -> Some value | Error _ -> None)
              Expect.equal accepted.Length 16 "all concurrent callers receive an admission"
              Expect.equal
                  (accepted |> Array.map (fun value -> value.BuildId) |> Array.distinct |> Array.length)
                  16
                  "distinct keys create distinct builds"
              Expect.equal
                  (accepted |> Array.map (fun value -> value.Number) |> Array.sort |> Array.toList)
                  [ 1..16 ]
                  "project numbers form the exact gap-free allocation"
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

          test "claim carries byte-exact source and terminal publication rolls up atomically" {
              let org, project = freshProject ()
              let input = newBuild org project "controller-vertical" [ "build" ]
              let admitted = admitOk input

              let claim =
                  match store.ClaimNextExecution(org, "local:test", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | Ok None -> failtest "new build was not claimable"
                  | Error error -> failtestf "claim failed: %s" error

              Expect.sequenceEqual claim.PipelineSource input.PipelineSource "exact source bytes survive admission"
              Expect.equal claim.BuildId admitted.BuildId "claim belongs to admitted build"
              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, "local:test", 60))
                  (Ok ExecutionStarted)
                  "execution start was linearized"

              match store.PublishTerminal(org, claim.AttemptId, claim.Fence, "local:test", Success) with
              | Error error -> failtestf "terminal roll-up failed: %s" error
              | Ok _ -> ()

              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("success", false))
                  "public status is the same terminal truth"
              Expect.equal (store.CountEvents(org, admitted.BuildId, "attempt.terminal")) 1 "one terminal event"
              Expect.equal (store.CountOutbox org) 2 "admission plus terminal outbox"
          }

          test "a queued cancellation becomes terminal before execution starts" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "cancel-before-start" [ "build" ])

              Expect.equal
                  (store.RequestCancellation(org, project, admitted.BuildId))
                  CancellationAccepted
                  "queued cancellation accepted"

              let claim =
                  match store.ClaimNextExecution(org, "local:cancel-before-start", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | Ok None -> failtest "cancelled queued work still needs a fenced terminal decision"
                  | Error error -> failtestf "claim failed: %s" error

              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, "local:cancel-before-start", 60))
                  (Ok ExecutionCancelledBeforeStart)
                  "worker is told not to launch a child"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("terminal", claim.Fence.Value, Some "aborted"))
                  "attempt is atomically aborted"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("aborted", false))
                  "public truth is terminal and clears the request flag"
              Expect.equal (store.CountEvents(org, admitted.BuildId, "attempt.terminal")) 1 "one terminal event"
              Expect.equal (store.CountOutbox org) 2 "admission plus one terminal outbox"
          }

          test "execution start refreshes a near-expiry lease before child launch" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "start-refreshes-lease" [ "build" ])

              let claim =
                  match store.ClaimNextExecution(org, "local:lease-refresh", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected claim, got %A" other

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use shorten = conn.CreateCommand()
              shorten.CommandText <-
                  "UPDATE attempts
                      SET lease_expires_at = clock_timestamp() + interval '5 seconds'
                    WHERE organization_id = @o AND id = @a"
              shorten.Parameters.AddWithValue("o", org.Value) |> ignore
              shorten.Parameters.AddWithValue("a", claim.AttemptId.Value) |> ignore
              Expect.equal (shorten.ExecuteNonQuery()) 1 "fixture moved the offer near expiry"

              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, "local:lease-refresh", 60))
                  (Ok ExecutionStarted)
                  "start authority granted"

              use refreshed = conn.CreateCommand()
              refreshed.CommandText <-
                  "SELECT lease_expires_at > clock_timestamp() + interval '50 seconds'
                     FROM attempts
                    WHERE organization_id = @o AND id = @a"
              refreshed.Parameters.AddWithValue("o", org.Value) |> ignore
              refreshed.Parameters.AddWithValue("a", claim.AttemptId.Value) |> ignore
              Expect.isTrue (refreshed.ExecuteScalar() :?> bool) "start restored a full launch lease"

              let requeued =
                  [ 1..8 ]
                  |> List.map (fun _ -> async { return Store(connectionString).RequeueExpiredLocalAttempts org })
                  |> Async.Parallel
                  |> Async.RunSynchronously
                  |> Array.sum
              Expect.equal requeued 0 "concurrent expiry scans cannot steal freshly-started work"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("running", claim.Fence.Value, None))
                  "fence and running ownership remain intact through the launch window"
          }

          test "only an expired pre-launch offer is safe to requeue" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "expired-offer" [ "build" ])

              let oldClaim =
                  match store.ClaimNextExecution(org, "local:expired-offer", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected old claim, got %A" other

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use expire = conn.CreateCommand()
              expire.CommandText <-
                  "UPDATE attempts
                      SET lease_expires_at = clock_timestamp() - interval '1 second'
                    WHERE organization_id = @o AND id = @a"
              expire.Parameters.AddWithValue("o", org.Value) |> ignore
              expire.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              Expect.equal (expire.ExecuteNonQuery()) 1 "fixture expired the unstarted offer"

              let reconciliationEventsBefore =
                  store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required")
              let outboxBefore = store.CountOutbox org

              Expect.equal (store.RequeueExpiredLocalAttempts org) 1 "the pre-launch offer was recovered"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("queued", oldClaim.Fence.Value, None))
                  "recovery preserved the old fence until the next claim"
              Expect.equal
                  (store.RequeueExpiredLocalAttempts org)
                  0
                  "a repeated expiry scan has no transition left to publish"
              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  reconciliationEventsBefore
                  "safe offered recovery emits no reconciliation event"
              Expect.equal
                  (store.CountOutbox org)
                  outboxBefore
                  "safe offered recovery emits no reconciliation outbox"
              expectError
                  "the expired owner cannot cross the execution-start boundary"
                  (store.BeginExecution(org, admitted.AttemptId, oldClaim.Fence, "local:expired-offer", 60))

              let newClaim =
                  match store.ClaimNextExecution(org, "local:new-worker", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected replacement claim, got %A" other

              Expect.equal newClaim.AttemptId admitted.AttemptId "the safe offer was reclaimed"
              Expect.equal newClaim.Fence.Value (oldClaim.Fence.Value + 1L) "replacement authority has a new fence"
          }

          test "expired post-offer local states require reconciliation" {
              for state in [ "accepted"; "running"; "finalizing"; "cancelling" ] do
                  let org, project = freshProject ()
                  let admitted = admitOk (newBuild org project $"expired-{state}" [ "build" ])
                  let owner = $"local:expired-{state}"

                  let claim =
                      match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
                      | Ok(Some value) -> value
                      | other -> failtestf "expected %s claim, got %A" state other

                  if state <> "accepted" then
                      Expect.equal
                          (store.BeginExecution(org, claim.AttemptId, claim.Fence, owner, 60))
                          (Ok ExecutionStarted)
                          $"{state} fixture crossed the launch boundary"

                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use makeExpired = conn.CreateCommand()
                  makeExpired.CommandText <-
                      "WITH changed_attempt AS (
                           UPDATE attempts
                              SET state = @state,
                                  lease_expires_at = clock_timestamp() - interval '1 second'
                            WHERE organization_id = @o AND id = @a
                           RETURNING node_id
                       ), changed_node AS (
                           UPDATE nodes n SET status = 'running'
                             FROM changed_attempt a
                            WHERE n.organization_id = @o AND n.id = a.node_id
                           RETURNING n.build_id
                       )
                       UPDATE builds b SET status = 'running'
                         FROM changed_node n
                        WHERE b.organization_id = @o AND b.id = n.build_id"
                  makeExpired.Parameters.AddWithValue("state", state) |> ignore
                  makeExpired.Parameters.AddWithValue("o", org.Value) |> ignore
                  makeExpired.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
                  Expect.equal (makeExpired.ExecuteNonQuery()) 1 $"fixture expired {state}"

                  let outboxBefore = store.CountOutbox org

                  Expect.equal (store.RequeueExpiredLocalAttempts org) 1 $"expiry scan handled {state} once"
                  Expect.equal
                      (store.AttemptState(org, admitted.AttemptId))
                      (Some("reconciliation_required", claim.Fence.Value, None))
                      $"{state} failed closed instead of becoming runnable"
                  Expect.equal
                      (store.BuildSnapshot(org, project, admitted.BuildId))
                      (Some("reconciliation_required", false))
                      $"{state} ambiguity reached public build truth"

                  use publication = conn.CreateCommand()
                  publication.CommandText <-
                      "SELECT count(DISTINCT e.id),
                              min(e.payload->>'reason'),
                              count(DISTINCT o.id),
                              min(o.topic),
                              min(o.body->>'reason'),
                              min(o.body->>'build'),
                              min(o.body->>'attempt')
                         FROM events e
                         LEFT JOIN outbox o
                           ON o.organization_id = e.organization_id
                          AND o.topic = 'build.reconciliation_required'
                          AND o.body->>'build' = e.build_id::text
                          AND o.body->>'attempt' = e.attempt_id::text
                        WHERE e.organization_id = @o
                          AND e.build_id = @b
                          AND e.attempt_id = @a
                          AND e.kind = 'attempt.reconciliation_required'"
                  publication.Parameters.AddWithValue("o", org.Value) |> ignore
                  publication.Parameters.AddWithValue("b", admitted.BuildId.Value) |> ignore
                  publication.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
                  use publicationRow = publication.ExecuteReader()
                  Expect.isTrue (publicationRow.Read()) $"{state} transition publication is observable"
                  Expect.equal (publicationRow.GetInt64 0) 1L $"{state} emits one reconciliation event"
                  Expect.equal (publicationRow.GetString 1) "lease_expired" $"{state} event names lease expiry"
                  Expect.equal (publicationRow.GetInt64 2) 1L $"{state} emits one reconciliation outbox"
                  Expect.equal
                      (publicationRow.GetString 3)
                      "build.reconciliation_required"
                      $"{state} outbox names the transition"
                  Expect.equal (publicationRow.GetString 4) "lease_expired" $"{state} outbox preserves the event reason"
                  Expect.equal
                      (publicationRow.GetString 5)
                      (admitted.BuildId.Value.ToString())
                      $"{state} outbox binds the build"
                  Expect.equal
                      (publicationRow.GetString 6)
                      (admitted.AttemptId.Value.ToString())
                      $"{state} outbox binds the attempt"
                  publicationRow.Close()

                  Expect.equal
                      (store.RequeueExpiredLocalAttempts org)
                      0
                      $"a repeated expiry scan does not republish {state}"
                  Expect.equal
                      (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                      1
                      $"a repeated expiry scan leaves one {state} event"
                  Expect.equal
                      (store.CountOutbox org)
                      (outboxBefore + 1)
                      $"a repeated expiry scan leaves one {state} outbox"
                  Expect.equal
                      (store.ClaimNextExecution(org, "local:replacement", "trusted-linux", [ "linux" ], 60))
                      (Ok None)
                      $"{state} was not offered to another worker"
          }

          test "one expiry scan publishes every ambiguous attempt across legacy nodes and retry history" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "expired-legacy-nodes" [ "build" ])
              let firstOwner = "local:expired-legacy-first"

              let firstClaim =
                  match store.ClaimNextExecution(org, firstOwner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected first legacy-shape claim, got %A" other

              Expect.equal
                  (store.BeginExecution(org, firstClaim.AttemptId, firstClaim.Fence, firstOwner, 60))
                  (Ok ExecutionStarted)
                  "the admitted attempt crossed the launch boundary"

              let secondNode = NodeId(Guid.NewGuid())
              let secondAttempt = AttemptId(Guid.NewGuid())
              let secondOwner = "local:expired-legacy-second"
              let retryAttempt = AttemptId(Guid.NewGuid())
              let retryOwner = "local:expired-legacy-retry"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use fixture = conn.CreateCommand()
              fixture.CommandText <-
                  "UPDATE attempts
                      SET lease_expires_at = clock_timestamp() - interval '1 second'
                    WHERE organization_id = @o AND id = @first_attempt;
                   INSERT INTO nodes
                          (id, organization_id, build_id, name, ordinal,
                           required_trust_pool, required_capabilities, status)
                   VALUES (@second_node, @o, @b, 'legacy-second-node', 1,
                           'trusted-linux', ARRAY['linux'], 'running');
                   INSERT INTO attempts
                          (id, organization_id, node_id, ordinal, state, fence,
                           restore_epoch, lease_owner, lease_expires_at)
                   SELECT @second_attempt, @o, @second_node, 0, 'running', 1,
                          restore_epoch, @second_owner,
                          clock_timestamp() - interval '1 second'
                     FROM controller_metadata
                    WHERE singleton;
                   -- A retry-history row sharing the admitted node proves the
                   -- batch cardinality is per attempt, not per DISTINCT node.
                   -- Simultaneously active retry rows are migration-tolerance
                   -- input here, not a claim about normal scheduler behavior.
                   INSERT INTO attempts
                          (id, organization_id, node_id, ordinal, retry_of, state, fence,
                           restore_epoch, lease_owner, lease_expires_at)
                   SELECT @retry_attempt, @o, @first_node, 1, @first_attempt,
                          'running', 2, restore_epoch, @retry_owner,
                          clock_timestamp() - interval '1 second'
                     FROM controller_metadata
                    WHERE singleton"
              fixture.Parameters.AddWithValue("o", org.Value) |> ignore
              fixture.Parameters.AddWithValue("b", admitted.BuildId.Value) |> ignore
              fixture.Parameters.AddWithValue("first_attempt", admitted.AttemptId.Value) |> ignore
              fixture.Parameters.AddWithValue("second_node", secondNode.Value) |> ignore
              fixture.Parameters.AddWithValue("second_attempt", secondAttempt.Value) |> ignore
              fixture.Parameters.AddWithValue("second_owner", secondOwner) |> ignore
              fixture.Parameters.AddWithValue("retry_attempt", retryAttempt.Value) |> ignore
              fixture.Parameters.AddWithValue("retry_owner", retryOwner) |> ignore
              fixture.Parameters.AddWithValue("first_node", admitted.NodeId.Value) |> ignore
              Expect.equal
                  (fixture.ExecuteNonQuery())
                  4
                  "fixture created three expired attempts across two distinct nodes"

              let outboxBefore = store.CountOutbox org
              Expect.equal (store.RequeueExpiredLocalAttempts org) 3 "one scan moved all three ambiguous attempts"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("reconciliation_required", firstClaim.Fence.Value, None))
                  "the admitted attempt requires reconciliation"
              Expect.equal
                  (store.AttemptState(org, secondAttempt))
                  (Some("reconciliation_required", 1L, None))
                  "the legacy-node attempt requires reconciliation"
              Expect.equal
                  (store.AttemptState(org, retryAttempt))
                  (Some("reconciliation_required", 2L, None))
                  "the retry-history attempt requires reconciliation"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("reconciliation_required", false))
                  "both node transitions roll up through the one build"

              use publications = conn.CreateCommand()
              publications.CommandText <-
                  "SELECT e.attempt_id, e.payload->>'reason',
                          o.topic, o.body->>'reason',
                          o.body->>'build', o.body->>'attempt'
                     FROM events e
                     JOIN outbox o
                       ON o.organization_id = e.organization_id
                      AND o.topic = 'build.reconciliation_required'
                      AND o.body->>'build' = e.build_id::text
                      AND o.body->>'attempt' = e.attempt_id::text
                    WHERE e.organization_id = @o
                      AND e.build_id = @b
                      AND e.attempt_id IN (@first_attempt, @second_attempt, @retry_attempt)
                      AND e.kind = 'attempt.reconciliation_required'
                    ORDER BY e.attempt_id"
              publications.Parameters.AddWithValue("o", org.Value) |> ignore
              publications.Parameters.AddWithValue("b", admitted.BuildId.Value) |> ignore
              publications.Parameters.AddWithValue("first_attempt", admitted.AttemptId.Value) |> ignore
              publications.Parameters.AddWithValue("second_attempt", secondAttempt.Value) |> ignore
              publications.Parameters.AddWithValue("retry_attempt", retryAttempt.Value) |> ignore
              use publicationRows = publications.ExecuteReader()
              let observed = ResizeArray<Guid * string * string * string * string * string>()

              while publicationRows.Read() do
                  observed.Add(
                      publicationRows.GetGuid 0,
                      publicationRows.GetString 1,
                      publicationRows.GetString 2,
                      publicationRows.GetString 3,
                      publicationRows.GetString 4,
                      publicationRows.GetString 5)

              publicationRows.Close()
              Expect.equal observed.Count 3 "the one-build batch has exactly three event/outbox pairs"
              Expect.equal
                  (observed |> Seq.map (fun (attemptId, _, _, _, _, _) -> attemptId) |> Set.ofSeq)
                  (Set.ofList [ admitted.AttemptId.Value; secondAttempt.Value; retryAttempt.Value ])
                  "each moved attempt has its own publication pair"

              for attemptId, eventReason, topic, outboxReason, outboxBuild, outboxAttempt in observed do
                  Expect.equal eventReason "lease_expired" "each batch event names lease expiry"
                  Expect.equal topic "build.reconciliation_required" "each batch outbox names reconciliation"
                  Expect.equal outboxReason eventReason "each batch outbox preserves its event reason"
                  Expect.equal outboxBuild (admitted.BuildId.Value.ToString()) "each batch outbox binds the shared build"
                  Expect.equal outboxAttempt (attemptId.ToString()) "each batch outbox binds its own attempt"

              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  3
                  "three attempts across two distinct nodes produced three per-attempt events"
              Expect.equal (store.CountOutbox org) (outboxBefore + 3) "three moved attempts produced three outbox rows"
              Expect.equal (store.RequeueExpiredLocalAttempts org) 0 "a second batch scan moves nothing"
              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  3
                  "a second batch scan emits no events"
              Expect.equal (store.CountOutbox org) (outboxBefore + 3) "a second batch scan emits no outbox rows"
          }

          test "stale owner and replacement-worker race cannot duplicate started work" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "expired-running-race" [ "build" ])
              let owner = "local:stale-owner"

              let claim =
                  match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected stale-owner claim, got %A" other

              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, owner, 60))
                  (Ok ExecutionStarted)
                  "old child crossed the launch boundary"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use expire = conn.CreateCommand()
              expire.CommandText <-
                  "UPDATE attempts
                      SET lease_expires_at = clock_timestamp() - interval '1 second'
                    WHERE organization_id = @o AND id = @a"
              expire.Parameters.AddWithValue("o", org.Value) |> ignore
              expire.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              Expect.equal (expire.ExecuteNonQuery()) 1 "fixture expired the started owner"

              let outboxBefore = store.CountOutbox org

              use gate = new System.Threading.ManualResetEventSlim(false)
              let scans =
                  [| 1..8 |]
                  |> Array.map (fun _ ->
                      System.Threading.Tasks.Task.Run(fun () ->
                          gate.Wait()
                          Store(connectionString).RequeueExpiredLocalAttempts org))
              let replacementClaims =
                  [| 1..8 |]
                  |> Array.map (fun i ->
                      System.Threading.Tasks.Task.Run(fun () ->
                          gate.Wait()
                          Store(connectionString).ClaimNextExecution(
                              org,
                              $"local:replacement-{i}",
                              "trusted-linux",
                              [ "linux" ],
                              60)))
              gate.Set()
              Array.append
                  (scans |> Array.map (fun task -> task :> System.Threading.Tasks.Task))
                  (replacementClaims |> Array.map (fun task -> task :> System.Threading.Tasks.Task))
              |> System.Threading.Tasks.Task.WaitAll

              Expect.equal (scans |> Array.sumBy (fun task -> task.Result)) 1 "exactly one scan quarantined the attempt"
              replacementClaims
              |> Array.iter (fun task ->
                  Expect.equal task.Result (Ok None) "no race winner received ambiguous started work")
              expectError
                  "the stale owner cannot publish after lease expiry"
                  (store.PublishTerminal(org, admitted.AttemptId, claim.Fence, owner, Success))
              Expect.isFalse
                  (store.RequeueOwnedAttempt(org, admitted.AttemptId, claim.Fence, owner))
                  "cleared ownership prevents a stale termination claim from reopening work"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("reconciliation_required", claim.Fence.Value, None))
                  "race converged on reconciliation without a fence handoff"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("reconciliation_required", false))
                  "public truth names the ambiguity"

              use publication = conn.CreateCommand()
              publication.CommandText <-
                  "SELECT count(DISTINCT e.id), min(e.payload->>'reason'),
                          count(DISTINCT o.id), min(o.topic),
                          min(o.body->>'reason'), min(o.body->>'build'),
                          min(o.body->>'attempt')
                     FROM events e
                     LEFT JOIN outbox o
                       ON o.organization_id = e.organization_id
                      AND o.topic = 'build.reconciliation_required'
                      AND o.body->>'build' = e.build_id::text
                      AND o.body->>'attempt' = e.attempt_id::text
                    WHERE e.organization_id = @o
                      AND e.build_id = @b
                      AND e.attempt_id = @a
                      AND e.kind = 'attempt.reconciliation_required'"
              publication.Parameters.AddWithValue("o", org.Value) |> ignore
              publication.Parameters.AddWithValue("b", admitted.BuildId.Value) |> ignore
              publication.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              use publicationRow = publication.ExecuteReader()
              Expect.isTrue (publicationRow.Read()) "the concurrent transition publication is observable"
              Expect.equal (publicationRow.GetInt64 0) 1L "eight scanners emit one lease-expiry event"
              Expect.equal (publicationRow.GetString 1) "lease_expired" "the race event names lease expiry"
              Expect.equal (publicationRow.GetInt64 2) 1L "eight scanners emit one reconciliation outbox"
              Expect.equal
                  (publicationRow.GetString 3)
                  "build.reconciliation_required"
                  "the race outbox names reconciliation"
              Expect.equal (publicationRow.GetString 4) "lease_expired" "the race outbox preserves the event reason"
              Expect.equal
                  (publicationRow.GetString 5)
                  (admitted.BuildId.Value.ToString())
                  "the race outbox binds the build"
              Expect.equal
                  (publicationRow.GetString 6)
                  (admitted.AttemptId.Value.ToString())
                  "the race outbox binds the attempt"
              publicationRow.Close()
              Expect.equal (store.CountOutbox org) (outboxBefore + 1) "the scan race emits one outbox row total"
          }

          test "an unstarted launcher-loss claim requeues without reconciliation and advances its replacement fence" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "offered-readiness-requeue" [ "build" ])
              let owner = "local:unavailable-launcher"

              let claim =
                  match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected offered claim, got %A" other

              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("offered", claim.Fence.Value, None))
                  "the readiness boundary has not begun or completed the offered attempt"

              let outboxBefore = store.CountOutbox org

              Expect.isTrue
                  (store.RequeueOwnedAttempt(org, claim.AttemptId, claim.Fence, owner))
                  "the exact unstarted owner and fence may return the offer immediately"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use lineage = conn.CreateCommand()
              lineage.CommandText <-
                  "SELECT a.state, n.status, b.status, a.lease_owner, a.lease_expires_at
                     FROM attempts a
                     JOIN nodes n
                       ON n.organization_id = a.organization_id AND n.id = a.node_id
                     JOIN builds b
                       ON b.organization_id = n.organization_id AND b.id = n.build_id
                    WHERE a.organization_id = @o AND a.id = @a"
              lineage.Parameters.AddWithValue("o", org.Value) |> ignore
              lineage.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              use row = lineage.ExecuteReader()
              Expect.isTrue (row.Read()) "the requeued offered lineage exists"
              Expect.equal (row.GetString 0) "queued" "attempt returned to queued"
              Expect.equal (row.GetString 1) "queued" "node remained queued"
              Expect.equal (row.GetString 2) "queued" "build remained queued"
              Expect.isTrue (row.IsDBNull 3) "requeue cleared the offer owner"
              Expect.isTrue (row.IsDBNull 4) "requeue cleared the offer expiry"
              row.Close()

              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  0
                  "an unstarted offer emits no reconciliation event"
              Expect.equal
                  (store.CountOutbox org)
                  outboxBefore
                  "an unstarted offer emits no reconciliation outbox"

              let replacementOwner = "local:restored-launcher"
              let replacement =
                  match
                      store.ClaimNextExecution(
                          org,
                          replacementOwner,
                          "trusted-linux",
                          [ "linux" ],
                          60)
                  with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected replacement claim, got %A" other

              Expect.equal replacement.AttemptId claim.AttemptId "replacement receives the same queued attempt"
              Expect.equal
                  replacement.Fence.Value
                  (claim.Fence.Value + 1L)
                  "replacement offer advances the stale owner's fence exactly once"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("offered", replacement.Fence.Value, None))
                  "replacement owns a new offer with no terminal result"
              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  0
                  "replacement claim still emits no reconciliation event"
              Expect.equal
                  (store.CountOutbox org)
                  outboxBefore
                  "replacement claim still emits no reconciliation outbox"
          }

          test "verified-extinction requeue rolls the whole lineage back to queued" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "owned-requeue-rollup" [ "build" ])
              let owner = "local:verified-extinction"
              let outboxBefore = store.CountOutbox org

              let claim =
                  match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected original claim, got %A" other

              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, owner, 60))
                  (Ok ExecutionStarted)
                  "original owner crossed the launch boundary"
              Expect.isTrue
                  (store.RequeueOwnedAttempt(org, claim.AttemptId, claim.Fence, owner))
                  "verified extinction authorized the fenced requeue"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use lineage = conn.CreateCommand()
              lineage.CommandText <-
                  "SELECT a.state, n.status, b.status, a.lease_owner, a.lease_expires_at
                     FROM attempts a
                     JOIN nodes n
                       ON n.organization_id = a.organization_id AND n.id = a.node_id
                     JOIN builds b
                       ON b.organization_id = n.organization_id AND b.id = n.build_id
                    WHERE a.organization_id = @o AND a.id = @a"
              lineage.Parameters.AddWithValue("o", org.Value) |> ignore
              lineage.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              use row = lineage.ExecuteReader()
              Expect.isTrue (row.Read()) "requeued lineage exists"
              Expect.equal (row.GetString 0) "queued" "attempt returned to queued"
              Expect.equal (row.GetString 1) "queued" "node returned to queued"
              Expect.equal (row.GetString 2) "queued" "build returned to queued"
              Expect.isTrue (row.IsDBNull 3) "requeue cleared the old lease owner"
              Expect.isTrue (row.IsDBNull 4) "requeue cleared the old lease expiry"
              row.Close()

              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  0
                  "known pre-launch extinction emits no reconciliation event"
              Expect.equal
                  (store.CountOutbox org)
                  outboxBefore
                  "known pre-launch extinction emits no reconciliation outbox"

              let replacement =
                  match store.ClaimNextExecution(org, "local:replacement", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected replacement claim, got %A" other

              Expect.equal replacement.AttemptId admitted.AttemptId "replacement reclaimed the same attempt"
              Expect.equal replacement.Fence.Value (claim.Fence.Value + 1L) "replacement claim advanced the fence"
              expectError
                  "the terminated owner cannot publish through its old fence"
                  (store.PublishTerminal(org, admitted.AttemptId, claim.Fence, owner, Success))
          }

          test "cancellation racing verified-extinction requeue remains authoritative" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "owned-requeue-cancel-race" [ "build" ])
              let owner = "local:shutdown-owner"

              let claim =
                  match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected shutdown claim, got %A" other

              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, owner, 60))
                  (Ok ExecutionStarted)
                  "shutdown fixture is running"

              use gate = new System.Threading.ManualResetEventSlim(false)
              let requeue =
                  System.Threading.Tasks.Task.Run(fun () ->
                      gate.Wait()
                      Store(connectionString).RequeueOwnedAttempt(org, claim.AttemptId, claim.Fence, owner))
              let cancel =
                  System.Threading.Tasks.Task.Run(fun () ->
                      gate.Wait()
                      Store(connectionString).RequestCancellation(org, project, admitted.BuildId))
              gate.Set()
              System.Threading.Tasks.Task.WaitAll [| requeue :> System.Threading.Tasks.Task; cancel :> System.Threading.Tasks.Task |]

              Expect.isTrue requeue.Result "verified owner completed its requeue"
              Expect.equal cancel.Result CancellationAccepted "racing cancellation was durably accepted"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("queued", claim.Fence.Value, None))
                  "attempt remained reclaimable under the old fence"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("queued", true))
                  "requeue preserved the accepted cancellation bit"

              let replacementOwner = "local:cancellation-finisher"
              let replacement =
                  match store.ClaimNextExecution(org, replacementOwner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected cancellation-finisher claim, got %A" other

              Expect.equal replacement.Fence.Value (claim.Fence.Value + 1L) "replacement received fresh authority"
              Expect.equal
                  (store.BeginExecution(
                      org,
                      replacement.AttemptId,
                      replacement.Fence,
                      replacementOwner,
                      60))
                  (Ok ExecutionCancelledBeforeStart)
                  "accepted cancellation prevented a replacement child launch"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("aborted", false))
                  "cancellation became the terminal public truth"
          }

          test "cancellation linearized before publication makes aborted the effective result" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "cancel-wins-publication" [ "build" ])

              let claim =
                  match store.ClaimNextExecution(org, "local:cancel-wins", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected claim, got %A" other

              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, "local:cancel-wins", 60))
                  (Ok ExecutionStarted)
                  "running"
              Expect.equal
                  (store.RequestCancellation(org, project, admitted.BuildId))
                  CancellationAccepted
                  "cancellation wins the build-row arbitration"
              Expect.isTrue
                  (Result.isOk
                      (store.PublishTerminal(org, claim.AttemptId, claim.Fence, "local:cancel-wins", Success)))
                  "publisher retains fenced authority"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("terminal", claim.Fence.Value, Some "aborted"))
                  "requested success cannot erase accepted cancellation"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("aborted", false))
                  "build agrees with attempt truth"
              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  0
                  "late cancellation is terminal arbitration, not manual reconciliation"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use terminalTruth = conn.CreateCommand()
              terminalTruth.CommandText <-
                  "SELECT
                       (SELECT payload->>'result' FROM events
                         WHERE organization_id = @o AND build_id = @b AND kind = 'attempt.terminal'),
                       (SELECT body->>'result' FROM outbox
                         WHERE organization_id = @o AND topic = 'build.terminal'
                           AND body->>'build' = @build)"
              terminalTruth.Parameters.AddWithValue("o", org.Value) |> ignore
              terminalTruth.Parameters.AddWithValue("b", admitted.BuildId.Value) |> ignore
              terminalTruth.Parameters.AddWithValue("build", admitted.BuildId.Value.ToString()) |> ignore
              use truth = terminalTruth.ExecuteReader()
              Expect.isTrue (truth.Read()) "terminal projections exist"
              Expect.equal (truth.GetString 0) "aborted" "event records effective result"
              Expect.equal (truth.GetString 1) "aborted" "outbox records effective result"
              truth.Close()

              use noReconciliationOutbox = conn.CreateCommand()
              noReconciliationOutbox.CommandText <-
                  "SELECT count(*) FROM outbox
                    WHERE organization_id = @o
                      AND topic = 'build.reconciliation_required'
                      AND body->>'build' = @build"
              noReconciliationOutbox.Parameters.AddWithValue("o", org.Value) |> ignore
              noReconciliationOutbox.Parameters.AddWithValue("build", admitted.BuildId.Value.ToString()) |> ignore
              Expect.equal
                  (noReconciliationOutbox.ExecuteScalar() :?> int64)
                  0L
                  "late cancellation emits no reconciliation outbox"
          }

          test "publication linearized before cancellation remains terminal" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "publication-wins-cancel" [ "build" ])

              let claim =
                  match store.ClaimNextExecution(org, "local:publish-wins", "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected claim, got %A" other

              Expect.equal
                  (store.BeginExecution(org, claim.AttemptId, claim.Fence, "local:publish-wins", 60))
                  (Ok ExecutionStarted)
                  "running"
              Expect.isTrue
                  (Result.isOk
                      (store.PublishTerminal(org, claim.AttemptId, claim.Fence, "local:publish-wins", Success)))
                  "publication wins the build-row arbitration"
              Expect.equal
                  (store.RequestCancellation(org, project, admitted.BuildId))
                  (AlreadyTerminal "success")
                  "later cancellation is not falsely accepted"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("success", false))
                  "terminal result remains stable"
          }

          test "invalid FIFO claims are quarantined once and cannot poison later work" {
              for poison, expectedReason in
                  [ "missing-definition", "missing_definition"
                    "legacy-multi-node", "legacy_multi_node"
                    "digest-mismatch", "definition_digest_mismatch" ] do
                  let org, project = freshProject ()
                  let invalid = admitOk (newBuild org project $"poison-{poison}" [ "build" ])

                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()

                  // Admission timestamps are immutable execution identity. Let
                  // PostgreSQL's own clock advance before admitting the valid
                  // row instead of rewriting either attempt after admission.
                  use separateAdmissions = conn.CreateCommand()
                  separateAdmissions.CommandText <- "SELECT pg_sleep(0.01)"
                  separateAdmissions.ExecuteNonQuery() |> ignore
                  let valid = admitOk (newBuild org project $"valid-after-{poison}" [ "build" ])

                  match poison with
                  | "legacy-multi-node" ->
                      use addNode = conn.CreateCommand()
                      addNode.CommandText <-
                          "INSERT INTO nodes
                               (id, organization_id, build_id, name, ordinal, required_trust_pool,
                                required_capabilities, status)
                           VALUES (@id, @o, @b, 'legacy-second-stage', 1,
                                   'trusted-linux', ARRAY['linux'], 'queued')"
                      addNode.Parameters.AddWithValue("id", Guid.NewGuid()) |> ignore
                      addNode.Parameters.AddWithValue("o", org.Value) |> ignore
                      addNode.Parameters.AddWithValue("b", invalid.BuildId.Value) |> ignore
                      Expect.equal (addNode.ExecuteNonQuery()) 1 "fixture added the incompatible second node"
                  | "missing-definition"
                  | "digest-mismatch" ->
                      try
                          use disable = conn.CreateCommand()
                          disable.CommandText <-
                              "ALTER TABLE build_definitions DISABLE TRIGGER build_definitions_guard"
                          disable.ExecuteNonQuery() |> ignore

                          use damage = conn.CreateCommand()
                          damage.CommandText <-
                              if poison = "missing-definition" then
                                  "DELETE FROM build_definitions
                                    WHERE organization_id = @o AND build_id = @b"
                              else
                                  "UPDATE build_definitions
                                      SET source_digest = decode(repeat('00', 32), 'hex')
                                    WHERE organization_id = @o AND build_id = @b"
                          damage.Parameters.AddWithValue("o", org.Value) |> ignore
                          damage.Parameters.AddWithValue("b", invalid.BuildId.Value) |> ignore
                          Expect.equal (damage.ExecuteNonQuery()) 1 $"fixture planted {poison}"
                      finally
                          use enable = conn.CreateCommand()
                          enable.CommandText <-
                              "ALTER TABLE build_definitions ENABLE TRIGGER build_definitions_guard"
                          enable.ExecuteNonQuery() |> ignore
                  | other -> failtestf "unknown poison fixture %s" other

                  let outboxBefore = store.CountOutbox org

                  use gate = new System.Threading.ManualResetEventSlim(false)
                  let claimers =
                      [| 1..8 |]
                      |> Array.map (fun index ->
                          System.Threading.Tasks.Task.Run(fun () ->
                              gate.Wait()
                              Store(connectionString).ClaimNextExecution(
                                  org,
                                  $"local:poison-race-{index}",
                                  "trusted-linux",
                                  [ "linux" ],
                                  60)))
                  gate.Set()
                  claimers
                  |> Array.map (fun task -> task :> System.Threading.Tasks.Task)
                  |> System.Threading.Tasks.Task.WaitAll

                  let results = claimers |> Array.map (fun task -> task.Result)
                  let errors = results |> Array.choose (function Error error -> Some error | Ok _ -> None)
                  Expect.isEmpty errors $"{poison} is durable state, not a recurring claim error"
                  let claims =
                      results
                      |> Array.choose (function Ok(Some claim) -> Some claim | _ -> None)
                  Expect.equal claims.Length 1 $"exactly one worker passed {poison} and claimed later work"
                  Expect.equal claims.[0].AttemptId valid.AttemptId $"{poison} did not starve the valid FIFO row"

                  Expect.equal
                      (store.AttemptState(org, invalid.AttemptId))
                      (Some("reconciliation_required", 0L, None))
                      $"{poison} attempt was quarantined without acquiring execution authority"
                  Expect.equal
                      (store.BuildSnapshot(org, project, invalid.BuildId))
                      (Some("reconciliation_required", false))
                      $"{poison} is visible in public build truth"

                  use truth = conn.CreateCommand()
                  truth.CommandText <-
                      "SELECT n.status,
                              count(DISTINCT e.id),
                              min(e.payload->>'reason'),
                              count(DISTINCT o.id),
                              min(o.topic),
                              min(o.body->>'reason'),
                              min(o.body->>'build'),
                              min(o.body->>'attempt')
                         FROM nodes n
                         LEFT JOIN events e
                           ON e.organization_id = n.organization_id
                          AND e.build_id = n.build_id
                          AND e.attempt_id = @a
                          AND e.kind = 'attempt.reconciliation_required'
                         LEFT JOIN outbox o
                           ON o.organization_id = n.organization_id
                          AND o.topic = 'build.reconciliation_required'
                          AND o.body->>'build' = n.build_id::text
                          AND o.body->>'attempt' = @a::text
                        WHERE n.organization_id = @o AND n.id = @n
                        GROUP BY n.status"
                  truth.Parameters.AddWithValue("o", org.Value) |> ignore
                  truth.Parameters.AddWithValue("n", invalid.NodeId.Value) |> ignore
                  truth.Parameters.AddWithValue("a", invalid.AttemptId.Value) |> ignore
                  use row = truth.ExecuteReader()
                  Expect.isTrue (row.Read()) $"{poison} node/event truth exists"
                  Expect.equal (row.GetString 0) "reconciliation_required" $"{poison} node was quarantined"
                  Expect.equal (row.GetInt64 1) 1L $"{poison} emitted exactly one refusal event"
                  Expect.equal (row.GetString 2) expectedReason $"{poison} event names the durable reason"
                  Expect.equal (row.GetInt64 3) 1L $"{poison} emitted exactly one reconciliation outbox row"
                  Expect.equal (row.GetString 4) "build.reconciliation_required" $"{poison} outbox names the transition"
                  Expect.equal (row.GetString 5) expectedReason $"{poison} outbox preserves the event reason"
                  Expect.equal (row.GetString 6) (invalid.BuildId.Value.ToString()) $"{poison} outbox binds the build"
                  Expect.equal (row.GetString 7) (invalid.AttemptId.Value.ToString()) $"{poison} outbox binds the attempt"
                  row.Close()

                  Expect.equal
                      (store.CountEvents(org, invalid.BuildId, "attempt.reconciliation_required"))
                      1
                      $"later claimers did not duplicate the {poison} refusal event"
                  Expect.equal
                      (store.CountOutbox org)
                      (outboxBefore + 1)
                      $"later claimers did not duplicate the {poison} refusal outbox"
          }

          test "a materialization failure is quarantined and cannot starve later execution" {
              let org, project = freshProject ()
              let poisoned = admitOk (newBuild org project "materialization-poison" [ "build" ])

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use separateAdmissions = conn.CreateCommand()
              separateAdmissions.CommandText <- "SELECT pg_sleep(0.01)"
              separateAdmissions.ExecuteNonQuery() |> ignore

              let valid = admitOk (newBuild org project "valid-after-materialization-poison" [ "build" ])
              let outboxBefore = store.CountOutbox org
              let poisonedOwner = "local:materialization-poison"

              let poisonedClaim =
                  match
                      store.ClaimNextExecution(
                          org,
                          poisonedOwner,
                          "trusted-linux",
                          [ "linux" ],
                          60)
                  with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected poisoned FIFO claim, got %A" other

              Expect.equal
                  poisonedClaim.AttemptId
                  poisoned.AttemptId
                  "the materialization fixture owns the oldest offered attempt"

              Expect.isTrue
                  (store.RequireReconciliation(
                      org,
                      poisonedClaim.AttemptId,
                      poisonedClaim.Fence,
                      poisonedOwner,
                      "materialization_failed"))
                  "the pre-execution materialization failure is durably quarantined"

              Expect.equal
                  (store.AttemptState(org, poisoned.AttemptId))
                  (Some("reconciliation_required", poisonedClaim.Fence.Value, None))
                  "the poisoned offer cannot become queued when its old lease expires"
              Expect.equal
                  (store.BuildSnapshot(org, project, poisoned.BuildId))
                  (Some("reconciliation_required", false))
                  "public build truth exposes the poisoned admission"
              Expect.equal
                  (store.CountEvents(org, poisoned.BuildId, "attempt.reconciliation_required"))
                  1
                  "the transition emits one durable reason event"
              Expect.equal (store.CountOutbox org) (outboxBefore + 1) "the same transaction emits one outbox row"

              use reasonTruth = conn.CreateCommand()
              reasonTruth.CommandText <-
                  "SELECT e.payload->>'reason', o.topic, o.body->>'reason',
                          o.body->>'build', o.body->>'attempt'
                     FROM events e
                     JOIN outbox o
                       ON o.organization_id = e.organization_id
                      AND o.topic = 'build.reconciliation_required'
                      AND o.body->>'attempt' = e.attempt_id::text
                    WHERE e.organization_id = @o AND e.build_id = @b
                      AND e.attempt_id = @a
                      AND e.kind = 'attempt.reconciliation_required'"
              reasonTruth.Parameters.AddWithValue("o", org.Value) |> ignore
              reasonTruth.Parameters.AddWithValue("b", poisoned.BuildId.Value) |> ignore
              reasonTruth.Parameters.AddWithValue("a", poisoned.AttemptId.Value) |> ignore
              use reasonRow = reasonTruth.ExecuteReader()
              Expect.isTrue (reasonRow.Read()) "reason event and outbox are atomically observable"
              Expect.equal (reasonRow.GetString 0) "materialization_failed" "event preserves the stable reason"
              Expect.equal (reasonRow.GetString 1) "build.reconciliation_required" "outbox topic names the transition"
              Expect.equal (reasonRow.GetString 2) "materialization_failed" "outbox preserves the same reason"
              Expect.equal (reasonRow.GetString 3) (poisoned.BuildId.Value.ToString()) "outbox binds the build"
              Expect.equal (reasonRow.GetString 4) (poisoned.AttemptId.Value.ToString()) "outbox binds the attempt"
              reasonRow.Close()

              Expect.isFalse
                  (store.RequireReconciliation(
                      org,
                      poisonedClaim.AttemptId,
                      poisonedClaim.Fence,
                      poisonedOwner,
                      "worker_exception"))
                  "a repeated stale transition cannot emit a second reason"
              Expect.equal
                  (store.CountEvents(org, poisoned.BuildId, "attempt.reconciliation_required"))
                  1
                  "stale replay emits no duplicate event"
              Expect.equal (store.CountOutbox org) (outboxBefore + 1) "stale replay emits no duplicate outbox"
              Expect.equal
                  (store.RequeueExpiredLocalAttempts org)
                  0
                  "lease recovery has no offered poison left to requeue"

              let validOwner = "local:after-materialization-poison"
              let validClaim =
                  match
                      store.ClaimNextExecution(
                          org,
                          validOwner,
                          "trusted-linux",
                          [ "linux" ],
                          60)
                  with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected later valid claim, got %A" other

              Expect.equal validClaim.AttemptId valid.AttemptId "FIFO advanced past the poison"
              Expect.equal
                  (store.BeginExecution(org, validClaim.AttemptId, validClaim.Fence, validOwner, 60))
                  (Ok ExecutionStarted)
                  "the later valid build remains executable"
              Expect.isTrue
                  (Result.isOk
                      (store.PublishTerminal(
                          org,
                          validClaim.AttemptId,
                          validClaim.Fence,
                          validOwner,
                          Success)))
                  "the later valid build can publish terminal truth"
          }

          test "expired offered reconciliation loses atomically to safe lease recovery" {
              let org, project = freshProject ()
              let admitted = admitOk (newBuild org project "expired-reconciliation-race" [ "build" ])
              let owner = "local:expired-reconciliation"

              let claim =
                  match store.ClaimNextExecution(org, owner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected offered race fixture, got %A" other

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use expire = conn.CreateCommand()
              expire.CommandText <-
                  "UPDATE attempts
                      SET lease_expires_at = clock_timestamp() - interval '1 second'
                    WHERE organization_id = @o AND id = @a"
              expire.Parameters.AddWithValue("o", org.Value) |> ignore
              expire.Parameters.AddWithValue("a", claim.AttemptId.Value) |> ignore
              Expect.equal (expire.ExecuteNonQuery()) 1 "the offered authority is deterministically expired"

              let outboxBefore = store.CountOutbox org
              use gate = new System.Threading.ManualResetEventSlim(false)
              let reconcile =
                  System.Threading.Tasks.Task.Run(fun () ->
                      gate.Wait()
                      Store(connectionString).RequireReconciliation(
                          org,
                          claim.AttemptId,
                          claim.Fence,
                          owner,
                          "materialization_failed"))
              let recover =
                  System.Threading.Tasks.Task.Run(fun () ->
                      gate.Wait()
                      Store(connectionString).RequeueExpiredLocalAttempts org)
              gate.Set()
              System.Threading.Tasks.Task.WaitAll
                  [| reconcile :> System.Threading.Tasks.Task
                     recover :> System.Threading.Tasks.Task |]

              Expect.isFalse reconcile.Result "expired authority cannot publish reconciliation"
              Expect.equal recover.Result 1 "lease recovery requeues the still-unstarted offer exactly once"
              Expect.equal
                  (store.AttemptState(org, admitted.AttemptId))
                  (Some("queued", claim.Fence.Value, None))
                  "the race converges on safe queued attempt truth"
              Expect.equal
                  (store.BuildSnapshot(org, project, admitted.BuildId))
                  (Some("queued", false))
                  "the node and build remain runnable"
              Expect.equal
                  (store.CountEvents(org, admitted.BuildId, "attempt.reconciliation_required"))
                  0
                  "the expired worker emits no reconciliation event"
              Expect.equal
                  (store.CountOutbox org)
                  outboxBefore
                  "the expired worker emits no reconciliation outbox"

              let replacement =
                  match
                      store.ClaimNextExecution(
                          org,
                          "local:replacement-after-expiry",
                          "trusted-linux",
                          [ "linux" ],
                          60)
                  with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected replacement claim after recovery, got %A" other

              Expect.equal replacement.AttemptId claim.AttemptId "replacement receives the recovered attempt"
              Expect.equal
                  replacement.Fence.Value
                  (claim.Fence.Value + 1L)
                  "replacement authority advances beyond the expired fence"
          }

          test "reconciliation requires the current restore epoch and preserves live running semantics" {
              let org, project = freshProject ()
              let live = admitOk (newBuild org project "live-running-reconciliation" [ "build" ])
              let liveOwner = "local:live-reconciliation"

              let liveClaim =
                  match store.ClaimNextExecution(org, liveOwner, "trusted-linux", [ "linux" ], 60) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected live reconciliation claim, got %A" other

              Expect.equal
                  (store.BeginExecution(org, liveClaim.AttemptId, liveClaim.Fence, liveOwner, 60))
                  (Ok ExecutionStarted)
                  "the valid post-launch fixture is running"
              Expect.isTrue
                  (store.RequireReconciliation(
                      org,
                      liveClaim.AttemptId,
                      liveClaim.Fence,
                      liveOwner,
                      "worker_exception"))
                  "a live current-epoch running owner retains reconciliation authority"
              Expect.equal
                  (store.AttemptState(org, live.AttemptId))
                  (Some("reconciliation_required", liveClaim.Fence.Value, None))
                  "valid post-launch reconciliation remains durable"
              Expect.equal
                  (store.CountEvents(org, live.BuildId, "attempt.reconciliation_required"))
                  1
                  "valid post-launch reconciliation publishes its reason"

              let stale = admitOk (newBuild org project "pre-restore-reconciliation" [ "build" ])
              let staleOwner = "local:pre-restore-reconciliation"
              let staleClaim =
                  match store.ClaimNextExecution(org, staleOwner, "trusted-linux", [ "linux" ], 300) with
                  | Ok(Some value) -> value
                  | other -> failtestf "expected pre-restore offered claim, got %A" other
              let outboxBeforeStaleAttempt = store.CountOutbox org

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use bump = conn.CreateCommand()
              bump.CommandText <-
                  "UPDATE controller_metadata
                      SET restore_epoch = restore_epoch + 1
                    WHERE singleton"
              Expect.equal (bump.ExecuteNonQuery()) 1 "the global restore epoch advances behind the offered authority"

              Expect.isFalse
                  (store.RequireReconciliation(
                      org,
                      staleClaim.AttemptId,
                      staleClaim.Fence,
                      staleOwner,
                      "materialization_failed"))
                  "pre-restore authority cannot publish a worker-selected reconciliation reason"
              Expect.equal
                  (store.AttemptState(org, stale.AttemptId))
                  (Some("offered", staleClaim.Fence.Value, None))
                  "the stale worker leaves active state untouched for restore recovery"
              Expect.equal
                  (store.CountEvents(org, stale.BuildId, "attempt.reconciliation_required"))
                  0
                  "the stale worker emits no reconciliation event"
              Expect.equal
                  (store.CountOutbox org)
                  outboxBeforeStaleAttempt
                  "the stale worker emits no reconciliation outbox"

              store.ActivateRestore() |> ignore
              Expect.equal
                  (store.AttemptState(org, stale.AttemptId))
                  (Some("reconciliation_required", staleClaim.Fence.Value, None))
                  "restore recovery owns the stale attempt's eventual disposition"
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

/// FG-026 Store foundation. Runtime effect invocation is deliberately outside
/// this package; these controls prove the durable prepare/advance/reconcile law.
let effectCheckpoints =
    testList
        "FG-026 effect checkpoint ledger"
        [ test "focused suite proves a live PostgreSQL effect ledger" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <-
                  "SELECT current_setting('server_version_num')::int,
                          EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0003'),
                          EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0005'),
                          EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0007'),
                          c.relrowsecurity,
                          c.relforcerowsecurity
                     FROM pg_class c
                     JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relname = 'effect_checkpoints'"
              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "live PostgreSQL returned one marker row"
              Expect.isGreaterThan (reader.GetInt32 0) 0 "real server version"
              Expect.isTrue (reader.GetBoolean 1) "migration 0003 installed"
              Expect.isTrue (reader.GetBoolean 2) "migration 0005 installed"
              Expect.isTrue (reader.GetBoolean 3) "migration 0007 installed"
              Expect.isTrue (reader.GetBoolean 4) "effect checkpoints have row security enabled"
              Expect.isTrue (reader.GetBoolean 5) "effect checkpoints force row security"
              printfn "FG026_LIVE_PG=1 FG026_SCHEMA=0003/0005/0007 FG026_CONCURRENCY=16"
          }

          test "prepare hashes exact payload bytes and stores no payload column" {
              let org, project = freshProject ()
              let admitted, fence = runningAttempt org project "effect-digest" "effect-agent" 60
              let payload = [| 0uy; 255uy; 13uy; 10uy; 65uy |]
              let outcome = prepareEffectOk org admitted.AttemptId fence "effect-agent" "notify:primary" payload

              use sha = SHA256.Create()
              let expectedDigest = Convert.ToHexString(sha.ComputeHash payload).ToLowerInvariant()

              Expect.isFalse outcome.WasReplay "first preparation creates the checkpoint"
              Expect.equal outcome.Checkpoint.PayloadSha256 expectedDigest "digest covers exact bytes"
              Expect.equal outcome.Checkpoint.State EffectPrepared "intent is durable before invocation"
              Expect.equal outcome.Checkpoint.RestoreEpoch (store.CurrentRestoreEpoch()) "current epoch captured"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use schema = conn.CreateCommand()
              schema.CommandText <-
                  "SELECT count(*) FROM information_schema.columns
                   WHERE table_schema = 'public'
                     AND table_name = 'effect_checkpoints'
                     AND column_name = 'payload'"
              Expect.equal (schema.ExecuteScalar() :?> int64) 0L "raw payload has no storage column"
          }

          test "identical replay is stable and payload substitution is refused" {
              let org, project = freshProject ()
              let admitted, fence = runningAttempt org project "effect-replay" "effect-agent" 60
              let payload = Text.Encoding.UTF8.GetBytes "same bytes"
              let first = prepareEffectOk org admitted.AttemptId fence "effect-agent" "deploy" payload
              let replay = prepareEffectOk org admitted.AttemptId fence "effect-agent" "deploy" payload

              Expect.isFalse first.WasReplay "first call inserts"
              Expect.isTrue replay.WasReplay "same exact preparation is replay-safe"
              Expect.equal replay.Checkpoint first.Checkpoint "replay returns the same durable identity"
              Expect.equal (checkpointCount org admitted.AttemptId) 1 "one row for one per-attempt key"

              expectError
                  "the same key cannot name different bytes"
                  (store.PrepareEffect(
                      org,
                      admitted.AttemptId,
                      fence,
                      "effect-agent",
                      "deploy",
                      Text.Encoding.UTF8.GetBytes "different bytes"))

              Expect.equal (checkpointCount org admitted.AttemptId) 1 "substitution has no side effect"
          }

          test "16 concurrent preparations converge on one checkpoint" {
              let org, project = freshProject ()
              let admitted, fence = runningAttempt org project "effect-race" "effect-agent" 60
              let payload = Text.Encoding.UTF8.GetBytes "one immutable request"

              let results =
                  [ 1..16 ]
                  |> List.map (fun _ ->
                      async {
                          return
                              Store(connectionString).PrepareEffect(
                                  org,
                                  admitted.AttemptId,
                                  fence,
                                  "effect-agent",
                                  "publish",
                                  payload)
                      })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              let outcomes =
                  results
                  |> Array.map (function
                      | Ok outcome -> outcome
                      | Error error -> failtestf "concurrent preparation failed: %s" error)

              Expect.equal outcomes.Length 16 "all callers completed"
              Expect.equal (outcomes |> Array.filter (fun value -> not value.WasReplay) |> Array.length) 1 "one insert"
              Expect.equal (outcomes |> Array.filter (fun value -> value.WasReplay) |> Array.length) 15 "fifteen replays"
              Expect.equal
                  (outcomes |> Array.map (fun outcome -> outcome.Checkpoint) |> Array.distinct |> Array.length)
                  1
                  "one durable truth"
              Expect.equal (checkpointCount org admitted.AttemptId) 1 "one database row"

              let replayMarker = $"FG026_PRELOCK_{Guid.NewGuid():N}"
              let replayConnectionString =
                  let builder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
                  builder.ApplicationName <- replayMarker
                  builder.ConnectionString

              use blocker = new Npgsql.NpgsqlConnection(connectionString)
              blocker.Open()
              use blockerTx = blocker.BeginTransaction()
              use blockerPidCmd = blocker.CreateCommand()
              blockerPidCmd.Transaction <- blockerTx
              blockerPidCmd.CommandText <- "SELECT pg_backend_pid()"
              let blockerPid = blockerPidCmd.ExecuteScalar() :?> int

              use lockCheckpoint = blocker.CreateCommand()
              lockCheckpoint.Transaction <- blockerTx
              lockCheckpoint.CommandText <-
                  "SELECT effect_key
                   FROM effect_checkpoints
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k
                   FOR UPDATE"
              lockCheckpoint.Parameters.AddWithValue("o", org.Value) |> ignore
              lockCheckpoint.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              lockCheckpoint.Parameters.AddWithValue("k", "publish") |> ignore
              Expect.equal (lockCheckpoint.ExecuteScalar() :?> string) "publish" "existing checkpoint row locked"

              let replayTask =
                  Async.StartAsTask(async {
                      return
                          Store(replayConnectionString).PrepareEffect(
                              org,
                              admitted.AttemptId,
                              fence,
                              "effect-agent",
                              "publish",
                              payload)
                  })

              use observer = new Npgsql.NpgsqlConnection(connectionString)
              observer.Open()

              let rec replayBlockedOnCheckpoint remaining =
                  use waiting = observer.CreateCommand()
                  waiting.CommandText <-
                      "SELECT EXISTS (
                           SELECT 1
                           FROM pg_stat_activity
                           WHERE application_name = @marker
                             AND state = 'active'
                             AND wait_event_type = 'Lock'
                             AND query LIKE '%effect_checkpoints%'
                             AND query LIKE '%FOR UPDATE%'
                             AND @blocker_pid = ANY(pg_blocking_pids(pid))
                       )"
                  waiting.Parameters.AddWithValue("marker", replayMarker) |> ignore
                  waiting.Parameters.AddWithValue("blocker_pid", blockerPid) |> ignore

                  if waiting.ExecuteScalar() :?> bool then
                      true
                  elif remaining = 0 then
                      false
                  else
                      Threading.Thread.Yield() |> ignore
                      replayBlockedOnCheckpoint (remaining - 1)

              let mutable barrierEstablished = false
              let mutable attemptLocked = false

              let observationError =
                  try
                      try
                          barrierEstablished <- replayBlockedOnCheckpoint 2000

                          if barrierEstablished then
                              use probe = new Npgsql.NpgsqlConnection(connectionString)
                              probe.Open()
                              use probeTx = probe.BeginTransaction()
                              use lockAttempt = probe.CreateCommand()
                              lockAttempt.Transaction <- probeTx
                              lockAttempt.CommandText <-
                                  "SELECT id
                                   FROM attempts
                                   WHERE organization_id = @o AND id = @a
                                   FOR UPDATE NOWAIT"
                              lockAttempt.Parameters.AddWithValue("o", org.Value) |> ignore
                              lockAttempt.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore

                              try
                                  lockAttempt.ExecuteScalar() |> ignore
                              with :? Npgsql.PostgresException as error when error.SqlState = "55P03" ->
                                  attemptLocked <- true

                          None
                      with ex ->
                          Some ex
                  finally
                      blockerTx.Rollback()

              let replayResult = replayTask.GetAwaiter().GetResult()

              match observationError with
              | Some error -> raise error
              | None -> ()

              Expect.isTrue barrierEstablished "replay blocked on the existing checkpoint behind the attested blocker"

              match replayResult with
              | Ok replay -> Expect.isTrue replay.WasReplay "blocked exact preparation remained a replay"
              | Error error -> failtestf "blocked exact replay failed: %s" error

              Expect.isTrue attemptLocked "replay held the attempt lock before waiting on the checkpoint"
          }

          test "prepare requires the exact live PublishTerminal authority" {
              let org, project = freshProject ()
              let admitted, fence = runningAttempt org project "effect-authority" "effect-agent" 60
              let payload = [| 1uy |]

              expectError
                  "stale fence"
                  (store.PrepareEffect(org, admitted.AttemptId, Fence(fence.Value + 1L), "effect-agent", "stale", payload))
              expectError
                  "wrong owner"
                  (store.PrepareEffect(org, admitted.AttemptId, fence, "other-agent", "owner", payload))

              let otherOrg, _ = freshProject ()
              expectError
                  "cross-organization authority"
                  (store.PrepareEffect(otherOrg, admitted.AttemptId, fence, "effect-agent", "tenant", payload))
              expectError
                  "missing attempt"
                  (store.PrepareEffect(org, AttemptId(Guid.NewGuid()), fence, "effect-agent", "missing", payload))

              let queued = admitOk (newBuild org project "effect-inactive" [ "effect" ])
              expectError
                  "inactive attempt"
                  (store.PrepareEffect(org, queued.AttemptId, Fence 0L, "effect-agent", "inactive", payload))

              let expired, expiredFence = runningAttempt org project "effect-expired" "effect-agent" 0
              Threading.Thread.Sleep 50
              expectError
                  "expired lease"
                  (store.PrepareEffect(org, expired.AttemptId, expiredFence, "effect-agent", "expired", payload))

              let beforeRestore, beforeRestoreFence =
                  runningAttempt org project "effect-restore" "effect-agent" 300
              store.ActivateRestore() |> ignore
              expectError
                  "pre-restore authority"
                  (store.PrepareEffect(
                      org,
                      beforeRestore.AttemptId,
                      beforeRestoreFence,
                      "effect-agent",
                      "restore",
                      payload))

              Expect.equal
                  (checkpointCountForOrg org + checkpointCountForOrg otherOrg)
                  0
                  "all probed attempts and organizations remain checkpoint-free"
          }

          test "legal advancement is ordered and replay-safe" {
              let org, project = freshProject ()
              let admitted, fence = runningAttempt org project "effect-advance" "effect-agent" 60
              let payload = Text.Encoding.UTF8.GetBytes "call body"
              prepareEffectOk org admitted.AttemptId fence "effect-agent" "call" payload |> ignore

              expectError
                  "confirmation cannot skip application"
                  (store.AdvanceEffect(
                      org,
                      admitted.AttemptId,
                      fence,
                      "effect-agent",
                      "call",
                      payload,
                      RecordConfirmed))

              let applied =
                  advanceEffectOk org admitted.AttemptId fence "effect-agent" "call" payload RecordApplied
              Expect.equal applied.Checkpoint.State EffectApplied "application recorded"
              Expect.isFalse applied.WasReplay "first application advances"

              let appliedAgain =
                  advanceEffectOk org admitted.AttemptId fence "effect-agent" "call" payload RecordApplied
              Expect.isTrue appliedAgain.WasReplay "application acknowledgement is idempotent"

              let confirmed =
                  advanceEffectOk org admitted.AttemptId fence "effect-agent" "call" payload RecordConfirmed
              Expect.equal confirmed.Checkpoint.State EffectConfirmed "confirmation recorded"
              Expect.isFalse confirmed.WasReplay "first confirmation advances"

              let confirmedAgain =
                  advanceEffectOk org admitted.AttemptId fence "effect-agent" "call" payload RecordConfirmed
              Expect.isTrue confirmedAgain.WasReplay "confirmation is idempotent"

              let lateAppliedReplay =
                  advanceEffectOk org admitted.AttemptId fence "effect-agent" "call" payload RecordApplied
              Expect.equal lateAppliedReplay.Checkpoint.State EffectConfirmed "late applied replay cannot regress"
              Expect.isTrue lateAppliedReplay.WasReplay "already confirmed satisfies applied"

              expectError
                  "advancement payload substitution"
                  (store.AdvanceEffect(
                      org,
                      admitted.AttemptId,
                      fence,
                      "effect-agent",
                      "call",
                      Text.Encoding.UTF8.GetBytes "other body",
                      RecordApplied))
          }

          test "stale prepared and applied effects become tenant-scoped uncertainty" {
              let org, project = freshProject ()
              let preparedAttempt, preparedFence = runningAttempt org project "effect-uncertain-p" "agent-p" 60
              let appliedAttempt, appliedFence = runningAttempt org project "effect-uncertain-a" "agent-a" 60
              let liveAttempt, liveFence = runningAttempt org project "effect-live" "agent-live" 60
              let payload = [| 9uy; 8uy; 7uy |]

              prepareEffectOk org preparedAttempt.AttemptId preparedFence "agent-p" "prepared" payload |> ignore
              prepareEffectOk org appliedAttempt.AttemptId appliedFence "agent-a" "applied" payload |> ignore
              advanceEffectOk org appliedAttempt.AttemptId appliedFence "agent-a" "applied" payload RecordApplied |> ignore
              prepareEffectOk org liveAttempt.AttemptId liveFence "agent-live" "live" payload |> ignore

              let foreignOrg, foreignProject = freshProject ()
              let foreignAttempt, foreignFence =
                  runningAttempt foreignOrg foreignProject "effect-foreign" "agent-foreign" 60
              prepareEffectOk foreignOrg foreignAttempt.AttemptId foreignFence "agent-foreign" "foreign" payload |> ignore

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use expire = conn.CreateCommand()
              expire.CommandText <-
                  "UPDATE attempts SET lease_expires_at = clock_timestamp() - interval '1 second'
                   WHERE (organization_id = @o AND id IN (@prepared, @applied))
                      OR (organization_id = @foreign_o AND id = @foreign)"
              expire.Parameters.AddWithValue("o", org.Value) |> ignore
              expire.Parameters.AddWithValue("prepared", preparedAttempt.AttemptId.Value) |> ignore
              expire.Parameters.AddWithValue("applied", appliedAttempt.AttemptId.Value) |> ignore
              expire.Parameters.AddWithValue("foreign_o", foreignOrg.Value) |> ignore
              expire.Parameters.AddWithValue("foreign", foreignAttempt.AttemptId.Value) |> ignore
              Expect.equal (expire.ExecuteNonQuery()) 3 "three authorities expired"

              let marked =
                  match store.MarkStaleEffectsUncertain org with
                  | Ok checkpoints -> checkpoints
                  | Error error -> failtestf "uncertainty marking failed: %s" error

              Expect.equal marked.Length 2 "only stale effects in the requested organization"
              let origins = marked |> List.map (fun value -> value.EffectKey, value.UncertainOrigin) |> Map.ofList
              Expect.equal origins.["prepared"] (Some UncertainAfterPrepare) "prepared origin retained"
              Expect.equal origins.["applied"] (Some UncertainAfterApply) "applied origin retained"
              let listed = store.ListUncertainEffects org
              Expect.equal (listed |> List.map (fun value -> value.EffectKey)) [ "prepared"; "applied" ] "stable list order"
              Expect.isEmpty (store.ListUncertainEffects foreignOrg) "foreign stale effect is not disclosed"

              let liveReplay =
                  prepareEffectOk org liveAttempt.AttemptId liveFence "agent-live" "live" payload
              Expect.equal liveReplay.Checkpoint.State EffectPrepared "live authority remains prepared"

              match store.MarkStaleEffectsUncertain org with
              | Ok repeated -> Expect.isEmpty repeated "uncertainty marking is idempotent"
              | Error error -> failtestf "repeated uncertainty marking failed: %s" error

              use renew = conn.CreateCommand()
              renew.CommandText <-
                  "UPDATE attempts SET lease_expires_at = clock_timestamp() + interval '1 minute'
                   WHERE organization_id = @o AND id = @a"
              renew.Parameters.AddWithValue("o", org.Value) |> ignore
              renew.Parameters.AddWithValue("a", preparedAttempt.AttemptId.Value) |> ignore
              Expect.equal (renew.ExecuteNonQuery()) 1 "authority restored only to probe terminal uncertainty"

              expectError
                  "uncertain is terminal until reconciliation"
                  (store.AdvanceEffect(
                      org,
                      preparedAttempt.AttemptId,
                      preparedFence,
                      "agent-p",
                      "prepared",
                      payload,
                      RecordApplied))
          }

          test "marker snapshot excludes a checkpoint committed behind its attempt locks" {
              let org, project = freshProject ()
              let firstAttempt, firstFence = runningAttempt org project "effect-snapshot-first" "agent-first" 60
              let lateAttempt, lateFence = runningAttempt org project "effect-snapshot-late" "agent-late" 60
              let payload = [| 4uy; 2uy |]
              prepareEffectOk org firstAttempt.AttemptId firstFence "agent-first" "snapshot-first" payload |> ignore

              use expireFirst = new Npgsql.NpgsqlConnection(connectionString)
              expireFirst.Open()
              use expireFirstCmd = expireFirst.CreateCommand()
              expireFirstCmd.CommandText <-
                  "UPDATE attempts SET lease_expires_at = clock_timestamp() - interval '1 second'
                   WHERE organization_id = @o AND id = @a"
              expireFirstCmd.Parameters.AddWithValue("o", org.Value) |> ignore
              expireFirstCmd.Parameters.AddWithValue("a", firstAttempt.AttemptId.Value) |> ignore
              Expect.equal (expireFirstCmd.ExecuteNonQuery()) 1 "first authority made stale"

              use blocker = new Npgsql.NpgsqlConnection(connectionString)
              blocker.Open()
              use blockerTx = blocker.BeginTransaction()
              use lockAttempt = blocker.CreateCommand()
              lockAttempt.Transaction <- blockerTx
              lockAttempt.CommandText <-
                  "SELECT id FROM attempts
                   WHERE organization_id = @o AND id = @a
                   FOR UPDATE"
              lockAttempt.Parameters.AddWithValue("o", org.Value) |> ignore
              lockAttempt.Parameters.AddWithValue("a", firstAttempt.AttemptId.Value) |> ignore
              Expect.isNotNull (lockAttempt.ExecuteScalar()) "test holds the first attempt lock"

              let firstPass =
                  Async.StartAsTask(async {
                      return Store(connectionString).MarkStaleEffectsUncertain org
                  })

              use observer = new Npgsql.NpgsqlConnection(connectionString)
              observer.Open()

              let rec markerSnapshotEstablished remaining =
                  use waiting = observer.CreateCommand()
                  waiting.CommandText <-
                      "SELECT EXISTS (
                           SELECT 1 FROM pg_stat_activity
                           WHERE pid <> pg_backend_pid()
                             AND state = 'active'
                             AND wait_event_type = 'Lock'
                             AND query LIKE '%FG026_MARKER_SNAPSHOT%'
                       )"

                  if waiting.ExecuteScalar() :?> bool then
                      true
                  elif remaining = 0 then
                      false
                  else
                      Threading.Thread.Sleep 20
                      markerSnapshotEstablished (remaining - 1)

              Expect.isTrue
                  (markerSnapshotEstablished 100)
                  "marker established its stable snapshot before blocking"

              prepareEffectOk org lateAttempt.AttemptId lateFence "agent-late" "snapshot-late" payload |> ignore

              use expireLate = observer.CreateCommand()
              expireLate.CommandText <-
                  "UPDATE attempts SET lease_expires_at = clock_timestamp() - interval '1 second'
                   WHERE organization_id = @o AND id = @a"
              expireLate.Parameters.AddWithValue("o", org.Value) |> ignore
              expireLate.Parameters.AddWithValue("a", lateAttempt.AttemptId.Value) |> ignore
              Expect.equal (expireLate.ExecuteNonQuery()) 1 "late checkpoint committed and then became stale"

              blockerTx.Commit()
              Expect.isTrue (firstPass.Wait(TimeSpan.FromSeconds 5.0)) "first bounded reconciliation completed"

              let firstMarked =
                  match firstPass.Result with
                  | Ok checkpoints -> checkpoints
                  | Error error -> failtestf "first reconciliation returned a database error: %s" error

              Expect.equal
                  (firstMarked |> List.map (fun checkpoint -> checkpoint.EffectKey))
                  [ "snapshot-first" ]
                  "first stable snapshot cannot see or lock the late checkpoint"

              let secondMarked =
                  match store.MarkStaleEffectsUncertain org with
                  | Ok checkpoints -> checkpoints
                  | Error error -> failtestf "second reconciliation returned a database error: %s" error

              Expect.equal
                  (secondMarked |> List.map (fun checkpoint -> checkpoint.EffectKey))
                  [ "snapshot-late" ]
                  "the next bounded pass reconciles the late checkpoint"
          }

          test "database trigger rejects hostile identity and transition SQL" {
              let org, project = freshProject ()
              let admitted, fence = runningAttempt org project "effect-trigger" "effect-agent" 60
              let payload = Text.Encoding.UTF8.GetBytes "trigger body"
              let prepared = prepareEffectOk org admitted.AttemptId fence "effect-agent" "guarded" payload
              prepareEffectOk org admitted.AttemptId fence "effect-agent" "timing-one" payload |> ignore
              prepareEffectOk org admitted.AttemptId fence "effect-agent" "timing-two" payload |> ignore
              prepareEffectOk org admitted.AttemptId fence "effect-agent" "timing-three" payload |> ignore
              advanceEffectOk org admitted.AttemptId fence "effect-agent" "timing-one" payload RecordApplied |> ignore
              advanceEffectOk org admitted.AttemptId fence "effect-agent" "timing-two" payload RecordApplied |> ignore

              let expectSqlRejected description sql bind =
                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- sql
                  bind cmd

                  try
                      cmd.ExecuteNonQuery() |> ignore
                      failtestf "%s unexpectedly succeeded" description
                  with :? Npgsql.PostgresException ->
                      ()

              let bindIdentity (cmd: Npgsql.NpgsqlCommand) =
                  cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                  cmd.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
                  cmd.Parameters.AddWithValue("k", "guarded") |> ignore

              let bindKey key (cmd: Npgsql.NpgsqlCommand) =
                  cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                  cmd.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
                  cmd.Parameters.AddWithValue("k", key) |> ignore

              expectSqlRejected
                  "digest rewrite"
                  "UPDATE effect_checkpoints
                   SET payload_digest = decode(repeat('00', 32), 'hex')
                  WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  bindIdentity

              expectSqlRejected
                  "checkpoint deletion"
                  "DELETE FROM effect_checkpoints
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  bindIdentity

              let afterDelete = prepareEffectOk org admitted.AttemptId fence "effect-agent" "guarded" payload
              Expect.isTrue afterDelete.WasReplay "reprepare cannot replace a deleted identity"

              expectSqlRejected
                  "effect key rewrite"
                  "UPDATE effect_checkpoints
                   SET effect_key = 'renamed'
                  WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  bindIdentity

              expectSqlRejected
                  "application before preparation"
                  "UPDATE effect_checkpoints
                   SET state = 'applied', applied_at = prepared_at - interval '1 second'
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  (bindKey "timing-three")

              expectSqlRejected
                  "confirmation before application"
                  "UPDATE effect_checkpoints
                   SET state = 'confirmed', confirmed_at = applied_at - interval '1 second'
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  (bindKey "timing-one")

              expectSqlRejected
                  "application timestamp rewrite during confirmation"
                  "UPDATE effect_checkpoints
                   SET state = 'confirmed',
                       applied_at = applied_at + interval '1 microsecond',
                       confirmed_at = clock_timestamp()
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  (bindKey "timing-two")

              expectSqlRejected
                  "prepared-to-confirmed jump"
                  "UPDATE effect_checkpoints
                   SET state = 'confirmed', applied_at = clock_timestamp(), confirmed_at = clock_timestamp()
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  bindIdentity

              expectSqlRejected
                  "live checkpoint forced uncertain"
                  "UPDATE effect_checkpoints
                   SET state = 'uncertain', uncertain_from = 'prepared', uncertain_at = clock_timestamp()
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  bindIdentity

              expectSqlRejected
                  "direct applied insertion"
                  "INSERT INTO effect_checkpoints
                     (organization_id, attempt_id, effect_key, fence, authority_owner,
                      restore_epoch, payload_digest, state, applied_at)
                   VALUES (@o, @a, 'direct', @f, @owner, @e, decode(@digest, 'hex'),
                           'applied', clock_timestamp())"
                  (fun cmd ->
                      cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                      cmd.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
                      cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
                      cmd.Parameters.AddWithValue("owner", "effect-agent") |> ignore
                      cmd.Parameters.AddWithValue("e", prepared.Checkpoint.RestoreEpoch.Value) |> ignore
                      cmd.Parameters.AddWithValue("digest", prepared.Checkpoint.PayloadSha256) |> ignore)

              expectSqlRejected
                  "future preparation time"
                  "INSERT INTO effect_checkpoints
                     (organization_id, attempt_id, effect_key, fence, authority_owner,
                      restore_epoch, payload_digest, state, prepared_at)
                   VALUES (@o, @a, 'future', @f, @owner, @e, decode(@digest, 'hex'),
                           'prepared', clock_timestamp() + interval '1 hour')"
                  (fun cmd ->
                      cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                      cmd.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
                      cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
                      cmd.Parameters.AddWithValue("owner", "effect-agent") |> ignore
                      cmd.Parameters.AddWithValue("e", prepared.Checkpoint.RestoreEpoch.Value) |> ignore
                      cmd.Parameters.AddWithValue("digest", prepared.Checkpoint.PayloadSha256) |> ignore)

              use expire = new Npgsql.NpgsqlConnection(connectionString)
              expire.Open()
              use expireCmd = expire.CreateCommand()
              expireCmd.CommandText <-
                  "UPDATE attempts SET lease_expires_at = clock_timestamp() - interval '1 second'
                   WHERE organization_id = @o AND id = @a"
              expireCmd.Parameters.AddWithValue("o", org.Value) |> ignore
              expireCmd.Parameters.AddWithValue("a", admitted.AttemptId.Value) |> ignore
              Expect.equal (expireCmd.ExecuteNonQuery()) 1 "authority expired for uncertainty ordering probe"

              expectSqlRejected
                  "uncertainty before preparation"
                  "UPDATE effect_checkpoints
                   SET state = 'uncertain', uncertain_from = 'prepared',
                       uncertain_at = prepared_at - interval '1 second'
                   WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k"
                  (bindKey "timing-three")

              Expect.equal afterDelete.Checkpoint.State EffectPrepared "all hostile statements were atomic"
              Expect.equal (checkpointCount org admitted.AttemptId) 4 "hostile insert and delete changed no rows"
          }

          test "invalid API inputs and unknown keys have no effect" {
              let org, project = freshProject ()
              let admitted, fence = runningAttempt org project "effect-inputs" "effect-agent" 60
              let payload = [| 1uy |]

              expectError "blank effect key" (store.PrepareEffect(org, admitted.AttemptId, fence, "effect-agent", "  ", payload))
              expectError
                  "oversized effect key"
                  (store.PrepareEffect(org, admitted.AttemptId, fence, "effect-agent", String.replicate 257 "k", payload))
              expectError "blank owner" (store.PrepareEffect(org, admitted.AttemptId, fence, " ", "key", payload))
              expectError
                  "null payload"
                  (store.PrepareEffect(org, admitted.AttemptId, fence, "effect-agent", "key", null))
              expectError
                  "unknown advancement key"
                  (store.AdvanceEffect(
                      org,
                      admitted.AttemptId,
                      fence,
                      "effect-agent",
                      "missing",
                      payload,
                      RecordApplied))

              Expect.equal (checkpointCount org admitted.AttemptId) 0 "invalid inputs are side-effect free"
          } ]

/// FG-026b. The bounded reconciliation trigger and its operator surface. This
/// list is deliberately separate from the ten-test FG-026 slice above, which the
/// gate's effect-ledger proof pins by name and count.
let effectReconciliation =
    let uncertaintySurface (org: OrganizationId) (attempt: AttemptId) =
        use conn = new Npgsql.NpgsqlConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT (SELECT count(*) FROM events e
                      WHERE e.organization_id = @o AND e.attempt_id = @a AND e.kind = 'effect.uncertain'),
                    (SELECT count(*) FROM outbox o
                      WHERE o.organization_id = @o AND o.topic = 'effect.uncertain'
                        AND o.body->>'attempt' = @a::text),
                    (SELECT string_agg(e.payload->>'effect_key' || '|' || (e.payload->>'uncertain_from') || '|' || (e.payload->>'reason'), ',' ORDER BY e.id)
                       FROM events e
                      WHERE e.organization_id = @o AND e.attempt_id = @a AND e.kind = 'effect.uncertain'),
                    (SELECT string_agg(o.body->>'effect_key' || '|' || (o.body->>'uncertain_from') || '|' || (o.body->>'reason') || '|' || (o.body->>'build'), ',' ORDER BY o.id)
                       FROM outbox o
                      WHERE o.organization_id = @o AND o.topic = 'effect.uncertain'
                        AND o.body->>'attempt' = @a::text)"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        use reader = cmd.ExecuteReader()
        Expect.isTrue (reader.Read()) "surface query returned a row"
        let events = reader.GetInt64 0
        let outbox = reader.GetInt64 1
        let eventBodies = if reader.IsDBNull 2 then "" else reader.GetString 2
        let outboxBodies = if reader.IsDBNull 3 then "" else reader.GetString 3
        reader.Close()
        events, outbox, eventBodies, outboxBodies

    let expireLeases (org: OrganizationId) (attempts: AttemptId list) =
        use conn = new Npgsql.NpgsqlConnection(connectionString)
        conn.Open()
        use expire = conn.CreateCommand()
        expire.CommandText <-
            "UPDATE attempts SET lease_expires_at = clock_timestamp() - interval '1 second'
             WHERE organization_id = @o AND id = ANY(@ids)"
        expire.Parameters.AddWithValue("o", org.Value) |> ignore
        expire.Parameters.AddWithValue("ids", attempts |> List.map (fun a -> a.Value) |> Array.ofList) |> ignore
        Expect.equal (expire.ExecuteNonQuery()) attempts.Length "every named authority expired"

    let reconcileOk org reason =
        match store.ReconcileStaleEffects(org, reason) with
        | Ok checkpoints -> checkpoints
        | Error error -> failtestf "effect reconciliation failed: %s" error

    testList
        "FG-026b effect reconciliation"
        [ test "a lease-expiry pass classifies stale work once and publishes one effect.uncertain event and outbox row per checkpoint" {
              let org, project = freshProject ()
              let preparedAttempt, preparedFence = runningAttempt org project "fg026b-prepared" "agent-p" 60
              let appliedAttempt, appliedFence = runningAttempt org project "fg026b-applied" "agent-a" 60
              let liveAttempt, liveFence = runningAttempt org project "fg026b-live" "agent-live" 60
              let payload = [| 2uy; 6uy; 98uy |]

              prepareEffectOk org preparedAttempt.AttemptId preparedFence "agent-p" "file-drop-receipt:p" payload |> ignore
              prepareEffectOk org appliedAttempt.AttemptId appliedFence "agent-a" "file-drop-receipt:a" payload |> ignore
              advanceEffectOk org appliedAttempt.AttemptId appliedFence "agent-a" "file-drop-receipt:a" payload RecordApplied |> ignore
              prepareEffectOk org liveAttempt.AttemptId liveFence "agent-live" "file-drop-receipt:live" payload |> ignore

              let foreignOrg, foreignProject = freshProject ()
              let foreignAttempt, foreignFence =
                  runningAttempt foreignOrg foreignProject "fg026b-foreign" "agent-foreign" 60
              prepareEffectOk foreignOrg foreignAttempt.AttemptId foreignFence "agent-foreign" "file-drop-receipt:f" payload |> ignore

              expireLeases org [ preparedAttempt.AttemptId; appliedAttempt.AttemptId ]
              expireLeases foreignOrg [ foreignAttempt.AttemptId ]

              let classified = reconcileOk org "lease_expired"
              Expect.equal classified.Length 2 "only the stale effects in the requested organization"
              let origins = classified |> List.map (fun c -> c.EffectKey, c.UncertainOrigin) |> Map.ofList
              Expect.equal origins.["file-drop-receipt:p"] (Some UncertainAfterPrepare) "prepared origin retained"
              Expect.equal origins.["file-drop-receipt:a"] (Some UncertainAfterApply) "applied origin retained"

              let pEvents, pOutbox, pEventBody, pOutboxBody = uncertaintySurface org preparedAttempt.AttemptId
              Expect.equal (pEvents, pOutbox) (1L, 1L) "exactly one event and one outbox row for the prepared checkpoint"
              Expect.equal pEventBody "file-drop-receipt:p|prepared|lease_expired" "event names key, origin and trigger reason"
              Expect.equal
                  pOutboxBody
                  $"file-drop-receipt:p|prepared|lease_expired|{preparedAttempt.BuildId.Value}"
                  "outbox names key, origin, reason and build lineage"

              let aEvents, aOutbox, aEventBody, _ = uncertaintySurface org appliedAttempt.AttemptId
              Expect.equal (aEvents, aOutbox) (1L, 1L) "exactly one event and one outbox row for the applied checkpoint"
              Expect.equal aEventBody "file-drop-receipt:a|applied|lease_expired" "applied origin is observable"

              let liveEvents, liveOutbox, _, _ = uncertaintySurface org liveAttempt.AttemptId
              Expect.equal (liveEvents, liveOutbox) (0L, 0L) "a live checkpoint is neither classified nor surfaced"
              let foreignEvents, foreignOutbox, _, _ = uncertaintySurface foreignOrg foreignAttempt.AttemptId
              Expect.equal (foreignEvents, foreignOutbox) (0L, 0L) "another organization's stale work is untouched by this tenant's pass"
              Expect.isEmpty (store.ListUncertainEffects foreignOrg) "the foreign organization lists nothing"

              Expect.equal (reconcileOk org "lease_expired") [] "a second pass finds nothing stale"
              let pEventsAgain, pOutboxAgain, _, _ = uncertaintySurface org preparedAttempt.AttemptId
              Expect.equal (pEventsAgain, pOutboxAgain) (1L, 1L) "a second pass publishes nothing more"

              // Both rows entered the set in one pass, so their uncertain_at may
              // tie; the listing is asserted as a set here, not an order.
              let listed = store.ListUncertainEffects org |> List.map (fun c -> c.EffectKey) |> List.sort
              Expect.equal listed [ "file-drop-receipt:a"; "file-drop-receipt:p" ] "the tenant listing carries both classified effects"

              match store.AdvanceEffect(org, preparedAttempt.AttemptId, preparedFence, "agent-p", "file-drop-receipt:p", payload, RecordApplied) with
              | Error _ -> ()
              | Ok _ -> failtest "an uncertain checkpoint must never advance again"

              Expect.equal (reconcileOk org "lease_expired") [] "a third pass after the refused advance still publishes nothing"
          }

          test "a reconciliation reason must be a stable lowercase code" {
              let org, _ = freshProject ()

              Expect.throwsT<ArgumentException>
                  (fun () -> store.ReconcileStaleEffects(org, "Lease Expired") |> ignore)
                  "mixed case and whitespace are refused before any transaction"

              Expect.throwsT<ArgumentException>
                  (fun () -> store.ReconcileStaleEffects(org, "") |> ignore)
                  "an empty reason is refused"
          }

          test "activating a restore classifies pre-restore prepared and applied effects atomically with the epoch bump" {
              let org, project = freshProject ()
              let preparedAttempt, preparedFence = runningAttempt org project "fg026b-restore-p" "agent-p" 300
              let appliedAttempt, appliedFence = runningAttempt org project "fg026b-restore-a" "agent-a" 300
              let confirmedAttempt, confirmedFence = runningAttempt org project "fg026b-restore-c" "agent-c" 300
              let payload = [| 42uy |]

              prepareEffectOk org preparedAttempt.AttemptId preparedFence "agent-p" "file-drop-receipt:p" payload |> ignore
              prepareEffectOk org appliedAttempt.AttemptId appliedFence "agent-a" "file-drop-receipt:a" payload |> ignore
              advanceEffectOk org appliedAttempt.AttemptId appliedFence "agent-a" "file-drop-receipt:a" payload RecordApplied |> ignore
              prepareEffectOk org confirmedAttempt.AttemptId confirmedFence "agent-c" "file-drop-receipt:c" payload |> ignore
              advanceEffectOk org confirmedAttempt.AttemptId confirmedFence "agent-c" "file-drop-receipt:c" payload RecordApplied |> ignore
              advanceEffectOk org confirmedAttempt.AttemptId confirmedFence "agent-c" "file-drop-receipt:c" payload RecordConfirmed |> ignore

              Expect.isEmpty (store.ListUncertainEffects org) "every checkpoint is live before the restore"

              let before = store.CurrentRestoreEpoch()
              let after = store.ActivateRestore()
              Expect.isGreaterThan after.Value before.Value "epoch advanced"

              let listed =
                  store.ListUncertainEffects org
                  |> List.map (fun c -> c.EffectKey, c.UncertainOrigin)
                  |> Map.ofList
              Expect.equal listed.Count 2 "the prepared and applied checkpoints became uncertain in the restore"
              Expect.equal listed.["file-drop-receipt:p"] (Some UncertainAfterPrepare) "prepared origin retained across restore"
              Expect.equal listed.["file-drop-receipt:a"] (Some UncertainAfterApply) "applied origin retained across restore"

              let pEvents, pOutbox, pEventBody, _ = uncertaintySurface org preparedAttempt.AttemptId
              Expect.equal (pEvents, pOutbox) (1L, 1L) "the restore published one event and one outbox row for the prepared checkpoint"
              Expect.equal pEventBody "file-drop-receipt:p|prepared|restore_epoch_advanced" "the restore reason is carried"
              let aEvents, aOutbox, _, aOutboxBody = uncertaintySurface org appliedAttempt.AttemptId
              Expect.equal (aEvents, aOutbox) (1L, 1L) "the restore published one pair for the applied checkpoint"
              Expect.equal
                  aOutboxBody
                  $"file-drop-receipt:a|applied|restore_epoch_advanced|{appliedAttempt.BuildId.Value}"
                  "the outbox binds the build lineage"
              let cEvents, cOutbox, _, _ = uncertaintySurface org confirmedAttempt.AttemptId
              Expect.equal (cEvents, cOutbox) (0L, 0L) "a confirmed checkpoint is final and surfaces nothing"

              Expect.equal (reconcileOk org "controller_startup") [] "the startup pass after a restore finds nothing left to classify"
              let pEventsAgain, _, _, _ = uncertaintySurface org preparedAttempt.AttemptId
              Expect.equal pEventsAgain 1L "the startup pass published nothing more"
          }

          test "the FG-026 marking primitive still publishes no operator surface" {
              let org, project = freshProject ()
              let attempt, fence = runningAttempt org project "fg026b-mark-only" "agent-m" 60
              let payload = [| 1uy |]
              prepareEffectOk org attempt.AttemptId fence "agent-m" "file-drop-receipt:m" payload |> ignore
              expireLeases org [ attempt.AttemptId ]

              match store.MarkStaleEffectsUncertain org with
              | Ok [ marked ] -> Expect.equal marked.State EffectUncertain "marked uncertain"
              | Ok other -> failtestf "expected one marked checkpoint, observed %A" other
              | Error error -> failtestf "marking failed: %s" error

              let events, outbox, _, _ = uncertaintySurface org attempt.AttemptId
              Expect.equal (events, outbox) (0L, 0L) "MarkStaleEffectsUncertain is the surface-free primitive FG-026 closed"
              Expect.equal (reconcileOk org "lease_expired") [] "a trigger pass after the primitive has nothing left and stays silent"
          }

          test "the uncertain listing pages by keyset in listing order, refuses another organization's cursor, and bounds the limit" {
              let org, project = freshProject ()
              let payload = [| 7uy |]
              // Prepared in order 1, 2, 3 but classified in order 3, 1, 2, one
              // pass each: the listing follows the classification instant
              // (uncertain_at), never the preparation instant.
              let attempts =
                  [ for index in 1..3 do
                        let attempt, fence = runningAttempt org project $"fg026b-page-{index}" "agent-page" 60
                        prepareEffectOk org attempt.AttemptId fence "agent-page" $"file-drop-receipt:{index}" payload |> ignore
                        System.Threading.Thread.Sleep 5
                        attempt.AttemptId ]
              for index in [ 3; 1; 2 ] do
                  expireLeases org [ attempts.[index - 1] ]
                  Expect.equal (reconcileOk org "lease_expired").Length 1 $"row {index} classified alone"
                  System.Threading.Thread.Sleep 5

              let page limit cursor =
                  match store.ListUncertainEffectsPage(org, cursor, limit) with
                  | Ok page -> page
                  | Error error -> failtestf "page failed: %s" error

              let keys (page: UncertainEffectPage) = page.Effects |> List.map (fun entry -> entry.Checkpoint.EffectKey)

              let expectedOrder = store.ListUncertainEffects org |> List.map (fun c -> c.EffectKey)
              Expect.equal expectedOrder [ "file-drop-receipt:3"; "file-drop-receipt:1"; "file-drop-receipt:2" ] "the unbounded listing is in classification order, not preparation order"

              let first = page 2 None
              Expect.equal (keys first) [ "file-drop-receipt:3"; "file-drop-receipt:1" ] "the first page holds the first two classified, in order"
              Expect.isSome first.NextCursor "a full page with a row behind it carries a cursor"
              Expect.isTrue (first.Effects.[0].UncertainAt <= first.Effects.[1].UncertainAt) "uncertain_at is non-decreasing down the page"

              let second = page 2 first.NextCursor
              Expect.equal (keys second) [ "file-drop-receipt:2" ] "the cursor continues without skipping or repeating"
              Expect.isNone second.NextCursor "the last page carries no cursor"

              let exact = page 3 None
              Expect.equal exact.Effects.Length 3 "a page that exactly fits holds every row"
              Expect.isNone exact.NextCursor "and carries no cursor when nothing is behind it"

              let single = page 1 None
              Expect.equal (keys single) [ "file-drop-receipt:3" ] "limit 1"
              let singleNext = page 1 single.NextCursor
              Expect.equal (keys singleNext) [ "file-drop-receipt:1" ] "limit 1, page 2"

              // Codex #424 round 6: a row that becomes uncertain behind an issued
              // cursor must appear on the next page. Row A was prepared before
              // row B, so a prepared_at keyset would have left A behind the
              // cursor issued after B; uncertain_at cannot.
              let lateOrg, lateProject = freshProject ()
              let prepareLate name =
                  let attempt, fence = runningAttempt lateOrg lateProject $"fg026b-late-{name}" "agent-late" 60
                  prepareEffectOk lateOrg attempt.AttemptId fence "agent-late" $"file-drop-receipt:{name}" payload |> ignore
                  System.Threading.Thread.Sleep 5
                  attempt.AttemptId
              let rowA = prepareLate "a"
              let rowB = prepareLate "b"
              let rowC = prepareLate "c"
              let classify attempt =
                  expireLeases lateOrg [ attempt ]
                  Expect.equal (reconcileOk lateOrg "lease_expired").Length 1 "one row classified"
                  System.Threading.Thread.Sleep 5
              classify rowB
              classify rowC
              let latePage limit cursor =
                  match store.ListUncertainEffectsPage(lateOrg, cursor, limit) with
                  | Ok page -> page
                  | Error error -> failtestf "late page failed: %s" error
              let pageOne = latePage 1 None
              Expect.equal (keys pageOne) [ "file-drop-receipt:b" ] "page 1 holds B, the first to enter the set"
              Expect.isSome pageOne.NextCursor "with C behind it"
              // A, prepared before B and C, enters the set only now — behind the
              // cursor the reader already holds.
              classify rowA
              let rec follow cursor collected =
                  match cursor with
                  | None -> List.rev collected
                  | Some _ ->
                      let next = latePage 1 cursor
                      follow next.NextCursor (List.rev (keys next) @ collected)
              Expect.equal
                  (follow pageOne.NextCursor [])
                  [ "file-drop-receipt:c"; "file-drop-receipt:a" ]
                  "following the cursor reaches C and then A: a prepared_at keyset would have left A behind the cursor"
              let allLate = latePage 10 None
              Expect.equal (keys allLate) [ "file-drop-receipt:b"; "file-drop-receipt:c"; "file-drop-receipt:a" ] "the full listing is in classification order"
              Expect.isTrue
                  (allLate.Effects |> List.pairwise |> List.forall (fun (x, y) -> x.UncertainAt <= y.UncertainAt))
                  "uncertain_at is non-decreasing down the listing"

              let foreignOrg, _ = freshProject ()
              match store.ListUncertainEffectsPage(foreignOrg, first.NextCursor, 10) with
              | Error error -> Expect.stringContains error "another organization" "a cursor is bound to the organization it was issued for"
              | Ok page -> failtestf "another organization accepted this tenant's cursor: %A" page

              match store.ListUncertainEffectsPage(org, Some "not-a-cursor", 10) with
              | Error error -> Expect.stringContains error "malformed" "garbage is refused"
              | Ok page -> failtestf "garbage cursor accepted: %A" page

              for badLimit in [ 0; -1; 1001 ] do
                  match store.ListUncertainEffectsPage(org, None, badLimit) with
                  | Error error -> Expect.stringContains error "1 through 1000" $"limit {badLimit} is refused"
                  | Ok page -> failtestf "limit %d accepted: %A" badLimit page

              Expect.equal (page 1000 None).Effects.Length 3 "the maximum limit is accepted"

              // Codex #424 round 5: a well-formed cursor with a hostile payload
              // must be a refusal before any connection, never a database error.
              // The unreachable store proves no round trip was attempted.
              let unreachable = Store("Host=127.0.0.1;Port=9;Username=nobody;Database=nowhere;Timeout=1")
              let forged (fields: string list) =
                  Convert.ToBase64String(Text.Encoding.UTF8.GetBytes(String.concat "|" ("fg026b-2" :: org.Value.ToString() :: fields)))
              let ticks = string DateTime.UtcNow.Ticks
              let attempt = Guid.NewGuid().ToString()
              let tampered =
                  [ "a NUL in the key", forged [ ticks; ticks; attempt; "file-drop-receipt:\000x" ]
                    "an oversized key", forged [ ticks; ticks; attempt; String.replicate 300 "k" ]
                    "an empty key", forged [ ticks; ticks; attempt; "" ]
                    "a whitespace key", forged [ ticks; ticks; attempt; "   " ]
                    "a non-GUID attempt", forged [ ticks; ticks; "not-an-attempt"; "file-drop-receipt:x" ]
                    "a garbage timestamp", forged [ "yesterday"; ticks; attempt; "file-drop-receipt:x" ]
                    "an out-of-range timestamp", forged [ "9999999999999999999"; ticks; attempt; "file-drop-receipt:x" ]
                    "a non-GUID organization", Convert.ToBase64String(Text.Encoding.UTF8.GetBytes $"fg026b-2|nope|{ticks}|{ticks}|{attempt}|k")
                    "invalid UTF-8", Convert.ToBase64String(Array.append (Text.Encoding.UTF8.GetBytes $"fg026b-2|{org.Value}|{ticks}|{ticks}|{attempt}|k") [| 0xFFuy; 0xFEuy |])
                    "a missing field", forged [ ticks; ticks; attempt ] ]

              for label, cursor in tampered do
                  match unreachable.ListUncertainEffectsPage(org, Some cursor, 10) with
                  | Error error -> Expect.stringContains error "malformed" $"{label} is refused as malformed without a database round trip"
                  | Ok page -> failtestf "%s was accepted: %A" label page

              // The genuine cursor still works, proving the validator is not
              // simply refusing everything.
              Expect.equal (page 2 first.NextCursor).Effects.Length 1 "a genuine cursor is still accepted after the validator"
          }

          test "the production trigger never waits on a live attempt: chunked, SKIP LOCKED, a held live row is untouched, a held stale row is skipped whether it sorts first or last, and an all-held remainder terminates" {
              // Codex #424 round 10 (thread fl7EL): the pass used to lock every
              // prepared/applied checkpoint's attempt and hold the locks while
              // emitting row by row, so a slow pass blocked a live worker's
              // RenewLease until its lease expired. Hosted run 33989057339 then
              // caught the first chunk loop terminating on the moved count: a
              // held row that sorted into the first chunk made it "short" and
              // 50 stale rows were left behind. The loop now terminates on
              // candidate exhaustion, so both orderings are asserted here.
              let scenario (label: string) (choose: AttemptId list -> AttemptId) =
                  let org, project = freshProject ()
                  let payload = [| 1uy; 0uy |]
                  let liveAttempt, liveFence = runningAttempt org project $"fg026b-live-lock-{label}" "agent-live" 60
                  prepareEffectOk org liveAttempt.AttemptId liveFence "agent-live" "file-drop-receipt:live" payload |> ignore

                  // 150 stale rows: more than one chunk of 100.
                  let stale =
                      [ for index in 1..150 do
                            let attempt, fence = runningAttempt org project $"fg026b-stale-{label}-{index}" "agent-stale" 60
                            prepareEffectOk org attempt.AttemptId fence "agent-stale" $"file-drop-receipt:s{index}" payload |> ignore
                            attempt.AttemptId ]
                  expireLeases org stale

                  // Another connection holds the LIVE attempt's row lock for the
                  // whole pass, and one STALE attempt's row too — chosen so that
                  // it sorts where the caller wants it in the candidate order.
                  let heldStale = choose stale
                  use holder = new Npgsql.NpgsqlConnection(connectionString)
                  holder.Open()
                  use holderTx = holder.BeginTransaction()
                  use hold = holder.CreateCommand()
                  hold.Transaction <- holderTx
                  hold.CommandText <-
                      "SELECT id FROM attempts WHERE organization_id = @o AND id IN (@live, @stale) ORDER BY id FOR UPDATE"
                  hold.Parameters.AddWithValue("o", org.Value) |> ignore
                  hold.Parameters.AddWithValue("live", liveAttempt.AttemptId.Value) |> ignore
                  hold.Parameters.AddWithValue("stale", heldStale.Value) |> ignore
                  use heldRows = hold.ExecuteReader()
                  let held = [ while heldRows.Read() do yield heldRows.GetGuid 0 ]
                  heldRows.Close()
                  Expect.equal held.Length 2 $"{label}: the test holds both row locks"

                  let pass = Async.StartAsTask(async { return Store(connectionString).ReconcileStaleEffects(org, "lease_expired") })
                  Expect.isTrue (pass.Wait(TimeSpan.FromSeconds 20.0)) $"{label}: the pass completed without waiting on the held rows"
                  let classified =
                      match pass.Result with
                      | Ok checkpoints -> checkpoints
                      | Error error -> failtestf "%s: the pass failed: %s" label error
                  Expect.equal classified.Length 149 $"{label}: every stale row except the one whose attempt is held was classified, across two chunks"
                  Expect.isFalse (classified |> List.exists (fun c -> c.AttemptId = liveAttempt.AttemptId)) $"{label}: the live row was never a candidate"
                  Expect.isFalse (classified |> List.exists (fun c -> c.AttemptId = heldStale)) $"{label}: the held stale row was skipped, not waited on"

                  holderTx.Commit()
                  Expect.isTrue (store.RenewLease(org, liveAttempt.AttemptId, liveFence, "agent-live", 60)) $"{label}: the live attempt renews its lease after the pass"
                  Expect.equal
                      (store.AdvanceEffect(org, liveAttempt.AttemptId, liveFence, "agent-live", "file-drop-receipt:live", payload, RecordApplied) |> Result.map (fun o -> o.Checkpoint.State))
                      (Ok EffectApplied)
                      $"{label}: the live checkpoint is untouched and still advances"

                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use surface = conn.CreateCommand()
                  surface.CommandText <-
                      "SELECT (SELECT count(*) FROM events WHERE organization_id = @o AND kind = 'effect.uncertain'),
                              (SELECT count(*) FROM outbox WHERE organization_id = @o AND topic = 'effect.uncertain'),
                              (SELECT count(DISTINCT uncertain_at) FROM effect_checkpoints WHERE organization_id = @o AND state = 'uncertain')"
                  surface.Parameters.AddWithValue("o", org.Value) |> ignore
                  use counts = surface.ExecuteReader()
                  Expect.isTrue (counts.Read()) "surface row"
                  Expect.equal (counts.GetInt64 0, counts.GetInt64 1) (149L, 149L) $"{label}: exactly one event and one outbox row per classified row"
                  Expect.equal (counts.GetInt64 2) 2L $"{label}: two chunks, two classification instants"
                  counts.Close()

                  // The skipped stale row is picked up by the next bounded pass,
                  // and the pass after that publishes nothing more.
                  Expect.equal (reconcileOk org "lease_expired" |> List.map (fun c -> c.AttemptId)) [ heldStale ] $"{label}: the next pass classifies the previously held stale row"
                  Expect.equal (reconcileOk org "lease_expired") [] $"{label}: and the pass after that finds nothing"

              // Candidates are ordered by attempt_id: hold the lowest (it lands
              // in the FIRST chunk, which is then short by one) and the highest
              // (it lands in the second).
              scenario "held-first" (List.minBy (fun (a: AttemptId) -> a.Value))
              scenario "held-last" (List.maxBy (fun (a: AttemptId) -> a.Value))

              // Every remaining candidate held: the pass terminates with zero
              // moved instead of spinning, and the next pass classifies them.
              let org, project = freshProject ()
              let payload = [| 3uy |]
              let allHeld =
                  [ for index in 1..3 do
                        let attempt, fence = runningAttempt org project $"fg026b-all-held-{index}" "agent-stale" 60
                        prepareEffectOk org attempt.AttemptId fence "agent-stale" $"file-drop-receipt:h{index}" payload |> ignore
                        attempt.AttemptId ]
              expireLeases org allHeld
              use holder = new Npgsql.NpgsqlConnection(connectionString)
              holder.Open()
              use holderTx = holder.BeginTransaction()
              use hold = holder.CreateCommand()
              hold.Transaction <- holderTx
              hold.CommandText <- "SELECT id FROM attempts WHERE organization_id = @o AND id = ANY(@ids) ORDER BY id FOR UPDATE"
              hold.Parameters.AddWithValue("o", org.Value) |> ignore
              hold.Parameters.AddWithValue("ids", allHeld |> List.map (fun a -> a.Value) |> Array.ofList) |> ignore
              use heldRows = hold.ExecuteReader()
              let held = [ while heldRows.Read() do yield heldRows.GetGuid 0 ]
              heldRows.Close()
              Expect.equal held.Length 3 "all three stale attempts are held"

              let watch = Diagnostics.Stopwatch.StartNew()
              Expect.equal (reconcileOk org "lease_expired") [] "a pass whose every candidate is held moves nothing and terminates"
              Expect.isLessThan watch.ElapsedMilliseconds 5000L "and does not spin"
              holderTx.Commit()
              Expect.equal (reconcileOk org "lease_expired" |> List.map (fun c -> c.AttemptId) |> List.sort) (List.sort allHeld) "the next pass classifies all three once they are released"
          }

          test "a held prefix of the candidate window is skipped, not counted: 250 stale rows with the 100 lowest held are classified past them in one pass" {
              // Codex #424 round 11 (thread fmyYR): with LIMIT applied before
              // SKIP LOCKED, a held first hundred filled the window every pass,
              // moved nothing, and the unlocked tail was never reached. The
              // lock now sits inside the window, so a held row never counts
              // toward it.
              let org, project = freshProject ()
              let payload = [| 2uy; 5uy; 0uy |]
              let stale =
                  [ for index in 1..250 do
                        let attempt, fence = runningAttempt org project $"fg026b-prefix-{index}" "agent-stale" 60
                        prepareEffectOk org attempt.AttemptId fence "agent-stale" $"file-drop-receipt:p{index}" payload |> ignore
                        attempt.AttemptId ]
              expireLeases org stale
              let heldPrefix = stale |> List.sortBy (fun a -> a.Value) |> List.take 100
              let tail = stale |> List.filter (fun a -> not (List.contains a heldPrefix))

              use holder = new Npgsql.NpgsqlConnection(connectionString)
              holder.Open()
              use holderTx = holder.BeginTransaction()
              use hold = holder.CreateCommand()
              hold.Transaction <- holderTx
              hold.CommandText <- "SELECT id FROM attempts WHERE organization_id = @o AND id = ANY(@ids) ORDER BY id FOR UPDATE"
              hold.Parameters.AddWithValue("o", org.Value) |> ignore
              hold.Parameters.AddWithValue("ids", heldPrefix |> List.map (fun a -> a.Value) |> Array.ofList) |> ignore
              use heldRows = hold.ExecuteReader()
              let held = [ while heldRows.Read() do yield heldRows.GetGuid 0 ]
              heldRows.Close()
              Expect.equal held.Length 100 "the hundred lowest attempt ids are held"

              let pass = Async.StartAsTask(async { return Store(connectionString).ReconcileStaleEffects(org, "lease_expired") })
              Expect.isTrue (pass.Wait(TimeSpan.FromSeconds 20.0)) "the pass completed without waiting on the held prefix"
              let classified =
                  match pass.Result with
                  | Ok checkpoints -> checkpoints
                  | Error error -> failtestf "the pass failed: %s" error
              Expect.equal (classified |> List.map (fun c -> c.AttemptId) |> List.sort) (List.sort tail) "a single pass classified the 150 unlocked rows past the held prefix"
              Expect.isFalse (classified |> List.exists (fun c -> List.contains c.AttemptId heldPrefix)) "no held row was touched"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use instants = conn.CreateCommand()
              instants.CommandText <- "SELECT count(DISTINCT uncertain_at) FROM effect_checkpoints WHERE organization_id = @o AND state = 'uncertain'"
              instants.Parameters.AddWithValue("o", org.Value) |> ignore
              Expect.equal (instants.ExecuteScalar() :?> int64) 2L "the 150 rows took two chunks (100 + 50): the window was full of unlocked rows, not of held ones"

              holderTx.Commit()
              Expect.equal (reconcileOk org "lease_expired" |> List.map (fun c -> c.AttemptId) |> List.sort) (List.sort heldPrefix) "the next pass classifies the released hundred"
              Expect.equal (reconcileOk org "lease_expired") [] "and the pass after that finds nothing"
          } ]

/// FG-027b Store foundation. This proves durable retry arbitration and replay;
/// scheduler/controller retry policy and dispatch remain deliberately outside
/// this package.
let retryDecisions =
    testList
        "FG-027b persistent retry decisions"
        [ test "focused suite proves live PostgreSQL migration 0004" {
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <-
                  "SELECT current_setting('server_version_num')::int,
                          EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0004'),
                          EXISTS (SELECT 1 FROM schema_migrations WHERE version = '0011')"
              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "live PostgreSQL returned one marker row"
              Expect.isGreaterThan (reader.GetInt32 0) 0 "real server version"
              Expect.isTrue (reader.GetBoolean 1) "migration 0004 installed"
              Expect.isTrue (reader.GetBoolean 2) "migration 0011 installed"
              printfn "FG027B_LIVE_PG=1 FG027B_SCHEMA=0011 FG027B_CONCURRENCY=16"
          }

          test "fresh child decision atomically creates child decision event and outbox" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-atomic"
              let outboxBefore = store.CountOutbox org
              let childId = AttemptId(Guid.NewGuid())
              let outcome = decideRetryOk org parent.AttemptId 2 childId

              Expect.isFalse outcome.WasReplay "fresh decision"

              match outcome.Persisted.Decision.Outcome with
              | BudgetExhausted -> failtest "budget unexpectedly exhausted"
              | ChildCreated child ->
                  Expect.equal child.Id childId "proposed identity became the child"
                  Expect.equal child.OrganizationId org "tenant retained"
                  Expect.equal child.Ordinal 1 "ordinal incremented exactly once"
                  Expect.equal child.RetryOf (Some parent.AttemptId) "immutable parent link"
                  Expect.equal child.State Queued "creation snapshot is queued"
                  Expect.equal child.Fence Fence.initial "creation fence is initial"
                  Expect.isNone child.LeaseOwner "creation has no lease"

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <-
                  "SELECT state, fence, retry_of, result IS NULL,
                          lease_owner IS NULL AND lease_expires_at IS NULL,
                          n.status, b.status, b.cancellation_requested
                     FROM attempts a
                     JOIN nodes n
                       ON n.organization_id = a.organization_id AND n.id = a.node_id
                     JOIN builds b
                       ON b.organization_id = n.organization_id AND b.id = n.build_id
                    WHERE a.organization_id = @o AND a.id = @a"
              cmd.Parameters.AddWithValue("o", org.Value) |> ignore
              cmd.Parameters.AddWithValue("a", childId.Value) |> ignore
              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "child committed"
              Expect.equal (reader.GetString 0) "queued" "durable child starts queued"
              Expect.equal (reader.GetInt64 1) 0L "durable child starts at fence zero"
              Expect.equal (reader.GetGuid 2) parent.AttemptId.Value "durable ancestry"
              Expect.isTrue (reader.GetBoolean 3) "child has no result"
              Expect.isTrue (reader.GetBoolean 4) "child has no lease"
              Expect.equal (reader.GetString 5) "queued" "retry reopens the node"
              Expect.equal (reader.GetString 6) "queued" "retry reopens the build"
              Expect.isFalse (reader.GetBoolean 7) "retry does not manufacture cancellation"
              Expect.equal (retryDecisionCount org parent.AttemptId) 1 "one decision row"
              Expect.equal (store.CountEvents(org, parent.BuildId, "retry.decided")) 1 "one event"
              Expect.equal (store.CountOutbox org) (outboxBefore + 1) "one outbox addition"
          }

          test "retry reopening makes cancellation effective before child launch" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-cancel-child"
              let childId = AttemptId(Guid.NewGuid())
              decideRetryOk org parent.AttemptId 2 childId |> ignore

              Expect.equal
                  (store.BuildSnapshot(org, project, parent.BuildId))
                  (Some("queued", false))
                  "fresh retry is publicly queued"
              Expect.equal
                  (store.RequestCancellation(org, project, parent.BuildId))
                  CancellationAccepted
                  "reopened build accepts cancellation"

              let claim =
                  match
                      store.ClaimNextExecution(
                          org,
                          "local:retry-cancel-child",
                          "trusted-linux",
                          [ "linux" ],
                          60)
                  with
                  | Ok(Some value) -> value
                  | Ok None -> failtest "queued retry child was not claimable"
                  | Error error -> failtestf "retry child claim failed: %s" error

              Expect.equal claim.AttemptId childId "the retry child was claimed"
              Expect.equal
                  (store.BeginExecution(
                      org,
                      claim.AttemptId,
                      claim.Fence,
                      "local:retry-cancel-child",
                      60))
                  (Ok ExecutionCancelledBeforeStart)
                  "accepted cancellation prevents child launch"
              Expect.equal
                  (store.AttemptState(org, childId))
                  (Some("terminal", claim.Fence.Value, Some "aborted"))
                  "retry child terminates without running"
              Expect.equal
                  (store.BuildSnapshot(org, project, parent.BuildId))
                  (Some("aborted", false))
                  "public state records the pre-launch abort"
          }

          test "database retry invariant reopens direct SQL child creation and publishes its build" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-sql-invariant"
              let childId = Guid.NewGuid()

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use tx = conn.BeginTransaction()
              use create = conn.CreateCommand()
              create.Transaction <- tx
              create.CommandText <-
                  "INSERT INTO attempts
                       (id, organization_id, node_id, ordinal, retry_of,
                        state, fence, restore_epoch, lease_owner, lease_expires_at, result)
                   VALUES
                       (@child, @o, @node, 1, @parent,
                        'queued', 0,
                        (SELECT restore_epoch FROM controller_metadata WHERE singleton),
                        NULL, NULL, NULL);
                   INSERT INTO retry_decisions
                       (organization_id, parent_attempt_id, parent_node_id,
                        parent_ordinal, parent_retry_of, parent_restore_epoch,
                        attempt_limit, outcome, child_attempt_id, dead_letter_reason)
                   VALUES
                       (@o, @parent, @node, 0, NULL,
                        (SELECT restore_epoch FROM controller_metadata WHERE singleton),
                        2, 'child_created', @child, NULL)"
              create.Parameters.AddWithValue("child", childId) |> ignore
              create.Parameters.AddWithValue("o", org.Value) |> ignore
              create.Parameters.AddWithValue("node", parent.NodeId.Value) |> ignore
              create.Parameters.AddWithValue("parent", parent.AttemptId.Value) |> ignore
              create.ExecuteNonQuery() |> ignore
              tx.Commit()

              Expect.equal
                  (store.BuildSnapshot(org, project, parent.BuildId))
                  (Some("queued", false))
                  "database trigger owns aggregate reopening"

              use publication = conn.CreateCommand()
              publication.CommandText <-
                  "SELECT body->>'build', body->>'buildStatus'
                     FROM outbox
                    WHERE organization_id = @o
                      AND topic = 'retry.decided'
                      AND body->>'parentAttempt' = @parent"
              publication.Parameters.AddWithValue("o", org.Value) |> ignore
              publication.Parameters.AddWithValue("parent", parent.AttemptId.Value.ToString()) |> ignore
              use row = publication.ExecuteReader()
              Expect.isTrue (row.Read()) "direct decision published one compensating transition"
              Expect.equal (row.GetString 0) (parent.BuildId.Value.ToString()) "publication binds the build"
              Expect.equal (row.GetString 1) "queued" "publication carries the reopened state"
              Expect.isFalse (row.Read()) "direct decision has one publication"
          }

          test "execution claim requires both queued node and queued build aggregates" {
              let nodeOrg, nodeProject = freshProject ()
              let nodeMismatch =
                  admitOk (newBuild nodeOrg nodeProject "retry-node-mismatch" [ "retry" ])
              let buildOrg, buildProject = freshProject ()
              let buildMismatch =
                  admitOk (newBuild buildOrg buildProject "retry-build-mismatch" [ "retry" ])

              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use poison = conn.CreateCommand()
              poison.CommandText <-
                  "UPDATE nodes SET status = 'failure'
                    WHERE organization_id = @node_org AND id = @node;
                   UPDATE builds SET status = 'failure'
                    WHERE organization_id = @build_org AND id = @build"
              poison.Parameters.AddWithValue("node_org", nodeOrg.Value) |> ignore
              poison.Parameters.AddWithValue("node", nodeMismatch.NodeId.Value) |> ignore
              poison.Parameters.AddWithValue("build_org", buildOrg.Value) |> ignore
              poison.Parameters.AddWithValue("build", buildMismatch.BuildId.Value) |> ignore
              poison.ExecuteNonQuery() |> ignore

              Expect.equal
                  (store.ClaimNextExecution(
                      nodeOrg,
                      "local:retry-node-mismatch",
                      "trusted-linux",
                      [ "linux" ],
                      60))
                  (Ok None)
                  "terminal node aggregate prevents a queued attempt claim"
              Expect.equal
                  (store.ClaimNextExecution(
                      buildOrg,
                      "local:retry-build-mismatch",
                      "trusted-linux",
                      [ "linux" ],
                      60))
                  (Ok None)
                  "terminal build aggregate prevents a queued attempt claim"
          }

          test "replay ignores hostile fresh inputs and returns exact prior bytes" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-replay-hostile"
              let first = decideRetryOk org parent.AttemptId 2 (AttemptId(Guid.NewGuid()))
              let replay = decideRetryOk org parent.AttemptId 999 (AttemptId(Guid.NewGuid()))

              Expect.isTrue replay.WasReplay "prior decision recognized"
              Expect.equal replay.Persisted first.Persisted "replay returns the exact persisted snapshot"
              Expect.equal (retryDecisionCount org parent.AttemptId) 1 "hostile inputs create nothing"
              Expect.equal (store.CountEvents(org, parent.BuildId, "retry.decided")) 1 "event is not replayed"
          }

          test "replay returns queued creation snapshot after the live child advances" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-child-moved"
              let first = decideRetryOk org parent.AttemptId 2 (AttemptId(Guid.NewGuid()))

              let child =
                  match first.Persisted.Decision.Outcome with
                  | ChildCreated value -> value
                  | BudgetExhausted -> failtest "expected a child"

              let liveFence =
                  match store.OfferAttempt(org, child.Id, "child-agent", 60) with
                  | Ok value -> value
                  | Error error -> failtestf "child offer failed: %s" error

              Expect.equal liveFence.Value 1L "live child advanced its fence"
              Expect.equal
                  (store.BeginExecution(org, child.Id, liveFence, "child-agent", 60))
                  (Ok ExecutionStarted)
                  "live child entered running state"
              let replay = decideRetryOk org parent.AttemptId 1 parent.AttemptId

              match replay.Persisted.Decision.Outcome with
              | BudgetExhausted -> failtest "replay was recomputed from hostile inputs"
              | ChildCreated snapshot ->
                  Expect.equal snapshot child "queued creation snapshot is retained"

              Expect.equal replay.Persisted first.Persisted "live state cannot rewrite creation history"
              Expect.equal
                  (store.BuildSnapshot(org, project, parent.BuildId))
                  (Some("running", false))
                  "replay cannot reopen or regress the live aggregate"
          }

          test "16 concurrent callers converge on one immutable result" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-race"

              let results =
                  [ 1..16 ]
                  |> List.map (fun _ ->
                      async {
                          return
                              Store(connectionString).DecideRetry(
                                  org,
                                  parent.AttemptId,
                                  2,
                                  AttemptId(Guid.NewGuid()))
                      })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              let outcomes =
                  results
                  |> Array.map (function
                      | Ok value -> value
                      | Error error -> failtestf "concurrent retry decision failed: %A" error)

              Expect.equal (outcomes |> Array.filter (fun value -> not value.WasReplay) |> Array.length) 1 "one writer"
              Expect.equal (outcomes |> Array.filter (fun value -> value.WasReplay) |> Array.length) 15 "fifteen replays"
              Expect.equal
                  (outcomes |> Array.map (fun value -> value.Persisted) |> Array.distinct |> Array.length)
                  1
                  "all callers receive one snapshot"
              Expect.equal (retryDecisionCount org parent.AttemptId) 1 "one durable arbiter row"
              Expect.equal (store.CountEvents(org, parent.BuildId, "retry.decided")) 1 "one event"

              let expected = outcomes.[0].Persisted
              let replayMarker = $"FG027B_PRELOCK_{Guid.NewGuid():N}"
              let replayBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              replayBuilder.ApplicationName <- replayMarker

              use blocker = new Npgsql.NpgsqlConnection(connectionString)
              blocker.Open()
              use blockerTx = blocker.BeginTransaction()
              use blockerPidCmd = blocker.CreateCommand()
              blockerPidCmd.Transaction <- blockerTx
              blockerPidCmd.CommandText <- "SELECT pg_backend_pid()"
              let blockerPid = blockerPidCmd.ExecuteScalar() :?> int

              use lockDecision = blocker.CreateCommand()
              lockDecision.Transaction <- blockerTx
              lockDecision.CommandText <-
                  "SELECT parent_attempt_id
                   FROM retry_decisions
                   WHERE organization_id = @o AND parent_attempt_id = @a
                   FOR UPDATE"
              lockDecision.Parameters.AddWithValue("o", org.Value) |> ignore
              lockDecision.Parameters.AddWithValue("a", parent.AttemptId.Value) |> ignore
              Expect.equal
                  (lockDecision.ExecuteScalar() :?> Guid)
                  parent.AttemptId.Value
                  "test holds the exact prior decision row"

              let replayTask =
                  Async.StartAsTask(async {
                      return
                          Store(replayBuilder.ConnectionString).DecideRetry(
                              org,
                              parent.AttemptId,
                              1,
                              parent.AttemptId)
                  })

              use observer = new Npgsql.NpgsqlConnection(connectionString)
              observer.Open()

              let replayWait = Diagnostics.Stopwatch.StartNew()

              let rec replayBlockedOnDecision () =
                  use waiting = observer.CreateCommand()
                  waiting.CommandText <-
                      "SELECT EXISTS (
                           SELECT 1
                           FROM pg_stat_activity
                           WHERE application_name = @marker
                             AND state = 'active'
                             AND wait_event_type = 'Lock'
                             AND query LIKE '%retry_decisions%'
                             AND query LIKE '%FOR UPDATE OF d%'
                             AND @blocker_pid = ANY(pg_blocking_pids(pid))
                       )"
                  waiting.Parameters.AddWithValue("marker", replayMarker) |> ignore
                  waiting.Parameters.AddWithValue("blocker_pid", blockerPid) |> ignore

                  if waiting.ExecuteScalar() :?> bool then
                      true
                  elif replayWait.Elapsed >= TimeSpan.FromSeconds 10.0 then
                      false
                  else
                      Threading.Thread.Sleep 20
                      replayBlockedOnDecision ()

              let mutable barrierEstablished = false
              let mutable parentLocked = false

              let observationError =
                  try
                      try
                          barrierEstablished <- replayBlockedOnDecision ()

                          if barrierEstablished then
                              use probe = new Npgsql.NpgsqlConnection(connectionString)
                              probe.Open()
                              use probeTx = probe.BeginTransaction()
                              use lockParent = probe.CreateCommand()
                              lockParent.Transaction <- probeTx
                              lockParent.CommandText <-
                                  "SELECT id
                                   FROM attempts
                                   WHERE organization_id = @o AND id = @a
                                   FOR UPDATE NOWAIT"
                              lockParent.Parameters.AddWithValue("o", org.Value) |> ignore
                              lockParent.Parameters.AddWithValue("a", parent.AttemptId.Value) |> ignore

                              try
                                  lockParent.ExecuteScalar() |> ignore
                              with :? Npgsql.PostgresException as error when error.SqlState = "55P03" ->
                                  parentLocked <- true

                          None
                      with ex ->
                          Some ex
                  finally
                      blockerTx.Rollback()

              let replayResult = replayTask.GetAwaiter().GetResult()

              match observationError with
              | Some error -> raise error
              | None -> ()

              Expect.isTrue barrierEstablished "replay visibly blocked on the exact prior decision row"
              Expect.isTrue parentLocked "replay held the exact parent lock before waiting on prior truth"

              match replayResult with
              | Ok replay ->
                  Expect.isTrue replay.WasReplay "blocked call remained a replay"
                  Expect.equal replay.Persisted expected "blocked replay returned exact prior truth"
              | Error error -> failtestf "blocked exact replay failed: %A" error
          }

          test "zero-based boundary creates below the limit and dead-letters at it" {
              let org, project = freshProject ()
              let below = terminalAttempt org project "retry-boundary-child"
              let at = terminalAttempt org project "retry-boundary-dead"

              match (decideRetryOk org below.AttemptId 2 (AttemptId(Guid.NewGuid()))).Persisted.Decision.Outcome with
              | ChildCreated child -> Expect.equal child.Ordinal 1 "ordinal one is below limit two"
              | BudgetExhausted -> failtest "below-boundary retry was exhausted"

              let exhausted = decideRetryOk org at.AttemptId 1 (AttemptId(Guid.NewGuid()))

              match exhausted.Persisted.Decision.Outcome with
              | ChildCreated _ -> failtest "at-boundary retry created a child"
              | BudgetExhausted ->
                  Expect.equal
                      exhausted.Persisted.DeadLetterReason
                      (Some "attempt budget exhausted")
                      "exact dead-letter reason"

              Expect.equal
                  (store.BuildSnapshot(org, project, at.BuildId))
                  (Some("failure", false))
                  "budget exhaustion leaves the terminal aggregate closed"
              Expect.equal
                  (store.RequestCancellation(org, project, at.BuildId))
                  (AlreadyTerminal "failure")
                  "budget exhaustion does not reopen cancellation"
          }

          test "dead-letter replay is exact and emits no second publication" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-dead-replay"
              let first = decideRetryOk org parent.AttemptId 1 (AttemptId(Guid.NewGuid()))
              let replay = decideRetryOk org parent.AttemptId 999 (AttemptId(Guid.NewGuid()))

              Expect.isTrue replay.WasReplay "dead-letter is replayed"
              Expect.equal replay.Persisted first.Persisted "dead-letter bytes are stable"
              Expect.equal (store.CountEvents(org, parent.BuildId, "retry.decided")) 1 "single event"
          }

          test "invalid active cross-tenant and corrupt parents are side-effect free" {
              let org, project = freshProject ()
              let active = admitOk (newBuild org project "retry-active" [ "retry" ])
              let otherOrg, _ = freshProject ()

              match store.DecideRetry(org, active.AttemptId, 2, AttemptId(Guid.NewGuid())) with
              | Error(RetryLawRejected(ParentNotTerminal Queued)) -> ()
              | other -> failtestf "active parent returned %A" other

              match store.DecideRetry(otherOrg, active.AttemptId, 2, AttemptId(Guid.NewGuid())) with
              | Error RetryParentUnavailable -> ()
              | other -> failtestf "cross-tenant parent returned %A" other

              let terminal = terminalAttempt org project "retry-invalid-child"

              match store.DecideRetry(org, terminal.AttemptId, 2, AttemptId(Guid.Empty)) with
              | Error(RetryLawRejected(InvalidProposedChildIdentity _)) -> ()
              | other -> failtestf "empty child returned %A" other

              let corrupt = terminalAttempt org project "retry-corrupt-result"
              let corruptOutboxBefore = store.CountOutbox org
              use conn = new Npgsql.NpgsqlConnection(connectionString)
              conn.Open()
              use breakResult = conn.CreateCommand()
              breakResult.CommandText <-
                  "UPDATE attempts SET result = 'not-a-result'
                   WHERE organization_id = @o AND id = @a"
              breakResult.Parameters.AddWithValue("o", org.Value) |> ignore
              breakResult.Parameters.AddWithValue("a", corrupt.AttemptId.Value) |> ignore
              Expect.equal (breakResult.ExecuteNonQuery()) 1 "test installed malformed result"

              match store.DecideRetry(org, corrupt.AttemptId, 2, AttemptId(Guid.NewGuid())) with
              | Error(RetryDecisionCorrupt _) -> ()
              | other -> failtestf "corrupt parent returned %A" other

              use direct = conn.CreateCommand()
              direct.CommandText <-
                  "INSERT INTO retry_decisions
                     (organization_id, parent_attempt_id, parent_node_id,
                      parent_ordinal, parent_retry_of, parent_restore_epoch,
                      attempt_limit, outcome, child_attempt_id, dead_letter_reason)
                   SELECT organization_id, id, node_id, ordinal, retry_of, restore_epoch,
                          1, 'budget_exhausted', NULL, 'attempt budget exhausted'
                   FROM attempts
                   WHERE organization_id = @o AND id = @a"
              direct.Parameters.AddWithValue("o", org.Value) |> ignore
              direct.Parameters.AddWithValue("a", corrupt.AttemptId.Value) |> ignore

              try
                  direct.ExecuteNonQuery() |> ignore
                  failtest "direct retry decision over corrupt terminal result unexpectedly succeeded"
              with :? Npgsql.PostgresException ->
                  ()

              Expect.equal (retryDecisionCount org active.AttemptId) 0 "active parent unchanged"
              Expect.equal (retryDecisionCount org terminal.AttemptId) 0 "invalid child unchanged"
              Expect.equal (retryDecisionCount org corrupt.AttemptId) 0 "corrupt parent unchanged"
              Expect.equal
                  (store.CountEvents(org, corrupt.BuildId, "retry.decided"))
                  0
                  "corrupt direct insert emitted no event"
              Expect.equal (store.CountOutbox org) corruptOutboxBefore "corrupt direct insert emitted no outbox row"

              match store.ListRetryDeadLetters org with
              | Ok listed ->
                  Expect.isFalse
                      (listed |> List.exists (fun value -> value.Decision.ParentId = corrupt.AttemptId))
                      "corrupt parent is absent from dead-letter listing"
              | Error error -> failtestf "dead-letter list failed: %A" error
          }

          test "fresh stale-epoch decision is refused while an old prior still replays" {
              let org, project = freshProject ()
              let staleFresh = terminalAttempt org project "retry-stale-fresh"
              store.ActivateRestore() |> ignore

              match store.DecideRetry(org, staleFresh.AttemptId, 2, AttemptId(Guid.NewGuid())) with
              | Error(RetryRestoreEpochMismatch _) -> ()
              | other -> failtestf "stale fresh decision returned %A" other

              let priorParent = terminalAttempt org project "retry-prior-before-restore"
              let first = decideRetryOk org priorParent.AttemptId 2 (AttemptId(Guid.NewGuid()))
              store.ActivateRestore() |> ignore
              let replay = decideRetryOk org priorParent.AttemptId 1 priorParent.AttemptId

              Expect.isTrue replay.WasReplay "prior remains replay-authoritative"
              Expect.equal replay.Persisted first.Persisted "restore cannot rewrite prior history"
          }

          test "restore metadata lock deterministically serializes a fresh decision" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-restore-lock"
              let marker = $"FG027B_RESTORE_{Guid.NewGuid():N}"
              let builder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              builder.ApplicationName <- marker

              use blocker = new Npgsql.NpgsqlConnection(connectionString)
              blocker.Open()
              use blockerTx = blocker.BeginTransaction()
              use lockEpoch = blocker.CreateCommand()
              lockEpoch.Transaction <- blockerTx
              lockEpoch.CommandText <-
                  "SELECT restore_epoch FROM controller_metadata WHERE singleton FOR UPDATE"
              lockEpoch.ExecuteScalar() |> ignore

              let task =
                  Async.StartAsTask(async {
                      return
                          Store(builder.ConnectionString).DecideRetry(
                              org,
                              parent.AttemptId,
                              2,
                              AttemptId(Guid.NewGuid()))
                  })

              use observer = new Npgsql.NpgsqlConnection(connectionString)
              observer.Open()

              let rec blocked remaining =
                  use wait = observer.CreateCommand()
                  wait.CommandText <-
                      "SELECT EXISTS (
                           SELECT 1 FROM pg_stat_activity
                           WHERE application_name = @marker
                             AND state = 'active'
                             AND wait_event_type = 'Lock'
                             AND query LIKE '%controller_metadata%'
                             AND query LIKE '%FOR SHARE%'
                       )"
                  wait.Parameters.AddWithValue("marker", marker) |> ignore

                  if wait.ExecuteScalar() :?> bool then true
                  elif remaining = 0 then false
                  else
                      Threading.Thread.Sleep 20
                      blocked (remaining - 1)

              let barrier = blocked 100
              use bump = blocker.CreateCommand()
              bump.Transaction <- blockerTx
              bump.CommandText <-
                  "UPDATE controller_metadata SET restore_epoch = restore_epoch + 1 WHERE singleton"
              Expect.equal (bump.ExecuteNonQuery()) 1 "restore epoch bumped behind held lock"
              blockerTx.Commit()

              Expect.isTrue barrier "retry waited at the metadata serialization point"
              Expect.isTrue (task.Wait(TimeSpan.FromSeconds 5.0)) "blocked decision completed"

              match task.Result with
              | Error(RetryRestoreEpochMismatch _) -> ()
              | other -> failtestf "post-restore fresh decision returned %A" other

              Expect.equal (retryDecisionCount org parent.AttemptId) 0 "no stale decision committed"
          }

          test "child identity collision rolls the whole transaction back" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "retry-collision-parent"
              let occupied = admitOk (newBuild org project "retry-collision-child" [ "retry" ])
              let outboxBefore = store.CountOutbox org

              match store.DecideRetry(org, parent.AttemptId, 2, occupied.AttemptId) with
              | Error(RetryStorageFailure _) -> ()
              | other -> failtestf "occupied child identity returned %A" other

              Expect.equal (retryDecisionCount org parent.AttemptId) 0 "decision rolled back"
              Expect.equal (store.CountEvents(org, parent.BuildId, "retry.decided")) 0 "event rolled back"
              Expect.equal (store.CountOutbox org) outboxBefore "outbox rolled back"

              let recovered = decideRetryOk org parent.AttemptId 2 (AttemptId(Guid.NewGuid()))
              Expect.isFalse recovered.WasReplay "parent lock and transaction were released"
          }

          test "SQL guards reject rewrites and dead-letter listing is tenant ordered" {
              let org, project = freshProject ()
              let firstParent = terminalAttempt org project "retry-list-first"
              let first = decideRetryOk org firstParent.AttemptId 1 (AttemptId(Guid.NewGuid()))
              let secondParent = terminalAttempt org project "retry-list-second"
              let second = decideRetryOk org secondParent.AttemptId 1 (AttemptId(Guid.NewGuid()))
              let foreignOrg, foreignProject = freshProject ()
              let foreignParent = terminalAttempt foreignOrg foreignProject "retry-list-foreign"
              decideRetryOk foreignOrg foreignParent.AttemptId 1 (AttemptId(Guid.NewGuid())) |> ignore

              let expectSqlRejected description sql bind =
                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- sql
                  bind cmd

                  try
                      cmd.ExecuteNonQuery() |> ignore
                      failtestf "%s unexpectedly succeeded" description
                  with :? Npgsql.PostgresException ->
                      ()

              let bindParent (cmd: Npgsql.NpgsqlCommand) =
                  cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                  cmd.Parameters.AddWithValue("a", firstParent.AttemptId.Value) |> ignore

              expectSqlRejected
                  "decision rewrite"
                  "UPDATE retry_decisions SET attempt_limit = attempt_limit + 1
                   WHERE organization_id = @o AND parent_attempt_id = @a"
                  bindParent

              expectSqlRejected
                  "decision deletion"
                  "DELETE FROM retry_decisions
                   WHERE organization_id = @o AND parent_attempt_id = @a"
                  bindParent

              expectSqlRejected
                  "attempt lineage rewrite"
                  "UPDATE attempts SET ordinal = ordinal + 1
                   WHERE organization_id = @o AND id = @a"
                  bindParent

              let undecided = terminalAttempt org project "retry-direct-malformed"
              expectSqlRejected
                  "direct mismatched parent snapshot"
                  "INSERT INTO retry_decisions
                     (organization_id, parent_attempt_id, parent_node_id,
                      parent_ordinal, parent_restore_epoch, attempt_limit,
                      outcome, dead_letter_reason)
                   SELECT @o, a.id, a.node_id, a.ordinal + 1, a.restore_epoch,
                          1, 'budget_exhausted', 'attempt budget exhausted'
                   FROM attempts a
                   WHERE a.organization_id = @o AND a.id = @a"
                  (fun cmd ->
                      cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                      cmd.Parameters.AddWithValue("a", undecided.AttemptId.Value) |> ignore)

              let listed =
                  match store.ListRetryDeadLetters org with
                  | Ok values -> values
                  | Error error -> failtestf "dead-letter list failed: %A" error

              let expected =
                  [ first.Persisted; second.Persisted ]
                  |> List.sortBy (fun value -> value.DecidedAt, value.Decision.ParentId.Value)

              Expect.equal listed expected "stable tenant-scoped (decided_at, parent_attempt_id) order"
              Expect.isFalse
                  (listed |> List.exists (fun value -> value.Decision.ParentId = foreignParent.AttemptId))
                  "foreign dead letter is not disclosed"
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

          test "concurrent duplicate publication does not burn the next build cursor" {
              let nextCursor (org: OrganizationId) (build: BuildId) =
                  use conn = new Npgsql.NpgsqlConnection(connectionString)
                  conn.Open()
                  use tx = conn.BeginTransaction()
                  use scope = conn.CreateCommand()
                  scope.Transaction <- tx
                  scope.CommandText <- "SELECT set_config('fogell.organization_id', @o, true)"
                  scope.Parameters.AddWithValue("o", string org.Value) |> ignore
                  scope.ExecuteScalar() |> ignore
                  use read = conn.CreateCommand()
                  read.Transaction <- tx
                  read.CommandText <-
                      "SELECT next_log_sequence FROM builds
                        WHERE organization_id = @o AND id = @b"
                  read.Parameters.AddWithValue("o", org.Value) |> ignore
                  read.Parameters.AddWithValue("b", build.Value) |> ignore
                  let value = read.ExecuteScalar() :?> int
                  tx.Commit()
                  value

              let race (append: unit -> bool) =
                  [ async { return append () }
                    async { return append () } ]
                  |> Async.Parallel
                  |> Async.RunSynchronously

              let org, project = freshProject ()
              let unfenced = admitOk (newBuild org project "log-no-burn-unfenced" [ "b" ])
              let unfencedResults =
                  race (fun () ->
                      Store(connectionString).AppendLog(
                          org, unfenced.BuildId, unfenced.AttemptId, 0, "unfenced-zero"))

              Expect.equal (unfencedResults |> Array.filter id |> Array.length) 1 "one unfenced publisher wins"
              Expect.isTrue
                  (store.AppendLog(org, unfenced.BuildId, unfenced.AttemptId, 1, "unfenced-one"))
                  "next real unfenced chunk appends"
              Expect.equal
                  (readLog org project unfenced.BuildId 0)
                  [ 0, "unfenced-zero"; 1, "unfenced-one" ]
                  "unfenced replay burned no public cursor"
              Expect.equal (nextCursor org unfenced.BuildId) 2 "unfenced counter advanced only for real chunks"

              let fenced, fence = runningAttempt org project "log-no-burn-fenced" "log-no-burn-owner" 60
              let fencedResults =
                  race (fun () ->
                      Store(connectionString).AppendLogFenced(
                          org,
                          fenced.BuildId,
                          fenced.AttemptId,
                          fence,
                          "log-no-burn-owner",
                          0,
                          "fenced-zero"))

              Expect.equal (fencedResults |> Array.filter id |> Array.length) 1 "one fenced publisher wins"
              Expect.isTrue
                  (store.AppendLogFenced(
                      org,
                      fenced.BuildId,
                      fenced.AttemptId,
                      fence,
                      "log-no-burn-owner",
                      1,
                      "fenced-one"))
                  "next real fenced chunk appends"
              Expect.equal
                  (readLog org project fenced.BuildId 0)
                  [ 0, "fenced-zero"; 1, "fenced-one" ]
                  "fenced replay burned no public cursor"
              Expect.equal (nextCursor org fenced.BuildId) 2 "fenced counter advanced only for real chunks"
          }

          test "a concurrent node move cannot splice a log into the requested build" {
              let org, project = freshProject ()
              let source = admitOk (newBuild org project "log-node-move-source" [ "b" ])
              let target = admitOk (newBuild org project "log-node-move-target" [ "b" ])
              let applicationName = $"fogell-log-lineage-{Guid.NewGuid():N}"
              let raceBuilder = Npgsql.NpgsqlConnectionStringBuilder(connectionString)
              raceBuilder.ApplicationName <- applicationName

              use mover = new Npgsql.NpgsqlConnection(connectionString)
              mover.Open()
              use moveTx = mover.BeginTransaction()
              use scope = mover.CreateCommand()
              scope.Transaction <- moveTx
              scope.CommandText <- "SELECT set_config('fogell.organization_id', @o, true)"
              scope.Parameters.AddWithValue("o", string org.Value) |> ignore
              scope.ExecuteScalar() |> ignore
              use move = mover.CreateCommand()
              move.Transaction <- moveTx
              move.CommandText <-
                  "UPDATE nodes SET build_id = @target
                    WHERE organization_id = @o AND id = @node"
              move.Parameters.AddWithValue("target", target.BuildId.Value) |> ignore
              move.Parameters.AddWithValue("o", org.Value) |> ignore
              move.Parameters.AddWithValue("node", source.NodeId.Value) |> ignore
              Expect.equal (move.ExecuteNonQuery()) 1 "node move holds the lineage row lock"

              let append =
                  System.Threading.Tasks.Task.Run(fun () ->
                      Store(raceBuilder.ConnectionString).AppendLog(
                          org, source.BuildId, source.AttemptId, 0, "must-not-splice"))

              use observer = new Npgsql.NpgsqlConnection(connectionString)
              observer.Open()

              let rec waitForNodeLock deadline =
                  use waiting = observer.CreateCommand()
                  waiting.CommandText <-
                      "SELECT EXISTS (
                           SELECT 1 FROM pg_stat_activity
                            WHERE application_name = @application
                              AND wait_event_type = 'Lock'
                              AND query LIKE 'SELECT build_id FROM nodes%'
                       )"
                  waiting.Parameters.AddWithValue("application", applicationName) |> ignore

                  if waiting.ExecuteScalar() :?> bool then
                      true
                  elif DateTime.UtcNow >= deadline then
                      false
                  else
                      System.Threading.Thread.Sleep 10
                      waitForNodeLock deadline

              Expect.isTrue
                  (waitForNodeLock (DateTime.UtcNow.AddSeconds 5.0))
                  "append reaches and waits on the explicitly locked node"

              moveTx.Commit()
              Expect.isFalse append.Result "moved lineage is re-read and rejected after the lock wait"
              Expect.equal (readLog org project source.BuildId 0) [] "requested build received no spliced log"
              Expect.equal (readLog org project target.BuildId 0) [] "actual moved-to build received no disguised log"

              use verify = new Npgsql.NpgsqlConnection(connectionString)
              verify.Open()
              use verifyTx = verify.BeginTransaction()
              use verifyScope = verify.CreateCommand()
              verifyScope.Transaction <- verifyTx
              verifyScope.CommandText <- "SELECT set_config('fogell.organization_id', @o, true)"
              verifyScope.Parameters.AddWithValue("o", string org.Value) |> ignore
              verifyScope.ExecuteScalar() |> ignore
              use counters = verify.CreateCommand()
              counters.Transaction <- verifyTx
              counters.CommandText <-
                  "SELECT next_log_sequence FROM builds
                    WHERE organization_id = @o AND id IN (@source, @target)
                    ORDER BY id"
              counters.Parameters.AddWithValue("o", org.Value) |> ignore
              counters.Parameters.AddWithValue("source", source.BuildId.Value) |> ignore
              counters.Parameters.AddWithValue("target", target.BuildId.Value) |> ignore
              use rows = counters.ExecuteReader()
              let values = [ while rows.Read() do yield rows.GetInt32 0 ]
              Expect.equal values [ 0; 0 ] "rejected splice consumed no cursor on either build"
              rows.Close()
              verifyTx.Commit()
          }

          test "build-wide pages do not skip retry chunks with the same attempt-local sequence" {
              let org, project = freshProject ()
              let parent = terminalAttempt org project "log-retry-page"

              let child =
                  let decision = decideRetryOk org parent.AttemptId 2 (AttemptId(Guid.NewGuid()))

                  match decision.Persisted.Decision.Outcome with
                  | BudgetExhausted -> failtest "retry budget unexpectedly exhausted"
                  | ChildCreated child -> child

              Expect.isTrue
                  (store.AppendLog(org, parent.BuildId, parent.AttemptId, 0, "parent-zero"))
                  "parent attempt publishes local sequence zero"

              Expect.isTrue
                  (store.AppendLog(org, parent.BuildId, child.Id, 0, "child-zero"))
                  "retry attempt independently publishes local sequence zero"

              let first = store.ReadLogPage(org, project, parent.BuildId, 0, 1)
              let second = store.ReadLogPage(org, project, parent.BuildId, 1, 1)
              let exhausted = store.ReadLogPage(org, project, parent.BuildId, 2, 1)

              Expect.equal first (Some [ 0, "parent-zero" ]) "first page contains exactly the first attempt"
              Expect.equal second (Some [ 1, "child-zero" ]) "next cursor contains exactly the retry"
              Expect.equal exhausted (Some []) "advancing twice neither duplicates nor skips a chunk"
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
                (testSequenced
                    (testList
                        "Fogell.Store"
                        [ migrations
                          tenantIsolation
                          admission
                          fencing
                          effectCheckpoints
                          effectReconciliation
                          retryDecisions
                          scheduling
                          logs ]))
