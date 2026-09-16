# Workspace storage pool operations

Fogell can use one operator-provisioned, bounded filesystem at
`$FOGELL_STATE_ROOT/workspaces`. This is one global execution pool for every
controller using that mount. It is not a per-build quota, a byte reservation,
or a promise that an arbitrary shell command cannot receive `ENOSPC`.

The filesystem is the hard bound. Fogell's capacity checks decide whether to
start work, but an unbounded shell command or stash copy can consume the pool
after admission. The single pool lease serializes controllers that share it.

## Provisioning

For production, mount a persistent ext4 filesystem at the exact `workspaces`
path. The accepted filesystem types are the fixed-inode ext family and tmpfs;
finite reports from XFS, btrfs, overlay, or unknown types are refused. Keep
journals, definitions, events, and admission diagnostics on the state-root
filesystem outside the mount, on a different device. Do not grow, remount,
replace, or modify pool-control metadata while controllers are active.

Fully provisioned backing is needed if free-space observations should predict
successful writes. Thin or shared backing may fail earlier with ENOSPC/EIO;
reported logical free space does not reserve underlying physical capacity.

The controller requires 64-bit Linux with `statx` mount-ID reporting and
finite, nonzero filesystem byte and inode totals. Stop existing controllers
before enabling this policy; an older binary does not participate in the pool
locks. A current controller with no policy refuses an initialized pool.
An unconfigured controller also refuses work when it cannot determine whether
pool-control metadata exists; lookup failures are never treated as an opt-out.

The mount must be empty except for an empty filesystem-created `lost+found`
directory. Recovered files inside `lost+found` must be handled before
initialization; the helper never adopts or deletes them. After mounting it,
run once as the service operator:

```sh
scripts/storage-pool.py init \
  --state-root /srv/fogell/state \
  --pool-id production-workspaces
```

The command walks every path component without following symlinks, verifies the
separate device, refuses unexpected entries, and creates only:

```text
workspaces/.fogell-pool-id       mode 0600, exact pool ID bytes
workspaces/.fogell-pool-state    mode 0600, exactly 4096 zero bytes
```

It never overwrites or adopts either file. Both files and the workspace
directory are fsynced before success.

If initialization reports a write or sync failure, it rolls back only metadata
created by that invocation while retaining the pool lock. Existing or observed
replacement files are preserved. An incomplete rollback is reported explicitly;
do not treat it, or an interrupted initializer, as a successfully initialized
pool.

Set all five variables on every controller sharing this pool:

```text
FOGELL_STORAGE_POOL_ID=production-workspaces
FOGELL_STORAGE_POOL_MAX_BYTES=...
FOGELL_STORAGE_POOL_MAX_INODES=...
FOGELL_STORAGE_POOL_MIN_FREE_BYTES=...
FOGELL_STORAGE_POOL_MIN_FREE_INODES=...
```

`MAX_*` bounds the expected mounted filesystem and prevents a larger misplaced
or replacement mount from being accepted. `MIN_FREE_*` is the start guard.
Missing any variable is a configuration error. The marker must exactly match
`FOGELL_STORAGE_POOL_ID`.

A rootless Podman lab may use a dedicated tmpfs with explicit `size` and inode
limits. Tmpfs is not production recovery storage: reboot loses its workspace
bytes and dirty-state evidence. Use persistent ext4 for production.

The [persistent ext4 evidence](../../evidence/20260916-ext4-persistence/README.md)
records a rootless Podman/KVM guest with separate workspace, state, and
PostgreSQL filesystems. Byte/inode exhaustion, a clean reboot, and three rounds
of each of three abrupt VM cut points passed against the merged Release
binaries. The dirty record and synced receipts survived reboot, and recovery
required the expected hash and an explicit writers-extinct attestation.
This measures guest crash persistence on the recorded configuration; it does
not establish physical-host power-loss behavior, arbitrary backing-store
durability, or backup/restore correctness. Keep the provisioning and recovery
requirements below even when using that tested profile.

## Runtime scope and trust boundary

