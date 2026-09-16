# Persistent ext4 crash and recovery evidence

**Passed on Luigi, 2026-09-16.** This closes the persistent-filesystem VM
crash/reboot gap for Fogell's bounded, single-writer storage pool. It does not
certify the whole product for production. No application code changed for this
campaign: the tested Release binaries came from merged commit
`7e558e0f2ea665fd00422c840f5bf7690c325767` (PR #452).

## Measured outcomes

| Case | Measurement | Outcome |
| --- | --- | --- |
| ext4 byte exhaustion | Nonroot writes reach kernel ENOSPC and zero available bytes | State-root writes still work; admission records byte pressure; queued build has no runner and succeeds after relief |
| ext4 inode exhaustion | Nonroot file creation reaches kernel ENOSPC and zero available inodes | Same refusal/relief controls, with the inode-pressure reason |
| Clean reboot | Stop controller, guest poweroff, boot unchanged disks | Exact idle metadata retained; normal build succeeds with scratch inside the pool |
| Active execution crash, repeated 3 times | SIGKILL QEMU while the real Run.Host is running | Exact dirty record/hash/inode survives; readiness 503; newly submitted work remains queued without a runner |
| Receipt-before-clear crash, repeated 3 times | SIGKILL QEMU after the helper successfully fsyncs the receipt directory, before it clears the dirty record | Receipt and exact dirty record survive; readiness remains 503 and work remains queued |
| Completed-clear crash, repeated 3 times | Explicit recovery returns, then SIGKILL QEMU | Both receipts and the 4096-byte zero record survive; the same refused queued build succeeds |
| Recovery negative controls, each round | Omit writers-extinct attestation; then supply the wrong record hash | Both refuse without changing the dirty record or creating a receipt |

The campaign recorded **31 result records, 33 lifecycle events, nine SIGKILL
exits (137), ten distinct reboot IDs, and two clean shutdowns**. The separate
post-campaign collector boot is excluded from those counts. `results.jsonl`
and `vm-events.jsonl` contain the assertions' observations; boot journals are
under `journals/`. The raw six receipts, deployed binary hashes, runtime
versions, disk allocation, and protected-service comparison are retained in
`supplemental.json`.

## Deployment and fault boundary

Rootless Podman contains a KVM-accelerated QEMU guest (2 vCPU, 4 GiB RAM).
The container retains access to `/dev/kvm`, drops capabilities, and enables
`no-new-privileges`. The guest has no shared host filesystem. After guest
package provisioning, QEMU user networking is restricted; SSH and HTTPS are
accessed through `podman exec` and forwarding inside the container, with no
published host ports. Fogell runs as guest UID 1100, with root-owned binaries.
The controller service is manually started and disabled at boot.

Three guest block devices hold separate ext4 filesystems:

| Guest device | Mount | Raw image capacity |
| --- | --- | --- |
| `/dev/vdb` | `/srv/fogell/state/workspaces` | 256 MiB, 4096 inodes |
| `/dev/vdc` | `/srv/fogell/state` | 128 MiB |
| `/dev/vdd` | `/var/lib/postgresql` | 512 MiB |

`mounts.json` records actual sources, UUIDs, mount options, and ext4 types.
Every QEMU data drive uses `cache=none,aio=native,discard=ignore`; the same
backing-file device/inode identities are checked on every boot and stop.
PostgreSQL `fsync`, `synchronous_commit`, and `full_page_writes` are all on.
The Ubuntu Noble cloud image was checked against its official SHA256SUMS:
`612b2c0cc1bc413a6cb8c38fd611794caf0f2b436c50013d8b3794db12ad7354`.
The guest kernel is `6.8.0-139-generic`. Fully allocated raw files still share
Luigi's host storage; this does not establish independent physical disks or
guaranteed capacity underneath that filesystem.

The original Release bundle archive SHA-256 is
`576c87e5d793f26e9fb7f56d4327e581403f2349a98b3e7b0e97994b636cd446`.
`binary-manifest.sha256` binds all 89 deployed application files;
`source-manifest.sha256` records the original bundle's source inputs. The
production recovery helper SHA-256 is
`1513a843c414cf264b9739fe93aa2d8e6fab5903a29b79c5d931cef78e98d8c9`.

## Deterministic receipt cut point

The test wrapper runs the unchanged production `storage-pool.py`. It preopens
the fixed receipt directory and intercepts `os.fsync` only for a descriptor
with that directory's device/inode identity. It calls the real fsync first,
checks the helper hash, writes a synchronization witness under guest `/run`,
then blocks before returning to the helper. The campaign verifies the receipt,
unchanged dirty record, helper hash, and directory identity before killing QEMU.
The witness is synchronization, not persistent evidence; the receipt reread
after reboot is the persistence observation.

Each recovery follows termination of the entire previous guest execution
domain. The current controller is stopped and absence of Run.Host checked
before operator recovery. Old receipts are archived without deletion between
rounds, with affected directories synced. Neither recovery nor the harness
reinitializes or deletes pool-control metadata.

## Recheck and reproduce

From the repository root:

```sh
python3 scripts/prove-storage-persistence/verify.py \
  evidence/20260916-ext4-persistence/results.jsonl \
  evidence/20260916-ext4-persistence/vm-events.jsonl \
  evidence/20260916-ext4-persistence/mounts.json --self-test
```

The checker validates controls, exact state/receipt preservation, receipt
content hashes, the resumed build IDs, all cut/boot identities, ext4 topology,
and exact QEMU drive paths/formats/cache settings. Negative mutations must be
rejected. [Harness instructions](../../scripts/prove-storage-persistence/README.md)
describe recreating the disposable lab; SSH keys, tokens, disk images, and
binaries are intentionally excluded from this repository.

Preliminary setup trials exposed overlapping guest/container NAT ranges and
restricted-network forwarding, then a fixed fixture slug collision. Those
trials are not included in the passing campaign counts. The final harness
uses a separate guest subnet, inside-container SSH/API access, and its own
organization/project. Completed preliminary fixture jobs remained on the
same disks; no preliminary active writer or recovery receipt was carried into
the measured rounds.

## Scope and remaining work

SIGKILL discards the guest kernel and its page cache. Luigi and its storage
remain powered on. This proves the recorded guest-crash behavior, not physical
host power loss, drive/controller cache honesty, or other filesystems. It also
does not prove paired database/state restore, retention, per-build quotas,
concurrent-worker reservations, or hostile-job isolation. Those claims remain
outside this campaign. Retention is the next delivery batch.

The collector cleanly stops and removes its named guest container. Cleanup
removes the task's toolbox image; inert disk artifacts remain in Luigi's
private task directory for investigation. The five pre-existing Luigi service
containers retain their IDs, PIDs, start times, restart counts, and state.
