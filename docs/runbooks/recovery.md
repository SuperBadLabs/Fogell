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
