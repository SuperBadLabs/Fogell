# Fogell custodian handoff — 2026-08-29 — FG-223

Status: **FG-223 is implemented, signed, reviewed on its exact source head,
green in both hosted gate contexts, merged into `main`, and accounted DONE.**

This is an outgoing-custody record. It describes the repository after source
PR [#209](https://github.com/SuperBadLabs/Fogell/pull/209) and accounting PR
[#211](https://github.com/SuperBadLabs/Fogell/pull/211) merged. It is not an
instruction to replay a historical branch or trust a stale check.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- Observed `origin/main`:
  `cdf5a2f0a99861f9d9361d109b5a4d3b52ffb87b`.
- Observed `origin/main` tree:
  `1bc93c2b7ca841d91d584536827fdae1aba34762`.
- Final source branch: `codex/fg-223-evidence-source-binding`.
- Final accounting branch: `codex/fg-223-accounting-closure-r2`.
- Handoff branch: `codex/custodian-handoff-fg223-2026-08-29`.
- This document's PR and merge record identify the commit containing the
  handoff; do not insert a predicted self-reference into the file.

Fetch before branching. A local branch name, worktree, or earlier green run is
not evidence that the checkout is current.

## Impact dimension closed

FG-223 makes evidence sealing fail closed. The sealer now refuses a failed
corpus check, Release build, test project, incomplete test inventory, unsafe
input, or source drift instead of publishing a checksum-valid failure bundle.
It stages privately, verifies its manifest, and publishes atomically without
overwriting an existing destination.

The closure also binds the measured source rather than trusting the publishing
checkout:

1. A binary/full-index candidate is derived through a private index.
2. Every sealer-controlled snapshot, materialization, and audit Git operation
   disables hooks and fsmonitor. Prerequisites run in an isolated worktree and
   inherit a Git environment scrubbed of ambient repository, worktree, index,
   object, and replace-ref redirects.
3. Active content filters, ignored/untracked materialized inputs, gitlinks,
   unsupported index modes, and symlinks that escape the candidate or enter
   Git administration fail closed.
4. Physical bytes, file type, executable mode, index identity, source status,
   HEAD, and inventory are audited before and after measurement.
5. Every tracked `tests/**/*.fsproj` is inventoried recursively with explicit
   Git glob semantics, and producer failure is load-bearing.
6. The blocking hostile proof covers prerequisite failure, races, hooks,
   filters, symlinks, binary/staged/unstaged identity, foreign Git state,
   SHA-1 and SHA-256 repositories, and atomic no-clobber publication.

The complete contract and publication evidence are in
[`tickets/FG-223.md`](tickets/FG-223.md). The canonical FG-223 board row is
DONE.

## Evidence identity and nonclaims

Preserve these boundaries:

- The seal proves evidence-command integrity. It does not attest the semantic
  quality of passing tests, replace differential receipts, or grant GitHub
  publication authority.
- It is not an operating-system boundary against a malicious same-UID process
  that deliberately discovers and rewrites the temporary worktree.
- A malicious prerequisite could mutate and restore source entirely between
  its own reads and the final materialized-source audit.
- Refusal of ignored inputs, active filters, unsafe symlinks, gitlinks, and
  unsupported index modes is intentional fail-closed behavior, not an
  unfinished acceptance item.
- The committed `evidence/20260825T121723Z-fg-223/` bundle predates the
  expanded isolated-source boundary and is not closure evidence for the final
  source head.
- The pre-fix checksum-valid false-seal control remains machine-local at
  `/tmp/fg223-pre-fix-negative-evidence`, with manifest digest
  `24899e95576922a4329145c2c5c3e83cc64997d8adca68460052f08ea5a28a0e`.
  A path in `/tmp` is not durable repository evidence.

## Source publication receipt

PR #209 merged at `2026-08-29T12:20:00Z` (07:20 in America/Chicago):

```text
reviewed signed head  ec85ef5816baad21f868dc9d46dffbc36b33d30a
source tree           ecab44314d1457da2f0f3fe1ad5a45fe1f1ae2f8
base                  fc8ffbb29c11527d224827d601cdf5e76075fd9a
merge commit          e6ac836b96e66562fb370ddd5dbe9db44d6e20d2
merge parents         fc8ffbb29c11527d224827d601cdf5e76075fd9a
                      ec85ef5816baad21f868dc9d46dffbc36b33d30a
```

The source and merge trees are identical. The source commit verifies with
ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`.

Exact-head GitHub evidence:

- push gate run `33251218480`, job `99097105032`: success;
- pull-request gate run `33251225260`, job `99097121946`: success;
- Copilot formal review `5058026163`: exact source head;
- Codex clean comment `5462291328`: reviewed `ec85ef5816`;
- `scripts/review-coverage.py --pr 209`: both reviewers covered on the
  unchanged head.

The sole inline thread was resolved. Copilot comment `3886538074` claimed a
zero-iteration Bash `while` pipeline failed under `pipefail`; exact-shell and
black-box evidence in rebuttal `3886543424` showed that premise false. The
source did not change, and no unresolved review thread remained at merge.

The authoritative gate passed 926/926 project tests (API 38, Diff 281, Domain
34, Execution 101, Groovy 225, Journal 31, Parser 120, Store 96), FG-207's 8/8,
26/26 lock tests and mutation controls, the complete FG-223 proof, the live
228/228 compatibility regression, restart/watcher/approval lanes, and terminal
`OK`. The focused proof separately passed with SHA-1 and SHA-256 repositories,
an unused global LFS driver, and relative tool paths.

## Accounting publication receipt

PR #211 merged at `2026-08-29T12:51:44Z` (07:51 in America/Chicago):

```text
reviewed signed head  8bb32ca753e8630cdddc82214a1e3700f9e38f68
accounting tree       1bc93c2b7ca841d91d584536827fdae1aba34762
base                  e6ac836b96e66562fb370ddd5dbe9db44d6e20d2
merge commit          cdf5a2f0a99861f9d9361d109b5a4d3b52ffb87b
merge parents         e6ac836b96e66562fb370ddd5dbe9db44d6e20d2
                      8bb32ca753e8630cdddc82214a1e3700f9e38f68
```

The accounting and merge trees are identical. The accounting commit verifies
with the same ED25519 key. Exact-head evidence:

- push gate run `33252694905`, job `99101007105`: success;
- pull-request gate run `33252704505`, job `99101032092`: success;
- Copilot formal review `5058077075`: exact head, both files, no comments;
- Codex clean comment `5462461035`: reviewed `8bb32ca753`;
- `scripts/review-coverage.py --pr 211`: both reviewers covered;
- the inline comment and review-thread inventories were empty.

The native board audit derived rows=210, DONE=136, open=74, and open
P0/P1/P2/P3=3/24/35/12. Its hostile mutation proof, the queue prose audit, and
`git diff --check` passed.

Accounting PR #210 was retired after valid Codex finding `3886583376`: it
updated the totals on 2026-08-29 but left `accounting-verified` at 2026-08-28.
Both in-flight hosted runs were canceled, #210 was closed, and #211 republished
the complete correction as one fresh signed commit. No check or review from
#210 carried forward.

## Current repository and queue snapshot

At `cdf5a2f0a99861f9d9361d109b5a4d3b52ffb87b`, the derived board state is:

```text
rows=210; DONE=136; open=74; open P0/P1/P2/P3=3/24/35/12
compatibility ledger: tier1=1; tier3=28; admitted=199
```

The three mechanically open P0 rows are FG-026, FG-041, and FG-224. This is
inventory, not an instruction to choose one automatically. FG-041 needs semantic
accounting reconciliation before prioritization: its row names FG-041b as the
sole outstanding `withEnv` residual, while FG-041b is already DONE. Nearby
queue prose also calls FG-042 open although its canonical row is DONE.

Two pull requests remain open:

- [#200](https://github.com/SuperBadLabs/Fogell/pull/200), FG-226 accuracy and
  FG-227 follow-up, at head
  `8b12db61cbe29754c7739ca22a52466a31b8065f`, is conflicting against current
  `main`. Its old green runs predate FG-223 accounting and are not merge
  evidence; rebase, re-account, and re-review it.
- [#163](https://github.com/SuperBadLabs/Fogell/pull/163), FG-073 threat-model
  closure, at head `1c30031fb6298646ab5a66eeeba745f5891036b7`, is conflicting
  against current `main`. FG-073 remains TODO, but this historical head must
  be refreshed and re-proven or closed.

The repository also has many pre-existing local worktrees. Their age is not
proof of abandonment. Inspect branch, cleanliness, ancestry, publication, and
ownership before removing any of them.

## Safe opening move for the next custodian

1. Fetch `origin`, record exact `origin/main`, and branch from it.
2. Run `scripts/build-audits.sh`, the native board audit, and the canonical
   row audit before trusting queue totals or an old worktree.
3. Reconcile the FG-041/FG-041b status and inspect PRs #200 and #163 before
   treating the mechanical queue as semantic priority.
4. Choose an impact dimension explicitly. Run its narrow falsifying proof,
   then the authoritative HeMan gate with PostgreSQL and the external FG-094
   baseline and Jenkins oracle where required.
5. Publish only a signed, already-proven head. Require exact-head Copilot and
   Codex coverage plus both hosted gate contexts. A head-changing Codex fix
   requires a fresh `@codex review`; a head-changing Copilot fix requires a
   replacement PR because Copilot cannot be re-requested. In both cases, rerun
   every exact-head gate and coverage check.
6. Merge with an exact-head guard, fetch the merge identity, and only then move
   canonical accounting to DONE.

The watch is complete. Take the baton; welcome to the jungle.
