#!/usr/bin/env bash
# Start and stop the disposable PostgreSQL used by hosted gate jobs. The host
# port is allocated by the runtime, never shared by convention: run 33693320962
# failed before checkout because its fixed 55440 service port was already in use.
set -Eeuo pipefail

runtime=${FOGELL_CONTAINER_RUNTIME:-podman}
image=${FOGELL_PG_IMAGE:-docker.io/library/postgres:16}

die() {
  printf 'CI POSTGRES REFUSED: %s\n' "$*" >&2
  exit 1
}

[[ "$runtime" = podman || "$runtime" = docker ]] \
  || die "FOGELL_CONTAINER_RUNTIME must be exactly podman or docker"
command -v "$runtime" >/dev/null 2>&1 || die "$runtime is unavailable"

case "${1:-}" in
  start)
    [[ $# -eq 1 ]] || die "start takes no arguments"
    token="${GITHUB_RUN_ID:-local}-${GITHUB_JOB:-job}-${GITHUB_RUN_ATTEMPT:-1}-$$-$RANDOM"
    token=${token//[^A-Za-z0-9_.-]/-}
    container="fogell-gate-postgres-$token"
    started=0
    cleanup_failed_start() {
      if (( started )); then "$runtime" rm -f "$container" >/dev/null 2>&1 || true; fi
    }
    trap cleanup_failed_start EXIT

    "$runtime" run --detach --rm --name "$container" \
      --publish 127.0.0.1::5432 \
      --env POSTGRES_USER=fogell \
      --env POSTGRES_DB=fogell \
      --env POSTGRES_HOST_AUTH_METHOD=trust \
      --health-cmd 'pg_isready -U fogell -d fogell' \
      --health-interval 1s \
      --health-timeout 5s \
      --health-retries 60 \
      "$image" >/dev/null
    started=1

    mapping=$("$runtime" port "$container" 5432/tcp) \
      || die "could not read the allocated PostgreSQL host port"
    mapping_pattern='^(5432/tcp[[:space:]]+->[[:space:]]+)?127\.0\.0\.1:([0-9]+)$'
    [[ "$mapping" =~ $mapping_pattern ]] \
      || die "unexpected PostgreSQL port mapping: $mapping"
    port=${BASH_REMATCH[2]}

    ready=0
    for _ in $(seq 1 60); do
      if "$runtime" exec "$container" pg_isready -U fogell -d fogell >/dev/null 2>&1; then
        ready=1
        break
      fi
      sleep 1
    done
    (( ready )) || die "PostgreSQL did not become ready within 60 seconds"

    connection="Host=127.0.0.1;Port=$port;Username=fogell;Database=fogell"
    if [[ -n "${GITHUB_ENV:-}" ]]; then
      {
        printf 'FOGELL_PG_CONTAINER=%s\n' "$container"
        printf 'FOGELL_PG_PORT=%s\n' "$port"
        printf 'FOGELL_TEST_DATABASE_URL=%s\n' "$connection"
      } >>"$GITHUB_ENV"
    fi
    printf 'CI POSTGRES READY: %s on dynamically allocated port %s via %s\n' "$container" "$port" "$runtime"
    if [[ -z "${GITHUB_ENV:-}" ]]; then
      printf '  export FOGELL_PG_CONTAINER=%q\n' "$container"
      printf '  export FOGELL_PG_PORT=%q\n' "$port"
      printf '  export FOGELL_TEST_DATABASE_URL=%q\n' "$connection"
    fi
    started=0
    ;;
  stop)
    [[ $# -eq 1 ]] || die "stop takes no arguments"
    container=${FOGELL_PG_CONTAINER:-}
    [[ "$container" =~ ^fogell-gate-postgres-[A-Za-z0-9_.-]+$ ]] \
      || die "FOGELL_PG_CONTAINER is outside the disposable gate namespace"
    "$runtime" rm -f "$container" >/dev/null 2>&1 || true
    printf 'CI POSTGRES STOPPED: %s via %s\n' "$container" "$runtime"
    ;;
  *)
    die "usage: scripts/ci-postgres.sh start|stop"
    ;;
esac
