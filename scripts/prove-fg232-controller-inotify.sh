#!/usr/bin/env bash
# FG-232 — prove the controller's inotify footprint is bounded whatever
# directory it is started from.
#
# ASP.NET Core roots the host's file provider at the current working directory
# and, to reload appsettings.json on change, watches that whole tree with one
# inotify watch per directory. On 2026-09-02 a Fogell.Controller.Host started
# from a home directory of ~268k directories held 65,361 of the user's 65,536
# inotify watches (fs.inotify.max_user_watches), and every other
# FileSystemWatcher for the user then failed: the inbox watcher reported
# OVERFLOW and scripts/prove-approval-watcher.sh failed 3/3. The controller
# now pins its content root to the apphost directory and reads configuration
# once, so it holds no inotify instance at all (docs/tickets/FG-232.md).
#
# The checker is shown to fail before it is trusted:
#   - a process that cannot be read is a named refusal, never a count of zero
#     (FG-103 rule 2: a check that cannot decide says so);
#   - a stand-in holding exactly the bound is refused with its count named;
#   - a stand-in one below the bound passes with its count named, so the
#     counter is proven to count and the bound to be strict.
# Then the real controller is started from a scratch tree with more
# subdirectories than the bound, against a scratch database, and must hold
# fewer watches than the bound; a controller that roots at its cwd again would
# hold one per subdirectory plus one and refuse here.
#
# errtrace: the ERR diagnostic below must fire inside the helper functions
# that own every bound, which bash does not do for a function body by default.
set -Eeuo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
container=${FOGELL_PG_CONTAINER:-fogell-fg060a}
port=${FOGELL_PG_PORT:-}
runtime=${FOGELL_CONTAINER_RUNTIME:-podman}
[[ "$runtime" = podman || "$runtime" = docker ]] \
  || { echo "FG-232 REFUSED: FOGELL_CONTAINER_RUNTIME must be exactly podman or docker" >&2; exit 2; }
command -v "$runtime" >/dev/null \
  || { echo "FG-232 REFUSED: $runtime is required for the scratch database" >&2; exit 2; }
