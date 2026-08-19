#!/usr/bin/env bash
# Atomically bind one FG-177 runner invocation to its oracle, log and receipts.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

if [[ $# -lt 11 ]]; then
  echo "usage: $0 MANIFEST RUN STARTED VERIFIED FINISHED RC EXIT_KEY LOG EXIT_FILE METADATA CASE..." >&2
  exit 2
fi

manifest=$1
run_name=$2
started_at=$3
verified_at=$4
finished_at=$5
cli_rc=$6
exit_key=$7
log=$8
exit_file=$9
metadata=${10}
shift 10
cases=("$@")

fail() {
  printf 'ERROR: run manifest refused: %s\n' "$*" >&2
  exit 1
}

timestamp_pattern='^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'
for timestamp in "$started_at" "$verified_at" "$finished_at"; do
  [[ "$timestamp" =~ $timestamp_pattern ]] || fail "non-canonical UTC timestamp: $timestamp"
done
[[ "$started_at" < "$verified_at" || "$started_at" == "$verified_at" ]] ||
  fail 'verification timestamp precedes run start'
[[ "$verified_at" < "$finished_at" || "$verified_at" == "$finished_at" ]] ||
  fail 'finish timestamp precedes oracle verification'
[[ "$cli_rc" =~ ^[0-9]+$ && "$cli_rc" -le 255 ]] || fail "invalid CLI status: $cli_rc"
[[ "$run_name" =~ ^[a-z0-9-]+$ ]] || fail "invalid run name: $run_name"
[[ "$exit_key" =~ ^[a-z0-9-]+$ ]] || fail "invalid exit key: $exit_key"
[[ ${#cases[@]} -gt 0 ]] || fail 'run has no ordered cases'
[[ -f "$log" ]] || fail "missing run log: $log"
[[ -f "$exit_file" ]] || fail "missing exit marker: $exit_file"
[[ $(<"$exit_file") == "$exit_key=$cli_rc" ]] || fail 'exit marker does not match CLI status'

core_file="$metadata/jenkins-core.txt"
plugins_file="$metadata/jenkins-plugins.tsv"
image_file="$metadata/jenkins-controller-image.txt"
for provenance_file in "$core_file" "$plugins_file" "$image_file"; do
  [[ -f "$provenance_file" ]] || fail "missing oracle provenance: $provenance_file"
done

mapfile -t core_lines < "$core_file"
[[ ${#core_lines[@]} -eq 1 ]] || fail 'core metadata is not one line'
core=${core_lines[0]}
mapfile -t image_lines < "$image_file"
[[ ${#image_lines[@]} -eq 1 ]] || fail 'image metadata is not one line'
IFS='|' read -r image_name image_id image_digest extra <<< "${image_lines[0]}"
[[ -n "$image_name" && "$image_id" =~ ^[0-9a-f]{64}$ &&
   "$image_digest" =~ ^sha256:[0-9a-f]{64}$ && -z "$extra" ]] ||
  fail 'image metadata is malformed'

digest() {
  sha256sum "$1" | awk '{ print $1 }'
}

manifest_dir=$(dirname "$manifest")
[[ -d "$manifest_dir" ]] || fail "manifest directory does not exist: $manifest_dir"
manifest_tmp=$(mktemp "$manifest.tmp.XXXXXX")
trap 'rm -f "$manifest_tmp"' EXIT
receipt_dir="$manifest_dir/raw-receipts"

expected_receipts=()
for case_file in "${cases[@]}"; do
  case_name=$(basename "$case_file")
  expected_receipts+=("${case_name%.Jenkinsfile}.receipt.txt")
done
[[ -d "$receipt_dir" && ! -L "$receipt_dir" ]] ||
  fail "receipt directory is missing or is a symlink: $receipt_dir"
mapfile -d '' -t observed_receipts < <(
  find "$receipt_dir" -mindepth 1 -maxdepth 1 -printf '%f\0' 2>/dev/null | sort -z
)
mapfile -d '' -t sorted_expected_receipts < <(
  printf '%s\0' "${expected_receipts[@]}" | sort -z
)
receipt_set_matches=true
if [[ ${#observed_receipts[@]} -ne ${#sorted_expected_receipts[@]} ]]; then
  receipt_set_matches=false
else
  for index in "${!observed_receipts[@]}"; do
    if [[ "${observed_receipts[$index]}" != "${sorted_expected_receipts[$index]}" ]]; then
      receipt_set_matches=false
      break
    fi
  done
fi
if [[ "$receipt_set_matches" != true ]]; then
  fail "receipt set mismatch: expected [${sorted_expected_receipts[*]}], observed [${observed_receipts[*]}]"
fi
for receipt_name in "${expected_receipts[@]}"; do
  receipt="$receipt_dir/$receipt_name"
  [[ -f "$receipt" && ! -L "$receipt" && -s "$receipt" ]] ||
    fail "receipt is missing, empty, or not a regular file: $receipt"
done

{
  printf 'format\tfogell-evidence-run-v1\n'
  printf 'run\t%s\n' "$run_name"
  printf 'started-at-utc\t%s\n' "$started_at"
  printf 'oracle-verified-at-utc\t%s\n' "$verified_at"
  printf 'finished-at-utc\t%s\n' "$finished_at"
  printf 'cli-exit\t%s\n' "$cli_rc"
  printf 'jenkins-core\t%s\n' "$core"
  printf 'core-metadata-sha256\t%s\n' "$(digest "$core_file")"
  printf 'plugin-manifest-sha256\t%s\n' "$(digest "$plugins_file")"
  printf 'plugin-count\t%s\n' "$(wc -l < "$plugins_file")"
  printf 'controller-image-name\t%s\n' "$image_name"
  printf 'controller-image-id\t%s\n' "$image_id"
  printf 'controller-image-digest\t%s\n' "$image_digest"
  printf 'image-metadata-sha256\t%s\n' "$(digest "$image_file")"
  printf 'run-log\t%s\t%s\n' "$(basename "$log")" "$(digest "$log")"
  printf 'exit-marker\t%s\t%s\n' "$(basename "$exit_file")" "$(digest "$exit_file")"
  printf 'case-count\t%s\n' "${#cases[@]}"
  ordinal=0
  for case_file in "${cases[@]}"; do
    [[ -f "$case_file" ]] || fail "missing rendered case: $case_file"
    case_name=$(basename "$case_file")
    receipt_name=${case_name%.Jenkinsfile}.receipt.txt
    receipt="$receipt_dir/$receipt_name"
    [[ -f "$receipt" ]] || fail "missing receipt: $receipt"
    ordinal=$((ordinal + 1))
    printf 'case\t%02d\t%s\t%s\t%s\t%s\n' \
      "$ordinal" "$case_name" "$(digest "$case_file")" \
      "$receipt_name" "$(digest "$receipt")"
  done
} > "$manifest_tmp"

mv "$manifest_tmp" "$manifest"
trap - EXIT
printf 'run manifest %s bound %s ordered case(s) to Jenkins %s\n' \
  "$manifest" "${#cases[@]}" "$core"
