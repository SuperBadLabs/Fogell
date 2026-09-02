#!/usr/bin/env bash
# FG-231 — prove that the FG-224 runnable-controller proof cannot hang: every
# planted stall below must become a named refusal within the proof's own
# budgets, and no controller process or container may survive it.
#
# Each arm runs a byte-mutated copy of scripts/prove-runnable-controller.sh
# under an outer timeout that is the proof failing, not the bound firing. A
# mutation is asserted to have changed exactly the line it targets, so a
# refactor that moves the target is a refusal here rather than a silently
# unexercised arm. The staged copy resolves the real repository (it no longer
# lives in it), which is the one plant every arm shares.
#
# The arms are the hang shapes measured or named on 2026-09-01:
#   - a reap of a controller that ignores SIGTERM;
#   - an HTTP request against a socket that accepts and never answers;
#   - a synchronous Run.Host invocation that never exits, once leaving on
#     SIGTERM and once ignoring it (GNU timeout returns 124 and 137);
#   - with FOGELL_FG224_CONTROLLER_IMAGE set, a refusal after the container
#     launched whose runtime stop then hangs (a daemon that stopped
#     answering), and a runtime run that takes 15 s to create the container
#     (a cold image pull) so the identity budget expires first, runtime stop
#     fails fast on a container that does not exist, and the client then
#     brings the controller up with nothing left to stop it — the two
#     readings of hosted jobs 100045425020 and 100055372746.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
proof="$repo/scripts/prove-runnable-controller.sh"
controller_image=${FOGELL_FG224_CONTROLLER_IMAGE:-}
# Outer bound per arm. The proof's widest single budget is the 80 s tail poll;
# an arm that reaches this has hung, which is the defect this proves absent.
arm_budget=${FOGELL_FG231_ARM_BUDGET:-180}
scratch=$(mktemp -d /tmp/fogell-fg231-proof.XXXXXX)
runtime=${FOGELL_CONTAINER_RUNTIME:-podman}
real_runtime=$(command -v "$runtime" || true)
blackhole_pid=""
arms_run=0

cleanup() {
  if [[ -n "$blackhole_pid" ]]; then
    kill -KILL "$blackhole_pid" 2>/dev/null || true
    wait "$blackhole_pid" 2>/dev/null || true
  fi
  if [[ ${FOGELL_KEEP_FG231_PROOF:-0} = 1 ]]; then
    echo "FG-231 proof scratch retained: $scratch" >&2
    return 0
  fi
  case "$scratch" in
    /tmp/fogell-fg231-proof.*) rm -rf -- "$scratch" ;;
    *) echo "FG-231 REFUSED: unsafe cleanup path" >&2 ;;
  esac
}
trap cleanup EXIT

[[ -x "$proof" ]] || { echo "FG-231 REFUSED: $proof is not executable" >&2; exit 2; }
[[ "$runtime" = podman || "$runtime" = docker ]] \
  || { echo "FG-231 REFUSED: FOGELL_CONTAINER_RUNTIME must be exactly podman or docker" >&2; exit 2; }
[[ -n "$real_runtime" ]] || { echo "FG-231 REFUSED: $runtime is required" >&2; exit 2; }
command -v python3 >/dev/null || { echo "FG-231 REFUSED: python3 is required for the silent-server arm" >&2; exit 2; }
command -v timeout >/dev/null || { echo "FG-231 REFUSED: timeout is required" >&2; exit 2; }
command -v rg >/dev/null || { echo "FG-231 REFUSED: rg (ripgrep) is required" >&2; exit 2; }

# Stage a copy that resolves the real repository, and prove that plant took.
stage() {
  local name="$1"
  local copy="$scratch/$name/scripts/prove-runnable-controller.sh"
  mkdir -p "$(dirname "$copy")"
  sed "s|^repo=.*|repo=$repo|" "$proof" >"$copy"
  chmod +x "$copy"
  [[ "$(rg -F -c "repo=$repo" "$copy")" = 1 ]] \
    || { echo "FG-231 REFUSED: staged copy did not bind the repository path" >&2; exit 1; }
  printf '%s\n' "$copy"
}

