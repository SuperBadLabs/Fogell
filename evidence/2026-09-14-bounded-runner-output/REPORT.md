# Bounded runner output and failure diagnostics

Operator regression evidence, 2026-09-14. This is not a Jenkins differential
receipt or a production-readiness certification.

Base: `5b46d4515152a81319b9ea201fc6f9f0560b011b` (current remote main at verification).
`SOURCE-SHA256SUMS` identifies the tested implementation, tests and controller
proof. Builds used .NET SDK 10.0.301 on HeMan and runtime 10.0.12 on Luigi.

## Change

Each `ProcessGroup` invocation now bounds retained stdout/stderr, framed records
and pending asynchronous callback text to 16,777,216 UTF-16 code units. The
callback queue additionally admits at most 1,024 entries. Chunked readers replace
the framework's unbounded physical-line readers. Limit detection wakes the
process wait even without a timeout or interrupt predicate, stops the process
group and raises a named failure. Captures below the limit retain their existing
contents; overflow cannot return a successful truncated value.

Persisted failures now publish fixed engine-owned cause codes through the
existing event stream. Arbitrary exception messages and runner stderr are not
forwarded into public logs. Actual output-publication failures retain precedence
over output limits; an inability to publish a diagnostic leaves reconciliation
required rather than writing terminal build truth.

## Luigi observations

A fresh rootless Podman pod used an internal network and loopback HTTPS port
19367. Controller/runner: 2 GiB, two CPUs, 160 tasks, UID 1000, no capabilities,
no-new-privileges and a read-only root filesystem. PostgreSQL 16 used a separate
runtime role without superuser/BYPASSRLS, with 1 GiB and one CPU. The sandbox
used generated test input and its own database, token, certificate and state.

| Workload | Observation |
| --- | --- |
| Two 8 MiB newline-free controls | Both succeeded; each retained 8,388,706 log bytes including framing/events and its tail sentinel. |
| Two 96 MiB newline-free attempts | Both failed with `OUTPUT_LIMIT_EXCEEDED` in 1.91–1.92 seconds; 181 diagnostic/event bytes remained; the later shell effect did not run. |
| Control after each oversized attempt | Both succeeded. |
| 1,024 records totaling 4 MiB | Succeeded; 4,194,402 log bytes and the tail sentinel survived pagination. |
| Infinite newline-free writer (`yes x` through `base64 -w0`) | Failed with `OUTPUT_LIMIT_EXCEEDED` in 1.74 seconds; its delayed child effect did not occur. |
| Ordinary `exit 7` | Failed normally. |
| Started TERM-ignoring workload, then cancellation | Required reconciliation with durable reason `build_cancelled`, matching the current forced-exit contract. No late effect after waiting beyond its 12-second delay; no remaining child processes. |
| Control after cancellation | Succeeded. |

The two monitored final-binary phases recorded 183 and five readiness samples,
all HTTP 200. Maximum sampled readiness latency was 52 ms. Maximum sampled
container memory was 812,978,176 bytes; cgroup `oom` and `oom_kill` counters stayed
zero. These are bounded samples, not continuous peak-memory or latency claims.

`luigi-results.jsonl` contains reviewed generated metadata from the three named
logs. It excludes raw console output, credentials and workspace files. The first
two scripts ended at assertions described below; their earlier passing rows and
health samples are retained. `verification-cancel.log` completed successfully.

## Findings and limits

An additional **tiny-line flood remains a production gap**. `yes bounded-output`
produced a log backlog: the harness exceeded its 120-second terminal wait and
500-page log limit. A later inspection found 23,658 persisted records (331,215
bytes), status still `running`, and cancellation still pending after another
20-second wait. The isolated controller was restarted; the build required
reconciliation with reason `build_cancelled`, and subsequent builds succeeded.
No fix for that persistence backlog is claimed here. It is distinct from the
newline-free infinite writer above.

The first cancellation harness expected `aborted`, based on the earlier campaign.
Current main's unchanged controller permits terminal publication only after a
natural exit with proven extinction. Forced cancellation therefore reconciles.
The final check explicitly asserted the durable `build_cancelled` reason, child
cleanup and post-cancellation progress instead of weakening the expected state.

During development, the first candidate exposed two defects that were fixed
before final verification: a synthetic callback-settlement timeout hid the
output-limit cause, and a wait without a deadline did not wake on limit detection.
Focused regressions now cover both. The earlier 96 MiB candidate's delayed effect
is not included as a passing observation.

These limits apply to individual process invocations. The walker still retains
aggregate build output and publication history; many steps or concurrent branches
can consume more memory. This change does not provide a whole-build memory
budget, hostile multi-tenant isolation, or a bound on log-persistence latency.

## Local verification

- Release solution build: zero warnings and errors.
- All eight test projects: 1,203 tests passed, none ignored or failed.
- Native `prove-runnable-controller.sh`: passed containment, recovery,
  progressive/fenced logs, artifacts and exact 18,000,000-byte post-exit draining.
  Its tail workload now uses two 9 MB shell invocations, retaining the same total
  payload and timing proof under the new per-invocation limit.
- Independent Terra review: no remaining blockers after the final wake-up and
  proof-script changes. Terra implemented the capture change; Luna mapped QA;
  the primary agent integrated diagnostics and performed hosted verification.
- All local gate checks completed across the primary run and its continuation.
  The full invocation passed tests, mutation proofs and audits, then stopped
  because the external corpus fixture variables were unset. The corpus check
  passed after configuring the established baseline and pinned oracle paths
  from `docs/CUSTODIAN_HANDOFF_2026-08-27_FG-042b.md`. The remaining strict
  stale-reference audit and restart/approval lane then passed. This was not an
  uninterrupted invocation returning bare `OK`; see `local-gate-summary.txt`.

The disposable pod, containers, network, runtime image, copied runtimes and
generated credentials were removed. Luigi's original containers retained their
IDs: `b3c3d866d9d1` (`ctrl`), `5388364b3d6b` (`ag1`), `3d18d8520a10`
(`jenkins-bench`), `38cf17f58768` (`mcloving-faceoff2`) and `f828e5fbbd95`
(`jenkins-lab`).
