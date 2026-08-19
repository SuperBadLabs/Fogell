#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

evidence='evidence/20260818-fg-177-measurement'
fixture_tmp=$(mktemp -d)
trap 'rm -rf "$fixture_tmp"' EXIT
remote="$fixture_tmp/fixture.git"
seed="$fixture_tmp/seed"
rendered="$fixture_tmp/rendered"

git init -q --bare "$remote"
git init -q "$seed"
printf 'fixture\n' > "$seed/README"
git -C "$seed" add README
git -C "$seed" \
  -c user.email=harness@fogell \
  -c user.name=fogell-harness \
  commit -qm 'fixture: initial main'
git -C "$seed" branch -M main
git -C "$seed" remote add origin "$remote"
git -C "$seed" push -q origin main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main

scm_url="file://$remote"
FOGELL_SCM_URL="$scm_url" \
FOGELL_RENDERED_CASES_DIR="$rendered" \
  python3 "$evidence/render-probe-cases.py"

git ls-remote --exit-code "$scm_url" refs/heads/main >/dev/null
(
  cd "$rendered"
  sha256sum -c SHA256SUMS
)

for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile
do
  if [[ $(grep -Foc "$scm_url" "$rendered/$name") -ne 1 ]]; then
    echo "ERROR: rendered $name does not contain exactly one configured URL" >&2
    exit 1
  fi
  if grep -Fq '@@FOGELL_SCM_URL@@' "$rendered/$name"; then
    echo "ERROR: rendered $name retains its SCM token" >&2
    exit 1
  fi
  if grep -Fq 'git://100.105.179.51/repo.git' "$rendered/$name"; then
    echo "ERROR: rendered $name retains the default fixture URL" >&2
    exit 1
  fi
done

for name in \
  fg177-probe-requiredness.Jenkinsfile \
  fg177-probe-checkout-scm.Jenkinsfile
do
  cmp "$evidence/cases/$name" "$rendered/$name"
done

# Exercise the actual runner from a fresh fixture. A fake CLI proves that all
# paths handed to it are exact rendered files and that its status is preserved.
mkdir -p "$fixture_tmp/bin"
stub="$fixture_tmp/bin/dotnet"
cat > "$stub" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$FOGELL_STUB_ARGS"
printf 'intentional probe CLI failure\n'
exit 29
EOF
chmod +x "$stub"
runner_out="$fixture_tmp/runner-output"
args="$fixture_tmp/dotnet-args"
set +e
PATH="$fixture_tmp/bin:$PATH" \
FOGELL_EVIDENCE_OUT="$runner_out" \
FOGELL_SCM_URL="$scm_url" \
FOGELL_STUB_ARGS="$args" \
  bash "$evidence/run-probes.sh"
rc=$?
set -e
if [[ $rc -ne 29 ]]; then
  echo "ERROR: expected probe runner rc 29, got $rc" >&2
  exit 1
fi
grep -Fx 'intentional probe CLI failure' "$runner_out/probe-run.log"
grep -Fx 'probe-cli-exit=29' "$runner_out/probe-exit.txt"
grep -Fx "$runner_out/raw-receipts" "$args"
for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-requiredness.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile \
  fg177-probe-checkout-scm.Jenkinsfile
do
  path="$runner_out/rendered-cases/$name"
  grep -Fx "$path" "$args"
done
for name in \
  fg177-probe-unknown-policy.Jenkinsfile \
  fg177-probe-return-semantics.Jenkinsfile
do
  [[ $(grep -Foc "$scm_url" "$runner_out/rendered-cases/$name") -eq 1 ]]
done
cmp "$evidence/cases/fg177-probe-checkout-scm.Jenkinsfile" \
  "$runner_out/rendered-cases/fg177-probe-checkout-scm.Jenkinsfile"

printf 'rendered probe cases verified with fresh local fixture %s\n' "$scm_url"
