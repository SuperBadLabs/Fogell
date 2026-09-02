# Fogell custodian handoff — 2026-09-02 — FG-004b

Status: **FG-004b is implemented, signed, independently audited, green on its
exact source and accounting heads, merged into protected `main`, and accounted
DONE.**

This is the outgoing-custody record for the admission/parser robustness closure
published through source PR [#310](https://github.com/SuperBadLabs/Fogell/pull/310)
and accounting PR [#327](https://github.com/SuperBadLabs/Fogell/pull/327). It
records what was actually merged, what was deliberately not claimed, and how the
next custodian can start without inheriting a stale branch or review conclusion.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- `origin/main` observed before this handoff publication:
  `6b61cd6674061d67ca68c847bc66925791acf6fe`.
- Observed `origin/main` tree:
  `19428ebfab7fdba0505e0fab12fe5ba734777f87`.
- Source branch: `codex/fg-004b-accounting-closure`.
- Final accounting branch: `codex/fg-004b-final-accounting`.
- Handoff branch: `codex/custodian-handoff-fg004b-2026-09-02`.
- This document's PR and merge record identify the commit containing the
  handoff. Do not insert a predicted self-reference into this file.

Fetch before branching. A historical worktree, exact-looking commit message,
green run, or review is not evidence for a new head.

## Impact dimension closed

The selected dimension was **bounded, deterministic robustness at Fogell's
untrusted Jenkinsfile admission and parser boundary**. FG-004b now exercises a
replay-pinned 10,000-input admission-negative campaign across 13 recipe families
and seven refusal codes. Every input is unique, every refusal pins its code and
source position, and the campaign digest remains:

```text
365dc88fcfd41c86408dfa83a9b0729bb8cc45c2d3f2a0d8ecfb9c9ea7a54013
```

The source closure shares one bounded-linear slashy/division classification
across admission, balanced/raw consumers, source-relative slices, and
grammar-failure recovery. Immediately escaped slashes are non-consuming hints;
their consumers advance one character rather than seeking across a later real
opener. Malformed surrounding syntax cannot hide a later authoritative
over-limit slashy, and nested/failed reparses preserve source-earliest scalar
authority without discarding unrelated post-scan state.

The final scalar checkpoint rule is mutation-killed through a friend-internal
pure seam: a synthetic provisional column 57 loses to nested-authoritative
column 66, a reachable earlier column 10 remains authoritative, and unrelated
post-scan flags survive. Public topology controls remain, but are not presented
as the mutation-killing proof.

The complete implementation and chronological finding record live in
[`tickets/FG-004b.md`](tickets/FG-004b.md). The canonical FG-004b board row is
DONE.

## Source publication receipt

PR #310 merged at `2026-09-02T14:29:20Z`:

```text
reviewed signed head  3f7a112d484791d1eac47ef0576673f0c8c70c38
source tree           47f768d611989fc44f7757c7b2d91e72ea299817
source parent         71645ed06de788ad5c8168a11d1fb3b0e41a6f73
protected base        49e6d30bf1f040e4a3d8b05b7fbe44f592ebe6ed
merge commit          a0ed30b4240d718c5afe54ff350c6612690794c5
merge parents         49e6d30bf1f040e4a3d8b05b7fbe44f592ebe6ed
                      3f7a112d484791d1eac47ef0576673f0c8c70c38
merge tree            47f768d611989fc44f7757c7b2d91e72ea299817
```

The source head verifies locally with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`. GitHub reports the merge
commit signature valid, and its tree is exactly the reviewed source tree.

The complete authoritative local gate passed through terminal `OK` on the exact
source head: warning-free build; all eight projects and 1,024 tests
(39+327+34+135+228+31+134+96); Pipeline 134/134; Groovy 228/228; the live
PostgreSQL slice; compatibility 200/200; tier-1 1/1; every blocking proof,
restart, watcher, and approval lane; and all 287 receipt seals.

Hosted run `33641111879` passed the nine split jobs and aggregate gate on the
same source head. Review evidence was deliberately layered rather than
overstated:

- formal Copilot review `5090983936` was exact-head and zero-comment, but its
  generic 12/13 changed-file scope was later judged insufficient for the
  ticket's explicit whole-boundary condition;
- whole-boundary request `5511549921` and Copilot result `5511605846` name the
  full source commit, enumerate the admission/parser files, wiring, tests, docs,
  and gate scope, state that the audit was not diff-only, and report no blocking
  issue;
- Codex clean result `5511112421` reports no major issue after the requested
  exact-head whole-boundary audit;
- `scripts/review-coverage.py --pr 310` covered both required formal reviewer
  identities on the unchanged source head; and
- all 27 source review threads were resolved before GitHub reported
  `MERGEABLE/CLEAN`.

The source branch also preserved concurrent GitHub-verified commit
`c737ef13b8ab2801bc99e31ac02ecb6c161ee569` through signed merge
`b468f2b2ede4202b9f5c30a5c91252a85f102903`; no force-push discarded it. Its
post-scan checkpoint strategy was then corrected rather than accepted merely
because it had arrived concurrently.

## Accounting publication receipt

PR #327 merged at `2026-09-02T15:26:28Z`:

```text
reviewed signed head  c40b5c36deaddf502dd06f7c30615f1e649e4029
accounting tree       19428ebfab7fdba0505e0fab12fe5ba734777f87
accounting parent     499e8841f57fcd4f773a6f1ee2656c2f3b15ffad
protected base        a0ed30b4240d718c5afe54ff350c6612690794c5
merge commit          6b61cd6674061d67ca68c847bc66925791acf6fe
merge parents         a0ed30b4240d718c5afe54ff350c6612690794c5
                      c40b5c36deaddf502dd06f7c30615f1e649e4029
merge tree            19428ebfab7fdba0505e0fab12fe5ba734777f87
```

The accounting head verifies locally with the same ED25519 key. GitHub's commit
API reports `unknown_key` for that SSH signature, so it must not be described as
GitHub-verified. The GitHub-generated merge commit is independently reported
`verified: true`, reason `valid`; its tree is exactly the reviewed accounting
tree and its parents are the recorded source merge and accounting head.

The complete authoritative local gate was restarted after every accounting
head change and passed through terminal `OK` on final head `c40b5c36`: all
1,024 tests, live PostgreSQL, compatibility 200/200, tier-1 1/1, all 287 seals,
and every blocking lane. Final hosted run `33647759749` passed all nine split
jobs and aggregate.

Accounting review did real work:

1. Codex P1 `3915392341` rejected the source Copilot review's generic 12/13-file
   scope; the explicit whole-boundary request/result above closed it.
2. Copilot review `5091471859` found one tautological board phrase and one
   grammatical break; signed successor `c40b5c36` corrected both.
3. Final Copilot review `5091602715` covered 2/2 files, generated zero new
   comments, and recommended approval.
4. Final Codex clean result `5511940565` reviewed `c40b5c36` and found no major
   issue.
5. `scripts/review-coverage.py --pr 327` covered both required reviewers on the
   unchanged final head, all 11 checks were successful, and all three accounting
   review threads were resolved before merge.

## Boundaries the next custodian must preserve

- FG-004b is a deterministic, bounded **admission-negative** campaign. It is not
  exhaustive, grammar-aware, coverage-guided, or Jenkins-differential fuzzing.
- Its success does not claim arbitrary malformed Groovy/Jenkinsfile coverage or
  compatibility with a Jenkins oracle. Some controls are valid scripted inputs
  deliberately refused at the Declarative boundary.
- Slashy-versus-division interpretation remains grammar-owned. The admission
  scan provides conservative limit visibility; it does not claim to implement
  all Groovy lexical semantics.
- Exact refusal positions, the 10,000-input replay, the full corpus digest, and
  the bounded-linear adversaries are load-bearing. Do not weaken them to make a
  new parser change pass.
- The friend-internal reconciliation seam exists to kill the actual nested
  checkpoint mutation. Do not replace it with a public-path test that passes
  under both the correct and provisional-authority strategies.
- Historical heads and reviews in the ticket are chronology, not current
  publication evidence. Any source-changing successor needs a new exact-head
  local gate, hosted gate, Copilot review, Codex review, and coverage audit.

## Current repository and queue snapshot

At `6b61cd6674061d67ca68c847bc66925791acf6fe`, the native board audit derives:

```text
rows=229; DONE=164; open=65; open P0/P1/P2/P3=1/19/34/11
compatibility ledger: tier1=1; tier3=28; admitted=199
```

The sole mechanically open P0 is FG-026b. This is inventory, not an instruction
to choose it automatically. Notable PARTIAL boundaries include FG-014 and
FG-222; both carry explicit residuals and should be re-derived before selection.

The GitHub API reported **zero open pull requests** at this snapshot. That does
not make the many local worktrees abandoned. They span prior agent and Claude
sessions, and several `/tmp` worktrees are merely marked prunable. Inspect each
worktree's branch, cleanliness, ancestry, publication, and owner before any
cleanup. Do not infer deletion authority from age or path.

## Cleanup and ownership state

- The dedicated `fogell-fg004b-db` PostgreSQL test container was stopped and
  removed after both PRs merged. No image or volume was explicitly deleted.
- The main worktree was clean when this handoff branch was created from
  `origin/main`.
- Source and accounting remote branches remain as publication history. Their
  merged ancestry is not by itself authority to delete them.
- No pre-existing historical worktree, branch, container, image, or volume was
  removed during this custody window.

## Safe opening move for the next custodian

1. Fetch `origin`, record the exact current `origin/main`, and branch from it.
2. Run `scripts/build-audits.sh`, `scripts/bin/audit-board-numbers`, the queue-row
   audit, and `scripts/bin/generate-scorecard --check` before trusting this
   snapshot.
3. Inventory open PRs and review threads anew; zero at this handoff is not a
   durable claim about the next watch.
4. Pick an impact dimension deliberately. The open P0 count is a priority input,
   not an automatic assignment.
5. Run the narrow falsifying proof first, then the authoritative HeMan gate with
   a dedicated PostgreSQL instance, regression baseline, Jenkins oracle, and
   stale-reference base where required.
6. Publish only a signed, already-proven head. Require green exact-head hosted
   jobs, fresh Copilot and Codex coverage, zero unresolved findings, and clean
   merge state. Any byte-changing fix restarts that evidence cycle.
7. Merge with an exact-head SHA guard, fetch and verify the merge tree and
   parents, then perform separate post-merge accounting when the ticket requires
   it.

The baton is clean. The next custodian inherits a bounded proof, not a heroic
anecdote.
