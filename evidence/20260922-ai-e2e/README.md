# AI feedback loop: integrated pilot evidence

This campaign exercises an AI development loop through the real authenticated
F# client, Controller.Host, worker, PostgreSQL and Run.Host: submit captured
source, receive progressive feedback and typed diagnostics, correct the source,
and verify a fresh successful execution. Jenkins syntax is the workload input;
the acceptance evidence concerns feedback, reproducibility, bounded retention
and recovery.

**Local end-to-end acceptance passed.** Final contracts, 30 correction loops
and paired recovery were rerun after the artifact-stream lifetime correction
and recovery inventory-checker strengthening. The complete repository gate
passed with exit 0 and final `OK`; disposable deployment cleanup passed. Earlier
campaigns and the interrupted pre-review gate remain preserved separately.

## Evidence index

| Evidence | Scope and current status |
| --- | --- |
| [Final contracts](contracts-final-reviewed/receipt.json) | 16 checks passed over seven watched builds plus the two-build M1 rerun |
| [Final pilot](pilot/receipt.json) | 30 complete correction loops, 60 submissions, zero unexpected failures or censored runs |
| [Final recovery](recovery/receipt.json) | 12.481 seconds through restored artifact access and a fresh correction loop; zero lost backed-up rows/files |
| [Reviewed retention](../20260922-retention/revised/receipt.json) | 23 PostgreSQL/filesystem tests passed in each of 3 repetitions; 69 executions, zero skips |
| [Classification ordering](../20260922-retention/revised/classification-order.log) | Existing Store classification/restore ordering regression passed after migration correction |
| [Upgrade summary](upgrade-summary.json) | Final migration 0017 interruption runs 9–11 passed; 4 deliberately changed inventory controls rejected per run |
| [Rollback v2](upgrade-rollback-v2.log) | 0016 → 0017 → restore 0016 → 0017 passed, including 4 foreign-key reconstruction phases |
| [Full repository gate](validation.json) | Uninterrupted final run: 1,385 tests, zero skips/failures/errors, all required proof lanes; [raw log](full-gate.log) |
| [Cleanup](cleanup.json) | Owned runtime stopped; exact container and private token/state root removed; shared images preserved |

Source and binary hashes in each receipt bind the tested working tree and
runtime. [candidate-sources.json](candidate-sources.json) binds 289 production,
build, test and proof files, all unchanged through the complete gate. This is
local acceptance evidence; no publication, merge or release is claimed.
The final reviewed runner closure is
`548c88fbd58e3fb6463923b959db8cf232eedc499b3089f103d398810344e0f4`;
[pilot/declaration.json](pilot/declaration.json) records final deployment identity.

## Measured condition and denominator

The pilot profile uses one trusted local Linux x64 worker, concurrency 1,
fresh submitted source/workspaces/build outputs, and a prepopulated locked NuGet
cache. The recorded host is an AMD Ryzen 9 9950X3D with 32 logical CPUs and
approximately 123 GiB RAM, Linux 7.0.0-30, SDK 10.0.301, runtime 10.0.12 and
PostgreSQL 16.14. Controller polling is 50 ms, client polling 100 ms, and the
controller storage pool is disabled. The declaration records binary, workload
source and package-cache file hashes before measurement.

Each of 30 loops submits a changed expectation in an existing Fogell Domain
test, then submits the original source. Each submission builds the actual Domain
library and runs all 41 Domain tests. Expected results are one named test failure
and `unstable`, followed by zero failures and `success`; both must have zero
skips. The 60 submissions include the 30 deliberate failures. Unexpected failures
and censored observations are recorded separately and cannot disappear from the
denominator. JUnit artifacts and SDK identity are checked for every run.

