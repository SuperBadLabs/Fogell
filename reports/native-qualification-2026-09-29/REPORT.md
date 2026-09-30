# Wave 1 native correctness qualification

Date: 2026-09-29. Host: HeMan. Exact candidate:
`9550bb05f36a293ce2c8606b02d17e60028cc6d1`.

The 115-case black-box runner matrix passed **115/115**. The locked native gate
passed **582/582** tests across all nine projects, built with zero warnings and
errors, and passed the real runner and controller proofs against a disposable
PostgreSQL 16 container (`fogell-w1-20260929`, loopback port 35807). The container
was removed after the run.

The candidate rejects the September 26 trailing-newline environment-name
reproducer before workspace or journal creation. The controller proof also
checks root LF and step CRLF names return HTTP 422. Failed JUnit cases remain
terminal `unstable`, with typed diagnostics, later work, and artifacts intact.

The matrix is carried forward from the immutable September 26 harness in
`scripts/qualification/native-runner-matrix.py`. Its sole behavioral expectation
change is the malformed-JUnit case: exit 1 (`unstable`) now replaces the prior
infrastructure failure exit 2. All 115 cases and raw case logs are retained.

## Evidence

- `source-manifest.json`: SHA-256 of all 187 tracked files in the candidate.
- `matrix-logs.tar.gz`: raw output of every matrix case, result JSON, and the
  harness summary stream.
- `gate.log`: locked restore, build, every test-project summary, and runner and
  controller proof output.
- `SHA256SUMS`: hashes of this report and those evidence files.

To repeat the matrix, build the exact commit and run
`python3 scripts/qualification/native-runner-matrix.py SOURCE LOG_DIRECTORY`.
To repeat the gate, provision an explicitly disposable PostgreSQL 16 instance,
set `FOGELL_TEST_DATABASE_URL`, `FOGELL_PG_CONTAINER`, and `FOGELL_PG_PORT`, then
run `./scripts/build-and-test.sh`.

This was local correctness qualification on HeMan, not a sustained Luigi pilot
or output-burst qualification. The 350-marker traced-output failure remains
Wave 2 work. The candidate was not published or deployed.
