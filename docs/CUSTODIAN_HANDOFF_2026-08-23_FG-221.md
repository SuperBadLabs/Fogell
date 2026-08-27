# Fogell custodian handoff — 2026-08-23 — superseded intake snapshot

> **SUPERSEDED after the FG-221 collector completed.** This file is retained as
> the pre-completion custody snapshot. Its collector blocker, staging inventory,
> TODO checklist, and proposed external-target-security non-goal are historical
> and must not be used as current state. Current ticket/publication state lives
> in `docs/tickets/FG-221.md`; retained evidence state lives in
> `evidence/20260823T104948Z-fg221-junit-symlink-traversal/README.md` and
> `run/STATUS`. FG-221 remains PARTIAL until required exact-head final
> review/coverage, CI, and merge are recorded; the local candidate commit records
> the completed staged tree.

## Executive state

FG-220 is merged. FG-221 is implemented and has passing focused tests plus two
tier-1 live-Jenkins differential receipts, but it is **not finished**: there is
no FG-221 commit, push, PR, exact-head review, CI result, or merge yet. The
current work is staged in a dedicated worktree. Do not reset or recreate it.

Host and paths:

- host: `heman`
- canonical repository: `/home/srikanth/projects/fogell`
- active worktree: `/home/srikanth/projects/fogell-worktrees/fg-221-junit-symlink-traversal`
- branch: `agent/fg-221-junit-symlink-traversal`
- exact base/HEAD: `8c2930428d32c6b77fd68334e5cd09b2b3c79972`
- base is the FG-220 merge commit and was equal to `origin/main` when FG-221 began

## Last completed ticket

FG-220 merged through GitHub PR #144 on 2026-08-23.

- reviewed head: `ecd9eb90862f5ef06bb9372488d1b5a0fe0c3e16`
- merge commit: `8c2930428d32c6b77fd68334e5cd09b2b3c79972`
- merge parents: previous main
  `1b29ba466bb3cf436796ebdf9aea4352b5576ba3` and the reviewed head
- retained evidence:
  `evidence/20260823T093929Z-fg220-junit-skip-old-reports`
- manifest digest:
  `da0fc9b54e0609081aa4a5c27d752c221f259e1f8b74961f83f689b567db97d8`

## FG-221 claim and implementation

The ticket is a pinned-Linux, JUnit-private compatibility slice for Jenkins
2.568.1 / JUnit `1416.vd753e036de5e` / Ant 1.10.17.

Implemented behavior:

- healthy file and directory symlinks are followed under their logical scanner
  paths, including an absolute directory target outside the workspace;
- traversal uses a separate resolved physical directory and logical report path;
- a branch follows the same canonical directory target at most five times;
- a directory self-loop therefore ingests the base report plus five aliases;
- a dangling literal file symlink is excluded by Ant's literal fast path;
- a dangling wildcard-selected file symlink is retained lexically and becomes
  one zero-byte synthetic failure;
- a dangling directory symlink has no recursive descendants;
- a file self-loop is retained lexically and becomes one synthetic failure;
- existence, length, and `skipOldReports` timestamp reads use a refreshed final
  target `FileInfo`, not the Unix symlink entry's metadata;
- public archive/stash matching is deliberately unchanged.

Primary files:

- `src/Fogell.Execution/Publish.fs`
- `tests/Fogell.Execution.Tests/Tests.fs`

The focused test is named
`junit follows healthy symlinks and bounds repeated directory targets like pinned Ant`.
It also covers the dangling literal/wildcard split, broken directory link,
file self-loop, wildcard `allowEmptyResults`, dangling `skipOldReports`, a
zero-byte symlink target, an old symlink target, external traversal, and unchanged
archive/stash behavior.

One review caveat is intentionally visible: `junitFileTargetInfo` catches all
resolution exceptions and returns `None`. For `skipOldReports` that becomes a
`FileNotFoundException`, so permission or unusual resolver failures lose their
original exception class. Races and permissions are outside the proposed ticket
scope, but an exact-head reviewer may still ask to narrow this catch.

## Verified results

Actual Expecto invocation (do not substitute `dotnet test`; this project is an
executable-style suite):

```sh
dotnet run --project tests/Fogell.Execution.Tests/Fogell.Execution.Tests.fsproj -c Release
```

Result: **57/57 passed**.

Promoted canonical cases and receipts are already staged:

- `fg221-junit-symlink-loop`: Jenkins and Fogell both return success with
  `SUMMARY=6,0,0,6;DURATION=6.0`, continue, and have the same workspace hash;
- `fg221-junit-dangling-links`: Jenkins and Fogell both return typed zero for
  the allowed literal miss, one synthetic failure for the wildcard selection,
  continue, finish UNSTABLE, and have the same workspace hash.

