#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

evidence='evidence/20260818-fg-177-measurement'
PYTHONDONTWRITEBYTECODE=1 python3 "$evidence/test-publish-run-bundle.py"
proof_tmp=$(mktemp -d)
trap 'rm -rf "$proof_tmp"' EXIT
out="$proof_tmp/out"
metadata="$out/oracle-metadata"
mkdir -p "$metadata" "$out/raw-receipts"

printf '2.568.1\n' > "$metadata/jenkins-core.txt"
printf 'alpha\t1.0\ttrue\ttrue\n' > "$metadata/jenkins-plugins.tsv"
printf 'fixture/jenkins:2.568.1|%064d|sha256:%064d\n' 1 2 \
  > "$metadata/jenkins-controller-image.txt"
printf 'runner output\n' > "$out/probe-run.log"
printf 'probe-cli-exit=1\n' > "$out/probe-exit.txt"
core_digest=$(sha256sum "$metadata/jenkins-core.txt" | awk '{print $1}')
plugin_digest=$(sha256sum "$metadata/jenkins-plugins.tsv" | awk '{print $1}')
image_digest=$(sha256sum "$metadata/jenkins-controller-image.txt" | awk '{print $1}')
fixture_session_digest=$(printf fixture-session | sha256sum | awk '{print $1}')
fixture_container_id=$(printf '%064d' 5)
{
  printf 'format\tfogell-jenkins-oracle-v2\n'
  printf 'jenkins-core\t2.568.1\n'
  printf 'jenkins-session-sha256\t%s\n' "$fixture_session_digest"
  printf 'controller-container-id\t%s\n' "$fixture_container_id"
  printf 'core-metadata-sha256\t%s\n' "$core_digest"
  printf 'plugin-count\t1\n'
  printf 'plugin-manifest-sha256\t%s\n' "$plugin_digest"
  printf 'controller-image-name\tfixture/jenkins:2.568.1\n'
  printf 'controller-image-id\t%064d\n' 1
  printf 'controller-image-digest\tsha256:%064d\n' 2
  printf 'image-metadata-sha256\t%s\n' "$image_digest"
} > "$out/oracle-before-verification.txt"
cp "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt"
printf 'echo(message: "one")\n' > "$out/one.Jenkinsfile"
printf 'echo(message: "two")\n' > "$out/two.Jenkinsfile"
printf 'receipt one\n' > "$out/raw-receipts/one.receipt.txt"
printf 'receipt two\n' > "$out/raw-receipts/two.receipt.txt"

manifest="$out/probe-run-manifest.tsv"
bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.568.1 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile"

grep -Fx $'started-at-utc\t2026-08-19T10:00:00Z' "$manifest"
grep -Fx $'oracle-verified-before-at-utc\t2026-08-19T10:00:01Z' "$manifest"
grep -Fx $'oracle-verified-after-at-utc\t2026-08-19T10:00:02Z' "$manifest"
grep -Fx $'finished-at-utc\t2026-08-19T10:00:03Z' "$manifest"
grep -Fx $'format\tfogell-evidence-run-v3' "$manifest"
grep -Fx $'oracle-metadata-directory\toracle-metadata' "$manifest"
grep -Fx $'jenkins-core\t2.568.1' "$manifest"
grep -Fx $'jenkins-session-sha256\t'"$fixture_session_digest" "$manifest"
grep -Fx $'controller-container-id\t'"$fixture_container_id" "$manifest"
grep -Fx $'plugin-manifest-sha256\t'"$(sha256sum "$metadata/jenkins-plugins.tsv" | awk '{print $1}')" "$manifest"
grep -Fx $'controller-image-id\t'"$(printf '%064d' 1)" "$manifest"
grep -Fx $'controller-image-digest\tsha256:'"$(printf '%064d' 2)" "$manifest"
[[ $(awk -F '\t' '$1 == "case" { print $3 }' "$manifest") == $'one.Jenkinsfile\ntwo.Jenkinsfile' ]]

# A reversed timestamp and a missing receipt both refuse. Failure is atomic:
# the last complete manifest remains byte-identical and no temp is published.
manifest_hash=$(sha256sum "$manifest" | awk '{ print $1 }')

