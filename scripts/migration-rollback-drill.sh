#!/usr/bin/env bash
# FG-081 — rehearse the newest migration forward, restore-based rollback, and
# the same forward migration again against real PostgreSQL.
set -euo pipefail
export LC_ALL=C

die() {
  printf 'FG-081 REFUSED: %s\n' "$*" >&2
  exit 1
}

valid_database_name() {
  [[ "${1:-}" =~ ^fogell_fg081_[0-9_]+_(primary|audit_(pre|forward1|rollback|forward2))$ ]]
}

if [[ "${1:-}" == "--validate-db-name" ]]; then
  [[ $# -eq 2 ]] || die "--validate-db-name requires exactly one value"
  valid_database_name "$2" || die "database name is outside the private rehearsal namespace: $2"
  printf 'VALID %s\n' "$2"
  exit 0
elif [[ $# -ne 0 ]]; then
  die "unexpected arguments"
fi

runtime="${FOGELL_CONTAINER_RUNTIME:-podman}"
container="${FOGELL_PG_CONTAINER:-}"
pg_user="${FOGELL_PG_USER:-fogell}"
admin_db="${FOGELL_PG_ADMIN_DB:-fogell}"
[[ "$runtime" == "docker" || "$runtime" == "podman" ]] || die "container runtime must be docker or podman"
command -v "$runtime" >/dev/null 2>&1 || die "$runtime is unavailable"
[[ "$container" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] || die "FOGELL_PG_CONTAINER must be a literal container name or id"
[[ "$pg_user" =~ ^[a-z_][a-z0-9_]*$ ]] || die "unsafe PostgreSQL user"
[[ "$admin_db" =~ ^[a-z_][a-z0-9_]*$ ]] || die "unsafe administrative database"

for fault in \
  FG081_TEST_CORRUPT_ARCHIVE_AFTER_HASH \
  FG081_TEST_CORRUPT_ROLLBACK_DATA \
  FG081_TEST_DROP_FORWARD_FK \
  FG081_TEST_SKIP_SECOND_FORWARD; do
  value="${!fault:-0}"
  [[ "$value" == "0" || "$value" == "1" ]] || die "$fault must be 0 or 1"
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="${FG081_REPO_ROOT:-$(cd "$script_dir/.." && pwd)}"
[[ "$repo_root" == /* && -d "$repo_root" ]] || die "FG081_REPO_ROOT must be an existing absolute directory"
migration_root="$repo_root/src/Fogell.Store/migrations"
[[ -d "$migration_root" ]] || die "migration directory is missing"

run_tool() { "$runtime" exec -i "$container" "$@"; }
psql_admin() { run_tool psql -X -q -A -t -v ON_ERROR_STOP=1 -U "$pg_user" -d "$admin_db" "$@"; }
psql_db() {
  local database=$1
  shift
  valid_database_name "$database" || die "database escaped rehearsal namespace: $database"
  run_tool psql -X -q -A -t -v ON_ERROR_STOP=1 -U "$pg_user" -d "$database" "$@"
}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | awk '{print $1}'
  else die "no SHA-256 utility is available"
  fi
}

token="$(date -u +%Y%m%d%H%M%S)_$$_${RANDOM}"
[[ "$token" =~ ^[0-9_]+$ ]] || die "internal token is malformed"
primary_db="fogell_fg081_${token}_primary"
valid_database_name "$primary_db" || die "generated primary database name is unsafe"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/fogell-fg081.XXXXXX")"
case "$scratch" in
  "${TMPDIR:-/tmp}"/fogell-fg081.*) ;;
  *) die "unsafe scratch path: $scratch" ;;
esac
declare -A created=()
completed=0
validated_hash=""

drop_database() {
  local database=$1
  valid_database_name "$database" || return 97
  psql_admin -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$database' AND pid <> pg_backend_pid()" >/dev/null
  psql_admin -c "DROP DATABASE $database" >/dev/null
  unset "created[$database]"
}

create_database() {
  local database=$1
  valid_database_name "$database" || die "refusing to create unsafe database: $database"
  local present
  present="$(psql_admin -c "SELECT count(*) FROM pg_database WHERE datname = '$database'" | tr -d '[:space:]')"
  [[ "$present" == "0" ]] || die "generated database already exists: $database"
  psql_admin -c "CREATE DATABASE $database" >/dev/null
  created["$database"]=1
}

cleanup() {
  local original_rc=$?
  local cleanup_rc=0
  trap - EXIT INT TERM
  set +e
  for database in "${!created[@]}"; do
    drop_database "$database" || cleanup_rc=1
  done
  case "$scratch" in
    "${TMPDIR:-/tmp}"/fogell-fg081.*) rm -rf -- "$scratch" || cleanup_rc=1 ;;
    *) cleanup_rc=1 ;;
  esac
  if [[ "$cleanup_rc" -ne 0 ]]; then
    printf 'FG-081 REFUSED: cleanup did not remove only owned databases and scratch\n' >&2
    exit 1
  fi
  if [[ "$original_rc" -eq 0 && "$completed" -ne 1 ]]; then
    printf 'FG-081 REFUSED: drill exited before every phase completed\n' >&2
    exit 1
  fi
  exit "$original_rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

server_version_num="$(psql_admin -c 'SHOW server_version_num' | tr -d '[:space:]')"
[[ "$server_version_num" =~ ^[0-9]{6}$ ]] || die "server version is malformed"
server_major=$((10#$server_version_num / 10000))
for tool in psql pg_dump pg_restore; do
  version="$(run_tool "$tool" --version)" || die "$tool --version failed"
  [[ "$version" =~ ([0-9]+)\. ]] || die "cannot parse $tool version"
  [[ "${BASH_REMATCH[1]}" -eq "$server_major" ]] || die "$tool/server major mismatch"
done

shopt -s nullglob
migrations=("$migration_root"/*.sql)
shopt -u nullglob
[[ "${#migrations[@]}" -ge 2 ]] || die "at least two migrations are required"
declare -A seen_versions=()
prior_number=0
versions=()
checksums=()
for migration in "${migrations[@]}"; do
  filename="$(basename "$migration")"
  [[ "$filename" =~ ^([0-9]{4})_[a-z0-9_]+\.sql$ ]] || die "malformed migration filename: $filename"
  version="${BASH_REMATCH[1]}"
  number=$((10#$version))
  [[ "$number" -eq $((prior_number + 1)) ]] || die "migration versions are not contiguous at $version"
  [[ -z "${seen_versions[$version]+x}" ]] || die "duplicate migration version: $version"
  seen_versions["$version"]=1
  versions+=("$version")
  checksum="$(sha256_file "$migration")"
  [[ "$checksum" =~ ^[0-9a-f]{64}$ ]] || die "malformed migration checksum"
  checksums+=("$checksum")
  prior_number=$number
done
latest_index=$((${#migrations[@]} - 1))
latest_version="${versions[$latest_index]}"
previous_version="${versions[$((latest_index - 1))]}"

apply_migration() {
  local database=$1 index=$2
  psql_db "$database" < "${migrations[$index]}" >/dev/null
  psql_db "$database" -c \
    "INSERT INTO schema_migrations (version, checksum, applied_at) VALUES ('${versions[$index]}', '${checksums[$index]}', '2026-08-26T00:00:00Z')" >/dev/null
}

create_database "$primary_db"
psql_db "$primary_db" -c \
  "CREATE TABLE schema_migrations (version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT clock_timestamp())" >/dev/null
for ((index=0; index<latest_index; index++)); do apply_migration "$primary_db" "$index"; done

# The pre-upgrade fixture reaches every tenant-bearing table available at N-1,
# including live effect authority and an immutable retry decision.
psql_db "$primary_db" -v runtime="$runtime" >/dev/null <<'SQL'
UPDATE controller_metadata SET restore_epoch = 2 WHERE singleton;
INSERT INTO organizations (id, slug) VALUES ('10000000-0000-0000-0000-000000000081', 'fg081-org');
INSERT INTO projects (id, organization_id, slug)
VALUES ('20000000-0000-0000-0000-000000000081', '10000000-0000-0000-0000-000000000081', 'fg081-project');
INSERT INTO builds (id, organization_id, project_id, number, idempotency_key, status, cancellation_requested, next_log_sequence, created_at)
VALUES ('30000000-0000-0000-0000-000000000081', '10000000-0000-0000-0000-000000000081',
        '20000000-0000-0000-0000-000000000081', 81, 'fg081-key', 'running', true, 82,
        '2026-08-25T01:00:00Z');
INSERT INTO nodes (id, organization_id, build_id, name, ordinal, required_trust_pool, required_capabilities, status)
VALUES ('40000000-0000-0000-0000-000000000081', '10000000-0000-0000-0000-000000000081',
        '30000000-0000-0000-0000-000000000081', 'rehearsal', 0, 'trusted-linux', ARRAY['linux', :'runtime'], 'running');
INSERT INTO attempts
  (id, organization_id, node_id, ordinal, retry_of, state, fence, restore_epoch, lease_owner, lease_expires_at, result, created_at)
VALUES
  ('50000000-0000-0000-0000-000000000081', '10000000-0000-0000-0000-000000000081',
   '40000000-0000-0000-0000-000000000081', 0, NULL, 'terminal', 3, 2, NULL, NULL, 'failure', '2026-08-25T01:01:00Z'),
  ('50000000-0000-0000-0000-000000000082', '10000000-0000-0000-0000-000000000081',
   '40000000-0000-0000-0000-000000000081', 1, '50000000-0000-0000-0000-000000000081', 'queued', 0, 2, NULL, NULL, NULL, '2026-08-25T01:02:00Z'),
  ('50000000-0000-0000-0000-000000000083', '10000000-0000-0000-0000-000000000081',
   '40000000-0000-0000-0000-000000000081', 2, NULL, 'running', 4, 2, 'agent-81', '2099-01-01T00:00:00Z', NULL, '2026-08-25T01:03:00Z');
INSERT INTO events (organization_id, build_id, attempt_id, kind, payload, created_at)
VALUES ('10000000-0000-0000-0000-000000000081', '30000000-0000-0000-0000-000000000081',
        '50000000-0000-0000-0000-000000000083', 'fg081.fixture', '{"unicode":"π","flags":[true,false]}'::jsonb, '2026-08-25T01:04:00Z');
INSERT INTO outbox (organization_id, topic, body, published_at, created_at)
VALUES ('10000000-0000-0000-0000-000000000081', 'fg081.fixture', '{"ordinal":81}'::jsonb, NULL, '2026-08-25T01:05:00Z');
INSERT INTO log_chunks
  (organization_id, build_id, attempt_id, sequence, build_sequence, body, created_at)
VALUES ('10000000-0000-0000-0000-000000000081', '30000000-0000-0000-0000-000000000081',
        '50000000-0000-0000-0000-000000000083', 81, 81, 'quoted '' value | unicode π', '2026-08-25T01:06:00Z');
INSERT INTO effect_checkpoints
  (organization_id, attempt_id, effect_key, fence, authority_owner, restore_epoch, payload_digest, state, prepared_at)
VALUES ('10000000-0000-0000-0000-000000000081', '50000000-0000-0000-0000-000000000083',
        'publish', 4, 'agent-81', 2, decode(repeat('ab', 32), 'hex'), 'prepared', '2026-08-25T01:07:00Z');
INSERT INTO retry_decisions
  (organization_id, parent_attempt_id, parent_node_id, parent_ordinal, parent_retry_of,
   parent_restore_epoch, attempt_limit, outcome, child_attempt_id, dead_letter_reason, decided_at)
VALUES ('10000000-0000-0000-0000-000000000081', '50000000-0000-0000-0000-000000000081',
        '40000000-0000-0000-0000-000000000081', 0, NULL, 2, 3, 'child_created',
        '50000000-0000-0000-0000-000000000082', NULL, '2026-08-25T01:08:00Z');
SELECT setval(pg_get_serial_sequence('events','id'), 810, true);
SELECT setval(pg_get_serial_sequence('outbox','id'), 820, true);
SELECT setval(pg_get_serial_sequence('log_chunks','id'), 830, true);
SQL

normalize_schema() {
  awk '
    /^\\restrict / { token=$2; print "\\restrict FG081_TRANSPORT_KEY"; next }
    /^\\unrestrict / { if ($2 != token) exit 41; print "\\unrestrict FG081_TRANSPORT_KEY"; next }
    { print }
  ' "$1" > "$2" || die "schema transport guards are malformed"
}

capture_phase() {
  local database=$1 label=$2 prefix="$scratch/$2"
  run_tool pg_dump -U "$pg_user" -d "$database" --schema-only --format=plain --encoding=UTF8 --quote-all-identifiers > "$prefix.schema.raw"
  normalize_schema "$prefix.schema.raw" "$prefix.schema"
  if ! run_tool pg_dump -U "$pg_user" -d "$database" --data-only --inserts --column-inserts --rows-per-insert=1 \
    --exclude-table=schema_migrations --encoding=UTF8 > "$prefix.data.raw" 2> "$prefix.data.stderr"; then
    cat "$prefix.data.stderr" >&2
    die "$label data inventory dump failed"
  fi
  awk '/^INSERT INTO / { print }' "$prefix.data.raw" | LC_ALL=C sort > "$prefix.data"
  psql_db "$database" -c "SELECT version || E'\\t' || checksum FROM schema_migrations ORDER BY version" > "$prefix.ledger"
  psql_db "$database" > "$prefix.sequences" <<'SQL'
SELECT format(
  'SELECT %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || %L || E''\t'' || last_value::text || E''\t'' || is_called::text FROM %I.%I',
  n.nspname, c.relname, s.seqstart::text, s.seqincrement::text,
  s.seqmax::text, s.seqmin::text, s.seqcache::text, s.seqcycle::text,
  n.nspname, c.relname)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
JOIN pg_sequence s ON s.seqrelid = c.oid
WHERE n.nspname = 'public'
ORDER BY c.relname
\gexec
SQL
  [[ -s "$prefix.schema" && -s "$prefix.data" && -s "$prefix.ledger" && -s "$prefix.sequences" ]] || die "empty logical inventory at $label"
  {
    printf 'schema %s\n' "$(sha256_file "$prefix.schema")"
    printf 'data %s\n' "$(sha256_file "$prefix.data")"
    printf 'ledger %s\n' "$(sha256_file "$prefix.ledger")"
    printf 'sequences %s\n' "$(sha256_file "$prefix.sequences")"
  } > "$prefix.components"
  sha256_file "$prefix.components"
}

validate_fk_rebuild() {
  local database=$1 label=$2 audit_db="fogell_fg081_${token}_audit_$2"
  local invalid fk_count
  invalid="$(psql_db "$database" -c "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND NOT convalidated" | tr -d '[:space:]')"
  fk_count="$(psql_db "$database" -c "SELECT count(*) FROM pg_constraint WHERE contype = 'f'" | tr -d '[:space:]')"
  [[ "$invalid" == "0" && "$fk_count" -gt 0 ]] || die "$label has invalid or no foreign keys"
  run_tool pg_dump -U "$pg_user" -d "$database" --format=custom > "$scratch/$label.audit.custom"
  [[ -s "$scratch/$label.audit.custom" ]] || die "$label clean-room archive is empty"
  create_database "$audit_db"
  run_tool pg_restore -U "$pg_user" -d "$audit_db" --exit-on-error --single-transaction < "$scratch/$label.audit.custom" >/dev/null \
    || die "$label clean-room FK rebuild failed"
  audit_invalid="$(psql_db "$audit_db" -c "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND NOT convalidated" | tr -d '[:space:]')"
  audit_count="$(psql_db "$audit_db" -c "SELECT count(*) FROM pg_constraint WHERE contype = 'f'" | tr -d '[:space:]')"
  [[ "$audit_invalid" == "0" && "$audit_count" == "$fk_count" ]] || die "$label clean-room FK inventory differs"
  source_hash="$(capture_phase "$database" "$label-source")"
  audit_hash="$(capture_phase "$audit_db" "$label-audit")"
  cmp -s "$scratch/$label-source.data" "$scratch/$label-audit.data" || die "$label clean-room data differs"
  cmp -s "$scratch/$label-source.ledger" "$scratch/$label-audit.ledger" || die "$label clean-room ledger differs"
  cmp -s "$scratch/$label-source.sequences" "$scratch/$label-audit.sequences" || die "$label clean-room sequences differ"
  # The restored schema is the canonical logical representation. PostgreSQL's
  # parser removes semantically redundant expression parentheses during a
  # dump/restore, so raw pre-restore pg_dump text is transport syntax, not a
  # stable logical schema oracle.
  validated_hash="$audit_hash"
  drop_database "$audit_db"
}

validate_fk_rebuild "$primary_db" pre
pre_hash="$validated_hash"
rollback_archive="$scratch/rollback-pre-$latest_version.custom"
run_tool pg_dump -U "$pg_user" -d "$primary_db" --format=custom > "$rollback_archive" || die "pre-upgrade rollback dump failed"
[[ -s "$rollback_archive" ]] || die "pre-upgrade rollback archive is empty"
rollback_archive_sha="$(sha256_file "$rollback_archive")"
[[ "$rollback_archive_sha" =~ ^[0-9a-f]{64}$ ]] || die "rollback archive hash is malformed"
if [[ "${FG081_TEST_CORRUPT_ARCHIVE_AFTER_HASH:-0}" == "1" ]]; then printf X >> "$rollback_archive"; fi

apply_migration "$primary_db" "$latest_index"
if [[ "${FG081_TEST_DROP_FORWARD_FK:-0}" == "1" ]]; then
  psql_db "$primary_db" -c "ALTER TABLE log_chunks DROP CONSTRAINT log_chunks_attempt_tenant_fk" >/dev/null
fi
latest_guards="$(psql_db "$primary_db" -c "SELECT count(*) FROM pg_constraint WHERE conname IN ('events_attempt_tenant_fk','log_chunks_attempt_tenant_fk')" | tr -d '[:space:]')"
[[ "$latest_guards" == "2" ]] || die "latest migration did not install both tenant-composite attempt keys"
validate_fk_rebuild "$primary_db" forward1
forward1_hash="$validated_hash"

[[ "$(sha256_file "$rollback_archive")" == "$rollback_archive_sha" ]] || die "rollback archive changed after its hash was recorded"
drop_database "$primary_db"
create_database "$primary_db"
run_tool pg_restore -U "$pg_user" -d "$primary_db" --exit-on-error --single-transaction < "$rollback_archive" >/dev/null \
  || die "restore-based rollback failed"
if [[ "${FG081_TEST_CORRUPT_ROLLBACK_DATA:-0}" == "1" ]]; then
  psql_db "$primary_db" -c "INSERT INTO outbox (id, organization_id, topic, body) VALUES (999999, '10000000-0000-0000-0000-000000000081', 'fault', '{}'::jsonb)" >/dev/null
fi
validate_fk_rebuild "$primary_db" rollback
rollback_hash="$validated_hash"
[[ "$rollback_hash" == "$pre_hash" ]] || die "rollback logical hash differs from the pre-upgrade state"
if [[ "${FG081_TEST_CORRUPT_ROLLBACK_DATA:-0}" == "1" ]]; then
  psql_db "$primary_db" -c "DELETE FROM outbox WHERE id = 999999" >/dev/null
fi

if [[ "${FG081_TEST_SKIP_SECOND_FORWARD:-0}" != "1" ]]; then apply_migration "$primary_db" "$latest_index"; fi
validate_fk_rebuild "$primary_db" forward2
forward2_hash="$validated_hash"
[[ "$forward2_hash" == "$forward1_hash" ]] || die "second forward logical hash differs from the first forward state"

completed=1
printf 'FG-081 PASS previous=%s latest=%s pre=%s rollback=%s forward1=%s forward2=%s archive-sha256=%s fk-phases=4\n' \
  "$previous_version" "$latest_version" "$pre_hash" "$rollback_hash" "$forward1_hash" "$forward2_hash" "$rollback_archive_sha"
