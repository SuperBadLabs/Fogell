# Native release board

This board drives the [release milestones](PRODUCT_DIRECTION.md). Update it when
evidence changes the order of work. Keep detailed tickets for the active wave
and the next wave; break down later waves when the preceding gate is measured.
The supported release remains one trusted Linux host and one local worker.

As of 2026-09-29, **W1 is complete**, **W2 is active**, and **W3 is next**.
The qualified product candidate is local at `9550bb05`; GitHub `main` still
has the Jenkinsfile runtime. Publication of the exact branch was rejected by
automatic approval review pending explicit owner authorization.

| Wave | Milestone and deadline | Gate | Status |
| --- | --- | --- | --- |
| W0 | Reviewable baseline · Oct 2 | Approve publication scope; record pilot repos, host, and targets. | Publication blocked |
| W1 | Correctness · Oct 9 | Invalid env names refused; failed JUnit stays unstable; independent 115/115 matrix and full native gate pass. | Complete; [evidence](../reports/native-qualification-2026-09-29/REPORT.md) |
| W2 | Output · Oct 16 | Default-traced 350-marker case and representative build output complete without lost, duplicate, or reordered records. | Active |
| W3 | Operations · Oct 23 | Backups, retention, alerts, cleanup catch-up, and 72-hour unattended staging proof. | Next |
| W4 | Pilot · Nov 6 | Seven days, 50 builds, ten failure/fix pairs across Fogell plus two named repos. | Planned; split after W3 |
| W5 | Recovery/decision · Nov 13 | Paired restore, upgrade/rollback, stale-writer proof, then release-candidate or no-release decision. | Planned; split after W4 |

## Ordered tickets

| Order | Ticket | Wave | State | Exit evidence |
| --- | --- | --- | --- | --- |
| 1 | [FG-311](tickets/FG-311.md) nested process-test qualification | W2 | Doing | Full Fogell gate succeeds through the controller with reaping guarantees intact. |
| 2 | [FG-306](tickets/FG-306.md) representative output | W2 | Blocked on FG-311 | Maven passed; full Fogell gate remains required. |
| 3 | [FG-308](tickets/FG-308.md) paired backup and retention | W3 | Ready | Scheduled, checked recovery points and bounded cleanup. |
| 4 | [FG-309](tickets/FG-309.md) operator alerts and capacity | W3 | Ready | Held-work and capacity alerts have measured thresholds. |
| 5 | [FG-310](tickets/FG-310.md) unattended staging proof | W3 | Ready | 72-hour run and downtime catch-up recorded. |
| — | [FG-302](tickets/FG-302.md) environment-name admission | W1 | Done | Parser, runner, and controller refuse invalid names. |
| — | [FG-305](tickets/FG-305.md) burst-output transport | W2 | Done | [350 markers, 15 feedback pages, 584/584 gate, 115/115 matrix](../reports/native-output-2026-09-29/REPORT.md). |
| — | [FG-304](tickets/FG-304.md) independent qualification | W1 | Done | 115/115 matrix and 582-test full gate on `9550bb05`. |
| — | [FG-303](tickets/FG-303.md) failed JUnit persistence | W1 | Done | Runner/controller regression and 569-test gate passed. |
| — | [FG-301](tickets/FG-301.md) publish review baseline | W0 | Blocked | Explicit publication approval and working GitHub authentication. |
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