# Replace one exact text (possibly spanning lines). The target must occur
# exactly once before and zero times after, and the replacement exactly once
# after; anything else is a refusal naming the target.
plant() {
  local file="$1"
  local target="$2"
  local replacement="$3"
  python3 - "$file" "$target" "$replacement" <<'PY' \
    || { echo "FG-231 REFUSED: plant did not take: $target" >&2; exit 1; }
import sys, pathlib
path, target, replacement = sys.argv[1:]
p = pathlib.Path(path)
before = p.read_text()
if before.count(target) != 1:
    sys.exit(f"plant target occurs {before.count(target)} times, expected 1")
after = before.replace(target, replacement)
if after.count(target) != 0 or after.count(replacement) != 1:
    sys.exit("plant did not change bytes")
p.write_text(after)
PY
}

# The harness's own runtime calls are bounded too: a daemon that stops
# answering is one of the shapes under test, and an inventory that hangs
# outside an arm would turn this proof into the hang it exists to refuse.
runtime_budget=30

# The inventory is the proof's central cleanup assertion, so it must never
# read as empty when it did not run: any failure is a named refusal.
leftover_containers() {
  local rc=0
  local names
  names=$(timeout -k 5 "$runtime_budget" "$real_runtime" ps -a --filter name=fogell-fg224-proof- --format '{{.Names}}' 2>"$scratch/inventory.stderr") \
    || rc=$?
  if (( rc == 124 || rc == 137 )); then
    echo "FG-231 REFUSED: $runtime did not answer the container inventory within ${runtime_budget} s" >&2
    exit 1
  elif (( rc != 0 )); then
    echo "FG-231 REFUSED: container inventory failed ($rc): $(tr '\n' ' ' <"$scratch/inventory.stderr")" >&2
    exit 1
  fi
  printf '%s\n' "$names"
}

# Run one mutated copy. It must exit nonzero on its own, before the outer
# bound, with the expected refusal on stderr, and leave nothing behind. The
# copy's mode is named by the arm, never inherited from this proof's own
# environment: a native arm that inherits the image variable runs in container
# mode, where its planted line is dead code (FG-231 records the runs that
# showed it).
run_arm() {
  local name="$1"
  local copy="$2"
  local expected="$3"
  shift 3
  local out="$scratch/$name.stdout"
  local err="$scratch/$name.stderr"
  local started rc elapsed leftovers
  started=$(date +%s)
  set +e
  timeout -k 10 "$arm_budget" env -u FOGELL_FG224_CONTROLLER_IMAGE "$@" "$copy" >"$out" 2>"$err"
  rc=$?
  set -e
  elapsed=$(( $(date +%s) - started ))
  # The proof ends every refusal with status 1 (its ERR diagnostic exits 1),
  # so 124 and 137 here can only be this outer bound.
  if (( rc == 124 || rc == 137 )); then
    echo "FG-231 REFUSED: arm $name hung: no bound fired within ${arm_budget} s (rc $rc after ${elapsed} s)" >&2
    sed 's/^/  /' "$err" >&2
    exit 1
  fi
  if (( rc == 0 )); then
    echo "FG-231 REFUSED: arm $name passed in ${elapsed} s: the planted stall was not refused" >&2
    tail -c 400 "$out" | sed 's/^/  stdout: /' >&2
    sed 's/^/  stderr: /' "$err" >&2
    exit 1
  fi
  if ! rg -q -- "$expected" "$err"; then
    echo "FG-231 REFUSED: arm $name failed for an unrelated reason (expected /$expected/):" >&2
    sed 's/^/  /' "$err" >&2
    exit 1
  fi
  leftovers=$(leftover_containers)
  if [[ -n "$leftovers" ]]; then
    timeout -k 5 "$runtime_budget" "$real_runtime" rm -f $leftovers >/dev/null 2>&1 || true
    echo "FG-231 REFUSED: arm $name left a controller container behind: $leftovers" >&2
    exit 1
  fi
  # Single-tenant by construction: the only controller built from this tree
  # that may be running is the arm's own.
  if pgrep -f "$repo/src/Fogell.Controller.Host/bin/" >/dev/null 2>&1; then
    echo "FG-231 REFUSED: arm $name left a native controller process behind" >&2
    exit 1
  fi
  echo "FG-231 arm $name: refused in ${elapsed} s with: $(rg -m 1 -- "$expected" "$err")"
  arms_run=$((arms_run + 1))
}

