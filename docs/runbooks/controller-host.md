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
  events_id_seq, outbox_id_seq, log_chunks_id_seq TO fogell_runtime;
```

The maintenance identity applies checksum-pinned migrations during startup and
must not be used for requests or worker operations.

## Required configuration

```text
FOGELL_DATABASE_URL                 runtime Npgsql connection string
FOGELL_MAINTENANCE_DATABASE_URL     distinct migration connection string
FOGELL_API_TOKEN_FILE               absolute/readable token file, >=32 characters
FOGELL_LISTEN_URL                   HTTPS, or loopback HTTP for local operation
FOGELL_STATE_ROOT                   absolute durable controller-state directory, creatable/writable by the service identity
FOGELL_RUN_HOST_PATH                built Fogell.Run.Host executable by the effective service identity
FOGELL_LOCAL_TRUST_POOL             controller-owned placement pool
FOGELL_MAX_PIPELINE_BYTES           1024..16777216
FOGELL_MAX_LOG_CHUNKS               1..10000 returned per request
FOGELL_WORKER_POLL_MS               25..60000, and no more than one third of the lease in ms
FOGELL_WORKER_LEASE_SECONDS         10..3600
```

The worker targets renewal after one third of each lease. Keep the poll interval
at or below that same one-third boundary: if a control check falls immediately
before the renewal target, the following check still occurs by two thirds of the
lease, reserving the final third for scheduling delay and the fenced database
round trip. Startup rejects an unsafe pair and reports the maximum poll interval
for the configured lease.

Keep the token file and both database strings out of command arguments and logs.
The runtime state root holds immutable definitions, journals, event frames,
identity-bound inner process-group registry records, workspaces, a neutral child
home, private job/build-scoped agent homes, and temporary files. The configured
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
Put the state root on storage whose loss/recovery policy matches the PostgreSQL
database; an incomplete journal is a reconciliation
event, not permission to guess success.

## Start and observe

Build the solution in locked mode, export the variables above, then start:

```bash
src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host
```

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
inner leader has already exited. Start-time validation prevents a stale record
from authorizing a signal to a reused pid.

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

## Acceptance and recovery checks

From HeMan, with the PostgreSQL container and host port selected:

```bash
FOGELL_PG_CONTAINER=fogell-fg060a \
FOGELL_PG_PORT=55445 \
FOGELL_BUILD_CONFIGURATION=Release \
./scripts/prove-runnable-controller.sh
```

Expected final line begins `FG-224 PROOF PASS`. The script owns a uniquely named
scratch database, role, state directory, and listener, and removes them on exit.
If progressive event publication fails, Run.Host emits no terminal journal
record; the failure remains infrastructure truth and the controller requires
reconciliation instead of inventing a build failure. A local-worker
`RequireReconciliation` transition atomically records its stable reason in an
`attempt.reconciliation_required` event and a `build.reconciliation_required`
outbox row, and preserves the fence-specific event file. Inspect those records,
the durable journal, retained event frames, and effect checkpoints before
deciding recovery. Invalid queued definitions have their own reason event;
their attempt → node → build quarantine and exactly one reasoned reconciliation
event/outbox pair commit atomically, so a notification never describes a state
change that did not commit. Lease-expiry and restore transitions remain
distinguishable by their reason and restore-epoch records. Never repair any of
them by editing immutable
`build_definitions` or force a terminal status directly.
Both bypass the evidence the controller uses to refuse duplicate or substituted
work.
