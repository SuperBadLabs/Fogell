# Read-only operator checks

`scripts/qualification/operator-check.py` emits one compact JSON snapshot for
database reconciliation and held-retention counts, database size, filesystem
byte/inode headroom, and paired-backup evidence. It is read-only. Exit status is
0 when checks pass, 1 when one or more conditions need attention, and 2 for an
invalid invocation. Database access errors are emitted as an alert and return 1.

Configure `PGSERVICE` (recommended) or the normal libpq `PG*` variables for a
database role that can only `SELECT` from `attempts` and `build_retention` and
read `pg_database_size`. The Fogell table counts are scoped by row-level
security to `--organization`; run once per organization in a multi-tenant
deployment. Keep credentials in the service file/password store, not command
arguments. Inspect the state root, workspace pool and PostgreSQL
volume separately where they use different filesystems:

```sh
scripts/qualification/operator-check.py \
  --organization 00000000-0000-0000-0000-000000000001 \
  --filesystem /srv/fogell/state \
  --filesystem /srv/fogell/state/workspaces \
  --filesystem /var/lib/postgresql \
  --backup-dir /srv/fogell/backup-evidence \
  --min-free-bytes 10737418240 \
  --min-free-inodes 100000 \
  --max-database-bytes 107374182400 \
  --max-backup-age-hours 24
```

Grant the check role `SELECT` on `attempts` and `build_retention`; the normal
organization RLS policy continues to apply. Thresholds above are examples
only. Measure actual mount capacity, growth,
retention cadence and recovery objectives on the pilot host before choosing
floors. Database size is a soft threshold: it does not measure free database
volume capacity. Use `--filesystem` for that backing volume too. A mount path
reports the capacity of the filesystem containing it; verify mount identity
independently during host provisioning.

Backup evidence follows `scripts/paired-backup.py`: each immediate child
directory of `--backup-dir` is treated as a recovery point expected to contain
`manifest.json`, `database.custom`, and `state.tar`. The check invokes the helper's read-only
`check` command for each point, verifies recorded hashes and the state
inventory, and measures age from the manifest's `created_at`. Invalid or
incomplete points trigger an alert. A failed create attempt removes its partial
point and leaves an adjacent `*.failed.json` record with only its UTC timestamp,
output name and exception class. A failed-attempt alert clears after a newer
verified point is present; the failure record remains as history. Malformed
failure records remain unresolved. Keep evidence directories and sidecars until
their retention policy permits removal. A passing hash and inventory check
does not prove a database restore or application recovery; perform the
documented restore drill separately.

Each alert includes a stable code, observed value, threshold and operator
action. Pipe stdout JSON to the host's existing scheduler/monitoring collector;
nonzero exit status signals attention. Re-running after resolution emits no
alert for cleared conditions. The script itself does not deliver notifications
or deduplicate notifications across runs. Collector wiring, one-notification
behavior, threshold calibration, and safe induced alert/clear exercises remain
staging work.

Reconciliation and held-retention conditions require human review. Follow the
[recovery](recovery.md), [retention](retention.md), and
[storage-pool](storage-pool.md) runbooks; do not automatically clear durable
state or delete protected evidence in response to an alert.
