#!/usr/bin/env bash
# FG-251 — prove the focused suite kills regressions at each credential-file
# boundary rather than merely observing the secure implementation on HeMan.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
scratch=$(mktemp -d /tmp/fogell-fg251-mutant.XXXXXX)
trap 'rm -rf "$scratch"' EXIT

git -C "$repo" ls-files -z -- \
  src tests/Fogell.Controller.Api.Tests \
  Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  | tar -C "$repo" --null -T - -cf - \
  | tar -xf - -C "$scratch"

project="$scratch/tests/Fogell.Controller.Api.Tests/Fogell.Controller.Api.Tests.fsproj"
config="$scratch/src/Fogell.Controller.Host/Config.fs"
filter='FG-251 descriptor-bound API token file'

bash -ic "dotnet restore '$project' --locked-mode --ignore-failed-sources -m:1"
bash -ic "dotnet build '$project' -c Release --no-restore -m:1"
dotnet run --project "$project" -c Release --no-build -- \
  --filter-test-list "$filter" --sequenced >/dev/null

restore_config() {
  cp "$repo/src/Fogell.Controller.Host/Config.fs" "$config"
}

kill_mutant() {
  local label=$1
  shift
  set +e
  bash -ic "dotnet build '$project' -c Release --no-restore -m:1" >/dev/null
  local build_rc=$?
  local output
  output=$(timeout 20s dotnet run --project "$project" -c Release --no-build -- \
    --filter-test-list "$filter" --sequenced 2>&1)
  local run_rc=$?
  set -e

  (( build_rc == 0 )) \
    || { echo "FG-251 proof: $label mutant did not compile" >&2; exit 1; }
  (( run_rc != 0 && run_rc != 124 )) \
    || { printf '%s\n' "$output" >&2; echo "FG-251 proof: $label mutant survived or hung" >&2; exit 1; }

  local expected
  for expected in "$@"; do
    printf '%s\n' "$output" | rg -F "$expected" >/dev/null \
      || { printf '%s\n' "$output" >&2; echo "FG-251 proof: $label failed for an unrelated reason" >&2; exit 1; }
  done
}

# A host-local constant is correct on x86-64 but silently drops O_NOFOLLOW on
# arm64. The test supplies both kernel ABI tables, so this mutant must die here.
flags='        OpenReadOnly ||| OpenNonBlocking ||| table.NoFollow ||| OpenCloseOnExec'
[[ $(rg -F -c "$flags" "$config") == 1 ]] \
  || { echo 'FG-251 proof: flag mutation target is not unique' >&2; exit 1; }
sed -i "s/^$flags$/        OpenReadOnly ||| OpenNonBlocking ||| LinuxOpenFlags.asmGeneric.NoFollow ||| OpenCloseOnExec/" "$config"
kill_mutant architecture 'arm lineage contributes its distinct no-follow bit'

restore_config
mask='        if status.Mask &&& required <> required then'
[[ $(rg -F -c "$mask" "$config") == 1 ]] \
  || { echo 'FG-251 proof: statx-mask mutation target is not unique' >&2; exit 1; }
sed -i "s/^$mask$/        if false then/" "$config"
kill_mutant statx-mask 'a kernel record that omits stx_uid is refused'

restore_config
owner='                  Owner = status.UserId'
[[ $(rg -F -c "$owner" "$config") == 1 ]] \
  || { echo 'FG-251 proof: statx-owner mutation target is not unique' >&2; exit 1; }
sed -i "s/^$owner$/                  Owner = status.GroupId/" "$config"
kill_mutant statx-owner 'the ABI record maps stx_uid'

restore_config
loader='                        let token = rawToken.TrimEnd'
[[ $(rg -F -c "$loader" "$config") == 1 ]] \
  || { echo 'FG-251 proof: loader mutation target is not unique' >&2; exit 1; }
sed -i '/let token = rawToken.TrimEnd/c\                        let token = File.ReadAllText(tokenPath).Trim()' "$config"
rg -F 'let token = File.ReadAllText(tokenPath).Trim()' "$config" >/dev/null \
  || { echo 'FG-251 proof: loader mutant did not change bytes' >&2; exit 1; }
kill_mutant loader-reopen 'the loader consumes its reader result without reopening the path'

restore_config
stream='                                    use stream = new FileStream(handle, FileAccess.Read, 4096, false)'
[[ $(rg -F -c "$stream" "$config") == 1 ]] \
  || { echo 'FG-251 proof: descriptor-read mutation target is not unique' >&2; exit 1; }
sed -i "s@^$stream\$@                                    use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)@" "$config"
kill_mutant pathname-reopen 'validation and reading stay on the opened inode'

restore_config
safe_flags='        OpenReadOnly ||| OpenNonBlocking ||| table.NoFollow ||| OpenCloseOnExec'
sed -i "s/^$safe_flags$/        OpenReadOnly ||| table.NoFollow/" "$config"
kill_mutant blocking-inheritable-fd \
  'O_NONBLOCK prevents an attacker-controlled FIFO from holding startup' \
  'O_CLOEXEC marks the token descriptor close-on-exec'

restore_config
sed -i 's/let private PermissionMask = 0x0FFFus/let private PermissionMask = 0x01FFus/' "$config"
sed -i 's/let bytes = Array.zeroCreate<byte> (maxApiTokenFileBytes + 1)/let bytes = Array.zeroCreate<byte> maxApiTokenFileBytes/' "$config"
sed -i 's/let strictUtf8 = UTF8Encoding(false, true)/let strictUtf8 = Encoding.UTF8/' "$config"
kill_mutant validation-weakening \
  'permissive modes are refused' \
  'malformed and non-UTF-8 token encodings are refused' \
  'growth after metadata validation is still bounded'

echo 'FG-251 PROOF PASS: baseline passed; architecture, statx mask/uid, loader/path reopen, blocking/inheritable descriptor, mode, decode, and growth mutants compiled and were killed'
