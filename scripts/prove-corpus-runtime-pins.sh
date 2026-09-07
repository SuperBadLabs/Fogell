#!/usr/bin/env bash
# Hostile proof for the fail-closed corpus runtime-pin boundary. It uses this
# host's ordinary `make` as inert bytes and a fake ssh; no Jenkins, corpus,
# container runtime or network is used.
# shellcheck disable=SC1003,SC2016,SC2317  # deliberate literal source/mutant patterns and exported hostile function
set -Eeuo pipefail
cd "$(dirname "$0")/.."

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/bin"
build_path=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
# The proof must not inherit its baseline from a package that a developer or CI
# image may legitimately install. Generate one shell-safe name for this process
# and establish its absence before using it as the negative fixture.
absent_command="fogell_fg259_absent_${BASHPID}"
[[ "$absent_command" =~ ^[A-Za-z0-9._+-]+$ ]] || { echo "RUNTIME-PIN PROOF FAILED: generated command is unsafe" >&2; exit 1; }
if PATH="$build_path" command -v "$absent_command" >/dev/null 2>&1; then
  echo "RUNTIME-PIN PROOF FAILED: generated command unexpectedly resolves" >&2
  exit 1
fi
local_path=$(PATH="$build_path" command -v make)
local_sha=$(sha256sum -- "$local_path" | cut -d' ' -f1)
image_id=$(printf 'a%.0s' {1..64})
image_digest="sha256:$(printf 'b%.0s' {1..64})"
container_port=8080/tcp
host_binding=0.0.0.0:18083
expected_node=Jenkins
plugin_json='{"plugins":[{"shortName":"workflow-job","version":"1400.v7fd111b_ec82f","active":true,"enabled":true}]}'
plugin_count=1
plugin_sha=$(printf '%s' "$plugin_json" | jq -cS '.plugins | sort_by(.shortName)' | sha256sum | cut -d' ' -f1)

apply_fixture() {
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    make-pin make present "$local_path" /usr/local/bin/make "$local_sha" "$image_id" "$image_digest" "$container_port" "$host_binding" "$expected_node" "$plugin_count" "$plugin_sha" \
    > "$scratch/pins.tsv"
  export FAKE_REMOTE_PATH=/usr/local/bin/make
  export FAKE_REMOTE_TOOL_SHA=$local_sha
  export FAKE_IMAGE_ID=$image_id
  export FAKE_IMAGE_DIGEST=$image_digest
  export FAKE_PORT_BINDING=$host_binding
  export FAKE_PLUGIN_JSON=$plugin_json
  export FOGELL_JENKINS_URL=http://fake:18083
  unset FAKE_REMOTE_RESOLUTION FAKE_SSH_FAIL
}

apply_absent_fixture() {
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    composer-absent-v1 "$absent_command" absent - - - "$image_id" "$image_digest" "$container_port" "$host_binding" "$expected_node" "$plugin_count" "$plugin_sha" \
    > "$scratch/pins.tsv"
  export FAKE_REMOTE_PATH=-
  export FAKE_REMOTE_TOOL_SHA=-
  export FAKE_REMOTE_RESOLUTION=absent
  export FAKE_IMAGE_ID=$image_id
  export FAKE_IMAGE_DIGEST=$image_digest
  export FAKE_PORT_BINDING=$host_binding
  export FAKE_PLUGIN_JSON=$plugin_json
  export FOGELL_JENKINS_URL=http://fake:18083
  unset FAKE_SSH_FAIL
}

cat > "$scratch/bin/ssh" <<'EOF'
#!/usr/bin/env bash
set -eu
remote_command=${!#}
[ -z "${FAKE_SSH_FAIL:-}" ] || exit 1
case "$remote_command" in
  *"pluginManager/api/json"*) printf '%s\n' "$FAKE_PLUGIN_JSON" ;;
  *"podman exec "*"command -v"*) printf '%s\n' "${FAKE_REMOTE_RESOLUTION:-present:$FAKE_REMOTE_PATH}" ;;
  *"podman exec "*" sha256sum -- "*) printf '%s  %s\n' "$FAKE_REMOTE_TOOL_SHA" "$FAKE_REMOTE_PATH" ;;
  *"podman inspect "*"Image"*) printf '%s\n' "$FAKE_IMAGE_ID" ;;
  *"podman image inspect "*"Digest"*) printf '%s\n' "$FAKE_IMAGE_DIGEST" ;;
  *"podman port "*) printf '%s\n' "$FAKE_PORT_BINDING" ;;
  *) printf 'unexpected ssh command: %s\n' "$remote_command" >&2; exit 3 ;;
esac
EOF
chmod +x "$scratch/bin/ssh"

check() {
  pin=${1:-make-pin}
  PATH="$scratch/bin:$PATH" \
  FOGELL_JENKINS_HOST=fake FOGELL_JENKINS_CONTAINER=jenkins-lab \
    ./scripts/check-corpus-runtime-pins.sh "$scratch/pins.tsv" "$pin"
}

must_refuse() {
  label=$1 expected=$2 pin=${3:-make-pin}
  set +e
  out=$(check "$pin" 2>&1); rc=$?
  set -e
  [ "$rc" -ne 0 ] || { echo "RUNTIME-PIN PROOF FAILED: $label was accepted" >&2; exit 1; }
  case "$out" in *"$expected"*) ;; *) printf 'RUNTIME-PIN PROOF FAILED: %s emitted:\n%s\n' "$label" "$out" >&2; exit 1 ;; esac
  printf '  refused %s\n' "$label"
}

