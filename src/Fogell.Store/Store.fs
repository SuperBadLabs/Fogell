namespace Fogell.Store

open System
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

    /// FG-064. Read the log from a sequence offset, so a client can tail a
    /// running build rather than waiting for completion.
    member _.ReadLog(org: OrganizationId, build: BuildId, fromSequence: int) : (int * string) list =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT sequence, body FROM log_chunks
              WHERE organization_id = @o AND build_id = @b AND sequence >= @s
              ORDER BY sequence"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        cmd.Parameters.AddWithValue("s", fromSequence) |> ignore

        use r = cmd.ExecuteReader()
        [ while r.Read() do
              yield r.GetInt32 0, r.GetString 1 ]

    /// Cancellation is IDEMPOTENT by design. A retried request — after a client
    /// timeout, say — must not look like an error: the caller's intent is already
    /// satisfied. What genuinely is a conflict is asking to cancel a build that
    /// has already finished, or one that does not exist.
    member _.RequestCancellation(org: OrganizationId, build: BuildId) : CancellationOutcome =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT status, cancellation_requested FROM builds
              WHERE organization_id = @o AND id = @b"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
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
                  WHERE organization_id = @o AND id = @b"
            upd.Parameters.AddWithValue("o", org.Value) |> ignore
            upd.Parameters.AddWithValue("b", build.Value) |> ignore
            upd.ExecuteNonQuery() |> ignore
            CancellationAccepted

    member _.BuildSnapshot(org: OrganizationId, build: BuildId) : (string * bool) option =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT status, cancellation_requested FROM builds WHERE organization_id = @o AND id = @b"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.Parameters.AddWithValue("b", build.Value) |> ignore
        use r = cmd.ExecuteReader()
        if r.Read() then Some(r.GetString 0, r.GetBoolean 1) else None

    member _.CountOutbox(org: OrganizationId) : int =
        use conn = openConn ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT count(*) FROM outbox WHERE organization_id = @o"
        cmd.Parameters.AddWithValue("o", org.Value) |> ignore
        cmd.ExecuteScalar() :?> int64 |> int
