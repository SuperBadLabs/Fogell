# Wave 2 representative-output qualification, complete

Date: 2026-09-29. Host: HeMan. Exact Fogell candidate:
`1d2973c0819c5f84b452767f21b5f80d8cd2c269`. The product output
transport is unchanged from the FG-305 candidate `016ed3b2`; subsequent
changes are qualification harness and test fixture work.

**Fogell passed its full locked gate as a native controller workload.** The
explicit source snapshot contained 209 files and had SHA-256
`6e43b69752b500868a5d7d8fac3f969fa6810ef48e0809a94d975b6114e5cfb8`.
The controller reported terminal `success` after 109.010 seconds. All nine
Expecto projects passed, 584/584 tests, zero ignored, failed, or errored;
build warnings and errors were both zero. The archived 13,386-byte gate log
contained the completion marker and exactly matched a contiguous 114-line
slice of the 120 ordered, unique feedback records over two pages. Artifact
SHA-256 was `6995a4dd5b44e2ed4f5bf17f3ee2f03bec7b0a40014cecd5b9f8ec2aa761ea04`.
The measured peak resident size was 515,760 KiB; the disposable controller
state tree held 115,644,090 bytes when collected.

The nested Fogell gate used a separate test database so its store and retention
tests could advance a restore epoch without fencing the controller under test.
Its test process used `scripts/qualification/reap-descendants.py` to reap
orphaned fixture children under the controller's process ownership. The
Execution suite's `/proc` assertions now use an existing three-second bounded
reap check, preserving the disappearance and no-late-effect guarantees. The
earlier source-bound attempts improved from 194/196 at `beddb488` to 195/196
at `997c48cd`, and then 196/196 at this candidate. Their raw failure results
are retained in `fg311-failed-attempts.tar.gz`; the [earlier partial report](../native-representative-2026-09-29/REPORT.md)
retains the shared-database setup error and two earlier failed attempts.

**Apache Maven also passed.** The [partial report](../native-representative-2026-09-29/REPORT.md)
holds its exact source/tool inputs and raw evidence: Maven's debug-output
`api/maven-api-annotations` test lifecycle matched its 188,271-byte artifact
across 2,517 ordered records and 26 pages. The module compiled 11 Java sources
but contained no test cases. It qualifies noisy build output, not Maven test
behavior. Fogell and Maven were separately submitted with explicit source
snapshots. The native runner, default-traced 350-marker burst, and controller
API proof also passed in the final Fogell harness run; the burst was 1,411
ordered records over 15 pages with all 350 markers in order.

## Evidence and reproduction

- `fogell-gate.log`: exact archived output, including all nine test summaries,
  build diagnostics, completion marker, and `/usr/bin/time -v` measurements.
- `fogell-feedback.jsonl`: every feedback page. `fogell-metrics.json`,
  `fogell-files.txt`, and `fogell-harness.log`: terminal status, hashes,
  snapshot inventory, measurements, and the harness result.
- `fg311-failed-attempts.tar.gz`: the two later failed attempts with their
  raw feedback, artifacts, inventories, metrics, and harness logs.
- `SHA256SUMS`: hashes of this report and every evidence file above.

Run `python3 -u scripts/prove-native.py --container CONTAINER --port PORT
--fogell-representative-logs OUTPUT_DIR` against a disposable PostgreSQL 16
container. The harness creates separate controller and gate test databases,
then saves the terminal feedback even on failure. The Maven command and source
inputs are recorded in the earlier report. The independent 115-case matrix and
FG-305 over-limit failure evidence remain in the [W2 transport report](../native-output-2026-09-29/REPORT.md).