# Proven to fail before it is trusted: a runtime whose `ps` exits 1 must be a
# refusal naming the inventory, never an empty inventory.
mkdir -p "$scratch/runtime-ps-fails"
cat >"$scratch/runtime-ps-fails/$runtime" <<EOS
#!/usr/bin/env bash
if [[ "\${1:-}" = ps ]]; then
  echo "Cannot connect to the container runtime (planted)" >&2
  exit 1
fi
exec "$real_runtime" "\$@"
EOS
chmod +x "$scratch/runtime-ps-fails/$runtime"
inventory_verdict=$( (real_runtime="$scratch/runtime-ps-fails/$runtime" leftover_containers >/dev/null) 2>&1; echo "rc=$?" )
rg -q 'FG-231 REFUSED: container inventory failed \(1\): Cannot connect to the container runtime \(planted\)' <<<"$inventory_verdict" \
  && rg -q '^rc=1$' <<<"$inventory_verdict" \
  || { echo "FG-231 REFUSED: a failing runtime ps was not refused by the inventory: $inventory_verdict" >&2; exit 1; }

preexisting=$(leftover_containers)
[[ -z "$preexisting" ]] \
  || { echo "FG-231 REFUSED: controller containers exist before the proof: $preexisting" >&2; exit 1; }

# Arm 1: the native controller ignores SIGTERM. stop_controller's bounded reap
# must kill it and name it; the proof exits through the ERR diagnostic.
copy=$(stage native-stop-ignored)
plant "$copy" \
  '    kill -TERM "$host_pid"
    wait_bounded "$host_pid" "$reap_budget_ms" "native controller after SIGTERM"' \
  '    kill -0 "$host_pid"
    wait_bounded "$host_pid" "$reap_budget_ms" "native controller after SIGTERM"'
run_arm native-stop-ignored "$copy" \
  'native controller after SIGTERM \(pid [0-9]+\) did not exit within 15000 ms and was killed'

# Arm 2: an HTTP request against a socket that accepts and never answers. The
# per-request bound must expire and the ERR diagnostic must name the call.
python3 - "$scratch/blackhole.port" <<'PY' &
import socket, sys
s = socket.socket()
s.bind(("127.0.0.1", 0))
s.listen(16)
open(sys.argv[1], "w").write(str(s.getsockname()[1]))
held = []
while True:
    conn, _ = s.accept()
    held.append(conn)
PY
blackhole_pid=$!
for _ in $(seq 1 100); do
  [[ -s "$scratch/blackhole.port" ]] && break
  sleep 0.05
done
[[ -s "$scratch/blackhole.port" ]] || { echo "FG-231 REFUSED: silent server did not publish its port" >&2; exit 1; }
blackhole_port=$(cat "$scratch/blackhole.port")
copy=$(stage http-silent-server)
plant "$copy" \
  '  poison_terminal=$(curl --max-time "$http_max_time" -fsS -H "$auth" "$builds_url/$poison_build_id")' \
  '  poison_terminal=$(curl --max-time "$http_max_time" -fsS -H "$auth" "http://127.0.0.1:'"$blackhole_port"'/")'
run_arm http-silent-server "$copy" \
  'FG-224 REFUSED: line [0-9]+: `poison_terminal=\$\(curl .*` exited 28'

# Arm 3: the synchronous same-child Run.Host restart never exits. The process
# budget must expire and the ERR diagnostic must say so.
copy=$(stage run-host-restart-hang)
plant "$copy" \
  'bounded "$process_budget" "$run_host" \' \
  'bounded "$process_budget" /bin/sh -c '"'"'exec /bin/sleep 1000'"'"' -- \'
