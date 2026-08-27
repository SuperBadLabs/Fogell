# FG-224 worker fairness and terminal-drain provenance closure

Collected at `2026-08-26T16:07:26Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `824610ee6fdc64959eefe4f57cd920c99acab0a9`
- Base tree: `3bb55fa45d3cf3565c27d239f9acc446585dd9a0`
- Candidate tree: `1880079ea0ed686a329adb3fb89327b90ee35daf`
- Candidate-diff SHA-256: `aaab9dd13808c36a3a62c80102d64bbdd98401c0c85691f223107ea27926c291`
- Delta: 8 files changed, 310 insertions, 66 deletions
- .NET SDK: `10.0.301`

The complete audited delta was staged before snapshotting. `candidate.diff` is
the index-versus-HEAD diff and `tree.txt` is derived from `git write-tree`.
The newly added `WorkerScheduling.fs` is recorded as an added file, its full
implementation appears in `candidate.diff`, and its candidate-tree blob is
`5024597811f85ef2b76dad4c888cef367e6b7611`. Working and tree-extracted
SHA-256 are both
`d244a476f3fa58faac8a56cc68ca9d39ac743a1b6bde084a3be8d820c1c5d5a8`.

`diffstat.txt`, `status-before-commit.txt`, `base-commit.txt`,
`base-tree.txt`, and `tree.txt` complete the provenance record. The evidence
directory is output and is intentionally absent from the candidate tree. No
commit or push occurred during collection.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative locked gate with legitimate FG-094 inputs | PASS, 871/871 main-suite tests; final `OK` | `fg224-worker-fair-drain-final-gate.log` |
| Runnable controller | PASS; poison quarantine/FIFO progress and a finite post-exit tail over 16 MiB drained exactly once | `fg224-worker-fair-drain-runnable-proof.log` |
| Hostile FG-085a backup proof | PASS; 15 unique byte-changing mutants killed | `fg224-worker-fair-drain-backup-live-postgresql.log` |
| Live PostgreSQL restore | PASS; server major 16 | `fg224-worker-fair-drain-backup-live-postgresql.log` |
| FG-031/032 containment | PASS, 14/14 | `fg224-worker-fair-drain-containment-proof.log` |
| Controller/build environment isolation | PASS | `fg224-worker-fair-drain-control-env-proof.log` |
| Live inherited-HOME parallel fold | PROVEN tier 1, 1/1 including workspace; partial 0/1 | `parallel-inherited-env-fold-tier1.log`, `parallel-inherited-env-fold.receipt.txt` |

The 871 main-suite tests are Controller API 23, Differential 265, Domain 34,
Execution 94, Groovy 224, Journal 31, Pipeline Parser 120, and Store 80.
The eight `tests-*.log` files are extracted from the exact-candidate full-gate
run. `build.log` and `corpus-gate.log` are exact-tree convention checks; the
build has zero warnings and errors and the corpus is 228/228.

The live PostgreSQL 16 drill sealed archive
`ff4c13e79e556ecc21430a605b0b5bbedceaa8e7599df1039cb855bee9362ada`,
schema `cf8e7673dc0fa0d0893f45f12cbf12e63d8849d3e8c912de7c1b95db0dc32a43`,
data `b6ebabb8624a14716c9d7133b647947532e846c4e45e79b9051151321d0c6373`,
and sequences `572c919ecc6ff689513b8df0bfab59fb275936ed35f8d87c10006d892d550bc5`.

The live receipt binds Jenkins core `2.568.1`, case digest
`e5a074d0798be112a511e275650926224240439173fe78090c48bcae6c1de29d`,
and seal `6cc89ce94a13886a2c9460a05bd473a43d32ea400dead2dd46716c0eae28b7ad`.
Its six comparison notes disclose four deciding `${HOME}` folds, and the
receipt contains no raw build HOME path.

## Command provenance

The unchanged full gate ran on the exact working bytes with the legitimate
external FG-094 baseline and its digest-matching 228-project Jenkins oracle.
Focused proofs ran the repository runnable-controller, backup/restore,
containment, and control-environment paths. The live differential ran only
`parallel-inherited-env-fold.Jenkinsfile` with the strict v2 collectors.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. It is verified before publication and again after the atomic rename.
Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
