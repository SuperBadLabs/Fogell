# Custodian receipt — FG-253 Tier-1 compatibility

Date: 2026-09-06 (America/Chicago)

Status: **PARTIAL, SOURCE MERGED**. The implementation and publication are
complete. The sole remainder is a substantive Copilot review of exact source
head `665b7bacf35f22b92627d35434ba791625193938`, or an explicit owner reviewer
exception. Copilot's current-head review artifact reports an error and is not
treated as approval.

## Authority and selection

The owner appointed this session as Fogell's 12-hour custodian on HeMan and
explicitly authorized publication, pull requests, and merge. The selected
Tier-1 compatibility target was FG-253: stop executing Declarative work on the
built-in node when Jenkins would require an unavailable label or agent
provisioner.

The accepted boundary is conservative refusal, not scheduler parity. Exact
`agent any`, `agent none`, and `label 'built-in'` remain executable. Other
labels, docker, dockerfile, and plugin agents fail closed as
`unsupported_agent` before workspace-root creation, SCM, persistence,
credential resolution, journal repair, or user effects.

## Immutable identities

- Source PR: #430, `https://github.com/SuperBadLabs/Fogell/pull/430`.
- Source base: `e60cde34f7010c3166b1ef088069320bcc83965c`.
- Exact source head: `665b7bacf35f22b92627d35434ba791625193938`.
- Exact source tree: `3e38ac2e6f76a66478bfe0ef7825a4653a0fa944`.
- Local signature verification: good SSH signature for
  `srikanth.remani@gmail.com`, ED25519 key
  `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`.
- GitHub source verification reports `unknown_key`; this is recorded rather
  than rewritten as GitHub-verified.
- Merge commit: `32bb3e6f2582891fba7ca760f05668f4c9c87d51`, merged at
  `2026-09-06T05:45:21Z`.
- Merge parents: base `e60cde34f7010c3166b1ef088069320bcc83965c`
  and exact source `665b7bacf35f22b92627d35434ba791625193938`.
- Merge tree: `3e38ac2e6f76a66478bfe0ef7825a4653a0fa944`, exactly the
  source tree; GitHub reports the merge signature `verified: true`, reason
  `valid`.

## Evidence and gates

- Retained bundle:
  `evidence/20260906T013715Z-fg253-agent-allocation/`.
- Jenkins 2.568.1 kept `fg253-not-offered` queued with the label-unavailable
  reason and no workspace; Fogell refused it before workspace or journal.
  Cleanup removed the probe job and left no matching queue item.
- Receipt `agent-label-built-in` is Tier-1 PROVEN, seal
  `7b64e05e9a4564d591a410e8b12d2db998f4210090705661998a8a488fe75d8d`.
- Corpus accounting is unchanged: `tier1=12`, `admitted=188`, `tier3=28`.
  The 13-file agent class is guarded, not promoted. The separate hand-written
  population is 308/308 proven.
- Complete local HeMan gate on exact source: **OK**. It built warning-free,
  passed 1,135 project tests, recomputed 320 receipt seals, killed every
  blocking mutant, passed restart/watcher/approval lanes, and passed the live
  228-file compatibility regression gate with zero losses or gains.
- Hosted run `34013237910` on exact source passed all nine leaf jobs and the
  protected aggregate `gate`.

## Review folds and disposition

- First Codex P2: the library persisted test did not cover Run.Host's earlier
  journal repair. Folded in `957eb32340409181ee2480b0308e34e12eedf032`.
- Independent follow-up: FileShare was advisory on Linux and did not close the
  cross-open race. Folded into immutable snapshot commit
  `667cb5504b109688540a775d34661ea392621372`.
- Second Codex P2: a durable terminal before a torn tail lost terminal no-op
  semantics. Folded in `363d8d3b57a450a7d5387ce1f523843f56af9d12`.
- Third Codex P2: direct runners still created a missing workspace root before
  agent refusal. Folded in final head `665b7bac`; independent engine review was
  clean and the missing-root regression was load-bearing.
- Final exact-head Codex correctness result `5557124732` is clean. Final
  exact-head security result `5557134185` found no security issue. All three
  review threads are resolved.
- Copilot exact-head formal review `5124265373` says it encountered an error.
  `scripts/review-coverage.py` classifies the presence of a current formal
  review as coverage, but this receipt does not convert that error into a
  substantive review. The pre-merge PR disposition records the custodian's
  authorized merge and keeps FG-253 PARTIAL for this sole remainder.

## Cleanup and handoff boundary

Disposable verification databases `fogell-fg253-final`,
`fogell-fg253-final2`, and `fogell-fg253-final3` were removed; no shared
container was stopped or changed. The Jenkins evidence probe was also cleaned
up as recorded in its retained bundle.

Concurrent PR #424 (`ff5798b303895c13d63af4c07e8e52b9b70d7acc`) remains
open and was not modified. This receipt changes no generated compatibility
count. The next custodian may close FG-253 only with a substantive Copilot
review of `665b7bac`, or an explicit owner reviewer exception recorded beside
this publication identity.
