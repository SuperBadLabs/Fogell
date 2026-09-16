#!/usr/bin/env bash
# One-shot setup for the isolated Ubuntu 24.04 persistence guest.
#
# The caller mounts, before this script runs:
#   /dev/vdc -> /srv/fogell/state       (controller journals/events/config)
#   /dev/vdb -> /srv/fogell/state/workspaces (bounded workspace/stash pool)
#   /dev/vdd -> /var/lib/postgresql     (database data)
# All are ext4 filesystems.  The bundle is copied separately from the approved
# exact merged release to $FOGELL_BUNDLE_DIR (default /root/fogell-bundle).
set -euo pipefail
umask 077

BUNDLE_DIR=${FOGELL_BUNDLE_DIR:-/root/fogell-bundle}
INSTALL_ROOT=/opt/fogell
STATE_ROOT=/srv/fogell/state
WORKSPACE_ROOT=$STATE_ROOT/workspaces
PG_ROOT=/var/lib/postgresql
POOL_ID=ext4persistence20260916
POOL_BYTES=268435456
POOL_INODES=4096
MIN_FREE_BYTES=8388608
MIN_FREE_INODES=128
FOGELL_UID=1100

fail() { echo "guest-setup: $*" >&2; exit 1; }
require_root() { [ "${EUID:-$(id -u)}" -eq 0 ] || fail "run as root"; }
require_dir() { [ -d "$1" ] || fail "required directory is missing: $1"; }
require_file() { [ -f "$1" ] || fail "required bundle file is missing: $1"; }

mount_source() { findmnt -n -o SOURCE --target "$1" 2>/dev/null || true; }
mount_fstype() { findmnt -n -o FSTYPE --target "$1" 2>/dev/null || true; }

require_mount() {
  local path=$1 device=$2
  [ "$(mount_source "$path")" = "$device" ] || fail "$path is not mounted from $device"
  [ "$(mount_fstype "$path")" = ext4 ] || fail "$path is not an ext4 mount"
}

require_root
require_dir "$BUNDLE_DIR"
require_file "$BUNDLE_DIR/app/controller/Fogell.Controller.Host"
require_file "$BUNDLE_DIR/app/runner/Fogell.Run.Host"
require_file "$BUNDLE_DIR/dotnet/dotnet"
require_file "$BUNDLE_DIR/storage-pool.py"
require_dir "$STATE_ROOT"
require_dir "$WORKSPACE_ROOT"
require_dir "$PG_ROOT"
require_mount "$STATE_ROOT" /dev/vdc
require_mount "$WORKSPACE_ROOT" /dev/vdb
require_mount "$PG_ROOT" /dev/vdd
[ "$(stat -c %d "$STATE_ROOT")" != "$(stat -c %d "$WORKSPACE_ROOT")" ] \
  || fail "workspaces must be a filesystem distinct from the controller state root"

# This is intentionally a fresh-machine procedure: it never overwrites an
# existing state root, pool marker, or application installation.
if find "$STATE_ROOT" -mindepth 1 -maxdepth 1 ! -name workspaces ! -name lost+found -print -quit | grep -q .; then
  fail "state root is not empty outside its workspaces mount"
fi
if find "$WORKSPACE_ROOT" -mindepth 1 -maxdepth 1 ! -name lost+found -print -quit | grep -q .; then
  fail "workspace pool already contains data or pool metadata"
fi
[ ! -e "$INSTALL_ROOT" ] || fail "$INSTALL_ROOT already exists"

if ! getent group fogell >/dev/null; then
  groupadd --gid "$FOGELL_UID" fogell
fi
if ! id -u fogell >/dev/null 2>&1; then
  useradd --uid "$FOGELL_UID" --gid fogell --home-dir /srv/fogell --no-create-home --shell /usr/sbin/nologin fogell
fi
[ "$(id -u fogell)" = "$FOGELL_UID" ] || fail "fogell must have UID $FOGELL_UID"

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq postgresql-16 openssl libunwind8

