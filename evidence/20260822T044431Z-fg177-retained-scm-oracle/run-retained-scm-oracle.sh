#!/usr/bin/env bash
# Capture retained history for `git` and SCM-defined `checkout scm`.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$here/../.." && pwd)
out=${1:?usage: run-retained-scm-oracle.sh OUTPUT_DIRECTORY}
push_url=${FG177_FIXTURE_PUSH_URL:?FG177_FIXTURE_PUSH_URL is required}
clone_url=${FG177_FIXTURE_CLONE_URL:?FG177_FIXTURE_CLONE_URL is required}
canonical_driver="$here/jenkins-driver.py"
canonical_surface="$here/capture-controller-surface.py"
validator="$here/validate-retained-scm-run.py"
hermetic=${FG177_HERMETIC:-0}

case "$hermetic" in
  0)
    [[ ! ${FG177_ORACLE_DRIVER+x} ]] || { echo 'ERROR: production refuses FG177_ORACLE_DRIVER' >&2; exit 2; }
    [[ ! ${FG177_RUN_ID+x} ]] || { echo 'ERROR: production refuses caller-supplied FG177_RUN_ID' >&2; exit 2; }
    [[ ! ${FG177_SURFACE_CAPTURE+x} || ${FG177_SURFACE_CAPTURE} == "$canonical_surface" ]] || {
      echo 'ERROR: production fixes the canonical surface capture tool' >&2; exit 2;
    }
    driver=$canonical_driver
    export FG177_SURFACE_CAPTURE=$canonical_surface
    random_suffix=$(openssl rand -hex 16)
    [[ $random_suffix =~ ^[0-9a-f]{32}$ ]] || { echo 'ERROR: cryptographic run-id generation failed' >&2; exit 2; }
    run_id="$(date -u +%Y%m%dT%H%M%SZ)-$random_suffix"
    mode=production
    ;;
  1)
    driver=${FG177_ORACLE_DRIVER:-$canonical_driver}
    run_id=${FG177_RUN_ID:-hermetic-$(date -u +%Y%m%dT%H%M%SZ)-$$}
    mode=hermetic
    ;;
  *) echo 'ERROR: FG177_HERMETIC must be exactly 0 or 1' >&2; exit 2 ;;
esac

[[ $run_id =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$ ]] || { echo 'ERROR: unsafe run id' >&2; exit 2; }
[[ ! -e $out && ! -L $out ]] || { echo "ERROR: output already exists: $out" >&2; exit 2; }
for tool in "$driver" "$validator"; do
  [[ -f $tool && -x $tool && ! -L $tool ]] || { echo "ERROR: tooling is not an exact executable file: $tool" >&2; exit 2; }
done

out_parent=$(cd "$(dirname "$out")" && pwd)
stage=$(mktemp -d "$out_parent/.fg177-retained-scm.XXXXXX")
fixture=$(mktemp -d)
cleanup() { rm -rf "$stage" "$fixture"; }
trap cleanup EXIT
printf '%s\n' "$mode" > "$stage/capture-mode.txt"

mkdir -p "$stage/tooling/cases"
cp "$here/run-retained-scm-oracle.sh" "$stage/tooling/"
cp "$canonical_driver" "$canonical_surface" "$validator" "$here/README.md" "$stage/tooling/"
cp "$here/cases/Jenkinsfile" "$here/cases/fg177-retained-git.Jenkinsfile.in" "$stage/tooling/cases/"
(
  cd "$stage/tooling"
  find . -type f ! -name SHA256SUMS -print0 | LC_ALL=C sort -z | xargs -0 sha256sum > SHA256SUMS
)

if [[ $mode == production ]]; then
  pinned_metadata="$repo_root/evidence/20260818-fg-177-measurement/runs/probes/oracle-metadata"
  oracle_verifier="$repo_root/evidence/20260818-fg-177-measurement/verify-run-oracle.sh"
  : "${FG177_JENKINS_URL:?FG177_JENKINS_URL is required in production}"
  : "${FG177_ORACLE_SSH_HOST:?FG177_ORACLE_SSH_HOST is required in production}"
  : "${FG177_JENKINS_CONTAINER:?FG177_JENKINS_CONTAINER is required in production}"
  [[ -f $oracle_verifier && ! -L $oracle_verifier && -d $pinned_metadata && ! -L $pinned_metadata ]] || {
    echo 'ERROR: canonical pinned oracle tooling/metadata is absent' >&2; exit 2;
  }
  oracle_before=$(bash "$oracle_verifier" "$FG177_JENKINS_URL" 2.568.1 \
    "$FG177_ORACLE_SSH_HOST" "$FG177_JENKINS_CONTAINER" "$pinned_metadata" \
    "$stage/oracle-snapshot-before")
  printf '%s\n' "$oracle_before" > "$stage/oracle-before-verification.txt"
  expected_container=$(awk -F '\t' '$1 == "controller-container-id" { if (++seen > 1) exit 2; value=$2 } END { if (seen != 1) exit 2; print value }' \
    "$stage/oracle-before-verification.txt")
  [[ $expected_container =~ ^[0-9a-f]{64}$ ]] || { echo 'ERROR: pre-oracle container identity is malformed' >&2; exit 2; }
  export FG177_EXPECTED_CONTAINER_ID=$expected_container
