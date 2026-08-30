#!/usr/bin/env bash
# FG-085a hostile proof. Every mutation is made in a private mktemp directory;
# the repository candidate is never rewritten. The fake container runtime is an
# exact protocol surrogate, while the workflow separately runs the genuine
# PostgreSQL 16 drill.
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
candidate="$script_dir/backup-restore-drill.sh"
[[ -f "$candidate" ]] || { echo "missing backup-restore-drill.sh" >&2; exit 1; }
repo_root="$(cd "$script_dir/.." && pwd)"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/fogell-fg085a-proof.XXXXXX")"
case "$scratch" in
  "${TMPDIR:-/tmp}"/fogell-fg085a-proof.*) ;;
  *) echo "unsafe proof scratch path: $scratch" >&2; exit 1 ;;
esac
evidence_dir="${FG085_PROOF_EVIDENCE_DIR:-}"
if [[ -n "$evidence_dir" ]]; then
  [[ "$evidence_dir" =~ ^/tmp/fg085a-compliant-[A-Za-z0-9._-]+$ ]] \
    || { echo "unsafe FG085_PROOF_EVIDENCE_DIR: $evidence_dir" >&2; exit 1; }
  [[ ! -e "$evidence_dir" ]] || { echo "proof evidence directory already exists" >&2; exit 1; }
  mkdir -p "$evidence_dir"
fi

cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  case "$scratch" in
    "${TMPDIR:-/tmp}"/fogell-fg085a-proof.*) rm -rf -- "$scratch" ;;
    *) printf 'refusing unsafe proof cleanup: %s\n' "$scratch" >&2; rc=1 ;;
  esac
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

fake_bin="$scratch/bin"
mkdir -p "$fake_bin"
fake_runtime="$fake_bin/docker"

cat > "$fake_runtime" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == "exec" && "${2:-}" == "-i" ]] || { echo "fake runtime: expected exec -i" >&2; exit 91; }
shift 2
container=${1:-}
shift
[[ "$container" == "fake-postgres" ]] || { echo "fake runtime: wrong container" >&2; exit 92; }
tool=${1:-}
shift
state=${FG085_FAKE_STATE:?}
fault=${FG085_FAKE_FAULT:-none}
mkdir -p "$state"

database_from_args() {
  local prior=""
  local arg
  for arg in "$@"; do
    if [[ "$prior" == "-d" ]]; then printf '%s\n' "$arg"; return; fi
    prior=$arg
  done
  printf '\n'
}

database_role() {
  local database=$1
  if [[ -f "$state/source" && "$(<"$state/source")" == "$database" ]]; then
    printf 'source\n'
  elif [[ -f "$state/target" && "$(<"$state/target")" == "$database" ]]; then
    printf 'target\n'
  else
    printf 'other\n'
  fi
}

has_psql_command_arg() {
  local arg
  for arg in "$@"; do
    case "$arg" in
      -c|--command|--command=*) return 0 ;;
    esac
  done
  return 1
}

