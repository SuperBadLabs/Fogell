# Luigi campaign harnesses

These black-box scripts were run against the source snapshot in `../source.tar.gz`.
They do not apply product fixes. Build that snapshot with its pinned SDK first.
Set `DOTNET_ROOT` and put that SDK's directory on `PATH` when using a private SDK.

- `fogell-luigi-runner-matrix.py SOURCE LOG_DIRECTORY`: 115 runner checks. The
  log directory must exist. Expected candidate outcome: 112 pass, 3 fail.
- `fogell-luigi-controller-soak.py --container NAME --port PORT`: 50 snapshot
  feedback loops, concurrent admission/idempotency, output, restart and crash.
  Its original default-traced output test fails before restart/crash assertions.
- `fogell-luigi-controller-followup.py`: short diagnostic repeat that records
  the first feedback page. It retains the failing high-output assertion.
- `fogell-luigi-controller-diagnostics.py`: drains every feedback page and
  records output-limit diagnostics. With `FOGELL_CAMPAIGN_QUIET=1`, it disables
  shell tracing for the pagination workload and completes restart/crash checks.
- `fogell-luigi-junit-proof.py`: verifies the real-controller JUnit contract.
  Expected candidate outcome: assertion failure because `unstable` becomes
  terminal `failure` and subsequent work is skipped.
- `fogell-luigi-operational.sh`: exact campaign driver for the remaining
  database drills. Its directory and container names are campaign-specific;
  update those only to resources you explicitly created for another rehearsal.

The controller scripts require `FOGELL_CAMPAIGN_SOURCE` and
`FOGELL_CAMPAIGN_LOGS` pointing to the built snapshot and an existing log
directory. They reuse the baseline controller fixture from `scripts/prove-native.py`,
with bounded assertions added for this campaign. Use a uniquely named,
explicitly disposable PostgreSQL 16 container and its loopback port.

Use `setsid --wait` and an outer timeout when invoking the harnesses. The source
snapshot also contains `scripts/build-and-test.sh`, `run-project-tests.sh`, and
the backup/restore and migration drill scripts. Injected-corruption drills are
successful tests only when they return nonzero for the stated refusal reason;
the driver preserves raw exit codes rather than relabeling them as zero.
