# Controller host runbook

`Fogell.Controller.Host` is Fogell's runnable single-node controller. It must be
started with separate runtime and maintenance database capabilities. Startup
refuses before binding when configuration, migration, or role validation fails.

## Database roles

The runtime identity must be `NOSUPERUSER NOBYPASSRLS` and distinct from the
maintenance identity. Grant only the runtime surface:

```sql
GRANT USAGE ON SCHEMA public TO fogell_runtime;
GRANT SELECT, UPDATE(singleton) ON controller_metadata TO fogell_runtime;
GRANT SELECT ON organization_work_roots TO fogell_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON
  organizations, projects, builds, nodes, attempts, events, outbox, log_chunks,
  effect_checkpoints, retry_decisions, build_definitions
TO fogell_runtime;
GRANT USAGE, SELECT ON
  events_id_seq, outbox_id_seq, log_chunks_id_seq,
  effect_checkpoints_uncertain_seq TO fogell_runtime;
```

The maintenance identity applies checksum-pinned migrations during startup and
must not be used for requests or worker operations.

Before migration, startup proves both connection capabilities reach the same live
PostgreSQL database. The maintenance side takes a fresh random transaction-scoped
advisory lock, then runtime probes the same key inside an overlapping transaction.
The overlap pins distinct backends through transaction-pooling proxies; every
unwind releases the locks automatically. This active challenge deliberately does
not compare connection strings, host names, database names, or role/schema
metadata: aliases and proxies can make equal strings differ, while a separately
migrated or cloned database can make metadata look equal. Connection and command
waits are capped at five seconds for this proof. Any connection, transaction,
query, or cleanup uncertainty refuses startup without applying migrations.

## Required configuration

```text
FOGELL_DATABASE_URL                 runtime Npgsql connection string
FOGELL_MAINTENANCE_DATABASE_URL     distinct migration connection string
FOGELL_API_TOKEN_FILE               absolute service-owned regular file, mode 0400/0600, >=32 decoded UTF-16 code units, <=4096 bytes
FOGELL_LISTEN_URL                   HTTPS, or loopback HTTP for local operation
FOGELL_STATE_ROOT                   absolute durable controller-state directory, creatable/writable by the service identity
FOGELL_RUN_HOST_PATH                built Fogell.Run.Host executable by the effective service identity
FOGELL_LOCAL_TRUST_POOL             controller-owned placement pool
FOGELL_MAX_PIPELINE_BYTES           1024..16777216
FOGELL_MAX_LOG_CHUNKS               1..10000 returned per request
FOGELL_WORKER_POLL_MS               25..60000, and no more than one third of the lease in ms
FOGELL_WORKER_LEASE_SECONDS         10..3600
```

Every setting in this list is required, and a whitespace-only value is treated
as missing. In particular, `FOGELL_LOCAL_TRUST_POOL` must name a nonblank pool;
startup refuses a blank value before the controller can bind or admit work.

Two further settings are optional (FG-026b) and enable the registered
external-effect producer:

```text
FOGELL_EFFECT_FILE_DROP_ROOT        absolute, existing, readable+writable directory, not itself a symlink (opened O_NOFOLLOW; a link is ELOOP), physically disjoint from FOGELL_STATE_ROOT by device/inode ancestry (ancestors need only search permission), containing an operator-created `.fogell-drop-root` marker that is a readable regular file with one link of at most 4096 bytes (empty is fine); startup performs the runtime opens and the full receipt write sequence in a `.fogell-probe-<guid>` directory it creates and removes in the root, and refuses by name; absent = no producer enabled
FOGELL_EFFECT_KILL_AT               prepare | invoke | apply | confirm; crash-window proof only, refused without the drop root
```

