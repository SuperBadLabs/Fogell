# Fogell native runtime: Luigi test report

Date: 26 September 2026; campaign setup through cleanup, 21:15:19–21:33:43 UTC (16:15:19–16:33:43 America/Chicago). The tested candidate is **not ready for release qualification**: the existing gate passes, but independent end-to-end testing found a failed-test report persistence defect and an admission validation defect. Fast traced output also reaches a much smaller practical queue limit than the whole-build byte/record limits suggest.

## Candidate and environment

The campaign tested the current native JSON working tree, including the uncommitted removal of the previous authoring engine. It did not test the older Fogell pilot as though it were the candidate.

- Base commit: `97ef81b634a7223ebbcac9b1f240c0c214db29d1`.
- Source: 163 files, enumerated in [source-manifest.json](source-manifest.json).
- Transferred archive SHA-256: `99934f812cad0538f3010bbf3a6d2a7d9bb109f8600cfa100f5cc7ea6fc7452b`.
- Host: Luigi, Ubuntu 24.04.4, Linux `6.8.0-138-generic`, x86-64, 56 logical CPUs, 125 GiB RAM. Initial available memory was 116 GiB and available root filesystem capacity was 330 GiB.
- SDK: pinned .NET `10.0.301`, extracted into the private campaign directory from Luigi's cached SDK image. The system SDK installation was not changed.
- PostgreSQL: a uniquely named, disposable PostgreSQL 16 container bound to a dynamically allocated loopback port. Controller proofs used unique databases, restricted runtime roles, private token files and separate state roots.
- Remote campaign directory: `/tmp/fogell-native-campaign-20260926.rrPLAz`.

All workload execution, controller crashes and database corruption were confined to campaign-owned resources. The existing pilot service and its database were outside the test scope.

## Findings

### 1. High priority: failed JUnit cases break journal persistence and skip subsequent work

A valid JUnit XML report containing a failed case should make the native `test_report` step and build `unstable`. Later ordinary steps should continue, and `always` artifact publication should run. Through the real runner and controller, the actual outcome is terminal `failure` with `RUN_FAILED`; subsequent work and the artifact step are skipped.

The cause is the integration between the new runtime and the journal. `Runtime.runPersisted` passes `result.Diagnostic` to `OnStepFinished` for an unstable result. The host forwards that reason to `Journal.AppendStepFinished`, which explicitly rejects reasons on `Success` or `Unstable`. The exception escapes the loop and becomes an infrastructure failure.

