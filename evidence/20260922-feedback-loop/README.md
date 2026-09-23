# FG-265: real controller feedback loop

Three sequential failure/correction campaigns passed on the development checkout
based on `1c010549` plus the uncommitted M1 implementation. Each used the real
Release F# client, authenticated controller API, PostgreSQL store, worker and
Run.Host. Each failed fixture reached `failure`; its corrected submission reached
`success`. All six submissions have distinct durable build and attempt IDs.
Every watch observed nonempty output while the build was still nonterminal and
then drained the final feedback. Ten known-bad receipt mutations were rejected
for every campaign, and the saved receipts independently reverified.

The directory name uses the requested September 22 local task date; receipts
record the actual UTC execution timestamp on September 23.

| Campaign / fixture | Admission ms | First nonempty feedback ms | Terminal observation ms |
| --- | ---: | ---: | ---: |
| 1 / failed | 184.39 | 392.00 | 1509.77 |
| 1 / corrected | 95.88 | 301.40 | 1417.72 |
| 2 / failed | 105.00 | 305.13 | 1420.92 |
| 2 / corrected | 111.62 | 305.34 | 1423.27 |
| 3 / failed | 114.55 | 322.59 | 1433.19 |
| 3 / corrected | 109.04 | 306.71 | 1417.71 |

These are individual monotonic harness observations relative to each submission
process start. They include process startup, HTTP and polling. First nonempty
feedback can be a step-start notification; it is not a diagnosis metric. Each
fixture deliberately sleeps one second after printing the assertion marker, and
the terminal times include that delay. This is a correctness experiment, not a
performance baseline, latency target, percentile claim, or an autonomous fix.

The isolated deployment used one local Linux controller and a newly provisioned
PostgreSQL container/database, runtime role, organization/project and state root.
Campaigns 2 and 3 reused this same disposable deployment. Binaries were warm;
no CPU scheduling or cache-isolation claim is made. Polling was 100 ms, request
timeout 10 s, watch timeout 60 s, response cap 1 MiB and process output cap 4 MiB.
The controller owner captured [source hashes](source-manifest.sha256),
[runtime binary hashes](binary-manifest.sha256), and [provenance](provenance.json)
before subsequent gate rebuilds. The [cleanup receipt](cleanup.json) confirms
controller exit and removal of the task-owned PostgreSQL container, token and
state directory after validation.

All three passing runs recorded the same binary hashes:

- Client DLL: `0917b18ff074690c7621808b79651be9513f8e1a44635eccf60576b6f6f75b2d`
- Controller DLL: `6beb1d2d7bd2ea754117701dd2bedee79b6d353f03810cfd3ac6ba16d17f3680`

`setup-failure-1/` preserves an earlier unsuccessful attempt: the client returned
`transport_failure` before any admission response, and the receipt has
`passed: false`. Later comparison using the same client status command produced
`transport_failure` under the restricted command sandbox and the expected HTTP
404 when local-network execution was allowed. The successful campaigns therefore
ran with reviewed local-network access. That initial attempt also preceded the
final client/controller rebuild; its binary hashes differ. It is excluded from
the three completed loops, retained rather than relabeled as a passing sample,
and not evidence of a controller restart or application failure.

The [checker self-test](checker-self-test.json) also rejected process deadlines and aggregate output
overflow using real local Python child processes, repeated three times each.
These synthetic guard checks do not substitute for the real-controller receipts.

After the live campaigns, review corrected the harness cleanup to signal its
process group only while the leader remains unreaped. This avoids a theoretical
PID-reuse race after a normally completed client. The collection/validation logic
and saved measurements did not change. The final harness reran its checker and
all six process-guard exercises and reverified all three saved campaigns; the
real campaigns were not rerun for this cleanup-only correction. The original
source manifest describes the [preserved measured harness](measured-harness.py);
[the amendment](harness-amendment.json)
records the reviewed replacement hash explicitly.

Reproduce or inspect with the [runbook](../../docs/runbooks/feedback-loop-proof.md).
Reverify the saved successful campaigns from the repository root:

```bash
python3 scripts/prove-feedback-loop.py --verify evidence/20260922-feedback-loop/run-1/receipt.json
python3 scripts/prove-feedback-loop.py --verify evidence/20260922-feedback-loop/run-2/receipt.json
python3 scripts/prove-feedback-loop.py --verify evidence/20260922-feedback-loop/run-3/receipt.json
python3 scripts/prove-feedback-loop.py --self-test
```

Each run directory includes raw NDJSON and stderr from both `submit` and `watch`,
plus the receipt with observed pages and checker results. The known fixtures are
trusted and contain no credentials. No database connection string or bearer token
is included in this evidence.

## Combined validation

[Validation receipt](validation.json) and [test summary](validation.txt) record
1,346 passing tests across nine projects, with no ignored, failed or errored
tests. The initial full gate passed the build, mutation, prelude and preceding
audit checks, then refused the corpus regression check because this shell had
not configured the external baseline/oracle paths. The existing pinned files
were located; that check passed unchanged, followed by the remaining
stale-reference, restart and approval lanes. All gate components passed across
these runs; this is not an uninterrupted full-gate pass. The repository's full
pre-publication gate and external review requirements remain before publication.
