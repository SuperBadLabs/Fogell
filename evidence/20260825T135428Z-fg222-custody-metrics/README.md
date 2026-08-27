# FG-222 custody completion, test, and benchmark evidence

Collected on 2026-08-25 from HeMan. This bundle binds the evidence to the
signed source candidate rather than asking a later reader to trust ticket prose.

## Candidate identity

- commit: `20ec7cbc8171be8dd7b0e6f1c2df15e7abe65406`
- tree: `faa37710ea6bfcf44fed56e92554ff54c1a0ac99`
- subject: `security: isolate build environment authority`
- signature: good ED25519 signature for `srikanth.remani@gmail.com`, key
  `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`

`gate/before.txt` and `gate/after.txt` contain the same commit and tree. The
authoritative log is `gate/full-gate.log`; its SHA-256 is
`bba8c64fa776e0c63ecba5b769786b10bb767b01849d6dfc669c9a7bede46f09`.

## Authoritative gate

The exact command, from the candidate worktree, was:

```sh
env \
  FOGELL_TEST_DATABASE_URL='Host=127.0.0.1;Port=55443;Username=fogell;Database=fogell' \
  FOGELL_CORPUS=/sn8100/work/exchange/crucible-gate/corpus \
  FOGELL_REGRESSION_BASELINE=/home/srikanth/projects/fogell-baselines/fg-094-candidate/baseline.json \
  FOGELL_JENKINS_ORACLE=/home/srikanth/projects/ghenghis/chengis/test/resources/jenkins-oracle/Jenkins-RAnvil-Chengis-228-projects.tsv \
  FOGELL_STALE_REF_BASE=origin/main \
  ./scripts/build-and-test.sh
```

The gate terminated `OK`. Its primary test matrix was 784/784, with no ignored,
failed, or errored tests:

| Project | Tests | Expecto time |
|---|---:|---:|
| Fogell.Controller.Api | 18 | 0.228 s |
| Fogell.Differential | 218 | 4.144 s |
| Fogell.Domain | 34 | 0.076 s |
| Fogell.Execution | 84 | 14.426 s |
| Fogell.Groovy | 224 | 0.191 s |
| Fogell.Journal | 31 | 0.239 s |
| Fogell.Pipeline.Parser | 120 | 0.248 s |
| Fogell.Store | 55 | 0.696 s |

The Controller and Store projects produced real PostgreSQL-backed summaries;
neither silently skipped. The primary Expecto durations sum to about 20.25 s,
but they are functional-test timings, not a performance benchmark.

Additional blocking results in the same log include:

- FG-207 durability subset: 8/8;
- committed receipt seals: 283/283 recomputed and valid;
- private compatibility gate: 228 files, accepted 200 to 200, tier 1 1 to 1;
- FG-222 live environment boundary PASS, with all 11 planted mutations rejected;
- restart lane and approval lane: all assertions passed;
- sandbox, claim/citation, stale-reference, section-refusal, board, queue,
  review-coverage, release-provenance, scorecard-mapping, compatibility-mutant,
  seal-mutant, and inbox-watcher proofs passed.

`gate/differential-stress.log` retains ten consecutive 218/218 Differential
runs. This satisfies the default-parallel repetition requirement without
turning one green run into a stability claim.

There is no Coverlet, dotnet-coverage, LCOV, or Cobertura configuration and no
line/branch coverage threshold in this source tree. There is also no standard
mutation-testing score. The planted-mutant proof lanes above are direct,
scenario-specific evidence and are not reported as an aggregate mutation rate.

## Pinned Jenkins oracle

The full 275-case run emitted 282 receipt builds: 278 were tier-1 PROVEN and four
were DIVERGED. `oracle/fogell-fg222-full-282-receipts.tar.gz` retains the complete
receipt population, and `oracle/fogell-fg222-full-verdicts.tsv` is its compact
verdict index. The archive contains exactly 282 receipt files and has SHA-256
`453aca4210afde2a02090e0a25a05c04ccd6c3d7999b094c179a3780e102cfe5`;
recounting the index gives 278 PROVEN and four DIVERGED. `oracle/full-residuals`
also exposes the four first-run residual receipts without requiring extraction.

`oracle/residual-retained/run.log` is a fresh targeted rerun of those four cases.
The harness retried every divergence three times and every attempt stayed
diverged. In all four final receipts Jenkins and Fogell have the same successful
result and byte-equal output. Fogell alone retains one empty directory:

| Case | Fogell-only empty directory |
|---|---|
| `fg177-wrapper-body-result` | `fg177-body-result-dir/` |
| `post-exit-env-read-fault` | `s/` |
| `script-closure-mutates-enclosing` | `sub/` |
| `script-return-is-closure-local` | `sub/` |

The targeted command is reproducible with `oracle/targeted-runner.sh`; its
expected exit is nonzero because the command requires every case to be fully
proven. This bundle preserves the receipts and console output rather than
relabeling that expected failure as green.

The three HOME-dependent cases are present under `oracle/home-occurrence`,
`oracle/targeted`, and `oracle/home-repeat`. Each is tier-1 PROVEN, and each
receipt is byte-identical across all three fresh runs:

- `env-inherited-output-fold`: `508f1af26cb4093d7cc03bc23ee35d5229cbd1454c0acf5ccdb604564c2f9fe9`;
- `parallel-inherited-env-fold`: `03cbbce0e114945990ef2a96d6acaa6bd2abb80dcf35706196ee203c30a55f96`;
- `xtrace-continuation-inherited`: `559dd6063de2095275121d8620f29d266a53f01cc250fd31b1514da21bf24301`.

