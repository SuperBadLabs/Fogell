# Wave 2 representative-output qualification, partial

Date: 2026-09-29. Host: HeMan. Latest exact Fogell candidate for this report:
`95e8966f03df489fa37f1d82dffdefb719da2831`. The output transport code is
unchanged from FG-305's qualified `016ed3b2`.

**Apache Maven passed through the native controller.** The explicit snapshot
contained 2,046 files from Maven commit
`9ed00db446cf248a579c766cbad4e2322be1f036`; its snapshot SHA-256 was
`b750bbee8cbf3c34c6c772504ee53d7ad8b70f67b3444035ede4e077aa0f211d`.
Maven 3.9.16 ran the `api/maven-api-annotations` `test` lifecycle with `-X`
debug output and a warmed task-local dependency cache. It completed
successfully in 4.207 seconds inside the controller. Feedback contained 2,517
unique ordered records across 26 pages; the command output matched the archived
188,271-byte debug log line for line. The artifact SHA-256 is
`858f1d829a9e1edd9d96de1fec59e5983f8e731208a89cfd1060f45107610941`.
`/usr/bin/time -v` reported 503,200 KiB maximum resident set size, and the
controller state tree measured 9,794,461 bytes at collection. This module
compiled 11 Java sources but has no test cases; it qualifies noisy build output,
not Maven behavioral test coverage. The Maven binary and dependency cache are
host inputs outside the source snapshot: the binary was
`/home/srikanth/.m2/wrapper/dists/apache-maven-3.9.16-bin/4200fbc6/apache-maven-3.9.16/bin/mvn`
and the warmed cache was `/tmp/fg306-maven.isLMNI/m2`.

**Fogell's full gate did not pass through the controller.** Its standalone
locked gate passed 584/584 on the FG-305 candidate, but the source-snapshot
controller attempts ended as failures, with exact logs and feedback retained:

| Candidate | Controller attempt | Result |
| --- | --- | --- |
| `d21c1d40` | Gate tests shared the controller database. | Restore epoch advanced; controller correctly returned `reconciliation_required`. This was a qualification setup error. The interrupted harness log is retained; this attempt did not produce a complete feedback archive. |
| `30ba55a6` | Separate disposable test database. | Execution suite 189/196; seven child PID disappearance assertions failed under nested process ownership. The controller returned failure and retained the gate-log artifact. |
| `52b453b7` | Separate database plus a workspace process-group registry for nested tests. | Execution suite 189/196; three reaping/timing assertions failed and four 500 ms reader-settlement checks errored. The controller returned failure and retained the artifact. |

The latter two attempts passed Fogell's locked build with zero warnings/errors
and completed the earlier test projects, then stopped at the failing execution
suite. Their feedback sequences and artifacts are retained for FG-311. This is
an open W2 release gate, not a qualified full Fogell controller workload. M2's
October 16 deadline is unchanged. The two attempts took 114.851 and 110.303
seconds; their peak measured resident sizes were 546,448 and 530,144 KiB, and
their controller state trees each reached about 113.7 MB.

## Evidence and reproduction

- `maven-debug.log`, `maven-feedback.jsonl`, `maven-metrics.json`,
  `maven-files.txt`, `maven-version.txt`, and `maven-harness.log` are the Maven
  controller output, every feedback page, measurements, inventory, tool
  version, and harness summary.
- `fogell-failures.tar.gz` contains the two complete failed Fogell controller
  attempts (raw feedback pages, gate-log artifacts, metrics, inventories,
  harness logs) and the initial interrupted harness log.
- `product-source-manifest.json` hashes all 200 files at the latest product
  candidate. `SHA256SUMS` protects this report and each evidence file.

The opt-in harness is `scripts/prove-native.py`. For Maven, supply a disposable
PostgreSQL container and port plus `--maven-source`, `--maven-bin`,
`--maven-cache`, and `--maven-representative-logs`. For Fogell, use
`--fogell-representative-logs`; the harness creates separate controller and
test databases and saves feedback even on a failed terminal result.