require_input_refusal() {
  local label=$1
  local log_arg=$2
  local exit_arg=$3
  local metadata_arg=$4
  local before_arg=$5
  local after_arg=$6
  local case_one=$7
  local case_two=$8
  if bash "$evidence/write-run-manifest.sh" \
    "$manifest" fixture \
    2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
    2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
    1 probe-cli-exit "$log_arg" "$exit_arg" 2.568.1 "$metadata_arg" \
    "$before_arg" "$after_arg" "$case_one" "$case_two" \
      > "$proof_tmp/$label.log" 2>&1; then
    printf 'ERROR: %s manifest input unexpectedly passed\n' "$label" >&2
    exit 1
  fi
  if [[ $(sha256sum "$manifest" | awk '{ print $1 }') != "$manifest_hash" ]]; then
    printf 'ERROR: %s refusal changed the prior complete manifest\n' "$label" >&2
    exit 1
  fi
  if find "$out" -maxdepth 1 -name 'probe-run-manifest.tsv.tmp.*' | grep -q .; then
    printf 'ERROR: %s refusal left a partial manifest temp\n' "$label" >&2
    exit 1
  fi
}

valid_log="$out/probe-run.log"
valid_exit="$out/probe-exit.txt"
valid_before="$out/oracle-before-verification.txt"
valid_after="$out/oracle-after-verification.txt"
valid_case_one="$out/one.Jenkinsfile"
valid_case_two="$out/two.Jenkinsfile"

# Mutate the staged plugin bytes only after the writer has copied and hashed
# them. The final stability comparison must refuse and preserve the old seal.
mutation_bin="$proof_tmp/mutation-bin"
mkdir "$mutation_bin"
cat > "$mutation_bin/sha256sum" <<'EOF'
#!/usr/bin/env bash
/usr/bin/sha256sum "$@"
if [[ -n ${FOGELL_MUTATE_METADATA:-} &&
      ${1:-} == */.oracle-metadata-copy.*/jenkins-plugins.tsv &&
      ! -e ${FOGELL_MUTATION_MARKER:-/nonexistent} ]]; then
  printf 'mutated\n' > "$FOGELL_MUTATION_MARKER"
  printf 'alpha\t9.9\ttrue\ttrue\n' > "$FOGELL_MUTATE_METADATA"
fi
EOF
chmod +x "$mutation_bin/sha256sum"
cp "$metadata/jenkins-plugins.tsv" "$proof_tmp/plugins.stable"
PATH="$mutation_bin:$PATH" \
FOGELL_MUTATE_METADATA="$metadata/jenkins-plugins.tsv" \
FOGELL_MUTATION_MARKER="$proof_tmp/mutation-fired" \
  require_input_refusal writer-metadata-swap "$valid_log" "$valid_exit" \
    "$metadata" "$valid_before" "$valid_after" \
    "$valid_case_one" "$valid_case_two"
[[ -s "$proof_tmp/mutation-fired" ]]
cp "$proof_tmp/plugins.stable" "$metadata/jenkins-plugins.tsv"

empty_log="$proof_tmp/empty-run.log"
: > "$empty_log"
symlink_log="$proof_tmp/symlink-run.log"
ln -s "$valid_log" "$symlink_log"
directory_log="$proof_tmp/directory-run.log"
mkdir "$directory_log"
for label_path in \
  "missing-log:$proof_tmp/missing-run.log" \
  "empty-log:$empty_log" \
  "symlink-log:$symlink_log" \
  "nonregular-log:$directory_log"
