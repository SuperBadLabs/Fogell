namespace Fogell.Retention

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Npgsql

type Policy =
    { KeepBuilds: int; MaxBytes: int64; MaxAgeSeconds: int64
      MaxCandidates: int; MaxEntries: int; MaxOperations: int; BudgetSeconds: int }

type SweepResult =
    { Selected: int; Expired: int; Operations: int; Pending: int; Held: int
      ReclaimableBytesBefore: int64; SelectedLogicalBytes: int64; ElapsedMs: int64 }

module Retention =
    let private command (connection: NpgsqlConnection) sql (values: (string*obj) list) =
        let cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.CommandTimeout <- 5
        for (name,value: obj) in values do cmd.Parameters.AddWithValue(name,value) |> ignore
        cmd

    let private execute connection sql values =
        use cmd =
            command connection sql values
        cmd.ExecuteNonQuery()

    let private scalar connection sql values =
        use cmd =
            command connection sql values
        cmd.ExecuteScalar()

    let private transaction (connection: NpgsqlConnection) org operation =
        use tx = connection.BeginTransaction()
        execute connection "SELECT set_config('fogell.organization_id',@org,true); SET LOCAL lock_timeout='2s'; SET LOCAL statement_timeout='5s'" ["org",box(string org)] |> ignore
        let result = operation()
        tx.Commit()
        result

    let private eligibility = "
        b.status IN ('success','succeeded','failure','failed','unstable','aborted')
        AND EXISTS(SELECT 1 FROM attempts a JOIN nodes n ON n.id=a.node_id AND n.organization_id=a.organization_id
          WHERE n.build_id=b.id AND n.organization_id=b.organization_id)
        AND NOT EXISTS(SELECT 1 FROM attempts a JOIN nodes n ON n.id=a.node_id AND n.organization_id=a.organization_id
          WHERE n.build_id=b.id AND n.organization_id=b.organization_id AND
           (a.state<>'terminal' OR a.retry_of IS NOT NULL OR a.lease_owner IS NOT NULL OR a.result IS NULL
            OR EXISTS(SELECT 1 FROM attempts child WHERE child.retry_of=a.id AND child.organization_id=a.organization_id)
            OR EXISTS(SELECT 1 FROM retry_decisions d WHERE d.organization_id=a.organization_id AND d.parent_attempt_id=a.id)
            OR EXISTS(SELECT 1 FROM effect_checkpoints e WHERE e.organization_id=a.organization_id
              AND e.attempt_id=a.id AND e.state<>'confirmed')))"

    let private paths (org: Guid) (build: Guid) (number: int) (attempts: (Guid*int64) array) =
        let o,b = org.ToString("N"),build.ToString("N")
        let number=number.ToString(Globalization.CultureInfo.InvariantCulture)
        let home=Text.Encoding.UTF8.GetBytes(b+"\u0000"+number) |> Security.Cryptography.SHA256.HashData |> Convert.ToHexStringLower
        [| yield $"workspaces/{o}/{b}"
           yield $"workspaces/{o}/_agent_home/{home}"
           yield $"workspaces/{o}/_artifacts/_stash/{b}#build-{number}"
           yield $"workspaces/{o}/_artifacts/{b}"
           yield $"definitions/{o}/{b}"
           for a,fence in attempts do
               let a = a.ToString("N")
               yield $"workspaces/{o}/_artifact-snapshots/{a}"
               yield $"workspaces/{o}/_runtime/{a}"
               yield $"journals/{o}/attempts/{a}.journal"
               yield $"events/{o}/{a}-{fence}.events"
               yield $"containment/{o}/{a}-{fence}" |]

    let private save connection org build state epoch manifest error =
        execute connection "UPDATE build_retention SET state=@state,manifest=@manifest::jsonb,error=@error,updated_at=clock_timestamp()
          WHERE organization_id=@org AND build_id=@build AND restore_epoch=@epoch" ["org",box org;"build",box build;"state",box state;"epoch",box epoch;
             "manifest",box(JsonSerializer.Serialize manifest);"error",(match error with Some e->box e | None->box DBNull.Value)]
        |> fun count -> if count<>1 then failwith "retention_journal_disagrees"

    let sweep (connectionString: string) root org policy (boundary: string -> unit) =
        if policy.KeepBuilds<0 || policy.MaxBytes<0L || policy.MaxAgeSeconds<0L || policy.MaxCandidates<1 ||
           policy.MaxEntries<1 || policy.MaxOperations<1 || policy.BudgetSeconds<1 then invalidArg "policy" "invalid retention policy"
        let watch = Stopwatch.StartNew()
        let checkBudget() = if watch.Elapsed.TotalSeconds >= float policy.BudgetSeconds then raise(TimeoutException "sweep_budget")
        let builder = NpgsqlConnectionStringBuilder connectionString
        builder.Timeout <- 5
        builder.CommandTimeout <- 5
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        // Advisory session lock covers planning and every filesystem boundary.
        let locked =
            scalar connection "SELECT pg_try_advisory_lock(hashtextextended(@scope,268))"
                         ["scope",box(string org)] :?> bool
        if not locked then failwith "retention_sweep_busy"
        try
            let epoch() =
                scalar connection "SELECT restore_epoch FROM controller_metadata WHERE singleton FOR SHARE" [] |> Convert.ToInt64
            let mutable selected,expired,operations = 0,0,0
            let mutable beforeBytes,selectedBytes = 0L,0L
            // Plan all bounded candidates atomically. A partial inventory never
            // withdraws evidence or becomes permission to delete unknown files.
            transaction connection org (fun () ->
                let currentEpoch = epoch()
                use find =
                    command connection ($"SELECT b.id,
                  COALESCE((SELECT MAX(e.created_at) FROM events e WHERE e.organization_id=b.organization_id AND e.build_id=b.id),b.created_at), b.number
                  FROM builds b WHERE b.organization_id=@org AND {eligibility}
                  AND NOT EXISTS(SELECT 1 FROM build_retention r WHERE r.organization_id=b.organization_id AND r.build_id=b.id)
                  ORDER BY b.created_at DESC,b.id DESC LIMIT @limit FOR UPDATE OF b") ["org",box org;"limit",box(policy.MaxCandidates+1)]
                let candidates = ResizeArray<Guid*DateTime*int>()
                use reader = find.ExecuteReader()
                while reader.Read() do candidates.Add(reader.GetGuid 0,reader.GetDateTime 1,reader.GetInt32 2)
                reader.Close()
                if candidates.Count>policy.MaxCandidates then failwith "candidate_inventory_limit"
                let plans = ResizeArray<Guid*DateTime*Manifest*int64>()
                let mutable entriesRemaining=policy.MaxEntries
                for build,created,number in candidates do
                    checkBudget()
                    use query =
                        command connection "SELECT a.id,a.fence FROM attempts a JOIN nodes n ON n.id=a.node_id
                       AND n.organization_id=a.organization_id WHERE n.organization_id=@org AND n.build_id=@build FOR UPDATE OF a" ["org",box org;"build",box build]
                    let attempts = ResizeArray<Guid*int64>()
                    use rows = query.ExecuteReader()
                    while rows.Read() do attempts.Add(rows.GetGuid 0,rows.GetInt64 1)
                    rows.Close()
                    // Repeat after attempt locks: a concurrent retry/restore may
                    // have changed eligibility while build locks were acquired.
                    let eligible =
                        scalar connection ($"SELECT EXISTS(SELECT 1 FROM builds b WHERE b.organization_id=@org AND b.id=@build AND {eligibility})") ["org",box org;"build",box build] :?> bool
                    if eligible then
                        let manifest = Filesystem.inventory root (paths org build number (attempts.ToArray())) entriesRemaining checkBudget
                        entriesRemaining<-entriesRemaining-manifest.Entries.Length
                        let source=scalar connection "SELECT source_bytes FROM build_definitions WHERE organization_id=@org AND build_id=@build" ["org",box org;"build",box build]
                        if isNull source || source=box DBNull.Value then failwith "definition_database_missing"
                        let source=source :?> byte array
                        let definitionPath= $"definitions/{org:N}/{build:N}/Jenkinsfile"
                        match Fogell.Domain.SourceSnapshot.decode source with
                        | Error _ -> failwith "definition_database_invalid"
                        | Ok None -> Filesystem.verifyDefinition root manifest definitionPath (Security.Cryptography.SHA256.HashData source)
                        | Ok(Some snapshot) ->
                            Filesystem.verifyDefinition root manifest (definitionPath+".snapshot.json") (Security.Cryptography.SHA256.HashData source)
                            Filesystem.verifyDefinition root manifest definitionPath (Convert.FromHexString snapshot.PipelineSha256)
                        let dbBytes =
                            scalar connection "SELECT
                            COALESCE((SELECT sum(octet_length(body)+COALESCE(octet_length(diagnostic::text),0)) FROM log_chunks WHERE organization_id=@org AND build_id=@build),0)
                            +COALESCE((SELECT octet_length(source_bytes) FROM build_definitions WHERE organization_id=@org AND build_id=@build),0)" ["org",box org;"build",box build] |> Convert.ToInt64
                        plans.Add(build,created,manifest,manifest.Bytes+dbBytes)
                beforeBytes <- plans |> Seq.sumBy(fun (_,_,_,bytes)->bytes)
                let mutable retainedBytes,retainedCount = 0L,0
                for build,created,manifest,bytes in plans do
                    let aged = (DateTime.UtcNow-created).TotalSeconds >= float policy.MaxAgeSeconds
                    let discard = aged || retainedCount>=policy.KeepBuilds || bytes>policy.MaxBytes-retainedBytes
                    if discard then
                        execute connection "INSERT INTO build_retention(organization_id,build_id,state,restore_epoch,manifest,source_digest,admission_fingerprint)
                          VALUES(@org,@build,'selected',@epoch,@manifest::jsonb,
                          (SELECT source_digest FROM build_definitions WHERE organization_id=@org AND build_id=@build),
                          (SELECT admission_fingerprint FROM build_definitions WHERE organization_id=@org AND build_id=@build))" ["org",box org;"build",box build;"epoch",box currentEpoch;"manifest",box(JsonSerializer.Serialize manifest)] |> ignore
                        selected<-selected+1
                        selectedBytes<-selectedBytes+bytes
                    else
                        retainedCount<-retainedCount+1
                        retainedBytes<-retainedBytes+bytes)
            boundary "selected"
            let pending = transaction connection org (fun () ->
                use query =
                    command connection "SELECT build_id,restore_epoch,manifest::text FROM build_retention WHERE organization_id=@org
                    AND state IN ('selected','deleting') ORDER BY selected_at,build_id LIMIT @limit" ["org",box org;"limit",box policy.MaxCandidates]
                use reader=query.ExecuteReader()
                let result=ResizeArray<Guid*int64*Manifest>()
                while reader.Read() do result.Add(reader.GetGuid 0,reader.GetInt64 1,JsonSerializer.Deserialize<Manifest>(reader.GetString 2))
                result.ToArray())
            for build,selectedEpoch,initial in pending do
                if operations<policy.MaxOperations && watch.Elapsed.TotalSeconds<float policy.BudgetSeconds then
                    let mutable manifest=initial
                    try
                        transaction connection org (fun () ->
                            if epoch()<>selectedEpoch then failwith "restore_epoch_changed"
                            save connection org build "deleting" selectedEpoch manifest None)
                        boundary "deleting"
                        while manifest.Cursor<manifest.Entries.Length && operations<policy.MaxOperations &&
                              watch.Elapsed.TotalSeconds<float policy.BudgetSeconds do
                            if not manifest.PendingDelete then
                                Filesystem.verifyPresent root manifest
                                manifest<-{manifest with PendingDelete=true}
                                transaction connection org (fun () ->
                                    if epoch()<>selectedEpoch then failwith "restore_epoch_changed"
                                    save connection org build "deleting" selectedEpoch manifest None)
                            boundary "intent"
                            transaction connection org (fun () ->
                                if epoch()<>selectedEpoch then failwith "restore_epoch_changed"
                                Filesystem.removeNext root manifest boundary
                                boundary "unlinked"
                                manifest<-{manifest with Cursor=manifest.Cursor+1;PendingDelete=false}
                                save connection org build "deleting" selectedEpoch manifest None)
                            operations<-operations+1
                        if manifest.Cursor=manifest.Entries.Length && operations<policy.MaxOperations then
                            Filesystem.verifyMissingRoots root manifest
                            boundary "filesystem"
                            let complete = transaction connection org (fun () ->
                                if epoch()<>selectedEpoch then failwith "restore_epoch_changed"
                                Filesystem.verifyMissingRoots root manifest
                                let deleted =
                                    execute connection "DELETE FROM log_chunks WHERE id IN
                                  (SELECT id FROM log_chunks WHERE organization_id=@org AND build_id=@build ORDER BY id LIMIT @limit)" ["org",box org;"build",box build;"limit",box(policy.MaxOperations-operations)]
                                operations<-operations+deleted
                                let remaining =
                                    scalar connection "SELECT EXISTS(SELECT 1 FROM log_chunks WHERE organization_id=@org AND build_id=@build)" ["org",box org;"build",box build] :?> bool
                                if not remaining && operations<policy.MaxOperations then
                                    execute connection "DELETE FROM build_definitions WHERE organization_id=@org AND build_id=@build" ["org",box org;"build",box build] |> ignore
                                    operations<-operations+1
                                    boundary "logs"
                                    Filesystem.verifyMissingRoots root manifest
                                    save connection org build "expired" selectedEpoch manifest None
                                    true
                                else false)
                            if complete then expired<-expired+1
                    with
                    | :? OperationCanceledException -> reraise() // proof cutpoint; resumable, not a hold
                    | error ->
                        let reason=if error.Message |> Seq.forall(fun c->Char.IsAsciiLetterOrDigit c || c='_' || c=':') then error.Message else "retention_operation_failed"
                        transaction connection org (fun () -> save connection org build "held" selectedEpoch manifest (Some reason))
            let pendingCount,held = transaction connection org (fun () ->
                use query=command connection "SELECT count(*) FILTER(WHERE state IN ('selected','deleting')),count(*) FILTER(WHERE state='held')
                  FROM build_retention WHERE organization_id=@org" ["org",box org]
                use reader=query.ExecuteReader()
                reader.Read() |> ignore
                int(reader.GetInt64 0),int(reader.GetInt64 1))
            {Selected=selected;Expired=expired;Operations=operations;Pending=pendingCount;Held=held
             ReclaimableBytesBefore=beforeBytes;SelectedLogicalBytes=selectedBytes;ElapsedMs=watch.ElapsedMilliseconds}
        finally
            scalar connection "SELECT pg_advisory_unlock(hashtextextended(@scope,268))" ["scope",box(string org)] |> ignore
