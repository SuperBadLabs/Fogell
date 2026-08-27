# FG-224 final review closure

Collected at `2026-08-26T13:05:05Z` on HeMan from
`/home/srikanth/projects/fogell-worktrees/fg-223-runnable-controller`.

## Reviewed pre-commit candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `aeecaa404555fea1a7fad0f4ecccfa0ff1dd0355`
- Base tree: `abda5d4bfbe15d1e4cc28a53a8a6d8ba0ca7442a`
- Candidate-diff SHA-256: `b8dabcfeccfb7bd4b886a127742b80d7c86282908e3ec82def4e96a111a2697a`
- Delta: 7 files changed, 88 insertions, 12 deletions
- Host: `Linux 7.0.0-30-generic x86_64 GNU/Linux`
- .NET SDK: `10.0.301`

`candidate.diff`, `diffstat.txt`, `status-before-commit.txt`, `base-commit.txt`,
`base-tree.txt`, and `tree.txt` bind the exact reviewed dirty tree. The evidence
directory itself is output and is intentionally absent from `candidate.diff`.
No commit or push was performed while collecting this bundle.

## Closure results

| Proof | Result | Bound artifact |
| --- | --- | --- |
| Authoritative locked full gate | PASS, 857/857 main-suite tests, final `OK` | `fg224-final-review-literal-final-gate.log` |
| Runnable controller | PASS | `fg224-final-review-runnable-proof.log` |
| Hostile FG-085a backup proof | PASS, 15 unique byte-changing mutants killed | `fg224-final-review-backup-live-postgresql.log` |
| Live PostgreSQL restore drill | PASS, server major 16 | `fg224-final-review-backup-live-postgresql.log` |
| FG-031/032 containment | PASS, 14/14 | `fg224-final-review-containment-proof.log` |
| Controller/build environment isolation | PASS, live boundary plus 11 planted refusals | `fg224-final-review-control-env-proof.log` |
| Live Jenkins inherited-HOME fold | PROVEN tier 1, 1/1 including workspace; partial 0/1 | `parallel-inherited-env-fold-tier1.log`, `parallel-inherited-env-fold.receipt.txt` |

The 857 main-suite tests are Controller API 22, Differential 259, Domain 34,
Execution 94, Groovy 224, Journal 31, Pipeline Parser 120, and Store 73. The
eight `tests-*.log` files are fresh exact-tree reruns produced while sealing;
`build.log` and `corpus-gate.log` are fresh convention-compatible reruns.

The live Jenkins receipt binds Jenkins core `2.568.1`, case digest
`e5a074d0798be112a511e275650926224240439173fe78090c48bcae6c1de29d`,
seal `6cc89ce94a13886a2c9460a05bd473a43d32ea400dead2dd46716c0eae28b7ad`,
and equal empty-workspace hashes
`e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
Its six comparison notes disclose the four deciding `${HOME}` folds.

## Exact command provenance

The authoritative full gate ran with the persisted FG-094 inputs:

```sh
FOGELL_REGRESSION_BASELINE=/home/srikanth/projects/fogell-baselines/fg-094-candidate/baseline.json \
FOGELL_JENKINS_ORACLE=/home/srikanth/projects/ghenghis/chengis/test/resources/jenkins-oracle/Jenkins-RAnvil-Chengis-228-projects.tsv \
./scripts/build-and-test.sh
```

The remaining proofs ran as:

```sh
./scripts/prove-runnable-controller.sh
./scripts/prove-backup-restore-drill.sh
FOGELL_CONTAINER_RUNTIME=podman FOGELL_PG_CONTAINER=fogell-pg \
  ./scripts/backup-restore-drill.sh
tests/Fogell.Execution.Tests/bin/Release/net10.0/Fogell.Execution.Tests \
  --filter-test-list "FG-031/032 process group containment" --summary
./scripts/prove-control-env-isolation.sh
```

For the live differential check, the Jenkins URL/core, remote environment
collector, strict v2 workspace collector, git-version collector, and wipe command
were taken verbatim from lines 7-79 of `scripts/run-differential.sh`; then this
single case was run:

```sh
dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  http://127.0.0.1:18099 2.568.1 "$receipt_dir" \
  differential/cases/parallel-inherited-env-fold.Jenkinsfile
```

The exact output log records the receipt directory and exit code 0. No credential
values are included in this bundle. `docs/tickets/FG-224.md` did not point to the
review-10 bundle, so the conditional evidence-reference replacement did not apply.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this directory.
Verify standalone with:
