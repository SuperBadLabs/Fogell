---
title: Related work — McLoving, current state
audience: mixed
category: engineering-reference
purpose: Record what McLoving is today, what Fogell can take from it, and the boundary rules for doing so — because Fogell's picture of its ancestor is a year of PRs stale.
lifecycle: live
last-verified: 2026-08-05
---

# Related work: McLoving

Status: informational. This document grants no authority, transfers no
evidence, and changes no ticket status. It exists so Fogell work can consult
McLoving's measurements and completed techniques instead of rediscovering
them, under the boundary rules below.

## Why this document exists

Fogell already knows McLoving as an ancestor: `adr/0007` and `adr/0008` credit
it for the durability and evidence discipline, and the board's lineage note
names it. What the board does NOT record is McLoving's current state — and it
has moved. The picture below is from protected `main` at `b75165d` (PR #33),
board updated 2026-08-05.

- Repository: `git@github.com:SuperBadLabs/McLoving.git`, private. Working
  checkouts live on HeMan under `/sn8100/work/forge/McLoving-*`, one per
  `codex/` ticket branch. A reverse dossier introducing Fogell to McLoving
  exists at its `docs/related-work/FOGELL.md` (branch
  `codex/related-work-fogell`, prepared 2026-08-05).
- Board: 75 DONE, 29 PENDING, 2 ACTIVE. Waves 0–3 (architecture, end-to-end
  slice, durability, native product surface) are closed. The
  persistent-Windows campaign is CLOSED — signed package on a persistent
  Windows host through controller/network interruption and machine reboot.
- The Jenkins migration campaign (its Wave 4) is well underway: a sealed
  Jenkins inventory from the Mario `jenkins-oracle-228` population, an
  isolated compiler worker, a sealed corpus, exact authorization mapping, and
  external read/write cutover gates. Active slot: `SCM-001`.

## The race is now real

McLoving kept the bet Fogell rejected. Its `MIG-001` compiler worker is the
compile-to-IR plane whose measured 74.3% lowering tax is Fogell's founding
argument (`adr/0002`) — but hardened: rootless Podman, no network, read-only
root and source, pinned Java/Groovy/Jenkins-core/WAR/image/90-plugin hashes,
bounded CPU/memory/PIDs/FDs/time/input/output, with deterministic,
hostile-input, and authority-negative gates.

Its `MIG-002` corpus reports oracle signals identical to Fogell's baseline
scorecard: 228 sources, 80 Declarative-valid, 199 compile/CPS entry, and 119
reached-agent-scheduling — the same denominator this board accepts. Same
corpus lineage, same Jenkins 2.568.1 oracle version, different host and
plugin provenance (Mario vs luigi).

Two engines, opposite architectures, one corpus. When McLoving's campaign
emits per-file results, the per-file comparison against Fogell's ledger is
the cheapest arbitration of `adr/0002` either project will ever get. Note the
current asymmetry honestly, in both directions: McLoving's own board records
"native runnable and certified equivalence remain zero" — Fogell leads on
proven execution parity (63/63 tier-1, ~49 receipts). McLoving leads on
everything around the engine: platform, fleet, and operations.

## What McLoving has that Fogell can use

Ranked by which open FG ticket each unblocks.

1. **FG-038a (Windows executor).** `WIN-004` is the finished form of what the
   ticket sketches: every workload created SUSPENDED with atomic
   kill-on-close Job membership via `PROC_THREAD_ATTRIBUTE_JOB_LIST`, durable
   process identity recorded before resume, restricted inherited-handle list,
   and native forced-crash gates at every creation boundary proving no
   escaped process. The persistent-host campaign behind it is closed, so the
   technique is production-proven, not a design note.
2. **Cancellation identity (FG-032's hard edge, and the abort-cause class).**
   `AGENT-006`: journal rows persist Linux boot ID plus `/proc` birth ticks;
   cancellation revalidates identity before TERM and KILL, never signals a
   recycled PGID, never treats a missing group leader as proof of an empty
   group, and returns distinct completed / already-exited / retire-stale /
   reconciliation-required outcomes. Fogell's five-times-recurring
   abort-cause lesson has a sibling here: theirs is identity, ours is
   ordering, and a Fogell agent runtime will need both.
3. **FG-062 (agent protocol).** `AGENT-001/004/005` are a complete, proven
   contract: outbound mTLS with rotation and session epochs,
   journal-before-ack acceptance in SQLite WAL, fenced start/lease/
   cancellation, lease-loss execution cancellation, bounded streamed log
   publication, and exact terminal replay after crash — forced response-loss
   and agent-crash gates converge to one terminal event. Their partition bar
   is the one FG-062 already states: a ≥45 s partition costs zero log lines.
4. **Store invariants for free (FG-022/023 hardening).** `ARCH-001` is a
   finite TLC model checking lease typing, stale-publication rejection,
   fencing, terminal monotonicity, and completion stability in CI. Fogell's
   fence/epoch design is near-isomorphic; porting the model is days, not
   weeks, and it checks the invariants Fogell currently proves only by
   concurrent-test evidence.
5. **A corpus-integrity lesson at inventory-construction time.** McLoving's
   first sealed inventory was WRONG — 220 of 230 inline-source digests
   truncated because the exporter ignored XML `GeneralRef` events — and the
   error was caught only by a reconciliation pass, after sealing. The repair
   discipline matches this board's own: the bad inventory stays immutable as
   a rejected predecessor; a create-new successor reconciles all 230 with
   byte-exact digests and four explicit CRLF LF-normalization receipts. That
   last item is FG-113's declared-weakening rule, arrived at independently.
6. **FG-111's shape (SCM lane).** `SCM-001` is McLoving's active slot. Their
   source/trigger boundary work will hit the same problems FG-111 names —
   a real repo, a `CHANGE_ID`, a populated changelog — first. Watch it.

## What Fogell has that McLoving lacks

For symmetry: proven execution parity (63/63 tier-1 differential cases,
~49 sealed receipts, per-construct measured Jenkins semantics in `adr/0005`),
demand counts counted rather than recalled, the five-class review-findings
taxonomy (cancellation ordering, string provenance, abort-cause, fail-closed,
narration), and the claim-audit gate (`scripts/bin/audit-claims`). The reverse
dossier records these for their side.

## Boundary rules (non-negotiable, mirrored in their copy)

1. **Receipts do not transfer.** A McLoving gate proves McLoving-vs-Jenkins.
   It licenses no Fogell claim, no PROVEN status, no board row. The reverse
   holds equally.
2. **Measurements inform; re-measurement claims.** McLoving's measurements of
   JENKINS behavior may shape a ticket and its expected outcome, but the
   Fogell receipt must come from Fogell's harness against Fogell's pinned
   oracle before anything is recorded as proven.
3. **No scalar compatibility percentage**, theirs or ours, ever published.
4. **Read-only, with provenance.** Fogell work may read McLoving's docs,
   models, and source for insight. Anything that crosses over carries
   provenance in the commit message and passes this project's own review and
   evidence gates as if written fresh.