The mount covers workspaces, their `@tmp` durable-script siblings, artifacts,
stashes, SCM history, snapshots, and the secrets tree below `workspaces`. The
worker places runner scratch below the bounded execution filesystem, and the
standard build environment supplies a private `HOME` and `TMPDIR` there.
Trusted pipeline environment overrides and explicit absolute paths can redirect
writes elsewhere; deploy a read-only container root and independently bounded
`/tmp` where required. This pool is not whole-host write confinement.

The state record is not an allocation counter. An all-zero 4096-byte record is
idle; any nonzero byte is dirty. Fogell fsyncs an active record before
materialization or child launch, then clears it only after a durable terminal
result or a proven path where no child started.

Entering `Process.Start` makes the launch uncertain even if that call throws:
an exception alone is not evidence that no child was created. Such failures
retain the dirty record and require the same explicit reconciliation as other
uncertain launches.

Only the controller holding the pool lease updates the bounded
`storage-admission.json` on the state-root filesystem. It records capacity
pressure and other decisions after ownership. Structural configuration or
lock-contention failures before ownership appear in worker logs and readiness
503; they do not overwrite the active owner's durable decision. Storage
refusals after a claim also log whether the fenced requeue succeeded, lost
authority, or failed.

The controller holds exclusive nonblocking `flock` locks for its lifetime on
the mounted `workspaces` directory and then on the state file, in that order.
The directory lock keeps cooperating controllers from splitting their state
coordination across a replaced state-file inode. A controller crash releases
the kernel locks but leaves the record dirty. There is no lease expiry,
automatic cleanup, deletion, or recovery.

Descriptor walking and revalidation reject observed symlink or inode
substitution. They do not protect a state root writable by the workload UID:
the existing local worker is a same-UID mutually trusting deployment profile.
Trusted jobs in that profile must not alter the pool mount, pool-control files,
or controller metadata. Metadata erasure and destructive reinitialization are
never safe recovery actions. Hostile-job containment is outside the current
deployment model; this configuration does not create that separation or stop a
deliberately malicious same-UID process.

## Status

Status is read-only. It takes a nonblocking shared lock on the mounted pool;
while a controller or recovery holds the exclusive directory lock, status
refuses as busy instead of reporting an unstable record. Otherwise it validates
the nofollow path, dedicated mount, marker, regular 0600 state file, exact
size, and descriptor/path identity. It returns only the pool ID, `idle` or
`dirty`, and a SHA-256 record hash; it does not print the active record text.

```sh
scripts/storage-pool.py status \
  --state-root /srv/fogell/state \
  --pool-id production-workspaces
```

If status reports a malformed file, path replacement, marker mismatch, or dirty
record, treat it as a refusal. Never remove, truncate, or replace the state
file to get around it.

## Explicit recovery

The script cannot prove that a controller, Run.Host, container, VM, shell, or
descendant has stopped. Before recovery:

1. Stop **all** controllers using the pool.
2. Stop the entire container or VM execution domain, including detached
   descendants which may still have the workspace mount open.
3. Prove no old writer remains using process and mount evidence, and retain
   that evidence with the incident.
4. Run `status` and copy `record_sha256`.
5. Create the fixed receipt directory
   `$FOGELL_STATE_ROOT/storage-pool-receipts` on the state-root filesystem.
   The helper opens that exact nofollow path and refuses it if it resolves to
   the pool filesystem. The operator note becomes part of that receipt; do not
   include credentials or other secrets.

Only after those steps may an operator attest and clear exactly the observed
record:

```sh
scripts/storage-pool.py recover \
  --state-root /srv/fogell/state \
  --pool-id production-workspaces \
  --writers-extinct \
  --expected-record-sha256 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
  --operator-note 'INC-1234: writers stopped; evidence retained'
```

For a normal Fogell active record, `--expected-active-text` may replace the
hash. Exactly one expected value is required, so an operator cannot clear a
different attempt accidentally. Recovery takes the controller's exclusive lock,
validates marker and descriptor identity before and after the operation, fsyncs
an audit receipt outside the pool before zeroing, then writes and fsyncs all
4096 zero bytes.

Recovery is an operator acknowledgement of a proven-safe condition. It does
not reclaim workspaces, artifacts, stashes, or snapshots. Never automate it
from a timer, restart hook, health check, or lease-expiry path.
