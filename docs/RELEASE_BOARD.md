# Native release board

This board drives the [release milestones](PRODUCT_DIRECTION.md). Update it when
evidence changes the order of work. Keep detailed tickets for the active wave
and the next wave; break down later waves when the preceding gate is measured.
The supported release remains one trusted Linux host and one local worker.

As of 2026-09-29, **W1 is active** and **W2 is next**. W0 decisions remain open
but do not block local correctness work. The candidate is local at `14855090`;
GitHub `main` still has the Jenkinsfile runtime. Publication of the exact branch
was rejected by automatic approval review pending explicit owner authorization.

| Wave | Milestone and deadline | Gate | Status |
| --- | --- | --- | --- |
| W0 | Reviewable baseline · Oct 2 | Approve publication scope; record pilot repos, host, and targets. | Publication blocked |
| W1 | Correctness · Oct 9 | Invalid env names refused; failed JUnit stays unstable; independent 115/115 matrix and full native gate pass. | Active |
| W2 | Output · Oct 16 | Default-traced 350-marker case and representative build output complete without lost, duplicate, or reordered records. | Next |
| W3 | Operations · Oct 23 | Backups, retention, alerts, cleanup catch-up, and 72-hour unattended staging proof. | Planned; split after W2 |
| W4 | Pilot · Nov 6 | Seven days, 50 builds, ten failure/fix pairs across Fogell plus two named repos. | Planned; split after W3 |
| W5 | Recovery/decision · Nov 13 | Paired restore, upgrade/rollback, stale-writer proof, then release-candidate or no-release decision. | Planned; split after W4 |

## Ordered tickets

| Order | Ticket | Wave | State | Exit evidence |
| --- | --- | --- | --- | --- |
| 1 | [FG-302](tickets/FG-302.md) environment-name admission | W1 | Doing | Parser, runner, and controller refuse invalid names. |
| 2 | [FG-304](tickets/FG-304.md) independent qualification | W1 | Ready | 115/115 matrix and full gate on one exact tree. |
| 3 | [FG-305](tickets/FG-305.md) burst-output transport | W2 | Ready | 350 traced markers complete with exact ordered output. |
| 4 | [FG-306](tickets/FG-306.md) representative output | W2 | Ready | Real build/test and noisy workload through the controller. |
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
| 2026-09-29 | Keep detailed W3–W5 tickets uncreated for now. | Their design depends on measured W2 output behavior and the two named pilot repos. |
| 2026-09-29 | Select local Apache Maven and clenkins as the additional pilot repos, Luigi as host, and 5 s idle-host p95 feedback, 60 min recovery time, 24 h recovery point as working gates. | The two repos exercise Java and Clojure/JS workloads beyond Fogell's F# gate; the delegated project lead can revise the targets when evidence warrants. |
