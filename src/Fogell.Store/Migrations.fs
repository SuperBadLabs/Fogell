namespace Fogell.Store

open System.Reflection
open Npgsql

/// FG-020 migrations.
///
/// Two properties, both learned from watching engines get this wrong:
///
/// 1. **Advisory lock.** Concurrent controller startup must install each
///    migration exactly once. Without a lock, two controllers booting together
///    both see "not applied" and both apply it.
/// 2. **Version ledger with a checksum.** A migration whose text changed after
///    being applied is a silent divergence between what the schema is and what
///    the repository says it is — the same class of defect as a gate baseline
///    that binds only a binary hash.
module Migrations =

    /// Postgres advisory lock key. Arbitrary but fixed; shared by every
    /// controller so they serialize against each other.
    let private lockKey = 0x464F47454C4CL // "FOGELL"

    let private ledgerDdl =
        "CREATE TABLE IF NOT EXISTS schema_migrations (
             version    text PRIMARY KEY,
             checksum   text NOT NULL,
             applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
         )"

    let private sha256 (text: string) =
        use h = System.Security.Cryptography.SHA256.Create()

        System.Text.Encoding.UTF8.GetBytes text
        |> h.ComputeHash
        |> System.Convert.ToHexString
        |> fun s -> s.ToLowerInvariant()

    /// Migrations embedded at build time, ordered by filename.
    let all () : (string * string) list =
        let asm = Assembly.GetExecutingAssembly()

        asm.GetManifestResourceNames()
        |> Array.filter (fun n -> n.EndsWith ".sql")
        |> Array.sort
        |> Array.map (fun name ->
            use stream = asm.GetManifestResourceStream name
            use reader = new System.IO.StreamReader(stream)
            // resource name looks like Fogell.Store.migrations.0001_controller_truth.sql;
            // the version is the leading numeric prefix so the ledger key is
            // stable even if a migration is renamed for clarity.
            let version =
                name.Split('.')
                |> Array.tryFind (fun part -> part.Length > 0 && System.Char.IsDigit part.[0])
                |> Option.map (fun part -> part.Split('_') |> Array.head)

            (defaultArg version name), reader.ReadToEnd())
        |> Array.toList

    type Applied =
        { Version: string
          AlreadyPresent: bool }

    /// Install every pending migration. Safe to call concurrently.
    let run (connectionString: string) : Result<Applied list, string> =
        try
            use conn = new NpgsqlConnection(connectionString)
            conn.Open()

            use cmd = conn.CreateCommand()
            cmd.CommandText <- ledgerDdl
            cmd.ExecuteNonQuery() |> ignore

            // serialize concurrent controllers; released when the session ends
            use lockCmd = conn.CreateCommand()
            lockCmd.CommandText <- "SELECT pg_advisory_lock(@k)"
            lockCmd.Parameters.AddWithValue("k", lockKey) |> ignore
            lockCmd.ExecuteNonQuery() |> ignore

            try
                let results =
                    all ()
                    |> List.map (fun (version, sql) ->
                        let checksum = sha256 sql

                        use check = conn.CreateCommand()
                        check.CommandText <- "SELECT checksum FROM schema_migrations WHERE version = @v"
                        check.Parameters.AddWithValue("v", version) |> ignore

                        match check.ExecuteScalar() with
                        | null ->
                            use tx = conn.BeginTransaction()

                            use apply = conn.CreateCommand()
                            apply.Transaction <- tx
                            apply.CommandText <- sql
                            apply.ExecuteNonQuery() |> ignore

                            use record = conn.CreateCommand()
                            record.Transaction <- tx
                            record.CommandText <-
                                "INSERT INTO schema_migrations (version, checksum) VALUES (@v, @c)"
                            record.Parameters.AddWithValue("v", version) |> ignore
                            record.Parameters.AddWithValue("c", checksum) |> ignore
                            record.ExecuteNonQuery() |> ignore

                            tx.Commit()
                            { Version = version; AlreadyPresent = false }
                        | existing when string existing <> checksum ->
                            failwith
                                $"migration {version} was applied with checksum {existing} but the \
                                  repository now contains {checksum}; the schema and the source have diverged"
                        | _ -> { Version = version; AlreadyPresent = true })

                Ok results
            finally
                use unlock = conn.CreateCommand()
                unlock.CommandText <- "SELECT pg_advisory_unlock(@k)"
                unlock.Parameters.AddWithValue("k", lockKey) |> ignore
                unlock.ExecuteNonQuery() |> ignore
        with ex ->
            Error ex.Message