case "$tool" in
  psql)
    args="$*"
    database="$(database_from_args "$@")"
    # docker exec -i preserves the caller's stdin.  Argument-only psql calls
    # must not wait for EOF from an operator's terminal or agent pipe; consume
    # stdin only for the migration/fixture/inventory calls that use it as the
    # SQL protocol.
    input=""
    if [[ "$args" != "--version" ]] && ! has_psql_command_arg "$@"; then
      input="$(cat || true)"
    fi
    if [[ "$args" == "--version" ]]; then
      printf 'psql (PostgreSQL) 16.4\n'
    elif [[ "$args" == *"SHOW server_version_num"* ]]; then
      printf '160000\n'
    elif [[ "$args" == *"FROM pg_database WHERE datname IN"* ]]; then
      count=0
      [[ -f "$state/source" ]] && count=$((count + 1))
      [[ -f "$state/target" ]] && count=$((count + 1))
      printf '%s\n' "$count"
    elif [[ "$args" =~ CREATE[[:space:]]DATABASE[[:space:]](fogell_fg085a_[a-z0-9_]+_source) ]]; then
      printf '%s\n' "${BASH_REMATCH[1]}" > "$state/source"
    elif [[ "$args" =~ CREATE[[:space:]]DATABASE[[:space:]](fogell_fg085a_[a-z0-9_]+_target) ]]; then
      printf '%s\n' "${BASH_REMATCH[1]}" > "$state/target"
    elif [[ "$args" =~ DROP[[:space:]]DATABASE[[:space:]](fogell_fg085a_[a-z0-9_]+_(source|target)) ]]; then
      role="$(database_role "${BASH_REMATCH[1]}")"
      [[ "$role" == "source" || "$role" == "target" ]] || exit 93
      [[ "$fault" == "cleanup_failure" && "$role" == "target" ]] && exit 44
      rm -f -- "$state/$role"
    elif [[ "$args" == *"FROM pg_class c JOIN pg_namespace"* ]]; then
      if [[ "$fault" == "contamination" && "$(database_role "$database")" == "target" ]]; then
        printf '1\n'
      else
        printf '0\n'
      fi
    elif [[ "$args $input" == *"pg_sequence"* ]]; then
      role="$(database_role "$database")"
      if [[ "$fault" == "empty_sequences" ]]; then
        :
      else
        printf 'public\tevents_id_seq\t1\t1\t9223372036854775807\t1\t1\tf\t41\tt\n'
        printf 'public\tlog_chunks_id_seq\t1\t1\t9223372036854775807\t1\t1\tf\t63\tt\n'
        if [[ "$fault" == "sequence_drift" && "$role" == "target" ]]; then
          printf 'public\toutbox_id_seq\t1\t1\t9223372036854775807\t1\t1\tf\t52\tf\n'
        else
          printf 'public\toutbox_id_seq\t1\t1\t9223372036854775807\t1\t1\tf\t52\tt\n'
        fi
      fi
    elif [[ "$input" == *"DROP CONSTRAINT effect_checkpoints_effect_key_check"* ]]; then
      # Model the PostgreSQL 16 deparse boundary that exposed FG-085a: the
      # original BETWEEN form dumps with a nested comparison group, whereas a
      # restored schema flattens that equivalent AND tree.  Only the explicit
      # stable form from migration 0007 makes the source inventory canonical.
      if [[ "$input" == *"char_length(effect_key) >= 1"* \
            && "$input" == *"char_length(effect_key) <= 256"* \
            && "$input" == *"btrim(effect_key) <> ''"* ]]; then
        : > "$state/effect-key-check-canonical"
      fi
    elif [[ -n "$input" || "$args" == *"schema_migrations"* || "$args" == *"pg_terminate_backend"* ]]; then
      :
    else
      :
    fi
    ;;
  pg_dump)
    if [[ "${1:-}" == "--version" ]]; then
      if [[ "$fault" == "tool_mismatch" ]]; then printf 'pg_dump (PostgreSQL) 15.9\n'; else printf 'pg_dump (PostgreSQL) 16.4\n'; fi
      exit 0
    fi
    args="$*"
    database="$(database_from_args "$@")"
    role="$(database_role "$database")"
    if [[ "$args" == *"--format=custom"* ]]; then
      [[ "$fault" == "empty_archive" ]] || printf 'FAKE_CUSTOM_ARCHIVE_v1\n'
      [[ "$fault" == "dump_rc" ]] && exit 17
      :
    elif [[ "$args" == *"--schema-only"* ]]; then
      if [[ "$fault" == "empty_schema" ]]; then
        :
      elif [[ "$fault" == "schema_drift" && "$role" == "target" ]]; then
        printf '\\restrict TARGETtoken9\nCREATE TABLE public.fixture (id bigint, drift text);\n\\unrestrict TARGETtoken9\n'
      elif [[ "$role" == "target" ]]; then
        printf '\\restrict TARGETtoken9\nCREATE TABLE public.fixture (id bigint, payload jsonb, CONSTRAINT effect_key_check CHECK ((char_length(payload::text) >= 1 AND char_length(payload::text) <= 256 AND btrim(payload::text) <> '\''\'')));\n\\unrestrict TARGETtoken9\n'
      elif [[ -f "$state/effect-key-check-canonical" ]]; then
        printf '\\restrict SOURCEtoken7\nCREATE TABLE public.fixture (id bigint, payload jsonb, CONSTRAINT effect_key_check CHECK ((char_length(payload::text) >= 1 AND char_length(payload::text) <= 256 AND btrim(payload::text) <> '\''\'')));\n\\unrestrict SOURCEtoken7\n'
      else
        printf '\\restrict SOURCEtoken7\nCREATE TABLE public.fixture (id bigint, payload jsonb, CONSTRAINT effect_key_check CHECK (((char_length(payload::text) >= 1 AND char_length(payload::text) <= 256) AND btrim(payload::text) <> '\''\'')));\n\\unrestrict SOURCEtoken7\n'
      fi
    elif [[ "$args" == *"--data-only"* ]]; then
      if [[ "$fault" == "empty_data" ]]; then
        :
      elif [[ "$role" == "target" ]]; then
        printf "INSERT INTO public.fixture (id, payload) VALUES (2, '{\"v\":\"b\"}');\n"
        if [[ "$fault" == "data_drift" ]]; then
          printf "INSERT INTO public.fixture (id, payload) VALUES (1, '{\"v\":\"WRONG\"}');\n"
        else
          printf "INSERT INTO public.fixture (id, payload) VALUES (1, '{\"v\":\"a\"}');\n"
        fi
      else
        printf "INSERT INTO public.fixture (id, payload) VALUES (1, '{\"v\":\"a\"}');\n"
        printf "INSERT INTO public.fixture (id, payload) VALUES (2, '{\"v\":\"b\"}');\n"
      fi
    else
      exit 94
    fi
    ;;
  pg_restore)
    if [[ "${1:-}" == "--version" ]]; then
      printf 'pg_restore (PostgreSQL) 16.4\n'
      exit 0
    fi
    if [[ "${1:-}" == "--list" ]]; then
      cat >/dev/null
      [[ "$fault" == "truncated_archive" ]] && exit 31
      printf '1; 0 0 TABLE public fixture fogell\n'
    else
      cat >/dev/null
      [[ "$fault" == "restore_rc" ]] && exit 23
      :
    fi
    ;;
  *) echo "fake runtime: unexpected tool $tool" >&2; exit 95 ;;
