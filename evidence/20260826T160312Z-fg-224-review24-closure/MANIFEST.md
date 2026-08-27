# FG-224 exact-head review 24 closure

Collected at `2026-08-26T21:35:03Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `79b8edecc6de23c2201e6e668f37644c5c985862`
- Base tree: `fcc0754d74aa0fd9fe0e7dd4978cc9a447fadf51`
- Candidate product tree: `6cd1ce5e1fc6b67b0de6f9d31f06dbe284fa8625`
- Candidate-diff SHA-256: `6c8367a7de8b7598283883408ffefff3b7404adec6de2b2114f78780027e3a6e`
- Delta: 5 files changed, 363 insertions, 14 deletions
- .NET SDK: `10.0.301`

The complete audited product and documentation delta was staged before
snapshotting. `candidate.diff` is the index-versus-HEAD diff and the candidate
product tree is derived from `git write-tree`. This evidence directory is output
and is intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5035092019` on base commit `79b8ede` found that
`ClaimNextExecution` quarantined invalid definitions and emitted an
`attempt.reconciliation_required` event, but omitted the matching
`build.reconciliation_required` outbox row.

The closure makes reconciliation publication complete and atomic:

- Missing definitions, legacy multi-node definitions, and definition digest
  mismatches move attempt → node → build to `reconciliation_required`, then
  insert one event and one outbox row in the same tenant transaction.
- Both records carry the exact definition-specific reason. The outbox body binds
  the build and attempt UUIDs, so external consumers observe the same transition
  that committed in Store.
- Eight concurrent claimers prove each poisoned FIFO row publishes exactly one
  pair and cannot starve valid work behind it.
- The adjacent audit closed the same publication gap in expired post-offer local
  leases. Expired `accepted`, `running`, `finalizing`, and `cancelling` attempts
  publish an exact `lease_expired` event/outbox pair atomically with attempt,
  node, and build reconciliation. Safe expired `offered → queued` recovery emits
  neither record.
- An eight-scanner race proves exactly one lease-expiry pair. A migration-
  tolerance fixture moves three expired attempts—including two retry ordinals on
  one node—through two distinct nodes and one build, proving one correctly bound
  pair per moved attempt and no publication on a repeated zero-transition scan.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 894/894 main-suite tests; every blocking proof; final `OK` | `fg224-review24-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review24-runnable-proof.log` |
| Store | PASS, 88/88 including poison race, lease-state table, scanner race, and three-attempt batch cardinality | `tests-Fogell.Store.Tests.log` |
| Release build | PASS, one existing FS0040 parser initialization warning and zero errors | `build.log` |
| Independent final audit | CLEAN; DML-CTE dependencies, RLS, atomicity, cardinality, JSON parity, and non-duplication verified | behavior is reproduced by the sealed Store tests |

The 894 main-suite tests are Controller API 31, Differential 266, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 88. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