run_arm run-host-restart-hang "$copy" \
  'FG-224 REFUSED: line [0-9]+: `timeout -k 5 "\$1" "\$\{@:2\}"` exited 124 \(budget expired\) in bounded called from line [0-9]+'

# Arm 3b: the same restart, but the process ignores SIGTERM. GNU timeout then
# kills it after the grace period and returns 137, which must still read as
# the budget expiring.
copy=$(stage run-host-restart-ignores-term)
plant "$copy" \
  'bounded "$process_budget" "$run_host" \' \
  'bounded "$process_budget" /bin/sh -c '"'"'trap "" TERM; exec /bin/sleep 1000'"'"' -- \'
run_arm run-host-restart-ignores-term "$copy" \
  'FG-224 REFUSED: line [0-9]+: `timeout -k 5 "\$1" "\$\{@:2\}"` exited 137 \(budget expired\) in bounded called from line [0-9]+'

# Arms 4 and 5 need the digest-pinned image. Both end in the EXIT trap's
# bounded reap, which must kill the runtime client, remove the container
# by name, and name the reap; each first reproduces the refusal that reached
# the trap.
if [[ -n "$controller_image" ]]; then
  reap_expected='controller container client \(pid [0-9]+\) did not exit within 15000 ms and was killed'

  # Arm 4: the container is up (its PID1 identity is compared against a name
  # the controller cannot have, so the launch refuses) and runtime stop never
  # returns.
  mkdir -p "$scratch/runtime-stop-hangs" "$scratch/runtime-run-delayed"
  cat >"$scratch/runtime-stop-hangs/$runtime" <<EOS
#!/usr/bin/env bash
[[ "\${1:-}" = stop ]] && exec /bin/sleep infinity
exec "$real_runtime" "\$@"
EOS
  # Arm 5: runtime run takes 15 s to create the container, longer than the
  # 10 s identity budget. The proof refuses with the container absent, the
  # real runtime stop fails fast on a name that does not exist yet, and the
  # client then starts the controller during the reap. Nothing is mutated in
  # the proof itself: this is hosted job 100045425020's own sequence.
  cat >"$scratch/runtime-run-delayed/$runtime" <<EOS
#!/usr/bin/env bash
[[ "\${1:-}" = run ]] && /bin/sleep 15
exec "$real_runtime" "\$@"
EOS
  chmod +x "$scratch/runtime-stop-hangs/$runtime" "$scratch/runtime-run-delayed/$runtime"

  copy=$(stage container-stop-hangs)
  plant "$copy" \
    '    if [[ "$pid1_executable" != "$controller" ]]; then' \
    '    if [[ "$pid1_executable" != "$controller-never" ]]; then'
  run_arm container-stop-hangs "$copy" "$reap_expected" \
    "PATH=$scratch/runtime-stop-hangs:$PATH" "FOGELL_FG224_CONTROLLER_IMAGE=$controller_image"
  rg -q 'FG-224 REFUSED: container PID1 was /.*, expected /.* \(container running;' "$scratch/container-stop-hangs.stderr" \
    || { echo "FG-231 REFUSED: container-stop-hangs did not first refuse on PID1 identity with the container up" >&2; exit 1; }

  copy=$(stage container-run-delayed)
  run_arm container-run-delayed "$copy" "$reap_expected" \
    "PATH=$scratch/runtime-run-delayed:$PATH" "FOGELL_FG224_CONTROLLER_IMAGE=$controller_image"
  rg -q 'FG-224 REFUSED: container PID1 was unreadable, expected .* \(container absent;' "$scratch/container-run-delayed.stderr" \
    || { echo "FG-231 REFUSED: container-run-delayed did not first refuse on an absent container" >&2; exit 1; }
else
  echo "FG-231: FOGELL_FG224_CONTROLLER_IMAGE is unset; the two container-stop arms did not run"
fi

echo "FG-231 PROOF PASS: $arms_run planted stalls in the runnable-controller proof each became a named refusal within budget and left no controller behind"
