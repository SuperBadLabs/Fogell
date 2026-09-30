# Local native Fogell benchmark

Measured 2026-09-29 on exact GitHub `main` commit
`bdf682ef02ba343977310ad504a74d819c9a0eff`. This is a local engineering
baseline, not the Luigi pilot or a multi-user capacity result.

## Setup

- Host: Ubuntu 24.04.4 LTS, Linux 7.0.0-30-generic, AMD Ryzen 9 9950X3D
  (16 cores, 32 threads), 123 GiB installed memory, .NET SDK 10.0.301.
- PostgreSQL 16: task-created disposable Podman container on a dynamically
  assigned loopback port. The controller benchmark created and removed its
  own database and restricted runtime role. No existing service was used.
- Dependency cache and Release outputs were warm. Runs were sequential and
  source-bound. The latency workload was a version 1 JSON pipeline with one
  shell step, `cat input.txt`, from an explicit one-file snapshot. There was
  no competing benchmark workload during timing.
- Worker polling was 50 ms; feedback was observed with 50 ms HTTP polling.
  Times use `time.monotonic()` in the benchmark client. The accepted-to-feedback
  clock starts when the submit command returns its accepted build ID. Snapshot
  creation is outside the timed submit round trip. The first of 30 runs is
  included. Percentiles use linear interpolation over sorted samples.

## Results

| Measurement | p50 | p95 | Range |
| --- | ---: | ---: | ---: |
| Submit command round trip | 141.7 ms | 179.3 ms | 117.9–184.1 ms |
| Submit response to first feedback | 157.9 ms | 216.0 ms | 104.2–265.8 ms |
| Submit response to terminal feedback | 210.9 ms | 297.4 ms | 155.6–321.0 ms |

All **30/30** controller builds completed successfully. Each returned five
unique feedback records; the temporary controller state tree measured 43,140
bytes after the run. Sampled controller `VmRSS` peaked at 131,514,368 bytes
(125.4 MiB); this excludes its worker child processes. The 20 ms RSS sampler
may miss shorter peaks.

The warmed full locked gate took **107.67 seconds wall time** and 70.16 seconds
of combined user/system CPU. Its build phase took **30.85 seconds** with zero
warnings/errors. All nine test projects passed **584/584** tests, zero ignored,
failed, or errored. The native runner, default-traced 350-marker burst, and
real controller proof passed. `/usr/bin/time -v` reported a maximum resident
set of 533,604 KiB (521.1 MiB); this is the maximum observed process in the
gate command tree, not the sum of concurrent processes.

The observed client-side response-to-feedback p95 is below five seconds. It
starts after the server has accepted the build, so it cannot alone prove the
release board's accepted-submission-to-first-feedback target. That target
still requires a seven-day Luigi pilot with cold dependencies, interruptions,
failure/fix pairs, and uncensored latency samples. These short, sequential
local jobs do not establish the pilot result or a throughput ceiling.

## Reproduction and raw data

Use `scripts/ci-postgres.sh start` to create a disposable PostgreSQL 16
container. With its printed `FOGELL_PG_CONTAINER`, `FOGELL_PG_PORT` and
`FOGELL_TEST_DATABASE_URL` in the environment, run:

```sh
/usr/bin/time -v -o gate-time.txt ./scripts/build-and-test.sh > gate.log 2>&1
python3 scripts/benchmark-native.py \
  --container "$FOGELL_PG_CONTAINER" --port "$FOGELL_PG_PORT" \
  --repeats 30 --output controller-runs.json
```

Run the controller benchmark after the gate exits to avoid concurrent load.
`gate.log`, `gate-time.txt`, and `controller-runs.json` are the raw evidence
for this report. `SHA256SUMS` records their hashes and the report hash.