# PostgreSQL is bound by its own default loopback settings.  These two explicit
# rules are the only trust exceptions added for this single-guest test: the
# controller's runtime and migration connections both use 127.0.0.1.
PG_HBA=/etc/postgresql/16/main/pg_hba.conf
[ -f "$PG_HBA" ] || fail "PostgreSQL 16 cluster configuration was not installed"
sed -i '/^# fogell guest test trust$/,/^# end fogell guest test trust$/d' "$PG_HBA"
sed -i '1i# fogell guest test trust\nhost fogell fogell_runtime 127.0.0.1/32 trust\nhost fogell postgres 127.0.0.1/32 trust\n# end fogell guest test trust' "$PG_HBA"
systemctl enable --now postgresql >/dev/null
systemctl reload postgresql

sudo -u postgres psql -X -v ON_ERROR_STOP=1 -d postgres <<'SQL' >/dev/null
CREATE ROLE fogell_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
CREATE DATABASE fogell OWNER postgres;
SQL

install -d -o root -g root -m 0755 "$INSTALL_ROOT"
cp -a "$BUNDLE_DIR/app/controller" "$INSTALL_ROOT/controller"
cp -a "$BUNDLE_DIR/app/runner" "$INSTALL_ROOT/runner"
cp -a "$BUNDLE_DIR/dotnet" "$INSTALL_ROOT/dotnet"
install -o root -g root -m 0755 "$BUNDLE_DIR/storage-pool.py" "$INSTALL_ROOT/storage-pool.py"
chown -R root:root "$INSTALL_ROOT"
chmod -R a-w "$INSTALL_ROOT"
chmod 0755 "$INSTALL_ROOT/controller/Fogell.Controller.Host" "$INSTALL_ROOT/runner/Fogell.Run.Host" "$INSTALL_ROOT/dotnet/dotnet" "$INSTALL_ROOT/storage-pool.py"

if [ -e /usr/share/dotnet ] || [ -L /usr/share/dotnet ]; then
  fail "/usr/share/dotnet already exists; refusing to replace a guest runtime"
fi
ln -s "$INSTALL_ROOT/dotnet" /usr/share/dotnet
if [ -e /usr/bin/dotnet ] || [ -L /usr/bin/dotnet ]; then
  fail "/usr/bin/dotnet already exists; refusing to replace a guest runtime"
fi
ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet

for recovered in "$STATE_ROOT/lost+found" "$WORKSPACE_ROOT/lost+found"; do
  [ -d "$recovered" ] && [ ! -L "$recovered" ] || fail "invalid lost+found directory"
  [ -z "$(find "$recovered" -mindepth 1 -print -quit)" ] || fail "recovered files present"
done
chown fogell:fogell "$WORKSPACE_ROOT/lost+found"
chown fogell:fogell /srv/fogell "$STATE_ROOT" "$WORKSPACE_ROOT"
chmod 0700 /srv/fogell "$STATE_ROOT" "$WORKSPACE_ROOT"
runuser -u fogell -- python3 "$INSTALL_ROOT/storage-pool.py" init --state-root "$STATE_ROOT" --pool-id "$POOL_ID" >/dev/null

install -d -o fogell -g fogell -m 0700 "$STATE_ROOT/tls"
openssl req -x509 -newkey rsa:2048 -nodes -days 7 -subj /CN=fogell-persistence-guest \
  -keyout "$STATE_ROOT/tls/key.pem" -out "$STATE_ROOT/tls/cert.pem" >/dev/null 2>&1
openssl pkcs12 -export -passout pass: -inkey "$STATE_ROOT/tls/key.pem" -in "$STATE_ROOT/tls/cert.pem" \
  -out "$STATE_ROOT/tls/controller.pfx" >/dev/null 2>&1
