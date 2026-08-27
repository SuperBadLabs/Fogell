# FG-224 pagination final closure

Collected at `2026-08-26T15:18:59Z` on HeMan from
`/home/srikanth/projects/fogell-worktrees/fg-223-runnable-controller`.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `991c1cded150e967b75ce19806ad6ecee08e18e9`
- Base tree: `05753c3fcaa6f84f3396a5173d42924a0589a980`
- Candidate-diff SHA-256: `9c2719ef745925f924da2ccd7f1bbe94409c288ef4c1658f768678224b3a619f`
- Delta: 6 files changed, 669 insertions, 56 deletions
- Host: `Linux 7.0.0-30-generic x86_64 GNU/Linux`
- .NET SDK: `10.0.301`

`candidate.diff`, `diffstat.txt`, `status-before-commit.txt`,
`base-commit.txt`, `base-tree.txt`, and `tree.txt` bind the staged, reviewed
candidate tree. The evidence directory is output and is intentionally absent
from the candidate diff and pre-seal status. No commit or push occurred during
collection.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative locked gate with legitimate FG-094 inputs | PASS, 867/867 main-suite tests; final `OK` | `fg224-pagination-final-gate.log` |
| Runnable controller materialization poison | PASS; conflicting Jenkinsfile quarantined, ownership cleared, zero poison logs, later FIFO build executed | `fg224-pagination-final-runnable-proof.log`, `candidate.diff` |
| Hostile FG-085a backup proof | PASS; 15 unique byte-changing mutants killed | `fg224-pagination-final-backup-live-postgresql.log` |
| Live PostgreSQL restore | PASS; server major 16; build-wide log cursor fixture round-tripped | `fg224-pagination-final-backup-live-postgresql.log` |
| FG-031/032 containment | PASS, 14/14 | `fg224-pagination-final-containment-proof.log` |
| Controller/build environment isolation | PASS; live boundary plus 11 planted refusals | `fg224-pagination-final-control-env-proof.log` |
| Live inherited-HOME parallel fold | PROVEN tier 1, 1/1 including workspace; partial 0/1 | `parallel-inherited-env-fold-tier1.log`, `parallel-inherited-env-fold.receipt.txt` |

The 867 main-suite tests are Controller API 23, Differential 261, Domain 34,
Execution 94, Groovy 224, Journal 31, Pipeline Parser 120, and Store 80. The
eight `tests-*.log` files are extracted from the fresh exact-candidate full-gate
run by the fail-closed sealer. `build.log` and `corpus-gate.log` are fresh
exact-tree convention checks; the build has zero warnings and errors and the
corpus is 228/228.

The live PostgreSQL 16 drill sealed archive
`2f2268bc5008e1c763925e7c460602d11fded292628225f123ef791e063bad90`,
schema `cf8e7673dc0fa0d0893f45f12cbf12e63d8849d3e8c912de7c1b95db0dc32a43`,
data `b6ebabb8624a14716c9d7133b647947532e846c4e45e79b9051151321d0c6373`,
and sequences `572c919ecc6ff689513b8df0bfab59fb275936ed35f8d87c10006d892d550bc5`.

The live receipt binds Jenkins core `2.568.1`, case digest
`e5a074d0798be112a511e275650926224240439173fe78090c48bcae6c1de29d`,
seal `6cc89ce94a13886a2c9460a05bd473a43d32ea400dead2dd46716c0eae28b7ad`,
and equal empty-workspace hashes. Its six comparison notes disclose four
deciding `${HOME}` folds, and the receipt contains no raw build HOME path.

## Command provenance

The unchanged full gate ran on the exact candidate with:

```sh
FOGELL_TEST_DATABASE_URL='Host=127.0.0.1;Port=55440;Username=fogell;Database=fogell_pagination_closure' \
FOGELL_REGRESSION_BASELINE=/home/srikanth/projects/fogell-baselines/fg-094-candidate/baseline.json \
FOGELL_JENKINS_ORACLE=/home/srikanth/projects/ghenghis/chengis/test/resources/jenkins-oracle/Jenkins-RAnvil-Chengis-228-projects.tsv \
./scripts/build-and-test.sh
```

Focused proofs ran repository scripts `prove-runnable-controller.sh`,
`prove-backup-restore-drill.sh`, `backup-restore-drill.sh`, and
`prove-control-env-isolation.sh`, plus the Execution test-list filter
`FG-031/032 process group containment`. The live differential used the current
strict v2 workspace collector and environment/wipe commands from
`scripts/run-differential.sh`, then ran only
`parallel-inherited-env-fold.Jenkinsfile` into a fresh `/tmp` receipt directory.
The backup proof was rerun after its fixture was extended with the migration-0008
`build_sequence` and consistent `next_log_sequence`; both hostile and genuine
PostgreSQL lanes passed on that exact script.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. It was verified before publication and again after the atomic rename.
Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
