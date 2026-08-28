namespace Fogell.Store

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open Fogell.Domain

/// The outcome of a cancellation request, so a caller is never misled about
/// whether it took effect.
type CancellationOutcome =
    | CancellationAccepted
    /// Already requested — idempotent, not an error.
    | AlreadyRequested
    | AlreadyTerminal of status: string
    | NoSuchBuild

/// The database-linearized decision immediately before a local worker may
/// launch a child. A cancellation that wins this decision is made terminal
/// without starting user code.
type ExecutionStartOutcome =
    | ExecutionStarted
    | ExecutionCancelledBeforeStart

/// What admission returns. Repeating the same idempotency key returns exactly
/// these identifiers again, without emitting a second event or outbox message.
type Admission =
    { BuildId: BuildId
      NodeId: NodeId
      AttemptId: AttemptId
      Number: int
      WasExisting: bool }

type NewBuild =
    { OrganizationId: OrganizationId
      ProjectId: ProjectId
      IdempotencyKey: string
      /// Exact UTF-8 request bytes accepted by the parser.  They are persisted
      /// atomically with the build and never reconstructed from an AST.
      PipelineSource: byte array
      StageNames: string list
      RequiredTrustPool: string
      RequiredCapabilities: string list }

/// The immutable request identity needed to resolve a durable admission before
/// parsing. Parsed stage metadata is intentionally absent: exact source bytes
/// and controller-owned placement policy are the complete idempotency binding.
type AdmissionProbe =
    { OrganizationId: OrganizationId
      ProjectId: ProjectId
      IdempotencyKey: string
      PipelineSource: byte array
      RequiredTrustPool: string
      RequiredCapabilities: string list }

/// One whole-pipeline execution owned by the local controller.  Nodes retain
/// stage metadata, but the persisted runner executes the accepted definition
/// exactly once per build rather than once per stage.
type ExecutionClaim =
    { OrganizationId: OrganizationId
      ProjectId: ProjectId
      BuildId: BuildId
      BuildNumber: int
      NodeId: NodeId
      AttemptId: AttemptId
      Fence: Fence
      PipelineSource: byte array
      PipelineSha256: string }

type DatabaseIdentity =
    { User: string
      IsSuperuser: bool
      BypassesRls: bool }

/// FG-026. The durable state of one externally visible effect for one attempt.
type EffectCheckpointState =
    | EffectPrepared
    | EffectApplied
    | EffectConfirmed
    | EffectUncertain

type EffectUncertainOrigin =
    | UncertainAfterPrepare
    | UncertainAfterApply

type EffectCheckpoint =
    { OrganizationId: OrganizationId
      AttemptId: AttemptId
      EffectKey: string
      Fence: Fence
      AuthorityOwner: string
      RestoreEpoch: RestoreEpoch
      PayloadSha256: string
      State: EffectCheckpointState
      UncertainOrigin: EffectUncertainOrigin option }

type EffectCheckpointOutcome =
    { Checkpoint: EffectCheckpoint
      WasReplay: bool }

type EffectAdvance =
    | RecordApplied
    | RecordConfirmed

/// FG-027b. The immutable decision snapshot read from durable storage.  The
/// embedded Domain value is the exact value originally decided: a child is
/// reconstructed as queued even after its live attempt has advanced.
type PersistedRetryDecision =
    { Decision: RetryDecision
      DeadLetterReason: string option
      DecidedAt: DateTimeOffset }

type RetryPersistenceOutcome =
    { Persisted: PersistedRetryDecision
      WasReplay: bool }

type RetryPersistenceError =
    | RetryParentUnavailable
    | RetryRestoreEpochMismatch of parent: RestoreEpoch * current: RestoreEpoch
    | RetryLawRejected of RetryDecisionError
    | RetryDecisionCorrupt of string
    | RetryStorageFailure of string

