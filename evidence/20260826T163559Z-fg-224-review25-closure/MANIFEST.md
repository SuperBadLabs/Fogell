# FG-224 exact-head review 25 closure

Collected at `2026-08-26T22:06:56Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `ff7b21c3d1679f8a71a1fc007ea73bfc1599d2cd`
- Base tree: `1da7d3d47b18f14f40af51803944440ffb87a9cd`
- Candidate product tree: `491e3e23ed0e7098a86e60d75173ce669b825be1`
- Candidate-diff SHA-256: `d53ef96b42e4bdddc1334469c2dfdbd9b01bf640b0fcf891b151a90207bd8a01`
- Delta: 9 files changed, 654 insertions, 57 deletions
- .NET SDK: `10.0.301`

The complete audited product and documentation delta was staged before
snapshotting. `candidate.diff` is the index-versus-HEAD diff and the candidate
product tree is derived from `git write-tree`. This evidence directory is output
and is intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5035321007` on base commit `ff7b21c` found two operational gaps:

1. Active worker polling ignored shutdown cancellation, allowing a configured
   60-second poll to delay child cleanup and reconciliation.
2. `/health/ready` rechecked database and launcher dependencies but not continued
   state-root availability and effective-identity writability.

The closure makes both runtime boundaries prompt, truthful, and bounded:

- Active polling catches only the requested stopping token's cancellation as
  normal control flow. It sets the existing interrupted disposition, skips the
  natural-exit wait, performs identity-bound cleanup, and preserves durable reason
  `controller_shutdown` rather than escaping as `worker_exception`.
- State-root readiness uses a non-creating, uniquely named CreateNew/write/
  Flush(true)/unlink probe. A missing or unwritable configured root returns 503;
  partial probes are cleaned, operator data is untouched, and readiness recovers
  after the operator restores the path or permissions.
- Database, capability, launcher, and state-root checks are lazy and ordered.
  A lock-protected monotonic cache bounds healthy endpoint and idle-worker probes
  to once per second and prevents concurrent request stampedes.
- The worker consults cached readiness before claiming, then forces a fresh probe
  after an offer and before materialization. Fresh failure fenced-requeues the
  still-unstarted offer; a direct Store regression proves queued attempt/node/
  build lineage, cleared lease fields, no reconciliation publication, and a
  replacement fence advanced exactly once.
- Deterministic tests cover immediate wake from a 60-second active poll, durable
  shutdown reason precedence, cache cadence and recovery, forced post-offer
  override, missing/unwritable roots, partial-probe cleanup, lazy dependency
  ordering, and 32 concurrent readiness calls sharing one probe.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 902/902 main-suite tests; every blocking proof; final `OK` | `fg224-review25-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review25-runnable-proof.log` |
| Controller API | PASS, 33/33 including state-root loss, write failure, recovery, cadence, and stampede resistance | `tests-Fogell.Controller.Api.Tests.log` |
| Differential | PASS, 271/271 including shutdown latency/reason and worker cache/fresh semantics | `tests-Fogell.Differential.Tests.log` |
| Store | PASS, 89/89 including exact offered-state fenced requeue and replacement authority | `tests-Fogell.Store.Tests.log` |
| Release build | PASS, one existing FS0040 parser initialization warning and zero errors | `build.log` |
| Independent final audit | CLEAN; cancellation, probe cleanup, cache concurrency/cadence, ordered readiness, claim guards, and requeue contract verified | behavior is reproduced by the sealed tests |

The 902 main-suite tests are Controller API 33, Differential 271, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 89. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
