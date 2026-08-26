# FG-224 readiness final closure

Collected at `2026-08-26T14:13:05Z` on HeMan from
`/home/srikanth/projects/fogell-worktrees/fg-223-runnable-controller`.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `30ebfbad72585470bde79db8c409fc48d37b78d8`
- Base tree: `2aa03d3bc8ca8846d0158c674b44710dc0b72f65`
- Candidate-diff SHA-256: `30b7296a180e51a03457646090c4642dd204f2f93b5ec2b1a886bb5c202def8f`
- Delta: 6 files changed, 263 insertions, 35 deletions
- Host: `Linux 7.0.0-30-generic x86_64 GNU/Linux`
- .NET SDK: `10.0.301`

`candidate.diff`, `diffstat.txt`, `status-before-commit.txt`,
`base-commit.txt`, `base-tree.txt`, and `tree.txt` bind the reviewed dirty
tree. The evidence directory is output and is intentionally absent from the
candidate diff and pre-seal status. No commit or push occurred during collection.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative locked gate with legitimate FG-094 inputs | PASS, 861/861 main-suite tests; final `OK` | `fg224-readiness-final-gate.log` |
| Runnable controller materialization poison | PASS; conflicting Jenkinsfile quarantined, ownership cleared, zero poison logs, later FIFO build executed | `fg224-readiness-final-runnable-proof.log`, `candidate.diff` |
| Hostile FG-085a backup proof | PASS; 15 unique byte-changing mutants killed | `fg224-readiness-final-backup-live-postgresql.log` |
| Live PostgreSQL restore | PASS; server major 16 | `fg224-readiness-final-backup-live-postgresql.log` |
| FG-031/032 containment | PASS, 14/14 | `fg224-readiness-final-containment-proof.log` |
| Controller/build environment isolation | PASS; live boundary plus 11 planted refusals | `fg224-readiness-final-control-env-proof.log` |
| Live inherited-HOME parallel fold | PROVEN tier 1, 1/1 including workspace; partial 0/1 | `parallel-inherited-env-fold-tier1.log`, `parallel-inherited-env-fold.receipt.txt` |

The 861 main-suite tests are Controller API 22, Differential 261, Domain 34,
Execution 94, Groovy 224, Journal 31, Pipeline Parser 120, and Store 75. The
eight `tests-*.log` files are extracted from the exact full-gate run.
`build.log` and `corpus-gate.log` are fresh exact-tree convention checks; the
build has zero warnings and errors and the corpus is 228/228.

The live receipt binds Jenkins core `2.568.1`, case digest
`e5a074d0798be112a511e275650926224240439173fe78090c48bcae6c1de29d`,
seal `6cc89ce94a13886a2c9460a05bd473a43d32ea400dead2dd46716c0eae28b7ad`,
and equal empty-workspace hashes. Its six comparison notes disclose four
deciding `${HOME}` folds, and the receipt contains no raw build HOME path.

## Command provenance

The unchanged full gate ran with:

```sh
FOGELL_REGRESSION_BASELINE=/home/srikanth/projects/fogell-baselines/fg-094-candidate/baseline.json \
FOGELL_JENKINS_ORACLE=/home/srikanth/projects/ghenghis/chengis/test/resources/jenkins-oracle/Jenkins-RAnvil-Chengis-228-projects.tsv \
./scripts/build-and-test.sh
```

Focused proofs ran the repository scripts `prove-runnable-controller.sh`,
`prove-backup-restore-drill.sh`, `backup-restore-drill.sh`, and
`prove-control-env-isolation.sh`, plus the Execution test-list filter
`FG-031/032 process group containment`. The live differential used the current
strict v2 workspace collector and environment/wipe commands from
`scripts/run-differential.sh`, then ran only
`parallel-inherited-env-fold.Jenkinsfile` into a fresh `/tmp` receipt
directory.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. It was verified before publication and again after the atomic rename.
Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