esac
FAKE
chmod +x "$fake_runtime"

run_drill() {
  local script=$1
  local fault=$2
  local label=$3
  local drill_repo_root=${4:-$repo_root}
  local state="$scratch/state-$label"
  mkdir -p "$state"
  local rc=0
  PATH="$fake_bin:$PATH" \
    FG085_FAKE_STATE="$state" \
    FG085_FAKE_FAULT="$fault" \
    FG085_TEST_CORRUPT_AFTER_HASH="${FG085_TEST_CORRUPT_AFTER_HASH:-0}" \
    FG085_REPO_ROOT="$drill_repo_root" \
    FOGELL_CONTAINER_RUNTIME=docker \
    FOGELL_PG_CONTAINER=fake-postgres \
    bash -x "$script" > "$scratch/$label.log" 2>&1 || rc=$?
  if [[ -n "$evidence_dir" ]]; then
    cp "$scratch/$label.log" "$evidence_dir/$label.log"
    printf '%s\n' "$rc" > "$evidence_dir/$label.exit"
  fi
  return "$rc"
}

expect_pass() {
  local script=$1 fault=$2 label=$3
  local drill_repo_root=${4:-$repo_root}
  run_drill "$script" "$fault" "$label" "$drill_repo_root" \
    || { printf 'expected PASS: %s\n' "$label" >&2; tail -n 160 "$scratch/$label.log" >&2; exit 1; }
}