With the drop root set, every attempt that reaches a natural terminal writes one
receipt `<root>/<organization>/<attempt>.receipt` through the FG-026 ledger
(prepare, invoke, applied, confirmed) before its terminal status is published;
a refused or uncertain effect fails the attempt closed into
`reconciliation_required` (reason `effect_dispatch_unconfirmed`). The marker
pins the destination: the controller creates only the per-organization
subdirectory, never the root, and if the marker is gone (an unmounted or
replaced volume) the effect is refused before preparation or left uncertain
after it rather than written to whatever directory took the root's place.
Stale prepared/applied ledger rows are classified as uncertain on an
independent cadence of one lease period (`FOGELL_WORKER_LEASE_SECONDS`) that
does not wait on claim execution, on every worker scan, and at startup; each
classification publishes one `effect.uncertain` event and outbox row and never
re-invokes. `GET /api/v1/organizations/{org}/effects/uncertain?limit=N&cursor=C`
lists them read-only in pages (`limit` 1..1000, default 200; `next_cursor` in
the response continues the listing and is bound to the organization and to the
restore epoch: after a database restore every cursor issued before it is
refused with 400 `invalid_cursor`, "cursor predates a database restore; restart
the listing" — restart from the first page). The kill
hook exists only for `scripts/prove-fg026b-effect-dispatch.sh` and must never
be set on a service.

The maintenance identity that performs a restore (`ActivateRestore`) needs
`SELECT, UPDATE` on `effect_checkpoints` and `USAGE` on the classification
sequence `effect_checkpoints_uncertain_seq` in addition to its attempt, node,
build, event and outbox grants: a restore now classifies pre-restore
prepared/applied effects in the same transaction as the epoch bump.

The controller currently has no authenticated approval broker. Fresh admission
therefore rejects an `input` step unless a usable explicit, inherited stage, or
pipeline timeout provably bounds it. The refusal is `execution_unsupported` with
reason `unsupported_input_approval` and occurs before binding the build or its
idempotency key; exact legacy admissions still replay. Run.Host's optional
filesystem inbox remains available only to trusted standalone orchestration. Do
not expose that directory through the controller: build code shares the runner's
OS identity and could otherwise forge its own decision.

The worker targets renewal after one third of each lease. Keep the poll interval
at or below that same one-third boundary: if a control check falls immediately
before the renewal target, the following check still occurs by two thirds of the
lease, reserving the final third for scheduling delay and the fenced database
round trip. Startup rejects an unsafe pair and reports the maximum poll interval
for the configured lease.

Keep the token file and both database strings out of command arguments and logs.
Startup opens the token once with no-follow and nonblocking Linux flags, then
requires the opened object to be a regular file owned by the effective service
identity with exact mode `0400` or `0600` and at most 4096 bytes. Metadata and
token bytes come from that same descriptor, so replacing the pathname after it
opens cannot substitute another file. Startup removes only trailing CR/LF
characters, then requires at least 32 remaining characters and rejects any
leading or trailing whitespace. Every open, metadata, size, decode, or content
failure aborts startup with a named configuration refusal.

The process enforces the same final-component metadata rules as the shell checks
below. This deliberately narrower portable-client check remains a useful
deployment preflight before starting:

```bash
if [[ ! -f "$FOGELL_API_TOKEN_FILE" || -L "$FOGELL_API_TOKEN_FILE" ]]; then
  printf 'token path must be a regular non-symlink file\n' >&2
  exit 1
fi
if [[ "$(stat -c '%u' "$FOGELL_API_TOKEN_FILE")" != "$(id -u)" ]]; then
  printf 'token file must be owned by the service identity\n' >&2
  exit 1
fi
case "$(stat -c '%a' "$FOGELL_API_TOKEN_FILE")" in
  400|600) ;;
  *) printf 'token file mode must be 0400 or 0600\n' >&2; exit 1 ;;
esac

# The documented curl workflow deliberately supports a narrower portable token
# format than startup: one line of visible ASCII, at least 32 characters, with
# only optional trailing CR/LF bytes. Keep the file unchanged while the
# controller is running so this client credential matches the value in memory.
python3 - "$FOGELL_API_TOKEN_FILE" <<'PY'
import pathlib
import sys

raw = pathlib.Path(sys.argv[1]).read_bytes()
token = raw.rstrip(b"\r\n")
if len(raw) > 4096 or len(token) < 32 or any(byte < 0x21 or byte > 0x7e for byte in token):
    raise SystemExit(
        "token must be one visible-ASCII line of at least 32 characters in a file no larger than 4096 bytes"
    )
PY
```

The runtime state root holds immutable definitions, journals, event frames,
identity-bound inner process-group registry records, workspaces, a neutral child
home, private job/build-scoped agent homes, archived build artifacts, and temporary
files. The configured
root is validated as absolute before normalization; relative input is refused
rather than resolved against the controller's current directory. Before startup
continues, the controller creates a uniquely named probe without replacement,
writes and durably flushes it, and removes it. A root that cannot complete that
cycle is refused before readiness; existing operator data is never used as the
probe target. Runtime readiness repeats that exact effective-identity probe
without creating a missing root. Readiness and worker claim admission each use
a lock-protected cache of that probe, with results retained for at most one
second to bound durable flushes under concurrent health checks and the worker's
minimum 25 ms idle poll. The configured Run.Host path is likewise
checked against the Linux kernel using the service's effective identity, so
unrelated execute permission bits do not authorize startup. The outer
process-group launcher is fixed
to `/usr/bin/setsid`; startup refuses before binding unless that exact file is
executable by the service identity. Readiness and the worker claim boundary
recheck both executables and the state root, so later removal returns 503 and
cannot start new work. A cached state-root success may permit an offer, but the
worker checks the launchers first and performs the fresh state-root probe only
when they are available. Either dependency loss calls the same immediate fenced
requeue before logging. The still-unstarted attempt, node, and build return to
`queued`; the lease owner and expiry are cleared; and no event or outbox row is
published. Requeue requires the current owner, fence, unexpired lease, and
restore epoch through the attempt → node → build lock order; a replacement claim
advances the fence. A concurrent cancellation request remains set for the
replacement to observe before launch. Neither loss path materializes work,
begins execution, or enters reconciliation. The worker never recreates a missing
root.
For a hard aggregate bound on workspace, stash, artifact, and runner scratch
writes, configure the opt-in [bounded storage pool](storage-pool.md). It requires
a separate operator-provisioned filesystem at `STATE_ROOT/workspaces`, finite
byte and inode limits, all five `FOGELL_STORAGE_POOL_*` settings, and initialized
pool markers. Ordinary directories and missing mounts fail closed. This mode
admits one local writer; a dirty crash marker requires explicit reconciliation.
The default without these settings remains unmanaged workspace storage.

Put the state root on storage whose loss/recovery policy matches the PostgreSQL
database; an incomplete journal is a reconciliation
event, not permission to guess success.

## Start and observe

From the repository root, build the exact locked dependency graph:

```bash
dotnet restore --locked-mode
dotnet build -c Release --no-restore
```

If the restore fails with `NU1403` for `FSharp.Core.10.1.301` on a machine that
restored Fogell before 2026-09-02, evict the package the SDK's library-pack
source left in the global folder and restore again (FG-237; the lock now
records the nuget.org package, and the two copies cannot share one folder):

```bash
rm -rf ~/.nuget/packages/fsharp.core/10.1.301
```

Export the required variables above, then start the controller:

```bash
src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host
```

The directory the controller is started from is not an input. Its content
root is the apphost's own directory and configuration is read once at
startup, so the process holds no inotify watches (FG-232). Earlier builds
rooted at the working directory and watched it recursively for configuration
reloads: started from a large home directory, one controller held nearly all
of the user's `fs.inotify.max_user_watches`, and every other file watcher for
that user then failed. `scripts/prove-fg232-controller-inotify.sh` starts the
built controller from a 256-subdirectory tree and requires fewer than 64
watches.

Use `/health/live` for HTTP-process liveness; it remains 200 when dependencies
are unavailable. `/health/ready` lazily checks database reachability, runtime
database capabilities, both execution launchers, and then state-root
availability in that order, returning 503 at the first failure. Do not send
traffic until readiness returns 200. The one-second readiness cache is a bounded
point-in-time observation, not a transactional guarantee against storage loss
immediately after a check. Before an
engine-created inner `setsid` step may execute user code, Run.Host records its
pid and Linux start ticks and observes the same process stopped. An EOF watchdog
bound to Run.Host liveness and the controller's outer-plus-registered-inner
cleanup then provide two bounded descendant-reaping paths, including when the
inner leader has already exited.

Every managed cleanup path now re-observes the recorded leader or stopped anchor
start ticks and process-group membership before each TERM, CONT, or KILL. A
same-number group without either recorded identity is not signal authority; the
pre-`setsid` launcher publishes its own PID and start ticks before it may launch
user code, and a missing or mismatched handshake refuses without numeric signals;
survivor scans exclude the stopped anchor only when that scan observes its
recorded PID and start ticks, so PID reuse cannot hide a live group member. The
classified reconciliation cause is persisted before every fallible diagnostic,
including cleanup and definition-materialization failure, so a logging provider
cannot strand the attempt or replace its operational cause with a generic
fallback. The same ordering covers final-drain stream-change and incomplete-
frame causes. Final one-thread Z/X/x remnants are inert but do not themselves prove
extinction: while their numeric group exists it remains joinable. A zombie
leader with live sibling threads remains active; the generated POSIX watchdog
reads the proc thread-count field with the required braced `${18}` expansion.
Run.Host and Controller.Host establish Linux child-subreaper ownership, reap
adopted members of the registered group, and serialize parent, group, and
inert-state revalidation with each PID-specific waitpid. An adopted registered
leader is eligible after its wrapper owner is gone; Controller.Host reserves
only its outer Run.Host leader while the dedicated `Process` reaper owns it. Anchor reaping
also matches its recorded start ticks, so another step's reused or still-shell-
owned PID cannot be harvested. The durable record is disarmed/deleted only after
the kernel reports ESRCH for that group.
If Controller.Host cannot capture a launched outer process's Linux birth
identity, it performs memoized identity-bound inner cleanup, records
`outer_identity_unbound`, and only then emits diagnostics. It never signals the
unbound numeric PID, and a logging failure cannot precede the durable cause.
A zombie leader with more than one thread remains active, and unreadable or
malformed `/proc` state remains uncertain. If `getpgid` first identifies a
candidate but its following stat read reports a different group, cleanup also
remains uncertain rather than accepting that PID-reuse race as extinction. The
foreign-candidate boundary recheck narrows observation races but is not the
certificate; kernel group disappearance is. The production proof supports both a native
Linux service and a controller running as container PID 1. Its container lane
overrides the image
entrypoint and verifies `/proc/1/exe` is the exact controller apphost before it
accepts any HTTP result. It also admits a real long-running nested step, maps
the step shell and its sleep child's namespace pids to host-visible processes,
then runs a complementary boundary beneath a surviving container init: only the
controller is killed, and Run.Host, the shell, and the sleep child must disappear
while init remains alive and the post-sleep effect remains absent. Run.Host is
bound to a private controller-owned stdin liveness pipe: controller exit closes
the writer regardless of which native thread launched the child, and EOF fails
Run.Host closed so its per-step watchdog pipes also close. This deliberately
avoids the creating-thread semantics of Linux `PR_SET_PDEATHSIG` and works when
the controller is PID 1.

This is a trusted single-tenant Linux boundary. The fresh `/proc` observation
and following `kill(2)` are not one atomic pidfd/cgroup operation, so a hostile
same-UID actor deliberately racing process replacement is outside the claim.
Do not use this local worker as a multi-tenant OS sandbox. Such deployments need
stronger isolation (for example delegated cgroups/pidfds or separate VMs), not an
operator override of `reconciliation_required`.

### Submit and follow one build

The current slice does not provision organizations or projects over HTTP.
Generate the non-secret identities in the calling shell, then load the
maintenance connection into libpq's `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`,
and `PGPASSWORD` variables from the deployment secret store **inside** the
fail-fast subshell below. Values loaded there disappear when seeding ends. The
later client subshell also clears any libpq variables and both controller
database URLs inherited from the calling shell before it starts a child process.
This is a maintenance-plane action; the controller still uses only its
restricted runtime identity for requests and worker operations. The seed block
requires the PostgreSQL `psql` client.

```bash
FOGELL_ORGANIZATION_ID=$(cat /proc/sys/kernel/random/uuid)
FOGELL_PROJECT_ID=$(cat /proc/sys/kernel/random/uuid)
export FOGELL_ORGANIZATION_ID FOGELL_PROJECT_ID
export FOGELL_ORGANIZATION_SLUG="hello-$FOGELL_ORGANIZATION_ID"
export FOGELL_PROJECT_SLUG="hello-$FOGELL_PROJECT_ID"

(
set -euo pipefail

for FOGELL_PG_VARIABLE in "${!PG@}"; do
  [[ -n "$FOGELL_PG_VARIABLE" ]] || continue
  unset "$FOGELL_PG_VARIABLE"
done
unset FOGELL_PG_VARIABLE
# Load PGHOST, PGPORT, PGDATABASE, PGUSER, and PGPASSWORD from the deployment
# secret store here, inside this subshell. Do not set them in the calling shell.
: "${PGHOST:?load PGHOST inside the maintenance subshell}"
: "${PGPORT:?load PGPORT inside the maintenance subshell}"
: "${PGDATABASE:?load PGDATABASE inside the maintenance subshell}"
: "${PGUSER:?load PGUSER inside the maintenance subshell}"
: "${PGPASSWORD:?load PGPASSWORD inside the maintenance subshell}"
export PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD

psql -X -v ON_ERROR_STOP=1 \
  --set=organization_id="$FOGELL_ORGANIZATION_ID" \
  --set=project_id="$FOGELL_PROJECT_ID" \
  --set=organization_slug="$FOGELL_ORGANIZATION_SLUG" \
  --set=project_slug="$FOGELL_PROJECT_SLUG" <<'SQL'
BEGIN;
SELECT set_config('fogell.organization_id', :'organization_id', true);
INSERT INTO organizations (id, slug)
VALUES (:'organization_id'::uuid, :'organization_slug');
INSERT INTO projects (id, organization_id, slug)
VALUES (:'project_id'::uuid, :'organization_id'::uuid, :'project_slug');
COMMIT;
SQL
)
```

This Bash session requires `curl`, `jq`, and `python3`. It keeps the bearer token
out of `curl`'s argument list by building a mode-`0600` header file inside one
private scratch directory. The Python step enforces the portable token contract
above and copies the exact accepted token bytes into the header. The trap
removes both temporary files on success, error, or interruption:

```bash
(
set -euo pipefail

# Do not pass an ambient maintenance capability to client-only child processes.
for FOGELL_PG_VARIABLE in "${!PG@}"; do
  [[ -n "$FOGELL_PG_VARIABLE" ]] || continue
  unset "$FOGELL_PG_VARIABLE"
done
unset FOGELL_PG_VARIABLE
unset FOGELL_DATABASE_URL FOGELL_MAINTENANCE_DATABASE_URL

# The client origin may differ from a wildcard/proxied FOGELL_LISTEN_URL.
FOGELL_CLIENT_URL=${FOGELL_CLIENT_URL:-http://127.0.0.1:8080}
export FOGELL_CLIENT_URL
FOGELL_IDEMPOTENCY_KEY=$(cat /proc/sys/kernel/random/uuid)
export FOGELL_IDEMPOTENCY_KEY
export FOGELL_WAIT_SECONDS=300
export FOGELL_CONNECT_TIMEOUT_SECONDS=5
export FOGELL_HTTP_TIMEOUT_SECONDS=15
curl_common=(
  --fail-with-body -sS
  --connect-timeout "$FOGELL_CONNECT_TIMEOUT_SECONDS"
  --max-time "$FOGELL_HTTP_TIMEOUT_SECONDS"
)

umask 077
FOGELL_QUICKSTART_DIR=$(mktemp -d /tmp/fogell-controller-quickstart.XXXXXX)
cleanup_fogell_quickstart() {
  case "$FOGELL_QUICKSTART_DIR" in
    /tmp/fogell-controller-quickstart.*) rm -rf -- "$FOGELL_QUICKSTART_DIR" ;;
    *) printf 'refusing unsafe cleanup path: %s\n' "$FOGELL_QUICKSTART_DIR" >&2 ;;
  esac
}
trap cleanup_fogell_quickstart EXIT
trap 'exit 130' HUP INT TERM
FOGELL_AUTH_HEADER="$FOGELL_QUICKSTART_DIR/auth-header"
FOGELL_PIPELINE_FILE="$FOGELL_QUICKSTART_DIR/Jenkinsfile"
python3 - "$FOGELL_API_TOKEN_FILE" "$FOGELL_AUTH_HEADER" <<'PY'
import os
import pathlib
import sys

raw = pathlib.Path(sys.argv[1]).read_bytes()
token = raw.rstrip(b"\r\n")
if len(token) < 32 or any(byte < 0x21 or byte > 0x7e for byte in token):
    raise SystemExit(
        "token must be one visible-ASCII line of at least 32 characters"
    )

fd = os.open(sys.argv[2], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, "wb") as header:
    header.write(b"Authorization: Bearer " + token + b"\n")
PY

cat >"$FOGELL_PIPELINE_FILE" <<'JENKINSFILE'
pipeline {
  agent any
  stages {
    stage('hello') {
      steps {
        echo 'hello from Fogell'
        sh 'mkdir -p dist; printf "hello artifact\\n" > dist/output.bin'
        archiveArtifacts artifacts: 'dist/output.bin'
      }
    }
  }
}
JENKINSFILE

curl "${curl_common[@]}" "$FOGELL_CLIENT_URL/health/ready"

FOGELL_BUILDS_URL="$FOGELL_CLIENT_URL/api/v1/organizations/$FOGELL_ORGANIZATION_ID/projects/$FOGELL_PROJECT_ID/builds"
submission=$(
  curl "${curl_common[@]}" -X POST \
    --header "@$FOGELL_AUTH_HEADER" \
    -H "Idempotency-Key: $FOGELL_IDEMPOTENCY_KEY" \
    -H 'Content-Type: application/x-jenkinsfile' \
    --data-binary "@$FOGELL_PIPELINE_FILE" \
    "$FOGELL_BUILDS_URL"
)
printf '%s\n' "$submission" | jq .
FOGELL_BUILD_ID=$(printf '%s\n' "$submission" | jq -er .build_id)
FOGELL_ATTEMPT_ID=$(printf '%s\n' "$submission" | jq -er .attempt_id)

deadline=$((SECONDS + FOGELL_WAIT_SECONDS))
while true; do
  status_json=$(curl "${curl_common[@]}" --header "@$FOGELL_AUTH_HEADER" \
    "$FOGELL_BUILDS_URL/$FOGELL_BUILD_ID")
  status=$(printf '%s\n' "$status_json" | jq -er .status)
  printf 'status=%s\n' "$status"
  case "$status" in
    queued|running) sleep 1 ;;
    success) break ;;
    unstable|failure|aborted|reconciliation_required) printf '%s\n' "$status_json" | jq .; exit 1 ;;
    *) printf 'unexpected status: %s\n' "$status" >&2; exit 1 ;;
  esac

  if (( SECONDS >= deadline )); then
    printf 'timed out after %s seconds; last status:\n' "$FOGELL_WAIT_SECONDS" >&2
    printf '%s\n' "$status_json" | jq . >&2
    curl "${curl_common[@]}" "$FOGELL_CLIENT_URL/health/ready" >&2 || true
    exit 1
  fi
done

curl "${curl_common[@]}" --header "@$FOGELL_AUTH_HEADER" \
  "$FOGELL_BUILDS_URL/$FOGELL_BUILD_ID/logs?from=0" \
  | jq -r '.chunks[].body'

# For a path published by archiveArtifacts, preserve the path segments exactly.
# The response is application/octet-stream and is not a directory-listing API.
FOGELL_ARTIFACT_PATH=dist/output.bin
curl "${curl_common[@]}" --header "@$FOGELL_AUTH_HEADER" \
  --output "$FOGELL_QUICKSTART_DIR/output.bin" \
  "$FOGELL_BUILDS_URL/$FOGELL_BUILD_ID/attempts/$FOGELL_ATTEMPT_ID/artifacts/$FOGELL_ARTIFACT_PATH"

cleanup_fogell_quickstart
trap - EXIT HUP INT TERM
)
```

A fresh idempotency key returns HTTP 201. Replaying the identical source and key
returns 200 with the same build identity; changing the source under that key
returns 409. Placement is controller policy: a request carrying
`Fogell-Trust-Pool` is refused. Log responses include `next_sequence`; use that
value as the next `?from=` cursor when tailing a long-running build.

The local worker publishes logs in atomic batches of at most 128 frames and
1 MiB of decoded UTF-8 text. Each parsing slice reads at most 256 KiB, retaining
at most one 1 MiB encoded partial frame. Malformed base64 or UTF-8 becomes a
fixed diagnostic. A refused transaction advances neither the durable build
cursor nor the worker's committed frame cursor.

While a backlog remains, the worker yields between batches and checks control
without waiting for the idle poll interval. It rechecks cancellation and lease
authority between transactions; a slow database operation can still delay that
check until the operation returns. After producer extinction, it drains the
complete frozen boundary before terminal publication, with the same bounded
batches and control checks. These limits bound each publication transaction;
they are not total log-storage quotas.

A graceful or ungraceful forced stop still moves started work to
`reconciliation_required`: proving every process extinct does not prove whether
an external effect or journal write completed. Shutdown cancellation interrupts
the active worker poll promptly, including when it is configured for 60 seconds,
then follows the ordinary cleanup path and records reason `controller_shutdown`.
It never requeues a started
execution automatically. An expired never-started offer may be queued again,
while an expired `accepted`, `running`, `finalizing`, or `cancelling` lease
enters reconciliation. Each ambiguous expiry atomically moves attempt, node,
and build and publishes one `attempt.reconciliation_required` event plus one
`build.reconciliation_required` outbox row with reason `lease_expired`. The safe
pre-launch `offered` → `queued` transition emits neither record.

`BeginExecution` is the durable running transition, but the child still has not
started while the worker obtains the next log sequence and prepares event and
process-start state. A failure inside that known-unstarted setup boundary makes
one immediate fenced requeue before diagnostics, returning attempt, node, and
build to `queued` without a reconciliation event or outbox row. Once
`Process.Start` has been attempted, a false return or exception is treated as
ambiguous and retains reasoned `launcher_failed` reconciliation; the worker does
not convert an attempted launch into the safe setup-only requeue path.

After a natural leader exit, verified process extinction, and a complete
terminal event drain, the final control refresh may observe a cancellation that
raced with completion. The worker delegates that race to `PublishTerminal`: if
the cancellation committed first, the Store atomically publishes `aborted`; if
terminal publication committed first, the later cancellation reports the
existing terminal result. Shutdown, lease loss, or an incomplete terminal drain
remain reconciliation conditions and never enter this natural-exit arbitration.

### Artifact publishing limits and cleanup

Set these optional environment variables on the controller before startup.
It validates them and forwards the policy to each runner; pipeline environment
bindings cannot override it.
They use strict positive decimal values; the file limit cannot exceed the total
limit. If a setting is absent, the controller uses these defaults:

| Variable | Default | Applies to |
| --- | ---: | --- |
| `FOGELL_ARTIFACT_MAX_FILE_BYTES` | `268435456` (256 MiB) | One published file |
| `FOGELL_ARTIFACT_MAX_TOTAL_BYTES` | `1073741824` (1 GiB) | One build attempt's retained files plus active temporary bytes |
| `FOGELL_ARTIFACT_MAX_FILES` | `10000` | Published files in one attempt |
| `FOGELL_ARTIFACT_MAX_SCAN_ENTRIES` | `100000` | Filesystem entries, compiled globs, and glob evaluations; each bounded independently per scan |

The total budget belongs to the build and is shared by every archive call,
including parallel steps. A completed file remains retained for the current
attempt, so a later call charges against it. Replacing a file needs headroom
for both the old retained bytes and the new staged bytes until the replacement
is committed. Cancellation removes the active `.part` file; completed files
remain available. Empty private sidecar directories and lock files may remain.

Mutable files live under `_artifacts/<build-id>` while an attempt runs. Retry
preparation first freezes the old directory under
`_artifact-snapshots/<attempt-id>` and gives the new attempt an empty mutable
directory. Finalization removes crash leftovers through a bounded, nonblocking
pending cleanup; a busy lock fails finalization closed so reconciliation can
retry it. Historical snapshots remain immutable and are outside the new
attempt's admission budget. These settings scope artifact publishing only:
they do not impose retention limits on build history, the global workspace, or
other workspace and stash data.

Standalone persisted runs keep artifacts under
`_artifacts/<job-name>/build@<build-number>`. Different build numbers have separate
budgets; resuming the same build number continues to charge its retained files.
Controller-managed runs retain the UUID staging and attempt snapshot layout above.
Workspace freshness and a new journal do not mint a new persisted build identity;
use a new build number for a new standalone build.

The fresh single-run and sequence library APIs replace the job workspace and
require exclusive use of that job. They move the previous artifact namespace
under `_artifacts/.fogell-artifact-history` before the new run, so earlier files
remain preserved without consuming the new run's artifact budget. This history
is outside per-attempt limits and requires the same separate retention policy
as older snapshots.

## Acceptance and recovery checks

From HeMan, with the PostgreSQL container and host port selected:

```bash
FOGELL_PG_CONTAINER=fogell-fg060a \
FOGELL_PG_PORT=55445 \
FOGELL_BUILD_CONFIGURATION=Release \
./scripts/prove-runnable-controller.sh

FOGELL_PG_CONTAINER=fogell-fg060a \
FOGELL_PG_PORT=55445 \
FOGELL_BUILD_CONFIGURATION=Release \
FOGELL_FG224_CONTROLLER_IMAGE=mcr.microsoft.com/dotnet/sdk@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 \
./scripts/prove-runnable-controller.sh
```

Expected final line begins `FG-224/FG-042b PROOF PASS`. The script owns a uniquely named
scratch database, role, state directory, and listener, and removes them on exit.
Each shell invocation limits its retained stdout and stderr, individual framed
records, and asynchronous callback backlog to 16,777,216 UTF-16 code units each
(32 MiB of character storage before object/provenance overhead). The callback
backlog also has a 1,024-entry limit. Normal shell output includes normalized
line terminators in the capture budget; `returnStdout` counts the original text.
These are fixed limits, including when stdout is captured instead of echoed.
Crossing a limit stops the process group and fails the run explicitly; a
truncated capture is never returned as a successful shell result. Already
published log chunks remain available, but the rejected record may be absent.

Persisted runner exceptions publish a bounded `runner-failure` log event with
an engine-owned cause code: `ARTIFACT_LIMIT_EXCEEDED`, `BUILD_OUTPUT_LIMIT_EXCEEDED`,
`OUTPUT_LIMIT_EXCEEDED`, `RUNNER_OUT_OF_MEMORY`,
`RUNNER_ACCESS_DENIED`, `RUNNER_IO_ERROR`, or `RUNNER_INTERNAL_ERROR`. The host
also publishes a fixed `RUN_FAILED` fallback before recording terminal failure.
Exception messages and inherited runner stderr are not copied into public logs;
they can contain credentials and arbitrary process data. A failure to publish
the diagnostic retains the same reconciliation behavior as other event writes.
These events classify caught runner failures; they cannot diagnose an abrupt
kernel kill or guarantee that a process already out of memory can allocate a
diagnostic.

A build also shares a cumulative budget of 33,554,432 UTF-16 code units and
100,000 logical output records across all steps and parallel branches. Console
records charge masked text, timestamp prefixes, and normalized line terminators.
Decoded console chunks reserve shared capacity before secret matching, covering
both an unfinished secret candidate and an unfinished line. Matching reconciles
that reservation with its retained prefix and emitted text; completing a record
transfers its credit into the final charge, so parallel unfinished streams cannot
each consume a separate allowance. Discarding buffered
text releases its temporary reservation, without refunding retained records.
`returnStdout` charges raw decoded chunks before retaining them, even when the
script never prints the result. Captures consume characters without creating
console records. Positive growth from later secret masking consumes the same
budget; shortening or discarding a capture does not refund it.
When a bounded reader wait expires, returning the step closes output admission.
Late escaped-writer bytes cannot charge a later step; closing a stream does not
count as EOF for secret masking.

Crossing either build limit prevents further step execution and interrupts
running shell siblings even when `failFast` is disabled. All parallel branches
are joined before the failure escapes. Admitted safe output drains before the
fixed `BUILD_OUTPUT_LIMIT_EXCEEDED` diagnostic; a suffix that cannot be safely
remasked within the budget is withheld. A publication failure still takes
precedence and requires reconciliation. Each new run starts with a fresh budget.

These are output-retention limits, not an exact resident-memory ceiling. Object
and masking provenance overhead, transient copies, arbitrary script data,
workspace files, and stored artifacts require separate controls. Keep a
worker/container memory limit; this is not a multi-tenant isolation guarantee.

If progressive event publication fails, Run.Host emits no terminal journal
record; the failure remains infrastructure truth and the controller requires
reconciliation instead of inventing a build failure. A local-worker
`RequireReconciliation` transition atomically records its stable reason in an
`attempt.reconciliation_required` event and a `build.reconciliation_required`
outbox row, and preserves the fence-specific event file. That worker-selected
transition requires the exact owner and fence, an unexpired lease, and the
current restore epoch. Once a lease expires, the lease scanner owns disposition:
an unstarted `offered` attempt returns to `queued` without publication, while a
started attempt publishes reason `lease_expired`. A pre-restore worker likewise
publishes nothing; restore recovery owns that stale attempt. Inspect those
records, the durable journal, retained event frames, and effect checkpoints
before deciding recovery. Invalid queued definitions have their own reason event;
their attempt → node → build quarantine and exactly one reasoned reconciliation
event/outbox pair commit atomically, so a notification never describes a state
change that did not commit. Lease-expiry and restore transitions remain
distinguishable by their reason and restore-epoch records. Never repair any of
them by editing immutable
`build_definitions` or force a terminal status directly.
Both bypass the evidence the controller uses to refuse duplicate or substituted
work.
