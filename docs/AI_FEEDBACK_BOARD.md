# AI feedback loop — product and execution board

Owner: Product Manager / Chief Architect. Created 2026-09-22.

Local execution completed 2026-09-23: FG-262–269 meet their local acceptance
criteria. The [complete gate and pilot evidence](../evidence/20260922-ai-e2e/README.md)
include 30 correction loops, bounded retention, paired recovery and 1,385 passing
tests. This board does not claim publication or unattended production readiness.

The requested Luigi deployment and bounded adversarial campaign completed
2026-09-23 in [FG-270](tickets/FG-270.md). The [measured report](../evidence/20260923-luigi/README.md)
records 100 baseline loops, 30 final-release qualification loops, all 21 fault
scenarios and a 40.18-second paired restore. The persistent service is healthy;
the final release passed 1,387 tests and the full gate. Historical failures are
retained separately from the final passing qualification.

Fogell is CI for human and AI collaborators. Its primary product outcome is a
short, trustworthy path from a change to evidence that enables the next change.
Jenkins migration remains a secondary, explicitly scoped capability.

The owner requested this board and delegated implementation. This is the active
board for that work; the [historical board](EXECUTION_BOARD.md) retains its
ticket evidence and publication requirements. The [design boards](architecture/AI_FEEDBACK_LOOP.md)
own product requirements, architecture, and release boundaries.

## Milestones

| Milestone | User outcome | Exit evidence |
| --- | --- | --- |
| M1 — Observe and act | A trusted local agent submits a pipeline, follows output, retrieves a bounded structured result, cancels, and verifies a corrected submission. | Real controller failure/fix campaign plus client and API negative controls. No claim of source revision verification or parsed failure diagnosis. |
| M2 — Explain and reproduce | Each result identifies immutable source and structured failed steps/tests; another client can reproduce the same inputs. | Explicit content identity and dirty-tree controls; typed diagnostics with source locations where known; reproducibility campaign. |
| M3 — Sustain the loop | Daily self-hosting stays within storage policy and survives declared failures. | Retention and paired recovery proofs, sustained self-hosted pilot, measured latency distributions. |
| M4 — Operate on Luigi | A persistent owned service completes sustained feedback and declared fault tests without restarting existing services. | 100 baseline loops, 30 final-release loops, all 21 adversarial controls, paired restore and unchanged protected-service identities; see FG-270. |

An internal M1 experiment comes before broad deployment. Existing production
safety gates remain mandatory for a release. Preserve the current trusted Linux,
single-writer storage profile; do not imply untrusted-PR isolation.

## Ticket queue

Status vocabulary: TODO, PARTIAL, BLOCKED, DONE. PARTIAL includes implementation
in progress or local validation awaiting remaining acceptance. Ticket files own
validation evidence; this table owns ordering, dependency, and assignment.

| Order | Ticket | Deliverable | Depends on | Assignment |
| --- | --- | --- | --- | --- |
| 1 | [FG-262](tickets/FG-262.md) | Product and architecture design boards | — | Primary agent / PM and architect |
| 2 | [FG-263](tickets/FG-263.md) | Versioned bounded feedback read API | — | API implementation subagent |
| 2 | [FG-264](tickets/FG-264.md) | F# agent-facing controller client | —; feedback integration with FG-263 | Client implementation subagent |
| 2 | [FG-265](tickets/FG-265.md) | Real failure/fix proof and latency observations | FG-263, FG-264 | Proof implementation subagent |
| 3 | [FG-266](tickets/FG-266.md) | Structured step and test diagnostics | M1 findings | Diagnostics implementation subagent |
| 3 | [FG-267](tickets/FG-267.md) | Immutable source identity and reproduction | M1 findings | Source/client implementation subagent |
| 3 | [FG-268](tickets/FG-268.md) | Bounded historical retention | Storage ownership design | Retention implementation subagent |
| 4 | [FG-269](tickets/FG-269.md) | Paired recovery and sustained self-hosted pilot | FG-266–268 | Primary agent / integration and release evidence |
| 5 | [FG-270](tickets/FG-270.md) | Persistent Luigi deployment and bounded adversarial pilot | FG-269 | Primary deployment agent / proof driver / independent review |

## Execution protocol

Implementation agents own disjoint files and communicate interface changes.
The primary agent integrates and independently reviews their combined changes.
Run focused tests during iteration and applicable integration checks after
integration. An unavailable dependency is recorded as missing evidence, never
converted into a pass. Do not mark a ticket DONE until every acceptance item
is met. Publication additionally requires the existing full HeMan gate and
exact-head review route. Local implementation is not a release certification.

Choose subsequent tickets using observed friction and failure rates in M1.
Compatibility counts do not rank this queue. Do not add a new DSL, an LLM inside
the controller, or a remote agent protocol merely to label this AI CI.

## Reviewable results

- [Client usage and exit semantics](runbooks/feedback-client.md)
- [Real-controller proof and reproduction](runbooks/feedback-loop-proof.md)
- [M1 campaign receipts and validation](../evidence/20260922-feedback-loop/README.md)
- [Source snapshots and reproduction](runbooks/source-snapshots.md)
- [Typed diagnostics](architecture/EXECUTION_DIAGNOSTICS.md)
- [Retention ownership and recovery](runbooks/retention.md)
- [Self-hosted pilot and paired recovery profile](runbooks/self-hosted-pilot.md)
- [End-to-end campaign and recovery evidence](../evidence/20260922-ai-e2e/README.md)
- [Luigi deployment and adversarial campaign](runbooks/luigi-campaign.md)

The ticket files above remain authoritative for completion status. Retained
campaign receipts identify the measured working tree and explicitly separate
later harness corrections from the original live runs.