Both receipts report:

```text
VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash
```

The pinned corpus has 36 direct JUnit calls, 25 containing recursive `**`
patterns, and zero symlink indicators on those direct call lines. This ticket is
a correctness residual, not corpus recovery.

## Current staged tree

At handoff, the following eight FG-221 product/evidence-scaffold files are staged:

```text
A  differential/cases/fg221-junit-dangling-links.Jenkinsfile
A  differential/cases/fg221-junit-symlink-loop.Jenkinsfile
A  differential/receipts/fg221-junit-dangling-links.receipt.txt
A  differential/receipts/fg221-junit-symlink-loop.receipt.txt
A  evidence/20260823T104948Z-fg221-junit-symlink-traversal/README.md
A  evidence/20260823T104948Z-fg221-junit-symlink-traversal/collect.sh
M  src/Fogell.Execution/Publish.fs
M  tests/Fogell.Execution.Tests/Tests.fs
```

This handoff document should be staged as a ninth file. There is no retained
`run/` directory yet. The collector is atomic and correctly removed failed
private stages.

## Evidence collector blocker

Collector:

```sh
bash evidence/20260823T104948Z-fg221-junit-symlink-traversal/collect.sh
```

It currently reaches validation and fails only at:

```text
grep -Fx 'loop-count=6' ant-match-matrix-before.txt
```

The cause is in the **probe scaffold, not Fogell**. The direct Ant matrix creates
a physical directory named `loop` and then a child link also named `loop`.
Ant's name/path-based illegal-loop accounting counts that repeated component one
step earlier and returns five paths. The canonical Jenkins case instead uses
physical directory `reports` with child link `loop`, and returns six, as both
promoted receipt sides prove.

Minimal collector correction:

1. Rename the matrix's physical variable/directory from `loop` to `reports`.
2. Keep its child symlink named `loop -> .`.
3. Scan from the matrix root with `reports/**/*.xml`.
4. Keep the assertion `loop-count=6`.

The exact misleading probe output was:

```text
loop-paths=[loop/loop/loop/loop/loop/result.xml,
            loop/loop/loop/loop/result.xml,
            loop/loop/loop/result.xml,
            loop/loop/result.xml,
            loop/result.xml]
```

Debug logs are outside the repository at `/tmp/fg221-collector-debug.log` and
`/tmp/fg221-collector-debug2.log`. Temporary receipts/probes are under
`/tmp/fg221-receipts*` and `/tmp/fg221-matrix-probe*`; none is authoritative.

## Remaining path to merge

1. Correct the collector scaffold as described above and rerun it.
2. Verify `run/STATUS` is `COMPLETE`, run
   `(cd run && sha256sum -c MANIFEST.sha256)`, record payload count and manifest
   digest, and replace the evidence README's placeholder completion sentence.
3. Add `docs/tickets/FG-221.md` with the bounded claim, proof, corpus boundary,
   evidence identity, and explicit non-goals.
4. Update `docs/EXECUTION_BOARD.md` in two places: the FG-177 parent summary and
   a DONE FG-221 row immediately after FG-220.
5. Append the FG-221 slice to `docs/tickets/FG-177.md` and remove only the now
   closed bounded symlink residual; keep platform and unmeasured cycle residuals.
6. Regenerate `docs/COMPATIBILITY-SCORECARD.md` using the repository generator.
   With two new cases, the expected/proven differential count should move from
   275/275 to 277/277 and repository receipt seals from 276 to 278.
7. Stage every intended file and inspect both `git diff HEAD` and
   `git diff --cached`. Do not discard the current index.
8. Run the authoritative gate (`scripts/build-and-test.sh`) and any blocking
   lanes it invokes. Re-run the focused Execution suite separately if the gate
   does not print its 57/57 summary.
9. Commit, push `agent/fg-221-junit-symlink-traversal`, and open the PR.
10. Obtain exact-head Codex and Copilot coverage, run
    `scripts/review-coverage.py --pr <number>`, and require both exact-head CI
    jobs green.
11. Merge only the reviewed head, then verify the merge commit's two parents are
    the previous `origin/main` and that exact reviewed head. Confirm
    `origin/main` resolves to the merge commit.

FG-221 is not DONE until step 11.

## Scope boundary to preserve

Proposed non-goals: arbitrary multi-node cycles, chains of more than five
distinct link targets, root symlinks, Windows junctions and non-Linux platform
paths, races/permissions, external-target escape/security policy, and symlink
timestamp behavior beyond the measured final-target rule. The remaining JUnit
object/raw UI and generic numeric-width surface stays with FG-177.

Welcome to the jungle.
