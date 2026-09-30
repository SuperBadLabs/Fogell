# Controller installation

Use a dedicated Linux service identity and mutually trusted workloads. Install
.NET 10, `/bin/sh`, `setsid` and PostgreSQL 16. Build from a clean release directory
using the pinned SDK and locked dependency graph. Keep the controller, runner
and their dependency closures from the same build.

The host requires these environment settings:

| Setting | Value |
| --- | --- |
| `FOGELL_DATABASE_URL` | Restricted runtime PostgreSQL connection |
| `FOGELL_MAINTENANCE_DATABASE_URL` | Separate migration/maintenance connection to the same database |
| `FOGELL_API_TOKEN_FILE` | Absolute service-owned regular file, mode 0400/0600, containing at least 32 UTF-8 token bytes |
| `FOGELL_LISTEN_URL` | HTTPS URL, or loopback HTTP such as `http://127.0.0.1:46206` |
| `FOGELL_STATE_ROOT` | Absolute persistent state directory |
| `FOGELL_RUN_HOST_PATH` | Absolute executable path to the matching `Fogell.Run.Host` |
| `FOGELL_LOCAL_TRUST_POOL` | `trusted-linux` for the initial profile |
| `FOGELL_MAX_PIPELINE_BYTES` | `262144` for the native parser ceiling |
| `FOGELL_MAX_LOG_CHUNKS` | `100` is a bounded polling-page size |
| `FOGELL_WORKER_POLL_MS` | `50` |
| `FOGELL_WORKER_LEASE_SECONDS` | `60`; polling must be at most one third of the lease |

Apply migrations with the maintenance environment using
`Fogell.Retention migrate`. Create a separate login runtime role with
`NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE` and grant:

```sql
GRANT USAGE ON SCHEMA public TO fogell_runtime;
GRANT SELECT, UPDATE(singleton) ON controller_metadata TO fogell_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON organizations, projects, builds, nodes,
  attempts, events, outbox, log_chunks, effect_checkpoints, retry_decisions,
  build_definitions, source_verifications TO fogell_runtime;
GRANT SELECT ON organization_work_roots, build_retention TO fogell_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO fogell_runtime;
```

Provision organizations/projects with explicit UUIDs through maintenance access.
The runtime URL must actually authenticate as the restricted role. A test fixture
that connects as an administrator and changes role is not a production credential
boundary. Startup verifies distinct runtime/maintenance identities and capabilities.

Run `src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host` under
a service supervisor. Configure CPU/memory/process limits and egress policy
outside Fogell. `/health/live` reports host liveness; `/health/ready` checks
database, runtime capabilities, launchers, state root and storage admission.
Use the [storage pool](storage-pool.md) for a finite single-writer execution
filesystem. A free-space guard alone does not constrain arbitrary shell writes.

The [client](feedback-client.md) submits native definitions and snapshots, watches
bounded feedback, retrieves artifacts and explicitly cancels. Set up
[retention](retention.md) and [paired recovery](recovery.md) before unattended use.

For an isolated vertical-slice test, explicitly identify a disposable database
container created by `scripts/pg-test-db.sh`:

```bash
python3 scripts/prove-native.py --container YOUR_TEST_CONTAINER --port ITS_PORT
```

The proof creates unique database/role/state resources, runs the real controller
and worker, validates failure/fix source identity and artifacts, then removes only
its own resources. It does not modify an existing deployment.
