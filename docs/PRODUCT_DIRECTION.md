# Product direction and release milestones

Fogell is reliable self-hosted CI for human and AI development feedback loops.
Native versioned JSON is the only pipeline authoring contract. Work is selected
by supported user workflows, safety and operational evidence.

The current local native gate passes 584 automated tests and the runner/controller
proof: admission, source verification, failure/fix feedback, artifacts, source
retrieval, cancellation, terminal replay and interrupted-run reconciliation.
The build has zero warnings or errors. This is functional validation; sustained
operation and recovery objectives still require the qualification below.

The [26 September Luigi campaign](../reports/luigi-2026-09-26/REPORT.md)
found release qualification blockers beyond the passing gate: failed JUnit
results broke persisted execution, newline-terminated environment names
bypassed validation, and fast traced output reached a practical callback-queue
limit. The first two defects are fixed. The
[September 29 qualification](../reports/native-qualification-2026-09-29/REPORT.md)
passed its 115-case matrix and full gate. The
[burst-output qualification](../reports/native-output-2026-09-29/REPORT.md)
passed the traced controller case and the updated 584-test gate. The
[representative-output attempt](../reports/native-representative-2026-09-29/REPORT.md)
passed Apache Maven's noisy build but exposed a nested process-test blocker
when Fogell's own full gate ran as a controller workload. Close FG-311 before
advancing to a sustained native pilot.

This release replaces the previous authoring/runtime path. Old campaign results
are historical and do not qualify the native runtime. Production release requires
fresh validation of the supported profile.

## First release boundary

The first native release supports one dedicated Linux host, PostgreSQL 16, one
local worker, trusted workloads, a global operator bearer, and version 1 JSON
pipelines. It does not promise hostile-tenant isolation, remote workers, general
Jenkinsfile translation, or a multi-node controller. Those are separate products
with separate evidence requirements. No feature expansion enters this plan.

The [release board](RELEASE_BOARD.md) orders the current and next wave's tickets.
The dates below are decision deadlines for one focused engineering owner with
access to the existing Luigi test host. A missed gate is recorded as missed; it
does not silently move all later dates. Each gate needs an exact candidate commit,
the commands or harness used, raw results, and a short pass/fail summary. The
historical Luigi report remains an observation of its September 26 candidate.

| Milestone | Deadline | Required result |
| --- | --- | --- |
| M0 — Reviewable baseline | October 2, 2026 | Publish the current native branch for review only after explicit owner approval of its source/report archives. Name two additional pilot repositories, choose the test host, and approve the pilot and recovery objectives below. A protected-main merge is not part of this milestone. |
| M1 — Correctness blockers closed | October 9 | Reject trailing LF/CRLF and all other invalid environment-name characters; retain the fixed JUnit result and diagnostics through runner and controller persistence. Rerun the 115-case independent matrix against the exact candidate: 115/115 pass. The locked build, all test projects, and real controller proof pass with no warnings or skipped database suites. |
| M2 — Output under real load | October 16 | The reported 350-marker default-tracing reproducer completes without `OUTPUT_LIMIT_EXCEEDED`, loss, duplication, or reordering. Run Fogell's full build/test output and one noisy representative workload through the controller, including pagination and artifacts. Exceeding a documented hard limit must still fail with a typed reason and retained evidence. |
| M3 — Unattended operation | October 23 | Schedule paired database/state backups and retention, alert on held or reconciliation-required work and capacity thresholds, and prove cleanup catches up after downtime. Complete a 72-hour unattended staging run with backup, restore-point, alert, disk, and database measurements recorded. |
| M4 — Sustained native pilot | November 6 | Run Fogell and the two named repositories for at least seven consecutive days and at least 50 builds, including ten deliberate failure/fix pairs, cold dependencies, a burst-output case, a controller restart, and a worker interruption. Every build ends terminal or explicitly requires reconciliation; no false success, lost artifact, or unobserved replay is accepted. On an otherwise idle host, target p95 accepted-submission-to-first-feedback at five seconds or less. Record all latency and resource measurements without censoring failures. |
| M5 — Recovery and release decision | November 13 | From a clean release directory, rehearse install, upgrade, rollback, and paired database/state restore with a stale writer present. Measure the whole procedure against a proposed 60-minute recovery-time objective and 24-hour recovery-point objective, approved at M0. Decide **release candidate** only if M1–M5 pass and no supported-path correctness, data-loss, or security blocker remains; otherwise record **no release** and park expansion work. |

The November 13 decision is the stop point for this release attempt. A failed
milestone does not justify indefinite feature work or a partial success claim.
Correctness, data loss, false success, and supported-path security defects take
priority over new features. Keep the global bearer, single-node, and trusted-workload
limits explicit. Publishing code or deploying a release still requires owner
authorization; a passing gate alone does not grant it.
