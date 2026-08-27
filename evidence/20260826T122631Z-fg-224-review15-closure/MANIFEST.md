# FG-224 exact-head review 15 closure

Collected at `2026-08-26T17:51:57Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `d20d58f5bb8e4986fb3a28a40c47ecd435048c5c`
- Base tree: `89b101ff22c998085b349d56f08a6640f9f0913a`
- Candidate tree: `193abea5208445b3ef25971b8cc2edf281fdcf98`
- Candidate-diff SHA-256: `e4fab5727c46880090ffa7def39a8cf2ef6d52a38d7db2672b1550431f54f6f6`
- Delta: 10 files changed, 195 insertions, 62 deletions
- .NET SDK: `10.0.301`

The complete audited product delta was staged before snapshotting.
`candidate.diff` is the index-versus-HEAD diff and the candidate tree is
derived from `git write-tree`. The evidence directory is output and is
intentionally absent from the candidate tree.

## Review closure

Codex review `5033325876` and Copilot review `5033330638` on the base commit
reported five findings. All are closed in this candidate:

- The launch edge rechecks graceful-shutdown cancellation immediately before
  `Process.Start`. A suppressed launch performs no start effect and immediately
  uses the current fence to requeue the running attempt instead of holding its
  lease until expiry.
- The injectable launch boundary now returns a closed result union. It contains
  only `Process.Start`; no fallible reconciliation callback can escape, recurse,
  or be invoked twice. The worker performs durable reconciliation before
  diagnostics for false and thrown launches.
- Fresh retry decisions delegate child creation and authority reset to the
  canonical `Attempt.retryOf` constructor.
- Controller API and Differential tests no longer compile private copies of
  Host source files. Both reference the built Host assembly through explicit
  friend-assembly declarations, and their lockfiles bind that graph.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative locked gate with legitimate FG-094 inputs | PASS, 876/876 main-suite tests; final `OK` | `fg224-review15-final-gate.log` |
| Dependency locks | PASS, 26/26 projects; mutation proof and source-cleared locked build pass | `fg224-review15-final-gate.log` |
| Runnable controller | PASS; restart discovery, supervised execution, retry journals, bounded fenced logs, poison FIFO progress, and finite post-exit drain over 16 MiB | `fg224-review15-runnable-proof.log` |
| Live compatibility | PASS, 228/228 files; accepted 200→200; tier 1 1→1; zero oracle-not-ready losses | `fg224-review15-final-gate.log` |
| Release build | PASS, zero warnings and zero errors | `build.log` |

The 876 main-suite tests are Controller API 27, Differential 265, Domain 34,
Execution 95, Groovy 224, Journal 31, Pipeline Parser 120, and Store 80. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
