#!/usr/bin/env bash
# FG-005/FG-223 — evidence convention. Seals a ticket's receipt so it verifies standalone.
#   usage: scripts/seal-evidence.sh FG-010 [extra-file ...]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT="$PWD"
# Evidence always describes the repository containing this script. Ambient Git
# plumbing variables are process-local selectors, not part of the sealer's
# interface; ignoring them also keeps every prerequisite's child Git bound to
# the isolated candidate it runs inside.
unset GIT_DIR GIT_WORK_TREE GIT_COMMON_DIR GIT_INDEX_FILE \
  GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_QUARANTINE_PATH \
  GIT_GRAFT_FILE GIT_SHALLOW_FILE GIT_REPLACE_REF_BASE GIT_PREFIX GIT_NAMESPACE
GIT_NO_REPLACE_OBJECTS=1
export GIT_NO_REPLACE_OBJECTS
repository_git () {
  local repository="$1"
  local index_file="$2"
  shift 2
  # The script path, not caller-provided Git plumbing variables, selects the
  # repository being sealed. In particular, an inherited absolute
  # GIT_INDEX_FILE must never make linked-worktree read-tree/apply operations
  # rewrite the publishing checkout's index. Clear repository/object locators
  # that can override `git -C`, then opt into a private index only at
  # the one call site that derives the captured candidate inventory.
  if [ -n "$index_file" ]; then
    env -u GIT_DIR -u GIT_WORK_TREE -u GIT_COMMON_DIR -u GIT_INDEX_FILE \
      -u GIT_OBJECT_DIRECTORY -u GIT_ALTERNATE_OBJECT_DIRECTORIES \
      -u GIT_QUARANTINE_PATH -u GIT_GRAFT_FILE -u GIT_SHALLOW_FILE \
      -u GIT_REPLACE_REF_BASE \
      -u GIT_PREFIX -u GIT_NAMESPACE \
      GIT_INDEX_FILE="$index_file" \
      git -C "$repository" --work-tree="$repository" \
        -c core.hooksPath=/dev/null -c core.fsmonitor=false "$@"
  else
    env -u GIT_DIR -u GIT_WORK_TREE -u GIT_COMMON_DIR -u GIT_INDEX_FILE \
      -u GIT_OBJECT_DIRECTORY -u GIT_ALTERNATE_OBJECT_DIRECTORIES \
      -u GIT_QUARANTINE_PATH -u GIT_GRAFT_FILE -u GIT_SHALLOW_FILE \
      -u GIT_REPLACE_REF_BASE \
      -u GIT_PREFIX -u GIT_NAMESPACE \
      git -C "$repository" --work-tree="$repository" \
        -c core.hooksPath=/dev/null -c core.fsmonitor=false "$@"
  fi
}
root_git () {
  repository_git "$ROOT" "" "$@"
}
fail () {
  echo "REFUSING TO SEAL: $*" >&2
  exit 1
}
# Preserve the caller's historical relative-corpus semantics after prerequisite
# execution moves into the isolated candidate worktree.
if [ -n "${FOGELL_CORPUS:-}" ] && [[ "$FOGELL_CORPUS" != /* ]]; then
  FOGELL_CORPUS="$ROOT/$FOGELL_CORPUS"
  export FOGELL_CORPUS
fi
TICKET="${1:?usage: seal-evidence.sh <TICKET-ID> [files...]}"; shift || true
[[ "$TICKET" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] \
  || { echo "REFUSING TO SEAL: ticket id contains unsafe path characters: $TICKET" >&2; exit 1; }
STAMP="$(root_git log -1 --format=%cd --date=format:%Y%m%dT%H%M%SZ)"
DIR="evidence/${STAMP}-${TICKET,,}"

assert_no_configured_content_filters () {
  local label="$1"
  shift
  local config_rc
  # Attribute values named `unset`/`unspecified` are textually ambiguous with
  # check-attr's special states. Refuse those two driver names conservatively;
  # other configured drivers are allowed only when the path audits below prove
  # no candidate/base path activates them. This avoids making an unrelated
  # machine-wide LFS driver disable sealing for a repository that does not use it.
  if "$@" config --get-regexp \
    '^filter\.(unset|unspecified)\.(clean|smudge|process)$' \
    > /dev/null; then
    fail "$label has an ambiguously named configured Git content filter"
  else
    config_rc=$?
    [ "$config_rc" -eq 1 ] \
      || fail "$label Git content-filter configuration could not be audited"
  fi
}

assert_no_effective_content_filters () {
  local -a attributes=()
  local listing_pid i value
  assert_no_configured_content_filters "publishing checkout" root_git
  # check-attr only resolves policy; it does not execute the named driver. Refuse
  # filters in the publishing candidate before a diff can invoke a clean driver.
  mapfile -d '' attributes < <(
    root_git ls-files -z | root_git check-attr -z --stdin filter
  )
  listing_pid=$!
  wait "$listing_pid" || fail "effective Git content-filter attributes could not be audited"
  (( ${#attributes[@]} % 3 == 0 )) \
    || fail "effective Git content-filter audit returned malformed output"
  for ((i = 0; i < ${#attributes[@]}; i += 3)); do
    value="${attributes[i + 2]}"
    case "$value" in
      unspecified|unset) ;;
      *) fail "effective Git content filter is unsupported: ${attributes[i]} ($value)" ;;
    esac
  done

  # The receipt binds both the exact base and its candidate patch. Audit base
  # policy independently so a dirty candidate cannot make an active filter
  # disappear merely by deleting both its attribute and target; file checkout
  # still remains candidate-index-first below.
  attributes=()
  mapfile -d '' attributes < <(
    root_git ls-tree -rz --name-only HEAD \
      | root_git check-attr -z --source=HEAD --stdin filter
  )
  listing_pid=$!
  wait "$listing_pid" || fail "base Git content-filter attributes could not be audited"
  (( ${#attributes[@]} % 3 == 0 )) \
    || fail "base Git content-filter audit returned malformed output"
  for ((i = 0; i < ${#attributes[@]}; i += 3)); do
    value="${attributes[i + 2]}"
    case "$value" in
      unspecified|unset) ;;
      *) fail "base Git content filter is unsupported: ${attributes[i]} ($value)" ;;
    esac
  done
}

assert_no_effective_content_filters

# Validate caller-owned inputs before starting the transactional bundle. A missing
# extra used to be silently skipped, so a manifest could verify while omitting a
# measurement the caller explicitly asked it to bind.
declare -A EXTRA_PATHS=()
declare -A EXTRA_NAMES=()
for f in "$@"; do
  [ -f "$f" ] || fail "extra evidence file does not exist or is not a regular file: $f"
  normalized="${f#./}"
  name="$(basename -- "$f")"
  case "$name" in
    .*)
      fail "extra evidence basename is reserved for internal staging: $name" ;;
    SHA256SUMS|base-commit.txt|build.log|candidate.diff|corpus-gate.log|diffstat.txt|status-before-commit.txt|tree.txt|tests-*.log)
      fail "extra evidence basename is reserved by the bundle: $name" ;;
  esac
  [[ -z "${EXTRA_NAMES[$name]+present}" ]] || fail "extra evidence basenames collide: $name"
  EXTRA_PATHS["$normalized"]=1
  EXTRA_NAMES["$name"]=1
done

mkdir -p evidence
[ ! -e "$DIR" ] || fail "evidence destination already exists: $DIR"

# FG-104 review finding: a seal run before `git add` records a diff that OMITS every
# untracked file — the FG-104 bundle validated an intermediate patch and left out the audit
# script itself, which was the entire deliverable. Evidence that silently excludes the work
# is worse than no evidence, because it carries a checksum.
# The positional [extra-file ...] arguments are EVIDENCE artifacts — a measurement log, a
# probe output — and are normally untracked on purpose. Refusing them broke the script's
# own documented interface, and staging them as the error advised would have pushed
# evidence-only files into the product commit. They are exempt; everything else is not.
# Normalised: `git ls-files` reports `x.log` while a caller naturally writes `./x.log`,
# and an exemption that fails on a leading `./` is no exemption at all.
assert_no_untracked_source () {
  local -a all_untracked=()
  local -a untracked_source=()
  local listing_pid
  local untracked_path
  mapfile -d '' all_untracked < <(root_git ls-files --others --exclude-standard -z)
  listing_pid=$!
  wait "$listing_pid" || fail "untracked source inventory could not be read"
  for untracked_path in "${all_untracked[@]}"; do
    # ALL of evidence/ is output, not input. Excluding only this run's directory
    # would make every previous receipt block the next seal.
    case "$untracked_path" in evidence/*) continue ;; esac
    [[ -n "${EXTRA_PATHS[$untracked_path]+present}" ]] \
      || untracked_source+=("$untracked_path")
  done
  if [ "${#untracked_source[@]}" -gt 0 ]; then
    echo "REFUSING TO SEAL: untracked files would be omitted from the evidence:" >&2
    for untracked_path in "${untracked_source[@]}"; do
      printf '  %q\n' "$untracked_path" >&2
    done
    echo "Stage them (git add) so the sealed diff covers the actual change." >&2
    exit 1
  fi
}

assert_no_untracked_source

# Build the bundle outside its final name. A failed measurement leaves neither a
# checksum nor a directory that looks sealed; the exact mktemp result is the only
# path the cleanup trap may remove.
STAGING="$(mktemp -d "$ROOT/evidence/.${STAMP}-${TICKET,,}.partial.XXXXXX")"
SOURCE_PARENT=""
SOURCE_SNAPSHOT=""
cleanup () {
  if [ -n "${SOURCE_SNAPSHOT:-}" ]; then
    root_git worktree remove --force "$SOURCE_SNAPSHOT" >/dev/null 2>&1 || true
  fi
  if [ -n "${SOURCE_PARENT:-}" ] && [ -d "$SOURCE_PARENT" ]; then
    rm -rf -- "$SOURCE_PARENT"
  fi
  if [ -n "${STAGING:-}" ] && [ -d "$STAGING" ]; then
    rm -rf -- "$STAGING"
  fi
}
trap cleanup EXIT

snapshot_git () {
  # A measured source must not execute repository hooks or a configured
  # filesystem monitor while Git inspects/materializes it. Bind it to the
  # linked worktree's own Git directory and index regardless of caller env.
  repository_git "$SOURCE_SNAPSHOT" "" "$@"
}

assert_pristine_materialized_inputs () {
  local -a unexpected=()
  local -a ignored=()
  local listing_pid
  local unexpected_path
  mapfile -d '' unexpected < <(
    snapshot_git ls-files --others --exclude-standard -z
  )
  listing_pid=$!
  wait "$listing_pid" || fail "materialized untracked inventory could not be read"
  mapfile -d '' ignored < <(
    snapshot_git ls-files --others --ignored --exclude-standard -z
  )
  listing_pid=$!
  wait "$listing_pid" || fail "materialized ignored inventory could not be read"
  unexpected+=("${ignored[@]}")
  if [ "${#unexpected[@]}" -gt 0 ]; then
    echo "REFUSING TO SEAL: materialized candidate contains unbound input:" >&2
    for unexpected_path in "${unexpected[@]}"; do
      printf '  %q\n' "$unexpected_path" >&2
    done
    fail "materialized candidate contains untracked or ignored input"
  fi
}

assert_no_candidate_index_content_filters () {
  local -a attributes=()
  local listing_pid i value
  # The candidate index already contains base + candidate.diff, while the
  # worktree is still empty. Audit its exact .gitattributes view in the linked
  # worktree configuration context before checkout-index can execute a driver.
  mapfile -d '' attributes < <(
    snapshot_git ls-files -z \
      | snapshot_git check-attr -z --cached --stdin filter
  )
  listing_pid=$!
  wait "$listing_pid" \
    || fail "materialized candidate Git content-filter attributes could not be audited"
  (( ${#attributes[@]} % 3 == 0 )) \
    || fail "materialized candidate Git content-filter audit returned malformed output"
  for ((i = 0; i < ${#attributes[@]}; i += 3)); do
    value="${attributes[i + 2]}"
    case "$value" in
      unspecified|unset) ;;
      *) fail "materialized candidate Git content filter is unsupported: ${attributes[i]} ($value)" ;;
    esac
  done
}

assert_no_materialized_content_filters () {
  local phase="$1"
  local -a attributes=()
  local listing_pid i value
  # Candidate files can themselves satisfy a conditional include (for example,
  # an absolute /proc/self/cwd include), and prerequisites can create ignored
  # build outputs named by one. Reload exact-context config and attributes before
  # any later diff/status operation that could execute a clean driver.
  assert_no_configured_content_filters "$phase materialized worktree" snapshot_git
  mapfile -d '' attributes < <(
    snapshot_git ls-files -z \
      | snapshot_git check-attr -z --stdin filter
  )
  listing_pid=$!
  wait "$listing_pid" \
    || fail "$phase materialized Git content-filter attributes could not be audited"
  (( ${#attributes[@]} % 3 == 0 )) \
    || fail "$phase materialized Git content-filter audit returned malformed output"
  for ((i = 0; i < ${#attributes[@]}; i += 3)); do
    value="${attributes[i + 2]}"
    case "$value" in
      unspecified|unset) ;;
      *) fail "$phase materialized Git content filter is unsupported: ${attributes[i]} ($value)" ;;
    esac
  done
}

assert_raw_index_identity () {
  local checkout="$1"
  local entries="$2"
  local label="$3"
  local entry metadata mode expected stage tracked_path actual physical_mode
  local link_target_with_sentinel link_target link_sentinel link_base checkout_root resolved_target
  while IFS= read -r -d '' entry; do
    metadata="${entry%%$'\t'*}"
    [ "$metadata" != "$entry" ] \
      || fail "$label index entry has an unexpected shape"
    tracked_path="${entry#*$'\t'}"
    read -r mode expected stage <<< "$metadata"
    [ "$stage" = 0 ] || fail "$label index contains a non-stage-0 entry"
    case "$mode" in
      100644|100755)
        if [ ! -f "$checkout/$tracked_path" ] || [ -L "$checkout/$tracked_path" ]; then
          fail "$label tracked regular file has the wrong physical type: $tracked_path"
        fi
        physical_mode="$(stat -c '%a' -- "$checkout/$tracked_path")"
        if { [ "$mode" = 100755 ] && (( (8#$physical_mode & 0111) == 0 )); } \
          || { [ "$mode" = 100644 ] && (( (8#$physical_mode & 0111) != 0 )); }; then
          fail "$label tracked executable mode does not match the candidate index: $tracked_path"
        fi
        if ! actual="$(root_git hash-object --no-filters -- "$checkout/$tracked_path")"; then
          fail "$label tracked regular file could not be hashed raw: $tracked_path"
        fi
        ;;
      120000)
        [ -L "$checkout/$tracked_path" ] \
          || fail "$label tracked symlink has the wrong physical type: $tracked_path"
        link_sentinel='__FG223_LINK_TARGET_END__'
        if ! link_target_with_sentinel="$(
          readlink -n -- "$checkout/$tracked_path"
          printf %s "$link_sentinel"
        )"; then
          fail "$label tracked symlink target could not be read: $tracked_path"
        fi
        link_target="${link_target_with_sentinel%"$link_sentinel"}"
        [[ "$link_target" != /* ]] \
          || fail "$label tracked symlink has an absolute target: $tracked_path"
        if [[ "$tracked_path" == */* ]]; then
          link_base="$checkout/${tracked_path%/*}"
        else
          link_base="$checkout"
        fi
        checkout_root="$(realpath -m -- "$checkout")"
        if ! resolved_target="$(realpath -m -- "$link_base/$link_target")"; then
          fail "$label tracked symlink target could not be resolved: $tracked_path"
        fi
        case "$resolved_target" in
          "$checkout_root"|"$checkout_root"/*) ;;
          *) fail "$label tracked symlink escapes the candidate root: $tracked_path" ;;
        esac
        case "$resolved_target" in
          "$checkout_root/.git"|"$checkout_root/.git"/*)
            fail "$label tracked symlink enters the Git administrative namespace: $tracked_path" ;;
        esac
        if ! actual="$(readlink -n -- "$checkout/$tracked_path" | root_git hash-object --stdin)"; then
          fail "$label tracked symlink could not be hashed raw: $tracked_path"
        fi
        ;;
      160000)
        fail "$label contains an unsupported gitlink: $tracked_path" ;;
      *)
        fail "$label index contains an unsupported mode $mode: $tracked_path" ;;
    esac
    [ "$actual" = "$expected" ] \
      || fail "$label raw tracked bytes do not match the candidate index: $tracked_path"
  done < "$entries"
}

capture_tracked_inventory () {
  local destination="$1"
  local candidate_index="$destination/.candidate-index"
  # Derive the inventory represented by HEAD + candidate.diff in a private
  # index. The publishing index alone still names unstaged deletions and thus
  # does not describe the candidate that the prerequisites will consume.
  repository_git "$ROOT" "$candidate_index" read-tree HEAD
  if [ -s "$destination/candidate.diff" ]; then
    if ! repository_git "$ROOT" "$candidate_index" \
      apply --cached --binary "$destination/candidate.diff"; then
      fail "captured candidate tracked inventory could not be derived"
    fi
  fi
  repository_git "$ROOT" "$candidate_index" ls-files > "$destination/tree.txt"
  repository_git "$ROOT" "$candidate_index" ls-files --stage -z \
    > "$destination/.candidate-index-entries"
  assert_raw_index_identity "$ROOT" "$destination/.candidate-index-entries" \
    "publishing candidate"
  rm "$candidate_index" "$destination/.candidate-index-entries"
}

capture_candidate () {
  local destination="$1"
  # Bookend the capture itself. A stable file created after the early preflight
  # must never survive merely as an unbound `??` status record.
  assert_no_effective_content_filters
  assert_no_untracked_source
  root_git diff --no-ext-diff --no-textconv HEAD --stat \
                                             > "$destination/diffstat.txt"
  root_git diff --no-ext-diff --no-textconv --binary --full-index HEAD \
                                             > "$destination/candidate.diff"
  # Untracked evidence/ paths are command output, not candidate source. Filter
  # only those `??` records while retaining tracked evidence changes and every
  # other non-ignored untracked path. This also keeps concurrent sealers from
  # treating each other's private staging directory as source drift.
  root_git status --short --untracked-files=all | while IFS= read -r status_line; do
    case "$status_line" in
      "?? evidence/"*) continue ;;
    esac
    printf '%s\n' "$status_line"
  done                              > "$destination/status-before-commit.txt"
  root_git rev-parse HEAD             > "$destination/base-commit.txt"
  capture_tracked_inventory "$destination"
  assert_no_untracked_source
}

# Copy caller-owned measurements before the long-running prerequisites. The
# manifest binds these exact bytes even if their source paths are later replaced.
for f in "$@"; do
  cp -- "$f" "$STAGING/$(basename -- "$f")"
done

capture_candidate "$STAGING"

# Execute prerequisites from an isolated materialization of the captured base
# plus patch. A concurrent edit-and-restore in the publishing checkout cannot
# change the bytes the corpus verifier, compiler, or tests consume. This is
# process isolation from ordinary checkout drift, not a same-UID security
# boundary against an actor that deliberately attacks the temporary worktree.
SOURCE_PARENT="$(mktemp -d /tmp/fogell-evidence-source.XXXXXX)"
SOURCE_SNAPSHOT="$SOURCE_PARENT/source"
assert_no_effective_content_filters
if ! root_git worktree add --no-checkout --detach "$SOURCE_SNAPSHOT" "$(cat "$STAGING/base-commit.txt")" \
  > "$STAGING/.materialization.log" 2>&1; then
  cat "$STAGING/.materialization.log" >&2
  fail "captured candidate worktree could not be established"
fi
# Conditional includes can produce a different effective configuration for a
# linked worktree than for its publishing checkout. Establish only its Git admin
# context first, then audit the complete candidate index before any checkout can
# run a driver. An unrelated configured driver that no candidate path activates
# is harmless and does not make the evidence command host-dependent.
assert_no_configured_content_filters "materialized worktree" snapshot_git
if ! snapshot_git read-tree "$(cat "$STAGING/base-commit.txt")" \
  >> "$STAGING/.materialization.log" 2>&1; then
  cat "$STAGING/.materialization.log" >&2
  fail "captured candidate base index could not be established"
fi
if [ -s "$STAGING/candidate.diff" ]; then
  if ! snapshot_git apply --cached --binary "$STAGING/candidate.diff" \
    >> "$STAGING/.materialization.log" 2>&1; then
    cat "$STAGING/.materialization.log" >&2
    fail "captured candidate index could not be derived"
  fi
fi
assert_no_candidate_index_content_filters
if ! snapshot_git checkout-index --all --force \
  >> "$STAGING/.materialization.log" 2>&1; then
  cat "$STAGING/.materialization.log" >&2
  fail "captured candidate files could not be materialized"
fi
assert_no_materialized_content_filters "initial"
snapshot_git ls-files --stage -z > "$SOURCE_PARENT/index.initial"
assert_raw_index_identity "$SOURCE_SNAPSHOT" "$SOURCE_PARENT/index.initial" \
  "materialized candidate"
snapshot_git diff --no-ext-diff --no-textconv --binary --full-index HEAD \
  > "$STAGING/.materialized-candidate.diff"
cmp -s "$STAGING/candidate.diff" "$STAGING/.materialized-candidate.diff" \
  || fail "captured candidate could not be materialized exactly"
snapshot_git ls-files > "$STAGING/.materialized-tree.txt"
cmp -s "$STAGING/tree.txt" "$STAGING/.materialized-tree.txt" \
  || fail "captured tracked inventory could not be materialized exactly"
rm "$STAGING/.materialization.log" "$STAGING/.materialized-candidate.diff" \
  "$STAGING/.materialized-tree.txt"
assert_pristine_materialized_inputs
snapshot_git status --short --untracked-files=all \
  > "$SOURCE_PARENT/status.initial"

if ! (cd "$SOURCE_SNAPSHOT" && ./scripts/verify-corpus.sh) > "$STAGING/corpus-gate.log" 2>&1; then
  cat "$STAGING/corpus-gate.log" >&2
  fail "corpus verification failed"
fi

if ! (cd "$SOURCE_SNAPSHOT" && dotnet build -c Release --nologo) > "$STAGING/build.log" 2>&1; then
  tail -20 "$STAGING/build.log" >&2
  fail "Release build failed"
fi

mapfile -d '' test_projects < <(
  snapshot_git ls-files -z -- ':(glob)tests/**/*.fsproj'
)
[ "${#test_projects[@]}" -gt 0 ] || fail "no test projects were discovered"
for t in "${test_projects[@]}"; do
  n="$(basename -- "${t%.fsproj}")"
  project_key="$(printf '%s' "$t" | sha256sum | cut -c1-16)"
  full_log="$STAGING/.tests-$n-$project_key.full.log"
  if ! (cd "$SOURCE_SNAPSHOT" && dotnet run --project "$t" -c Release --no-build) > "$full_log" 2>&1; then
    cat "$full_log" >&2
    fail "test project failed: $t"
  fi

  summary="$(rg -o 'EXPECTO!.*' "$full_log" | tail -1 || true)"
  if [ -z "$summary" ]; then
    cat "$full_log" >&2
    fail "test project produced no Expecto summary: $t"
  fi

  printf 'project: %s\n%s\n' "$t" "$summary" > "$STAGING/tests-$n-$project_key.log"
  rm "$full_log"
