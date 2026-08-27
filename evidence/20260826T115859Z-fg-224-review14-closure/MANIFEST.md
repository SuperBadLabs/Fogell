# FG-224 exact-head review 14 closure

Collected at `2026-08-26T17:24:24Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `7a723835803d1f962561a3bba700395be271dec7`
- Base tree: `2640751bf30542ab3c66ba46748db2f576933114`
- Candidate tree: `851c164ec6b6517a20e548ed0893f34ce8745df9`
- Candidate-diff SHA-256: `d70919d038e33e8db96927e78a72370546f17b68ce52fd2d12ccb5dc623e3ef5`
- Delta: 11 files changed, 313 insertions, 37 deletions
- .NET SDK: `10.0.301`

The complete audited delta was staged before snapshotting. `candidate.diff`
is the index-versus-HEAD diff and `tree.txt` is derived from `git write-tree`.
The new `WorkerLaunch.fs` implementation is recorded as an added file, appears
in full in `candidate.diff`, and has candidate-tree blob
`25a5755b5681117fe318853199f2c38d104879de`. Its working and tree-extracted
SHA-256 values both equal
`12d23b7ba4a6a8918bda66c551b901e3a65dc5408b0dd64494e2be71ee2ad8ff`.

The evidence directory is output and is intentionally absent from the
candidate tree. No commit or push occurred during collection.

## Review closure

Codex review `5033061073` on the base commit reported two new findings. Both
are closed in this candidate:

- P1: every EOF-watchdog cleanup grace delay now invokes absolute
  `/bin/sleep`. A live regression supplies a blocking build-controlled `sleep`
  through `PATH`, proves a TERM-resistant effect is alive, kills the owner,
  observes KILL/reap, and proves the hostile executable was never invoked.
- P2: the exact `/usr/bin/setsid` identity is validated with effective-service-
  identity execute access before bind, carried into `ProcessStartInfo`, and
  rechecked at readiness plus pre- and post-claim boundaries. A false or thrown
  `Process.Start` after `BeginExecution` synchronously invokes fenced
  reconciliation rather than waiting for lease expiry.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative locked gate with legitimate FG-094 inputs | PASS, 876/876 main-suite tests; final `OK` | `fg224-review14-final-gate.log` |
| Dependency locks | PASS, 26/26 projects; seven mutations rejected; source-cleared locked restore and no-restore build pass | `fg224-review14-final-gate.log` |
| Runnable controller | PASS; pre-admission capability refusal, refused-key reuse, poison FIFO progress, retry journals, deterministic resume, and finite post-exit drain over 16 MiB | `fg224-review14-runnable-proof.log` |
| Hostile FG-085a backup proof | PASS; 15 unique byte-changing mutants killed | `fg224-review14-backup-live-postgresql.log` |
| Live PostgreSQL restore | PASS; server major 16 | `fg224-review14-backup-live-postgresql.log` |
| FG-031/032 containment | PASS, 15/15 including hostile PATH | `fg224-review14-containment-proof.log` |
| Controller/build environment isolation | PASS | `fg224-review14-control-env-proof.log` |
| Live inherited-HOME parallel fold | PROVEN tier 1, 1/1 including workspace; partial 0/1 | `fg224-review14-parallel-inherited-env-fold-tier1.log`, `fg224-review14-parallel-inherited-env-fold.receipt.txt` |

The 876 main-suite tests are Controller API 27, Differential 265, Domain 34,
Execution 95, Groovy 224, Journal 31, Pipeline Parser 120, and Store 80. The
eight `tests-*.log` files are extracted by the fail-closed sealer from the
exact candidate. `build.log` records zero warnings and zero errors, and
`corpus-gate.log` records 228/228 pinned corpus files.

The live PostgreSQL 16 drill sealed archive
`c95fc8434bad540410e5f1719ff1e61cf2decbf770afa70553ee87e2a7cbb3d4`,
schema `cf8e7673dc0fa0d0893f45f12cbf12e63d8849d3e8c912de7c1b95db0dc32a43`,
data `b6ebabb8624a14716c9d7133b647947532e846c4e45e79b9051151321d0c6373`,
and sequences
`572c919ecc6ff689513b8df0bfab59fb275936ed35f8d87c10006d892d550bc5`.

The live differential receipt binds Jenkins core `2.568.1`, case digest
`e5a074d0798be112a511e275650926224240439173fe78090c48bcae6c1de29d`,
and seal `6cc89ce94a13886a2c9460a05bd473a43d32ea400dead2dd46716c0eae28b7ad`.

## Command provenance

The full gate used the legitimate external FG-094 baseline, its digest-matching
228-project Jenkins oracle, and a fresh PostgreSQL 16 database. Focused proofs
ran the repository runnable-controller, backup/restore, containment, and
control-environment paths. The live differential ran only
`parallel-inherited-env-fold.Jenkinsfile` with the strict v2 collectors.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. It is verified before publication and after the final atomic rename.
Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