command -v timeout >/dev/null || { echo "FG-232 REFUSED: coreutils timeout is required to bound this proof" >&2; exit 2; }
[[ "$container" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] \
  || { echo "FG-232 REFUSED: FOGELL_PG_CONTAINER must be a literal container name" >&2; exit 2; }
[[ -n "$port" && "$port" =~ ^[0-9]{1,5}$ ]] \
  || { echo "FG-232 REFUSED: FOGELL_PG_PORT must be set to the runtime-allocated PostgreSQL host port" >&2; exit 2; }
port_number=$((10#$port))
(( port_number >= 1 && port_number <= 65535 )) \
  || { echo "FG-232 REFUSED: FOGELL_PG_PORT must be set to the runtime-allocated PostgreSQL host port" >&2; exit 2; }
# Distinct from the FG-224 proof's 18083 so the two can never contend.
listen_port=${FOGELL_FG232_PORT:-18084}
configuration=${FOGELL_BUILD_CONFIGURATION:-Release}
base_url="http://127.0.0.1:${listen_port}"
database="fogell_fg232_$$_$(date +%s)"
role="fogell_fg232_runtime_$$_$(date +%s)"
scratch=$(mktemp -d /tmp/fogell-fg232-proof.XXXXXX)
cleanup_scratch() {
  case "$scratch" in
    /tmp/fogell-fg232-proof.*) rm -rf -- "$scratch" ;;
    *) echo "FG-232 REFUSED: unsafe cleanup path" >&2 ;;
  esac
}
trap cleanup_scratch EXIT
state_root="$scratch/state"
token_file="$scratch/token"
host_log="$scratch/controller.log"
host_pid=""
stand_in_pid=""

# The bound, exclusive. The fixed controller measures zero watches and zero
# instances; 64 is 1/1024 of the default user limit, so a controller under it
# cannot starve the user's other watchers, and it is far below the count a
# cwd-rooted watcher reaches from any real working directory.
watch_bound=64
# The launch directory holds this many subdirectories: well above the bound,
# so a cwd-rooted controller refuses here by a margin, and cheap to create.
launch_subdirectories=256

# Every wait is bounded and every bound names what it waited for (FG-231).
runtime_budget=30
process_budget=30
reap_budget_ms=15000
http_max_time=10

now_ms() {
  # bash renders EPOCHREALTIME with the locale's decimal separator; a comma
  # would become the arithmetic comma operator below and silently truncate.
  local t=${EPOCHREALTIME/,/.}
  printf '%s\n' "$(( ${t%.*} * 1000 + 10#${t#*.} / 1000 ))"
}

deadline_after() {
  printf '%s\n' "$(( $(now_ms) + $1 ))"
}

before_deadline() {
  (( $(now_ms) < $1 ))
}

# Run a command under a wall-clock budget in seconds. 124 means the budget
# expired and the command left on SIGTERM; 137 means it ignored SIGTERM and
# was killed after the 5 s grace. Both are the budget expiring.
bounded() {
  timeout -k 5 "$1" "${@:2}"
}

budget_expired() {
  (( $1 == 124 || $1 == 137 ))
}

# Reap a background child within a budget. On expiry the child is killed and
# reaped, the wait is named as a refusal, and 124 is returned.
wait_bounded() {
  local pid="$1"
  local budget_ms="$2"
  local label="$3"
  local deadline
  deadline=$(deadline_after "$budget_ms")
  while kill -0 "$pid" 2>/dev/null; do
    if ! before_deadline "$deadline"; then
      kill -KILL "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
      echo "FG-232 REFUSED: $label (pid $pid) did not exit within ${budget_ms} ms and was killed" >&2
      return 124
    fi
    sleep 0.05
  done
  wait "$pid" 2>/dev/null
}

# Any command that fails while errexit is active ends the proof with status 1
# and a line naming it, written to the proof's original stderr.
exec {diagnostic_fd}>&2
on_err() {
  local rc="$1"
  local line="$2"
  local command="$3"
  [[ $- == *e* ]] || return 0
  (( BASH_SUBSHELL == 0 )) || return 0
  local note=""
  local i
  budget_expired "$rc" && note=" (budget expired)"
  for (( i = 1; i < ${#FUNCNAME[@]} - 1; i++ )); do
    note+=" in ${FUNCNAME[i]} called from line ${BASH_LINENO[i]}"
  done
  echo "FG-232 REFUSED: line $line: \`$command\` exited $rc$note" >&"$diagnostic_fd"
  exit 1
}
trap 'on_err $? $LINENO "$BASH_COMMAND"' ERR

admin() {
  bounded "$runtime_budget" "$runtime" exec "$container" psql -U fogell -d "$1" -v ON_ERROR_STOP=1 "${@:2}"
}

release_controller() {
  [[ -n "$host_pid" ]] || return 0
  kill -TERM "$host_pid" 2>/dev/null || true
  wait_bounded "$host_pid" "$reap_budget_ms" "native controller" >/dev/null || true
  host_pid=""
}

release_stand_in() {
  [[ -n "$stand_in_pid" ]] || return 0
  kill -TERM "$stand_in_pid" 2>/dev/null || true
  wait_bounded "$stand_in_pid" "$reap_budget_ms" "inotify stand-in" >/dev/null || true
  stand_in_pid=""
}

cleanup() {
  release_stand_in
  release_controller
  bounded "$runtime_budget" "$runtime" exec "$container" psql -U fogell -d postgres -v ON_ERROR_STOP=1 \
    -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$database' AND pid <> pg_backend_pid()" \
    -c "DROP DATABASE IF EXISTS $database" >/dev/null 2>&1 || true
  bounded "$runtime_budget" "$runtime" exec "$container" psql -U fogell -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP OWNED BY $role" -c "DROP ROLE IF EXISTS $role" >/dev/null 2>&1 || true
  if [[ ${FOGELL_KEEP_FG232_PROOF:-0} = 1 ]]; then
    echo "FG-232 proof scratch retained: $scratch" >&2
  else
    case "$scratch" in
      /tmp/fogell-fg232-proof.*) rm -rf -- "$scratch" ;;
      *) echo "FG-232 REFUSED: unsafe cleanup path" >&2 ;;
    esac
  fi
}

mapping_rc=0
mapping=$(bounded "$runtime_budget" "$runtime" port "$container" 5432/tcp) || mapping_rc=$?
if budget_expired "$mapping_rc"; then
  echo "FG-232 REFUSED: $runtime did not report the PostgreSQL port within ${runtime_budget} s" >&2
  exit 2
elif (( mapping_rc != 0 )); then
  echo "FG-232 REFUSED: $runtime could not report the PostgreSQL port" >&2
  exit 2
fi
mapping_pattern='^(5432/tcp[[:space:]]+->[[:space:]]+)?127\.0\.0\.1:([0-9]+)$'
[[ "$mapping" =~ $mapping_pattern ]] \
  || { echo "FG-232 REFUSED: PostgreSQL has an unexpected port mapping: $mapping" >&2; exit 2; }
mapped_port=$((10#${BASH_REMATCH[2]}))
(( mapped_port == port_number )) \
  || { echo "FG-232 REFUSED: FOGELL_PG_PORT does not match the selected PostgreSQL container" >&2; exit 2; }
trap cleanup EXIT

controller="$repo/src/Fogell.Controller.Host/bin/$configuration/net10.0/Fogell.Controller.Host"
run_host="$repo/tools/Fogell.Run.Host/bin/$configuration/net10.0/Fogell.Run.Host"
[[ -x "$controller" ]] || { echo "FG-232 REFUSED: controller host is not built" >&2; exit 2; }
[[ -x "$run_host" ]] || { echo "FG-232 REFUSED: run host is not built" >&2; exit 2; }
[[ -d /proc/self/fdinfo ]] || { echo "FG-232 REFUSED: /proc/<pid>/fdinfo is required to count inotify watches" >&2; exit 2; }
command -v python3 >/dev/null || { echo "FG-232 REFUSED: python3 is required for the counter and the stand-in" >&2; exit 2; }
command -v rg >/dev/null || { echo "FG-232 REFUSED: rg (ripgrep) is required" >&2; exit 2; }
command -v curl >/dev/null || { echo "FG-232 REFUSED: curl is required" >&2; exit 2; }
# The inotify instances and watches a process holds: an instance is a
# descriptor whose /proc/<pid>/fd link reads `anon_inode:inotify` (so an
# instance holding no watches still counts), and its watches are the
# `inotify wd:` lines of the matching fdinfo entry. Prints
# "<instances> <watches>". A count that cannot be taken is a refusal naming
# why, never zero: the process is gone, its fd table cannot be listed or an
# entry cannot be read, or the table changed under every read.
inotify_footprint() {
  python3 - "$1" <<'PY'
import os
import sys

pid = sys.argv[1]
base = f"/proc/{pid}/fdinfo"
attempts = 5
for attempt in range(attempts):
    try:
        entries = os.listdir(base)
    except OSError as error:
        sys.exit(f"check unavailable: {base} cannot be listed: {error.strerror}")
    instances = 0
    watches = 0
    churned = False
    for entry in entries:
        link = f"/proc/{pid}/fd/{entry}"
        path = f"{base}/{entry}"
        try:
            target = os.readlink(link)
            with open(path) as fdinfo:
                lines = fdinfo.read().splitlines()
        except FileNotFoundError:
            # Closed between the listing and the read. Whether the number was
            # reused cannot be told from here, so the whole table is re-read.
            churned = True
            break
        except OSError as error:
            sys.exit(f"check unavailable: {link} cannot be read: {error.strerror}")
        if target != "anon_inode:inotify":
            continue
        instances += 1
        watches += sum(1 for line in lines if line.startswith("inotify wd:"))
    if not churned:
        print(f"{instances} {watches}")
        sys.exit(0)
sys.exit(f"check unavailable: the fd table of pid {pid} changed under each of {attempts} reads")
PY
}

# The verdict on one process: a pass needs a count that was taken and that is
# below the bound. Every other outcome is a refusal naming the process.
judge_footprint() {
  local pid="$1"
  local label="$2"
  local footprint
  local rc=0
  local instances
  local watches
  footprint=$(inotify_footprint "$pid" 2>"$scratch/footprint.stderr") || rc=$?
  if (( rc != 0 )); then
    echo "FG-232 REFUSED: $label: $(tr '\n' ' ' <"$scratch/footprint.stderr")" >&2
    return 1
  fi
  read -r instances watches <<<"$footprint"
  if (( watches >= watch_bound )); then
    echo "FG-232 REFUSED: $label holds $watches inotify watches across $instances instances; the bound is fewer than $watch_bound" >&2
    return 1
  fi
  echo "FG-232: $label holds $watches inotify watches across $instances instances (bound: fewer than $watch_bound)"
}

# A process that holds exactly N inotify watches, one per fresh directory, and
# then waits to be terminated. Publishes its count to the ready file only after
# every watch is held, so a judgement never races the setup.
start_stand_in() {
  local count="$1"
  local ready="$scratch/stand-in-$count.ready"
  local root="$scratch/stand-in-$count"
  mkdir -p "$root"
  python3 - "$count" "$root" "$ready" <<'PY' &
import ctypes
import os
import signal
import sys

count, root, ready = int(sys.argv[1]), sys.argv[2], sys.argv[3]
libc = ctypes.CDLL(None, use_errno=True)
libc.inotify_init1.argtypes = [ctypes.c_int]
libc.inotify_add_watch.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_uint32]
IN_CREATE = 0x00000100
fd = libc.inotify_init1(0)
if fd < 0:
    sys.exit(f"inotify_init1 failed: {os.strerror(ctypes.get_errno())}")
for index in range(count):
    directory = os.path.join(root, f"w{index}")
    os.mkdir(directory)
    if libc.inotify_add_watch(fd, directory.encode(), IN_CREATE) < 0:
        sys.exit(f"inotify_add_watch failed: {os.strerror(ctypes.get_errno())}")
with open(ready, "w") as handle:
    handle.write(str(count))
signal.pause()
PY
  stand_in_pid=$!
  local poll_deadline
  poll_deadline=$(deadline_after 10000)
  while before_deadline "$poll_deadline"; do
    [[ -s "$ready" ]] && return 0
    kill -0 "$stand_in_pid" 2>/dev/null \
      || { echo "FG-232 REFUSED: the $count-watch stand-in exited before it was ready" >&2; exit 1; }
    sleep 0.05
  done
  echo "FG-232 REFUSED: the $count-watch stand-in did not become ready within 10000 ms" >&2
  exit 1
}

# Arm 1: a process that cannot be read. The pid of a child this shell has
# already reaped names no process, and the verdict must say the check was
# unavailable rather than pass on a count of zero.
/bin/true &
absent_pid=$!
wait_bounded "$absent_pid" 1000 "absent-process stand-in"
verdict=$( (judge_footprint "$absent_pid" "absent process") 2>&1; echo "rc=$?" )
rg -q "^FG-232 REFUSED: absent process: check unavailable: /proc/$absent_pid/fdinfo cannot be listed: " <<<"$verdict" \
  && rg -q '^rc=1$' <<<"$verdict" \
  || { echo "FG-232 REFUSED: an unreadable process was not refused as unavailable: $verdict" >&2; exit 1; }
echo "FG-232 arm absent-process: $(rg -m 1 '^FG-232 REFUSED' <<<"$verdict")"

# Arm 2: a stand-in holding exactly the bound must be refused with its count.
start_stand_in "$watch_bound"
verdict=$( (judge_footprint "$stand_in_pid" "stand-in at the bound") 2>&1; echo "rc=$?" )
rg -q "^FG-232 REFUSED: stand-in at the bound holds $watch_bound inotify watches across 1 instances; the bound is fewer than $watch_bound\$" <<<"$verdict" \
  && rg -q '^rc=1$' <<<"$verdict" \
  || { echo "FG-232 REFUSED: a stand-in holding $watch_bound watches was not refused: $verdict" >&2; exit 1; }
release_stand_in
echo "FG-232 arm stand-in-at-bound: $(rg -m 1 '^FG-232 REFUSED' <<<"$verdict")"

# Arm 3: one below the bound passes, with the count named, so the counter is
# proven to count and the bound to be strict.
start_stand_in "$((watch_bound - 1))"
verdict=$( (judge_footprint "$stand_in_pid" "stand-in under the bound") 2>&1; echo "rc=$?" )
rg -q "^FG-232: stand-in under the bound holds $((watch_bound - 1)) inotify watches across 1 instances \\(bound: fewer than $watch_bound\\)\$" <<<"$verdict" \
  && rg -q '^rc=0$' <<<"$verdict" \
  || { echo "FG-232 REFUSED: a stand-in holding $((watch_bound - 1)) watches did not pass with its count named: $verdict" >&2; exit 1; }
release_stand_in
echo "FG-232 arm stand-in-under-bound: $(rg -m 1 '^FG-232: ' <<<"$verdict")"

# The live arm. The launch directory has more subdirectories than the bound,
# and the controller is started with that directory as its cwd, which the
# proof then reads back from /proc so the launch cannot silently have happened
# from somewhere small.
launch_dir="$scratch/launch"
mkdir -p "$launch_dir"
(cd "$launch_dir" && seq -f 'd%g' 1 "$launch_subdirectories" | xargs mkdir)
launch_dir_physical=$(cd "$launch_dir" && pwd -P)
subdirectories=$(find "$launch_dir" -mindepth 1 -type d | wc -l)
[[ "$subdirectories" = "$launch_subdirectories" ]] \
  || { echo "FG-232 REFUSED: launch directory holds $subdirectories subdirectories, expected $launch_subdirectories" >&2; exit 1; }

printf '%s' 'fg232-proof-token-0123456789abcdef' >"$token_file"
chmod 400 "$token_file"

admin postgres -c "CREATE DATABASE $database" >/dev/null
admin postgres -c "CREATE ROLE $role NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS" >/dev/null

maintenance_url="Host=127.0.0.1;Port=$port;Username=fogell;Password=fogell;Database=$database"
runtime_url="$maintenance_url;Options=-c role=$role;No Reset On Close=true;Maximum Pool Size=8"
common_env=(
  "FOGELL_DATABASE_URL=$runtime_url"
  "FOGELL_MAINTENANCE_DATABASE_URL=$maintenance_url"
  "FOGELL_API_TOKEN_FILE=$token_file"
  "FOGELL_LISTEN_URL=$base_url"
  "FOGELL_STATE_ROOT=$state_root"
  "FOGELL_RUN_HOST_PATH=$run_host"
  "FOGELL_LOCAL_TRUST_POOL=trusted-linux"
  "FOGELL_MAX_PIPELINE_BYTES=1024"
  "FOGELL_MAX_LOG_CHUNKS=100"
  "FOGELL_WORKER_POLL_MS=50"
  "FOGELL_WORKER_LEASE_SECONDS=60"
)

# Startup migrates before it checks the runtime capability, so this first run
# applies the schema and refuses with status 3 (FG-224's own sequence); the
# grants below then complete the capability.
set +e
bounded "$process_budget" env "${common_env[@]}" "$controller" >"$scratch/migrate.stdout" 2>"$scratch/migrate.stderr"
migrate_rc=$?
set -e
! budget_expired "$migrate_rc" \
  || { echo "FG-232 REFUSED: migration startup did not exit within ${process_budget} s" >&2; exit 1; }
[[ $migrate_rc -eq 3 ]] \
  || { echo "FG-232 REFUSED: migration startup exited $migrate_rc, expected the incomplete-capability refusal 3: $(tr '\n' ' ' <"$scratch/migrate.stderr")" >&2; exit 1; }
admin "$database" \
  -c "GRANT USAGE ON SCHEMA public TO $role" \
  -c "GRANT SELECT, UPDATE(singleton) ON controller_metadata TO $role" \
  -c "GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes, attempts, events, outbox, log_chunks, effect_checkpoints, retry_decisions, build_definitions TO $role" \
  -c "GRANT SELECT ON organization_work_roots TO $role" \
  -c "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $role" >/dev/null

# `env` replaces itself with the controller, so the background pid is the
# controller's own.
(cd "$launch_dir" && exec env "${common_env[@]}" "$controller") >"$host_log" 2>&1 &
host_pid=$!

live=0
poll_deadline=$(deadline_after 10000)
while before_deadline "$poll_deadline"; do
  if curl --max-time "$http_max_time" -fsS "$base_url/health/live" >/dev/null 2>&1; then
    live=1
    break
  fi
  kill -0 "$host_pid" 2>/dev/null \
    || { echo "FG-232 REFUSED: controller exited during startup: $(tail -n 3 "$host_log" | tr '\n' ' ')" >&2; exit 1; }
  sleep 0.05
done
[[ $live -eq 1 ]] || { echo "FG-232 REFUSED: controller never answered /health/live" >&2; exit 1; }

controller_cwd=$(readlink "/proc/$host_pid/cwd")
[[ "$controller_cwd" = "$launch_dir_physical" ]] \
  || { echo "FG-232 REFUSED: controller cwd is $controller_cwd, expected the $launch_subdirectories-subdirectory launch directory $launch_dir_physical" >&2; exit 1; }

judge_footprint "$host_pid" "controller started from a $launch_subdirectories-subdirectory tree"

kill -TERM "$host_pid"
set +e
wait_bounded "$host_pid" "$reap_budget_ms" "native controller after SIGTERM"
stop_rc=$?
set -e
host_pid=""
[[ $stop_rc -eq 0 ]] \
  || { echo "FG-232 REFUSED: native controller returned $stop_rc after SIGTERM" >&2; exit 1; }

# The host names its content root once it has started, on the console logger's
# own thread, so the log is read only after the controller has exited and the
# line cannot still be in flight. It must be the apphost's own directory, not
# the launch directory: that is the mechanism the count above bounds.
apphost_dir=$(dirname "$controller")
rg -q -F "Content root path: $apphost_dir" "$host_log" \
  || { echo "FG-232 REFUSED: controller did not report the apphost directory as its content root: $(rg -m 1 'Content root path' "$host_log" || echo 'no content root line')" >&2; exit 1; }
! rg -q -F "Content root path: $launch_dir" "$host_log" \
  || { echo "FG-232 REFUSED: controller rooted at its launch directory" >&2; exit 1; }

echo "FG-232 PROOF PASS: an unreadable process is refused as unavailable, a stand-in at the bound ($watch_bound) is refused and one under it passes with its count named, and the controller started from a $launch_subdirectories-subdirectory tree holds fewer than $watch_bound inotify watches and exits on SIGTERM"
