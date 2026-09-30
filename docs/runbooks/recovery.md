# Upgrade and paired recovery

The native authoring release is a breaking change. Existing pipeline bytes are
immutable admission records, so they must not be rewritten in place. Convert
workflows to `pipeline.json`, validate them, and deliberately submit them with new
keys. The native runner refuses unsupported definitions before starting work.

Before upgrade:

1. Stop admission and drain running work. Preserve uncertain attempts and their
   evidence for operator reconciliation. Stop all controller/worker/retention writers.
2. Take a quiesced PostgreSQL custom-format backup and a matching copy of the
   complete state root, preserving regular file contents and metadata. Record
   hashes, schema version, release identity and inventories. Reject unexplained
   special files or incomplete inventories; do not turn missing evidence into a pass.
3. Retain the old immutable release and paired backup. Build/publish the native
   release into a fresh directory so removed assemblies cannot survive deployment.
4. Start with new native definitions. Definition transports now use `pipeline.json`;
   old execution history may need its matching old release for inspection/cleanup.
   An independent fresh native deployment is the simplest transition.
5. Check readiness, source identity, typed failure/fix results, artifacts and
   cancellation before reopening admission. Qualify the supported workload anew.

Restore database and filesystem state together into new empty destinations while
all writers remain stopped. Compare rows, sequences, schema and state inventories
with the saved backup. Point maintenance at the restored database, then run:

```bash
tools/Fogell.Recovery/bin/Release/net10.0/Fogell.Recovery \
  activate-restore --writers-quiesced
```

Activation advances the restore epoch and fences stale authority. It does not
prove quiescence or backup correctness; those are operator preconditions. Restart
only the matching controller release and confirm stale publications are refused,
retained artifacts can be read and a fresh native control completes. Inspect
`reconciliation_required` outcomes; never fabricate a terminal result or replay
an uncertain external effect automatically.

Rollback is a restoration of the matching release/database/state tuple, followed
by epoch activation and control checks. Do not run an older worker against newly
admitted native definitions. Keep evidence from failed upgrades and rehearsals.
Measure full service downtime, backup age/data loss and successful-control time;
there is no currently certified native-release recovery SLO.

## Local paired recovery-point helper

`scripts/paired-backup.py` creates one recovery point after the operator has
stopped admission, drained work, stopped every controller/worker/retention
writer, and verified that no descendant still writes the state root. The
`--writers-quiesced` flag records that assertion; the helper cannot prove it.
Use a new absolute output directory outside the state root and a PostgreSQL 16
client toolchain matching the server. Supply the maintenance connection through
the existing `FOGELL_MAINTENANCE_DATABASE_URL` secret environment:

```sh
scripts/paired-backup.py create \
  --state-root /srv/fogell/state \
  --output /srv/fogell/backups/2026-09-29T180000Z \
  --release-id EXACT_RELEASE_COMMIT \
  --writers-quiesced
scripts/paired-backup.py check /srv/fogell/backups/2026-09-29T180000Z
```

The point contains a PostgreSQL custom archive, state tar, and manifest with
both hashes, the state inventory hash, schema version, release ID and creation
time. `check` verifies both file hashes, the readable PostgreSQL archive table
of contents, and the state inventory. It does not restore the archive or prove
application recovery. Keep the point immutable and rehearse a paired restore
into empty disposable destinations before relying on it. A scheduler and
retention sweep are separate FG-308 work; never start them while writers are
quiesced for a backup unless their runbook conditions are met.

If creation fails after its output directory was created, the helper removes
the partial point and writes an adjacent `<output>.<UTC timestamp>.failed.json`
record containing only `timestamp`, `output`, and `failure_class`. Preserve
these records with backup evidence. The operator check alerts while a failed
attempt has no newer verified recovery point; successful verification clears
that alert without deleting the failure record. Failed-check records do not
make a recovery point valid: diagnose them and create a replacement point.