expect_refusal() {
  local script=$1 fault=$2 label=$3
  local drill_repo_root=${4:-$repo_root}
  if run_drill "$script" "$fault" "$label" "$drill_repo_root"; then
    printf 'expected REFUSAL: %s\n' "$label" >&2
    tail -n 160 "$scratch/$label.log" >&2
    exit 1
  fi
  if grep -q '^FG-085a PASS ' "$scratch/$label.log"; then
    printf 'refusal emitted a false PASS marker: %s\n' "$label" >&2
    tail -n 160 "$scratch/$label.log" >&2
    exit 1
  fi
}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}';
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}
initial_candidate_sha="$(sha256_file "$candidate")"

make_mutant() {
  local name=$1
  local output="$scratch/$name.sh"
  cp "$candidate" "$output"
  chmod +x "$output"
  printf '%s\n' "$output"
}

assert_changed_and_syntax() {
  local mutant=$1
  local mutant_sha candidate_sha
  mutant_sha="$(sha256_file "$mutant")"
  candidate_sha="$(sha256_file "$candidate")"
  [[ "$mutant_sha" != "$candidate_sha" ]] \
    || { echo "mutation was not byte-changing: $mutant" >&2; exit 1; }
  bash -n "$mutant"
  [[ "$(sha256_file "$candidate")" == "$candidate_sha" ]] \
    || { echo "candidate changed during scratch mutation: $mutant" >&2; exit 1; }
  printf 'MUTANT %s sha256=%s candidate-restored=%s\n' "$(basename "$mutant" .sh)" "$mutant_sha" "$candidate_sha"
  if [[ -n "$evidence_dir" ]]; then
    cp "$mutant" "$evidence_dir/$(basename "$mutant")"
    printf '%s  %s\n' "$mutant_sha" "$(basename "$mutant")" > "$evidence_dir/$(basename "$mutant").sha256"
    printf '%s  backup-restore-drill.sh\n' "$candidate_sha" > "$evidence_dir/candidate.restore.sha256"
  fi
}

expect_pass "$candidate" none candidate-valid-control

# M01: compare the source data inventory to a second source capture.
m01="$(make_mutant m01-source-vs-source)"
perl -0pi -e 's/capture_data "\$target_db" "\$target_data"/capture_data "\$source_db" "\$target_data"/' "$m01"
assert_changed_and_syntax "$m01"
expect_refusal "$candidate" data_drift m01-control
expect_pass "$m01" data_drift m01-mutant

# M02: reduce data comparison to row counts and neutralize its byte hash.
m02="$(make_mutant m02-row-count-only)"
perl -0pi -e 's/cmp -s "\$source_data" "\$target_data"/cmp -s <(wc -l < "\$source_data") <(wc -l < "\$target_data")/; s/\[\[ "\$data_sha" == "\$\(sha256_file "\$target_data"\)" \]\]/[[ "$data_sha" == "$data_sha" ]]/' "$m02"
assert_changed_and_syntax "$m02"
expect_refusal "$candidate" data_drift m02-control
expect_pass "$m02" data_drift m02-mutant

# M03: omit both schema byte and hash comparisons.
m03="$(make_mutant m03-omit-schema)"
perl -0pi -e 's/cmp -s "\$source_schema" "\$target_schema"/true/; s/\[\[ "\$schema_sha" == "\$\(sha256_file "\$target_schema"\)" \]\]/[[ "$schema_sha" == "$schema_sha" ]]/' "$m03"
assert_changed_and_syntax "$m03"
expect_refusal "$candidate" schema_drift m03-control
expect_pass "$m03" schema_drift m03-mutant

