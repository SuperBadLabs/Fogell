# Native release board

This board drives the [release milestones](PRODUCT_DIRECTION.md). Update it when
evidence changes the order of work. Keep detailed tickets for the active wave
and the next wave; break down later waves when the preceding gate is measured.
The supported release remains one trusted Linux host and one local worker.

As of 2026-09-29, **W1 and W2 are complete**, **W3 is active**, and **W4 is next**.
The qualified product candidate is local at `1d2973c0`; GitHub `main` still
has the Jenkinsfile runtime. Publication of the exact branch was rejected by
automatic approval review pending explicit owner authorization.

| Wave | Milestone and deadline | Gate | Status |
| --- | --- | --- | --- |
| W0 | Reviewable baseline · Oct 2 | Approve publication scope; record pilot repos, host, and targets. | Publication blocked |
| W1 | Correctness · Oct 9 | Invalid env names refused; failed JUnit stays unstable; independent 115/115 matrix and full native gate pass. | Complete; [evidence](../reports/native-qualification-2026-09-29/REPORT.md) |
| W2 | Output · Oct 16 | Default-traced 350-marker case and representative build output complete without lost, duplicate, or reordered records. | Complete; [evidence](../reports/native-representative-complete-2026-09-29/REPORT.md) |
| W3 | Operations · Oct 23 | Backups, retention, alerts, cleanup catch-up, and 72-hour unattended staging proof. | Active; [local slice](../reports/native-operations-progress-2026-09-29/REPORT.md) |
| W4 | Pilot · Nov 6 | Seven days, 50 builds, ten failure/fix pairs across Fogell plus two named repos. | Next |
| W5 | Recovery/decision · Nov 13 | Paired restore, upgrade/rollback, stale-writer proof, then release-candidate or no-release decision. | Planned; split after W4 |

## Ordered tickets

| Order | Ticket | Wave | State | Exit evidence |
| --- | --- | --- | --- | --- |
| 1 | [FG-315](tickets/FG-315.md) maintenance quiescence | W3 | Doing | Admission/drain and writer-extinction proof before backup. |
| 2 | [FG-308](tickets/FG-308.md) paired backup and retention | W3 | Blocked on FG-315 | Scheduled, checked recovery points and bounded cleanup. |
| 3 | [FG-309](tickets/FG-309.md) operator alerts and capacity | W3 | Doing | Held-work and capacity alerts have measured thresholds. |
| 4 | [FG-310](tickets/FG-310.md) unattended staging proof | W3 | Ready | 72-hour run and downtime catch-up recorded. |
| 5 | [FG-312](tickets/FG-312.md) pilot preparation | W4 | Ready | Exact repo/host manifest and three control runs. |
| 6 | [FG-313](tickets/FG-313.md) failure and interruption pairs | W4 | Ready | Ten pairs and interruption evidence. |
| 7 | [FG-314](tickets/FG-314.md) seven-day pilot | W4 | Ready | Seven days, 50 builds, measured M4 verdict. |
| — | [FG-311](tickets/FG-311.md) nested process-test qualification | W2 | Done | [584/584 controller gate and reaping evidence](../reports/native-representative-complete-2026-09-29/REPORT.md). |
| — | [FG-306](tickets/FG-306.md) representative output | W2 | Done | [Fogell and Maven controller output](../reports/native-representative-complete-2026-09-29/REPORT.md). |
| — | [FG-302](tickets/FG-302.md) environment-name admission | W1 | Done | Parser, runner, and controller refuse invalid names. |
| — | [FG-305](tickets/FG-305.md) burst-output transport | W2 | Done | [350 markers, 15 feedback pages, 584/584 gate, 115/115 matrix](../reports/native-output-2026-09-29/REPORT.md). |
| — | [FG-304](tickets/FG-304.md) independent qualification | W1 | Done | 115/115 matrix and 582-test full gate on `9550bb05`. |
| — | [FG-303](tickets/FG-303.md) failed JUnit persistence | W1 | Done | Runner/controller regression and 569-test gate passed. |
| — | [FG-301](tickets/FG-301.md) publish review baseline | W0 | Blocked | Explicit approval for this exact branch; GitHub authentication works. |
| — | [FG-307](tickets/FG-307.md) pilot/recovery decisions | W0 | Done | Maven, clenkins, Luigi, and numerical targets selected. |

