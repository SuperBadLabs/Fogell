# FG-268 focused retention proof

The reviewed implementation is recorded in [revised/receipt.json](revised/receipt.json)
and its three `run-*.log` files. It adds the exact build-scoped HOME and stash
roots to inventory and removes a redundant effect-checkpoint trigger that
inverted pre-existing restore/classification lock ordering. The unchanged
[existing classification-order regression](revised/classification-order.log)
passes against the revised migration. The original receipt/logs below are
preserved as earlier-candidate evidence, not the final implementation proof.

All 23 PostgreSQL/filesystem tests passed in each of three bounded repetitions;
69 test executions, zero skips. Each run created its own UUID-named PostgreSQL
database, installed the actual migrations, and dropped that database in `finally`.
Every filesystem mutation was confined to a test-created temporary state tree.
The host's existing controller data was not used by these tests.

[receipt.json](receipt.json) records monotonic process durations, binary/source
hashes and the 60-second process deadline. The source is the uncommitted
implementation, not a released revision. [Run 1](run-1.log), [run 2](run-2.log)
and [run 3](run-3.log) retain complete test summaries. The three expected CLI
`invalid_options` messages in each log are passing negative controls.

The suite proves:

- Age/count/size policies and source/log/HOME/stash expiry; a five-build workload returns
  to one retained build within a declared maximum of 40 two-operation sweeps.
- Active, reconciliation and retry-parent evidence survives.
- Resume after interruption at selection, deleting state, durable delete intent,
  quarantine rename, unlink, filesystem completion and database-payload deletion.
- A concurrent sweep promptly refuses while the winner owns the tenant lock.
- Changed state-root, ancestor or file identities, missing unjournaled files,
  recreated deleted roots and mismatched database/source transport refuse or hold.
- Symlink entries are unlinked without deleting their targets; inventory limits
  refuse before selection; expired builds reject new log and retry writes.
- Unknown/duplicate CLI options and overflowing limits cannot silently execute
  a destructive default. A deliberately broken test cleaner deletes a protected
  test file, and the preservation checker rejects that false success.

This is a focused safety proof. It does not establish unrestricted history
compaction, malicious same-UID filesystem isolation, crash consistency after
arbitrary storage hardware failure, or a sustained production load envelope.
The controller pilot separately measures capacity recovery on real M1 workloads.
