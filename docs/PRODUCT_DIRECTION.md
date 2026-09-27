# Product direction and roadmap

Fogell is reliable self-hosted CI for human and AI development feedback loops.
Native versioned JSON is the only pipeline authoring contract. Work is selected
by supported user workflows, safety and operational evidence.

The native baseline passes 569 automated tests and the local runner/controller
proof: admission, source verification, failure/fix feedback, artifacts, source
retrieval, cancellation, terminal replay and interrupted-run reconciliation.
The build has zero warnings or errors. This is functional validation; sustained
operation and recovery objectives still require the qualification below.

The [26 September Luigi campaign](../reports/luigi-2026-09-26/REPORT.md)
found release qualification blockers beyond the passing gate: failed JUnit
results broke persisted execution, and newline-terminated environment names
bypass validation. It also measured a practical burst-output queue limit.
The JUnit/journal integration is fixed in the current working tree and covered
by persisted-runner and controller regressions. Resolve the remaining findings
before advancing to a sustained native pilot.

This release replaces the previous authoring/runtime path. Old campaign results
are historical and do not qualify the native runtime. Production release requires
fresh validation of the supported profile.

| Order | Deliverable | Exit evidence |
| --- | --- | --- |
| 1 | Native execution qualification | Controller admission, source snapshots, typed failure/fix feedback, artifacts, cancellation and crash/restart work end to end with the native definition format. |
| 2 | Unattended operations | Scheduled retention and backups, cleanup catch-up after downtime, held-work alerts and measured filesystem/database capacity. |
| 3 | Representative sustained pilot | Full Fogell build/test plus named real projects over multiple days; cold dependency and resource-pressure cases; uncensored latency and failure measurements. |
| 4 | Recovery qualification | Native runtime installation, upgrade/rollback and paired restore, including stale writers; measured whole-procedure recovery time and data loss against declared objectives. |
| 5 | Expanded deployment only when needed | Independently designed worker isolation, quotas, user authorization and remote execution, each with adversarial evidence. |

Correctness, data loss, false success and security defects in the supported path
precede feature work. Keep the global bearer, single-node and trusted-workload
limits explicit. Do not publish or deploy a release without owner authorization.
