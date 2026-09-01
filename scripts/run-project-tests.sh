#!/usr/bin/env bash
# Run every tracked test project, independent of directory depth or filename.
# The caller must build first; this runner deliberately uses --no-build so a
# test execution cannot hide an inventory or configuration error behind a
# second implicit build.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
LOG_DIR=""

if [ "${1:-}" = "--log-dir" ]; then
  [ "$#" -eq 2 ] || { echo "usage: $0 [--log-dir DIR]" >&2; exit 2; }
  LOG_DIR="$2"
elif [ "$#" -ne 0 ]; then
  echo "usage: $0 [--log-dir DIR]" >&2
  exit 2
fi

if [ -n "$LOG_DIR" ]; then
  mkdir -p -- "$LOG_DIR"
  LOG_DIR="$(cd "$LOG_DIR" && pwd -P)"
fi

scratch="$(mktemp -d /tmp/fogell-project-tests.XXXXXX)"
trap 'rm -rf -- "$scratch"' EXIT

repository_git() {
  # `git -C` does not override inherited repository/index selectors. Bind the
  # inventory to the repository containing this script, even when a caller or
  # hosted runner exports Git plumbing state for a different checkout.
  env -u GIT_DIR -u GIT_WORK_TREE -u GIT_COMMON_DIR -u GIT_INDEX_FILE \
    -u GIT_OBJECT_DIRECTORY -u GIT_ALTERNATE_OBJECT_DIRECTORIES \
    -u GIT_QUARANTINE_PATH -u GIT_GRAFT_FILE -u GIT_SHALLOW_FILE \
    -u GIT_REPLACE_REF_BASE -u GIT_PREFIX -u GIT_NAMESPACE \
    -u GIT_CONFIG_COUNT -u GIT_CONFIG_PARAMETERS \
    git -C "$ROOT" --work-tree="$ROOT" \
      -c core.hooksPath=/dev/null -c core.fsmonitor=false \
      -c color.ui=false -c color.status=false "$@"
}

test_projects=()
mapfile -d '' test_projects < <(
  repository_git ls-files -z -- ':(glob)tests/**/*.fsproj'
)
inventory_pid=$!
if ! wait "$inventory_pid"; then
  echo "tracked test project inventory could not be read" >&2
  exit 1
fi
[ "${#test_projects[@]}" -gt 0 ] || {
  echo "no test projects were discovered" >&2
  exit 1
}

for project in "${test_projects[@]}"; do
  full_log="$(mktemp "$scratch/test.XXXXXX")"
  test_rc=0
  (
    cd "$ROOT"
    dotnet run --project "$project" -c Release --no-build
  ) >"$full_log" 2>&1 || test_rc=$?

  if [ "$test_rc" -ne 0 ]; then
    cat "$full_log" >&2
    echo "test project failed: $project" >&2
    exit 1
  fi

  normalized_log="$(mktemp "$scratch/normalized.XXXXXX")"
  # Expecto can retain ANSI colour and its timestamp/logger envelope even when
  # redirected (HeMan does). Remove terminal decoration, then count summary
  # MARKERS rather than lines: a failure and a forged success on one physical
  # line are still two summaries and must refuse.
  sed $'s/\033\\[[0-9;?]*[ -\\/]*[@-~]//g' "$full_log" >"$normalized_log"
  summary_markers="$(
    awk '
      {
        rest = $0
        while ((position = index(rest, "EXPECTO! ")) != 0) {
          count++
          rest = substr(rest, position + 9)
        }
      }
      END { print count + 0 }
    ' "$normalized_log"
  )"
  if [ "$summary_markers" -eq 0 ]; then
    cat "$full_log" >&2
    echo "test project produced no Expecto summary: $project" >&2
    exit 1
  fi
  if [ "$summary_markers" -ne 1 ]; then
    cat "$full_log" >&2
    echo "test project produced multiple Expecto summaries: $project" >&2
    exit 1
  fi

  summaries=()
  # One marker exists. Extract its semantic summary through Expecto's explicit
  # terminator; a marker without a well-formed terminator is a non-success below.
  mapfile -t summaries < <(
    sed -E -n 's/^.*(EXPECTO! .*(Success|Failure)!).*$/\1/p' "$normalized_log"
  )
  summary="${summaries[0]:-}"
  summary_pattern='^EXPECTO! ([0-9]{1,9}) tests? run .* ([0-9]{1,9}) passed, ([0-9]{1,9}) ignored, ([0-9]{1,9}) failed, ([0-9]{1,9}) errored\. Success!$'
  summary_valid=false
  if [[ "$summary" =~ $summary_pattern ]]; then
    total=$((10#${BASH_REMATCH[1]}))
    passed=$((10#${BASH_REMATCH[2]}))
    ignored=$((10#${BASH_REMATCH[3]}))
    failed=$((10#${BASH_REMATCH[4]}))
    errored=$((10#${BASH_REMATCH[5]}))
    if [ "$total" -gt 0 ] \
      && [ "$failed" -eq 0 ] \
      && [ "$errored" -eq 0 ] \
      && [ "$total" -eq $((passed + ignored + failed + errored)) ]; then
      summary_valid=true
    fi
  fi
  if [ "$summary_valid" != true ]; then
    cat "$full_log" >&2
    echo "test project produced a non-success Expecto summary: $project" >&2
    exit 1
  fi

  printf '%q: %s\n' "$project" "$summary"
  if [ -n "$LOG_DIR" ]; then
    project_key="$(printf '%s' "$project" | sha256sum | cut -c1-16)"
    {
      # Paths are Git/NUL-safe and can contain newlines. Shell escaping keeps
      # each value on one line and is reversible with a standard shell parser;
      # raw `%s` would let a path forge additional evidence-log fields.
      printf 'project: %q\n' "$project"
      printf 'working-directory: %q\n' "$ROOT"
      printf '%s\n' "$summary"
    } >"$LOG_DIR/tests-$project_key.log"
  fi
done
