# FG-224 admission and retry-journal P1 closure

Collected at `2026-08-26T16:56:05Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `8216cd639a971711e5d5b8cf42a5f28278937d1c`
- Base tree: `c44f62a741c5db63b1363f8d04d45a0a5feb095f`
- Candidate tree: `fdf9015ad38a763d823b593e9b1bd0fd3ebb05c5`
- Candidate-diff SHA-256: `56e42243e4bf2e78e3fa5aa4820ee01cf12daf10dee56575d0a3c5538f6ee227`
- Delta: 12 files changed, 367 insertions, 39 deletions
- .NET SDK: `10.0.301`

The complete audited delta was staged before snapshotting. `candidate.diff`
is the index-versus-HEAD diff and `tree.txt` is derived from `git write-tree`.
The new `WorkerPaths.fs` implementation is recorded as an added file, appears
in full in `candidate.diff`, and has candidate-tree blob
`fc807a52b5fd52b78107d38073e0d6e7d031080f`. Its working and tree-extracted
SHA-256 values both equal
`be838136baa35839f5ec129be19a8c0e9a30c53ea61a72b6488175527e27e402`.

The evidence directory is output and is intentionally absent from the
candidate tree. No commit or push occurred during collection.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative locked gate with legitimate FG-094 inputs | PASS, 872/872 main-suite tests; final `OK` | `fg224-p1-closure-final-gate.log` |
| Dependency locks | PASS, 26/26 projects; seven mutations rejected; source-cleared locked restore and no-restore build pass | `fg224-p1-closure-final-gate.log` |
| Runnable controller | PASS; exact execution preflight before admission, refused-key reuse, poison FIFO progress, attempt-keyed retry journals, deterministic same-child resume, and finite post-exit drain over 16 MiB | `fg224-p1-closure-runnable-proof.log` |
| Hostile FG-085a backup proof | PASS; 15 unique byte-changing mutants killed | `fg224-p1-closure-backup-live-postgresql.log` |
| Live PostgreSQL restore | PASS; server major 16 | `fg224-p1-closure-backup-live-postgresql.log` |
| FG-031/032 containment | PASS, 14/14 | `fg224-p1-closure-containment-proof.log` |
| Controller/build environment isolation | PASS | `fg224-p1-closure-control-env-proof.log` |
| Live inherited-HOME parallel fold | PROVEN tier 1, 1/1 including workspace; partial 0/1 | `fg224-p1-closure-parallel-inherited-env-fold-tier1.log`, `fg224-p1-closure-parallel-inherited-env-fold.receipt.txt` |

The 872 main-suite tests are Controller API 24, Differential 265, Domain 34,
Execution 94, Groovy 224, Journal 31, Pipeline Parser 120, and Store 80. The
eight `tests-*.log` files are extracted by the fail-closed sealer from the
exact candidate. `build.log` records zero warnings and zero errors, and
`corpus-gate.log` records 228/228 pinned corpus files.

The admission test proves two parseable but execution-unsupported requests
return stable HTTP 422 responses, create no build, consume no idempotency key,
and allow that same key to admit and replay a supported pipeline. The retry
proof records independent parent-failure and child-success terminal journals,
one actual child shell execution, no legacy build journal, and a byte-identical
terminal no-op when that exact child is restarted.

The live PostgreSQL 16 drill sealed archive
`4daf143780c314c1c4415af30106437f620a1be1c9804b7f9713ca532c00e6c7`,
schema `cf8e7673dc0fa0d0893f45f12cbf12e63d8849d3e8c912de7c1b95db0dc32a43`,
data `b6ebabb8624a14716c9d7133b647947532e846c4e45e79b9051151321d0c6373`,
and sequences
`572c919ecc6ff689513b8df0bfab59fb275936ed35f8d87c10006d892d550bc5`.

The live differential receipt binds Jenkins core `2.568.1`, case digest
`e5a074d0798be112a511e275650926224240439173fe78090c48bcae6c1de29d`,
and seal `6cc89ce94a13886a2c9460a05bd473a43d32ea400dead2dd46716c0eae28b7ad`.

## Command provenance

The unchanged full gate ran with the legitimate external FG-094 baseline and
its digest-matching 228-project Jenkins oracle on a fresh PostgreSQL database.
Focused proofs ran the repository runnable-controller, backup/restore,
containment, and control-environment paths. The live differential ran only
