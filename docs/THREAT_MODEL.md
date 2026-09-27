# Deployment and security boundary

Fogell supports one mutually trusted execution domain on a dedicated Linux
host or VM. The controller and local workloads share an OS identity. This is
not isolation for hostile code or mutually hostile tenants.

Implemented controls include:

- A required global bearer token, compared in fixed time and read from a bounded,
  service-owned regular file with mode 0400/0600.
- HTTPS for non-loopback listeners; loopback HTTP is supported.
- Separate runtime and maintenance database identities. The restricted runtime
  role cannot be superuser or bypass forced tenant row-level security.
- Fenced leases and restore epochs on execution publications; stale writers cannot
  publish current truth. The current worker itself is trusted.
- Strict JSON admission with source, depth, collection and scalar bounds.
- Cleared child environments, build-local HOME/TMPDIR, process-group supervision,
  finite output and artifact budgets, and descriptor-based publication checks.
- Durable journals and explicit reconciliation when execution evidence is missing.
- An opt-in exclusive storage pool backed by an operator-provisioned finite
  filesystem. Free-space observations are admission guards, not per-build quotas.
- Bounded, resumable operator retention that preserves active and uncertain work.

The API token is operator authority, not user identity or project RBAC. Database
row isolation depends on trusted application code selecting the transaction
context. Process groups clean up ordinary descendants; they do not isolate a
hostile process. Same-UID code can access service files, sockets and process state.
Artifact path checks and secret masking do not change that boundary.

Operators must enforce CPU, memory, process, disk and network policies outside
Fogell. Do not execute untrusted pull requests under this profile. The native
runtime does not provide remote workers, an approval broker, a secret-binding
pipeline operation, automatic retries, or external effect guarantees.

Keep state and database backups paired, quiesce writers before restore, advance
the restore epoch before admitting work, and inspect uncertain attempts instead
of replaying them. Retention is an explicit maintenance tool; it is not scheduled
by the controller. Never infer deployment qualification from a green unit test.
