#!/usr/bin/env bash
# Integration proof for scripts/storage-pool.py. It enters an isolated user and
# mount namespace and needs a real tmpfs mount because init must reject a
# workspaces directory on state-root's device; a normal-directory fake would
# prove the wrong policy. It changes no host mounts.
set -Eeuo pipefail

if [[ ${FOGELL_STORAGE_POOL_PROOF_NAMESPACE:-} != 1 ]]; then
  command -v unshare >/dev/null || {
    echo "unavailable: prove-storage-pool.sh requires util-linux unshare" >&2
    exit 2
  }
  exec env FOGELL_STORAGE_POOL_PROOF_NAMESPACE=1 unshare -Urnm --fork "$0" "$@"
fi

[[ $(id -u) -eq 0 ]] || {
  echo "unavailable: user namespace did not grant mount capability" >&2
  exit 2
}
mount --make-rprivate /

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
root=$(mktemp -d /tmp/fogell-storage-pool.XXXXXX)
state_root="$root/state"
pool="$state_root/workspaces"
receipt_dir="$state_root/storage-pool-receipts"
locker_pid=""
rollback_state_roots=()
cleanup() {
  [[ -z "$locker_pid" ]] || kill "$locker_pid" 2>/dev/null || true
  for rollback_state_root in "${rollback_state_roots[@]}"; do
    rollback_pool="$rollback_state_root/workspaces"
    mountpoint -q "$rollback_pool" && umount "$rollback_pool" || true
  done
  mountpoint -q "$pool" && umount "$pool" || true
  rm -rf -- "$root"
}
trap cleanup EXIT

mkdir -p "$pool" "$receipt_dir"
mount -t tmpfs -o size=8m,nr_inodes=1024 tmpfs "$pool"

for rollback_case in post-marker partial-state directory-fsync preexisting-marker state-oexcl replaced-marker lost-found-empty lost-found-nonempty; do
  rollback_state_root="$root/rollback-$rollback_case/state"
  mkdir -p "$rollback_state_root/workspaces"
  mount -t tmpfs -o size=2m,nr_inodes=256 tmpfs "$rollback_state_root/workspaces"
  rollback_state_roots+=("$rollback_state_root")
done

PYTHONDONTWRITEBYTECODE=1 python3 - "$script_dir/storage-pool.py" "${rollback_state_roots[@]}" <<'PY'
import argparse
import errno
import importlib.util
import os
import pathlib
import stat
import sys

source, *roots = sys.argv[1:]
spec = importlib.util.spec_from_file_location("storage_pool_proof", source)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)
pool_id = "rollback-proof"

# The controller accepts / as a state root; the helper must open that directory
# directly without trying to open an empty relative component.
root_fd = module.open_tree("/", "state root")
try:
    root_stat = os.stat("/")
    assert module.identity(root_fd) == module.FileIdentity(root_stat.st_dev, root_stat.st_ino)
finally:
    os.close(root_fd)
for malformed in ("//", "/tmp/", "/tmp//pool", "/tmp/../pool"):
    try:
        unexpected_fd = module.open_tree(malformed, "state root")
    except module.PoolError:
        continue
    os.close(unexpected_fd)
    raise AssertionError(f"malformed state root unexpectedly accepted: {malformed}")
print("root-state-path-and-malformed-components: passed")


def paths(root):
    pool = pathlib.Path(root) / "workspaces"
    return pool, pool / module.MARKER_FILE, pool / module.STATE_FILE


def invoke(root):
    return module.init(argparse.Namespace(state_root=root, pool_id=pool_id))


def expect_failure(root):
    try:
        invoke(root)
    except module.PoolError:
        return
    raise AssertionError("faulted init unexpectedly succeeded")


def assert_no_own_residue(root):
    pool, marker, state = paths(root)
    assert not marker.exists(), marker
    assert not state.exists(), state
    assert not set(os.listdir(pool)), os.listdir(pool)


def assert_successful_retry(root):
    invoke(root)
    _, marker, state = paths(root)
    assert marker.read_text(encoding="utf-8") == pool_id
    assert state.read_bytes() == bytes(module.STATE_BYTES)
    assert stat.S_IMODE(marker.stat().st_mode) == 0o600
    assert stat.S_IMODE(state.stat().st_mode) == 0o600


