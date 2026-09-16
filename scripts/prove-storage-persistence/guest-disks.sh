#!/bin/bash
set -euo pipefail
# Fresh guest only: refuse any pre-existing filesystem on these owned disks.
for disk in /dev/vdb /dev/vdc /dev/vdd; do
  test -b "$disk"
  if blkid "$disk" >/dev/null; then echo 'refusing nonempty guest disk' >&2; exit 1; fi
done
mkfs.ext4 -F -N 4096 -E lazy_itable_init=0,lazy_journal_init=0 /dev/vdb
mkfs.ext4 -F -E lazy_itable_init=0,lazy_journal_init=0 /dev/vdc
mkfs.ext4 -F -E lazy_itable_init=0,lazy_journal_init=0 /dev/vdd
mkdir -p /srv/fogell/state /var/lib/postgresql
mount /dev/vdc /srv/fogell/state
mkdir /srv/fogell/state/workspaces
mount /dev/vdb /srv/fogell/state/workspaces
mount /dev/vdd /var/lib/postgresql
for pair in '/dev/vdc /srv/fogell/state' '/dev/vdb /srv/fogell/state/workspaces' '/dev/vdd /var/lib/postgresql'; do
  read -r disk target <<<"$pair"
  printf 'UUID=%s %s ext4 defaults 0 2\n' "$(blkid -s UUID -o value "$disk")" "$target" >>/etc/fstab
done
sync
