# Bounded workspace pool validation

The [receipt](receipt.json) records the measured controller binary's Luigi campaign,
its 89-file binary manifest, changed source hashes against the named base, and
an explicit recovery receipt. Times are Unix seconds. Cleanup preserves the
five pre-existing containers and removes the task's containers, pod, network,
volume, and custom runtime image.

The controller ran as UID 1000 with dropped capabilities, a read-only container
root, a separately bounded `/tmp`, and an internal Podman network. The shared
workspace pool was a rootless local tmpfs volume with
`size=64m,nr_inodes=2048`; the controller's guards were 8 MiB and 128 free inodes.
Journals, events, definitions, and admission decisions remained outside it.

| Case | Observed result |
| --- | --- |
| Normal pipeline | Success; pool returned idle |
| 128 MiB workspace write | Kernel byte ENOSPC; pipeline failure, clean marker |
| More than 2,048 file creations | Kernel inode ENOSPC; pipeline failure, clean marker |
| Stash a 40 MiB file | Copy exhausted the 64 MiB shared pool; pipeline failure |
| Byte or inode pressure before claim | Readiness 503; build queued; no runner; durable reason |
| Remove only owned pressure fixtures | The same queued build succeeded |
| Second controller sharing pool | Readiness 503 |
| Wrong pool-ID marker | Readiness 503; restored ID returned 200 |
| SIGKILL during a running build | Restart retained dirty state and readiness 503 |
| Explicit recovery | Only after all execution processes were zombies; exact record hash matched; external receipt fsynced; next build succeeded |
| Default temporary file | Real `mktemp` output under build HOME/tmp in the pool |
| Admission decision destination made unwritable | Readiness 503, queued build, no runner, no stranded dirty marker; same build succeeded after restoration |

Direct byte and inode probes also wrote a control file on the controller-state
filesystem while the workspace resource was exhausted. Kernel diagnostics and
before/exhausted/restored capacity observations are retained in the receipt.
The repository's [operator proof](../../scripts/prove-storage-pool.sh) separately
exercises initialization, lock contention, recovery identity/attestation,
external receipts, and no-overwrite behavior inside an unprivileged user/mount
namespace.

This proves an aggregate filesystem boundary and serialized Fogell admission.
It does not prove persistent ext4 power-loss recovery, per-build reservations,
concurrent worker slots, retention, or hostile same-UID isolation. Tmpfs survives
an application-process crash inside the living sandbox, but not destruction of
that sandbox or a host reboot. See the [runbook](../../docs/runbooks/storage-pool.md)
for the production policy and operator responsibilities.

## Marker-permission correction

The subsequent [HeMan receipt](marker-permissions-20260916.json) records a fresh
local Podman campaign for source `509abd208b26a451bd184d301e8462980b291fdc`,
with its own Release binary manifest. A `0600` pool-ID marker returned readiness
200 and a successful build; `0644` and `0666` returned 503. The full exhaustion,
pressure, crash, explicit recovery, and diagnostic-write campaign also passed,
and the owned sandbox resources were removed. The runtime now requires reported
`statx` mode bits and exact `0600`, including rejection of special bits.

Automatic approval review rejected uploading the new bundle to Luigi, so this
correction's campaign ran locally. The original Luigi receipt is preserved;
the new receipt does not claim a fresh Luigi run. Later changes only clarify
diagnostic ownership and uncertain starts, and reuse the existing fenced-requeue
logging helper. Their validation belongs to the full local/hosted gates, not to
the measured binary recorded in either sandbox receipt.