fi

"$driver" surface "$stage/surface-before"
repo="$fixture/repo"
git init -q "$repo"
git -C "$repo" config user.name 'FG-177 evidence fixture'
git -C "$repo" config user.email 'fg177-evidence@example.invalid'
cp "$here/cases/Jenkinsfile" "$repo/Jenkinsfile"

commit_payload() {
  local label=$1 epoch=$2
  printf '%s\n' "$label" > "$repo/payload.txt"
  git -C "$repo" add Jenkinsfile payload.txt
  GIT_AUTHOR_DATE="@$epoch +0000" GIT_COMMITTER_DATE="@$epoch +0000" \
    git -C "$repo" commit -q -m "FG-177 fixture $label"
  git -C "$repo" rev-parse HEAD
}

A=$(commit_payload A 1704067201)
B=$(commit_payload B 1704067202)
C=$(commit_payload C 1704067203)
D=$(commit_payload D 1704067204)
git -C "$repo" switch -q --detach "$A"
F=$(commit_payload F 1704067205)
G=$(commit_payload G 1704067206)
jenkinsfile_sha=$(sha256sum "$repo/Jenkinsfile" | awk '{print $1}')

{
  printf 'label\tsha\tparent\tpayload\tjenkinsfile-sha256\n'
  printf 'A\t%s\t-\tA\t%s\n' "$A" "$jenkinsfile_sha"
  printf 'B\t%s\t%s\tB\t%s\n' "$B" "$A" "$jenkinsfile_sha"
  printf 'C\t%s\t%s\tC\t%s\n' "$C" "$B" "$jenkinsfile_sha"
  printf 'D\t%s\t%s\tD\t%s\n' "$D" "$C" "$jenkinsfile_sha"
  printf 'F\t%s\t%s\tF\t%s\n' "$F" "$A" "$jenkinsfile_sha"
  printf 'G\t%s\t%s\tG\t%s\n' "$G" "$F" "$jenkinsfile_sha"
} > "$stage/fixture.tsv"
for entry in "A:$A" "B:$B" "C:$C" "D:$D" "F:$F" "G:$G"; do
  git -C "$repo" update-ref "refs/heads/fixture/${entry%%:*}" "${entry#*:}"
done
git -C "$repo" bundle create "$stage/fixture.bundle" \
  refs/heads/fixture/A refs/heads/fixture/B refs/heads/fixture/C \
  refs/heads/fixture/D refs/heads/fixture/F refs/heads/fixture/G

prefix="fg177-retained/$run_id"
pin_prefix="$prefix/pins"
git_main_branch="$prefix/git/main"
git_feature_branch="$prefix/git/feature"
checkout_main_branch="$prefix/checkout-scm/main"
checkout_feature_branch="$prefix/checkout-scm/feature"
target_refs=(
  "refs/heads/$pin_prefix/A" "refs/heads/$pin_prefix/B" "refs/heads/$pin_prefix/C"
  "refs/heads/$pin_prefix/D" "refs/heads/$pin_prefix/F" "refs/heads/$pin_prefix/G"
  "refs/heads/$git_main_branch" "refs/heads/$git_feature_branch"
  "refs/heads/$checkout_main_branch" "refs/heads/$checkout_feature_branch"
)
printf '%s\n' "${target_refs[@]}" > "$stage/target-refs.tsv"
existing_refs=$(git ls-remote --refs "$push_url" "${target_refs[@]}")
printf '%s' "$existing_refs" > "$stage/ref-preflight.tsv"
[[ -z $existing_refs ]] || { echo 'ERROR: one or more target refs already exist' >&2; exit 2; }
git -C "$repo" push -q --atomic "$push_url" \
  "$A:refs/heads/$pin_prefix/A" "$B:refs/heads/$pin_prefix/B" \
  "$C:refs/heads/$pin_prefix/C" "$D:refs/heads/$pin_prefix/D" \
  "$F:refs/heads/$pin_prefix/F" "$G:refs/heads/$pin_prefix/G"
