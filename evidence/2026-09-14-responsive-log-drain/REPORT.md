# Responsive log backlog draining

Operator regression evidence, 2026-09-14. This is a bounded comparison, not a
production-readiness certification or a general throughput guarantee.

Base: `8f0f6104b7f4a2aaafe6c772c75d4e8b2c5cdac9` (PR #447).
`SOURCE-SHA256SUMS` identifies the candidate implementation and regression tests.
Build SDK: .NET 10.0.301; Luigi runtime: .NET 10.0.12.

## Change

The local worker previously persisted each child log frame in a separate
transaction, and a running producer could receive only 16 frames per poll.
It now commits at most 128 frames in one transaction, with one lineage lock
sequence, fresh fenced lease check, and contiguous public-cursor allocation.
Any sequence overlap refuses the entire batch. The worker installs its staged
event cursor only after the transaction succeeds.

Each parsing slice reads at most 256 KiB, with a retained encoded frame bounded
at 1 MiB. Store publication accepts at most 1 MiB of decoded UTF-8 per batch.
Strict UTF-8 decoding prevents malformed bytes expanding into replacement text
that would exceed that bound; they produce the existing malformed-frame marker.

Known backlogs drain without idle polling delays. The worker yields between
batches and checks cancellation, shutdown, and lease authority. Frozen post-exit
tails retain their existing complete-drain requirement and natural-exit terminal
arbitration. A forced cancellation still requires reconciliation.

## Luigi comparison

Baseline and candidate ran sequentially in the same fresh rootless Podman
sandbox, using an internal network and loopback HTTPS port 19368. The controller
had 2 GiB, two CPUs, 160 tasks, UID 1000, no capabilities, no-new-privileges, and
a read-only root. PostgreSQL 16 had 1 GiB and one CPU, with a separate runtime
role without superuser/BYPASSRLS. Input, database, state and credentials were
generated solely for this test.

| Workload | Baseline | Candidate |
| --- | --- | --- |
| 5,000 numbered short lines plus tail sentinel | Success in 41.634 s | Success in 2.070 s |
| 50,000 numbered short lines plus tail sentinel | Not run | Success in 7.782 s |
| Continuous `yes bounded-output`, cancel after at least 1,000 persisted records | Prior campaign observed a cancellation backlog; no same-run timing comparison | Terminal reconciliation in 0.674 s, reason `build_cancelled`; cancellation HTTP response in 0.039 s |
| Cancel after verified producer exit, with 31,699 of 50,000 rows still pending | Not run | `aborted` in 5.501 s, all 50,000 rows and tail sentinel preserved |

The comparable 5,000-line case improved about 20.1 times end-to-end. Each finite
passing case checked SQL row cardinality and an ordered MD5 digest over generated
numbered rows, plus exactly one tail sentinel. This catches loss, duplication
and reordering; it does not measure every possible workload or storage backend.

The finite and continuous-flood probes recorded 494 health/status samples, all
HTTP 200; maximum sampled latency was 89 ms. The controller's cgroup `oom` and
`oom_kill` counters were zero. Continuous-flood cancellation left no child
processes or late sentinel effect; a subsequent control build succeeded.

The first post-exit cancellation harness incorrectly treated a terminal journal
as proof of producer exit. It cancelled while that precondition was unverified,
and observed reconciliation with only the already-persisted log prefix. The
corrected harness verified Run.Host process absence before cancelling, then
passed the full-tail assertion. Both observations are retained in
`luigi-results.jsonl`; the first is explicitly marked as a harness precondition
failure, not counted as a passing natural-exit test.

Receipts contain allowlisted generated metadata only, excluding console logs,
credentials and workspace files. Candidate archive SHA-256 values:

```text
666b77b249b65e1b10379a9c6eb7a55292525de34137e3fa7e5fd47615e71cdd  candidate-controller.tar.gz
716a621fb94c1faca34f9cb4d06507cd1a7900eefe22a4a0240c75a1fadf54ec  candidate-runner.tar.gz
```

## Verification and limits

All eight test projects passed: 1,215 tests, none ignored or failed. The batch
regressions cover ordered cursor allocation, range overlap refusal, stale
authority, concurrent duplicates, staged cursor refusal/retry, exceptions,
oversized-frame state, invalid UTF-8, and bounded frozen-tail draining.
Independent Terra integration review found no remaining blockers.
The complete `scripts/build-and-test.sh` invocation passed all lanes and ended
with bare `OK`, including mutation proofs, audits, the configured external
corpus regression, strict stale-reference checks, and restart/approval recovery.
`local-gate-summary.txt` retains the project counts and selected gate receipts.

The native controller proof now establishes producer extinction before its
first active poll, using a valid 60-second poll/180-second lease, a durable
terminal journal, and a bound Run.Host process. Its exact 18,000,000-byte tail
check no longer relies on the old assumption that running backlogs wait between
every slice. Both native and digest-pinned Podman PID 1 proofs passed, including
containment, recovery, artifact retrieval, exact frozen-tail publication,
terminal cleanup and atomic roll-up. The container proof also verified that
controller death under a surviving init extinguishes its real Run.Host, shell,
and nested child without their delayed effect.
The proof-bounds check also passed all eight planted controls/stalls, each with
a named result within its deadline and no controller left behind.

This change bounds work per publication transaction. Database stalls can still
delay a control check until the current operation returns. It does not add
whole-build log quotas, log retention, a memory budget across many steps, or
hostile multi-tenant isolation. Late cancellation after natural producer exit
still waits for the finite tail to drain, so its latency scales with that tail.

The disposable Luigi pod, network, runtime image, copied runtime, state and
generated credentials were removed after verification. The five original
containers retained their IDs: `b3c3d866d9d1` (`ctrl`), `5388364b3d6b` (`ag1`),
`3d18d8520a10` (`jenkins-bench`), `38cf17f58768` (`mcloving-faceoff2`), and
`f828e5fbbd95` (`jenkins-lab`).

## Final stack review

The stack incorporates #447's `d7163c9e` review fixes: platform-correct newline
capacity accounting and fixed returned-failure stderr. The combined Release
build passed with zero warnings/errors; all eight suites passed 1,216 tests,
and the real-controller acceptance proof passed again with exact 18 MB tail
preservation. `review-followup-tests.txt` records this verification. The Luigi
timings above remain the original benchmark observations, not a new campaign.
The final shared output files are identified by #447's adjacent
`REVIEW-FOLLOWUP-SHA256SUMS`; the log-batch source hashes remain unchanged.
