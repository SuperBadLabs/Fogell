# Interrupted upgrade and restore-based rollback

The final interrupted-upgrade harness passed three consecutive real PostgreSQL
16.14 runs: `upgrade-9`, `upgrade-10`, and `upgrade-11`. Each used a separate generated
`fogell_fg269_upgrade_*` database in the campaign-owned Podman container.

The harness installed checksummed migrations 0001–0016, seeded workload data,
and saved a pre-upgrade custom dump. It then applied migration 0017 and its ledger
insert inside one uncommitted transaction. A PostgreSQL sleep barrier plus a
same-session checksum marker established that both had executed. The harness
terminated that exact backend before commit.

After termination, schema, application table data, complete migration ledger
(including audit timestamps), and sequence state matched the pre-upgrade
inventories. Only random pg_dump transport guards and row ordering were normalized.
Each run rejected four deliberately changed inventory components. The actual
compiled `Fogell.Store.Migrations.run`, invoked through `Fogell.Retention migrate`,
then applied migration 0017 successfully; a second invocation changed nothing.
Migration sources and both migrator/Store binary hashes remained unchanged.

The existing `scripts/migration-rollback-drill.sh` separately passed its complete
0016 → 0017 → restore 0016 → 0017 rehearsal. Its canonical pre/rollback hashes
matched, both forward hashes matched, and all four clean-room foreign-key rebuild
phases passed. The exact result is in `upgrade-rollback-v2.log`.

Every successful drill confirmed deletion of its owned database. A final read-only
inventory found no databases under either drill's private namespace. No shared
controller state or database was restored, stopped, or removed.

`upgrade-summary.json` indexes the final receipts. Earlier trials remain visible:
`upgrade-1` failed before database creation because the initial Docker lookup did
not find the Podman container; `upgrade-2` through `upgrade-5` passed earlier checker
revisions before the final complete-ledger comparison was added. Runs `upgrade-6`
through `upgrade-8` and `upgrade-rollback.log` passed the prior migration 0017
candidate; they were repeated after removing its classification-lock regression.
The indexed final receipts bind the revised migration and final Release binaries.

This is database migration interruption and rollback evidence. Paired filesystem/
database restoration and controller execution recovery are separate campaign
requirements and are not established by this drill alone.

Reproduce against an explicitly owned container:

```bash
python3 scripts/prove-interrupted-upgrade.py \
  --runtime podman --container YOUR_OWNED_PG_CONTAINER --port YOUR_PG_PORT \
  --output /tmp/new-unique-upgrade-receipt
```
