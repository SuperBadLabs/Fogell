# FG-224 exact-head review 26 closure

Collected at `2026-08-26T22:28:29Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `f836a75aff131513dfd26dfb3762dbb0aa009352`
- Base tree: `a78c1e344c47bcc73264cd2fa95c2ced314ba3a2`
- Candidate product tree: `86a46ec653d762f34ccf98817b174291db973b30`
- Candidate-diff SHA-256: `d670b3e01d76beb3180a024937004c6c8d0bb83802b4dc2ffa16440404d4f23a`
- Delta: 6 files changed, 132 insertions, 42 deletions
- .NET SDK: `10.0.301`

The complete audited product and documentation delta was staged before
snapshotting. `candidate.diff` is the index-versus-HEAD diff and the candidate
product tree is derived from `git write-tree`. This evidence directory is output
and is intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5035502687` on base commit `f836a75` found that launcher loss after
`ClaimNextExecution` only logged and returned. That left a still-unstarted
`offered` attempt unavailable until lease expiry, while equivalent state-root
loss already used immediate fenced recovery.

The closure gives both post-offer dependency failures one durable disposition:

- A lazy launcher-first guard checks the state root only when launchers are
  available and selects exactly one dependency-specific recovery callback.
- Launcher or state-root loss calls `RequeueOwnedAttempt` before any fallible
  diagnostic. Success, lost authority, and exception paths then emit one
  exclusive dependency-specific outcome log.
- Requeue requires the exact organization, attempt, owner, fence, unexpired
  lease, and current restore epoch under attempt → node → build lock order.
- A successful requeue restores attempt, node, and build to `queued`, clears the
  lease, publishes no reconciliation event or outbox row, preserves any
  concurrent cancellation request, and advances the replacement claim's fence.
- Either refusal blocks definition materialization and `BeginExecution`. A lost
  authority or Store exception fails closed for lease recovery; the later
  post-`BeginExecution` launcher race remains `launcher_failed` reconciliation.
- Deterministic coverage proves launcher-first short-circuiting, both exact
  causes, one callback, the admitted path, and the durable launcher-loss lineage,
  publication silence, lease clearing, and replacement authority.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 903/903 main-suite tests; every blocking proof; final `OK` | `fg224-review26-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review26-runnable-proof.log` |
| Controller API | PASS, 33/33 | `tests-Fogell.Controller.Api.Tests.log` |
| Differential | PASS, 272/272 including lazy post-offer dependency selection | `tests-Fogell.Differential.Tests.log` |
| Store | PASS, 89/89 including immediate offered-state launcher-loss requeue | `tests-Fogell.Store.Tests.log` |
| Sealer Release build | PASS, zero warnings and zero errors | `build.log` |
| Authoritative full gate build | PASS, one existing FS0040 parser initialization warning and zero errors | `fg224-review26-final-gate.log` |
| Independent final audit | CLEAN; ordering, authority races, cancellation preservation, publication silence, diagnostics, documentation, and coverage verified | behavior is reproduced by the sealed tests |

The 903 main-suite tests are Controller API 33, Differential 272, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 89. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

The authoritative gate ran against fresh database
`fogell_fg224_review26_3867130539`, whose absence was verified before creation
and after cleanup. Its SHA-256 is
`1147c642d44c31de806cdd33ec1f843a7364624dd75126c3fa19923f5e292863`.
The runnable proof SHA-256 is
`84de300d634cbebb6ffdcf95fd0052d753d066ba355b0f0c549ea4579f9d7bc9`.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
