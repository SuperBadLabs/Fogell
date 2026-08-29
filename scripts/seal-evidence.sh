#!/usr/bin/env bash
# FG-005/FG-223 — evidence convention. Seals a ticket's receipt so it verifies standalone.
#   usage: scripts/seal-evidence.sh FG-010 [extra-file ...]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT="$PWD"
# Preserve the caller's historical relative-corpus semantics after prerequisite
# execution moves into the isolated candidate worktree.
if [ -n "${FOGELL_CORPUS:-}" ] && [[ "$FOGELL_CORPUS" != /* ]]; then
  FOGELL_CORPUS="$ROOT/$FOGELL_CORPUS"
  export FOGELL_CORPUS
fi
TICKET="${1:?usage: seal-evidence.sh <TICKET-ID> [files...]}"; shift || true
[[ "$TICKET" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] \
  || { echo "REFUSING TO SEAL: ticket id contains unsafe path characters: $TICKET" >&2; exit 1; }
STAMP="$(git log -1 --format=%cd --date=format:%Y%m%dT%H%M%SZ)"
DIR="evidence/${STAMP}-${TICKET,,}"

fail () {
  echo "REFUSING TO SEAL: $*" >&2
  exit 1
}

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
UNTRACKED="$(git ls-files --others --exclude-standard | while read -r f; do
  # ALL of evidence/ is output, not input. Excluding only the current run's directory
  # still tripped over the PREVIOUS bundle, which makes the check circular: you cannot
  # seal until you stage the last seal. The check exists to catch untracked SOURCE.
  case "$f" in evidence/*) continue ;; esac
  [[ -n "${EXTRA_PATHS[$f]+present}" ]] || printf '%s\n' "$f"
done)"
if [ -n "$UNTRACKED" ]; then
  echo "REFUSING TO SEAL: untracked files would be omitted from the evidence:" >&2
  while IFS= read -r f; do printf '  %s\n' "$f"; done <<< "$UNTRACKED" >&2
  echo "Stage them (git add) so the sealed diff covers the actual change." >&2
  exit 1
fi

# Build the bundle outside its final name. A failed measurement leaves neither a
# checksum nor a directory that looks sealed; the exact mktemp result is the only
# path the cleanup trap may remove.
STAGING="$(mktemp -d "$ROOT/evidence/.${STAMP}-${TICKET,,}.partial.XXXXXX")"
SOURCE_PARENT=""
SOURCE_SNAPSHOT=""
cleanup () {
  if [ -n "${SOURCE_SNAPSHOT:-}" ]; then
    git worktree remove --force "$SOURCE_SNAPSHOT" >/dev/null 2>&1 || true
  fi
  if [ -n "${SOURCE_PARENT:-}" ] && [ -d "$SOURCE_PARENT" ]; then
    rm -rf -- "$SOURCE_PARENT"
  fi
  if [ -n "${STAGING:-}" ] && [ -d "$STAGING" ]; then
    rm -rf -- "$STAGING"
  fi
}
trap cleanup EXIT

capture_tracked_inventory () {
  local destination="$1"
  local candidate_index="$destination/.candidate-index"
  # Derive the inventory represented by HEAD + candidate.diff in a private
  # index. The publishing index alone still names unstaged deletions and thus
  # does not describe the candidate that the prerequisites will consume.
  GIT_INDEX_FILE="$candidate_index" git read-tree HEAD
  if [ -s "$destination/candidate.diff" ]; then
    if ! GIT_INDEX_FILE="$candidate_index" \
      git apply --cached --binary "$destination/candidate.diff"; then
      fail "captured candidate tracked inventory could not be derived"
    fi
  fi
  GIT_INDEX_FILE="$candidate_index" git ls-files > "$destination/tree.txt"
  rm "$candidate_index"
}

capture_candidate () {
  local destination="$1"
  git diff --no-ext-diff --no-textconv HEAD --stat \
                                             > "$destination/diffstat.txt"
  git diff --no-ext-diff --no-textconv --binary --full-index HEAD \
                                             > "$destination/candidate.diff"
  # Untracked evidence/ paths are command output, not candidate source. Filter
  # only those `??` records while retaining tracked evidence changes and every
  # other non-ignored untracked path. This also keeps concurrent sealers from
  # treating each other's private staging directory as source drift.
  git status --short --untracked-files=all | while IFS= read -r status_line; do
    case "$status_line" in
      "?? evidence/"*) continue ;;
    esac
    printf '%s\n' "$status_line"
  done                              > "$destination/status-before-commit.txt"
  git rev-parse HEAD                  > "$destination/base-commit.txt"
  capture_tracked_inventory "$destination"
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
if ! git worktree add --detach "$SOURCE_SNAPSHOT" "$(cat "$STAGING/base-commit.txt")" \
  > "$STAGING/.materialization.log" 2>&1; then
  cat "$STAGING/.materialization.log" >&2
  fail "captured candidate base could not be materialized"
fi
if [ -s "$STAGING/candidate.diff" ]; then
  if ! git -C "$SOURCE_SNAPSHOT" apply --binary --index "$STAGING/candidate.diff" \
    >> "$STAGING/.materialization.log" 2>&1; then
    cat "$STAGING/.materialization.log" >&2
    fail "captured candidate patch could not be materialized"
  fi
fi
git -C "$SOURCE_SNAPSHOT" diff --no-ext-diff --no-textconv --binary --full-index HEAD \
  > "$STAGING/.materialized-candidate.diff"
cmp -s "$STAGING/candidate.diff" "$STAGING/.materialized-candidate.diff" \
  || fail "captured candidate could not be materialized exactly"
git -C "$SOURCE_SNAPSHOT" ls-files > "$STAGING/.materialized-tree.txt"
cmp -s "$STAGING/tree.txt" "$STAGING/.materialized-tree.txt" \
  || fail "captured tracked inventory could not be materialized exactly"
rm "$STAGING/.materialization.log" "$STAGING/.materialized-candidate.diff" \
  "$STAGING/.materialized-tree.txt"
git -C "$SOURCE_SNAPSHOT" status --short --untracked-files=all \
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
  git -C "$SOURCE_SNAPSHOT" ls-files -z -- ':(glob)tests/**/*.fsproj'
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
git -C "$SOURCE_SNAPSHOT" diff --no-ext-diff --no-textconv --binary --full-index HEAD \
  > "$SOURCE_PARENT/candidate.final"
cmp -s "$STAGING/candidate.diff" "$SOURCE_PARENT/candidate.final" \
  || fail "materialized candidate changed while prerequisites ran: candidate.diff"
git -C "$SOURCE_SNAPSHOT" status --short --untracked-files=all \
  > "$SOURCE_PARENT/status.final"
cmp -s "$SOURCE_PARENT/status.initial" "$SOURCE_PARENT/status.final" \
  || fail "materialized candidate changed while prerequisites ran: status"
[ "$(git -C "$SOURCE_SNAPSHOT" rev-parse HEAD)" = "$(cat "$STAGING/base-commit.txt")" ] \
  || fail "materialized candidate changed while prerequisites ran: HEAD"
git -C "$SOURCE_SNAPSHOT" ls-files > "$SOURCE_PARENT/tree.final"
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

git worktree remove --force "$SOURCE_SNAPSHOT"
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