apply_fixture
check >/dev/null
echo "=== corpus runtime pin: exact tuple accepted ==="

apply_absent_fixture
check composer-absent-v1 >/dev/null
echo "=== corpus runtime pin: exact command absence accepted ==="

# The generated identifier was grammar-checked above before it enters this
# deliberately dynamic positive-control definition.
eval "$absent_command() { :; }"
export -f "${absent_command?}"
must_refuse "local shadowed synthetic command" "local command unexpectedly resolves" composer-absent-v1
unset -f "$absent_command"
apply_absent_fixture

FAKE_REMOTE_RESOLUTION="present:/opt/shadow/$absent_command"
must_refuse "Jenkins shadowed synthetic command" "Jenkins command unexpectedly resolves" composer-absent-v1
apply_absent_fixture

export FAKE_SSH_FAIL=1
must_refuse "unavailable remote absence identity" "could not inspect Jenkins command" composer-absent-v1
apply_fixture

sed -i "s#\t$local_path\t#\t/bin/not-the-resolved-make\t#" "$scratch/pins.tsv"
must_refuse "local command shadow/path drift" "local command resolves"
apply_fixture

bad_sha=$(printf 'c%.0s' {1..64})
sed -i "s/$local_sha/$bad_sha/" "$scratch/pins.tsv"
must_refuse "local tool-byte drift" "local tool SHA-256"
apply_fixture

FAKE_REMOTE_PATH=/opt/shadow/make
must_refuse "Jenkins command shadow/path drift" "Jenkins command resolution"
apply_fixture

FAKE_REMOTE_TOOL_SHA=$bad_sha
must_refuse "Jenkins tool-byte drift" "Jenkins tool SHA-256"
apply_fixture

FAKE_IMAGE_ID=$(printf 'd%.0s' {1..64})
must_refuse "container image replacement" "Jenkins image ID"
apply_fixture

FAKE_IMAGE_DIGEST="sha256:$(printf 'e%.0s' {1..64})"
must_refuse "image digest replacement" "Jenkins image digest"
apply_fixture

FOGELL_JENKINS_URL=http://other-host:18083
must_refuse "independent Jenkins endpoint host" "does not match inspected SSH host"
apply_fixture

FOGELL_JENKINS_URL=http://fake:18084
must_refuse "independent Jenkins endpoint port" "expected published port"
apply_fixture

FOGELL_JENKINS_URL=http://fake:18083/proxy
must_refuse "proxied Jenkins endpoint path" "must be exactly"
apply_fixture

FAKE_PORT_BINDING=0.0.0.0:18084
must_refuse "container port-binding drift" "Jenkins port binding"
apply_fixture

export FAKE_SSH_FAIL=1
must_refuse "unavailable remote identity" "could not inspect Jenkins command"
apply_fixture

sed -i 's/\tpresent\t/\tmissing\t/' "$scratch/pins.tsv"
must_refuse "missing/invalid resolution expectation" "invalid expectation"
apply_absent_fixture

sed -i 's#\tabsent\t-\t-\t-\t#\tabsent\t/tmp/composer\t-\t-\t#' "$scratch/pins.tsv"
must_refuse "absent row with local path" "absent expectation requires '-'" composer-absent-v1
apply_absent_fixture

sed -i 's#\tabsent\t-\t-\t-\t#\tabsent\t-\t/opt/composer\t-\t#' "$scratch/pins.tsv"
must_refuse "absent row with Jenkins path" "absent expectation requires '-'" composer-absent-v1
apply_absent_fixture

sed -i "s/\tabsent\t-\t-\t-\t/\tabsent\t-\t-\t$bad_sha\t/" "$scratch/pins.tsv"
must_refuse "absent row with tool digest" "absent expectation requires '-'" composer-absent-v1
apply_fixture

sed -i "s/$local_sha/bad/" "$scratch/pins.tsv"
must_refuse "malformed tool digest" "invalid tool SHA-256"
apply_fixture

sed -i "s#\t$container_port\t#\tbad-port\t#" "$scratch/pins.tsv"
must_refuse "malformed container port" "invalid Jenkins container port"
apply_fixture

sed -i "s#\t$host_binding#\t127.0.0.1:18083#" "$scratch/pins.tsv"
must_refuse "malformed host binding" "invalid Jenkins host binding"
apply_fixture

sed -i "s#\t$expected_node#\tbad node#" "$scratch/pins.tsv"
must_refuse "malformed Jenkins node" "invalid Jenkins node"
apply_fixture

sed -i "s#\t$plugin_count\t#\tbad\t#" "$scratch/pins.tsv"
must_refuse "malformed Jenkins plugin count" "invalid Jenkins plugin count"
apply_fixture

sed -i "s/$plugin_sha/bad/" "$scratch/pins.tsv"
must_refuse "malformed Jenkins plugin digest" "invalid Jenkins plugin digest"
apply_fixture

FAKE_PLUGIN_JSON='{"plugins":[]}'
must_refuse "Jenkins plugin-count drift" "Jenkins plugin count"
apply_fixture

FAKE_PLUGIN_JSON='{"plugins":[{"shortName":"workflow-job","version":"mutated","active":true,"enabled":true}]}'
must_refuse "Jenkins plugin-inventory drift" "Jenkins plugin digest"
apply_fixture

printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
  make-pin make present "$local_path" /usr/local/bin/make "$local_sha" "$image_id" "$image_digest" "$container_port" "$host_binding" "$expected_node" "$plugin_count" "$plugin_sha" \
  >> "$scratch/pins.tsv"
must_refuse "duplicate pin id" "duplicate pin id"
apply_fixture

set +e
missing_out=$(PATH="$scratch/bin:$PATH" \
  FOGELL_JENKINS_HOST=fake FOGELL_JENKINS_CONTAINER=jenkins-lab FOGELL_JENKINS_URL=http://fake:18083 \
  ./scripts/check-corpus-runtime-pins.sh "$scratch/pins.tsv" absent-pin 2>&1); missing_rc=$?
set -e
if [ "$missing_rc" -eq 0 ] || [[ "$missing_out" != *"is not defined"* ]]; then
  echo "RUNTIME-PIN PROOF FAILED: missing pin was accepted or misreported" >&2; exit 1
fi
echo "  refused missing pin id"

audit_runner() {
  runner=$1
  resolve_head=$(rg -n -F "source_head=\$(git rev-parse --verify 'HEAD^{commit}') \\" "$runner" | sed -n '1s/:.*//p')
  validate_head=$(rg -n '^\[\[ "\$source_head" =~ \^\[0-9a-f\]\{40,64\}\$ \]\] \\$' "$runner" | sed -n '1s/:.*//p')
  archive=$(rg -n -F 'git archive --format=tar "$source_head" | tar -xf - -C "$cli_source" \' "$runner" | sed -n '1s/:.*//p')
  print_head=$(rg -n '^echo "corpus lane: evidence-control commit \$source_head"$' "$runner" | sed -n '1s/:.*//p')
  runner_cmp=$(rg -n '^cmp -s -- "\$runner_path" "\$cli_source/scripts/run-corpus-differential\.sh" \\$' "$runner" | sed -n '1s/:.*//p')
  verify_use=$(rg -n '^"\$verify_corpus" >/dev/null \|\| die ' "$runner" | sed -n '1s/:.*//p')
  allowlist_use=$(rg -n '^\[ -f "\$allowlist" \] \|\| die ' "$runner" | sed -n '1s/:.*//p')
  restore=$(rg -n '^dotnet restore "\$cli_project" --locked-mode --nologo >/dev/null \\$' "$runner" | sed -n '1s/:.*//p')
  build=$(rg -n '^dotnet build "\$cli_project" -c Release --no-restore --no-incremental --nologo -o "\$cli_build" >/dev/null \\$' "$runner" | sed -n '1s/:.*//p')
  handshake=$(rg -n '^capability=\$\(dotnet "\$cli" --runtime-guard-capability 2>/dev/null\) \\$' "$runner" | sed -n '1s/:.*//p')
  lease=$(rg -n '^tail --pid=\$\$ -f /dev/null 9>&- | ssh -o BatchMode=yes "\$FOGELL_JENKINS_HOST" \\$' "$runner" | sed -n '1s/:.*//p')
  local_access=$(rg -n '^"\$fence_script" local access-apply$' "$runner" | sed -n '1s/:.*//p')
  remote_access=$(rg -n '^"\$fence_script" jenkins access-apply$' "$runner" | sed -n '1s/:.*//p')
  oracle=$(rg -n '^  "podman run --pull=never -d --name \$oracle_name --label fogell\.lane-token=\$FOGELL_JENKINS_ACCESS_TOKEN -p \$oracle_host_port:\$oracle_container_port \$oracle_image"\) \\$' "$runner" | sed -n '1s/:.*//p')
  home=$(rg -n '^oracle_home_volume=\$\(ssh ' "$runner" | sed -n '1s/:.*//p')
  egress=$(rg -n '^"\$fence_script" jenkins apply$' "$runner" | sed -n '1s/:.*//p')
  plugin=$(rg -n '^plugin_json=\$\(ssh ' "$runner" | sed -n '1s/:.*//p')
  empty=$(rg -n '^initial_json=\$\(ssh ' "$runner" | sed -n '1s/:.*//p')
  pre=$(rg -n '^verify_runtime_pins \|\| die ' "$runner" | sed -n '1s/:.*//p')
  tunnel=$(rg -n '^tail --pid=\$\$ -f /dev/null 9>&- \| ssh -o BatchMode=yes -o ExitOnForwardFailure=yes ' "$runner" | sed -n '1s/:.*//p')
  busy=$(rg -n 'busy_json=\$\(curl -sS -m 10 "\$tunnel_url/' "$runner" | sed -n '1s/:.*//p')
  run=$(rg -n 'dotnet "\$cli" "\$tunnel_url"' "$runner" | sed -n '1s/:.*//p')
  post=$(rg -n '^elif ! verify_runtime_pins; then$' "$runner" | sed -n '1s/:.*//p')
  promote=$(rg -n '^[[:space:]]*mkdir -p "\$FOGELL_RECEIPT_DIR"' "$runner" | sed -n '1s/:.*//p')
  for marker in "$resolve_head" "$validate_head" "$archive" "$print_head" "$runner_cmp" "$verify_use" "$allowlist_use" "$restore" "$build" "$handshake" "$lease" "$local_access" "$remote_access" "$oracle" "$home" "$egress" "$plugin" "$empty" "$pre" "$tunnel" "$busy" "$run" "$post" "$promote"; do
    [[ "$marker" =~ ^[0-9]+$ ]] || return 1
  done
  [ "$(rg -c '^runner_path=\$\(realpath -e "\$0"\) \|\| die ' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^fence_script="\$cli_source/scripts/no-egress-fence\.sh"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^runtime_pin_checker="\$cli_source/scripts/check-corpus-runtime-pins\.sh"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^workspace_helper="\$cli_source/scripts/jenkins-workspace-v2\.sh"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^verify_corpus="\$cli_source/scripts/verify-corpus\.sh"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^allowlist="\$cli_source/differential/corpus-allowlist\.tsv"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^runtime_pins="\$cli_source/differential/corpus-runtime-pins\.tsv"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^snap_dir=\$\(mktemp -d\); cli_private=\$\(mktemp -d\); cli_source="\$cli_private/source"; cli_build="\$cli_private/output"; snaps=\(\)$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^cli="\$cli_build/fogell-diff\.dll"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^\[ "\$capability" = fogell-runtime-guard-v4 \] \\$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  \[ -n "\$cli_private" \] && rm -rf "\$cli_private"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  "\$runtime_pin_checker" "\$runtime_pins" "\$\{pin_ids\[@\]\}"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^source "\$workspace_helper" \|\| die ' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^remote_port=\$\{FOGELL_JENKINS_URL##\*:\}$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^tunnel_forward=127\.0\.0\.1:18084:127\.0\.0\.1:\$remote_port$' "$runner")" = 1 ] || return 1
  [ "$(rg -c -- '-o ExitOnForwardFailure=yes' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^tail --pid=\$\$ -f /dev/null 9>&- \| ssh -o BatchMode=yes -o ExitOnForwardFailure=yes ' "$runner")" = 1 ] || return 1
  [ "$(rg -c 'FOGELL_JENKINS_HOST.*echo tunneled; exec cat >/dev/null.*tunnel_fifo' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^FOGELL_JENKINS_URL="\$tunnel_url" "\$fence_script" fogell run -- \\$' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c 'if ! queue_json=$(curl --globoff -sS -m 10 "$tunnel_url/queue/api/json?tree=items[id]" 2>&1); then' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  FOGELL_JENKINS_BUILD_PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  export FOGELL_JENKINS_BUILD_PATH$' "$runner")" = 1 ] || return 1
  [ "$(rg -c 'read -r guard_pin guard_command guard_expectation _ guard_tool_path _ guard_image guard_digest guard_container_port guard_host_binding guard_node guard_plugin_count guard_plugin_sha guard_extra <<< "\$guard_row"$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  FOGELL_RUNTIME_GUARD_CASE_SHA=\$\{file_digests\[0\]\}$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  FOGELL_RUNTIME_GUARD_NODE=\$guard_node$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  FOGELL_RUNTIME_GUARD_COMMAND=\$guard_command$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  FOGELL_RUNTIME_GUARD_EXPECTATION=\$guard_expectation$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^  FOGELL_RUNTIME_GUARD_TOOL_PATH=\$guard_tool_path$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^    FOGELL_RUNTIME_GUARD_COMMAND FOGELL_RUNTIME_GUARD_EXPECTATION \\$' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c 'podman run --pull=never -d --name $oracle_name --label fogell.lane-token=$FOGELL_JENKINS_ACCESS_TOKEN' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^FOGELL_JENKINS_ACCESS_TOKEN=\$\(od -An -N16 -tx1 /dev/urandom \| tr -d '\'' \\n'\''\)$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^\[\[ "\$oracle_run_output" =~ \^\[0-9a-f\]\{64\}\$ \]\] \\$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^oracle_id=\$oracle_run_output$' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c '(\$h|length) == 1 and \$h[0].Type == \"volume\" and (\$h[0].Name|test(\"^[0-9a-f]{64}\$\"))' "$runner")" = 2 ] || return 1
  [ "$(rg -c 'fresh anonymous JENKINS_HOME volume' "$runner")" -ge 1 ] || return 1
  [ "$(rg -F -c 'index .Config.Labels \"fogell.lane-token\"' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c 'if [ "$owned" = "${FOGELL_JENKINS_ACCESS_TOKEN:-}" ] \' "$runner")" = 1 ] || return 1
  [ "$(rg -c 'podman rm -fv \$cleanup_oracle_id' "$runner")" = 1 ] || return 1
  [ "$(rg -c 'podman container exists \$cleanup_oracle_id' "$runner")" = 1 ] || return 1
  [ "$(rg -c 'podman volume exists \$oracle_home_volume' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^FOGELL_JENKINS_CONTAINER=\$oracle_id$' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c 'fogell_configure_jenkins_workspace_v2 "$FOGELL_JENKINS_HOST" "$FOGELL_JENKINS_CONTAINER"' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c 'container_q=$(fogell_quote_posix_shell_v2 "$FOGELL_JENKINS_CONTAINER")' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c ".plugins | sort_by(.shortName)" "$runner")" = 1 ] || return 1
  [ "$(rg -F -c '[ "$plugin_count" = "$oracle_plugin_count" ] && [ "$plugin_sha" = "$oracle_plugin_sha" ] \' "$runner")" = 1 ] || return 1
  [ "$(rg -F -c '(.[0].jobs|length)==0 and .[0].quietingDown==false and (.[1].items|length)==0 and .[2].busyExecutors==0' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^      && "\$fence_script" jenkins access-present >/dev/null 2>&1 \\$' "$runner")" = 1 ] || return 1
  [ "$(rg -c '^      && "\$fence_script" local access-present >/dev/null 2>&1 \\$' "$runner")" = 1 ] || return 1
  [ "$resolve_head" -lt "$validate_head" ] && [ "$validate_head" -lt "$archive" ] \
    && [ "$archive" -lt "$print_head" ] && [ "$print_head" -lt "$runner_cmp" ] \
    && [ "$runner_cmp" -lt "$verify_use" ] \
    && [ "$verify_use" -lt "$allowlist_use" ] && [ "$allowlist_use" -lt "$restore" ] \
    && [ "$restore" -lt "$build" ] && [ "$build" -lt "$handshake" ] \
    && [ "$handshake" -lt "$lease" ] && [ "$lease" -lt "$local_access" ] \
    && [ "$local_access" -lt "$remote_access" ] && [ "$remote_access" -lt "$oracle" ] \
    && [ "$oracle" -lt "$home" ] && [ "$home" -lt "$egress" ] \
    && [ "$egress" -lt "$plugin" ] && [ "$plugin" -lt "$empty" ] && [ "$empty" -lt "$pre" ] \
    && [ "$pre" -lt "$tunnel" ] \
    && [ "$tunnel" -lt "$busy" ] && [ "$busy" -lt "$run" ] \
    && [ "$run" -lt "$post" ] && [ "$post" -lt "$promote" ]
}

audit_access_sources() {
  fence=$1
  [ "$(rg -c '^jenkins_access_token\(\) \{$' "$fence")" = 1 ] || return 1
  [ "$(rg -c 'requires a 32-character lowercase hex lane token' "$fence")" = 1 ] || return 1
  [ "$(rg -c '^jenkins_access_uid\(\) \{$' "$fence")" = 1 ] || return 1
  [ "$(rg -c 'Jenkins access fence uid must be non-root' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'and ([.nftables[].rule? | select(. != null)] | length == 4)' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'and ([.nftables[].rule? | select(. != null)] | length == 2)' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'meta skuid $uid tcp dport $port counter accept comment \"fogell:$token:owner\"' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'meta skuid $uid tcp dport $port counter accept comment "fogell:$token:owner"' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'local_access_present || die "local Jenkins tunnel access fence did not match its exact installed rules"' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'jenkins_access_present || die "Jenkins host access fence ruleset or lane token changed"' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'local_access_present \' "$fence")" = 1 ] || return 1
  [ "$(rg -F -c 'jenkins_access_present \' "$fence")" = 1 ] || return 1
}

audit_build_path_sources() {
  jenkins=$1 cli_source=$2
  [ "$(rg -c '<hudson\.model\.StringParameterDefinition><name>PATH</name>' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'let pathPart = $"PATH={Uri.EscapeDataString path}"' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '$"&FOGELL_BUILD_TOKEN={Uri.EscapeDataString value}"' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '$"/job/{jobName}/buildWithParameters?{pathPart}{tokenPart}"' "$jenkins")" = 1 ] || return 1
  [ "$(rg -c 'FOGELL_JENKINS_BUILD_PATH' "$cli_source")" = 1 ] || return 1
  [ "$(rg -c 'path when path = expected -> Some path' "$cli_source")" = 1 ] || return 1
  [ "$(rg -c 'FOGELL_RUNTIME_GUARD_CASE_SHA' "$cli_source")" = 1 ] || return 1
  [ "$(rg -c 'FOGELL_RUNTIME_GUARD_EXPECTATION' "$cli_source")" = 1 ] || return 1
  [ "$(rg -c 'runtimeGuardCaseSha' "$cli_source")" -ge 2 ] || return 1
  [ "$(rg -F -c '| "absent" when toolPath = "-" -> Ok(AbsentCommand command)' "$jenkins")" = 1 ] || return 1
  [ "$(rg -c 'runtimeGuardScript guard' "$jenkins")" = 1 ] || return 1
  [ "$(rg -c "runtime guard required Jenkins node" "$jenkins")" = 1 ] || return 1
  [ "$(rg -c 'FOGELL_RUNTIME_GUARD_OK' "$jenkins")" -ge 2 ] || return 1
  [ "$(rg -F -c 'let scheduledResults = executeScheduled runOne scheduled' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'match validateRuntimeGuardNode guard.RequiredNode rawLines with' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '| Inline script -> jobXml cfg.BuildPath script' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '(buildTriggerPath activeJobName cfg.BuildPath expectedBuildToken)' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'runtimeGuardResultFailure result' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c '$"_fogell-runtime-guard-{corpusJobName}"' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'let guardJobName = runtimeGuardJobName jobName' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'let cleanupNames = cleanupJobNames jobName cfg.RuntimeGuard.IsSome' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'for cleanupName in cleanupNames do' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c '/job/{cleanupName}/doDelete' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c 'JobName = corpusJobName' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'JobName = guardJobName' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c 'BuildNumber = index + 1' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'BuildNumber = 1' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'BuildNumber = 2' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'item.ExpectedTargetMarker' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'item.ExpectedBuildToken' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'runOneInner' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c '/createItem?name={activeJobName}' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '/job/{activeJobName}/config.xml' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '/job/{activeJobName}/{buildNumber}/api/json' "$jenkins")" = 3 ] || return 1
  [ "$(rg -F -c '/job/{activeJobName}/{buildNumber}/consoleText' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'exportRawConsole cfg.RawConsoleExport activeJobName buildNumber console' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'Trace.hashWorkspace (IO.Path.Combine(root, activeJobName))' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'template.Replace("{job}", activeJobName)' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '/var/jenkins_home/workspace/{activeJobName}' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'Convert.ToHexString(RandomNumberGenerator.GetBytes 16).ToLowerInvariant()' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'targetRuntimeMarker guard.CaseSha nonce' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '[ \"$PATH\" = \"{guard.BuildPath}\" ] || exit 90' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c '[ \"$FOGELL_BUILD_TOKEN\" = \"{buildToken}\" ] || exit 93' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'actual=$(command -v {command}) || exit 91' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c 'if command -v {command} >/dev/null 2>&1; then exit 94; fi' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c 'validateAndRemoveTargetRuntimeMarker' "$jenkins")" = 2 ] || return 1
  [ "$(rg -F -c 'if isNull r.Headers.Location then None' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'Regex.Match(location, "/queue/item/([1-9][0-9]*)/?(?:$|[?#])")' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '/queue/item/{queueId}/api/json' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'match queueExecutableNumber queueJson with' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'observed <> buildNumber' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'let queueId = root.GetProperty("queueId").GetInt64()' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '| observed, [ token ] when observed = expectedQueueId && token = expectedToken -> Ok()' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'match validateBuildOwnership queueId token parameterJson with' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '/job/{activeJobName}/{buildNumber}/replay/' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c 'String.Equals(observed, expectedScript, StringComparison.Ordinal)' "$jenkins")" = 1 ] || return 1
  [ "$(rg -F -c '| [ "--runtime-guard-capability" ] ->' "$cli_source")" = 1 ] || return 1
  [ "$(rg -F -c 'printfn "fogell-runtime-guard-v4"' "$cli_source")" = 1 ] || return 1
}

audit_runner scripts/run-corpus-differential.sh \
  || { echo "RUNTIME-PIN PROOF FAILED: runner pin boundary markers are missing or misordered" >&2; exit 1; }
audit_access_sources scripts/no-egress-fence.sh \
  || { echo "RUNTIME-PIN PROOF FAILED: Jenkins access-fence guards are missing" >&2; exit 1; }
audit_build_path_sources src/Fogell.Differential/Jenkins.fs tools/Fogell.Differential.Cli/Program.fs \
  || { echo "RUNTIME-PIN PROOF FAILED: Jenkins build-context PATH pin is missing" >&2; exit 1; }
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i '/^  FOGELL_JENKINS_BUILD_PATH=\/usr\/local\/sbin:/d' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: build-PATH lane-wiring mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i '/^  FOGELL_RUNTIME_GUARD_NODE=\$guard_node$/d' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: Jenkins-node guard-wiring mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's#buildWithParameters?{pathPart}{tokenPart}#build#' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: non-parameterized build-trigger mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's#<hudson.model.StringParameterDefinition><name>PATH</name>#<hudson.model.StringParameterDefinition><name>NOT_PATH</name>#' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: wrong Jenkins parameter-name mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i '0,/BuildNumber = 1/s//BuildNumber = 2/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: pre-guard numbering/removal mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i '0,/BuildNumber = 2/s//BuildNumber = 1/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: post-guard numbering/removal mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's/match validateRuntimeGuardNode guard.RequiredNode rawLines with/match Ok() with/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: removed per-build node validation mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's/jobXml cfg.BuildPath script/jobXml None script/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: job-config PATH propagation mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's/buildTriggerPath activeJobName cfg.BuildPath expectedBuildToken/buildTriggerPath activeJobName None expectedBuildToken/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: build-trigger PATH propagation mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i '0,/runtimeGuardResultFailure result/s//None/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: semantic pre-guard halt mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i '0,/JobName = guardJobName/s//JobName = corpusJobName/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: guard/corpus shared-history mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's/_fogell-runtime-guard-{corpusJobName}/{corpusJobName}-runtime-guard/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: colliding guard-job namespace mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's/cleanupJobNames jobName cfg.RuntimeGuard.IsSome/cleanupJobNames jobName true/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: unguarded sibling-deletion mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's#/job/{activeJobName}/config.xml#/job/{jobName}/config.xml#' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: wrong job-config target mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i 's/buildTriggerPath activeJobName cfg.BuildPath expectedBuildToken/buildTriggerPath jobName cfg.BuildPath expectedBuildToken/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: wrong build-trigger job mutant was accepted" >&2; exit 1
fi
cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
sed -i '0,/\/job\/{activeJobName}\/{buildNumber}\/api\/json/s//\/job\/{jobName}\/{buildNumber}\/api\/json/' "$scratch/Jenkins.fs"
if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
  echo "RUNTIME-PIN PROOF FAILED: wrong build-poll job mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i '0,/^verify_runtime_pins || die /{/^verify_runtime_pins || die /d;}' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: pre-execution check mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i '/^elif ! verify_runtime_pins; then$/d' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: pre-promotion check mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i 's/dotnet "$cli" "$tunnel_url"/dotnet "$cli" "$FOGELL_JENKINS_URL"/' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: direct-HTTP execution mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i 's/-o ExitOnForwardFailure=yes //' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: permissive tunnel-bind mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i '/ExitOnForwardFailure=yes/s/tail --pid=$$ -f \/dev\/null 9>&- | //' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: owner-unbound tunnel mutant was accepted" >&2; exit 1
fi
cp scripts/run-corpus-differential.sh "$scratch/runner"
sed -i 's/127.0.0.1:$remote_port/attacker.invalid:$remote_port/' "$scratch/runner"
if audit_runner "$scratch/runner"; then
  echo "RUNTIME-PIN PROOF FAILED: uninspected tunnel-target mutant was accepted" >&2; exit 1
fi

reject_runner_mutant() {
  label=$1 expression=$2
  cp scripts/run-corpus-differential.sh "$scratch/runner"
  sed -i "$expression" "$scratch/runner"
  cmp -s scripts/run-corpus-differential.sh "$scratch/runner" \
    && { echo "RUNTIME-PIN PROOF FAILED: $label mutant did not apply" >&2; exit 1; }
  if audit_runner "$scratch/runner"; then
    echo "RUNTIME-PIN PROOF FAILED: $label mutant was accepted" >&2; exit 1
  fi
  printf '  refused %s mutant\n' "$label"
}

reject_fence_mutant() {
  label=$1 expression=$2
  cp scripts/no-egress-fence.sh "$scratch/no-egress-fence.sh"
  sed -i "$expression" "$scratch/no-egress-fence.sh"
  cmp -s scripts/no-egress-fence.sh "$scratch/no-egress-fence.sh" \
    && { echo "RUNTIME-PIN PROOF FAILED: $label mutant did not apply" >&2; exit 1; }
  if audit_access_sources "$scratch/no-egress-fence.sh"; then
    echo "RUNTIME-PIN PROOF FAILED: $label mutant was accepted" >&2; exit 1
  fi
  printf '  refused %s mutant\n' "$label"
}

reject_jenkins_mutant() {
  label=$1 expression=$2
  cp src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs"
  sed -i "$expression" "$scratch/Jenkins.fs"
  cmp -s src/Fogell.Differential/Jenkins.fs "$scratch/Jenkins.fs" \
    && { echo "RUNTIME-PIN PROOF FAILED: $label mutant did not apply" >&2; exit 1; }
  if audit_build_path_sources "$scratch/Jenkins.fs" tools/Fogell.Differential.Cli/Program.fs; then
    echo "RUNTIME-PIN PROOF FAILED: $label mutant was accepted" >&2; exit 1
  fi
  printf '  refused %s mutant\n' "$label"
}

reject_runner_mutant "unverified source commit resolution" "s/HEAD\\^{commit}/HEAD~1/"
reject_runner_mutant "missing source commit OID validation" '/^\[\[ "$source_head" =~ /d'
reject_runner_mutant "non-resolved CLI archive" 's/git archive --format=tar "$source_head"/git archive --format=tar HEAD/'
reject_runner_mutant "missing source commit evidence" '/^echo "corpus lane: evidence-control commit \$source_head"$/d'
reject_runner_mutant "policy use before HEAD archive" '/^git archive --format=tar "$source_head"/i"$verify_corpus" >\/dev\/null || die "early policy use"'
reject_runner_mutant "missing executing-runner HEAD equality" '/^cmp -s -- "$runner_path"/d'
reject_runner_mutant "working-tree no-egress control" 's#fence_script="$cli_source/scripts/no-egress-fence.sh"#fence_script="scripts/no-egress-fence.sh"#'
reject_runner_mutant "working-tree runtime-pin checker" 's#runtime_pin_checker="$cli_source/scripts/check-corpus-runtime-pins.sh"#runtime_pin_checker="scripts/check-corpus-runtime-pins.sh"#'
reject_runner_mutant "working-tree workspace helper" 's#workspace_helper="$cli_source/scripts/jenkins-workspace-v2.sh"#workspace_helper="scripts/jenkins-workspace-v2.sh"#'
reject_runner_mutant "working-tree corpus verifier" 's#verify_corpus="$cli_source/scripts/verify-corpus.sh"#verify_corpus="scripts/verify-corpus.sh"#'
reject_runner_mutant "working-tree executed-surface allowlist" 's#allowlist="$cli_source/differential/corpus-allowlist.tsv"#allowlist="differential/corpus-allowlist.tsv"#'
reject_runner_mutant "working-tree runtime-pin policy" 's#runtime_pins="$cli_source/differential/corpus-runtime-pins.tsv"#runtime_pins="differential/corpus-runtime-pins.tsv"#'
reject_runner_mutant "unlocked CLI restore" 's/ --locked-mode / /'
reject_runner_mutant "shared CLI output" 's#cli="$cli_build/fogell-diff.dll"#cli="tools/Fogell.Differential.Cli/bin/fogell-diff.dll"#'
reject_runner_mutant "missing CLI capability handshake" 's/\[ "$capability" = fogell-runtime-guard-v4 \]/[ -n "$capability" ]/'
reject_runner_mutant "missing resolution-expectation parsing" 's/guard_command guard_expectation _ guard_tool_path/guard_command _ _ guard_tool_path/'
reject_runner_mutant "missing resolution-expectation assignment" '/^  FOGELL_RUNTIME_GUARD_EXPECTATION=\$guard_expectation$/d'
reject_runner_mutant "missing resolution-expectation export" '/^    FOGELL_RUNTIME_GUARD_COMMAND FOGELL_RUNTIME_GUARD_EXPECTATION \\/s/ FOGELL_RUNTIME_GUARD_EXPECTATION//'
reject_runner_mutant "missing local pre-listener access fence" '/^"\$fence_script" local access-apply$/d'
reject_runner_mutant "missing remote pre-listener access fence" '/^"\$fence_script" jenkins access-apply$/d'
reject_runner_mutant "mutable oracle image pull" 's/podman run --pull=never/podman run --pull=always/'
reject_runner_mutant "abbreviated oracle identity" '/^\[\[ "$oracle_run_output" =~ /d'
reject_runner_mutant "non-exact anonymous Jenkins home" '/JENKINS_HOME is not one fresh anonymous volume/d'
reject_runner_mutant "name-based collector identity" 's/FOGELL_JENKINS_CONTAINER=$oracle_id/FOGELL_JENKINS_CONTAINER=$oracle_name/'
reject_runner_mutant "missing plugin-closure comparison" '/^\[ "$plugin_count" = "$oracle_plugin_count" \]/d'
reject_runner_mutant "missing empty-state attestation" '/(\.\[0\]\.jobs|length)==0/d'
reject_runner_mutant "missing cleanup ownership label" '/^    if \[ "$owned" = /d'
reject_runner_mutant "missing anonymous-volume cleanup proof" '/podman volume exists \$oracle_home_volume/d'
reject_runner_mutant "missing remote access heartbeat" '/"\$fence_script" jenkins access-present >\/dev\/null 2>&1 \\/d'
reject_runner_mutant "missing local access heartbeat" '/"\$fence_script" local access-present >\/dev\/null 2>&1 \\/d'
reject_runner_mutant "glob-expanding tunneled queue check" '/queue_json=\$(curl --globoff/s/--globoff //'

reject_fence_mutant "missing access token validator" 's/^jenkins_access_token()/jenkins_access_token_unchecked()/'
reject_fence_mutant "missing authenticated uid validator" 's/^jenkins_access_uid()/jenkins_access_uid_unchecked()/'
reject_fence_mutant "weakened remote exact-rule count" '0,/length == 4/s//length >= 4/'
reject_fence_mutant "weakened local exact-rule count" '/^local_access_present()/,/^}/s/length == 2/length >= 2/'
reject_fence_mutant "remote owner rule without uid" '/comment \\"fogell:\$token:owner\\"/s/meta skuid \$uid //'
reject_fence_mutant "local owner rule without uid" '/comment "fogell:\$token:owner"/s/meta skuid \$uid //'
reject_fence_mutant "missing remote apply verification" '/jenkins_access_present || die "Jenkins host access fence ruleset or lane token changed"/d'
reject_fence_mutant "missing local apply verification" '/local_access_present || die "local Jenkins tunnel access fence did not match its exact installed rules"/d'
reject_fence_mutant "missing remote removal ownership check" '/^  jenkins_access_present \\/d'
reject_fence_mutant "missing local removal ownership check" '/^  local_access_present \\/d'

reject_jenkins_mutant "constant target nonce" 's/Convert.ToHexString(RandomNumberGenerator.GetBytes 16).ToLowerInvariant()/"constant"/'
reject_jenkins_mutant "missing target PATH check" '/          \[ \\"\$PATH\\" = \\"{guard.BuildPath}\\" \] || exit 90/d'
reject_jenkins_mutant "missing target build-token check" '/\$FOGELL_BUILD_TOKEN.*exit 93/d'
reject_jenkins_mutant "missing target command-resolution check" '/          actual=\$(command -v {command}) || exit 91/d'
reject_jenkins_mutant "missing target absent-command check" '/          if command -v {command} >\/dev\/null 2>&1; then exit 94; fi/d'
reject_jenkins_mutant "missing target marker validation" 's/validateAndRemoveTargetRuntimeMarker/acceptTargetRuntimeMarker/g'
reject_jenkins_mutant "missing queue Location" 's/if isNull r.Headers.Location then None/if true then None/'
reject_jenkins_mutant "loose queue Location parser" 's/\[1-9\]\[0-9\]\*/[0-9]*/'
reject_jenkins_mutant "wrong queue poll ownership" 's#/queue/item/{queueId}/api/json#/queue/api/json#'
reject_jenkins_mutant "missing owned build-number check" '/Some observed when observed <> buildNumber/d'
reject_jenkins_mutant "missing queueId-token ownership validation" 's/match validateBuildOwnership queueId token parameterJson with/match Ok() with/'
reject_jenkins_mutant "weakened exact ownership token" 's/observed = expectedQueueId && token = expectedToken/observed = expectedQueueId/'
reject_jenkins_mutant "non-exact Replay definition" 's/String.Equals(observed, expectedScript, StringComparison.Ordinal)/true/'

cp tools/Fogell.Differential.Cli/Program.fs "$scratch/Program.fs"
sed -i 's/printfn "fogell-runtime-guard-v4"/printfn "fogell-runtime-guard-v3"/' "$scratch/Program.fs"
if cmp -s tools/Fogell.Differential.Cli/Program.fs "$scratch/Program.fs"; then
  echo "RUNTIME-PIN PROOF FAILED: CLI capability mutant did not apply" >&2; exit 1
fi
if audit_build_path_sources src/Fogell.Differential/Jenkins.fs "$scratch/Program.fs"; then
  echo "RUNTIME-PIN PROOF FAILED: stale CLI capability mutant was accepted" >&2; exit 1
fi
echo "=== corpus runtime pin: private HEAD controls/CLI, disposable oracle, access fences, owned queue, Replay, and target guards mutation-proven ==="
echo "CORPUS RUNTIME PIN: ALL ASSERTIONS PASSED"
