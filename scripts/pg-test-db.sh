#!/usr/bin/env bash
# Real PostgreSQL for the store tests. ADR 0007's properties are database
# properties; a mock proves nothing about a unique constraint arbitrating a race.
set -uo pipefail
NAME=${1:-fogell-pg}
PORT=${2:-55440}
podman rm -f "$NAME" >/dev/null 2>&1
podman run -d --name "$NAME" -p "127.0.0.1:${PORT}:5432" \
  -e POSTGRES_USER=fogell -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=fogell \
  docker.io/library/postgres:16 >/dev/null
for i in $(seq 1 60); do
  podman exec "$NAME" pg_isready -U fogell -d fogell >/dev/null 2>&1 && {
    echo "postgres ready on ${PORT} (~${i}s)"
    echo "  export FOGELL_TEST_DATABASE_URL='Host=127.0.0.1;Port=${PORT};Username=fogell;Database=fogell'"
    exit 0; }
  sleep 1
done
echo "postgres did not become ready" >&2; exit 1
