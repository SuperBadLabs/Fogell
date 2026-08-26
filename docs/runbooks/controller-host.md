# Controller host runbook

`Fogell.Controller.Host` is Fogell's runnable single-node controller. It must be
started with separate runtime and maintenance database capabilities. Startup
refuses before binding when configuration, migration, or role validation fails.

## Database roles

The runtime identity must be `NOSUPERUSER NOBYPASSRLS` and distinct from the
maintenance identity. Grant only the runtime surface:

```sql
GRANT USAGE ON SCHEMA public TO fogell_runtime;
GRANT SELECT ON controller_metadata TO fogell_runtime;
GRANT SELECT ON organization_work_roots TO fogell_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON
  organizations, projects, builds, nodes, attempts, events, outbox, log_chunks,
  effect_checkpoints, retry_decisions, build_definitions
TO fogell_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO fogell_runtime;
```

The maintenance identity applies checksum-pinned migrations during startup and
must not be used for requests or worker operations.

## Required configuration

```text
FOGELL_DATABASE_URL                 runtime Npgsql connection string
FOGELL_MAINTENANCE_DATABASE_URL     distinct migration connection string
FOGELL_API_TOKEN_FILE               absolute/readable token file, >=32 characters
FOGELL_LISTEN_URL                   HTTPS, or loopback HTTP for local operation
FOGELL_STATE_ROOT                   absolute durable controller-state directory
FOGELL_RUN_HOST_PATH                built Fogell.Run.Host executable
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
workspaces, a neutral child home, and temporary files. Put it on storage whose
loss/recovery policy matches the PostgreSQL database; an incomplete journal is a
reconciliation event, not permission to guess success.

## Start and observe

Build the solution in locked mode, export the variables above, then start:

```bash
src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host
```

Use `/health/live` for process liveness and `/health/ready` for runtime database
reachability. Do not send traffic until readiness returns 200. A graceful stop
performs bounded outer process-group cleanup and moves started work to
`reconciliation_required`. It never requeues a started execution automatically:
steps may create nested sessions that an outer process-group probe cannot prove
extinct. After an ungraceful stop, an expired never-started offer may be queued
again, while any expired started lease also enters reconciliation.

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
If an attempt becomes `reconciliation_required`, inspect its durable journal and
effect checkpoints before deciding recovery. Never edit `build_definitions` or
force a terminal status directly: both bypass the evidence the controller uses
to refuse duplicate or substituted work.