do
  label=${label_path%%:*}
  planted=${label_path#*:}
  require_input_refusal "$label" "$planted" "$valid_exit" "$metadata" \
    "$valid_before" "$valid_after" "$valid_case_one" "$valid_case_two"
done

empty_exit="$proof_tmp/empty-exit.txt"
: > "$empty_exit"
symlink_exit="$proof_tmp/symlink-exit.txt"
ln -s "$valid_exit" "$symlink_exit"
directory_exit="$proof_tmp/directory-exit.txt"
mkdir "$directory_exit"
for label_path in \
  "missing-exit:$proof_tmp/missing-exit.txt" \
  "empty-exit:$empty_exit" \
  "symlink-exit:$symlink_exit" \
  "nonregular-exit:$directory_exit"
do
  label=${label_path%%:*}
  planted=${label_path#*:}
  require_input_refusal "$label" "$valid_log" "$planted" "$metadata" \
    "$valid_before" "$valid_after" "$valid_case_one" "$valid_case_two"
done

# The same immutable-input contract applies to every hashed manifest input.
empty_oracle="$proof_tmp/empty-oracle.txt"
: > "$empty_oracle"
symlink_oracle="$proof_tmp/symlink-oracle.txt"
ln -s "$valid_before" "$symlink_oracle"
directory_oracle="$proof_tmp/directory-oracle.txt"
mkdir "$directory_oracle"
require_input_refusal missing-oracle "$valid_log" "$valid_exit" "$metadata" \
  "$proof_tmp/missing-oracle.txt" "$valid_after" "$valid_case_one" "$valid_case_two"
require_input_refusal empty-oracle "$valid_log" "$valid_exit" "$metadata" \
  "$empty_oracle" "$valid_after" "$valid_case_one" "$valid_case_two"
require_input_refusal symlink-oracle "$valid_log" "$valid_exit" "$metadata" \
  "$symlink_oracle" "$valid_after" "$valid_case_one" "$valid_case_two"
require_input_refusal nonregular-oracle "$valid_log" "$valid_exit" "$metadata" \
  "$directory_oracle" "$valid_after" "$valid_case_one" "$valid_case_two"

empty_case="$proof_tmp/empty-case.Jenkinsfile"
: > "$empty_case"
symlink_case="$proof_tmp/symlink-case.Jenkinsfile"
ln -s "$valid_case_one" "$symlink_case"
directory_case="$proof_tmp/directory-case.Jenkinsfile"
mkdir "$directory_case"
require_input_refusal missing-case "$valid_log" "$valid_exit" "$metadata" \
  "$valid_before" "$valid_after" "$proof_tmp/missing-case.Jenkinsfile" "$valid_case_two"
require_input_refusal empty-case "$valid_log" "$valid_exit" "$metadata" \
  "$valid_before" "$valid_after" "$empty_case" "$valid_case_two"
require_input_refusal symlink-case "$valid_log" "$valid_exit" "$metadata" \
  "$valid_before" "$valid_after" "$symlink_case" "$valid_case_two"
require_input_refusal nonregular-case "$valid_log" "$valid_exit" "$metadata" \
  "$valid_before" "$valid_after" "$directory_case" "$valid_case_two"

empty_metadata="$proof_tmp/empty-metadata"
cp -a "$metadata" "$empty_metadata"
: > "$empty_metadata/jenkins-plugins.tsv"
symlink_metadata_file="$proof_tmp/symlink-metadata-file"
cp -a "$metadata" "$symlink_metadata_file"
rm "$symlink_metadata_file/jenkins-plugins.tsv"
ln -s "$metadata/jenkins-plugins.tsv" \
  "$symlink_metadata_file/jenkins-plugins.tsv"
symlink_metadata_dir="$proof_tmp/symlink-metadata-dir"
ln -s "$metadata" "$symlink_metadata_dir"
missing_metadata="$proof_tmp/missing-metadata"
cp -a "$metadata" "$missing_metadata"
rm "$missing_metadata/jenkins-plugins.tsv"
directory_metadata="$proof_tmp/directory-metadata"
cp -a "$metadata" "$directory_metadata"
rm "$directory_metadata/jenkins-plugins.tsv"
mkdir "$directory_metadata/jenkins-plugins.tsv"
unexpected_metadata="$proof_tmp/unexpected-metadata"
cp -a "$metadata" "$unexpected_metadata"
printf 'extra\n' > "$unexpected_metadata/unexpected.txt"
require_input_refusal missing-provenance "$valid_log" "$valid_exit" \
  "$missing_metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
require_input_refusal empty-provenance "$valid_log" "$valid_exit" \
  "$empty_metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
require_input_refusal symlink-provenance "$valid_log" "$valid_exit" \
  "$symlink_metadata_file" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
require_input_refusal symlink-metadata-dir "$valid_log" "$valid_exit" \
  "$symlink_metadata_dir" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
require_input_refusal nonregular-provenance "$valid_log" "$valid_exit" \
  "$directory_metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
require_input_refusal unexpected-provenance "$valid_log" "$valid_exit" \
  "$unexpected_metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"

# Same-count plugin and same-name image drift are both rejected against the
# embedded receipt digests even though their headline fields still look alike.
printf 'alpha\t9.9\ttrue\ttrue\n' > "$metadata/jenkins-plugins.tsv"
require_input_refusal same-count-plugin-drift "$valid_log" "$valid_exit" \
  "$metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
cp "$proof_tmp/plugins.stable" "$metadata/jenkins-plugins.tsv"
cp "$metadata/jenkins-controller-image.txt" "$proof_tmp/image.stable"
printf 'fixture/jenkins:2.568.1|%064d|sha256:%064d\n' 3 4 \
  > "$metadata/jenkins-controller-image.txt"
require_input_refusal same-name-image-drift "$valid_log" "$valid_exit" \
  "$metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
cp "$proof_tmp/image.stable" "$metadata/jenkins-controller-image.txt"

# Repeat the structural arms at the canonical in-bundle path so their refusal
# cannot be attributed merely to an external metadata-directory spelling.
mv "$metadata" "$proof_tmp/canonical-metadata-stable"
ln -s "$proof_tmp/canonical-metadata-stable" "$metadata"
require_input_refusal canonical-symlink-snapshot "$valid_log" "$valid_exit" \
  "$metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
rm "$metadata"
cp -a "$proof_tmp/canonical-metadata-stable" "$metadata"
rm "$metadata/jenkins-plugins.tsv"
require_input_refusal canonical-partial-snapshot "$valid_log" "$valid_exit" \
  "$metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
rm -rf "$metadata"
cp -a "$proof_tmp/canonical-metadata-stable" "$metadata"
rm "$metadata/jenkins-plugins.tsv"
ln -s "$proof_tmp/canonical-metadata-stable/jenkins-plugins.tsv" \
  "$metadata/jenkins-plugins.tsv"
require_input_refusal canonical-symlink-file "$valid_log" "$valid_exit" \
  "$metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
rm -rf "$metadata"
cp -a "$proof_tmp/canonical-metadata-stable" "$metadata"
printf 'extra\n' > "$metadata/unexpected.txt"
require_input_refusal canonical-extra-snapshot "$valid_log" "$valid_exit" \
  "$metadata" "$valid_before" "$valid_after" \
  "$valid_case_one" "$valid_case_two"
rm -rf "$metadata"
mv "$proof_tmp/canonical-metadata-stable" "$metadata"

if bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.999 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile" > "$proof_tmp/core-mismatch.log" 2>&1; then
  echo 'ERROR: requested/metadata core mismatch unexpectedly produced a manifest' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]

printf 'Jenkins oracle verified: drifted identity\n' > "$out/oracle-after-verification.txt"
if bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.568.1 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile" > "$proof_tmp/drift.log" 2>&1; then
  echo 'ERROR: differing pre/post oracle identities unexpectedly produced a manifest' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]
