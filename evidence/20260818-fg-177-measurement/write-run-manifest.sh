#!/usr/bin/env bash
# Atomically bind one FG-177 runner invocation to its oracle, log and receipts.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

if [[ $# -lt 15 ]]; then
  echo "usage: $0 MANIFEST RUN STARTED VERIFIED_BEFORE VERIFIED_AFTER FINISHED RC EXIT_KEY LOG EXIT_FILE REQUESTED_CORE METADATA BEFORE_RECEIPT AFTER_RECEIPT CASE..." >&2
  exit 2
fi

manifest=$1
run_name=$2
started_at=$3
verified_before_at=$4
verified_after_at=$5
finished_at=$6
cli_rc=$7
exit_key=$8
log=$9
exit_file=${10}
requested_core=${11}
metadata=${12}
verification_before=${13}
verification_after=${14}
shift 14
cases=("$@")

fail() {
  printf 'ERROR: run manifest refused: %s\n' "$*" >&2
  exit 1
}

require_regular_nonempty() {
  local label=$1
  local path=$2
  [[ -f "$path" && ! -L "$path" && -s "$path" ]] ||
    fail "$label is missing, empty, symlinked, or not a regular file: $path"
}

timestamp_pattern='^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'
for timestamp in "$started_at" "$verified_before_at" "$verified_after_at" "$finished_at"; do
  [[ "$timestamp" =~ $timestamp_pattern ]] || fail "non-canonical UTC timestamp: $timestamp"
done
[[ "$started_at" < "$verified_before_at" || "$started_at" == "$verified_before_at" ]] ||
  fail 'verification timestamp precedes run start'
[[ "$verified_before_at" < "$verified_after_at" || "$verified_before_at" == "$verified_after_at" ]] ||
  fail 'post-CLI verification timestamp precedes pre-CLI verification'
[[ "$verified_after_at" < "$finished_at" || "$verified_after_at" == "$finished_at" ]] ||
  fail 'finish timestamp precedes post-CLI oracle verification'
[[ "$cli_rc" =~ ^[0-9]+$ && "$cli_rc" -le 255 ]] || fail "invalid CLI status: $cli_rc"
[[ "$run_name" =~ ^[a-z0-9-]+$ ]] || fail "invalid run name: $run_name"
[[ "$exit_key" =~ ^[a-z0-9-]+$ ]] || fail "invalid exit key: $exit_key"
[[ "$requested_core" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]] ||
  fail "requested Jenkins core is not canonical: $requested_core"
[[ ${#cases[@]} -gt 0 ]] || fail 'run has no ordered cases'
require_regular_nonempty 'run log' "$log"
require_regular_nonempty 'exit marker' "$exit_file"
[[ $(<"$exit_file") == "$exit_key=$cli_rc" ]] || fail 'exit marker does not match CLI status'
for verification in "$verification_before" "$verification_after"; do
  require_regular_nonempty 'oracle verification receipt' "$verification"
done
cmp -s "$verification_before" "$verification_after" ||
  fail 'pre/post oracle verification identities differ'

core_file="$metadata/jenkins-core.txt"
plugins_file="$metadata/jenkins-plugins.tsv"
image_file="$metadata/jenkins-controller-image.txt"
[[ -d "$metadata" && ! -L "$metadata" ]] ||
  fail "oracle metadata directory is missing, symlinked, or not a directory: $metadata"
for provenance_file in "$core_file" "$plugins_file" "$image_file"; do
  require_regular_nonempty 'oracle provenance' "$provenance_file"
done

expected_metadata=(
  jenkins-controller-image.txt
  jenkins-core.txt
  jenkins-plugins.tsv
)
mapfile -d '' -t observed_metadata < <(
  find "$metadata" -mindepth 1 -maxdepth 1 -printf '%f\0' 2>/dev/null | sort -z
)
if [[ ${#observed_metadata[@]} -ne ${#expected_metadata[@]} ]]; then
  fail "oracle snapshot does not contain the exact three-file set"
fi
for index in "${!expected_metadata[@]}"; do
  [[ "${observed_metadata[$index]}" == "${expected_metadata[$index]}" ]] ||
    fail "oracle snapshot has unexpected entry: ${observed_metadata[$index]}"
done

manifest_dir=$(dirname "$manifest")
[[ -d "$manifest_dir" && ! -L "$manifest_dir" ]] ||
  fail "manifest directory is missing, symlinked, or not a directory: $manifest_dir"
[[ "$metadata" == "$manifest_dir/oracle-metadata" ]] ||
  fail 'oracle snapshot must be the manifest sibling named oracle-metadata'
metadata_copy=$(mktemp -d "$manifest_dir/.oracle-metadata-copy.XXXXXX")
manifest_tmp=$(mktemp "$manifest.tmp.XXXXXX")
cleanup() {
  rm -rf "$metadata_copy"
  rm -f "$manifest_tmp"
}
trap cleanup EXIT
cp -- "$core_file" "$metadata_copy/jenkins-core.txt"
cp -- "$plugins_file" "$metadata_copy/jenkins-plugins.tsv"
cp -- "$image_file" "$metadata_copy/jenkins-controller-image.txt"
copied_core_file="$metadata_copy/jenkins-core.txt"
copied_plugins_file="$metadata_copy/jenkins-plugins.tsv"
copied_image_file="$metadata_copy/jenkins-controller-image.txt"
for provenance_file in \
  "$copied_core_file" "$copied_plugins_file" "$copied_image_file"
do
  require_regular_nonempty 'copied oracle provenance' "$provenance_file"
done

mapfile -t core_lines < "$copied_core_file"
[[ ${#core_lines[@]} -eq 1 ]] || fail 'core metadata is not one line'
core=${core_lines[0]}
[[ "$core" == "$requested_core" ]] ||
  fail "requested Jenkins $requested_core differs from manifest metadata $core"
mapfile -t image_lines < "$copied_image_file"
[[ ${#image_lines[@]} -eq 1 ]] || fail 'image metadata is not one line'
IFS='|' read -r image_name image_id image_digest extra <<< "${image_lines[0]}"
[[ -n "$image_name" && "$image_id" =~ ^[0-9a-f]{64}$ &&
   "$image_digest" =~ ^sha256:[0-9a-f]{64}$ && -z "$extra" ]] ||
  fail 'image metadata is malformed'

digest() {
  sha256sum "$1" | awk '{ print $1 }'
}

receipt_keys=(
  format
  jenkins-core
  jenkins-session-sha256
  controller-container-id
  core-metadata-sha256
  plugin-count
  plugin-manifest-sha256
  controller-image-name
  controller-image-id
  controller-image-digest
  image-metadata-sha256
)
mapfile -t receipt_lines < "$verification_before"
[[ ${#receipt_lines[@]} -eq ${#receipt_keys[@]} ]] ||
  fail 'oracle verification receipt has the wrong line count'
declare -A receipt_values=()
for index in "${!receipt_keys[@]}"; do
  IFS=$'\t' read -r key value extra <<< "${receipt_lines[$index]}"
  [[ "$key" == "${receipt_keys[$index]}" && -n "$value" && -z "$extra" ]] ||
    fail "oracle verification receipt is noncanonical at line $((index + 1))"
  receipt_values[$key]=$value
done

core_metadata_digest=$(digest "$copied_core_file")
plugin_manifest_digest=$(digest "$copied_plugins_file")
plugin_count=$(wc -l < "$copied_plugins_file")
image_metadata_digest=$(digest "$copied_image_file")
[[ "${receipt_values[format]}" == fogell-jenkins-oracle-v2 ]] ||
  fail 'oracle verification receipt format is unsupported'
[[ "${receipt_values[jenkins-session-sha256]}" =~ ^[0-9a-f]{64}$ ]] ||
  fail 'oracle receipt session identity hash is malformed'
[[ "${receipt_values[controller-container-id]}" =~ ^[0-9a-f]{64}$ ]] ||
  fail 'oracle receipt controller container identity is malformed'
[[ "${receipt_values[jenkins-core]}" == "$core" ]] ||
  fail 'oracle receipt core differs from the staged snapshot'
[[ "${receipt_values[core-metadata-sha256]}" == "$core_metadata_digest" ]] ||
  fail 'oracle receipt core digest differs from the staged snapshot'
[[ "${receipt_values[plugin-count]}" == "$plugin_count" ]] ||
  fail 'oracle receipt plugin count differs from the staged snapshot'
[[ "${receipt_values[plugin-manifest-sha256]}" == "$plugin_manifest_digest" ]] ||
  fail 'oracle receipt plugin digest differs from the staged snapshot'
[[ "${receipt_values[controller-image-name]}" == "$image_name" &&
   "${receipt_values[controller-image-id]}" == "$image_id" &&
   "${receipt_values[controller-image-digest]}" == "$image_digest" ]] ||
  fail 'oracle receipt image identity differs from the staged snapshot'
[[ "${receipt_values[image-metadata-sha256]}" == "$image_metadata_digest" ]] ||
  fail 'oracle receipt image digest differs from the staged snapshot'

receipt_dir="$manifest_dir/raw-receipts"

expected_receipts=()
for case_file in "${cases[@]}"; do
  require_regular_nonempty 'rendered case' "$case_file"
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

scm_pin_digest=''
scm_execution_digest=''
if [[ "$run_name" == probes ]]; then
  scm_pin="$manifest_dir/scm-pin.tsv"
  scm_execution="$manifest_dir/scm-execution.tsv"
  require_regular_nonempty 'SCM pin' "$scm_pin"
  require_regular_nonempty 'SCM execution' "$scm_execution"
  pin_keys=(
    format source-branch source-revision scm-pinned-branch
    scm-pinned-revision scm-tree jenkinsfile-blob jenkinsfile-sha256
    git-pinned-branch git-pinned-revision git-tree
  )
  mapfile -t pin_lines < "$scm_pin"
  [[ ${#pin_lines[@]} -eq ${#pin_keys[@]} ]] || fail 'SCM pin has the wrong line count'
  declare -A pin_values=()
  for index in "${!pin_keys[@]}"; do
    IFS=$'\t' read -r key value extra <<< "${pin_lines[$index]}"
    [[ "$key" == "${pin_keys[$index]}" && -n "$value" && -z "$extra" ]] ||
      fail "SCM pin is noncanonical at line $((index + 1))"
    pin_values[$key]=$value
  done
  [[ "${pin_values[format]}" == fogell-scm-pin-v1 ]] || fail 'unsupported SCM pin format'
  [[ "${pin_values[source-branch]}" == case/fg177-probe-checkout-scm ]] ||
    fail 'SCM pin source branch is not the reserved evidence branch'
  sha1_pattern='^[0-9a-f]{40}$'
  sha256_pattern='^[0-9a-f]{64}$'
  for key in source-revision scm-pinned-revision scm-tree jenkinsfile-blob git-pinned-revision git-tree; do
    [[ "${pin_values[$key]}" =~ $sha1_pattern ]] || fail "SCM pin $key is malformed"
  done
  [[ "${pin_values[jenkinsfile-sha256]}" =~ $sha256_pattern ]] ||
    fail 'SCM pin Jenkinsfile SHA-256 is malformed'
  [[ "${pin_values[source-revision]}" == "${pin_values[scm-pinned-revision]}" &&
     "${pin_values[scm-pinned-branch]}" == "fogell-pins/${pin_values[scm-pinned-revision]}" &&
     "${pin_values[git-pinned-branch]}" == "fogell-pins/${pin_values[git-pinned-revision]}" ]] ||
    fail 'SCM pin branches are not content-addressed'
  scm_expected="${pin_values[scm-pinned-revision]}:${pin_values[scm-tree]}:${pin_values[jenkinsfile-blob]}"
  git_expected="${pin_values[git-pinned-revision]}:${pin_values[git-tree]}"
  checkout_receipt="$receipt_dir/fg177-probe-checkout-scm.receipt.txt"
  unknown_receipt="$receipt_dir/fg177-probe-unknown-policy.receipt.txt"
  return_receipt="$receipt_dir/fg177-probe-return-semantics.receipt.txt"
  checkout_case=''
  for case_file in "${cases[@]}"; do
    if [[ $(basename "$case_file") == fg177-probe-checkout-scm.Jenkinsfile ]]; then
      checkout_case=$case_file
      break
    fi
  done
  [[ -n "$checkout_case" && $(sed -n '1p' "$checkout_case") == '//// SCM JOB ////' ]] ||
    fail 'probe run lacks the canonical SCM-marker case'
  checkout_body_sha256=$(tail -n +2 "$checkout_case" | sha256sum | awk '{ print $1 }')
  checkout_body_blob=$(tail -n +2 "$checkout_case" | git hash-object --stdin)
  [[ "$checkout_body_sha256" == "${pin_values[jenkinsfile-sha256]}" &&
     "$checkout_body_blob" == "${pin_values[jenkinsfile-blob]}" ]] ||
    fail 'SCM pin Jenkinsfile identity differs from the rendered checkout case body'
  mapfile -t execution_lines < "$scm_execution"
  [[ ${#execution_lines[@]} -eq 10 &&
     "${execution_lines[0]}" == $'format\tfogell-scm-execution-v2' ]] ||
    fail 'SCM execution report is noncanonical'
  expected_execution_lines=(
    $'format\tfogell-scm-execution-v2'
    "case"$'\t'"checkout"$'\t'"scm"$'\t'"$scm_expected"$'\t'"$(digest "$checkout_receipt")"
    "attested"$'\t'"checkout"$'\t'"jenkins"$'\t'"executed"$'\t'"$scm_expected"
    "attested"$'\t'"checkout"$'\t'"fogell"$'\t'"preflight"$'\t'"$scm_expected"
    "case"$'\t'"unknown-policy-git"$'\t'"git"$'\t'"$git_expected"$'\t'"$(digest "$unknown_receipt")"
    "attested"$'\t'"unknown-policy-git"$'\t'"jenkins"$'\t'"executed"$'\t'"$git_expected"
    ""
    "case"$'\t'"return-semantics-git"$'\t'"git"$'\t'"$git_expected"$'\t'"$(digest "$return_receipt")"
    "attested"$'\t'"return-semantics-git"$'\t'"jenkins"$'\t'"executed"$'\t'"$git_expected"
    ""
  )
  for index in "${!expected_execution_lines[@]}"; do
    if [[ -n "${expected_execution_lines[$index]}" ]]; then
      [[ "${execution_lines[$index]}" == "${expected_execution_lines[$index]}" ]] ||
        fail "SCM execution report differs at line $((index + 1))"
    else
      IFS=$'\t' read -r kind case_name engine state identity extra <<< "${execution_lines[$index]}"
      expected_case=unknown-policy-git
      [[ "$index" -ne 9 ]] || expected_case=return-semantics-git
      [[ "$kind" == attested && "$engine" == fogell &&
         "$case_name" == "$expected_case" &&
         ( "$state" == executed || "$state" == not-executed ) &&
         "$identity" == "$git_expected" && -z "$extra" ]] ||
        fail "SCM execution report differs at line $((index + 1))"
    fi
  done
  scm_pin_digest=$(digest "$scm_pin")
  scm_execution_digest=$(digest "$scm_execution")
elif [[ -e "$manifest_dir/scm-pin.tsv" || -L "$manifest_dir/scm-pin.tsv" ||
        -e "$manifest_dir/scm-execution.tsv" || -L "$manifest_dir/scm-execution.tsv" ]]; then
  fail 'non-probe run contains unexpected SCM binding files'
fi

verification_before_digest=$(digest "$verification_before")
verification_after_digest=$(digest "$verification_after")
[[ "$verification_before_digest" == "$verification_after_digest" ]] ||
  fail 'pre/post oracle verification identities changed before hashing'
log_digest=$(digest "$log")
exit_digest=$(digest "$exit_file")
case_digests=()
receipt_digests=()
for index in "${!cases[@]}"; do
  case_file=${cases[$index]}
  case_name=$(basename "$case_file")
  receipt_name=${case_name%.Jenkinsfile}.receipt.txt
  case_digests+=("$(digest "$case_file")")
  receipt_digests+=("$(digest "$receipt_dir/$receipt_name")")
done

{
  printf 'format\tfogell-evidence-run-v3\n'
  printf 'run\t%s\n' "$run_name"
  printf 'started-at-utc\t%s\n' "$started_at"
  printf 'oracle-verified-before-at-utc\t%s\n' "$verified_before_at"
  printf 'oracle-verified-after-at-utc\t%s\n' "$verified_after_at"
  printf 'finished-at-utc\t%s\n' "$finished_at"
  printf 'cli-exit\t%s\n' "$cli_rc"
  printf 'jenkins-core\t%s\n' "$requested_core"
  printf 'jenkins-session-sha256\t%s\n' "${receipt_values[jenkins-session-sha256]}"
  printf 'controller-container-id\t%s\n' "${receipt_values[controller-container-id]}"
  printf 'oracle-metadata-directory\toracle-metadata\n'
  printf 'core-metadata-sha256\t%s\n' "$core_metadata_digest"
  printf 'plugin-manifest-sha256\t%s\n' "$plugin_manifest_digest"
  printf 'plugin-count\t%s\n' "$plugin_count"
  printf 'controller-image-name\t%s\n' "$image_name"
  printf 'controller-image-id\t%s\n' "$image_id"
  printf 'controller-image-digest\t%s\n' "$image_digest"
  printf 'image-metadata-sha256\t%s\n' "$image_metadata_digest"
  printf 'oracle-before-verification\t%s\t%s\n' \
    "$(basename "$verification_before")" "$verification_before_digest"
  printf 'oracle-after-verification\t%s\t%s\n' \
    "$(basename "$verification_after")" "$verification_after_digest"
  printf 'run-log\t%s\t%s\n' "$(basename "$log")" "$log_digest"
  printf 'exit-marker\t%s\t%s\n' "$(basename "$exit_file")" "$exit_digest"
  printf 'case-count\t%s\n' "${#cases[@]}"
  if [[ "$run_name" == probes ]]; then
    printf 'scm-pin\tscm-pin.tsv\t%s\n' "$scm_pin_digest"
    printf 'scm-execution\tscm-execution.tsv\t%s\n' "$scm_execution_digest"
    printf 'scm-pinned-revision\t%s\n' "${pin_values[scm-pinned-revision]}"
    printf 'scm-tree\t%s\n' "${pin_values[scm-tree]}"
    printf 'jenkinsfile-blob\t%s\n' "${pin_values[jenkinsfile-blob]}"
    printf 'git-pinned-revision\t%s\n' "${pin_values[git-pinned-revision]}"
    printf 'git-tree\t%s\n' "${pin_values[git-tree]}"
  fi
  ordinal=0
  for case_file in "${cases[@]}"; do
    case_name=$(basename "$case_file")
    receipt_name=${case_name%.Jenkinsfile}.receipt.txt
    receipt="$receipt_dir/$receipt_name"
    require_regular_nonempty 'receipt' "$receipt"
    ordinal=$((ordinal + 1))
    printf 'case\t%02d\t%s\t%s\t%s\t%s\n' \
      "$ordinal" "$case_name" "${case_digests[$((ordinal - 1))]}" \
      "$receipt_name" "${receipt_digests[$((ordinal - 1))]}"
  done
} > "$manifest_tmp"

# The staged snapshot is private to the runner, but compare it once more after
# all hashing so a planted or accidental in-writer swap cannot seal different
# bytes than the verifier accepted.
mapfile -d '' -t final_metadata < <(
  find "$metadata" -mindepth 1 -maxdepth 1 -printf '%f\0' 2>/dev/null | sort -z
)
[[ ${#final_metadata[@]} -eq ${#expected_metadata[@]} ]] ||
  fail 'oracle snapshot changed while the manifest was written'
for index in "${!expected_metadata[@]}"; do
  [[ "${final_metadata[$index]}" == "${expected_metadata[$index]}" ]] ||
    fail 'oracle snapshot entries changed while the manifest was written'
done
cmp -s "$core_file" "$copied_core_file" &&
  cmp -s "$plugins_file" "$copied_plugins_file" &&
  cmp -s "$image_file" "$copied_image_file" ||
  fail 'oracle snapshot bytes changed while the manifest was written'
require_regular_nonempty 'run log' "$log"
require_regular_nonempty 'exit marker' "$exit_file"
require_regular_nonempty 'oracle verification receipt' "$verification_before"
require_regular_nonempty 'oracle verification receipt' "$verification_after"
[[ "$(digest "$verification_before")" == "$verification_before_digest" &&
   "$(digest "$verification_after")" == "$verification_after_digest" &&
   "$(digest "$log")" == "$log_digest" &&
   "$(digest "$exit_file")" == "$exit_digest" ]] ||
  fail 'a receipt, log, or exit marker changed while the manifest was written'
if [[ "$run_name" == probes ]]; then
  require_regular_nonempty 'SCM pin' "$scm_pin"
  require_regular_nonempty 'SCM execution' "$scm_execution"
  [[ "$(digest "$scm_pin")" == "$scm_pin_digest" &&
     "$(digest "$scm_execution")" == "$scm_execution_digest" ]] ||
    fail 'SCM pin or execution report changed while the manifest was written'
fi
mapfile -d '' -t final_receipts < <(
  find "$receipt_dir" -mindepth 1 -maxdepth 1 -printf '%f\0' 2>/dev/null | sort -z
)
[[ ${#final_receipts[@]} -eq ${#sorted_expected_receipts[@]} ]] ||
  fail 'receipt set changed while the manifest was written'
for index in "${!final_receipts[@]}"; do
  [[ "${final_receipts[$index]}" == "${sorted_expected_receipts[$index]}" ]] ||
    fail 'receipt entries changed while the manifest was written'
done
for index in "${!cases[@]}"; do
  case_file=${cases[$index]}
  case_name=$(basename "$case_file")
  receipt="$receipt_dir/${case_name%.Jenkinsfile}.receipt.txt"
  require_regular_nonempty 'rendered case' "$case_file"
  require_regular_nonempty 'receipt' "$receipt"
  [[ "$(digest "$case_file")" == "${case_digests[$index]}" &&
     "$(digest "$receipt")" == "${receipt_digests[$index]}" ]] ||
    fail 'a case or receipt changed while the manifest was written'
done

mv "$manifest_tmp" "$manifest"
rm -rf "$metadata_copy"
trap - EXIT
printf 'run manifest %s bound %s ordered case(s) to Jenkins %s\n' \
  "$manifest" "${#cases[@]}" "$core"
