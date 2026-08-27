# FG-224 exact-head review 28 closure

Collected at `2026-08-26T23:28:39Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `1d5d34c4efaf8b892b04799b8715dff6fa3d4f9e`
- Base tree: `018b4e395a0351e5702adbadd4bc2d777b6f3c69`
- Candidate product tree: `5fdd6ee7464e6fc7e5a30812dd2ff2c400aa80b8`
- Candidate-diff SHA-256: `f6d128556c874ff06e75eee235f431b0deb880c0f0962e3d5f47fa9ed792e2fa`
- Delta: 6 files changed, 233 insertions, 32 deletions
- .NET SDK: `10.0.301`

The complete product and documentation delta was staged before
snapshotting. `candidate.diff` is the index-versus-HEAD diff and the candidate
product tree is derived from `git write-tree`. This evidence directory is output
and is intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5035785786` on base commit `1d5d34c` found that
`NextLogSequence` and process preparation occur after `BeginExecution` commits
`running` but before any child launch. An exception in that known-unstarted
interval escaped to the outer worker loop, so lease recovery later treated the
running state as ambiguous and required reconciliation unnecessarily.

The closure makes the no-child boundary explicit:

- One preparation helper contains every fallible operation between
  `BeginExecution` and the call to `WorkerLaunch.tryStart`: next log sequence,
  event state, `ProcessStartInfo` and environment construction, `Process`
  allocation, and `StartInfo` assignment.
- A setup exception invokes exactly one fenced `RequeueOwnedAttempt` before the
  first diagnostic. Requeue success, lost authority, and Store exception are
  classified separately; every failure returns no prepared process, making
  `Process.Start` unreachable.
- A partially allocated `Process` is disposed before the setup exception leaves
  its preparation closure. Successful ownership transfers to the branch-level
  `use` and preserves existing launch and cleanup behavior.
- The safe recovery requires exact owner, fence, live lease, and current restore
  epoch. It restores attempt, node, and build to `queued`, clears the lease,
  preserves cancellation, emits no reconciliation event/outbox row, and lets a
  replacement advance the fence.
- Once `Process.Start` is actually attempted, false or exceptional results remain
  conservatively classified as reasoned `launcher_failed` reconciliation.
- Deterministic Worker coverage proves setup-only success and the requeue
  true/false/throw branches, exact callback cardinality, and durable-before-log
  order. Store coverage explicitly proves zero reconciliation publication for
  verified no-child running-state requeue.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative gate with legitimate FG-094 inputs | PASS, 907/907 main-suite tests; every blocking proof; final `OK` | `fg224-review28-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review28-runnable-proof.log` |
| Differential | PASS, 273/273 including all pre-launch preparation outcomes and order | `tests-Fogell.Differential.Tests.log` |
| Store | PASS, 91/91 including publication-free verified-extinction requeue | `tests-Fogell.Store.Tests.log` |
| Sealer Release build | PASS, zero warnings and zero errors | `build.log` |
| Authoritative full-gate build | PASS, one existing FS0040 parser initialization warning and zero errors | `fg224-review28-final-gate.log` |

The 907 main-suite tests are Controller API 34, Differential 273, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 91. This
receipt checksum-binds the eight `tests-*.log` summaries, `candidate.diff`, and
the staged tree inventory together.

The authoritative gate log SHA-256 is
`37885d5c6652741a5bdf14ba878d0bc7d0f35a1e8b3d294b74269ad7554cc976`.
The runnable proof SHA-256 is
`c34bef51e85ab472274daa35dc4e56194b434ab01c62f9977f0b86641e729f88`.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
