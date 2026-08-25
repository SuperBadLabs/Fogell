namespace Fogell.Store

open System
open System.Security.Cryptography
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
      StageNames: string list
      RequiredTrustPool: string
      RequiredCapabilities: string list }

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
type Store(connectionString: string) =

    let retryDeadLetterReason = "attempt budget exhausted"

    let openConn () =
        let c = new NpgsqlConnection(connectionString)
        c.Open()
        c

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

    member _.Migrate() = Migrations.run connectionString

    member _.CreateProject(org: OrganizationId, orgSlug: string, project: ProjectId, projectSlug: string) =
        use conn = openConn ()
        use tx = conn.BeginTransaction()

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
        use tx = conn.BeginTransaction(System.Data.IsolationLevel.ReadCommitted)

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
            use cmd = conn.CreateCommand()
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

            match failure with
            | Some error -> Error error
            | None -> Ok(List.ofSeq values)
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
            use tx = conn.BeginTransaction()

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
            use tx = conn.BeginTransaction()

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
            use tx = conn.BeginTransaction(System.Data.IsolationLevel.RepeatableRead)

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
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            $"SELECT {effectProjection}
              FROM effect_checkpoints
              WHERE organization_id = @o AND state = 'uncertain'
              ORDER BY prepared_at, attempt_id, effect_key"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do yield readCheckpoint reader ]

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
        else

        use conn = openConn ()
        use tx = conn.BeginTransaction()

        try
            // Has this key already been admitted? Read inside the transaction so
            // the unique constraint is the arbiter under concurrency.
            use existing = conn.CreateCommand()
            existing.Transaction <- tx
            existing.CommandText <-
                "SELECT b.id, b.number, n.id, a.id
                 FROM builds b
                 JOIN nodes n    ON n.build_id = b.id AND n.organization_id = b.organization_id AND n.ordinal = 0
                 JOIN attempts a ON a.node_id = n.id  AND a.organization_id = n.organization_id AND a.ordinal = 0
                 WHERE b.organization_id = @o AND b.project_id = @p AND b.idempotency_key = @k"
            existing.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
            existing.Parameters.AddWithValue("p", input.ProjectId.Value) |> ignore
            existing.Parameters.AddWithValue("k", input.IdempotencyKey) |> ignore

            use reader = existing.ExecuteReader()

            if reader.Read() then
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

                let buildId = Guid.NewGuid()
                let epoch =
                    use e = conn.CreateCommand()
                    e.Transaction <- tx
                    e.CommandText <- "SELECT restore_epoch FROM controller_metadata WHERE singleton"
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

                // one node per declared stage; the first attempt of the first
                // node is created eagerly so the scheduler has something to offer
                let nodeIds =
                    input.StageNames
                    |> List.mapi (fun i name ->
                        let nodeId = Guid.NewGuid()

                        use n = conn.CreateCommand()
                        n.Transaction <- tx
                        n.CommandText <-
                            "INSERT INTO nodes
                                 (id, organization_id, build_id, name, ordinal, required_trust_pool,
                                  required_capabilities, status)
                             VALUES (@id, @o, @b, @name, @ord, @pool, @caps, 'queued')"
                        n.Parameters.AddWithValue("id", nodeId) |> ignore
                        n.Parameters.AddWithValue("o", input.OrganizationId.Value) |> ignore
                        n.Parameters.AddWithValue("b", buildId) |> ignore
                        n.Parameters.AddWithValue("name", name) |> ignore
                        n.Parameters.AddWithValue("ord", i) |> ignore
                        n.Parameters.AddWithValue("pool", input.RequiredTrustPool) |> ignore
                        n.Parameters.AddWithValue("caps", List.toArray input.RequiredCapabilities) |> ignore
                        n.ExecuteNonQuery() |> ignore
                        nodeId)

                let firstNode = List.head nodeIds
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
        use cmd = conn.CreateCommand()
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

        match cmd.ExecuteScalar() with
        | null -> Error "attempt is not offerable"
        | v -> Ok(Fence(v :?> int64))

    member _.AcceptAttempt(org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string) : bool =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "UPDATE attempts SET state = 'running'
              WHERE organization_id = @o AND id = @a AND fence = @f AND lease_owner = @owner
                AND state = 'offered' AND lease_expires_at > clock_timestamp()"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
        cmd.Parameters.AddWithValue("owner", owner) |> ignore
        cmd.ExecuteNonQuery() = 1

    /// FG-022. Publish a terminal result. Admissible only from the exact current
    /// fence, the exact lease owner, the current restore epoch, an unexpired
    /// lease and an active state — all seven conditions from ADR 0007, in one
    /// statement so there is no read-then-write race.
    ///
    /// `clock_timestamp()` deliberately, not `now()`: `now()` is transaction
    /// start time, so inside a long transaction an expired lease would still
    /// read as valid.
    member _.PublishTerminal
        (org: OrganizationId, attempt: AttemptId, fence: Fence, owner: string, result: BuildStatus)
        : Result<unit, string> =
        use conn = openConn ()
        use tx = conn.BeginTransaction()

        try
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <-
                "UPDATE attempts a
                    SET state = 'terminal', result = @r, lease_owner = NULL, lease_expires_at = NULL
                  WHERE a.organization_id = @o
                    AND a.id = @a
                    AND a.fence = @f
                    AND a.lease_owner = @owner
                    AND a.lease_expires_at > clock_timestamp()
                    AND a.state IN ('offered', 'accepted', 'running', 'finalizing', 'cancelling')
                    AND a.restore_epoch = (SELECT restore_epoch FROM controller_metadata WHERE singleton)
                  RETURNING a.node_id"
            cmd.Parameters.AddWithValue("o", org.Value) |> ignore
            cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
            cmd.Parameters.AddWithValue("f", fence.Value) |> ignore
            cmd.Parameters.AddWithValue("owner", owner) |> ignore
            cmd.Parameters.AddWithValue("r", BuildStatus.toWireString result) |> ignore

            match cmd.ExecuteScalar() with
            | null ->
                tx.Rollback()
                Error "publication refused: stale fence, wrong owner, expired lease, pre-restore epoch, or already terminal"
            | nodeId ->
                use ev = conn.CreateCommand()
                ev.Transaction <- tx
                ev.CommandText <-
                    "INSERT INTO events (organization_id, build_id, attempt_id, kind, payload)
                     SELECT @o, n.build_id, @a, 'attempt.terminal', @pl
                     FROM nodes n WHERE n.id = @n AND n.organization_id = @o"
                ev.Parameters.AddWithValue("o", org.Value) |> ignore
                ev.Parameters.AddWithValue("a", attempt.Value) |> ignore
                ev.Parameters.AddWithValue("n", nodeId :?> Guid) |> ignore
                ev.Parameters.Add(
                    NpgsqlParameter("pl", NpgsqlTypes.NpgsqlDbType.Jsonb,
                                    Value = $"{{\"result\":\"{BuildStatus.toWireString result}\"}}")) |> ignore
                ev.ExecuteNonQuery() |> ignore

                tx.Commit()
                Ok()
        with ex ->
            (try tx.Rollback() with _ -> ())
            Error ex.Message

    /// A restore invalidates every lease issued before it. Pre-restore agents
    /// can no longer renew or publish; their attempts require reconciliation.
    member _.ActivateRestore() : RestoreEpoch =
        use conn = openConn ()
        use tx = conn.BeginTransaction()

        use bump = conn.CreateCommand()
        bump.Transaction <- tx
        bump.CommandText <-
            "UPDATE controller_metadata SET restore_epoch = restore_epoch + 1
              WHERE singleton RETURNING restore_epoch"
        let epoch = bump.ExecuteScalar() :?> int64

        use invalidate = conn.CreateCommand()
        invalidate.Transaction <- tx
        invalidate.CommandText <-
            "UPDATE attempts
                SET state = 'reconciliation_required', lease_owner = NULL, lease_expires_at = NULL
              WHERE restore_epoch < @e
                AND state IN ('queued', 'offered', 'accepted', 'running', 'finalizing', 'cancelling')"
        invalidate.Parameters.AddWithValue("e", epoch) |> ignore
        invalidate.ExecuteNonQuery() |> ignore

        tx.Commit()
        RestoreEpoch epoch

    member _.AttemptState(org: OrganizationId, attempt: AttemptId) : (string * int64 * string option) option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT state, fence, result FROM attempts WHERE organization_id = @o AND id = @a"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore

        use r = cmd.ExecuteReader()

        if r.Read() then
            Some(r.GetString 0, r.GetInt64 1, (if r.IsDBNull 2 then None else Some(r.GetString 2)))
        else
            None

    member _.CountEvents(org: OrganizationId, build: BuildId, kind: string) : int =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT count(*) FROM events WHERE organization_id = @o AND build_id = @b AND kind = @k"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        cmd.Parameters.AddWithValue("k", kind) |> ignore
        cmd.ExecuteScalar() :?> int64 |> int

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
        use tx = conn.BeginTransaction()

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

    /// FG-061 wait diagnostics. Distinguishes an EMPTY queue from a concrete
    /// capability mismatch, and names the missing capabilities — Jenkins' own
    /// "There are no nodes with the label X" is the behaviour worth matching
    /// (JB-AGT-004), and it is far better than an unexplained wait.
    member _.ExplainWait(org: OrganizationId, trustPool: string, capabilities: string list) : string =
        use conn = openConn ()

        use cmd = conn.CreateCommand()
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

    /// FG-064a. The append and its attempt -> node -> build ownership check are
    /// one statement. IDs are tenant-composite, so the attempt predicate and
    /// node join both carry the tenant.
    member _.AppendLog(org: OrganizationId, build: BuildId, attempt: AttemptId, sequence: int, body: string) : bool =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "INSERT INTO log_chunks (organization_id, build_id, attempt_id, sequence, body)
             SELECT a.organization_id, n.build_id, a.id, @s, @body
             FROM attempts a
             JOIN nodes n ON n.organization_id = a.organization_id AND n.id = a.node_id
             WHERE a.organization_id = @o AND a.id = @a AND n.build_id = @b
             ON CONFLICT (organization_id, attempt_id, sequence) DO NOTHING"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        cmd.Parameters.AddWithValue("a", attempt.Value) |> ignore
        cmd.Parameters.AddWithValue("s", sequence) |> ignore
        cmd.Parameters.AddWithValue("body", body) |> ignore
        cmd.ExecuteNonQuery() = 1

    /// FG-060a/FG-064. The build lineage and progressive read are one query.
    /// Some [] therefore means a real build with no chunks at this offset,
    /// while None means that org/project/build lineage does not exist.
    member _.ReadLog(org: OrganizationId, project: ProjectId, build: BuildId, fromSequence: int) : (int * string) list option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT l.sequence, l.body
               FROM builds b
               LEFT JOIN log_chunks l
                 ON l.organization_id = b.organization_id
                AND l.build_id = b.id
                AND l.sequence >= @s
              WHERE b.organization_id = @o
                AND b.project_id = @p
                AND b.id = @b
              ORDER BY l.sequence"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        cmd.Parameters.AddWithValue("s", fromSequence) |> ignore

        use r = cmd.ExecuteReader()
        let mutable lineageExists = false

        let chunks =
            [ while r.Read() do
                  lineageExists <- true

                  if not (r.IsDBNull 0) then
                      yield r.GetInt32 0, r.GetString 1 ]

        if lineageExists then Some chunks else None

    /// Cancellation is IDEMPOTENT by design. A retried request — after a client
    /// timeout, say — must not look like an error: the caller's intent is already
    /// satisfied. What genuinely is a conflict is asking to cancel a build that
    /// has already finished, or one that does not exist.
    member _.RequestCancellation(org: OrganizationId, project: ProjectId, build: BuildId) : CancellationOutcome =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT status, cancellation_requested FROM builds
              WHERE organization_id = @o AND project_id = @p AND id = @b"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore

        let existing =
            use r = cmd.ExecuteReader()
            if r.Read() then Some(r.GetString 0, r.GetBoolean 1) else None

        match existing with
        | None -> NoSuchBuild
        | Some(status, _) when status = "succeeded" || status = "failed" || status = "aborted" ->
            AlreadyTerminal status
        | Some(_, true) -> AlreadyRequested
        | Some _ ->
            use upd = conn.CreateCommand()
            upd.CommandText <-
                "UPDATE builds SET cancellation_requested = true
                  WHERE organization_id = @o AND project_id = @p AND id = @b"
            upd.Parameters.AddWithValue("o", org.Value) |> ignore
            upd.Parameters.AddWithValue("p", project.Value) |> ignore
            upd.Parameters.AddWithValue("b", build.Value) |> ignore
            upd.ExecuteNonQuery() |> ignore
            CancellationAccepted

    member _.BuildSnapshot(org: OrganizationId, project: ProjectId, build: BuildId) : (string * bool) option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT status, cancellation_requested FROM builds
              WHERE organization_id = @o AND project_id = @p AND id = @b"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("p", project.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        use r = cmd.ExecuteReader()
        if r.Read() then Some(r.GetString 0, r.GetBoolean 1) else None

    member _.CountOutbox(org: OrganizationId) : int =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT count(*) FROM outbox WHERE organization_id = @o"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.ExecuteScalar() :?> int64 |> int
