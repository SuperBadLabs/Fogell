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
cleanup() {
  [[ -z "$locker_pid" ]] || kill "$locker_pid" 2>/dev/null || true
  mountpoint -q "$pool" && umount "$pool" || true
  rm -rf -- "$root"
}
trap cleanup EXIT

mkdir -p "$pool" "$receipt_dir"
mount -t tmpfs -o size=8m,nr_inodes=1024 tmpfs "$pool"

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
