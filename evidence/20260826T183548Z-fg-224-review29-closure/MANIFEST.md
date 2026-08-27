# FG-224 exact-head review 29 closure

Collected at `2026-08-26T23:59:13Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `104a8b785d0d4c3352d1eac94baedb1539bb4433`
- Base tree: `352ef788dc42423baa4a6af220dd03af63e4e831`
- Candidate product tree: `ded6ed9532b7fb71a6fb02f9346c9a060fe410b2`
- Candidate-diff SHA-256: `354e1941e987d33cd610b9018eb8c04edbd537a2c35512f2d8a22641b800c923`
- Delta: 6 files changed, 90 insertions, 7 deletions
- .NET SDK: `10.0.301`

The complete product and documentation delta was staged before snapshotting.
`candidate.diff` is the index-versus-HEAD diff and the candidate product tree is
derived from `git write-tree`. This evidence directory is output and is
intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5035951043` on base commit `104a8b7` found a finalization race:
after natural child exit and complete drain, cancellation could commit before the
worker's final control refresh. The worker then forced reconciliation instead of
letting `PublishTerminal` perform its locked completion/cancellation arbitration.

The closure preserves the exact safety boundary:

- `NaturalTerminalAllowed` is reached only after natural leader exit, verified
  process extinction, and a complete terminal event drain, while cancellation,
  shutdown, and lease loss were false through that boundary.
- After the forced final refresh, a newly observed cancellation alone proceeds
  to the journal terminal read and exact fenced `PublishTerminal` call. The
  Store's build-row lock publishes effective result `aborted` when cancellation
  committed first; publication-first remains terminal and a later cancellation
  observes that terminal result.
- Shutdown or lease loss at the final refresh still requires reconciliation.
  Cancellation concurrent with lease loss cannot override `lease_lost`, and an
  incomplete or authority-lost final drain never reaches terminal arbitration.
- Deterministic Worker coverage pins unchanged completion, late cancellation,
  shutdown with or without cancellation, and lease loss with or without
  cancellation. Store coverage binds aborted attempt/build truth, one aborted
  terminal event/outbox pair, and zero reconciliation event/outbox publication.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative gate with legitimate FG-094 inputs | PASS, 908/908 main-suite tests; every blocking proof; final `OK` | `fg224-review29-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review29-runnable-proof.log` |
| Differential | PASS, 274/274 including final-refresh terminal-action precedence | `tests-Fogell.Differential.Tests.log` |
| Store | PASS, 91/91 including locked cancellation-before-publication arbitration | `tests-Fogell.Store.Tests.log` |
| Sealer Release build | PASS, zero warnings and zero errors | `build.log` |
| Authoritative full-gate build | PASS, one existing FS0040 parser initialization warning and zero errors | `fg224-review29-final-gate.log` |

The 908 main-suite tests are Controller API 34, Differential 274, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 91. This
receipt checksum-binds the eight `tests-*.log` summaries, `candidate.diff`, and
the staged tree inventory together.

The authoritative gate log SHA-256 is
`1732b0c4cf461ed42891f5e685ab0ccf7321135e99e19b5e25ef52813e6eb5e7`.
The runnable proof SHA-256 is
`41328e5cb0e9e6b7bae131e6abdfd76b4e0e94dd7bb355b723125e0c51ea9d3a`.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
