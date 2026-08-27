#!/usr/bin/env bash
# FG-005/FG-223 — evidence convention. Seals a ticket's receipt so it verifies standalone.
#   usage: scripts/seal-evidence.sh FG-010 [extra-file ...]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
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
  name="$(basename "$f")"
  case "$name" in
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
STAGING="$(mktemp -d "evidence/.${STAMP}-${TICKET,,}.partial.XXXXXX")"
cleanup () {
  if [ -n "${STAGING:-}" ] && [ -d "$STAGING" ]; then
    rm -rf -- "$STAGING"
  fi
}
trap cleanup EXIT

git diff HEAD --stat > "$STAGING/diffstat.txt"
git diff HEAD          > "$STAGING/candidate.diff"
git status --short     > "$STAGING/status-before-commit.txt"
git rev-parse HEAD     > "$STAGING/base-commit.txt"
git ls-files           > "$STAGING/tree.txt"

if ! ./scripts/verify-corpus.sh > "$STAGING/corpus-gate.log" 2>&1; then
  cat "$STAGING/corpus-gate.log" >&2
  fail "corpus verification failed"
fi

if ! dotnet build -c Release --nologo > "$STAGING/build.log" 2>&1; then
  tail -20 "$STAGING/build.log" >&2
  fail "Release build failed"
fi

tests_run=0
for t in tests/*/; do
  n="$(basename "$t")"
  [ -f "$t/$n.fsproj" ] || continue
  tests_run=$((tests_run + 1))
  full_log="$STAGING/.tests-$n.full.log"
  if ! dotnet run --project "$t" -c Release --no-build > "$full_log" 2>&1; then
    cat "$full_log" >&2
    fail "test project failed: $n"
  fi

  summary="$(rg -o 'EXPECTO!.*' "$full_log" | tail -1 || true)"
  if [ -z "$summary" ]; then
    cat "$full_log" >&2
    fail "test project produced no Expecto summary: $n"
  fi

  printf '%s\n' "$summary" > "$STAGING/tests-$n.log"
  rm "$full_log"
done
[ "$tests_run" -gt 0 ] || fail "no test projects were discovered"

for f in "$@"; do
  cp "$f" "$STAGING/"
done

(
  cd "$STAGING"
  mapfile -d '' manifest_files < <(find . -maxdepth 1 -type f ! -name SHA256SUMS -print0 | sort -z)
  [ "${#manifest_files[@]}" -gt 0 ] || exit 1
  sha256sum -- "${manifest_files[@]}" > SHA256SUMS
  sha256sum -c SHA256SUMS >/dev/null
)

if mv --help 2>&1 | rg -q -- '--no-target-directory'; then
  # HeMan and the hosted Linux gate use GNU mv. `-T` refuses a destination that
  # appears during the run instead of moving the staging directory inside it.
  mv -T "$STAGING" "$DIR"
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
