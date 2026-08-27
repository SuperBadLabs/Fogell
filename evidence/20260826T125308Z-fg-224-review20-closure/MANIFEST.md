# FG-224 exact-tree review 20 closure

Collected at `2026-08-26T19:19:16Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `eefe3ab21dde99905bb8afb3e010f88dad152a01`
- Base tree: `170bdab39f41f292a1dc0b695f816e4ccb798c94`
- Candidate product tree: `680260d0349f7000baede10bc31339befc58fd01`
- Candidate-diff SHA-256: `ffb3a1db95c0829495ac0dc8d748920f392daacb6386a4d623ecc9642ec83656`
- Delta: 11 files changed, 600 insertions, 85 deletions
- .NET SDK: `10.0.301`

The complete audited product delta was staged before snapshotting.
`candidate.diff` is the index-versus-HEAD diff and the candidate product tree is
derived from `git write-tree`. This evidence directory is output and is
intentionally absent from that product tree.

## Review closure

The final review rounds closed the following exact-head defects:

- Reconciliation is one atomic, fenced Store transition that updates attempt,
  node, and build state and emits one stable reason event plus one matching
  outbox record. Replays are idempotent, and a transient Store failure retains
  the original classified reason for the cleanup retry.
- A worker deletes an attempt event file only after terminal publication
  succeeds. Reconciliation and exceptional exits retain the recovery file.
- The shutdown proof waits for the known quiescent child-output frame, hashes
  the recovery file before SIGTERM, and requires the same bytes after
  reconciliation and after producer extinction.
- Process readers ingest independently of user `OnLine` callbacks. Delivery is
  one ordered asynchronous tail; callback faults are sticky, callback or reader
  noncompletion fails closed inside the existing bound, and callback admission
  closes atomically before the tail snapshot.
- Every callback-producing reader must reach EOF. Raw captured stdout keeps its
  bounded best-effort contract, while capture-mode stderr remains fail-closed.
  On natural completion, containment groups and their identity anchors are
  reaped before the 500 ms EOF decision, while the readers remain active.
- Cold-process stress changed the original delayed-callback case from 48/50
  failures to repeated all-green runs. Focused regressions cover sticky callback
  faults, bounded noncompletion, escaped late writers, capture-mode stderr, and
  the production containment-anchor ordering.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 882/882 main-suite tests; final `OK` | `fg224-review20-final-gate.log` |
| Runnable controller | PASS; terminal-only cleanup, graceful-shutdown reason event/outbox, and exact retained recovery bytes in addition to the full vertical slice | `fg224-review20-runnable-proof.log` |
| Release build | PASS, zero warnings and zero errors | `build.log` |
| Board accounting | PASS, 207 canonical rows; 129 DONE and 78 open | `fg224-review20-final-gate.log` |
| Independent final audit | CLEAR; FG-197 16/16 and containment 15/15 | recorded in the exact review round |

The 882 main-suite tests are Controller API 28, Differential 265, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 80. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
