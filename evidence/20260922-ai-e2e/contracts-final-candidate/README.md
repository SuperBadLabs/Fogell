# Reviewed-candidate live feedback and source contracts

The complete contract run passed: seven watched builds, 16 checks, and a separate
two-build M1 failure/correction rerun against the real authenticated controller,
worker, PostgreSQL store and Run.Host. The test created a distinct organization
and project inside the disposable pilot deployment. It did not restart or
reconfigure the controller. Deployment cleanup belongs to the parent campaign.

[receipt.json](receipt.json) retains admission IDs, bounded feedback pages,
typed diagnostics, source/tool identity and client/harness hashes. The linked
NDJSON files retain each command's original output. Pipeline/snapshot files are
small trusted fixtures; no bearer token is included.

| Contract | Observed outcome |
| --- | --- |
| Shell exits 7 | `failure`; typed `workload_step`, exit 7, stage `Shell failure` |
| Planted JUnit failure | `unstable`; typed `test`, `planted_failure`, `tests/example.fs:23` |
| Exact snapshot/key replay | Same build and attempt IDs, `was_existing: true` |
| Changed manifest under original key | HTTP 409 `idempotency_conflict` |
| Explicit source download | Byte-for-byte identical original envelope |
| Reproduction after editing local source | New build/attempt; original output; verified source, pipeline, manifest and runner digests match |
| Incorrect runner pin | `reconciliation_required`; typed infrastructure diagnostic; client exit 5 |
| Snapshot submitted as plain pipeline | HTTP 422 refusal; no execution admitted |
| Omitted optional runner pin | Success; requested pin stays null, observed runner identity is recorded |
| Explicit cancellation | Acknowledged, then bounded `reconciliation_required` with `build_cancelled`; client exit 5 |

Cancellation did **not** establish an `aborted` terminal result. The client
preserved uncertainty and did not report success. The retention/recovery campaign
must preserve those attempts' reconciliation evidence.

First typed actionable diagnostic observations were 403.4 ms for the shell
failure, 415.3 ms for the JUnit failure, and 238.5 ms for the incorrect tool pin.
These are individual monotonic process observations from submission start,
including client startup, transport and 100 ms polling. They are not queue-time
measurements, percentiles or performance targets. Diagnostics were read from
typed response fields, not inferred by parsing full log text.

Earlier attempts remain preserved beside this directory:

- `contracts/`: the original JUnit fixture lost XML attribute quoting and produced
  only an aggregate diagnostic; the checker correctly refused missing test identity.
- `contracts-source/`: the proof's initial one-line pipeline used unsupported
  directive separators and admission refused it. The proof fixture was corrected.
- `contracts-source-2/`: source reproduction and incorrect-pin checks passed;
  the proof initially expected plain-pipeline snapshot transport to admit, whereas
  the controller safely refused it. The check now requires that refusal.
- `contracts-source-3/`: source controls passed; the optional cancellation check
  incorrectly expected `aborted`. The final receipt reports the actual explicit
  reconciliation behavior and checks that it cannot become a false success.

The corrected JUnit fixture and final harness do not change controller semantics.
This rerun binds the reviewed runner closure
`a6c4af1002df0c564d57a4d14f968e486949969533930ba35fdb0d87af9f2a49`
and observed .NET runtime `10.0.12`. It completed before the isolated timing
campaign; its separate tenant has no queued or running builds. The two
reconciliation cases remain protected evidence for deployment-owner cleanup.

Reproduce on an explicitly disposable configured deployment:

```bash
python3 scripts/prove-ai-feedback-contract.py \
  --lab-config /absolute/disposable-lab.json \
  --output /absolute/new-contract-receipt-directory
```

The lab file provides the owned PostgreSQL container/database, controller URL
and private deployment root containing `token`. Each command is bounded; watch
duration is 30 seconds, its outer process bound 40 seconds, and the reused client
capture limits aggregate process output to 4 MiB. The optional `--source-only`
mode explicitly scopes its receipt to independent source/cancellation checks.