done

# The prerequisite processes receive the isolated source as their cwd. Refuse
# any lasting tracked, staging-state, HEAD, inventory, or non-ignored mutation
# they leave behind. Ignored build outputs are expected and deliberately absent
# from this audit.
assert_no_materialized_content_filters "post-prerequisite"
snapshot_git ls-files --stage -z > "$SOURCE_PARENT/index.final"
cmp -s "$SOURCE_PARENT/index.initial" "$SOURCE_PARENT/index.final" \
  || fail "materialized candidate changed while prerequisites ran: index"
assert_raw_index_identity "$SOURCE_SNAPSHOT" "$SOURCE_PARENT/index.final" \
  "materialized candidate changed while prerequisites ran:"
snapshot_git diff --no-ext-diff --no-textconv --binary --full-index HEAD \
  > "$SOURCE_PARENT/candidate.final"
cmp -s "$STAGING/candidate.diff" "$SOURCE_PARENT/candidate.final" \
  || fail "materialized candidate changed while prerequisites ran: candidate.diff"
snapshot_git status --short --untracked-files=all \
  > "$SOURCE_PARENT/status.final"
cmp -s "$SOURCE_PARENT/status.initial" "$SOURCE_PARENT/status.final" \
  || fail "materialized candidate changed while prerequisites ran: status"