The final campaign had 30/30 complete loops, 60/60 verified
submissions, zero unexpected failures and zero censored runs. Nearest-rank
correction-loop p50/p95 were **8.754/9.693 seconds** across 30 loops. First typed
actionable diagnostic p50/p95 were **4.120/4.641 seconds** across the 30 planted
failures. Total campaign time was **305.993 seconds**. These are measured results for the declared development
profile, not a production SLO.

Loop duration includes packaging both snapshots, both submissions and watches,
and artifact verification. Diagnostic latency starts at submission process
launch and includes client startup, transport and polling. Inter-loop retention
sweeps are excluded from loop percentiles and included in total campaign time.
No cold dependency-download, cold operating-system cache, multiworker or remote
network result is inferred. The acceptance ceilings were 120 seconds per build,
90 minutes per campaign, and a 240-second loop p95.

## Public contracts and source reproduction

The [final reviewed contract receipt](contracts-final-reviewed/receipt.json)
passed 16 checks over seven watched builds plus a separate two-build M1
failure/correction rerun. It covered typed shell exit-7 diagnostics, a planted
JUnit failure with test/source identity, incorrect runner-pin reconciliation,
exact-key replay, changed-manifest conflict, byte-identical source download,
and reproduction in a fresh workspace after changing the local source.
Missing optional runner pins retained a null requested pin and a truthful
observed identity. Raw snapshot bytes submitted as a plain pipeline were refused.

Cancellation was acknowledged and reached `reconciliation_required` with a typed
`build_cancelled` infrastructure diagnostic and client exit 5. It did **not**
establish an `aborted` terminal outcome. Both cancellation and incorrect-pin
reconciliation evidence remain protected from retention. The final rerun
preserved these distinctions. Small fixture diagnostic observations are individual
contract timings, not pilot percentile samples.

## Bounded retention and measured capacity

The [revised focused proof](../20260922-retention/README.md) tests resumable
selection/deletion across seven interruption boundaries, preservation of active,
reconciliation and retry evidence, competing sweeps, identity changes, symlinks,
inventory bounds and unsafe CLI options. Inventory includes the real build HOME
and stash roots. Migration 0017 was revised to remove a redundant effect write
trigger that regressed existing classification/restore lock ordering; final
upgrade receipts bind that corrected migration.

The final live pilot swept after each five loops: six phases, three sweeps
per phase, at most 1,024 deletion operations per sweep, zero held records and
convergence to zero pending work. Policy kept two eligible builds, with a 64 MiB
logical-byte ceiling and one-day age ceiling. The first phase expired eight builds, reducing the state-file inventory from
**64.189 MB to 12.846 MB**. Each later phase expired ten builds and reduced that
inventory from approximately **77.025 MB to 12.846 MB**; 58 builds expired in total.
The workload wrote a 64 KiB HOME file and stashed its reports, so these paths
contributed to real capacity recovery.

Receipt fields named `physical_bytes_before/after` sum regular-file `st_size`.
They measure remaining state-file content bytes, **not** allocated filesystem
blocks, free disk space or PostgreSQL relation size. Database payload deletion
and logical accounting do not establish immediate PostgreSQL file shrinkage.
Retained metadata and protected evidence are outside eligible payload reclamation.
Oversized inventories and identity mismatches refuse or hold rather than delete
unverified paths. Concurrent retry or legacy-artifact adoption may contend with
retention's row locks and cause a bounded transaction refusal; the focused
competing-sweep test does not establish zero-refusal liveness for those overlaps.

## Paired recovery and interrupted upgrades

The final paired recovery passed in **12.481 seconds**, against the declared
120-second target, with zero lost backed-up rows/files. Its clock began **after**
writer quiescence, backup creation and negative controls. It included restoring a
new database/state pair, comparing inventories, advancing the restore epoch,
rejecting stale log/terminal publication, starting the restored controller,
retrieving an original artifact and completing a fresh failure/correction loop.
It excludes outage detection, operator response, quiescence and backup time.
The final run uses canonical complete JSON rows from every user table, including
multiline cells, duplicate rows and empty relation inventories, plus sequence
value/call state; it preserves PostgreSQL canonical JSONB numeric precision. RPO is relative to the quiesced backup boundary, not continuous replication.

