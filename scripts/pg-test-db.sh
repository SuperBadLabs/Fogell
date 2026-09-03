#!/usr/bin/env bash
# Real PostgreSQL for the store tests. ADR 0007's properties are database
# properties; a mock proves nothing about a unique constraint arbitrating a race.
set -Eeuo pipefail

runtime=${FOGELL_CONTAINER_RUNTIME:-podman}
image=${FOGELL_PG_IMAGE:-docker.io/library/postgres:16}
[[ "$runtime" = podman || "$runtime" = docker ]] \
  || { echo "FOGELL_CONTAINER_RUNTIME must be exactly podman or docker" >&2; exit 1; }
command -v "$runtime" >/dev/null 2>&1 || { echo "$runtime is unavailable" >&2; exit 1; }
[[ $# -le 2 ]] || { echo "usage: $0 [container-name [host-port]]" >&2; exit 1; }
NAME=${1:-fogell-pg}
PORT=${2:-}
[[ "$NAME" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] \
  || { echo "container name must be a literal name, not an option" >&2; exit 1; }

if [[ -n "$PORT" ]]; then
  [[ "$PORT" =~ ^[0-9]+$ ]] \
    || { echo "host port must be an integer from 1 through 65535" >&2; exit 1; }
  port_number=$((10#$PORT))
  (( port_number >= 1 && port_number <= 65535 )) \
    || { echo "host port must be an integer from 1 through 65535" >&2; exit 1; }
  publish="127.0.0.1:${PORT}:5432"
else
  publish="127.0.0.1::5432"
fi

started=0
cleanup_failed_start() {
  if (( started )); then "$runtime" rm -f "$NAME" >/dev/null 2>&1 || true; fi
}
trap cleanup_failed_start EXIT

"$runtime" rm -f "$NAME" >/dev/null 2>&1 || true
started=1
"$runtime" run -d --name "$NAME" -p "$publish" \
  -e POSTGRES_USER=fogell -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=fogell \
  "$image" >/dev/null

if [[ -z "$PORT" ]]; then
  mapping=$("$runtime" port "$NAME" 5432/tcp) || {
    echo "postgres did not expose a loopback host port" >&2
    exit 1
  }
  mapping_pattern='^(5432/tcp[[:space:]]+->[[:space:]]+)?127\.0\.0\.1:([0-9]+)$'
  [[ "$mapping" =~ $mapping_pattern ]] || {
    echo "postgres reported an unexpected port mapping: $mapping" >&2
    exit 1
  }
  PORT=${BASH_REMATCH[2]}
fi

for i in $(seq 1 60); do
  "$runtime" exec "$NAME" pg_isready -U fogell -d fogell >/dev/null 2>&1 && {
    trap - EXIT
    echo "postgres ready on ${PORT} (~${i}s)"
    echo "  export FOGELL_TEST_DATABASE_URL='Host=127.0.0.1;Port=${PORT};Username=fogell;Database=fogell'"
    exit 0; }
  sleep 1
done
echo "postgres did not become ready" >&2; exit 1