git ls-remote --refs "$push_url" \
  "refs/heads/$pin_prefix/A" "refs/heads/$pin_prefix/B" "refs/heads/$pin_prefix/C" \
  "refs/heads/$pin_prefix/D" "refs/heads/$pin_prefix/F" "refs/heads/$pin_prefix/G" \
  > "$stage/pin-refs.tsv"

cat > "$stage/schedule.tsv" <<EOF
build	label	sha	result	previous	previous-successful	payload
1	A	$A	SUCCESS	-	-	A
2	B	$B	FAILURE	$A	$A	B
3	C	$C	SUCCESS	$B	$A	C
4	F	$F	SUCCESS	-	-	F
5	G	$G	SUCCESS	$F	$F	G
6	D	$D	SUCCESS	$C	$C	D
EOF

mkdir -p "$stage/inputs" "$stage/runs"
cp "$here/cases/Jenkinsfile" "$stage/inputs/checkout-scm.Jenkinsfile"
python3 - "$here/cases/fg177-retained-git.Jenkinsfile.in" "$stage/inputs/git.Jenkinsfile" \
  "$git_main_branch" "$git_feature_branch" "$clone_url" <<'PY'
import pathlib, sys
source = pathlib.Path(sys.argv[1]).read_text()
for token, value in zip(("@@MAIN_BRANCH@@", "@@FEATURE_BRANCH@@", "@@CLONE_URL@@"), sys.argv[3:]):
    if "'" in value or "\n" in value or "\r" in value:
        raise SystemExit("ERROR: unsafe Jenkinsfile replacement")
    source = source.replace(token, value)
if "@@" in source:
    raise SystemExit("ERROR: unresolved Jenkinsfile token")
pathlib.Path(sys.argv[2]).write_text(source)
PY

run_producer() {
  local kind=$1 main_branch feature_branch job
  if [[ $kind == git ]]; then
    main_branch=$git_main_branch; feature_branch=$git_feature_branch
  else
    main_branch=$checkout_main_branch; feature_branch=$checkout_feature_branch
  fi
  job="fg177-$run_id-$kind"
  "$driver" assert-absent "$job"
  tail -n +2 "$stage/schedule.tsv" | while IFS=$'\t' read -r number label sha result previous previous_successful payload; do
    branch=$main_branch
    [[ $number == 4 || $number == 5 ]] && branch=$feature_branch
    build_dir="$stage/runs/$kind/build-$number"
    mkdir -p "$build_dir"
    printf 'build\t%s\nbranch\t%s\nlabel\t%s\nsha\t%s\nresult\t%s\nprevious\t%s\nprevious-successful\t%s\npayload\t%s\nclone-url\t%s\n' \
      "$number" "$branch" "$label" "$sha" "$result" "$previous" "$previous_successful" "$payload" "$clone_url" > "$build_dir/expected.tsv"
    git -C "$repo" push -q "$push_url" "$sha:refs/heads/$branch"
    git ls-remote --refs "$push_url" "refs/heads/$branch" > "$build_dir/ref-before.tsv"
    case_file="$stage/inputs/git.Jenkinsfile"
    [[ $kind == checkout-scm ]] && case_file="$stage/inputs/checkout-scm.Jenkinsfile"
    "$driver" configure "$job" "$kind" "$branch" "$case_file" "$clone_url" "$build_dir"
    "$driver" build "$job" "$number" "$build_dir"
    git ls-remote --refs "$push_url" "refs/heads/$branch" > "$build_dir/ref-after.tsv"
  done
}

run_producer git
run_producer checkout-scm
"$driver" surface "$stage/surface-after"
if [[ $mode == production ]]; then
  oracle_after=$(bash "$oracle_verifier" "$FG177_JENKINS_URL" 2.568.1 \
    "$FG177_ORACLE_SSH_HOST" "$FG177_JENKINS_CONTAINER" "$pinned_metadata" \
    "$stage/oracle-snapshot-after")
  printf '%s\n' "$oracle_after" > "$stage/oracle-after-verification.txt"
  cmp "$stage/oracle-before-verification.txt" "$stage/oracle-after-verification.txt"
  diff -qr "$stage/oracle-snapshot-before" "$stage/oracle-snapshot-after"
fi
printf 'complete\n' > "$stage/STATUS"
(
  cd "$stage"
  find . -type f ! -name MANIFEST.sha256 -print0 | LC_ALL=C sort -z | xargs -0 sha256sum > MANIFEST.sha256
)
if [[ $mode == hermetic ]]; then
  python3 "$validator" --hermetic "$stage"
else
  python3 "$validator" "$stage"
fi
mv "$stage" "$out"
trap - EXIT
rm -rf "$fixture"
printf 'FG177 RETAINED SCM ORACLE: %s capture complete and validated at %s\n' "$mode" "$out"
