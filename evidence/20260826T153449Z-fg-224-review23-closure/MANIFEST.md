# FG-224 exact-head review 23 closure

Collected at `2026-08-26T21:02:14Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `97f72ff918f598dc906590d63f42356796f8e971`
- Base tree: `ea9ac726ae39da502e8d437122d47b146da803ae`
- Candidate product tree: `e45b3572f900caa6715d9e47513bbbed1f93f804`
- Candidate-diff SHA-256: `20536c7143c366ade20a74f1f98ae68c87e90136048c75f4876784d135951d02`
- Delta: 8 files changed, 436 insertions, 119 deletions
- .NET SDK: `10.0.301`

The complete audited product and documentation delta was staged before
snapshotting. `candidate.diff` is the index-versus-HEAD diff and the candidate
product tree is derived from `git write-tree`. This evidence directory is output
and is intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5034885963` on base commit `97f72ff` found two blockers:

1. Outer Run.Host cleanup reused the launcher's numeric PID/PGID after the child
   exited, so rapid PID reuse could authorize TERM or KILL of an unrelated group.
2. The HTTP route decoded and parsed replacement bytes before consulting an
   existing idempotency binding, so a malformed replacement could return 422
   instead of the durable binding's 409 conflict.

The closure hardens both authorities:

- The worker captures the outer launcher's Linux pid plus `/proc` start time
  immediately after start. Every later leader or process-group signal rechecks
  that birth identity and the current PGID. A matching pre-`setsid` launcher can
  be stopped directly; group signalling additionally requires that the same
  process currently leads PGID=PID.
- Once the captured identity is absent, reused, or uncertain, numeric group
  presence grants no signalling authority. Group absence may prove extinction;
  populated or uncertain presence returns `StatusUncertain`. Deterministic tests
  prove a reused PID/PGID receives neither TERM nor KILL. Durable inner-group
  anchors retain their existing cleanup behavior.
- The API constructs a raw `AdmissionProbe` after authentication, header, and
  body-size checks but before UTF-8 decoding, parsing, or current execution
  preflight. Exact legacy bytes replay the immutable admission as 200, while any
  changed raw bytes or placement fingerprint for the bound key return 409.
- A probe miss creates nothing and grants no authority. Fresh input still passes
  strict decoding, parsing, and persisted preflight before transactional
  `AdmitBuild`; the unique constraint and project lock remain the concurrent
  create/race arbiter.
- Regressions cover exact direct-seeded invalid-UTF-8 replay, changed malformed,
  empty, and invalid-byte conflicts, fresh 400/422 classification, and an
  eight-way mixed-source race yielding one durable build.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 893/893 main-suite tests; every blocking proof; final `OK` | `fg224-review23-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review23-runnable-proof.log` |
| Controller API | PASS, 31/31 including raw-byte legacy replay/conflict and mixed-source race | `tests-Fogell.Controller.Api.Tests.log` |
| Differential | PASS, 266/266 including deterministic outer PID-reuse non-signalling | `tests-Fogell.Differential.Tests.log` |
| Release build | PASS, one existing FS0040 parser initialization warning and zero errors | `build.log` |
| Independent integrated audit | CLEAN; process identity, replay ordering, and race arbitration verified | orchestration handoff; behavior is reproduced by the sealed tests |

The 893 main-suite tests are Controller API 31, Differential 266, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 87. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
