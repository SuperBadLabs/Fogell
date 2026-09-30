# Wave 2 burst-output qualification

Date: 2026-09-29. Host: HeMan. Exact product candidate:
`016ed3b2943eefa2f78699a6b4c8bcb00a65acd7`.

**FG-305 passes.** The default-traced 350-marker workload completed through
the real controller. Feedback spanned 15 pages and 1,411 records; record
sequences were unique and ordered, and every marker appeared exactly once in
order. The associated artifact matched its expected bytes. The same shell
command passed three ProcessGroup runs. A test with 1,280 output lines drained
the bounded callback queue without loss or reordering. A permanently blocked
callback failed closed and released process cleanup in about five seconds.

The callback queue remains bounded at 1,024 entries and 16 Mi characters.
Producers wait up to five seconds for capacity, then report
`OUTPUT_LIMIT_EXCEEDED` if a sink stays blocked. The existing whole-output
limit is unchanged. The independent runner matrix passed **115/115**, including
the 18,000,000-byte over-limit case: it ended as a failed run with a retained
typed `OUTPUT_LIMIT_EXCEEDED` diagnostic. The locked native gate passed
**584/584** tests in nine projects with zero build warnings or errors, and
passed the runner and controller proofs against disposable PostgreSQL 16.

This closes the burst transport ticket. W2 still requires FG-306: Fogell's
full build/test and one named noisy repository workload through the controller,
with their own output, artifacts, and resource measurements. This report is
local qualification, not a Luigi staging or release proof.

## Evidence

- `source-manifest.json`: SHA-256 hashes of all 195 tracked files in the
  product candidate, with its full commit ID.
- `gate.log`: locked restore/build, nine test summaries, and real controller
  pagination and artifact proof.
- `matrix-logs.tar.gz`: 115 raw case logs, result JSON, and the summary stream.
- `SHA256SUMS`: hashes of this report and the three evidence files.

To repeat, build the exact candidate, start an explicitly disposable PostgreSQL
16 container, set `FOGELL_TEST_DATABASE_URL`, `FOGELL_PG_CONTAINER`, and
`FOGELL_PG_PORT`, and run `./scripts/build-and-test.sh`. For the independent
matrix, create an empty log directory and run
`python3 scripts/qualification/native-runner-matrix.py SOURCE LOG_DIRECTORY`.