[ "$(snapshot_git rev-parse HEAD)" = "$(cat "$STAGING/base-commit.txt")" ] \
  || fail "materialized candidate changed while prerequisites ran: HEAD"
snapshot_git ls-files > "$SOURCE_PARENT/tree.final"
cmp -s "$STAGING/tree.txt" "$SOURCE_PARENT/tree.final" \
  || fail "materialized candidate changed while prerequisites ran: tracked inventory"

# The build and test logs only apply to the candidate captured above. Refuse a
# concurrent edit, staging-state update, HEAD move, or tracked inventory change
# instead of publishing a self-consistent manifest for mismatched source and
# measurements. The final snapshot is removed before manifest construction.
FINAL_SNAPSHOT="$(mktemp -d "$STAGING/.candidate-final.XXXXXX")"
capture_candidate "$FINAL_SNAPSHOT"
for candidate_file in diffstat.txt candidate.diff status-before-commit.txt base-commit.txt tree.txt; do
  if ! cmp -s "$STAGING/$candidate_file" "$FINAL_SNAPSHOT/$candidate_file"; then
    fail "candidate source changed while prerequisites ran: $candidate_file"
  fi
done
rm -rf -- "$FINAL_SNAPSHOT"

root_git worktree remove --force "$SOURCE_SNAPSHOT"
SOURCE_SNAPSHOT=""
rm -rf -- "$SOURCE_PARENT"
SOURCE_PARENT=""

