# Self-hosted feedback pilot, profile 1

This profile exercises Fogell's real Domain library and its complete test
executable through Controller.Host. It is a fast development workload. It does
not replace `scripts/build-and-test.sh` or certify unattended production use.

## Declared campaign

The campaign has one cache condition: fresh source/workspace/build outputs on
every submission, with a prepopulated, locked NuGet package cache. It makes no
claim about a cold dependency download or operating-system page cache. One local
trusted Linux worker executes submissions sequentially. The controller and
runner binaries remain fixed throughout measurement.

Thirty correction loops each submit a snapshot containing a deliberately
incorrect expectation in the existing Domain wire-round-trip test, then submit
the original source. Both snapshots run all Domain tests. The planted submission
must produce the named structured test diagnostic and an unstable result; the
corrected submission must pass. JUnit XML, test output, SDK version and exit code
are archived and checked against their expected contents. All sixty attempts,
including failures and censored observations, remain in the campaign denominator.
Each loop binds its two snapshots to the same parent loop ID.

Before measurement, the harness records hardware, SDK/PostgreSQL versions,
source/binary hashes, file inventory, test count, controller limits, package
cache identity and timing criteria in `declaration.json`. Each build has a
120-second deadline and the campaign a 90-minute ceiling. Acceptance requires
30 complete loops, no unexpected terminal results or missing artifacts, and
p95 correction-loop duration below 240 seconds. Latencies use the harness's
monotonic clock from submission, including client startup; admission, first
output, first actionable diagnostic and terminal observations are separate.
No missing or timed-out observation becomes a zero or a success percentile.

Retention is exercised every five loops, with an explicit count/age/size policy
and a finite sweep budget. The receipt records physical/logical usage before and
after, expired evidence, and preserved active/reconciliation records. The
campaign cannot pass without the retention acceptance result.

## Recovery objectives and procedure

The disposable drill's objectives are zero lost records/files from the quiesced
backup boundary and recovery within 120 seconds, measured through a successful
control submission. These are acceptance targets, not an unmeasured service SLO.

1. Stop the controller and confirm its worker processes are gone. Record the
   database migration ledger and a canonical inventory of regular state files.
   The inventory rejects symbolic links, sockets and FIFOs. If a stopped .NET
   process left diagnostic endpoints in the private temporary directory, first
   verify its recorded PID/start identity is no longer alive and that all writers
   are quiesced. Record each endpoint's exact path, file type and ownership, then
   remove only those verified stale volatile endpoints and retain the cleanup
   receipt with any failed preflight. Do not recursively clear temporary or state
   directories, remove regular files, or delete endpoints belonging to a live or
   unidentified process. Rerun the complete inventory after this narrow cleanup.
2. Take a PostgreSQL custom archive and copy the matching state tree while all
   writers remain stopped. Hash both and verify their archive inventories.
3. Restore into a new empty database and a new private state root. Compare every
   database row/sequence and state-file hash before advancing authority.
4. Set `FOGELL_MAINTENANCE_DATABASE_URL` to the restored database, then run:

   ```bash
   dotnet tools/Fogell.Recovery/bin/Release/net10.0/Fogell.Recovery.dll \
     activate-restore --writers-quiesced
   ```

5. Verify the restore epoch advanced and the pre-backup authority cannot append
   logs or publish a terminal result. Restart against the restored pair; verify
   previous artifacts and run a fresh snapshot control to completion.
6. For an interrupted upgrade, terminate the private migration session before
   commit, verify neither schema nor ledger partially committed, then run the
   checksummed forward migrations and restore-based rollback rehearsal.

Keep the original pair for investigation until all comparisons pass. The drill
must reject altered archive bytes, missing/changed state files, a mismatched
pair and stale publication. It never restores over an existing deployment.

The pilot also tests an API cancellation and a controller restart. Missing
terminal evidence requires reconciliation; it must never trigger automatic
replay of uncertain effects. Run receipts state whether the bounded storage
pool was enabled; application accounting alone does not enforce a disk quota.