cp "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt"

if bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:02Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.568.1 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" > "$proof_tmp/reversed.log" 2>&1; then
  echo 'ERROR: reversed provenance timestamps unexpectedly passed' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]

mv "$out/raw-receipts/two.receipt.txt" "$proof_tmp/two.receipt.txt"
if bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.568.1 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile" > "$proof_tmp/missing.log" 2>&1; then
  echo 'ERROR: missing receipt unexpectedly produced a manifest' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]
if find "$out" -maxdepth 1 -name 'probe-run-manifest.tsv.tmp.*' | grep -q .; then
  echo 'ERROR: refused manifest left a partial temp file' >&2
  exit 1
fi

# Exact-set validation also rejects empty and symlinked expected receipts and
# any unexpected non-regular entry. Each refusal preserves the prior manifest.
mv "$proof_tmp/two.receipt.txt" "$out/raw-receipts/two.receipt.txt"
: > "$out/raw-receipts/two.receipt.txt"
if bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.568.1 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile" > "$proof_tmp/empty.log" 2>&1; then
  echo 'ERROR: empty receipt unexpectedly produced a manifest' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]
printf 'receipt two\n' > "$out/raw-receipts/two.receipt.txt"

mv "$out/raw-receipts/one.receipt.txt" "$proof_tmp/one.receipt.txt"
ln -s "$proof_tmp/one.receipt.txt" "$out/raw-receipts/one.receipt.txt"
if bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.568.1 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile" > "$proof_tmp/symlink.log" 2>&1; then
  echo 'ERROR: symlinked receipt unexpectedly produced a manifest' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]
rm "$out/raw-receipts/one.receipt.txt"
mv "$proof_tmp/one.receipt.txt" "$out/raw-receipts/one.receipt.txt"

mkdir "$out/raw-receipts/unexpected.receipt.txt"
if bash "$evidence/write-run-manifest.sh" \
  "$manifest" fixture \
  2026-08-19T10:00:00Z 2026-08-19T10:00:01Z \
  2026-08-19T10:00:02Z 2026-08-19T10:00:03Z \
  1 probe-cli-exit "$out/probe-run.log" "$out/probe-exit.txt" 2.568.1 "$metadata" \
  "$out/oracle-before-verification.txt" "$out/oracle-after-verification.txt" \
  "$out/one.Jenkinsfile" "$out/two.Jenkinsfile" > "$proof_tmp/nonregular.log" 2>&1; then
  echo 'ERROR: unexpected non-regular receipt entry produced a manifest' >&2
  exit 1
fi
[[ $(sha256sum "$manifest" | awk '{ print $1 }') == "$manifest_hash" ]]
rmdir "$out/raw-receipts/unexpected.receipt.txt"

printf 'RUN MANIFEST PROOF: timestamps/order/provenance bound; missing/empty/symlink/extra refused atomically\n'