rm -f "$STATE_ROOT/tls/key.pem" "$STATE_ROOT/tls/cert.pem"
chown fogell:fogell "$STATE_ROOT/tls/controller.pfx"
chmod 0600 "$STATE_ROOT/tls/controller.pfx"
openssl rand -hex 32 > "$STATE_ROOT/api-token"
chown fogell:fogell "$STATE_ROOT/api-token"
chmod 0600 "$STATE_ROOT/api-token"

install -d -o root -g fogell -m 0750 /etc/fogell
cat > /etc/fogell/controller.env <<EOF
DOTNET_ROOT=$INSTALL_ROOT/dotnet
FOGELL_DATABASE_URL=Host=127.0.0.1;Username=fogell_runtime;Database=fogell;Maximum Pool Size=16;Timeout=3;Command Timeout=5
FOGELL_MAINTENANCE_DATABASE_URL=Host=127.0.0.1;Username=postgres;Database=fogell;Timeout=3;Command Timeout=5
FOGELL_API_TOKEN_FILE=$STATE_ROOT/api-token
FOGELL_LISTEN_URL=https://0.0.0.0:8080
ASPNETCORE_Kestrel__Certificates__Default__Path=$STATE_ROOT/tls/controller.pfx
FOGELL_STATE_ROOT=$STATE_ROOT
FOGELL_RUN_HOST_PATH=$INSTALL_ROOT/runner/Fogell.Run.Host
FOGELL_LOCAL_TRUST_POOL=trusted-linux
FOGELL_MAX_PIPELINE_BYTES=65536
FOGELL_MAX_LOG_CHUNKS=20
FOGELL_WORKER_POLL_MS=100
FOGELL_WORKER_LEASE_SECONDS=10
FOGELL_STORAGE_POOL_ID=$POOL_ID
FOGELL_STORAGE_POOL_MAX_BYTES=$POOL_BYTES
FOGELL_STORAGE_POOL_MAX_INODES=$POOL_INODES
FOGELL_STORAGE_POOL_MIN_FREE_BYTES=$MIN_FREE_BYTES
FOGELL_STORAGE_POOL_MIN_FREE_INODES=$MIN_FREE_INODES
DOTNET_EnableDiagnostics=0
EOF
chown root:fogell /etc/fogell/controller.env
chmod 0640 /etc/fogell/controller.env

cat > /etc/systemd/system/fogell-controller.service <<EOF
[Unit]
Description=Fogell bounded-pool controller (manual guest test service)
After=postgresql.service
Requires=postgresql.service

[Service]
Type=simple
User=fogell
Group=fogell
WorkingDirectory=$STATE_ROOT
EnvironmentFile=/etc/fogell/controller.env
ExecStart=$INSTALL_ROOT/controller/Fogell.Controller.Host
Restart=no
NoNewPrivileges=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl disable fogell-controller.service >/dev/null 2>&1 || true

# First manual start applies maintenance migrations. Runtime table access then
# intentionally fails until the narrow runtime grants below exist. It is not
# enabled, and it is stopped/reset before this setup succeeds.
systemctl start fogell-controller.service || true
for _ in $(seq 1 300); do
  case "$(systemctl show --property=ActiveState --value fogell-controller.service)" in
    inactive|failed) break ;;
  esac
  sleep 0.1
done
case "$(systemctl show --property=ActiveState --value fogell-controller.service)" in
  inactive|failed) ;;
  *) fail "initial controller did not stop after migration capability check" ;;
esac

sudo -u postgres psql -X -v ON_ERROR_STOP=1 -d fogell <<'SQL' >/dev/null
GRANT USAGE ON SCHEMA public TO fogell_runtime;
GRANT SELECT, UPDATE(singleton) ON controller_metadata TO fogell_runtime;
GRANT SELECT ON organization_work_roots TO fogell_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes, attempts, events, outbox, log_chunks, effect_checkpoints, retry_decisions, build_definitions TO fogell_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO fogell_runtime;
SQL
systemctl reset-failed fogell-controller.service

echo "guest-setup: complete; controller remains disabled and stopped" >&2
echo "guest-setup: start manually with: systemctl start fogell-controller.service" >&2