post_marker, partial_state, directory_fsync, existing_marker, state_oexcl, replaced_marker, empty_lost_found, nonempty_lost_found = roots

original_write = module.write_exact
def fail_after_marker(fd, data, description):
    original_write(fd, data, description)
    if description == "storage pool marker":
        raise module.PoolError("injected post-marker-create failure")
module.write_exact = fail_after_marker
expect_failure(post_marker)
module.write_exact = original_write
assert_no_own_residue(post_marker)
assert_successful_retry(post_marker)

def fail_partial_state(fd, data, description):
    if description == "storage pool state":
        os.lseek(fd, 0, os.SEEK_SET)
        assert os.write(fd, data[:97]) == 97
        raise module.PoolError("injected partial-state-write failure")
    return original_write(fd, data, description)
module.write_exact = fail_partial_state
expect_failure(partial_state)
module.write_exact = original_write
assert_no_own_residue(partial_state)
assert_successful_retry(partial_state)

original_fsync = module.os.fsync
fsync_calls = 0
def fail_initial_directory_fsync(fd):
    global fsync_calls
    fsync_calls += 1
    if fsync_calls == 3:
        raise OSError(errno.ENOSPC, "injected directory fsync failure")
    return original_fsync(fd)
module.os.fsync = fail_initial_directory_fsync
expect_failure(directory_fsync)
module.os.fsync = original_fsync
assert_no_own_residue(directory_fsync)
assert_successful_retry(directory_fsync)

pool, marker, state = paths(existing_marker)
marker.write_text("preexisting", encoding="utf-8")
os.chmod(marker, 0o600)
marker_identity = (marker.stat().st_dev, marker.stat().st_ino)
expect_failure(existing_marker)
assert marker.read_text(encoding="utf-8") == "preexisting"
assert (marker.stat().st_dev, marker.stat().st_ino) == marker_identity
assert not state.exists()
marker.unlink()
assert_successful_retry(existing_marker)

original_open = module.os.open
foreign_state = b"preexisting-state"
def collide_state_open(path, flags, mode=0o777, *, dir_fd=None):
    if path == module.STATE_FILE and flags & os.O_EXCL:
        foreign_fd = original_open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=dir_fd)
        try:
            assert os.write(foreign_fd, foreign_state) == len(foreign_state)
            os.fsync(foreign_fd)
        finally:
            os.close(foreign_fd)
    return original_open(path, flags, mode, dir_fd=dir_fd)
module.os.open = collide_state_open
expect_failure(state_oexcl)
module.os.open = original_open
pool, marker, state = paths(state_oexcl)
assert not marker.exists(), "marker created by failed init was not rolled back"
assert state.read_bytes() == foreign_state
state.unlink()
assert_successful_retry(state_oexcl)

pool, marker, state = paths(replaced_marker)
displaced = pool / ".displaced-own-marker"
original_write = module.write_exact
def replace_marker_before_failure(fd, data, description):
    original_write(fd, data, description)
    if description == "storage pool marker":
        marker.rename(displaced)
        marker.write_text("foreign-marker", encoding="utf-8")
        os.chmod(marker, 0o600)
        raise module.PoolError("injected replacement before rollback")
module.write_exact = replace_marker_before_failure
expect_failure(replaced_marker)
module.write_exact = original_write
assert marker.read_text(encoding="utf-8") == "foreign-marker"
assert displaced.exists(), "the original inode must not be mistaken for the replacement"
marker.unlink()
displaced.unlink()
assert_successful_retry(replaced_marker)

pool, marker, state = paths(empty_lost_found)
(pool / "lost+found").mkdir()
invoke(empty_lost_found)
assert marker.exists() and state.exists(), "an empty filesystem-created lost+found is permitted"

pool, marker, state = paths(nonempty_lost_found)
lost_found = pool / "lost+found"
lost_found.mkdir()
recovered = lost_found / "recovered-file"
recovered.write_bytes(b"recovered data")
recovered_identity = (recovered.stat().st_dev, recovered.stat().st_ino)
expect_failure(nonempty_lost_found)
assert not marker.exists() and not state.exists(), "nonempty lost+found must be rejected before metadata creation"
assert recovered.read_bytes() == b"recovered data"
assert (recovered.stat().st_dev, recovered.stat().st_ino) == recovered_identity
print("rollback-init-faults-and-preexisting-metadata: passed")
PY

