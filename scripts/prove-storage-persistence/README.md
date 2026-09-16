# Storage persistence evidence harness

This directory contains the harness for the isolated Ubuntu 24.04 KVM/QEMU
persistence campaign. It is an operator-run lab artifact, not a general
installation or production deployment tool. The recorded campaign predates the
host-token and VM failure cleanup added during review; its original sources are retained
under `evidence/20260916-ext4-persistence/measured-harness/`. The evidence receipt
maps measured source paths and separately pins the current verifier.

The campaign exercises a Fogell controller and worker against three persistent
guest ext4 filesystems: controller state, the bounded workspaces pool, and
PostgreSQL data.  It records clean boots, byte and inode exhaustion, abrupt
guest termination while a worker is active, recovery-receipt cut points, and
post-recovery controls.  `verify.py` validates the fixed JSONL evidence schema
emitted by the campaign.

## Included and deliberately omitted material

The Python programs, guest setup scripts, and QEMU `Containerfile` are the
only copied artifacts.  This directory intentionally contains no SSH private
or public keys, API tokens, cloud-init `user-data` or `meta-data`, disk images,
release bundle, database contents, or campaign results.

`vm.py` expects those operator-provided inputs under
`$FOGELL_PERSISTENCE_ROOT` (default `/tmp/fogell-persistence-20260916`):

```
os.qcow2
pool.raw
state.raw
postgres.raw
seed.iso
guest-key
guest-key.pub
```

Keep that directory private.  `campaign.py` writes a short-lived copy of the
guest API token there with mode `0600`, as required for its local API client,
and removes that host-side copy on success or exception. If the
host harness itself is forcibly killed, remove its `token` file manually after
stopping API access; Python cleanup cannot run after host process SIGKILL.
The guest retains its own token on its disk for an explicit later boot.

Run the isolated cleanup tests without booting a guest:

```sh
for test in test-*.py; do python3 "$test" || exit; done
```

The campaign adopts its initial labeled VM and tracks each subsequent boot by
immutable container ID. The collector tracks its own boot. Both callers clean
up their owned VM on success or failure, including an already-stopped VM;
a replacement container with the same name is not targeted. Boot and shutdown
failures also clean up after ownership is established. Tests cover caller
failures, interruption, ownership changes, stopped containers, and cleanup
errors. The collector restores its logging callback even when teardown fails.
A cleanup failure raises an error; when another exception is already active,
that exception is retained with a cleanup-failure note.

## Safety boundary

**`guest-disks.sh` and `guest-setup.sh` run inside the disposable guest only,
as guest root.  Do not run either script on Luigi or any other host.**

`guest-disks.sh` formats `/dev/vdb`, `/dev/vdc`, and `/dev/vdd` as ext4 and
updates the guest's `/etc/fstab`.  `guest-setup.sh` installs packages, creates
the Fogell service user and systemd unit, refuses an existing `/opt/fogell`, and
initializes the guest storage-pool metadata.  They reject some unsafe pre-existing state,
but that does not make them safe to run on a host.

The Python harness also creates and kills a named rootless Podman/QEMU
container and rewrites files below its evidence root.  Use an isolated Luigi
lab host with `/dev/kvm`, rootless Podman, and an approved QEMU image only.

## Reproduction outline

This is intentionally an outline: SSH/bootstrap material and the approved
Fogell release bundle are supplied by the lab operator and are not recreated
by this repository.

1. On the isolated host, choose an empty private evidence root and set
   `FOGELL_PERSISTENCE_ROOT` to it.  Build the QEMU runner image from this
   directory if it is absent:

   ```sh
   podman build -t localhost/fogell-persistence-qemu:20260916 -f Containerfile .
   ```

2. Download the recorded official Ubuntu image and verify it before creating a
   private copy-on-write OS disk.  The URL and SHA-256 below identify the image
   recorded in the campaign receipt (the `current` URL can change; stop if the
   pinned checksum no longer matches):

   ```sh
   curl -fL -o "$FOGELL_PERSISTENCE_ROOT/noble-base.img" \
     https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img
   printf '%s  %s\n' \
     612b2c0cc1bc413a6cb8c38fd611794caf0f2b436c50013d8b3794db12ad7354 \
     "$FOGELL_PERSISTENCE_ROOT/noble-base.img" | sha256sum -c -
   chmod 0444 "$FOGELL_PERSISTENCE_ROOT/noble-base.img"
   podman run --rm -v "$FOGELL_PERSISTENCE_ROOT:/vm:rw" \
     localhost/fogell-persistence-qemu:20260916 \
     qemu-img create -f qcow2 -F qcow2 -b /vm/noble-base.img /vm/os.qcow2 12G
   fallocate -l 256M "$FOGELL_PERSISTENCE_ROOT/pool.raw"
   fallocate -l 128M "$FOGELL_PERSISTENCE_ROOT/state.raw"
   fallocate -l 512M "$FOGELL_PERSISTENCE_ROOT/postgres.raw"
   podman run --rm -v "$FOGELL_PERSISTENCE_ROOT:/vm:rw" \
     localhost/fogell-persistence-qemu:20260916 \
     cloud-localds /vm/seed.iso /vm/user-data /vm/meta-data
   ```

   The three raw files must be fresh and unformatted: `guest-disks.sh` formats
   them inside the guest.  It assigns `/dev/vdb` to the 256 MiB workspace pool
   with 4096 inodes, `/dev/vdc` to controller state, and `/dev/vdd` to
   PostgreSQL.

