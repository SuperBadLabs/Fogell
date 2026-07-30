namespace Fogell.Store

open System
open Npgsql
open Fogell.Domain

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

/// FG-021/FG-022. The controller's durable truth.
type Store(connectionString: string) =

    let openConn () =
        let c = new NpgsqlConnection(connectionString)
        c.Open()
        c

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

    member _.CountOutbox(org: OrganizationId) : int =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT count(*) FROM outbox WHERE organization_id = @o"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.ExecuteScalar() :?> int64 |> int