(
  cd "$STAGING"
  mapfile -d '' manifest_files < <(find . -maxdepth 1 -type f ! -name SHA256SUMS -print0 | sort -z)
  [ "${#manifest_files[@]}" -gt 0 ] || exit 1
  sha256sum -- "${manifest_files[@]}" > SHA256SUMS
  sha256sum -c SHA256SUMS >/dev/null
)

if mv --help 2>&1 | rg -q -- '--no-target-directory'; then
  # HeMan and the hosted Linux gate use GNU mv. `-T` refuses a destination that
  # appears during the run instead of moving the staging directory inside it;
  # `-n` additionally preserves an empty destination. A race may be reported as
  # failure or as a successful no-clobber skip, so inspect both destination and
  # source state.
  if ! mv -T -n "$STAGING" "$DIR"; then
    [ ! -e "$DIR" ] || fail "evidence destination appeared during sealing: $DIR"
    fail "atomic evidence publication failed: $DIR"
  fi
  [ ! -e "$STAGING" ] || fail "evidence destination appeared during sealing: $DIR"
else
  # Developer portability for non-GNU hosts. Recheck immediately before rename;
  # the authoritative concurrency guarantee is measured on HeMan's GNU path.
  [ ! -e "$DIR" ] || fail "evidence destination appeared during sealing: $DIR"
  mv "$STAGING" "$DIR"
fi
STAGING=""
trap - EXIT
echo "sealed $DIR"
echo "  manifest: $(sha256sum "$DIR/SHA256SUMS" | cut -c1-16)"
