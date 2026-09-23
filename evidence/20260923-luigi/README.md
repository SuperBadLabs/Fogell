# Luigi deployment qualification — 2026-09-23

**PASS.** The installation is persistent and healthy on Luigi at `http://127.0.0.1:46206`.
See the [operator runbook](../../docs/runbooks/luigi-campaign.md) for SSH access,
private token-file usage and restart instructions. Acceptance is tracked by
[FG-270](../../docs/tickets/FG-270.md).

## Measured results

[summary.json](summary.json) is the passing offline aggregate, bound to the final
source gate, both upgrades, all retained failed attempts and final service health.
The [export manifest](manifest.json) verifies all 2,589 files in
[campaigns.tgz](campaigns.tgz) (11,506,027 compressed bytes); its credential scan
passed. Private backup payloads and connection environments are excluded.

| Measurement | Original v1 baseline | Final v3 qualification |
| --- | ---: | ---: |
| Complete correction loops / real builds | 100 / 200 | 30 / 60 |
| Unexpected / censored builds | 0 / 0 | 0 / 0 |
| Actionable diagnostic p50 / p95 | 12.93 / 13.38 s | 13.14 / 13.60 s |
| Failure-to-correction loop p50 / p95 | 28.55 / 29.96 s | 27.80 / 28.58 s |
| Retention phases / sweeps | 20 / 60 | 6 / 20 |
| Retention tool elapsed time | 1,361.65 s | 448.05 s |
| Sampled state content peak | 76,953,461 bytes | 81,278,026 bytes |

Loop timings exclude retention between batches. V3's supervised qualification
including retention took 1,292.60 seconds. Its retention expired 225 eligible
builds, including prior fault-campaign history; that count is not the qualification
build denominator. The versions' latency samples are not pooled.

The final release passed all 21 adversarial scenarios in 264.29 seconds: 32
concurrent unique builds; idempotency and invalid-input controls; shell failure,
timeout, typed output/artifact limits, scratch ENOSPC; three cancellations, three
runner kills, three controller crashes, and three measured 20-second PostgreSQL
outages. All 80 distinct admitted builds (95 observations including idempotency
racers) met their expected outcome checks. Queue-inclusive load p50/p95 was
16.36/28.58 seconds. Each fault's two queued controls completed successfully.
Interrupted authority remained honestly marked for reconciliation.

Paired restore passed in **40.18 seconds**, with 58.69 seconds of planned original
service downtime. The restore timer starts after quiescence, backup creation and
negative controls. Complete database/state comparison reported zero lost backed-up
rows/files; corrupted backups and stale log/terminal authority were rejected. The
restored service served the v1 artifact and completed a fresh v3 correction pair.

Final readiness was 200. Both owned user services are active and enabled; user
lingering is enabled. All five protected container IDs and exact start times
match their original observations. Both retired Fogell controllers are stopped,
and all five temporary recovery tool/controller containers are absent. Final
state content was 12,841,919 bytes, source database size 58,661,911 bytes, and
allocated PostgreSQL volume usage 280,973,312 bytes (including the retained restore
database and WAL).

## Version boundaries

The original deployment used source
`47424dedd94fdeab39e70939cc396b488d29ae30`. Its independently verified baseline
completed 100 correction loops (200 real Domain builds, 41 tests per build),
with 20 retention phases and no unexpected or censored builds. Actionable
failure-diagnostic p95 was 13.385 seconds; complete correction-loop p95 was
29.962 seconds, excluding retention between batches. Build terminal p95 was
13.715 seconds. These are warm-package-cache, fresh-workspace results with one
worker and an effective eight-CPU controller limit.

The strict adversarial campaign on that version passed 32 concurrent distinct
builds and the idempotency, invalid-input, shell-failure and timeout controls,
then failed the output-cap diagnostic check: the console named
`OUTPUT_LIMIT_EXCEEDED`, while typed feedback exposed only `RUN_FAILED`.
Subsequent destructive faults were not run on that version. The failed receipt
is retained. A preceding zero-work preflight failure caused by a Podman template
compatibility error is also retained separately.

The first production fix is commit
`31d121118ebe1888d6acc556d4ddc6e51e36e20b`. It persists the safe named
infrastructure cause through typed diagnostics, with regressions covering the
output and artifact limits and diagnostic-sink failure. Its full repository gate
passed all 1,386 tests. `validation.json`, `full-gate.log` and `gate-source.json`
bind that result to the source. The release was built from an isolated Git
archive; `candidate.json` and `candidate-release-manifest.json` bind its files
and package hash. Operations helpers are recorded separately in the staging
manifests and are not included in the immutable application release.

The upgrade retained the previous container and release for rollback, reused the
owned database/state, and required identical migration files. Measurements on
the new release remain separate from the original baseline.

The intermediate release then passed the strict output/artifact/scratch controls,
three cancellations and three runner kills. Its first controller crash correctly
produced `reconciliation_required` after lease expiry, but no typed reason was
available in feedback. That campaign failed and is retained: further controller
repetitions and database outages were not reached. Its two initially queued
controls were not subsequently observed by the failed scenario; the final
healthy control did pass. The second production correction is `058126d3779a6897fde5edf718b958673f9ee926`:
lease expiry and restore activation publish stable typed reasons atomically with
reconciliation status, events and feedback cursors. Concurrent sweeps and repeated
restore activation cannot duplicate them. The final candidate source is
`d4a9bc76e20e60313fb0dc8e61553f697d6da19d`, which updates an existing stale-writer
test to expect the new restore diagnostic while still requiring atomic rejection.

`v3/validation.json` retains the unsuccessful first full gate: its old stale-writer
test expected an empty log after restore. `v3-final/` contains the corrected
candidate's build and subsequent gate evidence. Neither candidate was deployed
before a passing gate. The final gate passed all 1,387 project tests and all
required operational, audit and mutation proofs. Its isolated Release build had
zero warnings and errors.

## Evidence boundaries

Final acceptance includes the latest release's complete fault matrix, paired
restore, 30 separate correction loops with six retention phases, and final
healthy service/protected-container observations. Both historical executed
failures and the read-only setup failure remain visible in the aggregate.

Campaign evidence excludes private connection environments, database backups and
workspace-state payloads. The exporter checks for the installation's actual
credential bytes before packaging. Full database/state equality during recovery
is adapter-attested, with inventories and corruption controls retained; the
private backup payloads are not published.

Memory observations are cgroup usage, not process RSS. State content-byte sums
are not allocated disk usage or database/WAL size. The state storage guard is
neither a hard quota nor an unattended retention policy. This trusted-workload
profile does not qualify hostile PR isolation, cold-cache performance, host
reboot or power-loss durability.

## Reverify the export

First verify the archive digest and each extracted file against `manifest.json`.
Extract to a new directory, then run from this checkout:

```bash
python3 scripts/luigi/summarize.py --root /absolute/path/to/extracted-evidence \
  --local-validation evidence/20260923-luigi/v3-final/validation.json \
  --output /tmp/luigi-reverified.json
```

`ops-validation.json` records portable harness/verifier controls. The helper
staging manifests bind the exact scripts used on Luigi; the final application
release source is `d4a9bc76e20e60313fb0dc8e61553f697d6da19d`. Operations and
documentation committed afterward do not change that deployed source identity.