3. Provision an Ubuntu 24.04 guest with the omitted cloud-init seed and SSH
   key material. The seed must authorize `guest-key.pub` for user `ubuntu`,
   retain that user's passwordless sudo, and enable SSH; keep `guest-key` mode
   0600. The recorded host UID is 1000 and must have access to `/dev/kvm` through
   the retained supplementary group. Stage scripts and the approved bundle
   under the private evidence root so `vm.copy_to_guest` can access them through
   `/vm`. Start the provisioning guest from the host:

   ```sh
   python3 vm.py boot provision --provision
   ```

   In a new Python process, call `vm.adopt_named_owned()` before using
   `vm.copy_to_guest` or `vm.guest`; transports require the captured owned ID.
   Copy the two guest-only shell scripts into the guest and run them there as
   root.  Do not execute those scripts from the host.

4. In the guest, stage the approved merged Fogell bundle at
   `/home/ubuntu/fogell-bundle` and pass
   `FOGELL_BUNDLE_DIR=/home/ubuntu/fogell-bundle` to `guest-setup.sh`.  It must contain
   `app/controller`, `app/runner`, `dotnet`, `storage-pool.py`, and
   `binary-manifest.sha256` from the
   exact release under test.  Install `recovery-cutpoint.py` as
   `/opt/fogell/recovery-cutpoint.py`, readable and executable by `fogell`.
   The guest setup leaves the controller stopped and disabled for the campaign.

5. Shut the provisioned guest down cleanly.  `vm.py` appends records to
   `vm-events.jsonl`; preserve the provisioning log under another name before
   the campaign so the verifier sees exactly the campaign's expected start and
   shutdown records:

   ```sh
   python3 vm.py stop provision
   mv "$FOGELL_PERSISTENCE_ROOT/vm-events.jsonl" \
     "$FOGELL_PERSISTENCE_ROOT/vm-events.provisioning.jsonl"
   ```

   Start the initial campaign boot with its required label, then run the
   campaign from this directory:

   ```sh
   python3 vm.py boot campaign-start
   python3 campaign.py
   python3 verify.py "$FOGELL_PERSISTENCE_ROOT/results.jsonl" \
     "$FOGELL_PERSISTENCE_ROOT/vm-events.jsonl" \
     "$FOGELL_PERSISTENCE_ROOT/mounts.json" --self-test
   ```

   `campaign.py` performs its own reboot and power-cut cycles, and ends by
   stopping the guest. To collect raw receipts and check all deployed binary
   hashes afterward, retain a pre-campaign `podman ps -a --format json` snapshot
   as `protected-before.json`, then run:

   ```sh
   python3 collect.py --protected-before "$FOGELL_PERSISTENCE_ROOT/protected-before.json"
   ```

   This uses a separate collector lifecycle log and cleanly stops its guest.
   `collect.py` expects the recorded five protected service names on Luigi;
   adapt that explicit allowlist for another lab. Preserve `results.jsonl`, `vm-events.jsonl`, mount
   topology, serial logs, and captured guest journals as the evidence set.

## What this evidence does and does not establish

The harness establishes behavior for this exact guest, release bundle, host
kernel, QEMU configuration, and raw-disk setup.  QEMU `SIGKILL` is a guest
power-cut simulation; it is not evidence of physical power-loss behavior.
The QEMU drives use `cache=none` and the campaign checks PostgreSQL `fsync`,
`synchronous_commit`, and `full_page_writes`, but host storage and kernel
durability remain environmental assumptions.

The three guest filesystems are separate ext4 filesystems, while their raw
image files can still reside on the same host storage device.  The local
PostgreSQL trust configuration, loopback-in-guest API client, self-signed TLS,
and fixed test organization/project are test-only arrangements.  None of this
is a prescription for a multi-tenant or production deployment.