# M04: omit both sequence byte and hash comparisons.
m04="$(make_mutant m04-omit-sequences)"
perl -0pi -e 's/cmp -s "\$source_sequences" "\$target_sequences"/true/; s/\[\[ "\$sequences_sha" == "\$\(sha256_file "\$target_sequences"\)" \]\]/[[ "$sequences_sha" == "$sequences_sha" ]]/' "$m04"
assert_changed_and_syntax "$m04"
expect_refusal "$candidate" sequence_drift m04-control
expect_pass "$m04" sequence_drift m04-mutant

# M05: stop canonicalizing row order; equal sets in different physical order fail.
m05="$(make_mutant m05-no-sort)"
perl -0pi -e 's/ \| LC_ALL=C sort > "\$output"/ > "\$output"/' "$m05"
assert_changed_and_syntax "$m05"
grep -F 'awk '\''/^INSERT INTO / { print }'\'' "$data_raw" > "$output"' "$m05" >/dev/null \
  || { echo "M05 did not retain a valid literal output redirect" >&2; exit 1; }
expect_pass "$candidate" none m05-control
expect_refusal "$m05" none m05-mutant

# M06: ignore pg_dump's nonzero status after it emitted plausible bytes.
m06="$(make_mutant m06-ignore-dump-rc)"
perl -0pi -e 's/\|\| die "pg_dump failed"/|| true/' "$m06"
assert_changed_and_syntax "$m06"
expect_refusal "$candidate" dump_rc m06-control
expect_pass "$m06" dump_rc m06-mutant

# M07: accept a zero-byte custom archive.
m07="$(make_mutant m07-empty-archive)"
perl -0pi -e 's/\[\[ -s "\$archive" \]\] \|\| die "pg_dump produced an empty archive"/: # empty archive guard removed/' "$m07"
assert_changed_and_syntax "$m07"
expect_refusal "$candidate" empty_archive m07-control
expect_pass "$m07" empty_archive m07-mutant

# M08: accept equal but empty data inventories.
m08="$(make_mutant m08-empty-inventory)"
perl -0pi -e 's/\[\[ -s "\$output" \]\] \|\| die "data inventory is empty for \$database"/: # data inventory guard removed/' "$m08"
assert_changed_and_syntax "$m08"
expect_refusal "$candidate" empty_data m08-control
expect_pass "$m08" empty_data m08-mutant

# M09: restore into a target already containing a user relation.
m09="$(make_mutant m09-contaminated-target)"
perl -0pi -e 's/\[\[ "\$target_relation_count" == "0" \]\] \|\| die "restore target is not empty"/: # target contamination guard removed/' "$m09"
assert_changed_and_syntax "$m09"
expect_refusal "$candidate" contamination m09-control
expect_pass "$m09" contamination m09-mutant

# M10: trust the first archive hash after bytes can have changed.
m10="$(make_mutant m10-corrupt-after-hash)"
perl -0pi -e 's/\[\[ "\$archive_sha_final" == "\$archive_sha_initial" \]\] \|\| die "archive bytes changed after their SHA-256 was recorded"/: # second archive hash guard removed/' "$m10"
assert_changed_and_syntax "$m10"
FG085_TEST_CORRUPT_AFTER_HASH=1 expect_refusal "$candidate" none m10-control
FG085_TEST_CORRUPT_AFTER_HASH=1 expect_pass "$m10" none m10-mutant

# M11: broaden the database namespace guard beyond this drill.
m11="$(make_mutant m11-broaden-prefix)"
perl -0pi -e 's/\^fogell_fg085a_/^fogell_/' "$m11"
assert_changed_and_syntax "$m11"
if bash "$candidate" --validate-db-name fogell_unrelated_source >/dev/null 2>&1; then
  echo "candidate accepted a foreign database name" >&2; exit 1
fi
bash "$m11" --validate-db-name fogell_unrelated_source >/dev/null 2>&1 \
  || { echo "prefix mutant was not executable" >&2; exit 1; }

# M12: ignore pg_restore's nonzero status.
m12="$(make_mutant m12-ignore-restore-rc)"
perl -0pi -e 's/\|\| die "pg_restore failed"/|| true/' "$m12"
assert_changed_and_syntax "$m12"
expect_refusal "$candidate" restore_rc m12-control
expect_pass "$m12" restore_rc m12-mutant

