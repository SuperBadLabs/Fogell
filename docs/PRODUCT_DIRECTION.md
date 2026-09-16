# Product direction and release gates

Fogell's goal is reliable, self-hosted CI with a practical Jenkins migration
path. [ADR 0010](adr/0010-production-first-ci.md) is the binding scope decision.
This is a delivery plan, not a production certification.

## Initial user and deployment

Target a small team operating CI for mutually trusted projects on a dedicated
Linux host or VM with PostgreSQL and externally enforced resource and network
controls. The [threat model](THREAT_MODEL.md) defines the current boundary;
container use alone does not make same-UID workloads hostile-tenant safe.
The global controller bearer token is not per-user RBAC. Broader tenancy,
untrusted pull requests, and remote-worker isolation need separate designs and
proof before being offered.

Current pipeline input is Jenkinsfile syntax interpreted by the existing F#
engine. Parsing, execution support, and proven Jenkins parity are different
claims. The [generated ledger](COMPATIBILITY-LEDGER.tsv),
[scorecard](COMPATIBILITY-SCORECARD.md), and
[known limitations](KNOWN-LIMITATIONS.md) describe the current evidence.
They are regression and migration tools, not the release progress meter.

## What has landed

The merged production batches have bounded individual runner output, improved
log publication throughput, introduced shared output budgets across a build,
and bounded artifact publication and interrupted-copy cleanup. The
[controller runbook](runbooks/controller-host.md) owns the settings and limits.
The PRs linked in ADR 0010 own their measured validation results.

The opt-in [bounded storage pool](runbooks/storage-pool.md) adds kernel-enforced
aggregate workspace, stash, artifact, and runner scratch limits for one local
worker. It requires an operator-provisioned dedicated filesystem with finite
bytes and inodes. Free-space guards control admission; an exclusive durable
pool marker prevents overlapping Fogell writers and blocks uncertain restart.
It is not a per-build quota, concurrent-worker reservation, or history-retention
policy. Without this opt-in policy, arbitrary workspace writes remain unbounded.
Missing terminal execution evidence still requires reconciliation.

## Release gates

These are ordered delivery batches. Each is open until its stated evidence is
produced; this table introduces no DONE ticket and changes no historical board
accounting. Security or correctness defects in the supported path take priority
over this sequence.

| Order | Deliverable | Required acceptance evidence |
| --- | --- | --- |
| 1 — Storage safety | Workspace/stash bounds and disk-capacity admission, with explicit operator policy and a race-safe capacity decision. | Concurrent builds cannot bypass the configured reservation or quota; actual writes are constrained by the declared enforcement boundary. Disk pressure refuses new work with a durable reason. Cancellation, crash, and restart release or reconcile reservations without deleting another attempt's data. A normal control succeeds after pressure is relieved. |
| 2 — Retention | Bounded, resumable cleanup for logs, completed workspaces, snapshots, and artifact history. | Age/size/count policies preserve active attempts and required reconciliation evidence. Concurrent cleanup, restart mid-delete, filesystem substitution, and database/filesystem disagreement fail safely. Work per sweep is bounded and usage eventually returns below the declared target under the tested workload. |
| 3 — Operator recovery | Installation, upgrade/rollback, paired database/state backup and restore, reconciliation procedures, and useful health/capacity signals. | Run the procedures against a disposable deployment, including interrupted upgrade and stale workers after restore. Record recovery time and any data loss; compare them with explicit release targets. Do not claim a target before measuring it. |
| 4 — Controlled pilot | A versioned migration profile, actionable eligibility report, named representative CI jobs, and a sustained load/failure campaign. | A pipeline gets a supported, needs-change, or refused disposition with reasons. Approved jobs run through Controller.Host, preserve expected outputs and artifacts, and survive the declared cancellation/restart cases. Pin the profile, workload, concurrency, duration, resource limits, and pass thresholds before the campaign. Include Fogell building and testing itself as a candidate workload, subject to that profile. |

Batch 1's first implementation uses a dedicated filesystem as the aggregate
write boundary and admits one Fogell writer at a time. The storage-pool runbook
states its enforcement, recovery, and trust assumptions. Per-build isolation
and concurrent workers would require independently enforced slots or quotas;
application byte counting cannot provide either. Persistent deployment and
recovery evidence remain necessary before closing the broader release gate.

## Selecting work from the existing board

1. Fix a security, false-success, data-loss, or availability defect affecting the
   supported path according to its measured severity.
2. Complete the release gates above, starting with storage safety.
3. Add a capability only when a named user workflow needs it. State who needs
   it, its Fogell contract, migration impact, failure modes, operating cost,
   implementation scope, and acceptance measurement before implementing it.

The legacy board keeps ticket statuses and evidence; being unselected does not
mean a ticket is fixed. Compatibility-only expansion is deferred until it meets
rule 3. No ticket becomes urgent merely because it admits more corpus files.
Existing supported behavior keeps its tests and receipts. Differential probes
are required when making or changing a Jenkins compatibility claim; they are
not the oracle for a new Fogell-only operational feature.

PR #446's proposed capability waves require re-triage against these rules.
Its corpus survey and design work remain useful inputs. Its replacement must
identify which proposed capabilities serve the pilot; it cannot silently restore
corpus-ranked delivery. This decision does not claim those proposed features
have been implemented or authorize executing additional corpus files.

## Delivery and cost

Use one PR for a coherent batch, with cheaper implementation agents and an
independent reviewer where useful. Review shared lifecycle and failure behavior
before publication. Use automatic GitHub reviews; do not request extra paid
reviews or replace a PR solely to retrigger a reviewer. Record exact-commit
coverage and any tooling limitation honestly. The
[board operating contract](EXECUTION_BOARD.md#execution-cycle-and-definition-of-done)
defines the required local-QA/Codex route, optional stale Copilot coverage, and
the explicit evidence fallback for an unsupported bot-result format. Existing
required checks and the full pre-publication gate remain in force.