Relevant source: [Runtime.fs](../../src/Fogell.Runtime/Runtime.fs#L93), [Program.fs](../../tools/Fogell.Run.Host/Program.fs#L528), [Journal.fs](../../src/Fogell.Journal/Journal.fs#L142).

Reproducer: [reproduce-junit.json](reproduce-junit.json). Run it through the built `Fogell.Run.Host` with a fresh workspace and journal, or submit it through the controller. Expected: `unstable`, an `after-report` message and a published report. Observed: exit 2 from the runner; controller terminal `failure`; infrastructure diagnostics; no later message or publication. Malformed XML reaches the same journal integration failure after being represented internally as a failed test.

The existing runtime test uses in-memory persistence hooks, so it verifies `unstable` without exercising the journal constraint. Fix the hook/journal contract and add a real persisted-runner/controller regression covering failed tests, continuation and `always` publication.

### 2. Medium priority: newline-terminated environment names pass admission

`{"env":{"NAME\n":"x"}, ...}` is accepted and executes successfully. The native contract restricts names to ASCII letters, digits and underscores. The parser uses the .NET regex anchor `$`, which can match before a final newline.

Relevant source: [Parser.fs](../../src/Fogell.Pipeline.Parser/Parser.fs#L37). Reproducer: [reproduce-env-newline.json](reproduce-env-newline.json).

Use an absolute end anchor such as `\z`, or validate every character, and add admission tests for trailing LF/CRLF and other control characters. This is a contract violation; the campaign did not demonstrate credential exposure or a containment escape from it.

### 3. Operational limit: modest fast output can exhaust the callback queue

A shell loop producing 350 short markers under the default `-x` tracing failed repeatedly with the typed infrastructure reason `OUTPUT_LIMIT_EXCEEDED`. The diagnostic run retained 1,439 feedback records across 15 pages and stopped after marker 286. This workload is far below the advertised whole-build limits of 32 MiB and 100,000 records.

The process transport separately bounds queued callbacks to 1,024. Its overflow path fails closed rather than waiting for the sink. This is an implemented resource limit, not evidence of pagination corruption. Disabling shell tracing for the same 350 markers succeeded: all markers arrived exactly once across four pages, with 354 distinct records.

Relevant source: [ProcessGroup.fs](../../src/Fogell.Execution/ProcessGroup.fs#L301) and its callback admission near line 1332. Before a representative pilot, qualify realistic compiler/test output bursts, document this bound, and evaluate bounded backpressure or batched publication. Merely increasing the whole-build byte ceiling will not resolve the queue limit.

## Coverage and results

| Check | Result |
| --- | --- |
| Locked restore and clean Release build | Passed; zero build warnings/errors. Build: 102.79 seconds. |
| Repository gate, including runner recovery and real controller proof | Passed in 256 seconds. |
| All nine test projects, three complete executions | 568/568 each; **1,704 passed, zero ignored, zero failed**. Additional suite runs: 160 and 185 seconds. |
| Independent runner matrix | **112/115 passed**; three assertions expose two product defects. |
| Source snapshot failure/fix loop | 50/50 completed: 25 intentional exit-7 failures and 25 corrected successes, with exact artifacts/source checks. |
| Concurrent controller admission | 24 distinct submissions completed successfully per extended run. |
| Concurrent idempotency | 16 identical-key requests produced one build; changed-definition reuse returned 409. |
| Feedback pagination with shell tracing disabled | 350/350 markers, 354 distinct ordered records, four bounded pages, no duplicate sequences. |
| Default traced burst | Failed with `OUTPUT_LIMIT_EXCEEDED`; retained evidence remained readable across 15 pages. |
| Controller graceful restart | Committed result and idempotency binding preserved. |
| Controller SIGKILL during active work | Reconciliation required; side effect occurred once; later effect absent. |
| Runner SIGKILL/restart, terminal replay, definition change | Passed: no unsafe replay and changed definition refused. |
| Authentication, malformed/versioned admission, cancellation | Passed real controller checks. |
| PostgreSQL backup/restore | Passed schema/data/sequence comparisons in 20 seconds. |
| Migration 0016 → 0017 → restored 0016 → 0017 | Passed in 46 seconds; rollback matched the original logical hash, and both forward states matched. Four clean-room FK rebuild phases passed. |
| Corrupted backup archive | Correctly refused changed archive bytes, exit 1, 17 seconds. |
| Corrupted migration rollback archive | Correctly refused changed archive bytes, exit 1, 29 seconds. |
| Corrupted restored data | Correctly refused a changed rollback logical hash, exit 1, 38 seconds. |
| Skipped second forward migration | Correctly refused a changed second-forward logical hash, exit 1, 44 seconds. |
| Removed forward foreign key | Correctly refused missing tenant-composite attempt keys, exit 1, 23 seconds. |

Test inventory per run: runtime 8, client 27, retention 23, controller/API 116, domain 41, execution 194, journal 31, store 113 and parser 15. The retention/store suites exercise real PostgreSQL, including cleanup/recovery and authority constraints.

Across the 50 tiny snapshot feedback loops, elapsed time from snapshot creation through submission, watch, artifact retrieval and source retrieval had **median 1.6263 s, p95 1.7477 s, minimum 1.4586 s and maximum 1.9574 s**. The 50 measured loop intervals totaled 81.5099 seconds. These observations include concurrent suite activity on Luigi and are not a controlled throughput benchmark.

The independent runner matrix contains 115 cases: 21 execution/report/publication cases, 30 explicit admission cases, 40 deterministic unknown-field mutations and 24 concurrent runner invocations. Its corrected run passed **112/115**. The failures are `test-report-fail`, `test-report-malformed` and `admission-env-newline`, representing findings 1 and 2 above.

The first matrix draft also had expectation errors: its valid JUnit fixtures lacked class identities and it expected ordinary workload exit codes for symlink publication refusal. Those fixtures were corrected. Both runs are retained in the evidence; the initial draft is not counted as five product defects.

The controller soak completed 50 alternating failure/fix source-snapshot cycles, checking typed exit-7 feedback, successful corrected builds, exact artifact bytes and retained source identity on each cycle. It then passed 24 concurrent submissions and a 16-request same-key race yielding exactly one new build. A different definition with that key returned 409. The original combined script subsequently failed its high-output success assertion, so its overall exit code is a failure; completed earlier assertions are reported separately.

A follow-up with tracing disabled passed complete pagination, preserved terminal truth and idempotency across controller restart, and survived a controller SIGKILL during an active command. The interrupted build became nonterminal `reconciliation_required`, the effect occurred once, and the post-crash effect was absent. Explicit cancellation was also observed as `aborted`.

## Reproduction and evidence

The [harness directory](harness/) contains the actual campaign scripts. Controller extensions are derived from `scripts/prove-native.py` in the hashed source snapshot; the product source was not modified to make these assertions pass. Harnesses accept `FOGELL_CAMPAIGN_SOURCE` and `FOGELL_CAMPAIGN_LOGS`; the runner matrix takes source and log directories as arguments. Run only against an explicitly disposable PostgreSQL container, using the pinned SDK and a dedicated process session.

The source manifest identifies the uncommitted candidate independently of the base commit. All 163 source hashes still matched after testing. Product code was unchanged during the campaign; this report and the roadmap qualification note were added afterward.

- [Machine-readable results](results.json): suite counts, matrix failures, latency distribution and every command's raw exit code/duration.
- [Raw evidence archive](evidence.tar.gz): build/test logs, all matrix logs, controller soak/diagnostic logs, operational drills, before/after host state and cleanup checks. SHA-256: `925293edb28cf805b32181841cd76de94dfd442149b3b6ee863217bae8c08d24`.
- [Exact source archive](source.tar.gz): the 163-file tested snapshot, without build outputs or Git history.
- [Harness instructions](harness/README.md) and minimal reproducer definitions linked above.

Raw logs preserve successful checks, assertion failures, fault-injection refusals and timing, including unsuccessful intermediate diagnostic runs. Expected fault-injection exits remain nonzero in the raw results; they are successful detection tests only because the specific refusal was verified.

## Cleanup and pilot impact

The existing `fogell.service` and `fogell-postgres.service` retained their original PIDs (`1423097` and `1418902`), active states and 23 September start timestamps. Their existing container identities also remained present. No pilot restart, deployment or data mutation was part of this campaign.

After the tests, no campaign proof/drill databases or runtime roles remained, and no executable from the campaign source or private SDK was running. The disposable PostgreSQL container and its anonymous volume were removed. After the evidence archive was retrieved and its SHA-256 verified locally, the exact remote campaign directory, including its private SDK and build outputs, was removed at `2026-09-26T21:33:43.315049+00:00`. Evidence and the source snapshot remain in this local report directory.

## Limits and next steps

This is a bounded single-host campaign, not a multi-day soak or a capacity/SLO certification. The tiny feedback workloads do not establish large-repository build throughput. Tests use administrator-provisioned disposable databases and a restricted test runtime role; they do not qualify production credential provisioning. Database archive drills do not establish a full paired database/filesystem restore objective, off-host retention or recovery from host loss.

There was no hostile-tenant isolation test, external network partition, host-wide disk exhaustion, power cut, production deployment upgrade or migration of the existing pilot. Those require separately scoped operational qualification.

Priorities: fix and regression-test the JUnit/journal integration; reject invalid environment names; qualify output transport against representative bursty workloads; then conduct a sustained native pilot and paired-state recovery rehearsal. A green unit/integration gate alone is insufficient for this candidate.