# M13: ignore failure to drop the exact owned target during EXIT cleanup.
m13="$(make_mutant m13-ignore-cleanup-failure)"
perl -0pi -e 's/drop_drill_database "\$target_db" \|\| cleanup_rc=1/drop_drill_database "\$target_db" || true/' "$m13"
assert_changed_and_syntax "$m13"
expect_refusal "$candidate" cleanup_failure m13-control
expect_pass "$m13" cleanup_failure m13-mutant

# M14: compare raw random schema transport keys instead of validated normalization.
m14="$(make_mutant m14-raw-schema-guards)"
perl -0pi -e 's/normalize_schema_transport "\$schema_raw" "\$output"/cat "\$schema_raw" > "\$output"/' "$m14"
assert_changed_and_syntax "$m14"
expect_pass "$candidate" none m14-control
expect_refusal "$m14" none m14-mutant

# Direct hostile controls not coupled to a permissive mutant.
expect_refusal "$candidate" empty_schema empty-schema-control
expect_refusal "$candidate" empty_sequences empty-sequences-control
expect_refusal "$candidate" tool_mismatch tool-major-control
expect_refusal "$candidate" truncated_archive truncated-archive-control

# Migration regression controls. The fake PostgreSQL boundary intentionally
# reproduces the exact source-vs-restored deparse mismatch seen in hosted CI.
# Omitting the forward repair, or replacing its flat comparisons with the old
# BETWEEN form, must make the real candidate refuse schema equivalence.
canonical_migration="$repo_root/src/Fogell.Store/migrations/0007_canonical_effect_key_check.sql"
[[ -f "$canonical_migration" ]] || { echo "missing canonical effect-key migration" >&2; exit 1; }

omitted_root="$scratch/repo-omitted-canonical-migration"
mkdir -p "$omitted_root/src/Fogell.Store/migrations"
for migration in "$repo_root"/src/Fogell.Store/migrations/*.sql; do
  if [[ "$(basename "$migration")" != "0007_canonical_effect_key_check.sql" ]]; then
    cp "$migration" "$omitted_root/src/Fogell.Store/migrations/"
  fi
done
expect_refusal "$candidate" none omitted-canonical-migration-control "$omitted_root"

regressed_root="$scratch/repo-regressed-canonical-migration"
mkdir -p "$regressed_root/src/Fogell.Store/migrations"
cp "$repo_root"/src/Fogell.Store/migrations/*.sql "$regressed_root/src/Fogell.Store/migrations/"
regressed_migration="$regressed_root/src/Fogell.Store/migrations/0007_canonical_effect_key_check.sql"
perl -0pi -e 's/char_length\(effect_key\) >= 1\s+AND char_length\(effect_key\) <= 256/char_length(effect_key) BETWEEN 1 AND 256/' "$regressed_migration"
[[ "$(sha256_file "$regressed_migration")" != "$(sha256_file "$canonical_migration")" ]] \
  || { echo "canonical migration regression was not byte-changing" >&2; exit 1; }
grep -F 'char_length(effect_key) BETWEEN 1 AND 256' "$regressed_migration" >/dev/null \
  || { echo "canonical migration regression did not restore BETWEEN" >&2; exit 1; }
printf 'MUTANT m15-reintroduce-between sha256=%s candidate-restored=%s\n' \
  "$(sha256_file "$regressed_migration")" "$(sha256_file "$canonical_migration")"
expect_refusal "$candidate" none reintroduced-between-control "$regressed_root"

[[ "$(sha256_file "$candidate")" == "$initial_candidate_sha" ]] \
  || { echo "final candidate restore hash drifted" >&2; exit 1; }
printf 'FG-085a PROOF PASS: 15 unique byte-changing mutants killed; canonical-constraint omission, empty schema/sequence, truncated archive, and tool-major controls refused\n'
