# Migration rollback rehearsal

Fogell rolls a failed schema upgrade back by restoring the pre-upgrade custom
archive, not by attempting handwritten destructive `DOWN` SQL. The restored
database is then the source for a later retry of the same forward migration.

The automated rehearsal is intentionally destructive only inside database names
beginning `fogell_fg081_`:

```bash
FOGELL_PG_CONTAINER=<postgres-container> \
  ./scripts/migration-rollback-drill.sh
```

It requires `psql`, `pg_dump`, and `pg_restore` from the same PostgreSQL major as
the server. The tools are taken from the named database container. The role must
be able to create and drop the drill's private databases and apply migrations.

## What the drill does

1. Validates a contiguous migration inventory and pins every migration by
   SHA-256.
2. Creates a private database at N−1 and seeds all durable tables, including a
   live fenced effect checkpoint and an immutable retry decision.
3. Rebuilds N−1 into a clean database. Restoration re-creates every foreign key;
   any orphan makes `pg_restore --single-transaction --exit-on-error` fail.
4. Takes a custom-format rollback archive and records its SHA-256.
5. Applies migration N, checks its expected tenant-composite keys, and performs
   another clean-room rebuild.
6. Rechecks the archive bytes, drops only the private primary database, restores
   N−1, and requires the canonical logical hash to equal the original N−1 hash.
7. Applies N again, clean-room rebuilds it, and requires its canonical logical
   hash to equal the first N state.
8. Removes every private database and scratch file on success, refusal, signal,
   or tool failure.

The logical hash binds canonical clean-room schema, all business-table INSERT
rows, migration version/checksum ledger, and sequence definitions/state. Raw
custom-archive bytes are integrity-checked but are not claimed reproducible.

Run the hostile proof alongside the drill:

```bash
FOGELL_PG_CONTAINER=<postgres-container> \
  ./scripts/prove-migration-rollback-drill.sh
```

It plants archive tampering, rollback data drift, a missing forward FK, and a
skipped second forward. It also installs byte-changing mutants that remove the
archive, rollback, and second-forward comparisons and broaden the private
database namespace. Every candidate fault must refuse, while each mutant must
expose the corresponding false pass.

## Production operator sequence

The automation never points at a production database. For a production upgrade:

1. Quiesce writers and record the deployed commit, schema-migration ledger, and
   PostgreSQL server/client major versions.
2. Create a custom archive with `pg_dump --format=custom`; record its SHA-256 and
   prove `pg_restore --list` can read it. Copy it to the approved backup location.
3. Rehearse restoring that exact archive into a new empty database before
   touching the live database.
4. Apply the migration through Fogell's checksummed migration runner. Validate
   the ledger, constraints, and application smoke tests before reopening writes.
5. If rollback is required, quiesce again and retain the failed-forward database
   for diagnosis. Restore the recorded archive into a new empty database with
   `--single-transaction --exit-on-error`, validate it, then switch the controller
   connection to the restored database. Do not overwrite the failed database.
6. Retry the forward migration only after the failure is understood; rehearse
   against a copy first.

## Boundaries

The drill covers only the newest migration against the immediately preceding
schema, with no concurrent writers. It proves logical equality and FK-clean
restore on one PostgreSQL major. It does not prove downgrade SQL, cross-major
upgrade, physical-page equality, point-in-time recovery, encryption, off-host
retention, production-scale duration, replication/failover, or a live connection
cutover. Those require separate operational tickets and evidence.
