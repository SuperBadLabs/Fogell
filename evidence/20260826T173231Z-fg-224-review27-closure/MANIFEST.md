# FG-224 exact-head review 27 closure

Collected at `2026-08-26T22:56:35Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `70ee93e7c1ccebe4fa7156880d151f71cb45cd0d`
- Base tree: `eb2aa5865e1e2636ac3d74bd3088a6e157ba7c0f`
- Candidate product tree: `5c4e8bfc64aaa36c1fac3e5066e05970999598d7`
- Candidate-diff SHA-256: `056498deaeddd9bd6799a38465bfe1812ae62778dcfbbde39327ced8937a027f`
- Delta: 7 files changed, 199 insertions, 9 deletions
- .NET SDK: `10.0.301`

The complete audited product and documentation delta was staged before
snapshotting. `candidate.diff` is the index-versus-HEAD diff and the candidate
product tree is derived from `git write-tree`. This evidence directory is output
and is intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5035644861` on base commit `70ee93e` found two remaining authority
and startup-validation gaps:

1. `RequireReconciliation` accepted an exact owner/fence even after its lease
   expired or its restore epoch became stale, allowing an old worker to race the
   lease scanner or restore recovery and publish worker-selected durable truth.
2. A whitespace-only `FOGELL_LOCAL_TRUST_POOL` passed configuration load even
   though Store admission rejects a blank pool, so the controller could start
   healthy while every submission failed unavailable.

The closure makes both boundaries fail closed:

- `RequireReconciliation` now requires the exact owner and fence, an unexpired
  lease, and the current global restore epoch at its attempt-row UPDATE
  linearization point. Only then can the existing attempt → node → build
  transaction publish its stable reason event and matching outbox row.
- An expired `offered` attempt cannot publish worker-selected reconciliation and
  loses atomically to safe lease recovery, which restores queued lineage without
  event/outbox publication. A replacement claim advances the fence.
- A pre-restore attempt likewise publishes nothing and leaves its eventual
  disposition to restore recovery. Current-epoch live running reconciliation
  retains the established reasoned transition.
- Every required controller setting now treats a whitespace-only value as
  missing. In particular, a blank local trust pool returns the stable
  `FOGELL_LOCAL_TRUST_POOL is required` error before startup.
- Deterministic Store coverage races expired reconciliation against lease
  recovery and proves queued lineage, publication silence, and replacement
  authority. It separately proves live-running success and stale-epoch refusal
  followed by restore-owned recovery. Controller API coverage proves the exact
  whitespace-only startup refusal.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 906/906 main-suite tests; every blocking proof; final `OK` | `fg224-review27-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review27-runnable-proof.log` |
| Controller API | PASS, 34/34 including whitespace-only trust-pool startup refusal | `tests-Fogell.Controller.Api.Tests.log` |
| Store | PASS, 91/91 including expired-lease race and restore-epoch authority | `tests-Fogell.Store.Tests.log` |
| Sealer Release build | PASS, zero warnings and zero errors | `build.log` |
| Authoritative full-gate build | PASS, one existing FS0040 parser initialization warning and zero errors | `fg224-review27-final-gate.log` |

The 906 main-suite tests are Controller API 34, Differential 272, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 91. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

The authoritative gate log SHA-256 is
`4f5dc31fa0d0805b22a839aae96a26fd393d0e00a155b4948cb4b19572921702`.
The runnable proof SHA-256 is
`2c4d0db855788df0fcc477b82d652289e37de0feb48e3b2c5c184975d6c3dae4`.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
