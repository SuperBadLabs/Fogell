# Fogell custodian handoff — 2026-08-28 — FG-037

Status: **FG-037 is implemented, signed, reviewed on its exact source head,
green in both hosted gate contexts, merged into `main`, and accounted DONE.**

This is an outgoing-custody record. It describes the repository after source
PR [#183](https://github.com/SuperBadLabs/Fogell/pull/183) and accounting PR
[#184](https://github.com/SuperBadLabs/Fogell/pull/184) merged. It is not an
instruction to replay historical branches, trust stale checks, or delete old
worktrees.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- Observed `origin/main`:
  `dbadec590ab9ce8e986f892f26291bcb13cc6137`.
- Observed `origin/main` tree:
  `c0ca4a1695e9619aee982cd93547f6f9fa884651`.
- Source branch: `codex/fg-037-step-ceiling-final2-1fa6153`.
- Accounting branch: `codex/fg-037-accounting-closure`.
- Handoff branch: `codex/custodian-handoff-fg037-2026-08-28`.
- This document's PR and merge record identify the commit containing the
  handoff; do not insert a predicted self-reference into the file.

Fetch before branching. The outgoing work was deliberately based on refreshed
`origin/main`; a successor should not assume any local `main` or historical
worktree is current.

## Impact dimension closed

FG-037 establishes and continuously guards the claim that Fogell has no
Jenkins-style configured steps-per-stage ceiling through the required 400-step
bound. The accepted evidence is deliberately sharper than a large unit test:

1. A fast in-process Differential regression executes 400 ordered top-level
   steps and requires every unique marker exactly once and in order.
2. Retained adjacent inputs measure pinned Jenkins 2.568.1 and Fogell at 250,
   251, and 400 steps. Both engines succeed at 250. Jenkins fails before any
   workspace effect at 251 and 400; Fogell completes every step.
3. The 251 case retains the exact final-attempt raw Jenkins console and binds
   the 251-object `ArrayUtil` cause plus pre-execution CPS stack. The 400 case
   binds the exact 400-over-255 compiler diagnostic.
4. The retained manifest binds the exact 16-payload inventory and all hashes.
   A thin Git bundle reconstructs the signed measured source commit and tree
   from a prerequisite already on `main`.
5. Fresh probes build from a root-owned exact-HEAD export beneath `/run` using
   a run-exclusive systemd `DynamicUser`, freeze output root-owned before UID
   release, attest every regular output file before/between/after execution,
   and run the resulting CLI as the ordinary probe identity.
6. The blocking 33-arm proof rejects fourteen semantic, three controller
   identity, eleven collector/configuration, two manifest, and three source
   bundle attacks, including direct namespace/output mutation and a planted
   FIFO special file.

The complete contract, evidence identity, limits, and publication record are
in [`docs/tickets/FG-037.md`](tickets/FG-037.md). The comparison claim is also
carried by [`docs/COMPARISON-MATRIX.md`](COMPARISON-MATRIX.md),
[`docs/architecture/BASELINE.md`](architecture/BASELINE.md), and ADR 0005. The
canonical FG-037 board row is DONE.

## Measurement identity and nonclaims

The retained measurement predates the hardened publication head and remains a
separate identity:

```text
measured source commit  65674f9a4af80e358f645ad3409765a8738c68b4
measured source tree    7e09d220260b9117890cf4275fc240d989101f7c
manifest SHA-256        2944f2adde1122ab1d6cfd7cceb911e4b478a643156334ae79a9531fe2205891
evidence bundle         evidence/20260827T185436Z-fg037-step-ceiling
Jenkins core            2.568.1
SDK                     .NET 10.0.301
```

Preserve these boundaries:

- 400 steps proves the ticket's bound, not mathematical infinity.
- Only the 250-step control is compatibility evidence. The 251/400 cases are
  intentional capability divergences and stay outside canonical compatibility
  cases and receipts.
- The live Jenkins probe is not a CI gate. The fast Fogell regression and the
  retained evidence validators are the standing build coverage.
- The isolation closes ordinary concurrent publication-checkout and shared
  build-UID edits. It is not a security boundary against a malicious process
  already running as the same OS user, a host administrator, or the kernel.
- Do not rewrite the retained bundle to make its historical source identity
  equal the later publication identity.

## Source publication receipt

PR #183 merged at `2026-08-28T19:02:02Z`:

```text
reviewed signed head  1fa6153737e27212e1614d02567264e5e45b3a5d
source tree           47fcc2a82acfa4e7c04f05304108482da7aad04c
base                  244cad2b6425683767bbf7775a9ae7192b7ba09d
merge commit          6bad461abbf58839bc76ba9fe74389f81949570b
merge parents         244cad2b6425683767bbf7775a9ae7192b7ba09d
                      1fa6153737e27212e1614d02567264e5e45b3a5d
```

The source commit verifies with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`.

Exact-head GitHub evidence:

- push gate run `33200550061`, job `98948647320`: success;
- pull-request gate run `33200578856`, job `98948745017`: success;
- Copilot formal review `5054097248`: exact full head, no comments;
- Codex clean comment `5456435088`: reviewed `1fa6153737`, no major issues;
- `scripts/review-coverage.py --pr 183`: both expected reviewers covered the
  unchanged head immediately before merge.

The full gate reported 926/926 project tests:

```text
Controller.Api   38/38
Differential    281/281
Domain           34/34
Execution       101/101
Groovy          225/225
Journal          31/31
Parser          120/120
Store            96/96
Total           926/926
```

Every blocking evidence, security, restart, watcher, approval, backup/restore,
and migration rehearsal lane passed. The retained 16-payload manifest and all
three FG-037 receipt seals verified.

## Accounting publication receipt

PR #184 merged at `2026-08-28T19:23:29Z`:

```text
reviewed signed head  ab522329b3fe875850a4570767519688a805ff20
source tree           c0ca4a1695e9619aee982cd93547f6f9fa884651
base                  6bad461abbf58839bc76ba9fe74389f81949570b
merge commit          dbadec590ab9ce8e986f892f26291bcb13cc6137
merge parents         6bad461abbf58839bc76ba9fe74389f81949570b
                      ab522329b3fe875850a4570767519688a805ff20
```

The accounting commit verifies with the same ED25519 key. Exact-head evidence:

- push gate run `33202247290`, job `98954318103`: success;
- pull-request gate run `33202250918`, job `98954330503`: success;
- Copilot formal review `5054234966`: exact head, both files, no comments;
- Codex clean comment `5456635948`: reviewed `ab522329b3`, no major issues;
- the exact-head review coverage guard passed before and after merge;
- `scripts/audit-board-numbers.bb` and `scripts/prove-board-numbers.sh` passed.

## Current repository and queue snapshot

At `dbadec590ab9ce8e986f892f26291bcb13cc6137`, the derived board state is:

```text
rows=209; DONE=134; open=75; open P0/P1/P2/P3=3/25/35/12
compatibility ledger: tier1=1; tier3=28; admitted=199
```

The three canonical open P0 rows are:

- FG-026 — TODO: fenced effect checkpoint ledger;
- FG-041 — PARTIAL: `withEnv` remains outside the closed lexical scoping slice;
- FG-224 — PARTIAL: merged single-node controller acceptance lacks exact-head
  Copilot coverage and retains its documented single-node/residual boundaries.

This is inventory, not an instruction to start one of them. The live queue has
substantial historical prose; canonical rows, ticket documents, ancestry, and
current GitHub records are the evidence boundary.

## Published work still in flight

Two pull requests were open at this snapshot.

### PR #181 — FG-226 fflat audit-tool port

[#181](https://github.com/SuperBadLabs/Fogell/pull/181) is the newest active
candidate, but it is not merge-ready:

```text
head                   69a6f22486abbab781fa974fac33c4715ce0cb13
base recorded by PR    244cad2b6425683767bbf7775a9ae7192b7ba09d
current merge state    dirty / conflicting with main
push gate              33196980897 — failure
pull-request gate      33196985961 — failure
review coverage        Codex exact; Copilot earlier head only
```

The push gate reached the scorecard receipt-mapping proof and failed because
the hosted runner had no corpus at
`/sn8100/work/exchange/crucible-gate/corpus`; both hosted jobs report their
`gate` step failed. Do not paper over that as runner noise: FG-226 changes the
gate/tool boundary, so hosted reproducibility is part of its acceptance.

Seven exact-head inline findings remain visible: Copilot comments
`3882222652`, `3882222711`, `3882222770`, `3882222806`, and `3882222844`, plus
Codex P2 comments `3882224452` and `3882862008`. The two load-bearing Codex
findings are:

- `run-differential.sh` can execute a stale `sync-scm-cases` binary after the
  audit build fails, despite claiming the sync was skipped;
- `probe-input.fsx` tolerates an unavailable initial Jenkins request and can
  produce a false-green timeout/restart transcript.

If the next custodian takes this baton, branch from current `origin/main`,
reconcile the candidate rather than merging the dirty PR, triage all seven
findings, fix the hosted corpus contract, rerun the authoritative HeMan gate,
and publish a fresh exact-head PR if Copilot must be retriggered. An earlier
review or green local head does not carry across reconciliation.

### PR #163 — FG-073 current-tree threat model

[#163](https://github.com/SuperBadLabs/Fogell/pull/163) remains open at head
`1c30031fb6298646ab5a66eeeba745f5891036b7`, based on
`804bf7967cf3708eb3bb44387d59a24310c89607`. Its two historical gate runs passed,
but they predate many `main` advances and do not cover a conflict resolution or
changed head. Treat it as a reconciliation candidate, not merge evidence.

## Local workspace inventory

`git worktree list` shows the canonical worktree plus many historical ticket
worktrees under `${HOME}/projects/fogell-worktrees`, a Claude worktree beneath
`.claude/worktrees`, and detached temporary worktrees under `/tmp`. One `/tmp`
entry is reported prunable because its gitdir is gone.

No worktree, branch, or remote branch was deleted during this custody. Their
presence does not establish ownership, cleanliness, publication state, or
abandonment. Inspect the exact branch, status, ancestry, and open-PR linkage
before cleanup; use recoverable cleanup where practical.

## Safe opening move for the next custodian

1. Fetch `origin`, record the exact current `origin/main`, and create a new
   `codex/` branch from it.
2. Run `scripts/audit-board-numbers.bb`, inspect open PRs, and inventory
   worktrees before trusting queue prose or local branch names.
3. Decide explicitly whether to reconcile PR #181, reconcile PR #163, or start
   a fresh impact dimension. Do not combine those scopes implicitly.
4. If taking #181, begin with the two false-green/stale-binary findings and the
   hosted corpus failure; then rederive its board/accounting changes against
   the 209-row, 134-DONE baseline.
5. Run the selected ticket's narrow falsifying proof, then the unmodified
   authoritative HeMan gate with its required PostgreSQL, corpus baseline, and
   Jenkins oracle.
6. Publish only the already-proven signed head. Require exact-head Copilot and
   Codex coverage plus all required hosted checks; after any Copilot finding
   changes the head, follow FG-199 and use a fresh replacement PR.
7. Merge only the exact reviewed and green head, then record the fetched merge
   identity before moving a board row to DONE.

The watch is complete. Take the baton; welcome to the jungle.