## Operating rule

At each wave start, rank tickets by false-success/data-loss/security risk,
then by the milestone gate they unblock. Keep at most two tickets Doing. Add,
split, remove, or park tickets as evidence warrants; record the reason in this
board and update the affected gate. A P0 supported-path defect can preempt the
queue. A completed ticket needs its exact commit and verification evidence.
Do not move a milestone date automatically. On November 13 decide release
candidate or no release; unfinished expansion work is parked.

## Decisions and changes

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-09-29 | Put W1 correctness ahead of W2 output; pull FG-302 forward while W0 owner decisions are pending. | The admission defect is proven, small, and blocks the 115/115 gate. |
| 2026-09-29 | Keep W4–W5 outcomes on the board; split W3 into FG-308–310 when W1 closed. | The active and next wave now have actionable tickets without guessing the later pilot/recovery implementation. |
| 2026-09-29 | Select local Apache Maven and clenkins as the additional pilot repos, Luigi as host, and 5 s idle-host p95 feedback, 60 min recovery time, 24 h recovery point as working gates. | The two repos exercise Java and Clojure/JS workloads beyond Fogell's F# gate; the delegated project lead can revise the targets when evidence warrants. |
| 2026-09-29 | Close W1 at `9550bb05` and start FG-305. | The carried-forward matrix passed 115/115, the locked gate passed 582 tests with zero warnings/errors, and the real controller proof passed. The reported burst-output failure is the next release blocker. |
| 2026-09-29 | Close FG-305 at `016ed3b2` and start FG-306. | The exact traced burst passed through the controller with 350 ordered markers across 15 pages, 584 tests and the 115-case matrix passed, and the over-limit case retained its typed failure. W2 still needs representative workloads. |
| 2026-09-29 | Add FG-311 ahead of the remaining FG-306 qualification. | The source-bound Fogell gate fails seven execution tests under the controller's nested process environment even though the standalone gate passes; a registry retry changes, but does not resolve, the failures. The controller reports failure correctly. Keep M2's gate and date unchanged. |
| 2026-09-29 | Record Maven's controller output pass and block FG-306 on FG-311. | Maven debug output matched its artifact across 26 pages and 2,517 ordered records; Fogell's full gate still fails inside the controller. The [partial qualification](../reports/native-representative-2026-09-29/REPORT.md) retains both outcomes. |
| 2026-09-29 | Close FG-311 and FG-306 at `1d2973c0`, close W2, start W3 with FG-308, and split W4 into FG-312–314. | Fogell's source-snapshot controller gate passed 584/584 and its artifact matched paginated feedback; Maven's separate noisy output pass is retained. The [completion report](../reports/native-representative-complete-2026-09-29/REPORT.md) records both and the failed nested-fixture attempts. W3's backup and retention work is the next false-evidence/data-loss risk. |
| 2026-09-29 | Start FG-309 alongside FG-308. | The board permits two Doing tickets; read-only alert checks can proceed independently while paired-backup work is built. |
| 2026-09-29 | Keep FG-308 and FG-309 Doing after the local helper/rehearsal slice. | One disposable paired point was created, checked, and restored; the read-only operator check and failure records passed focused tests. Scheduled multiple points, retention catch-up, host alert delivery/calibration, and the 72-hour staging gate are still required. The [partial report](../reports/native-operations-progress-2026-09-29/REPORT.md) lists evidence and limits. |
| 2026-09-29 | Confirm GitHub `main` remains `1c010549` and `gh` authentication works; keep FG-301 blocked only on exact publication approval. | The branch is 24 commits ahead and changes 4,487 files. Automatic approval review rejected the earlier push because the broad delegation did not explicitly authorize this migration payload. |
| 2026-09-29 | Add FG-315 ahead of FG-308; block FG-308 and keep FG-309 Doing. | The current controller has no admission pause/drain or independent writer-extinction contract. A timer that merely stops services can interrupt active external effects; a shell hook returning zero does not prove a paired backup is safe. Keep W3's October 23 gate and date unchanged. |
