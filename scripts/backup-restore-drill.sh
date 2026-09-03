#!/usr/bin/env bash
# FG-085a — a bounded, destructive-only-to-self PostgreSQL backup/restore drill.
#
# This proves one narrow property: the current Fogell schema and a deliberately
# non-trivial fixture survive a PostgreSQL custom-archive round trip with equal
# canonical schema, data, and sequence inventories. It does not claim dump-byte
# reproducibility, PITR, encryption, off-host retention, a production runbook,
# or production-scale restore time.
set -euo pipefail
export LC_ALL=C

die() {
  printf 'FG-085a REFUSED: %s\n' "$*" >&2
  exit 1
}

valid_database_name() {
  [[ "${1:-}" =~ ^fogell_fg085a_[a-z0-9_]+_(source|target)$ ]]
}

if [[ "${1:-}" == "--validate-db-name" ]]; then
  [[ $# -eq 2 ]] || die "--validate-db-name requires exactly one value"
  valid_database_name "$2" || die "database name is outside the private drill namespace: $2"
  printf 'VALID %s\n' "$2"
  exit 0
elif [[ $# -ne 0 ]]; then
  die "unexpected arguments"
fi

runtime="${FOGELL_CONTAINER_RUNTIME:-podman}"
container="${FOGELL_PG_CONTAINER:-}"
pg_user="${FOGELL_PG_USER:-fogell}"
admin_db="${FOGELL_PG_ADMIN_DB:-fogell}"

[[ "$runtime" == "docker" || "$runtime" == "podman" ]] \
  || die "FOGELL_CONTAINER_RUNTIME must be exactly docker or podman"
command -v "$runtime" >/dev/null 2>&1 || die "$runtime is not available"
[[ "$container" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] \
  || die "FOGELL_PG_CONTAINER is required and must be a literal container name or id"
[[ "$pg_user" =~ ^[a-z_][a-z0-9_]*$ ]] || die "unsafe PostgreSQL user name"
[[ "$admin_db" =~ ^[a-z_][a-z0-9_]*$ ]] || die "unsafe PostgreSQL administrative database name"
[[ "${FG085_TEST_CORRUPT_AFTER_HASH:-0}" == "0" || "${FG085_TEST_CORRUPT_AFTER_HASH:-0}" == "1" ]] \
  || die "FG085_TEST_CORRUPT_AFTER_HASH must be 0 or 1"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="${FG085_REPO_ROOT:-$(cd "$script_dir/.." && pwd)}"
[[ "$repo_root" == /* && -d "$repo_root" ]] || die "FG085_REPO_ROOT must be an existing absolute directory"
migration_root="$repo_root/src/Fogell.Store/migrations"
[[ -d "$migration_root" ]] || die "migration directory is missing"

run_tool() {
  "$runtime" exec -i "$container" "$@"
}

psql_admin() {
  run_tool psql -X -q -A -t -v ON_ERROR_STOP=1 -U "$pg_user" -d "$admin_db" "$@"
}

psql_db() {
  local database=$1
  shift
  valid_database_name "$database" || die "refusing PostgreSQL access outside drill namespace: $database"
  run_tool psql -X -A -t -v ON_ERROR_STOP=1 -U "$pg_user" -d "$database" "$@"
}

sha256_file() {
  local path=$1
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$path" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$path" | awk '{print $1}'
  else
    die "no SHA-256 utility is available"
  fi
}

run_token="$(date -u +%Y%m%d%H%M%S)_$$_${RANDOM}"
[[ "$run_token" =~ ^[0-9_]+$ ]] || die "internal run token is malformed"
source_db="fogell_fg085a_${run_token}_source"
target_db="fogell_fg085a_${run_token}_target"
valid_database_name "$source_db" || die "internal source name failed its namespace guard"
valid_database_name "$target_db" || die "internal target name failed its namespace guard"
[[ "$source_db" != "$target_db" ]] || die "source and target database names must differ"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/fogell-fg085a.XXXXXX")"
archive="$scratch/backup.custom"
archive_list="$scratch/archive.list"
source_schema="$scratch/source.schema"
target_schema="$scratch/target.schema"
source_data="$scratch/source.data"
target_data="$scratch/target.data"
source_sequences="$scratch/source.sequences"
target_sequences="$scratch/target.sequences"
data_raw="$scratch/data.raw"
schema_raw="$scratch/schema.raw"
created_source=0
created_target=0
completed=0
archive_sha_initial=""
schema_sha=""
data_sha=""
sequences_sha=""

drop_drill_database() {
  local database=$1
  valid_database_name "$database" || return 97
  psql_admin -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$database' AND pid <> pg_backend_pid()" >/dev/null \
    && psql_admin -c "DROP DATABASE $database" >/dev/null
}

cleanup() {
  local original_rc=$?
  local cleanup_rc=0
  trap - EXIT INT TERM
  set +e

  if [[ "$created_target" -eq 1 ]]; then
    drop_drill_database "$target_db" || cleanup_rc=1
  fi
  if [[ "$created_source" -eq 1 ]]; then
    drop_drill_database "$source_db" || cleanup_rc=1
  fi

  rm -f -- "$archive" "$archive_list" "$data_raw" "$schema_raw" \
    "$source_schema" "$target_schema" "$source_data" "$target_data" \
    "$source_sequences" "$target_sequences" || cleanup_rc=1
  rmdir -- "$scratch" || cleanup_rc=1

  if [[ "$cleanup_rc" -ne 0 ]]; then
    printf 'FG-085a REFUSED: cleanup did not remove only the two validated drill databases and exact scratch files\n' >&2
    exit 1
  fi
  if [[ "$original_rc" -eq 0 && "$completed" -ne 1 ]]; then
    printf 'FG-085a REFUSED: drill exited before completing every comparison\n' >&2
    exit 1
  fi
  if [[ "$original_rc" -eq 0 ]]; then
    printf 'FG-085a PASS server-major=%s archive-sha256=%s schema-sha256=%s data-sha256=%s sequences-sha256=%s\n' \
      "$server_major" "$archive_sha_initial" "$schema_sha" "$data_sha" "$sequences_sha"
  fi
  exit "$original_rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

server_version_num="$(psql_admin -c 'SHOW server_version_num' | tr -d '[:space:]')"
[[ "$server_version_num" =~ ^[0-9]{6}$ ]] || die "server_version_num is not a six-digit PostgreSQL version"
server_major=$((10#$server_version_num / 10000))

tool_major() {
  local tool=$1
  local version
  version="$(run_tool "$tool" --version)" || die "$tool --version failed"
  [[ "$version" =~ ([0-9]+)\. ]] || die "cannot parse $tool major version"
  printf '%s\n' "${BASH_REMATCH[1]}"
}

dump_major="$(tool_major pg_dump)"
restore_major="$(tool_major pg_restore)"
psql_major="$(tool_major psql)"
[[ "$dump_major" -eq "$server_major" && "$restore_major" -eq "$server_major" && "$psql_major" -eq "$server_major" ]] \
  || die "tool/server major mismatch (server=$server_major psql=$psql_major pg_dump=$dump_major pg_restore=$restore_major)"

existing_count="$(psql_admin -c "SELECT count(*) FROM pg_database WHERE datname IN ('$source_db', '$target_db')" | tr -d '[:space:]')"
[[ "$existing_count" == "0" ]] || die "a generated drill database already exists"

psql_admin -c "CREATE DATABASE $source_db" >/dev/null
created_source=1

psql_db "$source_db" -c \
  "CREATE TABLE schema_migrations (version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT clock_timestamp())" \
  >/dev/null

shopt -s nullglob
migrations=("$migration_root"/*.sql)
shopt -u nullglob
[[ "${#migrations[@]}" -gt 0 ]] || die "no migration files were found"
declare -A seen_versions=()
for migration in "${migrations[@]}"; do
  filename="$(basename "$migration")"
  [[ "$filename" =~ ^([0-9]{4})_[a-z0-9_]+\.sql$ ]] || die "malformed migration filename: $filename"
  version="${BASH_REMATCH[1]}"
  [[ -z "${seen_versions[$version]+x}" ]] || die "duplicate migration version: $version"
  seen_versions[$version]=1
  checksum="$(sha256_file "$migration")"
  [[ "$checksum" =~ ^[0-9a-f]{64}$ ]] || die "migration checksum is malformed"
  psql_db "$source_db" < "$migration" >/dev/null
  psql_db "$source_db" -c \
    "INSERT INTO schema_migrations (version, checksum, applied_at) VALUES ('$version', '$checksum', '2026-08-24T00:00:00.000000Z')" \
    >/dev/null
done

psql_db "$source_db" -v runtime="$runtime" >/dev/null <<'SQL'
UPDATE controller_metadata SET restore_epoch = 2 WHERE singleton;
INSERT INTO organizations (id, slug)
VALUES ('10000000-0000-0000-0000-000000000001', 'fg085a-org');
INSERT INTO projects (id, organization_id, slug)
VALUES ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 'fg085a-project');
INSERT INTO builds
  (id, organization_id, project_id, number, idempotency_key, status,
   cancellation_requested, next_log_sequence, created_at)
VALUES
  ('30000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001',
   '20000000-0000-0000-0000-000000000001', 7, 'fg085a-key', 'running', true, 10,
   '2026-08-24T01:02:03.456789Z');
INSERT INTO nodes
  (id, organization_id, build_id, name, ordinal, required_trust_pool, required_capabilities, status)
VALUES
  ('40000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001',
   '30000000-0000-0000-0000-000000000001', 'deploy', 0, 'trusted-linux',
   ARRAY['linux', :'runtime'], 'running');
INSERT INTO attempts
  (id, organization_id, node_id, ordinal, retry_of, state, fence, restore_epoch,
   lease_owner, lease_expires_at, result, created_at)
VALUES
  ('50000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001',
   '40000000-0000-0000-0000-000000000001', 0, NULL, 'terminal', 3, 2,
   NULL, NULL, 'failure', '2026-08-24T01:03:00.000001Z'),
  ('50000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001',
   '40000000-0000-0000-0000-000000000001', 1,
   '50000000-0000-0000-0000-000000000001', 'running', 4, 2,
   'agent-17', '2026-08-24T02:03:00.000001Z', NULL, '2026-08-24T01:04:00.000001Z');
INSERT INTO events (organization_id, build_id, attempt_id, kind, payload, created_at)
VALUES
  ('10000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001',
   '50000000-0000-0000-0000-000000000002', 'effect.fixture',
   '{"flags":[true,false],"nested":{"attempt":2},"text":"fogell"}'::jsonb,
   '2026-08-24T01:05:00.123456Z');
INSERT INTO outbox (organization_id, topic, body, published_at, created_at)
VALUES
  ('10000000-0000-0000-0000-000000000001', 'build.fixture',
   '{"build":"30000000-0000-0000-0000-000000000001","ordinal":7}'::jsonb,
   '2026-08-24T01:06:00.123456Z', '2026-08-24T01:05:30.123456Z');
INSERT INTO log_chunks
  (organization_id, build_id, attempt_id, sequence, build_sequence, body, created_at)
VALUES
  ('10000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001',
   '50000000-0000-0000-0000-000000000002', 9, 9, 'quoted '' value | unicode π',
   '2026-08-24T01:07:00.123456Z');
SELECT setval(pg_get_serial_sequence('events', 'id'), 41, true);
SELECT setval(pg_get_serial_sequence('outbox', 'id'), 52, true);
SELECT setval(pg_get_serial_sequence('log_chunks', 'id'), 63, true);
SQL

run_tool pg_dump -U "$pg_user" -d "$source_db" --format=custom > "$archive" || die "pg_dump failed"
[[ -s "$archive" ]] || die "pg_dump produced an empty archive"
archive_sha_initial="$(sha256_file "$archive")"
[[ "$archive_sha_initial" =~ ^[0-9a-f]{64}$ ]] || die "archive SHA-256 is malformed"
run_tool pg_restore --list < "$archive" > "$archive_list" || die "custom archive is corrupt or truncated"
[[ -s "$archive_list" ]] || die "pg_restore listed an empty archive"

psql_admin -c "CREATE DATABASE $target_db" >/dev/null
created_target=1
target_relation_count="$(psql_db "$target_db" -c \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_toast' AND c.relkind IN ('r','p','S','v','m','f')" \
  | tr -d '[:space:]')"
[[ "$target_relation_count" == "0" ]] || die "restore target is not empty"

if [[ "${FG085_TEST_CORRUPT_AFTER_HASH:-0}" == "1" ]]; then
  printf 'X' >> "$archive"
fi
archive_sha_final="$(sha256_file "$archive")"
[[ "$archive_sha_final" == "$archive_sha_initial" ]] || die "archive bytes changed after their SHA-256 was recorded"

run_tool pg_restore -U "$pg_user" -d "$target_db" --exit-on-error --single-transaction < "$archive" >/dev/null \
  || die "pg_restore failed"

normalize_schema_transport() {
  local input=$1
  local output=$2
  awk '
    /^\\restrict / {
      if ($0 !~ /^\\restrict [A-Za-z0-9]+$/ || seen_restrict) exit 41
      token = $0; sub(/^\\restrict /, "", token)
      seen_restrict = 1
      print "\\restrict FG085A_TRANSPORT_KEY"
      next
    }
    /^\\unrestrict / {
      if ($0 !~ /^\\unrestrict [A-Za-z0-9]+$/ || !seen_restrict || seen_unrestrict) exit 42
      closing = $0; sub(/^\\unrestrict /, "", closing)
      if (closing != token) exit 43
      seen_unrestrict = 1
      print "\\unrestrict FG085A_TRANSPORT_KEY"
      next
    }
    { print }
    END { if (seen_restrict != seen_unrestrict) exit 44 }
  ' "$input" > "$output" || die "schema dump has malformed transport guards"
}

capture_schema() {
  local database=$1
  local output=$2
  valid_database_name "$database" || die "schema inventory database escaped the drill namespace"
  run_tool pg_dump -U "$pg_user" -d "$database" --schema-only --format=plain \
    --encoding=UTF8 --quote-all-identifiers > "$schema_raw"
  normalize_schema_transport "$schema_raw" "$output"
  rm -f -- "$schema_raw"
  [[ -s "$output" ]] || die "schema inventory is empty for $database"
}

capture_data() {
  local database=$1
  local output=$2
  valid_database_name "$database" || die "data inventory database escaped the drill namespace"
  run_tool pg_dump -U "$pg_user" -d "$database" --data-only --inserts --column-inserts \
    --rows-per-insert=1 --encoding=UTF8 --quote-all-identifiers > "$data_raw"
  awk '/^INSERT INTO / { print }' "$data_raw" | LC_ALL=C sort > "$output"
  rm -f -- "$data_raw"
  [[ -s "$output" ]] || die "data inventory is empty for $database"
}

capture_sequences() {
  local database=$1
  local output=$2
  valid_database_name "$database" || die "sequence inventory database escaped the drill namespace"
  psql_db "$database" > "$output" <<'SQL'
SET search_path = pg_catalog;
SET timezone = 'UTC';
SELECT format(
  'SELECT %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || last_value::text || E''\t'' || is_called::text FROM %I.%I',
  n.nspname, c.relname, s.seqstart::text, s.seqincrement::text,
  s.seqmax::text, s.seqmin::text, s.seqcache::text, s.seqcycle::text,
  n.nspname, c.relname)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
JOIN pg_sequence s ON s.seqrelid = c.oid
WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C"
\gexec
SQL
  [[ -s "$output" ]] || die "sequence inventory is empty for $database"
}

capture_schema "$source_db" "$source_schema"
capture_schema "$target_db" "$target_schema"
capture_data "$source_db" "$source_data"
capture_data "$target_db" "$target_data"
capture_sequences "$source_db" "$source_sequences"
capture_sequences "$target_db" "$target_sequences"

cmp -s "$source_schema" "$target_schema" || die "canonical schema inventory differs after restore"
cmp -s "$source_data" "$target_data" || die "canonical data inventory differs after restore"
cmp -s "$source_sequences" "$target_sequences" || die "canonical sequence inventory differs after restore"

schema_sha="$(sha256_file "$source_schema")"
data_sha="$(sha256_file "$source_data")"
sequences_sha="$(sha256_file "$source_sequences")"
[[ "$schema_sha" == "$(sha256_file "$target_schema")" ]] || die "schema SHA-256 differs after byte comparison"
[[ "$data_sha" == "$(sha256_file "$target_data")" ]] || die "data SHA-256 differs after byte comparison"
[[ "$sequences_sha" == "$(sha256_file "$target_sequences")" ]] || die "sequence SHA-256 differs after byte comparison"

completed=1