The earlier recovery preflight correctly refused stale .NET diagnostic FIFOs and
a socket in the private temporary directory. A separate cleanup receipt records
only the three volatile endpoints whose process was gone; the complete inventory
was rerun. Altered archive bytes, a changed state file and a missing state file
were rejected. The recovery proof restores into a fresh owned database/state root;
it does not overwrite a running deployment.

[Upgrade details](UPGRADE.md) describe three final independent databases in runs
9–11. An exact backend was terminated after migration SQL and ledger insertion
but before commit. Schema, application data, complete ledger and sequence state
remained unchanged. The compiled migrator then succeeded and repeated idempotently.
The separate rollback drill restored the previous database archive and migrated
forward again. These are database interruption/restore proofs, not in-place down
migration, autonomous failover or arbitrary storage-hardware crash certification.
Each upgrade drill confirmed deletion of its owned database.

## Preserved failed and superseded attempts

- `contracts/`: malformed XML quoting in the JUnit fixture; checker refused the
  missing typed test identity. The fixture was corrected.
- `contracts-source/`: unsupported one-line pipeline separators; admission refused.
- `contracts-source-2/`: the harness incorrectly expected raw snapshot bytes to
  admit as a plain pipeline; the controller safely refused. The assertion changed
  to require refusal.
- `contracts-source-3/`: the harness incorrectly expected cancellation to be
  `aborted`; subsequent evidence reports explicit reconciliation honestly.
- `contracts-final/` and `contracts-final-candidate/`: successful earlier candidate
  contracts, superseded by the final controller correction.
- `preflight/smoke-1/`: failed with an artifact HTTP 404; `smoke-2/` passed the
  corrected preflight. Neither contributes to the 30-loop timing denominator.
- `pilot-before-final-review/` and `recovery-before-final-review/`: successful prior
  campaigns, retained when the final review required a rerun. The latter contains
  the failed special-file preflight and its narrowly scoped cleanup receipt.
- Original retention receipts: passed the earlier focused suite but omitted HOME/
  stash roots and preceded the classification-order correction. Use `revised/`.
- `upgrade-1/`: container-runtime lookup failed before database creation.
  `upgrade-2/` through `upgrade-5/` passed earlier checker revisions;
  `upgrade-6/` through `upgrade-8/` and `upgrade-rollback.log` passed the prior
  migration candidate. Final evidence is runs 9–11 and rollback v2.

The earlier M1-only campaign is retained separately in
[20260922-feedback-loop](../20260922-feedback-loop/README.md), including its setup
failure and measured-harness/post-measurement cleanup-correction provenance.

## Scope and reproduction

This supports a declared development pilot for trusted workloads. It does not
certify untrusted multi-tenant process isolation, unattended retention scheduling,
unlimited history, broad Jenkins compatibility, external tool hermeticity,
hardware-independent latency, or multiworker availability. Source bytes and
runner identity are recorded; external services, all host utilities and ambient
machine state are not captured in the source envelope. The complete repository
gate and owned-resource cleanup both passed; publication and release approval
remain separate from this local pilot.

Use the [pilot runbook](../../docs/runbooks/self-hosted-pilot.md),
[retention runbook](../../docs/runbooks/retention.md),
[source snapshot runbook](../../docs/runbooks/source-snapshots.md), and
[contract reproduction instructions](contracts-final-reviewed/README.md).
The executable proof entry points are `scripts/prove-self-hosted-pilot.py`,
`scripts/prove-ai-feedback-contract.py`, `scripts/prove-paired-recovery.py` and
`scripts/prove-interrupted-upgrade.py`. These require explicitly disposable,
configured resources; the retained receipts are evidence, not authorization to
operate on another deployment.
