# Fogell custodian handoff — 2026-08-29 — FG-227

Status: **PR #200 is retired without merge; its defensible corrections are
merged through PR #216; FG-226 and FG-041 are accounted DONE; FG-227 remains
deliberately PARTIAL.**

This is an outgoing-custody record for the evidence-integrity reconciliation
that followed PR #200. It distinguishes the production result from the
invalidated benchmark and leaves the next custodian a reproducible boundary,
not an inherited conclusion.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- `origin/main` observed before this handoff publication:
  `ea6480ed12141ab51f0f5d7fdd84cef8e50b2768`.
- Observed `origin/main` tree:
  `d2295a4af00691156d6beaf56cba881691fbf04b`.
- Final remediation branch: `codex/fg-226-227-reconciliation-v4`.
- Handoff branch: `codex/custodian-handoff-fg227-2026-08-29`.
- This document's PR and merge record identify the commit containing the
  handoff. Do not insert a predicted self-reference into this file.

Fetch before branching. A historical worktree, green run, review, or branch is
not evidence for a new source head.

## Impact dimension closed

The dimension was **claim integrity at the executable evidence boundary**.
PR #200 combined useful FG-226 documentation corrections with a proposed DONE
answer to whether running the F# audit sources under `dotnet fsi` was preferable
to compiling them with fflat. Review showed that the benchmark did not execute,
count, retain, or model the claimed paths well enough to support any published
ratio, floor, break-even, or full-gate net.

The closure did four things:

1. Preserved FG-226's proven compiled production path and corrected its stale
   source inventory and deliberate-deviation record.
2. Expanded the hostile `probe-input` proof to nine fail-closed setup/restart
   arms, including all five Jenkins POST boundaries.
3. Retracted the unsupported performance claims into a new FG-227 PARTIAL
   ticket with falsifiable acceptance criteria.
4. Reconciled current board accounting, including moving stale FG-041 to DONE
   after its only named residual had already closed under FG-041b.

## Why PR #200 was not mergeable evidence