FG-222 therefore remains PARTIAL: acceptance items 1, 2, 4, and 5 have direct
evidence, while full pinned-oracle parity remains 278/282 because of the four
empty-directory lifecycle residuals.

## Durable-path benchmark

`benchmark` is the auditor's checksum-bound compact bundle. It contains the
exact `campaign-reproduction.sh`, host/toolchain record, source signature,
synthetic Jenkinsfiles, raw GNU-time TSVs, run logs, durable journals, summary
JSON, and the archived legacy harness used only as a reference.
`benchmark-compact.tar.gz` is an exact compressed snapshot of that final
directory, including `run-host.sha256` and its 219-entry manifest; its SHA-256
is `6f0f7728d66b23b15fd5339278de91c804a00a45518cffd86be5c05415ea7b56`.

`benchmark/restore.time` and `benchmark/logs/restore.log` preserve a discarded
exit-1 preflight against the nonexistent `Fogell.sln`. The successful campaign
uses `Fogell.slnx`; the failed preflight is excluded from every metric and from
`campaign-reproduction.sh`.

The campaign built an isolated `git archive` of the signed commit. Every sample
used a unique workspace root and a controller-side journal. All measured runs
had exactly one `build-finished success`; the sequential journals have exactly
200 starts and finishes, and the parallel journals have 80 starts and finishes
plus nine stage commits.

Host context: HeMan, Ubuntu 24.04.4, kernel 7.0.0-30, Ryzen 9 9950X3D
(16 cores/32 threads), 123 GiB RAM, ext4/NVMe, .NET SDK 10.0.301/runtime
10.0.11, Git 2.43.0. Load average moved from 1.50 to 1.69 on a five-user host;
the machine was not quieted. Isolated locked restore took 1.45 s and Release
build took 11.27 s. The isolated build's Run.Host SHA-256 is retained in
`benchmark/run-host.sha256`:
`bd88358b9e74e5e3192ecb0578418096c1ec5c76fbd2c5aad84d8a3be420fb71`.

Warmups are excluded from every metric below. GNU `time %e` has 0.01 s
resolution. Percentiles use the archived harness convention: sorted samples,
zero-based index `min(n-1, floor(p*n))`; with 15 samples, p95 is the maximum.

| Lane | n | Median | Mean | CV | Range / p95 | Median max RSS | Failures |
|---|---:|---:|---:|---:|---:|---:|---:|
| FG-222 proof | 15 | 0.53 s | 0.540 s | 5.0% | 0.52-0.61 / 0.61 s | 57,868 KiB | 0 |
| Durable echo end-to-end | 15 | 0.16 s | 0.170 s | 12.4% | 0.16-0.23 / 0.23 s | 50,620 KiB | 0 |
| Durable echo base | 8 | 0.16 s | 0.174 s | 17.9% | 0.16-0.25 / 0.25 s | 50,568 KiB | 0 |
| Durable 200 x `sh true` | 8 | 0.76 s | 0.745 s | 6.7% | 0.64-0.80 / 0.80 s | 94,396 KiB | 0 |
| Durable parallel 8 x 10 | 15 | 0.31 s | 0.361 s | 39.4% | 0.27-0.72 / 0.72 s | 69,244 KiB | 0 |

The harness-style marginal sequential subprocess cost is
`(0.76 - 0.16) / 200 = 3.0 ms/step`. It numerically clears FG-197's `<8 ms/step`
threshold, but does not close FG-197: that ticket requires comparison on the
same host as the Jenkins baseline, which is Luigi rather than HeMan.

The parallel lane's 39.4% CV is too noisy to serve as a regression threshold.
No benchmark threshold gate or machine-readable baseline is committed to CI.
The legacy full-run harness was not executed because it stops and restarts five
named containers and expects Luigi-local services. These figures are a
reproducible HeMan durable-path observation, not a refreshed cross-engine
trifecta result.

## Differential inventory derivation

The compatibility-matrix counts are static file coverage, not reachability or
runtime coverage. From the signed source tree:

```sh
find differential/cases -maxdepth 1 -type f -name '*.Jenkinsfile' | wc -l
# 275

rg --pcre2 -l '\b(?:sh|git|checkout)\s*(?:\(|\x22|\x27)' \
  differential/cases --glob '*.Jenkinsfile' | sort -u | wc -l
# 227 call-shaped process/SCM files

rg --pcre2 -l '\bwithEnv\s*\(' differential/cases --glob '*.Jenkinsfile' | wc -l
# 20 files

rg --pcre2 -l '\bwithCredentials\s*\(' differential/cases --glob '*.Jenkinsfile' | wc -l
# 8 files

rg -l '^//// SCM JOB ////$' differential/cases --glob '*.Jenkinsfile' | wc -l
# 5 files

find differential/cases -maxdepth 1 -type f -name 'git-step-*.Jenkinsfile' | wc -l
# 4 files
```

The 227 value is now reproducible, but its rule is deliberately described as a
lexical coverage floor. It does not prove every matching call is dynamically
reachable.

## Integrity

`SHA256SUMS` hashes every other file in this bundle. Verify it from this
directory with `sha256sum -c SHA256SUMS`. The nested benchmark bundle also has
its own independently generated and verified `SHA256SUMS`. `gate/SHA256SUMS`
is a portable verifier for the copied gate artifacts, including the stress log;
`gate/original-SHA256SUMS` preserves the exact absolute-path manifest emitted by
the authoritative `/tmp` run.