python3 "$script_dir/storage-pool.py" init --state-root "$state_root" --pool-id proof-pool
[[ $(stat -c '%a:%s' "$pool/.fogell-pool-state") == 600:4096 ]]
[[ $(cat "$pool/.fogell-pool-id") == proof-pool ]]
python3 "$script_dir/storage-pool.py" status --state-root "$state_root" --pool-id proof-pool | grep -q '"status": "idle"'

python3 - "$pool/.fogell-pool-state" <<'PY'
import os
import sys

path = sys.argv[1]
record = bytearray(4096)
record[0] = 1
record[1:14] = b"attempt-proof"
record[-1] = 0xA5
with open(path, "r+b", buffering=0) as state:
    state.write(record)
    state.flush()
    os.fsync(state.fileno())
PY

old_sha=$(sha256sum "$pool/.fogell-pool-state" | awk '{print $1}')

python3 - "$pool" "$pool/.fogell-pool-state" "$root/locked" <<'PY' &
import fcntl
import os
import sys
import time

pool_fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY)
with open(sys.argv[2], "r+b", buffering=0) as state:
    fcntl.flock(pool_fd, fcntl.LOCK_EX)
    fcntl.flock(state, fcntl.LOCK_EX)
    with open(sys.argv[3], "w", encoding="ascii"):
        pass
    time.sleep(30)
os.close(pool_fd)
PY
locker_pid=$!
for _ in {1..50}; do
  [[ -e "$root/locked" ]] && break
  sleep 0.1
done
[[ -e "$root/locked" ]] || { echo "lock helper did not acquire flock" >&2; exit 1; }
if python3 "$script_dir/storage-pool.py" status --state-root "$state_root" --pool-id proof-pool >/dev/null 2>&1; then
  echo "status while a competing directory lock is held unexpectedly succeeded" >&2
  exit 1
fi
if python3 "$script_dir/storage-pool.py" recover --state-root "$state_root" --pool-id proof-pool --writers-extinct --expected-active-text attempt-proof --operator-note proof >/dev/null 2>&1; then
  echo "recovery while a competing lock is held unexpectedly succeeded" >&2
  exit 1
fi
kill "$locker_pid"
wait "$locker_pid" || true
locker_pid=""

if python3 "$script_dir/storage-pool.py" recover --state-root "$state_root" --pool-id proof-pool --expected-active-text attempt-proof --operator-note proof >/dev/null 2>&1; then
  echo "recovery without writer attestation unexpectedly succeeded" >&2
  exit 1
fi
if python3 "$script_dir/storage-pool.py" recover --state-root "$state_root" --pool-id proof-pool --writers-extinct --expected-record-sha256 0000000000000000000000000000000000000000000000000000000000000000 --operator-note proof >/dev/null 2>&1; then
  echo "recovery with mismatched record hash unexpectedly succeeded" >&2
  exit 1
fi
python3 "$script_dir/storage-pool.py" recover --state-root "$state_root" --pool-id proof-pool --writers-extinct --expected-active-text attempt-proof --operator-note proof
python3 "$script_dir/storage-pool.py" status --state-root "$state_root" --pool-id proof-pool | grep -q '"status": "idle"'
[[ $(find "$receipt_dir" -type f -name 'storage-pool-recovery-*.json' | wc -l) -eq 1 ]]
python3 - "$receipt_dir" "$old_sha" <<'PY'
import json
import pathlib
import sys

receipt = next(pathlib.Path(sys.argv[1]).glob("storage-pool-recovery-*.json"))
assert json.loads(receipt.read_text(encoding="utf-8"))["old_record_sha256"] == sys.argv[2]
PY

if python3 "$script_dir/storage-pool.py" init --state-root "$state_root" --pool-id proof-pool >/dev/null 2>&1; then
  echo "second init unexpectedly overwrote pool metadata" >&2
  exit 1
fi