[PR #200](https://github.com/SuperBadLabs/Fogell/pull/200) was closed without
merge at old head `8b12db61cbe29754c7739ca22a52466a31b8065f`.
At retirement GitHub reported it conflicting/dirty against current `main`.
Its ten unresolved review threads were intentionally preserved as historical
evidence rather than marked resolved on an obsolete head.

Its green runs were real but stale relative to the replacement:

- push gate run `33246207450`, job `99084001890`;
- pull-request gate run `33246209825`, job `99084007913`.

Neither run nor any #200 review carries into PR #216. Copilot's recorded
no-comment review did not cover #200's final old head.

The deeper benchmark defects are recorded in
[`tickets/FG-227.md`](tickets/FG-227.md):

- direct `dotnet fsi scripts/fsx/*.fsx` does not invoke these scripts'
  `[<EntryPoint>] main`; it can emit FS2304 and exit zero without auditing;
- the unretained runner also changed `Environment.ProcessPath`, allowing
  repository-root discovery to resolve under `/usr`;
- the 134-call inventory missed temporary mutant binaries and multiple
  generator calls;
- the four-core model reused the 32-core compile result even though
  `build-audits.sh` compiles the eight tools sequentially under four-core
  affinity; and
- the proposed approximately `+39.7 s` full net depended on an unreproduced
  savings term inconsistent with the retained FG-226 subtotal.

Accordingly, the proposed 10.7x ratio, 7.6-invocation break-even, 4.1x
four-core floor, and approximately `+39.7 s` net are retracted. No replacement
headline number was published.

## What PR #216 published

[PR #216](https://github.com/SuperBadLabs/Fogell/pull/216) merged at
`2026-08-29T16:13:45Z`:

```text
reviewed signed head  77a8cbeb7d439a5db0b576e2d6bbab2a325233fd
source tree           d2295a4af00691156d6beaf56cba881691fbf04b
base                  270997643986bb2a7d22fe38c9dfbc3a15ac2535
merge commit          ea6480ed12141ab51f0f5d7fdd84cef8e50b2768
merge parents         270997643986bb2a7d22fe38c9dfbc3a15ac2535
                      77a8cbeb7d439a5db0b576e2d6bbab2a325233fd
merge tree            d2295a4af00691156d6beaf56cba881691fbf04b
```

The source head verifies with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`.
The merge commit's tree is exactly the reviewed source tree and its parents are
the recorded base and reviewed head. The local keyring does not contain
GitHub's RSA merge-signing public key, so it reports the merge signature as
uncheckable rather than asserting a locally verified signature.

The merged changes are:

- a new `docs/tickets/FG-227.md` with the invalidation and acceptance boundary;
- corrected FG-226 counts, cost framing, and four-deviation inventory;
- an exact source comment describing `probe-input`'s intentional fail-closed
  difference from the babashka original;
- hostile proof cases for initial-delete 500, create 403, build 500,
  input-action 500, and final-delete 500; and
- board rows and derived accounting aligned with FG-041 DONE, FG-226 DONE, and
  FG-227 PARTIAL.

The nine FG-226 refusal arms are one separately exercised unreachable loopback
endpoint plus eight request-ready fixture states: missing crumb, invalid crumb,
initial-delete failure, create failure, build failure, input-action failure,
final-delete failure, and restart no-op. Slow restart polling and active-version
and launcher-diagnostic controls are separate proof obligations, not inflated
into that count.

## Exact-head proof and review receipt

The authoritative `./scripts/build-and-test.sh` passed from a fresh detached
checkout of exact source head
`77a8cbeb7d439a5db0b576e2d6bbab2a325233fd`. The run used a dedicated
PostgreSQL database, the pinned regression baseline, Jenkins oracle, and a
resolvable stale-reference base. Every blocking proof and the Store,
runnable-controller, backup/restore, migration-rollback, restart, and approval
lanes completed successfully.

Both fresh hosted contexts passed on the same head:

- push gate run `33261234630`, job `99123426381` — success;
- pull-request gate run `33261263738`, job `99123506581` — success.

Exact-head review evidence:

- Copilot review `5058519485` covered all five changed files at
  `77a8cbeb7d439a5db0b576e2d6bbab2a325233fd` and generated no comments;
- Codex comment `5463374666` explicitly reviewed `77a8cbeb7d` and reported no
  major issues;
- `scripts/review-coverage.py --pr 216` passed on the unchanged head; and
- the final GraphQL inventory contained zero review threads.

Replacement PRs #213, #214, and #215 were each retired when review changed the
candidate bytes. Copilot comment `3886891140` on #213 found a duplicated cost
fragment. Codex comment `3886923854` on #214 found a stale six-arm proof count.
Codex review `5058486878` on #215 found that the detailed inventory still
enumerated the original six cases. Each valid finding was fixed in a signed
commit and sent through a fresh PR; no predecessor review was presented as
coverage for the corrected head.

## Boundaries the next custodian must preserve

- FG-226's compiled fflat path is the current proven production choice. FG-227
  does not reopen or invalidate that implementation.
- FG-226's historical `+8.42 s` table is only a subtotal over its listed rows,
  not a measured whole-gate net.
- Equivalence to the babashka twins does not prove either implementation is
  semantically correct.
- The build is linux-x64 and non-reproducible at the binary byte level; the
  exact fflat version pin remains weaker than a version-plus-digest pin.
- `sync-scm-cases` remains outside live comparison because it pushes to its
  fixture remote. `probe-input` remains outside engine timing because its live
  path is dominated by Jenkins and polling.
- Do not publish a ratio merely because missing invocations appear likely to
  favor one mechanism. Missing work can change either side of the comparison.

## FG-227 acceptance boundary

FG-227 remains PARTIAL until a successor publishes all of the following on one
reviewed source head:

1. A retained, fail-closed runner that invokes every FSI `main`, preserves
   arguments, binds the intended repository root, and rejects no-op/zero-scan
   execution.
2. Exit-code and output-identity controls for every measured fixture, using
   finding-set or multiset comparisons only where FG-226 already records an
   ordering deviation.
3. A complete command manifest covering production binaries, copied fixtures,
   generator calls, and every temporary mutant, with a planted omitted-call
   mutation that fails the inventory.
4. Repeated complete compile-plus-execution samples on the quiet 32-core host
   and under four-core CPU affinity, including raw samples, medians, affinity,
   tree identity, tool versions, and the exact manifest.
5. Only reproduced or immutable-source cost terms. If the old babashka
   baseline cannot be recovered, publish no full FG-226 net.
6. The authoritative gate, both fresh hosted gate contexts, and exact-head
   Copilot and Codex coverage before accounting DONE.

## Current repository and queue snapshot

At `ea6480ed12141ab51f0f5d7fdd84cef8e50b2768`, the native board audit derives:

```text
rows=211; DONE=137; open=74; open P0/P1/P2/P3=2/24/35/13
compatibility ledger: tier1=1; tier3=28; admitted=199
```

The queue-row audit scans 40 rows with no known deny-list tell. Its own output
correctly labels that result a floor, not proof.

One pull request is open: [#163](https://github.com/SuperBadLabs/Fogell/pull/163)
at head `1c30031fb6298646ab5a66eeeba745f5891036b7`. GitHub reports it conflicting
and dirty against current `main`; its August 27 green runs are historical, not
current merge evidence. Reconcile its content as fresh work if selected.

## Cleanup and ownership state

- All five disposable PR #200/216 gate worktrees and their empty parent
  directories were removed.
- The dedicated, recreatable `fogell-pr200-custodian` PostgreSQL container and
  its test data were removed after the exact-head gate.
- The unrelated `fogell-fg060a` container was left running and untouched.
- The old PR #200 branch `agent/fg-226-followup-accuracy` remains attached to
  `.claude/worktrees/execution-board-plan-9835ba`; treat it as owned historical
  state, not cleanup permission.
- The four remote `codex/fg-226-227-reconciliation*` publication branches were
  retained because branch deletion was not separately authorized. Inspect
  ancestry and ownership before removing them.

## Safe opening move for the next custodian

1. Fetch `origin`, record the exact current `origin/main`, and branch from it.
2. Run `scripts/build-audits.sh`, `scripts/bin/audit-board-numbers`, and the
   canonical queue audit before trusting this snapshot.
3. Pick an impact dimension deliberately. FG-227 is a valid residual, not an
   instruction to select it automatically.
4. If selecting FG-227, begin with the retained fail-closed runner and planted
   no-op/root/inventory mutants. Timing before those controls is not evidence.
5. Prove the narrow claim, then run the authoritative HeMan gate with its
   PostgreSQL, regression-baseline, Jenkins-oracle, and stale-ref dependencies.
6. Publish only a signed, already-proven head. Require both fresh hosted gates
   and exact-head Copilot/Codex coverage; any source-changing finding requires
   fresh coverage.
7. Merge with an exact-head guard, fetch and verify the merge identity, then
   update canonical accounting.

The watch is complete. The next custodian inherits a clean boundary, not a
benchmark conclusion.