/// FG-021/FG-022. The controller's durable truth.
type Store(connectionString: string, ?maintenanceConnectionString: string) =

    let retryDeadLetterReason = "attempt budget exhausted"
    let maintenanceConnectionString = defaultArg maintenanceConnectionString connectionString

    let openConn () =
        let c = new NpgsqlConnection(connectionString)
        c.Open()
        c

    let openMaintenanceConn () =
        let c = new NpgsqlConnection(maintenanceConnectionString)
        c.Open()
        c

    let setTenantContext
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (org: OrganizationId)
        =
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- "SELECT set_config('fogell.organization_id', @organization_id, true)"
        cmd.Parameters.AddWithValue("organization_id", org.Value.ToString()) |> ignore
        cmd.ExecuteScalar() |> ignore

    let beginTenantTransaction (conn: NpgsqlConnection) (org: OrganizationId) =
        let tx = conn.BeginTransaction()
        try
            setTenantContext conn tx org
            tx
        with _ ->
            tx.Dispose()
            reraise ()

    let beginTenantTransactionAt
        (conn: NpgsqlConnection)
        (org: OrganizationId)
        (isolationLevel: System.Data.IsolationLevel)
        =
        let tx = conn.BeginTransaction(isolationLevel)
        try
            setTenantContext conn tx org
            tx
        with _ ->
            tx.Dispose()
            reraise ()

    let lockExecutionAuthority
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (org: OrganizationId)
        (attempt: AttemptId)
        (fence: Fence)
        (owner: string)
        (offeredOnly: bool)
        =
        // Lock order is contractual and matches every attempt roll-up:
        // attempt -> node -> build. A multi-table FOR UPDATE leaves that order
        // implicit in the query plan and can deadlock an explicit roll-up that
        // already owns the attempt while waiting for the build.
        use attemptLock = conn.CreateCommand()
        attemptLock.Transaction <- tx
        attemptLock.CommandText <-
            "SELECT a.node_id
               FROM attempts a
              WHERE a.organization_id = @o AND a.id = @a AND a.fence = @f
                AND a.lease_owner = @owner
                AND a.lease_expires_at > clock_timestamp()
                AND a.restore_epoch = (SELECT restore_epoch FROM controller_metadata WHERE singleton)
                AND ((@offered_only AND a.state = 'offered')
                     OR (NOT @offered_only
                         AND a.state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')))
              FOR UPDATE OF a"
        attemptLock.Parameters.AddWithValue("o", org.Value) |> ignore
        attemptLock.Parameters.AddWithValue("a", attempt.Value) |> ignore
        attemptLock.Parameters.AddWithValue("f", fence.Value) |> ignore
        attemptLock.Parameters.AddWithValue("owner", owner) |> ignore
        attemptLock.Parameters.AddWithValue("offered_only", offeredOnly) |> ignore

        match attemptLock.ExecuteScalar() with
        | null -> None
        | nodeValue ->
            let nodeId = nodeValue :?> Guid
            use nodeLock = conn.CreateCommand()
            nodeLock.Transaction <- tx
            nodeLock.CommandText <-
                "SELECT build_id FROM nodes
                  WHERE organization_id = @o AND id = @n
                  FOR UPDATE"
            nodeLock.Parameters.AddWithValue("o", org.Value) |> ignore
            nodeLock.Parameters.AddWithValue("n", nodeId) |> ignore

            match nodeLock.ExecuteScalar() with
            | null -> None
            | buildValue ->
                let buildId = buildValue :?> Guid
                use buildLock = conn.CreateCommand()
                buildLock.Transaction <- tx
                buildLock.CommandText <-
                    "SELECT cancellation_requested FROM builds
                      WHERE organization_id = @o AND id = @b
                      FOR UPDATE"
                buildLock.Parameters.AddWithValue("o", org.Value) |> ignore
                buildLock.Parameters.AddWithValue("b", buildId) |> ignore

                match buildLock.ExecuteScalar() with
                | :? bool as cancellationRequested -> Some(nodeId, buildId, cancellationRequested)
                | _ -> None

    let publishLockedTerminal
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (org: OrganizationId)
        (attempt: AttemptId)
        (fence: Fence)
        (owner: string)
        (nodeId: Guid)
        (buildId: Guid)
        (terminalStatus: BuildStatus)
        =
        let resultWire = BuildStatus.toWireString terminalStatus
        use finishAttempt = conn.CreateCommand()
        finishAttempt.Transaction <- tx
        finishAttempt.CommandText <-
            "UPDATE attempts
                SET state = 'terminal', result = @r, lease_owner = NULL, lease_expires_at = NULL
              WHERE organization_id = @o AND id = @a AND fence = @f AND lease_owner = @owner
                AND lease_expires_at > clock_timestamp()
                AND state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
                AND restore_epoch = (SELECT restore_epoch FROM controller_metadata WHERE singleton)"
        finishAttempt.Parameters.AddWithValue("o", org.Value) |> ignore
        finishAttempt.Parameters.AddWithValue("a", attempt.Value) |> ignore
        finishAttempt.Parameters.AddWithValue("f", fence.Value) |> ignore
        finishAttempt.Parameters.AddWithValue("owner", owner) |> ignore
        finishAttempt.Parameters.AddWithValue("r", resultWire) |> ignore

        if finishAttempt.ExecuteNonQuery() <> 1 then
            Error "publication refused: stale fence, wrong owner, expired lease, pre-restore epoch, or already terminal"
        else
            use finishNode = conn.CreateCommand()
            finishNode.Transaction <- tx
            finishNode.CommandText <-
                "UPDATE nodes SET status = @r
                  WHERE organization_id = @o AND id = @n AND build_id = @b"
            finishNode.Parameters.AddWithValue("o", org.Value) |> ignore
            finishNode.Parameters.AddWithValue("n", nodeId) |> ignore
            finishNode.Parameters.AddWithValue("b", buildId) |> ignore
            finishNode.Parameters.AddWithValue("r", resultWire) |> ignore

            if finishNode.ExecuteNonQuery() <> 1 then
                failwith "terminal attempt has no node lineage"

            use finishBuild = conn.CreateCommand()
            finishBuild.Transaction <- tx
            finishBuild.CommandText <-
                "UPDATE builds
                    SET status = @r, cancellation_requested = false
                  WHERE organization_id = @o AND id = @b"
            finishBuild.Parameters.AddWithValue("o", org.Value) |> ignore
            finishBuild.Parameters.AddWithValue("b", buildId) |> ignore
            finishBuild.Parameters.AddWithValue("r", resultWire) |> ignore

            if finishBuild.ExecuteNonQuery() <> 1 then
                failwith "terminal attempt has no build lineage"

            use ev = conn.CreateCommand()
            ev.Transaction <- tx
            ev.CommandText <-
                "INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
                 VALUES (@o, @b, @a, 'attempt.terminal', @pl)"
            ev.Parameters.AddWithValue("o", org.Value) |> ignore
            ev.Parameters.AddWithValue("b", buildId) |> ignore
            ev.Parameters.AddWithValue("a", attempt.Value) |> ignore
            ev.Parameters.Add(
                NpgsqlParameter("pl", NpgsqlTypes.NpgsqlDbType.Jsonb,
                                Value = $"{{\"result\":\"{resultWire}\"}}")) |> ignore
            ev.ExecuteNonQuery() |> ignore

            use outbox = conn.CreateCommand()
            outbox.Transaction <- tx
            outbox.CommandText <-
                "INSERT INTO outbox (organization_id, topic, body)
                 VALUES (@o, 'build.terminal', @body)"
            outbox.Parameters.AddWithValue("o", org.Value) |> ignore
            outbox.Parameters.Add(
                NpgsqlParameter(
                    "body",
                    NpgsqlTypes.NpgsqlDbType.Jsonb,
                    Value = $"{{\"build\":\"{buildId}\",\"result\":\"{resultWire}\"}}"))
            |> ignore
            outbox.ExecuteNonQuery() |> ignore
            Ok()

    let effectProjection =
        "organization_id, attempt_id, effect_key, fence, authority_owner,
         restore_epoch, encode(payload_digest, 'hex'), state, uncertain_from"

    let retryProjection =
        "d.organization_id, d.parent_attempt_id, d.parent_node_id,
         d.parent_ordinal, d.parent_retry_of, d.parent_restore_epoch,
         d.attempt_limit, d.outcome, d.child_attempt_id,
         d.dead_letter_reason, d.decided_at,
         c.organization_id, c.node_id, c.ordinal, c.retry_of, c.restore_epoch,
         p.organization_id, p.node_id, p.ordinal, p.retry_of, p.restore_epoch"

    let timestampOffset (value: obj) =
        match value with
        | :? DateTimeOffset as timestamp -> timestamp
        | :? DateTime as timestamp -> DateTimeOffset timestamp
        | invalid -> failwithf "unexpected PostgreSQL timestamp value %A" invalid

    let attemptStateOfWire state result =
        match state with
        | "queued" -> Ok Queued
        | "offered" -> Ok Offered
        | "accepted" -> Ok Accepted
        | "running" -> Ok Running
        | "finalizing" -> Ok Finalizing
        | "cancelling" -> Ok Cancelling
        | "reconciliation_required" -> Ok ReconciliationRequired
        | "terminal" ->
            match result |> Option.bind BuildStatus.ofWireString with
            | Some status -> Ok(Terminal status)
            | None -> Error "terminal retry parent has no valid result"
        | invalid -> Error $"retry parent has invalid state '{invalid}'"

    let readRetryParent (reader: System.Data.Common.DbDataReader) =
        let retryOf =
            if reader.IsDBNull 4 then None else Some(AttemptId(reader.GetGuid 4))

        let owner =
            if reader.IsDBNull 8 then None else Some(reader.GetString 8)

        let expiry =
            if reader.IsDBNull 9 then
                None
            else
                Some(timestampOffset (reader.GetValue 9))

        let result =
            if reader.IsDBNull 10 then None else Some(reader.GetString 10)

        attemptStateOfWire (reader.GetString 5) result
        |> Result.map (fun state ->
            { Id = AttemptId(reader.GetGuid 0)
              NodeId = NodeId(reader.GetGuid 1)
              OrganizationId = OrganizationId(reader.GetGuid 2)
              Ordinal = reader.GetInt32 3
              RetryOf = retryOf
              State = state
              Fence = Fence(reader.GetInt64 6)
              RestoreEpoch = RestoreEpoch(reader.GetInt64 7)
              LeaseOwner = owner
              LeaseExpiresAt = expiry })

    let readPersistedRetryDecision (reader: System.Data.Common.DbDataReader) =
        let org = OrganizationId(reader.GetGuid 0)
        let parentId = AttemptId(reader.GetGuid 1)
        let nodeId = NodeId(reader.GetGuid 2)
        let ordinal = reader.GetInt32 3
        let parentRetryOf =
            if reader.IsDBNull 4 then None else Some(AttemptId(reader.GetGuid 4))
        let epoch = RestoreEpoch(reader.GetInt64 5)
        let attemptLimit = reader.GetInt32 6
        let outcomeWire = reader.GetString 7
        let childId =
            if reader.IsDBNull 8 then None else Some(AttemptId(reader.GetGuid 8))
        let reason =
            if reader.IsDBNull 9 then None else Some(reader.GetString 9)
        let decidedAt = timestampOffset (reader.GetValue 10)

        let liveParentRetryOf =
            if reader.IsDBNull 19 then None else Some(AttemptId(reader.GetGuid 19))

        let parentSnapshotMatches =
            not (reader.IsDBNull 16)
            && not (reader.IsDBNull 17)
            && not (reader.IsDBNull 18)
            && not (reader.IsDBNull 20)
            && OrganizationId(reader.GetGuid 16) = org
            && NodeId(reader.GetGuid 17) = nodeId
            && reader.GetInt32 18 = ordinal
            && liveParentRetryOf = parentRetryOf
            && RestoreEpoch(reader.GetInt64 20) = epoch

        let malformed message = Error(RetryDecisionCorrupt message)

        let snapshot =
            { ParentId = parentId
              ParentOrganizationId = org
              ParentNodeId = nodeId
              ParentOrdinal = ordinal
              ParentRetryOf = parentRetryOf
              ParentRestoreEpoch = epoch
              AttemptLimit = attemptLimit
              Outcome = BudgetExhausted }

        match parentSnapshotMatches, outcomeWire, childId, reason with
        | false, _, _, _ -> malformed "retry decision parent lineage differs from its captured snapshot"
        | true, "budget_exhausted", None, Some exact when exact = retryDeadLetterReason ->
            Ok
                { Decision = snapshot
                  DeadLetterReason = reason
                  DecidedAt = decidedAt }
        | true, "child_created", Some child, None ->
            if
                reader.IsDBNull 11
                || reader.IsDBNull 12
                || reader.IsDBNull 13
                || reader.IsDBNull 14
                || reader.IsDBNull 15
            then
                malformed "retry decision child is missing"
            else
                let liveOrg = OrganizationId(reader.GetGuid 11)
                let liveNode = NodeId(reader.GetGuid 12)
                let liveOrdinal = reader.GetInt32 13
                let liveRetryOf = AttemptId(reader.GetGuid 14)
                let liveEpoch = RestoreEpoch(reader.GetInt64 15)

                if
                    liveOrg <> org
                    || liveNode <> nodeId
                    || liveOrdinal <> ordinal + 1
                    || liveRetryOf <> parentId
                    || liveEpoch <> epoch
                then
                    malformed "retry decision child lineage differs from its creation snapshot"
                else
                    let childSnapshot =
                        { Id = child
                          NodeId = nodeId
                          OrganizationId = org
                          Ordinal = ordinal + 1
                          RetryOf = Some parentId
                          State = Queued
                          Fence = Fence.initial
                          RestoreEpoch = epoch
                          LeaseOwner = None
                          LeaseExpiresAt = None }

                    Ok
                        { Decision =
                            { snapshot with
                                Outcome = ChildCreated childSnapshot }
                          DeadLetterReason = None
                          DecidedAt = decidedAt }
        | true, "budget_exhausted", _, _ -> malformed "budget exhaustion has an invalid child or reason"
        | true, "child_created", _, _ -> malformed "child creation has an invalid child or reason"
        | true, invalid, _, _ -> malformed $"retry decision has invalid outcome '{invalid}'"

    let payloadDigest (payload: byte array) =
        use sha = SHA256.Create()
        sha.ComputeHash payload

    let digestHex (digest: byte array) =
        Convert.ToHexString(digest).ToLowerInvariant()

    let admissionFingerprint (source: byte array) (trustPool: string) (capabilities: string list) =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)

        let append (bytes: byte array) =
            let length = Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteInt32BigEndian(length.AsSpan(), bytes.Length)
            hash.AppendData length
            hash.AppendData bytes

        append (Encoding.UTF8.GetBytes "fogell-admission-v1")
        append source
        append (Encoding.UTF8.GetBytes trustPool)

        capabilities
        |> List.sortWith (fun left right -> String.CompareOrdinal(left, right))
        |> List.iter (Encoding.UTF8.GetBytes >> append)

        hash.GetHashAndReset()

    let readCheckpoint (reader: System.Data.Common.DbDataReader) =
        let state =
            match reader.GetString 7 with
            | "prepared" -> EffectPrepared
            | "applied" -> EffectApplied
            | "confirmed" -> EffectConfirmed
            | "uncertain" -> EffectUncertain
            | invalid -> failwithf "invalid effect checkpoint state '%s'" invalid

        let uncertainOrigin =
            if reader.IsDBNull 8 then
                None
            else
                match reader.GetString 8 with
                | "prepared" -> Some UncertainAfterPrepare
                | "applied" -> Some UncertainAfterApply
                | invalid -> failwithf "invalid effect uncertainty origin '%s'" invalid

        { OrganizationId = OrganizationId(reader.GetGuid 0)
          AttemptId = AttemptId(reader.GetGuid 1)
          EffectKey = reader.GetString 2
          Fence = Fence(reader.GetInt64 3)
          AuthorityOwner = reader.GetString 4
          RestoreEpoch = RestoreEpoch(reader.GetInt64 5)
          PayloadSha256 = reader.GetString 6
          State = state
          UncertainOrigin = uncertainOrigin }

    let addEffectIdentity
        (cmd: NpgsqlCommand)
        (org: OrganizationId)
        (attempt: AttemptId)
        (effectKey: string)
        =
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("k", effectKey) |> ignore

    let tryLockEffectAuthority
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (org: OrganizationId)
        (attempt: AttemptId)
        (fence: Fence)
        (owner: string)
        =
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT a.restore_epoch
             FROM attempts a
             WHERE a.organization_id = @o
               AND a.id = @a
               AND a.fence = @f
               AND a.lease_owner = @owner
               AND a.lease_expires_at > clock_timestamp()
               AND a.state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
               AND a.restore_epoch = (SELECT restore_epoch FROM controller_metadata WHERE singleton)
             FOR UPDATE"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
        cmd.Parameters.AddWithValue("owner", owner) |> ignore

        match cmd.ExecuteScalar() with
        | null -> None
        | value -> Some(RestoreEpoch(value :?> int64))

    let selectEffectForUpdate
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (org: OrganizationId)
        (attempt: AttemptId)
        (effectKey: string)
        =
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            $"SELECT {effectProjection}
              FROM effect_checkpoints
              WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k
              FOR UPDATE"
        addEffectIdentity cmd org attempt effectKey
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(readCheckpoint reader) else None

    let sameEffectIdentity
        (checkpoint: EffectCheckpoint)
        (fence: Fence)
        (owner: string)
        (epoch: RestoreEpoch)
        (digest: byte array)
        =
        checkpoint.Fence = fence
        && checkpoint.AuthorityOwner = owner
        && checkpoint.RestoreEpoch = epoch
        && checkpoint.PayloadSha256 = digestHex digest

    let validateEffectInput (effectKey: string) (owner: string) (payload: byte array) =
        if String.IsNullOrWhiteSpace effectKey then
            Some "effect key is required"
        elif effectKey.Length > 256 then
            Some "effect key exceeds 256 characters"
        elif String.IsNullOrWhiteSpace owner then
            Some "effect authority owner is required"
        elif isNull payload then
            Some "effect payload is required"
        else
            None

    member _.Migrate() = Migrations.run maintenanceConnectionString

    member _.Ping() =
        try
            use conn = openConn ()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT 1"
            cmd.ExecuteScalar() :?> int = 1
        with _ ->
            false

    member _.RuntimeDatabaseIdentity() : Result<DatabaseIdentity, string> =
        try
            use conn = openConn ()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT current_user, rolsuper, rolbypassrls
                   FROM pg_roles
                  WHERE rolname = current_user"
            use reader = cmd.ExecuteReader()

            if reader.Read() then
                Ok
                    { User = reader.GetString 0
                      IsSuperuser = reader.GetBoolean 1
                      BypassesRls = reader.GetBoolean 2 }
            else
                Error "runtime database identity is unavailable"
        with ex ->
            Error ex.Message

    /// Prove that the runtime and maintenance capabilities reach the same live
    /// PostgreSQL database, independent of connection-string aliases, proxies,
    /// host names, or cloned role/schema metadata. PostgreSQL advisory locks are
    /// scoped by database OID inside a cluster. Overlapping transactions pin
    /// transaction-pooling proxies to distinct backends for the duration of the
    /// proof; a transaction-scoped lock held by maintenance must therefore be
    /// unavailable to runtime only when both capabilities name the same database.
    member _.DatabasePairMatches() =
        try
            let openProbeConnection (raw: string) =
                let builder = NpgsqlConnectionStringBuilder(raw)
                // The proof requires two simultaneous physical sessions. A
                // caller's one-connector pool would deadlock the second open,
                // while multiplexing cannot provide the explicit transaction
                // ownership this proof relies on.
                builder.Pooling <- false
                builder.Multiplexing <- false
                // An undersized or unhealthy external transaction pool must
                // fail startup closed instead of blocking the proof forever.
                if builder.Timeout = 0 || builder.Timeout > 5 then
                    builder.Timeout <- 5
                if builder.CommandTimeout = 0 || builder.CommandTimeout > 5 then
                    builder.CommandTimeout <- 5
                let connection = new NpgsqlConnection(builder.ConnectionString)
                connection.Open()
                connection

            use maintenance = openProbeConnection maintenanceConnectionString
            use runtime = openProbeConnection connectionString
            let mutable result: bool option = None
            let mutable attempted = 0

            // A cryptographic random key makes collision with unrelated advisory
            // lock users negligible. Retrying also makes a real collision a
            // bounded startup delay rather than a false mismatch.
            while result.IsNone && attempted < 8 do
                attempted <- attempted + 1
                let keyBytes = RandomNumberGenerator.GetBytes 8
                let key = BinaryPrimitives.ReadInt64LittleEndian(keyBytes.AsSpan())

                use maintenanceTransaction = maintenance.BeginTransaction()
                use take = maintenance.CreateCommand()
                take.Transaction <- maintenanceTransaction
                take.CommandText <- "SELECT pg_try_advisory_xact_lock(@key)"
                take.Parameters.AddWithValue("key", key) |> ignore

                if take.ExecuteScalar() :?> bool then
                    // Begin only after maintenance owns the lock. Transaction
                    // poolers must now reserve a different backend for runtime.
                    use runtimeTransaction = runtime.BeginTransaction()
                    use probe = runtime.CreateCommand()
                    probe.Transaction <- runtimeTransaction
                    probe.CommandText <- "SELECT pg_try_advisory_xact_lock(@key)"
                    probe.Parameters.AddWithValue("key", key) |> ignore
                    result <- Some(not (probe.ExecuteScalar() :?> bool))

            // Disposing each transaction rolls back and releases every xact
            // lock on its pinned backend, including the different-database case.
            result |> Option.defaultValue false
        with _ ->
            false

    member _.RuntimeCapabilities() =
        try
            use conn = openConn ()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT
                     has_table_privilege(current_user, 'public.controller_metadata', 'SELECT')
                     AND has_column_privilege(
                         current_user, 'public.controller_metadata', 'singleton', 'UPDATE')
                     AND has_table_privilege(current_user, 'public.organization_work_roots', 'SELECT')
                     AND NOT EXISTS (
                         SELECT 1
                           FROM unnest(ARRAY[
                             'organizations', 'projects', 'builds', 'nodes', 'attempts',
                             'events', 'outbox', 'log_chunks', 'effect_checkpoints',
                             'retry_decisions', 'build_definitions'
                           ]) AS required_table(name)
                           CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE'])
                             AS required_privilege(name)
                          WHERE NOT has_table_privilege(
                              current_user,
                              'public.' || quote_ident(required_table.name),
                              required_privilege.name))
                     AND NOT EXISTS (
                         SELECT 1
                           FROM unnest(ARRAY[
                             'events_id_seq', 'outbox_id_seq', 'log_chunks_id_seq'
                           ]) AS required_sequence(name)
                           CROSS JOIN unnest(ARRAY['USAGE', 'SELECT'])
                             AS required_privilege(name)
                          WHERE NOT has_sequence_privilege(
                                current_user,
                                'public.' || quote_ident(required_sequence.name),
                                required_privilege.name))"
            cmd.ExecuteScalar() :?> bool
        with _ ->
            false

    /// Organization UUIDs are the bounded, deliberately non-tenant work scan
    /// roots.  Slugs and tenant records stay behind forced RLS; every subsequent
    /// claim still opens a transaction-local tenant context before seeing work.
    member _.OrganizationIds() : OrganizationId list =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT organization_id FROM organization_work_roots ORDER BY organization_id"
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do
              yield OrganizationId(reader.GetGuid 0) ]

    member _.CreateProject(org: OrganizationId, orgSlug: string, project: ProjectId, projectSlug: string) =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "INSERT INTO organizations (id, slug) VALUES (@o, @os) ON CONFLICT (id) DO NOTHING;
             INSERT INTO projects (id, organization_id, slug) VALUES (@p, @o, @ps)
             ON CONFLICT (id, organization_id) DO NOTHING"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("os", orgSlug) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("ps", projectSlug) |> ignore
        cmd.ExecuteNonQuery() |> ignore
        tx.Commit()

    member _.CurrentRestoreEpoch() : RestoreEpoch =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT restore_epoch FROM controller_metadata WHERE singleton"
        RestoreEpoch(cmd.ExecuteScalar() :?> int64)

    /// FG-027b Store foundation. Decide once while holding the restore epoch
    /// and parent locks, or validate and return the exact creation snapshot of
    /// the prior durable result. Runtime retry policy and dispatch are outside
    /// this API.
    member _.DecideRetry
        (org: OrganizationId, parentId: AttemptId, attemptLimit: int, proposedChildId: AttemptId)
        : Result<RetryPersistenceOutcome, RetryPersistenceError> =
        use conn = openConn ()
        // READ COMMITTED is deliberate: after waiting for a competing caller's
        // parent lock, this transaction must see the decision that caller just
        // committed and replay it. The explicit metadata/parent locks provide
        // the required serialization.
        use tx = beginTenantTransactionAt conn org System.Data.IsolationLevel.ReadCommitted

        let rollback error =
            try
                tx.Rollback()
            with _ ->
                ()

            Error error

        try
            // Restore takes the conflicting lock on this singleton before it
            // touches attempts. Taking the same order here prevents both a
            // check/use race and a restore-vs-retry deadlock.
            use epochCmd = conn.CreateCommand()
            epochCmd.Transaction <- tx
            epochCmd.CommandText <-
                "SELECT restore_epoch
                 FROM controller_metadata
                 WHERE singleton
                 FOR SHARE"
            let currentEpoch = RestoreEpoch(epochCmd.ExecuteScalar() :?> int64)

            use parentCmd = conn.CreateCommand()
            parentCmd.Transaction <- tx
            parentCmd.CommandText <-
                "SELECT id, node_id, organization_id, ordinal, retry_of, state,
                        fence, restore_epoch, lease_owner, lease_expires_at, result
                 FROM attempts
                 WHERE organization_id = @o AND id = @a
                 FOR UPDATE"
            parentCmd.Parameters.AddWithValue("o", org.Value) |> ignore
            parentCmd.Parameters.AddWithValue("a", parentId.Value) |> ignore

            use parentReader = parentCmd.ExecuteReader()

            if not (parentReader.Read()) then
                parentReader.Close()
                rollback RetryParentUnavailable
            else
                let parentResult = readRetryParent parentReader
                parentReader.Close()

                match parentResult with
                | Error reason -> rollback (RetryDecisionCorrupt reason)
                | Ok parent ->
                    use priorCmd = conn.CreateCommand()
                    priorCmd.Transaction <- tx
                    priorCmd.CommandText <-
                        $"SELECT {retryProjection}
                          FROM retry_decisions d
                          LEFT JOIN attempts p
                            ON p.organization_id = d.organization_id
                           AND p.id = d.parent_attempt_id
                          LEFT JOIN attempts c
                            ON c.organization_id = d.organization_id
                           AND c.id = d.child_attempt_id
                          WHERE d.organization_id = @o
                            AND d.parent_attempt_id = @a
                          FOR UPDATE OF d"
                    priorCmd.Parameters.AddWithValue("o", org.Value) |> ignore
                    priorCmd.Parameters.AddWithValue("a", parentId.Value) |> ignore
                    use priorReader = priorCmd.ExecuteReader()

                    if priorReader.Read() then
                        let priorResult = readPersistedRetryDecision priorReader
                        priorReader.Close()

                        match priorResult with
                        | Error error -> rollback error
                        | Ok persisted ->
                            match
                                RetryDecision.decide
                                    parent
                                    attemptLimit
                                    proposedChildId
                                    (Some persisted.Decision)
                            with
                            | Error error ->
                                rollback
                                    (RetryDecisionCorrupt
                                        $"persisted retry decision failed Domain replay validation: {error}")
                            | Ok exact when exact <> persisted.Decision ->
                                rollback (RetryDecisionCorrupt "Domain replay did not return the persisted decision exactly")
                            | Ok _ ->
                                tx.Commit()
                                Ok
                                    { Persisted = persisted
                                      WasReplay = true }
                    else
                        priorReader.Close()

                        if parent.RestoreEpoch <> currentEpoch then
                            rollback (RetryRestoreEpochMismatch(parent.RestoreEpoch, currentEpoch))
                        else
                            match RetryDecision.decide parent attemptLimit proposedChildId None with
                            | Error error -> rollback (RetryLawRejected error)
                            | Ok decision ->
                                match decision.Outcome with
                                | ChildCreated child ->
                                    // A terminal publication rolls the same lineage up to its
                                    // result. A queued retry must reopen that public aggregate in
                                    // this transaction; otherwise the child is claimable while the
                                    // build still reports terminal and refuses cancellation. Keep
                                    // the canonical attempt -> node -> build lock order and refuse
                                    // an aggregate that no longer matches the immutable parent.
                                    let parentResult =
                                        match parent.State with
                                        | Terminal result -> BuildStatus.toWireString result
                                        | state -> failwithf "retry parent became non-terminal: %A" state

                                    use reopenNode = conn.CreateCommand()
                                    reopenNode.Transaction <- tx
                                    reopenNode.CommandText <-
                                        "UPDATE nodes
                                            SET status = 'queued'
                                          WHERE organization_id = @o AND id = @n AND status = @terminal
                                          RETURNING build_id"
                                    reopenNode.Parameters.AddWithValue("o", child.OrganizationId.Value) |> ignore
                                    reopenNode.Parameters.AddWithValue("n", child.NodeId.Value) |> ignore
                                    reopenNode.Parameters.AddWithValue("terminal", parentResult) |> ignore

                                    let buildId =
                                        match reopenNode.ExecuteScalar() with
                                        | :? Guid as value -> value
                                        | _ -> failwith "retry parent node is not at its terminal result"

                                    use reopenBuild = conn.CreateCommand()
                                    reopenBuild.Transaction <- tx
                                    reopenBuild.CommandText <-
                                        "UPDATE builds
                                            SET status = 'queued'
                                          WHERE organization_id = @o AND id = @b
                                            AND status = @terminal"
                                    reopenBuild.Parameters.AddWithValue("o", child.OrganizationId.Value) |> ignore
                                    reopenBuild.Parameters.AddWithValue("b", buildId) |> ignore
                                    reopenBuild.Parameters.AddWithValue("terminal", parentResult) |> ignore

                                    if reopenBuild.ExecuteNonQuery() <> 1 then
                                        failwith "retry parent build is not at its terminal result"

                                    use childCmd = conn.CreateCommand()
                                    childCmd.Transaction <- tx
                                    childCmd.CommandText <-
                                        "INSERT INTO attempts
                                             (id, organization_id, node_id, ordinal, retry_of,
                                              state, fence, restore_epoch, lease_owner, lease_expires_at, result)
                                         VALUES
                                             (@id, @o, @n, @ord, @parent,
                                              'queued', 0, @epoch, NULL, NULL, NULL)"
                                    childCmd.Parameters.AddWithValue("id", child.Id.Value) |> ignore
                                    childCmd.Parameters.AddWithValue("o", child.OrganizationId.Value) |> ignore
                                    childCmd.Parameters.AddWithValue("n", child.NodeId.Value) |> ignore
                                    childCmd.Parameters.AddWithValue("ord", child.Ordinal) |> ignore
                                    childCmd.Parameters.AddWithValue("parent", parent.Id.Value) |> ignore
                                    childCmd.Parameters.AddWithValue("epoch", child.RestoreEpoch.Value) |> ignore
                                    childCmd.ExecuteNonQuery() |> ignore
                                | BudgetExhausted -> ()

                                use decisionCmd = conn.CreateCommand()
                                decisionCmd.Transaction <- tx
                                decisionCmd.CommandText <-
                                    "INSERT INTO retry_decisions
                                         (organization_id, parent_attempt_id, parent_node_id,
                                          parent_ordinal, parent_retry_of, parent_restore_epoch,
                                          attempt_limit, outcome, child_attempt_id, dead_letter_reason)
                                     VALUES
                                         (@o, @parent, @node, @ordinal, @retry_of, @epoch,
                                          @limit, @outcome, @child, @reason)
                                     RETURNING decided_at"
                                decisionCmd.Parameters.AddWithValue("o", org.Value) |> ignore
                                decisionCmd.Parameters.AddWithValue("parent", parent.Id.Value) |> ignore
                                decisionCmd.Parameters.AddWithValue("node", parent.NodeId.Value) |> ignore
                                decisionCmd.Parameters.AddWithValue("ordinal", parent.Ordinal) |> ignore
                                decisionCmd.Parameters.Add(
                                    NpgsqlParameter(
                                        "retry_of",
                                        NpgsqlTypes.NpgsqlDbType.Uuid,
                                        Value =
                                            match parent.RetryOf with
                                            | Some value -> box value.Value
                                            | None -> DBNull.Value))
                                |> ignore
                                decisionCmd.Parameters.AddWithValue("epoch", parent.RestoreEpoch.Value) |> ignore
                                decisionCmd.Parameters.AddWithValue("limit", decision.AttemptLimit) |> ignore

                                match decision.Outcome with
                                | ChildCreated child ->
                                    decisionCmd.Parameters.AddWithValue("outcome", "child_created") |> ignore
                                    decisionCmd.Parameters.Add(
                                        NpgsqlParameter(
                                            "child",
                                            NpgsqlTypes.NpgsqlDbType.Uuid,
                                            Value = child.Id.Value))
                                    |> ignore
                                    decisionCmd.Parameters.Add(
                                        NpgsqlParameter(
                                            "reason",
                                            NpgsqlTypes.NpgsqlDbType.Text,
                                            Value = DBNull.Value))
                                    |> ignore
                                | BudgetExhausted ->
                                    decisionCmd.Parameters.AddWithValue("outcome", "budget_exhausted") |> ignore
                                    decisionCmd.Parameters.Add(
                                        NpgsqlParameter(
                                            "child",
                                            NpgsqlTypes.NpgsqlDbType.Uuid,
                                            Value = DBNull.Value))
                                    |> ignore
                                    decisionCmd.Parameters.AddWithValue("reason", retryDeadLetterReason) |> ignore

                                let decidedAt = timestampOffset (decisionCmd.ExecuteScalar())
                                tx.Commit()

                                Ok
                                    { Persisted =
                                        { Decision = decision
                                          DeadLetterReason =
                                            match decision.Outcome with
                                            | ChildCreated _ -> None
                                            | BudgetExhausted -> Some retryDeadLetterReason
                                          DecidedAt = decidedAt }
                                      WasReplay = false }
        with ex ->
            rollback (RetryStorageFailure ex.Message)

    member _.ListRetryDeadLetters
        (org: OrganizationId)
        : Result<PersistedRetryDecision list, RetryPersistenceError> =
        try
            use conn = openConn ()
            use tx = beginTenantTransaction conn org
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <-
                $"SELECT {retryProjection}
                  FROM retry_decisions d
                  LEFT JOIN attempts p
                    ON p.organization_id = d.organization_id
                   AND p.id = d.parent_attempt_id
                  LEFT JOIN attempts c
                    ON c.organization_id = d.organization_id
                   AND c.id = d.child_attempt_id
                  WHERE d.organization_id = @o
                    AND d.outcome = 'budget_exhausted'
                  ORDER BY d.decided_at, d.parent_attempt_id"
            cmd.Parameters.AddWithValue("o", org.Value) |> ignore
            use reader = cmd.ExecuteReader()
            let values = ResizeArray<PersistedRetryDecision>()
            let mutable failure = None

            while reader.Read() && failure.IsNone do
                match readPersistedRetryDecision reader with
                | Ok value -> values.Add value
                | Error error -> failure <- Some error

            reader.Close()
            let result =
                match failure with
                | Some error -> Error error
                | None -> Ok(List.ofSeq values)
            tx.Commit()
            result
        with ex ->
            Error(RetryStorageFailure ex.Message)

    /// FG-026 Store foundation. Durably records intent before an external
    /// effect. The payload itself is never stored: its exact bytes are SHA-256
    /// hashed inside this API, and the digest is immutable thereafter.
    member _.PrepareEffect
        (org: OrganizationId,
         attempt: AttemptId,
         fence: Fence,
         owner: string,
         effectKey: string,
         payload: byte array)
        : Result<EffectCheckpointOutcome, string> =
        match validateEffectInput effectKey owner payload with
        | Some error -> Error error
        | None ->
            use conn = openConn ()
            use tx = beginTenantTransaction conn org

            try
                match tryLockEffectAuthority conn tx org attempt fence owner with
                | None ->
                    tx.Rollback()
                    Error "effect preparation refused: stale fence, wrong owner, expired lease, pre-restore epoch, or inactive attempt"
                | Some epoch ->
                    let digest = payloadDigest payload

                    match selectEffectForUpdate conn tx org attempt effectKey with
                    | Some checkpoint when sameEffectIdentity checkpoint fence owner epoch digest ->
                        tx.Commit()
                        Ok { Checkpoint = checkpoint; WasReplay = true }
                    | Some _ ->
                        tx.Rollback()
                        Error "effect preparation refused: key already has different authority or payload bytes"
                    | None ->
                        use insert = conn.CreateCommand()
                        insert.Transaction <- tx
                        insert.CommandText <-
                            $"INSERT INTO effect_checkpoints
                                 (organization_id, attempt_id, effect_key, fence, authority_owner,
                                  restore_epoch, payload_digest, state)
                              VALUES (@o, @a, @k, @f, @owner, @e, @d, 'prepared')
                              RETURNING {effectProjection}"
                        addEffectIdentity insert org attempt effectKey
                        insert.Parameters.AddWithValue("f", fence.Value) |> ignore
                        insert.Parameters.AddWithValue("owner", owner) |> ignore
                        insert.Parameters.AddWithValue("e", epoch.Value) |> ignore
                        insert.Parameters.Add(
                            NpgsqlParameter("d", NpgsqlTypes.NpgsqlDbType.Bytea, Value = digest))
                        |> ignore

                        use reader = insert.ExecuteReader()
                        if not (reader.Read()) then failwith "effect checkpoint insert returned no row"
                        let checkpoint = readCheckpoint reader
                        reader.Close()
                        tx.Commit()
                        Ok { Checkpoint = checkpoint; WasReplay = false }
            with ex ->
                (try tx.Rollback() with _ -> ())
                Error ex.Message

    /// Records only the two positive acknowledgements. A prepared checkpoint
    /// cannot skip directly to confirmed, and uncertain is terminal pending
    /// operator reconciliation.
    member _.AdvanceEffect
        (org: OrganizationId,
         attempt: AttemptId,
         fence: Fence,
         owner: string,
         effectKey: string,
         payload: byte array,
         advance: EffectAdvance)
        : Result<EffectCheckpointOutcome, string> =
        match validateEffectInput effectKey owner payload with
        | Some error -> Error error
        | None ->
            use conn = openConn ()
            use tx = beginTenantTransaction conn org

            try
                match tryLockEffectAuthority conn tx org attempt fence owner with
                | None ->
                    tx.Rollback()
                    Error "effect advancement refused: stale fence, wrong owner, expired lease, pre-restore epoch, or inactive attempt"
                | Some epoch ->
                    let digest = payloadDigest payload

                    match selectEffectForUpdate conn tx org attempt effectKey with
                    | None ->
                        tx.Rollback()
                        Error "effect checkpoint does not exist"
                    | Some checkpoint when not (sameEffectIdentity checkpoint fence owner epoch digest) ->
                        tx.Rollback()
                        Error "effect advancement refused: authority or payload bytes differ from preparation"
                    | Some checkpoint ->
                        let replay =
                            match advance, checkpoint.State with
                            | RecordApplied, EffectApplied
                            | RecordApplied, EffectConfirmed
                            | RecordConfirmed, EffectConfirmed -> true
                            | _ -> false

                        if replay then
                            tx.Commit()
                            Ok { Checkpoint = checkpoint; WasReplay = true }
                        else
                            let targetState, timestampColumn =
                                match advance, checkpoint.State with
                                | RecordApplied, EffectPrepared -> "applied", "applied_at"
                                | RecordConfirmed, EffectApplied -> "confirmed", "confirmed_at"
                                | RecordConfirmed, EffectPrepared ->
                                    failwith "effect confirmation requires an applied checkpoint"
                                | _, EffectUncertain ->
                                    failwith "an uncertain effect checkpoint cannot advance"
                                | RecordApplied, state ->
                                    failwithf "effect checkpoint cannot record applied from %A" state
                                | RecordConfirmed, state ->
                                    failwithf "effect checkpoint cannot record confirmed from %A" state

                            use update = conn.CreateCommand()
                            update.Transaction <- tx
                            update.CommandText <-
                                $"UPDATE effect_checkpoints
                                  SET state = @state, {timestampColumn} = clock_timestamp()
                                  WHERE organization_id = @o AND attempt_id = @a AND effect_key = @k
                                  RETURNING {effectProjection}"
                            addEffectIdentity update org attempt effectKey
                            update.Parameters.AddWithValue("state", targetState) |> ignore
                            use reader = update.ExecuteReader()
                            if not (reader.Read()) then failwith "effect checkpoint update returned no row"
                            let advanced = readCheckpoint reader
                            reader.Close()
                            tx.Commit()
                            Ok { Checkpoint = advanced; WasReplay = false }
            with ex ->
                (try tx.Rollback() with _ -> ())
                Error ex.Message

    /// Moves only checkpoints whose captured authority is no longer live into
    /// the reconciliation queue. The origin records whether the external call
    /// might have happened before authority was lost.
    member _.MarkStaleEffectsUncertain(org: OrganizationId) : Result<EffectCheckpoint list, string> =
        let markOnce () =
            use conn = openConn ()
            use tx = beginTenantTransactionAt conn org System.Data.IsolationLevel.RepeatableRead

            try
                // RepeatableRead gives the lock and update statements one stable
                // marker snapshot. A checkpoint committed after this query began
                // is deliberately left for the next bounded reconciliation pass.
                // Lock attempts first so prepare/advance and reconciliation never
                // invert the attempt -> checkpoint order.
                use authorityLocks = conn.CreateCommand()
                authorityLocks.Transaction <- tx
                authorityLocks.CommandText <-
                    "SELECT /* FG026_MARKER_SNAPSHOT */ a.id
                     FROM attempts a
                     JOIN effect_checkpoints e
                       ON e.organization_id = a.organization_id AND e.attempt_id = a.id
                     WHERE e.organization_id = @o AND e.state IN ('prepared', 'applied')
                     ORDER BY a.id
                     FOR UPDATE OF a"
                authorityLocks.Parameters.AddWithValue("o", org.Value) |> ignore
                use locked = authorityLocks.ExecuteReader()
                while locked.Read() do ()
                locked.Close()

                use update = conn.CreateCommand()
                update.Transaction <- tx
                update.CommandText <-
                    $"UPDATE effect_checkpoints e
                      SET uncertain_from = e.state,
                          state = 'uncertain',
                          uncertain_at = clock_timestamp()
                      WHERE e.organization_id = @o
                        AND e.state IN ('prepared', 'applied')
                        AND NOT EXISTS (
                            SELECT 1
                            FROM attempts a
                            WHERE a.organization_id = e.organization_id
                              AND a.id = e.attempt_id
                              AND a.fence = e.fence
                              AND a.lease_owner = e.authority_owner
                              AND a.lease_expires_at > clock_timestamp()
                              AND a.state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
                              AND a.restore_epoch = e.restore_epoch
                              AND a.restore_epoch = (
                                  SELECT restore_epoch FROM controller_metadata WHERE singleton
                              )
                        )
                      RETURNING {effectProjection}"
                update.Parameters.AddWithValue("o", org.Value) |> ignore
                use reader = update.ExecuteReader()
                let checkpoints = [ while reader.Read() do yield readCheckpoint reader ]
                reader.Close()
                tx.Commit()

                checkpoints
                |> List.sortBy (fun checkpoint ->
                    checkpoint.AttemptId.Value, checkpoint.EffectKey)
            with _ ->
                (try tx.Rollback() with _ -> ())
                reraise ()

        let rec run attemptNumber =
            try
                markOnce () |> Ok
            with
            | :? PostgresException as error
                when (error.SqlState = "40001" || error.SqlState = "40P01")
                     && attemptNumber < 3 ->
                run (attemptNumber + 1)
            | ex -> Error ex.Message

        run 1

    member _.ListUncertainEffects(org: OrganizationId) : EffectCheckpoint list =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            $"SELECT {effectProjection}
              FROM effect_checkpoints
              WHERE organization_id = @o AND state = 'uncertain'
              ORDER BY prepared_at, attempt_id, effect_key"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        use reader = cmd.ExecuteReader()
        let checkpoints = [ while reader.Read() do yield readCheckpoint reader ]
        reader.Close()
        tx.Commit()
        checkpoints

    /// Read-only idempotency probe for admission compatibility across stricter
    /// execution preflights. An exact durable result may be replayed without
    /// asking the current release to admit the source again. A miss is only a
    /// hint: the caller must still use AdmitBuild after preflight, because its
    /// transaction and unique constraint remain the create/race arbiter.
    member _.TryReplayAdmission(input: AdmissionProbe) : Result<Admission option, string> =
        if String.IsNullOrWhiteSpace input.IdempotencyKey then
            Error "idempotency key is required"
        elif isNull input.PipelineSource then
            Error "pipeline source is required"
        elif String.IsNullOrWhiteSpace input.RequiredTrustPool then
            Error "a trust pool is required"
        else
            use conn = openConn ()
            use tx = beginTenantTransaction conn input.OrganizationId

            try
                let sourceDigest = payloadDigest input.PipelineSource
                let fingerprint =
                    admissionFingerprint input.PipelineSource input.RequiredTrustPool input.RequiredCapabilities

                use existing = conn.CreateCommand()
                existing.Transaction <- tx
                existing.CommandText <-
                    "SELECT b.id, b.number, n.id, a.id,
                            d.source_digest, d.admission_fingerprint
                     FROM builds b
                     JOIN nodes n
                       ON n.build_id = b.id AND n.organization_id = b.organization_id AND n.ordinal = 0
                     JOIN attempts a
                       ON a.node_id = n.id AND a.organization_id = n.organization_id AND a.ordinal = 0
                     LEFT JOIN build_definitions d
                       ON d.build_id = b.id AND d.organization_id = b.organization_id
                     WHERE b.organization_id = @o AND b.project_id = @p AND b.idempotency_key = @k"
                existing.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                existing.Parameters.AddWithValue("p", input.ProjectId.Value) |> ignore
                existing.Parameters.AddWithValue("k", input.IdempotencyKey) |> ignore

                use reader = existing.ExecuteReader()

                if not (reader.Read()) then
                    reader.Close()
                    tx.Commit()
                    Ok None
                else
                    let sameDefinition =
                        not (reader.IsDBNull 4)
                        && not (reader.IsDBNull 5)
                        && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(4), sourceDigest)
                        && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(5), fingerprint)

                    if not sameDefinition then
                        reader.Close()
                        tx.Rollback()
                        Error "idempotency key is already bound to a different pipeline or placement policy"
                    else
                        let admission =
                            { BuildId = BuildId(reader.GetGuid 0)
                              Number = reader.GetInt32 1
                              NodeId = NodeId(reader.GetGuid 2)
                              AttemptId = AttemptId(reader.GetGuid 3)
                              WasExisting = true }

                        reader.Close()
                        tx.Commit()
                        Ok(Some admission)
            with ex ->
                (try tx.Rollback() with _ -> ())
                Error ex.Message

    /// FG-021. Build, first node, first attempt, a durable event and an outbox
    /// message commit **together**. There is no window in which a build exists
    /// without its event, or an event without its outbox row.
    ///
    /// Idempotency is enforced by a unique constraint, not by check-then-insert:
    /// two concurrent submissions of the same key cannot both win, because the
    /// database decides rather than the application.
    member _.AdmitBuild(input: NewBuild) : Result<Admission, string> =
        if List.isEmpty input.StageNames then
            Error "a build must declare at least one stage"
        elif String.IsNullOrWhiteSpace input.IdempotencyKey then
            Error "idempotency key is required"
        elif isNull input.PipelineSource || input.PipelineSource.Length = 0 then
            Error "pipeline source is required"
        elif String.IsNullOrWhiteSpace input.RequiredTrustPool then
            Error "a trust pool is required"
        else

        use conn = openConn ()
        use tx = beginTenantTransaction conn input.OrganizationId

        try
            let sourceDigest = payloadDigest input.PipelineSource
            let fingerprint =
                admissionFingerprint input.PipelineSource input.RequiredTrustPool input.RequiredCapabilities

            // Allocate project-scoped build numbers under the project row lock.
            // Every admission takes locks in the same order (project, then
            // idempotency key), so distinct keys cannot race MAX(number) + 1 and
            // exact replays cannot introduce a lock-order cycle.
            use projectLock = conn.CreateCommand()
            projectLock.Transaction <- tx
            projectLock.CommandText <-
                "SELECT id
                   FROM projects
                  WHERE organization_id = @o AND id = @p
                  FOR UPDATE"
            projectLock.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
            projectLock.Parameters.AddWithValue("p", input.ProjectId.Value) |> ignore

            if isNull (projectLock.ExecuteScalar()) then
                invalidOp "project is unavailable"

            // Serialise one idempotency key before looking it up.  The unique
            // constraint remains the database backstop, while this lock lets
            // concurrent exact replays observe and return the committed winner
            // instead of surfacing a transient uniqueness error.
            use idempotencyLock = conn.CreateCommand()
            idempotencyLock.Transaction <- tx
            idempotencyLock.CommandText <-
                "SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0))"
            idempotencyLock.Parameters.AddWithValue(
                "identity",
                $"{input.OrganizationId.Value:N}/{input.ProjectId.Value:N}/{input.IdempotencyKey}")
            |> ignore
            idempotencyLock.ExecuteNonQuery() |> ignore

            // Has this key already been admitted? Read inside the transaction so
            // the unique constraint is the arbiter under concurrency.
            use existing = conn.CreateCommand()
            existing.Transaction <- tx
            existing.CommandText <-
                "SELECT b.id, b.number, n.id, a.id,
                        d.source_digest, d.admission_fingerprint
                 FROM builds b
                 JOIN nodes n    ON n.build_id = b.id AND n.organization_id = b.organization_id AND n.ordinal = 0
                 JOIN attempts a ON a.node_id = n.id  AND a.organization_id = n.organization_id AND a.ordinal = 0
                 LEFT JOIN build_definitions d
                   ON d.build_id = b.id AND d.organization_id = b.organization_id
                 WHERE b.organization_id = @o AND b.project_id = @p AND b.idempotency_key = @k"
            existing.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
            existing.Parameters.AddWithValue("p", input.ProjectId.Value) |> ignore
            existing.Parameters.AddWithValue("k", input.IdempotencyKey) |> ignore

            use reader = existing.ExecuteReader()

            if reader.Read() then
                let sameDefinition =
                    not (reader.IsDBNull 4)
                    && not (reader.IsDBNull 5)
                    && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(4), sourceDigest)
                    && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(5), fingerprint)

                if not sameDefinition then
                    reader.Close()
                    tx.Rollback()
                    Error "idempotency key is already bound to a different pipeline or placement policy"
                else
                    let result =
                        { BuildId = BuildId(reader.GetGuid 0)
                          Number = reader.GetInt32 1
                          NodeId = NodeId(reader.GetGuid 2)
                          AttemptId = AttemptId(reader.GetGuid 3)
                          WasExisting = true }

                    reader.Close()
                    tx.Commit()
                    Ok result
            else
                reader.Close()

                // Restore takes the conflicting singleton lock before it
                // invalidates old-epoch attempts. Hold the shared side through
                // creation so restore either invalidates this committed attempt,
                // or admission waits and is born directly into the new epoch.
                let buildId = Guid.NewGuid()
                let epoch =
                    use e = conn.CreateCommand()
                    e.Transaction <- tx
                    e.CommandText <-
                        "SELECT restore_epoch
                           FROM controller_metadata
                          WHERE singleton
                          FOR SHARE"
                    e.ExecuteScalar() :?> int64

                use insert = conn.CreateCommand()
                insert.Transaction <- tx
                insert.CommandText <-
                    "WITH next AS (
                         SELECT COALESCE(MAX(number), 0) + 1 AS n
                         FROM builds WHERE organization_id = @o AND project_id = @p
                     )
                     INSERT INTO builds (id, organization_id, project_id, number, idempotency_key, status)
                     SELECT @b, @o, @p, next.n, @k, 'queued' FROM next
                     RETURNING number"
                insert.Parameters.AddWithValue("b", buildId) |> ignore
                insert.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                insert.Parameters.AddWithValue("p", input.ProjectId.Value) |> ignore
                insert.Parameters.AddWithValue("k", input.IdempotencyKey) |> ignore
                let number = insert.ExecuteScalar() :?> int

                // The persisted runner owns one whole Jenkinsfile.  Scheduling
                // it once per parsed stage would duplicate cross-stage effects
                // and break environment/post semantics, so a build has exactly
                // one local execution node; stage detail remains in the source.
                let firstNode = Guid.NewGuid()
                use n = conn.CreateCommand()
                n.Transaction <- tx
                n.CommandText <-
                    "INSERT INTO nodes
                         (id, organization_id, build_id, name, ordinal, required_trust_pool,
                          required_capabilities, status)
                     VALUES (@id, @o, @b, 'pipeline', 0, @pool, @caps, 'queued')"
                n.Parameters.AddWithValue("id", firstNode) |> ignore
                n.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                n.Parameters.AddWithValue("b", buildId) |> ignore
                n.Parameters.AddWithValue("pool", input.RequiredTrustPool) |> ignore
                n.Parameters.AddWithValue("caps", List.toArray input.RequiredCapabilities) |> ignore
                n.ExecuteNonQuery() |> ignore

                let attemptId = Guid.NewGuid()

                use a = conn.CreateCommand()
                a.Transaction <- tx
                a.CommandText <-
                    "INSERT INTO attempts (id, organization_id, node_id, ordinal, state, fence, restore_epoch)
                     VALUES (@id, @o, @n, 0, 'queued', 0, @e)"
                a.Parameters.AddWithValue("id", attemptId) |> ignore
                a.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                a.Parameters.AddWithValue("n", firstNode) |> ignore
                a.Parameters.AddWithValue("e", epoch) |> ignore
                a.ExecuteNonQuery() |> ignore

                use definition = conn.CreateCommand()
                definition.Transaction <- tx
                definition.CommandText <-
                    "INSERT INTO build_definitions
                         (build_id, organization_id, source_bytes, source_digest, admission_fingerprint)
                     VALUES (@b, @o, @source, @digest, @fingerprint)"
                definition.Parameters.AddWithValue("b", buildId) |> ignore
                definition.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                definition.Parameters.AddWithValue("source", input.PipelineSource) |> ignore
                definition.Parameters.AddWithValue("digest", sourceDigest) |> ignore
                definition.Parameters.AddWithValue("fingerprint", fingerprint) |> ignore
                definition.ExecuteNonQuery() |> ignore

                use ev = conn.CreateCommand()
                ev.Transaction <- tx
                ev.CommandText <-
                    "INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
                     VALUES (@o, @b, @a, 'build.admitted', @pl)"
                ev.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                ev.Parameters.AddWithValue("b", buildId) |> ignore
                ev.Parameters.AddWithValue("a", attemptId) |> ignore
                ev.Parameters.Add(NpgsqlParameter("pl", NpgsqlTypes.NpgsqlDbType.Jsonb, Value = $"{{\"number\":{number}}}")) |> ignore
                ev.ExecuteNonQuery() |> ignore

                use ob = conn.CreateCommand()
                ob.Transaction <- tx
                ob.CommandText <-
                    "INSERT INTO outbox (organization_id, topic, body) VALUES (@o, 'build.admitted', @bd)"
                ob.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                ob.Parameters.Add(NpgsqlParameter("bd", NpgsqlTypes.NpgsqlDbType.Jsonb, Value = $"{{\"build\":\"{buildId}\"}}")) |> ignore
                ob.ExecuteNonQuery() |> ignore

                tx.Commit()

                Ok
                    { BuildId = BuildId buildId
                      NodeId = NodeId firstNode
                      AttemptId = AttemptId attemptId
                      Number = number
                      WasExisting = false }
        with ex ->
            (try tx.Rollback() with _ -> ())
            Error ex.Message

    /// FG-022. Offer an attempt to an agent: increments the fence, records the
    /// lease. Every offer bumps the fence, so an expired offer's holder can
    /// never publish against the next one.
    member _.OfferAttempt(org: OrganizationId, attempt: AttemptId, owner: string, leaseSeconds: int) : Result<Fence, string> =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "UPDATE attempts
                SET fence = fence + 1,
                    state = 'offered',
                    lease_owner = @owner,
                    lease_expires_at = clock_timestamp() + make_interval(secs => @secs)
              WHERE organization_id = @o AND id = @a
                AND state IN ('queued', 'offered')
              RETURNING fence"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("owner", owner) |> ignore
        cmd.Parameters.AddWithValue("secs", float leaseSeconds) |> ignore

        let result =
            match cmd.ExecuteScalar() with
            | null -> Error "attempt is not offerable"
            | v -> Ok(Fence(v :?> int64))
        tx.Commit()
        result

    member _.AcceptAttempt(org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string) : bool =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "WITH accepted AS (
                 UPDATE attempts
                    SET state = 'running'
                  WHERE organization_id = @o AND id = @a AND fence = @f AND lease_owner = @owner
                    AND state = 'offered' AND lease_expires_at > clock_timestamp()
                 RETURNING node_id
             ), running_node AS (
                 UPDATE nodes n
                    SET status = 'running'
                   FROM accepted a
                  WHERE n.organization_id = @o AND n.id = a.node_id
                 RETURNING n.build_id
             )
             UPDATE builds b
                SET status = 'running'
               FROM running_node n
              WHERE b.organization_id = @o AND b.id = n.build_id"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
        cmd.Parameters.AddWithValue("owner", owner) |> ignore
        let accepted = cmd.ExecuteNonQuery() = 1
        tx.Commit()
        accepted

    /// Linearization point between queued/offered work and child execution.
    /// The build row is locked together with the fenced attempt, so a queued
    /// cancellation either wins here and becomes an atomic ABORTED terminal
    /// result, or observes RUNNING after this transaction and is handled as an
    /// in-flight cancellation. User code is never launched on the former path.
    member _.BeginExecution(org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string, leaseSeconds: int)
        : Result<ExecutionStartOutcome, string> =
        if leaseSeconds <= 0 then
            Error "execution start requires a positive lease"
        else
            use conn = openConn ()
            use tx = beginTenantTransaction conn org

            try
                match lockExecutionAuthority conn tx org attempt fence owner true with
                | None ->
                    tx.Rollback()
                    Error "execution start refused: stale fence, wrong owner, expired lease, pre-restore epoch, or invalid state"
                | Some(nodeId, buildId, true) ->
                    match publishLockedTerminal conn tx org attempt fence owner nodeId buildId BuildStatus.Aborted with
                    | Error error ->
                        tx.Rollback()
                        Error error
                    | Ok _ ->
                        tx.Commit()
                        Ok ExecutionCancelledBeforeStart
                | Some(nodeId, buildId, false) ->
                    use startAttempt = conn.CreateCommand()
                    startAttempt.Transaction <- tx
                    startAttempt.CommandText <-
                        "UPDATE attempts
                            SET state = 'running',
                                lease_expires_at = clock_timestamp() + make_interval(secs => @secs)
                          WHERE organization_id = @o AND id = @a AND fence = @f
                            AND lease_owner = @owner AND state = 'offered'
                            AND lease_expires_at > clock_timestamp()
                            AND restore_epoch = (SELECT restore_epoch FROM controller_metadata WHERE singleton)"
                    startAttempt.Parameters.AddWithValue("o", org.Value) |> ignore
                    startAttempt.Parameters.AddWithValue("a", attempt.Value) |> ignore
                    startAttempt.Parameters.AddWithValue("f", fence.Value) |> ignore
                    startAttempt.Parameters.AddWithValue("owner", owner) |> ignore
                    startAttempt.Parameters.AddWithValue("secs", float leaseSeconds) |> ignore

                    if startAttempt.ExecuteNonQuery() <> 1 then
                        tx.Rollback()
                        Error "execution start refused after authority validation"
                    else
                        use startNode = conn.CreateCommand()
                        startNode.Transaction <- tx
                        startNode.CommandText <-
                            "UPDATE nodes SET status = 'running'
                              WHERE organization_id = @o AND id = @n AND build_id = @b"
                        startNode.Parameters.AddWithValue("o", org.Value) |> ignore
                        startNode.Parameters.AddWithValue("n", nodeId) |> ignore
                        startNode.Parameters.AddWithValue("b", buildId) |> ignore

                        if startNode.ExecuteNonQuery() <> 1 then
                            failwith "starting attempt has no node lineage"

                        use startBuild = conn.CreateCommand()
                        startBuild.Transaction <- tx
                        startBuild.CommandText <-
                            "UPDATE builds SET status = 'running'
                              WHERE organization_id = @o AND id = @b"
                        startBuild.Parameters.AddWithValue("o", org.Value) |> ignore
                        startBuild.Parameters.AddWithValue("b", buildId) |> ignore

                        if startBuild.ExecuteNonQuery() <> 1 then
                            failwith "starting attempt has no build lineage"

                        tx.Commit()
                        Ok ExecutionStarted
            with ex ->
                (try tx.Rollback() with _ -> ())
                Error ex.Message

    member _.RenewLease
        (org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string, leaseSeconds: int)
        : bool =
        if leaseSeconds <= 0 then
            false
        else
            use conn = openConn ()
            use tx = beginTenantTransaction conn org
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <-
                "UPDATE attempts
                    SET lease_expires_at = clock_timestamp() + make_interval(secs => @secs)
                  WHERE organization_id = @o AND id = @a AND fence = @f
                    AND lease_owner = @owner
                    AND state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
                    AND restore_epoch = (SELECT restore_epoch FROM controller_metadata WHERE singleton)
                    AND lease_expires_at > clock_timestamp()"
            cmd.Parameters.AddWithValue("o", org.Value) |> ignore
            cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
            cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
            cmd.Parameters.AddWithValue("owner", owner) |> ignore
            cmd.Parameters.AddWithValue("secs", float leaseSeconds) |> ignore
            let renewed = cmd.ExecuteNonQuery() = 1
            tx.Commit()
            renewed

    /// FG-022. Publish a terminal result. Admissible only from the exact current
    /// fence, the exact lease owner, the current restore epoch, an unexpired
    /// lease and an active state — all seven conditions from ADR 0007.
    ///
    /// The attempt, node, and build are locked as one lineage before choosing
    /// the result. RequestCancellation locks the same build row. Consequently a
    /// cancellation committed before this decision makes ABORTED the effective
    /// terminal truth; a terminal publication committed first makes the later
    /// cancellation report AlreadyTerminal. An accepted cancellation can never
    /// be erased by a success publication racing behind it.
    ///
    /// `clock_timestamp()` deliberately, not `now()`: `now()` is transaction
    /// start time, so inside a long transaction an expired lease would still
    /// read as valid.
    member _.PublishTerminal
        (org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string, result: BuildStatus)
        : Result<unit, string> =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        try
            match lockExecutionAuthority conn tx org attempt fence owner false with
            | None ->
                tx.Rollback()
                Error "publication refused: stale fence, wrong owner, expired lease, pre-restore epoch, or already terminal"
            | Some(nodeId, buildId, cancellationRequested) ->
                let effectiveResult = if cancellationRequested then BuildStatus.Aborted else result

                match publishLockedTerminal conn tx org attempt fence owner nodeId buildId effectiveResult with
                | Error error ->
                    tx.Rollback()
                    Error error
                | Ok _ ->
                    tx.Commit()
                    Ok()
        with ex ->
            (try tx.Rollback() with _ -> ())
            Error ex.Message

    /// A restore invalidates every lease issued before it. Pre-restore agents
    /// can no longer renew or publish; their attempts require reconciliation.
    member _.ActivateRestore() : RestoreEpoch =
        use conn = openMaintenanceConn ()
        use tx = conn.BeginTransaction()

        try
            use bump = conn.CreateCommand()
            bump.Transaction <- tx
            bump.CommandText <-
                "UPDATE controller_metadata SET restore_epoch = restore_epoch + 1
                  WHERE singleton RETURNING restore_epoch"
            let epoch = bump.ExecuteScalar() :?> int64

            // organization_work_roots is the deliberately global UUID registry
            // used by restarted workers. Iterate that authority while setting the
            // same transaction-local tenant context as runtime operations: forced
            // RLS stays enabled, and the epoch bump plus every invalidation remain
            // one atomic transaction even for a NOBYPASSRLS maintenance role.
            use roots = conn.CreateCommand()
            roots.Transaction <- tx
            roots.CommandText <- "SELECT organization_id FROM organization_work_roots ORDER BY organization_id"
            use rootReader = roots.ExecuteReader()
            let organizations = [ while rootReader.Read() do yield OrganizationId(rootReader.GetGuid 0) ]
            rootReader.Close()

            for org in organizations do
                setTenantContext conn tx org
                use invalidate = conn.CreateCommand()
                invalidate.Transaction <- tx
                invalidate.CommandText <-
                    "WITH moved AS (
                         UPDATE attempts a
                            SET state = 'reconciliation_required',
                                lease_owner = NULL,
                                lease_expires_at = NULL
                           FROM nodes n
                          WHERE a.organization_id = @o
                            AND a.restore_epoch < @e
                            AND a.state IN ('queued', 'offered', 'accepted', 'running',
                                            'finalizing', 'cancelling')
                            AND n.organization_id = a.organization_id
                            AND n.id = a.node_id
                         RETURNING a.id AS attempt_id, a.node_id, n.build_id
                     ), reconciled_nodes AS (
                         UPDATE nodes n
                            SET status = 'reconciliation_required'
                          FROM (SELECT DISTINCT node_id FROM moved) m
                          WHERE n.organization_id = @o AND n.id = m.node_id
                         RETURNING n.id, n.build_id
                     ), reconciled_builds AS (
                         UPDATE builds b
                            SET status = 'reconciliation_required'
                           FROM (SELECT DISTINCT build_id FROM reconciled_nodes) m
                          WHERE b.organization_id = @o AND b.id = m.build_id
                         RETURNING b.id
                     ), emitted_events AS (
                         INSERT INTO events
                                (organization_id, build_id, attempt_id, kind, payload)
                         SELECT @o, build_id, attempt_id,
                                'attempt.reconciliation_required',
                                jsonb_build_object('reason', 'restore_epoch_advanced')
                           FROM moved
                           CROSS JOIN (SELECT count(*) FROM reconciled_builds) rollup
                         RETURNING 1
                     ), emitted_outbox AS (
                         INSERT INTO outbox (organization_id, topic, body)
                         SELECT @o, 'build.reconciliation_required',
                                jsonb_build_object(
                                    'build', build_id::text,
                                    'attempt', attempt_id::text,
                                    'reason', 'restore_epoch_advanced')
                           FROM moved
                           CROSS JOIN (SELECT count(*) FROM reconciled_builds) rollup
                         RETURNING 1
                     )
                     SELECT (SELECT count(*) FROM moved),
                            (SELECT count(*) FROM reconciled_nodes),
                            (SELECT count(*) FROM reconciled_builds),
                            (SELECT count(*) FROM emitted_events),
                            (SELECT count(*) FROM emitted_outbox)"
                invalidate.Parameters.AddWithValue("o", org.Value) |> ignore
                invalidate.Parameters.AddWithValue("e", epoch) |> ignore
                use invalidated = invalidate.ExecuteReader()
                invalidated.Read() |> ignore
                let moved = invalidated.GetInt64 0

                if invalidated.GetInt64 3 <> moved || invalidated.GetInt64 4 <> moved then
                    failwith "restore reconciliation truth was not emitted exactly once per invalidated attempt"

                invalidated.Close()

            tx.Commit()
            RestoreEpoch epoch
        with _ ->
            (try tx.Rollback() with _ -> ())
            reraise ()

    member _.AttemptState(org: OrganizationId, attempt: AttemptId) : (string * int64 * string option) option =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT state, fence, result FROM attempts WHERE organization_id = @o AND id = @a"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore

        use r = cmd.ExecuteReader()

        let state =
            if r.Read() then
                Some(r.GetString 0, r.GetInt64 1, (if r.IsDBNull 2 then None else Some(r.GetString 2)))
            else
                None
        r.Close()
        tx.Commit()
        state

    member _.CountEvents(org: OrganizationId, build: BuildId, kind: string) : int =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT count(*) FROM events WHERE organization_id = @o AND build_id = @b AND kind = @k"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        cmd.Parameters.AddWithValue("k", kind) |> ignore
        let count = cmd.ExecuteScalar() :?> int64 |> int
        tx.Commit()
        count

    /// FG-061. Claim the next offerable attempt for an agent.
    ///
    /// Serialised per tenant with a transaction advisory lock, and candidates are
    /// selected FOR UPDATE SKIP LOCKED so two schedulers never hand the same
    /// attempt to two agents. Capability containment is checked in SQL rather
    /// than in application code, so a mismatch cannot be waved through.
    member _.ClaimNext
        (org: OrganizationId, agentId: string, trustPool: string, capabilities: string list, leaseSeconds: int)
        : Result<(AttemptId * NodeId * BuildId * Fence) option, string> =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        try
            use lock = conn.CreateCommand()
            lock.Transaction <- tx
            lock.CommandText <- "SELECT pg_advisory_xact_lock(hashtext(@o))"
            lock.Parameters.AddWithValue("o", org.Value.ToString()) |> ignore
            lock.ExecuteNonQuery() |> ignore

            use pick = conn.CreateCommand()
            pick.Transaction <- tx
            pick.CommandText <-
                "SELECT a.id, n.id, n.build_id
                 FROM attempts a
                 JOIN nodes n ON n.id = a.node_id AND n.organization_id = a.organization_id
                 WHERE a.organization_id = @o
                   AND a.state = 'queued'
                   AND n.required_trust_pool = @pool
                   AND n.required_capabilities <@ @caps
                 ORDER BY a.created_at, a.id
                 FOR UPDATE OF a SKIP LOCKED
                 LIMIT 1"
            pick.Parameters.AddWithValue("o", org.Value) |> ignore
            pick.Parameters.AddWithValue("pool", trustPool) |> ignore
            pick.Parameters.AddWithValue("caps", List.toArray capabilities) |> ignore

            use reader = pick.ExecuteReader()

            if not (reader.Read()) then
                reader.Close()
                tx.Commit()
                Ok None
            else
                let attemptId = reader.GetGuid 0
                let nodeId = reader.GetGuid 1
                let buildId = reader.GetGuid 2
                reader.Close()

                use offer = conn.CreateCommand()
                offer.Transaction <- tx
                offer.CommandText <-
                    "UPDATE attempts
                        SET fence = fence + 1, state = 'offered', lease_owner = @agent,
                            lease_expires_at = clock_timestamp() + make_interval(secs => @secs)
                      WHERE organization_id = @o AND id = @a
                      RETURNING fence"
                offer.Parameters.AddWithValue("o", org.Value) |> ignore
                offer.Parameters.AddWithValue("a", attemptId) |> ignore
                offer.Parameters.AddWithValue("agent", agentId) |> ignore
                offer.Parameters.AddWithValue("secs", float leaseSeconds) |> ignore
                let fence = offer.ExecuteScalar() :?> int64

                tx.Commit()
                Ok(Some(AttemptId attemptId, NodeId nodeId, BuildId buildId, Fence fence))
        with ex ->
            (try tx.Rollback() with _ -> ())
            Error ex.Message

    /// FG-224 local-controller claim.  Unlike the legacy scheduler projection,
    /// this returns the immutable whole-pipeline bytes under the same lock and
    /// refuses legacy multi-node builds that cannot honestly be mapped onto the
    /// whole-pipeline runner.
    member _.ClaimNextExecution
        (org: OrganizationId, agentId: string, trustPool: string, capabilities: string list, leaseSeconds: int)
        : Result<ExecutionClaim option, string> =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        try
            use lock = conn.CreateCommand()
            lock.Transaction <- tx
            lock.CommandText <- "SELECT pg_advisory_xact_lock(hashtext(@o))"
            lock.Parameters.AddWithValue("o", org.Value.ToString()) |> ignore
            lock.ExecuteNonQuery() |> ignore

            let quarantine attemptId nodeId buildId reason =
                // The attempt was locked by the FIFO pick. Complete the same
                // attempt -> node -> build lock order used by every execution
                // roll-up before publishing the durable refusal.
                use nodeLock = conn.CreateCommand()
                nodeLock.Transaction <- tx
                nodeLock.CommandText <-
                    "SELECT build_id FROM nodes
                      WHERE organization_id = @o AND id = @n
                      FOR UPDATE"
                nodeLock.Parameters.AddWithValue("o", org.Value) |> ignore
                nodeLock.Parameters.AddWithValue("n", nodeId) |> ignore

                match nodeLock.ExecuteScalar() with
                | :? Guid as lockedBuildId when lockedBuildId = buildId -> ()
                | _ -> failwith "invalid execution claim has no node lineage"

                use buildLock = conn.CreateCommand()
                buildLock.Transaction <- tx
                buildLock.CommandText <-
                    "SELECT id FROM builds
                      WHERE organization_id = @o AND id = @b
                      FOR UPDATE"
                buildLock.Parameters.AddWithValue("o", org.Value) |> ignore
                buildLock.Parameters.AddWithValue("b", buildId) |> ignore

                if isNull (buildLock.ExecuteScalar()) then
                    failwith "invalid execution claim has no build lineage"

                use rejectAttempt = conn.CreateCommand()
                rejectAttempt.Transaction <- tx
                rejectAttempt.CommandText <-
                    "UPDATE attempts
                        SET state = 'reconciliation_required',
                            lease_owner = NULL,
                            lease_expires_at = NULL
                      WHERE organization_id = @o AND id = @a AND state = 'queued'"
                rejectAttempt.Parameters.AddWithValue("o", org.Value) |> ignore
                rejectAttempt.Parameters.AddWithValue("a", attemptId) |> ignore

                if rejectAttempt.ExecuteNonQuery() <> 1 then
                    failwith "invalid execution claim changed after FIFO arbitration"

                use rejectNode = conn.CreateCommand()
                rejectNode.Transaction <- tx
                rejectNode.CommandText <-
                    "UPDATE nodes SET status = 'reconciliation_required'
                      WHERE organization_id = @o AND id = @n AND build_id = @b"
                rejectNode.Parameters.AddWithValue("o", org.Value) |> ignore
                rejectNode.Parameters.AddWithValue("n", nodeId) |> ignore
                rejectNode.Parameters.AddWithValue("b", buildId) |> ignore

                if rejectNode.ExecuteNonQuery() <> 1 then
                    failwith "invalid execution claim has no node to quarantine"

                use rejectBuild = conn.CreateCommand()
                rejectBuild.Transaction <- tx
                rejectBuild.CommandText <-
                    "UPDATE builds SET status = 'reconciliation_required'
                      WHERE organization_id = @o AND id = @b"
                rejectBuild.Parameters.AddWithValue("o", org.Value) |> ignore
                rejectBuild.Parameters.AddWithValue("b", buildId) |> ignore

                if rejectBuild.ExecuteNonQuery() <> 1 then
                    failwith "invalid execution claim has no build to quarantine"

                let refusalPayload = JsonSerializer.Serialize(dict [ "reason", reason ])
                use refusal = conn.CreateCommand()
                refusal.Transaction <- tx
                refusal.CommandText <-
                    "INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
                     VALUES (@o, @b, @a, 'attempt.reconciliation_required', @payload)"
                refusal.Parameters.AddWithValue("o", org.Value) |> ignore
                refusal.Parameters.AddWithValue("b", buildId) |> ignore
                refusal.Parameters.AddWithValue("a", attemptId) |> ignore
                refusal.Parameters.Add(
                    NpgsqlParameter(
                        "payload",
                        NpgsqlTypes.NpgsqlDbType.Jsonb,
                        Value = refusalPayload))
                |> ignore
                refusal.ExecuteNonQuery() |> ignore

                let outboxBody =
                    JsonSerializer.Serialize(
                        dict
                            [ "build", buildId.ToString()
                              "attempt", attemptId.ToString()
                              "reason", reason ])
                use outbox = conn.CreateCommand()
                outbox.Transaction <- tx
                outbox.CommandText <-
                    "INSERT INTO outbox (organization_id, topic, body)
                     VALUES (@o, 'build.reconciliation_required', @body)"
                outbox.Parameters.AddWithValue("o", org.Value) |> ignore
                outbox.Parameters.Add(
                    NpgsqlParameter("body", NpgsqlTypes.NpgsqlDbType.Jsonb, Value = outboxBody))
                |> ignore
                outbox.ExecuteNonQuery() |> ignore

            let readCandidate () =
                use pick = conn.CreateCommand()
                pick.Transaction <- tx
                pick.CommandText <-
                    "SELECT a.id, n.id, n.build_id, b.project_id, b.number,
                            d.source_bytes, d.source_digest, n.ordinal,
                            (SELECT count(*) FROM nodes all_nodes
                              WHERE all_nodes.organization_id = b.organization_id
                                AND all_nodes.build_id = b.id) AS node_count
                       FROM attempts a
                       JOIN nodes n
                         ON n.id = a.node_id AND n.organization_id = a.organization_id
                       JOIN builds b
                         ON b.id = n.build_id AND b.organization_id = n.organization_id
                       LEFT JOIN build_definitions d
                         ON d.build_id = b.id AND d.organization_id = b.organization_id
                      WHERE a.organization_id = @o
                        AND a.state = 'queued'
                        AND n.status = 'queued'
                        AND b.status = 'queued'
                        AND n.required_trust_pool = @pool
                        AND n.required_capabilities <@ @caps
                      ORDER BY a.created_at, a.id
                      FOR UPDATE OF a SKIP LOCKED
                      LIMIT 1"
                pick.Parameters.AddWithValue("o", org.Value) |> ignore
                pick.Parameters.AddWithValue("pool", trustPool) |> ignore
                pick.Parameters.AddWithValue("caps", List.toArray capabilities) |> ignore
                use reader = pick.ExecuteReader()

                if not (reader.Read()) then
                    None
                else
                    let attemptId = reader.GetGuid 0
                    let nodeId = reader.GetGuid 1
                    let buildId = reader.GetGuid 2
                    let projectId = reader.GetGuid 3
                    let buildNumber = reader.GetInt32 4
                    let hasDefinition = not (reader.IsDBNull 5) && not (reader.IsDBNull 6)
                    let source = if hasDefinition then reader.GetFieldValue<byte array> 5 else Array.empty
                    let sourceDigest = if hasDefinition then reader.GetFieldValue<byte array> 6 else Array.empty
                    let nodeOrdinal = reader.GetInt32 7
                    let nodeCount = reader.GetInt64 8

                    Some(
                        attemptId,
                        nodeId,
                        buildId,
                        projectId,
                        buildNumber,
                        hasDefinition,
                        source,
                        sourceDigest,
                        nodeOrdinal,
                        nodeCount)

            let rec claimNext () =
                match readCandidate () with
                | None -> None
                | Some(
                    attemptId,
                    nodeId,
                    buildId,
                    projectId,
                    buildNumber,
                    hasDefinition,
                    source,
                    sourceDigest,
                    nodeOrdinal,
                    nodeCount) ->

                    let refusalReason =
                        if not hasDefinition then
                            Some "missing_definition"
                        elif nodeOrdinal <> 0 || nodeCount <> 1L then
                            Some "legacy_multi_node"
                        elif not (CryptographicOperations.FixedTimeEquals(payloadDigest source, sourceDigest)) then
                            Some "definition_digest_mismatch"
                        else
                            None

                    match refusalReason with
                    | Some reason ->
                        quarantine attemptId nodeId buildId reason
                        claimNext ()
                    | None ->
                        use offer = conn.CreateCommand()
                        offer.Transaction <- tx
                        offer.CommandText <-
                            "UPDATE attempts
                                SET fence = fence + 1, state = 'offered', lease_owner = @agent,
                                    lease_expires_at = clock_timestamp() + make_interval(secs => @secs)
                              WHERE organization_id = @o AND id = @a AND state = 'queued'
                              RETURNING fence"
                        offer.Parameters.AddWithValue("o", org.Value) |> ignore
                        offer.Parameters.AddWithValue("a", attemptId) |> ignore
                        offer.Parameters.AddWithValue("agent", agentId) |> ignore
                        offer.Parameters.AddWithValue("secs", float leaseSeconds) |> ignore

                        match offer.ExecuteScalar() with
                        | null -> failwith "execution candidate changed after FIFO arbitration"
                        | value ->
                            Some
                                { OrganizationId = org
                                  ProjectId = ProjectId projectId
                                  BuildId = BuildId buildId
                                  BuildNumber = buildNumber
                                  NodeId = NodeId nodeId
                                  AttemptId = AttemptId attemptId
                                  Fence = Fence(value :?> int64)
                                  PipelineSource = source
                                  PipelineSha256 = digestHex sourceDigest }

            let claimed = claimNext ()
            tx.Commit()
            Ok claimed
        with ex ->
            (try tx.Rollback() with _ -> ())
            Error ex.Message

    /// Recover expired in-process leases without confusing lease expiry with
    /// child termination. An offered attempt is still before BeginExecution's
    /// launch linearization point, so it is safe to offer again. Every later
    /// active state is ambiguous: the old child may still be running or may
    /// have written journal/effect state that has not been observed. Those
    /// attempts fail closed until reconciliation. RequeueOwnedAttempt is the
    /// separate caller-authorized path and must only be used after every child
    /// process has been confirmed terminated.
    member _.RequeueExpiredLocalAttempts(org: OrganizationId) : int =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "WITH expired AS MATERIALIZED (
                 SELECT id, node_id, state
                   FROM attempts
                  WHERE organization_id = @o
                    AND lease_owner LIKE 'local:%'
                    AND lease_expires_at <= clock_timestamp()
                    AND state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
                  ORDER BY id
                  FOR UPDATE
             ), moved AS (
                 UPDATE attempts a
                    SET state = CASE WHEN e.state = 'offered'
                                     THEN 'queued'
                                     ELSE 'reconciliation_required'
                                END,
                        lease_owner = NULL,
                        lease_expires_at = NULL
                   FROM expired e
                  WHERE a.organization_id = @o AND a.id = e.id
                 RETURNING a.id AS attempt_id, a.node_id, a.state
             ), reconciled_nodes AS (
                 UPDATE nodes n
                    SET status = 'reconciliation_required'
                   FROM (SELECT DISTINCT node_id
                           FROM moved
                          WHERE state = 'reconciliation_required') m
                  WHERE n.organization_id = @o AND n.id = m.node_id
                 RETURNING n.id AS node_id, n.build_id
             ), reconciled_builds AS (
                 UPDATE builds b
                    SET status = 'reconciliation_required'
                   FROM (SELECT DISTINCT build_id FROM reconciled_nodes) n
                  WHERE b.organization_id = @o AND b.id = n.build_id
                 RETURNING b.id AS build_id
             ), reconciliation_rows AS MATERIALIZED (
                 SELECT m.attempt_id, n.build_id
                   FROM moved m
                   JOIN reconciled_nodes n ON n.node_id = m.node_id
                   JOIN reconciled_builds b ON b.build_id = n.build_id
                  WHERE m.state = 'reconciliation_required'
             ), published_events AS (
                 INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
                 SELECT @o, r.build_id, r.attempt_id,
                        'attempt.reconciliation_required',
                        jsonb_build_object('reason', 'lease_expired')
                   FROM reconciliation_rows r
                 RETURNING build_id, attempt_id
             ), published_outbox AS (
                 INSERT INTO outbox (organization_id, topic, body)
                 SELECT @o, 'build.reconciliation_required',
                        jsonb_build_object(
                            'build', e.build_id::text,
                            'attempt', e.attempt_id::text,
                            'reason', 'lease_expired')
                   FROM published_events e
                 RETURNING id
             )
             SELECT (SELECT count(*)::integer FROM moved),
                    (SELECT count(*)::integer FROM reconciled_nodes),
                    (SELECT count(*)::integer FROM reconciled_builds),
                    (SELECT count(*)::integer FROM published_events),
                    (SELECT count(*)::integer FROM published_outbox)"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        use reader = cmd.ExecuteReader()
        let count = if reader.Read() then reader.GetInt32 0 else 0
        reader.Close()
        tx.Commit()
        count

    member _.RequeueOwnedAttempt(org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string) : bool =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        try
            // This path is authorized only after the caller has proved process
            // extinction. Lock the same lineage in the same attempt -> node ->
            // build order as execution start and terminal publication. The
            // build's cancellation_requested bit is deliberately not cleared:
            // if cancellation raced shutdown, the replacement claim observes it
            // and becomes ABORTED before launching another child.
            match lockExecutionAuthority conn tx org attempt fence owner false with
            | None ->
                tx.Rollback()
                false
            | Some(nodeId, buildId, _) ->
                use requeueAttempt = conn.CreateCommand()
                requeueAttempt.Transaction <- tx
                requeueAttempt.CommandText <-
                    "UPDATE attempts
                        SET state = 'queued', lease_owner = NULL, lease_expires_at = NULL
                      WHERE organization_id = @o AND id = @a AND fence = @f
                        AND lease_owner = @owner
                        AND state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')"
                requeueAttempt.Parameters.AddWithValue("o", org.Value) |> ignore
                requeueAttempt.Parameters.AddWithValue("a", attempt.Value) |> ignore
                requeueAttempt.Parameters.AddWithValue("f", fence.Value) |> ignore
                requeueAttempt.Parameters.AddWithValue("owner", owner) |> ignore

                if requeueAttempt.ExecuteNonQuery() <> 1 then
                    tx.Rollback()
                    false
                else
                    use requeueNode = conn.CreateCommand()
                    requeueNode.Transaction <- tx
                    requeueNode.CommandText <-
                        "UPDATE nodes SET status = 'queued'
                          WHERE organization_id = @o AND id = @n AND build_id = @b"
                    requeueNode.Parameters.AddWithValue("o", org.Value) |> ignore
                    requeueNode.Parameters.AddWithValue("n", nodeId) |> ignore
                    requeueNode.Parameters.AddWithValue("b", buildId) |> ignore

                    if requeueNode.ExecuteNonQuery() <> 1 then
                        failwith "requeued attempt has no node lineage"

                    use requeueBuild = conn.CreateCommand()
                    requeueBuild.Transaction <- tx
                    requeueBuild.CommandText <-
                        "UPDATE builds SET status = 'queued'
                          WHERE organization_id = @o AND id = @b"
                    requeueBuild.Parameters.AddWithValue("o", org.Value) |> ignore
                    requeueBuild.Parameters.AddWithValue("b", buildId) |> ignore

                    if requeueBuild.ExecuteNonQuery() <> 1 then
                        failwith "requeued attempt has no build lineage"

                    tx.Commit()
                    true
        with _ ->
            (try tx.Rollback() with _ -> ())
            reraise ()

    member _.RequireReconciliation
        (org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string, reason: string)
        : bool =
        if
            String.IsNullOrWhiteSpace reason
            || reason.Length > 128
            || not (reason |> Seq.forall (fun c -> Char.IsLower c || Char.IsDigit c || c = '_'))
        then
            invalidArg (nameof reason) "reconciliation reason must be a stable lowercase code of at most 128 characters"

        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use attemptCmd = conn.CreateCommand()
        attemptCmd.Transaction <- tx
        attemptCmd.CommandText <-
            "UPDATE attempts
                SET state = 'reconciliation_required', lease_owner = NULL, lease_expires_at = NULL
              WHERE organization_id = @o AND id = @a AND fence = @f AND lease_owner = @owner
                AND lease_expires_at > clock_timestamp()
                AND restore_epoch = (SELECT restore_epoch FROM controller_metadata WHERE singleton)
                AND state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
              RETURNING node_id"
        attemptCmd.Parameters.AddWithValue("o", org.Value) |> ignore
        attemptCmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        attemptCmd.Parameters.AddWithValue("f", fence.Value) |> ignore
        attemptCmd.Parameters.AddWithValue("owner", owner) |> ignore

        match attemptCmd.ExecuteScalar() with
        | null ->
            tx.Rollback()
            false
        | nodeId ->
            use rollup = conn.CreateCommand()
            rollup.Transaction <- tx
            rollup.CommandText <-
                "WITH changed_node AS (
                     UPDATE nodes
                        SET status = 'reconciliation_required'
                      WHERE organization_id = @o AND id = @n
                     RETURNING build_id
                 ), changed_build AS (
                     UPDATE builds b
                        SET status = 'reconciliation_required'
                       FROM changed_node n
                      WHERE b.organization_id = @o AND b.id = n.build_id
                     RETURNING b.id
                 )
                 SELECT id FROM changed_build"
            rollup.Parameters.AddWithValue("o", org.Value) |> ignore
            rollup.Parameters.AddWithValue("n", nodeId :?> Guid) |> ignore
            let buildId =
                match rollup.ExecuteScalar() with
                | :? Guid as value -> value
                | _ -> failwith "reconciled attempt has no build lineage"

            let eventPayload = JsonSerializer.Serialize(dict [ "reason", reason ])
            use event = conn.CreateCommand()
            event.Transaction <- tx
            event.CommandText <-
                "INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
                 VALUES (@o, @b, @a, 'attempt.reconciliation_required', @payload)"
            event.Parameters.AddWithValue("o", org.Value) |> ignore
            event.Parameters.AddWithValue("b", buildId) |> ignore
            event.Parameters.AddWithValue("a", attempt.Value) |> ignore
            event.Parameters.Add(
                NpgsqlParameter("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, Value = eventPayload))
            |> ignore
            event.ExecuteNonQuery() |> ignore

            let outboxBody =
                JsonSerializer.Serialize(
                    dict
                        [ "build", buildId.ToString()
                          "attempt", attempt.Value.ToString()
                          "reason", reason ])
            use outbox = conn.CreateCommand()
            outbox.Transaction <- tx
            outbox.CommandText <-
                "INSERT INTO outbox (organization_id, topic, body)
                 VALUES (@o, 'build.reconciliation_required', @body)"
            outbox.Parameters.AddWithValue("o", org.Value) |> ignore
            outbox.Parameters.Add(
                NpgsqlParameter("body", NpgsqlTypes.NpgsqlDbType.Jsonb, Value = outboxBody))
            |> ignore
            outbox.ExecuteNonQuery() |> ignore
            tx.Commit()
            true

    member _.BuildCancellationRequested(org: OrganizationId, build: BuildId) : bool =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT cancellation_requested
               FROM builds
              WHERE organization_id = @o AND id = @b"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        let requested =
            match cmd.ExecuteScalar() with
            | :? bool as value -> value
            | _ -> false
        tx.Commit()
        requested

    /// FG-061 wait diagnostics. Distinguishes an EMPTY queue from a concrete
    /// capability mismatch, and names the missing capabilities — Jenkins' own
    /// "There are no nodes with the label X" is the behaviour worth matching
    /// (JB-AGT-004), and it is far better than an unexplained wait.
    member _.ExplainWait(org: OrganizationId, trustPool: string, capabilities: string list) : string =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT count(*) FILTER (WHERE a.state = 'queued') AS queued,
                    count(*) FILTER (WHERE a.state = 'queued' AND n.required_trust_pool = @pool) AS pool_ok,
                    count(*) FILTER (WHERE a.state = 'queued' AND n.required_trust_pool = @pool
                                       AND n.required_capabilities <@ @caps) AS claimable,
                    coalesce(
                        (SELECT string_agg(DISTINCT c, ', ')
                         FROM attempts a2
                         JOIN nodes n2 ON n2.id = a2.node_id AND n2.organization_id = a2.organization_id
                         CROSS JOIN unnest(n2.required_capabilities) AS c
                         WHERE a2.organization_id = @o AND a2.state = 'queued'
                           AND NOT (c = ANY(@caps))), '') AS missing
             FROM attempts a
             JOIN nodes n ON n.id = a.node_id AND n.organization_id = a.organization_id
             WHERE a.organization_id = @o"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("pool", trustPool) |> ignore
        cmd.Parameters.AddWithValue("caps", List.toArray capabilities) |> ignore

        use r = cmd.ExecuteReader()

        let explanation =
            if not (r.Read()) then
                "queue is empty"
            else
                let queued = r.GetInt64 0
                let poolOk = r.GetInt64 1
                let claimable = r.GetInt64 2
                let missing = r.GetString 3

                if queued = 0L then "queue is empty"
                elif claimable > 0L then $"{claimable} attempt(s) claimable now"
                elif poolOk = 0L then $"{queued} attempt(s) queued, none in trust pool '{trustPool}'"
                elif missing <> "" then $"{queued} attempt(s) queued; missing capabilities: {missing}"
                else $"{queued} attempt(s) queued, none claimable"
        r.Close()
        tx.Commit()
        explanation

    /// Lock the canonical lineage in the same explicit order as execution
    /// roll-up: attempt -> node -> build. Both attempts.node_id and
    /// nodes.build_id are mutable columns, so locking only the attempt or using
    /// a multi-table query leaves a substitution window. The requested build
    /// is checked only after the actual lineage and its build row are locked.
    /// A concurrent duplicate waiter also acquires the attempt only after the
    /// winner commits, so its following READ COMMITTED duplicate query sees the
    /// committed chunk without burning a build cursor.
    member private _.LockLogLineage
        (conn: NpgsqlConnection,
         tx: NpgsqlTransaction,
         org: OrganizationId,
         build: BuildId,
         attempt: AttemptId)
        : bool =
        use attemptLock = conn.CreateCommand()
        attemptLock.Transaction <- tx
        attemptLock.CommandText <-
            "SELECT node_id FROM attempts
              WHERE organization_id = @o AND id = @a
              FOR UPDATE"
        attemptLock.Parameters.AddWithValue("o", org.Value) |> ignore
        attemptLock.Parameters.AddWithValue("a", attempt.Value) |> ignore

        match attemptLock.ExecuteScalar() with
        | null -> false
        | nodeValue ->
            let nodeId = nodeValue :?> Guid
            use nodeLock = conn.CreateCommand()
            nodeLock.Transaction <- tx
            nodeLock.CommandText <-
                "SELECT build_id FROM nodes
                  WHERE organization_id = @o AND id = @n
                  FOR UPDATE"
            nodeLock.Parameters.AddWithValue("o", org.Value) |> ignore
            nodeLock.Parameters.AddWithValue("n", nodeId) |> ignore

            match nodeLock.ExecuteScalar() with
            | null -> false
            | buildValue ->
                let lockedBuildId = buildValue :?> Guid
                use buildLock = conn.CreateCommand()
                buildLock.Transaction <- tx
                buildLock.CommandText <-
                    "SELECT id FROM builds
                      WHERE organization_id = @o AND id = @b
                      FOR UPDATE"
                buildLock.Parameters.AddWithValue("o", org.Value) |> ignore
                buildLock.Parameters.AddWithValue("b", lockedBuildId) |> ignore

                match buildLock.ExecuteScalar() with
                | :? Guid as lockedId -> lockedId = build.Value
                | _ -> false

    member private _.LogChunkExists
        (conn: NpgsqlConnection, tx: NpgsqlTransaction, org: OrganizationId, attempt: AttemptId, sequence: int)
        : bool =
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT EXISTS (
                 SELECT 1 FROM log_chunks
                  WHERE organization_id = @o AND attempt_id = @a AND sequence = @s
             )"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("s", sequence) |> ignore
        cmd.ExecuteScalar() :?> bool

    /// FG-064a. Canonical tenant lineage is locked before publication. The
    /// caller's sequence is attempt-local for replay idempotency; the UPDATE
    /// atomically allocates the public, build-wide cursor even when multiple
    /// attempts publish concurrently.
    member this.AppendLog(org: OrganizationId, build: BuildId, attempt: AttemptId, sequence: int, body: string) : bool =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        let appended =
            if not (this.LockLogLineage(conn, tx, org, build, attempt)) then
                false
            elif this.LogChunkExists(conn, tx, org, attempt, sequence) then
                false
            else
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <-
                    "WITH allocation AS (
                         UPDATE builds b
                            SET next_log_sequence = GREATEST(b.next_log_sequence, @s) + 1
                          WHERE b.organization_id = @o AND b.id = @b
                         RETURNING b.next_log_sequence - 1 AS build_sequence
                     )
                     INSERT INTO log_chunks
                            (organization_id, build_id, attempt_id, sequence, build_sequence, body)
                     SELECT @o, @b, @a, @s, allocation.build_sequence, @body
                       FROM allocation"
                cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                cmd.Parameters.AddWithValue("b", build.Value) |> ignore
                cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
                cmd.Parameters.AddWithValue("s", sequence) |> ignore
                cmd.Parameters.AddWithValue("body", body) |> ignore
                cmd.ExecuteNonQuery() = 1

        tx.Commit()
        appended

    /// The local supervisor, not the untrusted child, writes public log chunks.
    /// After the canonical lineage locks are held, authority is re-checked in
    /// the allocation statement so a stale supervisor cannot consume a cursor
    /// or append after its lease or fence has been replaced.
    member this.AppendLogFenced
        (org: OrganizationId,
         build: BuildId,
         attempt: AttemptId,
         fence: Fence,
         owner: string,
         sequence: int,
         body: string)
        : bool =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        let appended =
            if not (this.LockLogLineage(conn, tx, org, build, attempt)) then
                false
            elif this.LogChunkExists(conn, tx, org, attempt, sequence) then
                false
            else
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <-
                    "WITH allocation AS (
                         UPDATE builds b
                            SET next_log_sequence = GREATEST(b.next_log_sequence, @s) + 1
                          WHERE b.organization_id = @o AND b.id = @b
                            AND EXISTS (
                                SELECT 1
                                  FROM attempts a
                                 WHERE a.organization_id = @o AND a.id = @a
                                   AND a.fence = @f AND a.lease_owner = @owner
                                   AND a.lease_expires_at > clock_timestamp()
                                   AND a.state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
                                   AND a.restore_epoch =
                                       (SELECT restore_epoch FROM controller_metadata WHERE singleton)
                            )
                         RETURNING b.next_log_sequence - 1 AS build_sequence
                     )
                     INSERT INTO log_chunks
                            (organization_id, build_id, attempt_id, sequence, build_sequence, body)
                     SELECT @o, @b, @a, @s, allocation.build_sequence, @body
                       FROM allocation"
                cmd.Parameters.AddWithValue("o", org.Value) |> ignore
                cmd.Parameters.AddWithValue("b", build.Value) |> ignore
                cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
                cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
                cmd.Parameters.AddWithValue("owner", owner) |> ignore
                cmd.Parameters.AddWithValue("s", sequence) |> ignore
                cmd.Parameters.AddWithValue("body", body) |> ignore
                cmd.ExecuteNonQuery() = 1

        tx.Commit()
        appended

    member _.NextLogSequence(org: OrganizationId, attempt: AttemptId) : int =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT COALESCE(MAX(sequence), -1) + 1
               FROM log_chunks
              WHERE organization_id = @o AND attempt_id = @a"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        let value = cmd.ExecuteScalar() :?> int
        tx.Commit()
        value

    /// FG-060a/FG-064. The build lineage and progressive read are one query.
    /// Some [] therefore means a real build with no chunks at this offset,
    /// while None means that org/project/build lineage does not exist.
    member _.ReadLogPage
        (org: OrganizationId, project: ProjectId, build: BuildId, fromSequence: int, limit: int)
        : (int * string) list option =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT l.build_sequence, l.body
               FROM builds b
               LEFT JOIN log_chunks l
                 ON l.organization_id = b.organization_id
                AND l.build_id = b.id
                AND l.build_sequence >= @s
              WHERE b.organization_id = @o
                AND b.project_id = @p
                AND b.id = @b
              ORDER BY l.build_sequence
              LIMIT @limit"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        cmd.Parameters.AddWithValue("s", fromSequence) |> ignore
        cmd.Parameters.AddWithValue("limit", max 1 limit) |> ignore

        use r = cmd.ExecuteReader()
        let mutable lineageExists = false

        let chunks =
            [ while r.Read() do
                  lineageExists <- true

                  if not (r.IsDBNull 0) then
                      yield r.GetInt32 0, r.GetString 1 ]

        r.Close()
        let result = if lineageExists then Some chunks else None
        tx.Commit()
        result

    member this.ReadLog(org: OrganizationId, project: ProjectId, build: BuildId, fromSequence: int) =
        this.ReadLogPage(org, project, build, fromSequence, Int32.MaxValue)

    /// Cancellation is IDEMPOTENT by design. A retried request — after a client
    /// timeout, say — must not look like an error: the caller's intent is already
    /// satisfied. What genuinely is a conflict is asking to cancel a build that
    /// has already finished, or one that does not exist. The build row lock is
    /// also PublishTerminal's arbitration point: whichever transaction acquires
    /// it first determines whether cancellation or the prior terminal result wins.
    member _.RequestCancellation(org: OrganizationId, project: ProjectId, build: BuildId) : CancellationOutcome =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT status, cancellation_requested FROM builds
              WHERE organization_id = @o AND project_id = @p AND id = @b
              FOR UPDATE"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore

        let existing =
            use r = cmd.ExecuteReader()
            if r.Read() then Some(r.GetString 0, r.GetBoolean 1) else None

        match existing with
        | None -> NoSuchBuild
        | Some(status, _) when
            status = "success"
            || status = "unstable"
            || status = "failure"
            || status = "aborted"
            // Read compatibility for pre-FG-224 fixtures/rows.
            || status = "succeeded"
            || status = "failed"
            ->
            AlreadyTerminal status
        | Some(_, true) -> AlreadyRequested
        | Some _ ->
            use upd = conn.CreateCommand()
            upd.Transaction <- tx
            upd.CommandText <-
                "UPDATE builds SET cancellation_requested = true
                  WHERE organization_id = @o AND project_id = @p AND id = @b
                    AND status NOT IN ('success', 'unstable', 'failure', 'aborted', 'succeeded', 'failed')"
            upd.Parameters.AddWithValue("o", org.Value) |> ignore
            upd.Parameters.AddWithValue("p", project.Value) |> ignore
            upd.Parameters.AddWithValue("b", build.Value) |> ignore
            if upd.ExecuteNonQuery() <> 1 then
                failwith "cancellation target changed after row arbitration"
            CancellationAccepted
        |> fun outcome ->
            tx.Commit()
            outcome

    member _.BuildSnapshot(org: OrganizationId, project: ProjectId, build: BuildId) : (string * bool) option =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT status, cancellation_requested FROM builds
              WHERE organization_id = @o AND project_id = @p AND id = @b"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        use r = cmd.ExecuteReader()
        let snapshot = if r.Read() then Some(r.GetString 0, r.GetBoolean 1) else None
        r.Close()
        tx.Commit()
        snapshot

    /// Resolve an attempt through its tenant/project/build lineage. Attempt
    /// terminality is immutable even when a later retry reopens the build.
    member _.ArtifactAttemptSnapshot
        (org: OrganizationId, project: ProjectId, build: BuildId, attempt: AttemptId)
        : string option =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <-
            "SELECT a.state
               FROM builds b
               JOIN nodes n
                 ON n.organization_id = b.organization_id
                AND n.build_id = b.id
               JOIN attempts a
                 ON a.organization_id = n.organization_id
                AND a.node_id = n.id
              WHERE b.organization_id = @o
                AND b.project_id = @p
                AND b.id = @b
                AND a.id = @a"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        use reader = cmd.ExecuteReader()
        let snapshot = if reader.Read() then Some(reader.GetString 0) else None
        reader.Close()
        tx.Commit()
        snapshot

    /// Adopt the pre-FG-042b build-keyed directory only for the current
    /// terminal leaf. Hold the canonical attempt -> node -> build locks across
    /// the filesystem callback so DecideRetry cannot reopen the lineage in the
    /// eligibility-check/move gap.
    member _.MigrateLegacyArtifactSnapshot
        (org: OrganizationId,
         project: ProjectId,
         build: BuildId,
         attempt: AttemptId,
         migration: unit -> Result<unit, string>)
        : Result<bool, string> =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org

        let rollback error =
            try tx.Rollback() with _ -> ()
            Error error

        let notEligible () =
            tx.Commit()
            Ok false

        try
            use attemptCmd = conn.CreateCommand()
            attemptCmd.Transaction <- tx
            attemptCmd.CommandText <-
                "SELECT node_id, state
                   FROM attempts
                  WHERE organization_id = @o AND id = @a
                  FOR UPDATE"
            attemptCmd.Parameters.AddWithValue("o", org.Value) |> ignore
            attemptCmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
            use attemptReader = attemptCmd.ExecuteReader()

            if not (attemptReader.Read()) then
                attemptReader.Close()
                notEligible ()
            else
                let nodeId = attemptReader.GetGuid 0
                let attemptState = attemptReader.GetString 1
                attemptReader.Close()

                use nodeCmd = conn.CreateCommand()
                nodeCmd.Transaction <- tx
                nodeCmd.CommandText <-
                    "SELECT build_id
                       FROM nodes
                      WHERE organization_id = @o AND id = @n
                      FOR UPDATE"
                nodeCmd.Parameters.AddWithValue("o", org.Value) |> ignore
                nodeCmd.Parameters.AddWithValue("n", nodeId) |> ignore
                let actualBuild = nodeCmd.ExecuteScalar()

                if attemptState <> "terminal" || actualBuild <> box build.Value then
                    notEligible ()
                else
                    use buildCmd = conn.CreateCommand()
                    buildCmd.Transaction <- tx
                    buildCmd.CommandText <-
                        "SELECT project_id, status
                           FROM builds
                          WHERE organization_id = @o AND id = @b
                          FOR UPDATE"
                    buildCmd.Parameters.AddWithValue("o", org.Value) |> ignore
                    buildCmd.Parameters.AddWithValue("b", build.Value) |> ignore
                    use buildReader = buildCmd.ExecuteReader()

                    if not (buildReader.Read()) then
                        buildReader.Close()
                        notEligible ()
                    else
                        let actualProject = buildReader.GetGuid 0
                        let buildStatus = buildReader.GetString 1
                        buildReader.Close()
                        let terminalBuild =
                            buildStatus = "success"
                            || buildStatus = "unstable"
                            || buildStatus = "failure"
                            || buildStatus = "aborted"
                            || buildStatus = "succeeded"
                            || buildStatus = "failed"

                        if actualProject <> project.Value || not terminalBuild then
                            notEligible ()
                        else
                            use childCmd = conn.CreateCommand()
                            childCmd.Transaction <- tx
                            childCmd.CommandText <-
                                "SELECT EXISTS (
                                     SELECT 1
                                       FROM attempts
                                      WHERE organization_id = @o AND retry_of = @a)"
                            childCmd.Parameters.AddWithValue("o", org.Value) |> ignore
                            childCmd.Parameters.AddWithValue("a", attempt.Value) |> ignore

                            if childCmd.ExecuteScalar() :?> bool then
                                notEligible ()
                            else
                                match migration () with
                                | Ok () ->
                                    tx.Commit()
                                    Ok true
                                | Error error -> rollback error
        with ex ->
            rollback ex.Message

    member _.CountOutbox(org: OrganizationId) : int =
        use conn = openConn ()
        use tx = beginTenantTransaction conn org
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- "SELECT count(*) FROM outbox WHERE organization_id = @o"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        let count = cmd.ExecuteScalar() :?> int64 |> int
        tx.Commit()
        count
