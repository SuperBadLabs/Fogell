#!/usr/bin/env bash
# Bring up a PERSISTENT Fogell.Controller.Host, following the provisioning order
# in scripts/prove-runnable-controller.sh: create db+role, run the controller
# once so migrations create the schema, then GRANT the runtime surface, seed
# org/project, then launch for real.
set -uo pipefail
R=$HOME/fogell-ctrl
PGC=fogell-crash-pg
PGPORT=55450
DB=fogell_crash
ROLE=fogell_crash_runtime
ORG=$(cat /proc/sys/kernel/random/uuid)
PROJ=$(cat /proc/sys/kernel/random/uuid)
LISTEN=http://127.0.0.1:18095

mkdir -p "$R/state"
admin() { podman exec "$PGC" psql -U fogell -d "$1" -v ON_ERROR_STOP=1 "${@:2}"; }

podman rm -f "$PGC" >/dev/null 2>&1
podman run -d --name "$PGC" -p 127.0.0.1:$PGPORT:5432 \
  -e POSTGRES_USER=fogell -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=fogell \
  docker.io/library/postgres:16 >/dev/null
for i in $(seq 1 60); do podman exec "$PGC" pg_isready -U fogell >/dev/null 2>&1 && break; sleep 1; done

admin postgres -c "CREATE DATABASE $DB" >/dev/null
admin postgres -c "CREATE ROLE $ROLE NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS" >/dev/null

MAINT="Host=127.0.0.1;Port=$PGPORT;Username=fogell;Password=fogell;Database=$DB"
RUNTIME="$MAINT;Options=-c role=$ROLE;No Reset On Close=true;Maximum Pool Size=8"
TOKENF="$R/token"
printf '%s' 'fogell-crash-token-0123456789abcdef' > "$TOKENF"; chmod 400 "$TOKENF"

cat > "$R/env" <<ENV
export FOGELL_DATABASE_URL='$RUNTIME'
export FOGELL_MAINTENANCE_DATABASE_URL='$MAINT'
export FOGELL_API_TOKEN_FILE=$TOKENF
export FOGELL_LISTEN_URL=$LISTEN
export FOGELL_STATE_ROOT=$R/state
export FOGELL_RUN_HOST_PATH=$R/runhost/Fogell.Run.Host
export FOGELL_LOCAL_TRUST_POOL=trusted-linux
export FOGELL_MAX_PIPELINE_BYTES=16384
export FOGELL_MAX_LOG_CHUNKS=100
export FOGELL_WORKER_POLL_MS=50
export FOGELL_WORKER_LEASE_SECONDS=60
export FOGELL_ORG=$ORG
export FOGELL_PROJECT=$PROJ
export FOGELL_BUILDS_URL=$LISTEN/api/v1/organizations/$ORG/projects/$PROJ/builds
export FOGELL_TOKEN=fogell-crash-token-0123456789abcdef
ENV

# Pass 1: migrations. The controller binds and serves, so bound it and kill it.
source "$R/env"
timeout 25 "$R/controller/Fogell.Controller.Host" > "$R/migrate.log" 2>&1
echo "migrate pass: $(tail -1 "$R/migrate.log" | cut -c1-100)"

admin "$DB" \
  -c "GRANT USAGE ON SCHEMA public TO $ROLE" \
  -c "GRANT SELECT, UPDATE(singleton) ON controller_metadata TO $ROLE" \
  -c "GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes, attempts, events, outbox, log_chunks, effect_checkpoints, retry_decisions, build_definitions TO $ROLE" \
  -c "GRANT SELECT ON organization_work_roots TO $ROLE" \
  -c "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $ROLE" >/dev/null && echo "grants applied"
admin "$DB" \
  -c "INSERT INTO organizations (id, slug) VALUES ('$ORG','crash-org')" \
  -c "INSERT INTO projects (id, organization_id, slug) VALUES ('$PROJ','$ORG','crash-project')" >/dev/null && echo "org/project seeded"

bash "$HOME/fogell-ctrl/ctrl-start.sh"
