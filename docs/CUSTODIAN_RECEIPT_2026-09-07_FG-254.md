# Fogell custody receipt — 2026-09-07 — FG-254

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-09-07_FG-254.md`](CUSTODIAN_HANDOFF_2026-09-07_FG-254.md).

## Published Tier-1 closure

- Target: `charlires_golang-docker-jenkins.Jenkinsfile` in a clean workspace,
  executed alone against a fresh, immutable Jenkins 2.568.1 derivative with
  exact GNU Make 4.3 installed.
- Bounded result: both engines fail at the first `make build-base` for absent
  Makefile, match four compared lines, skip the same two later stages, and
  retain an empty workspace. Later Make targets, the intended Docker build,
  `ls`, and JUnit are not claimed.
- Receipt seal:
  `fe3dc56d1d187712bebdee932c619cd7cb269a1fee95b3ddebf6751d9daa4dfa`.
- Final accounting: `tier1=13`, `admitted=187`, `tier3=28`; the separate
  hand-written population is 308/308 proven.
- Source PR: [#432](https://github.com/SuperBadLabs/Fogell/pull/432), merged.
- Reviewed signed source head:
  `4366214e781a538c07b87453e4e0503d826e13ff`; tree
  `cccc53f98b0f3323cf91475a9145d4051c4e0bfc`.
- Source merge: `7578fd4905e8b7e2b08296318635be23b3a04c02`,
  GitHub-verified and tree-identical to the reviewed head.
- Source evidence: complete local gate with 1,139/1,139 tests, 321/321 seals,
  every fast and slow/mutant lane, and bare `OK`; hosted exact-head
  [run 34068813444](https://github.com/SuperBadLabs/Fogell/actions/runs/34068813444)
  and post-merge
  [run 34071018691](https://github.com/SuperBadLabs/Fogell/actions/runs/34071018691)
  both green.
- Source review: exact-head Codex clean; Copilot made one reader-mode
  portability suggestion; all eleven threads resolved.
- Accounting PR: [#435](https://github.com/SuperBadLabs/Fogell/pull/435),
  merged.
- Reviewed signed accounting head:
  `bc3f1b7846a35b9b618824542ba91de977d9bc85`; tree
  `6bf2918747a74df27e3dc7aeceae5a1e79f44d51`.
- Accounting merge/current snapshot:
  `cfb567a5483e37b2902631b390399e9b347946b6`, GitHub-verified and
  tree-identical to the accounting head.
- Accounting fold: the Python-invoked PID1 reader now requires a regular
  non-symlink, not an executable bit; focused proof killed all eight reader
  and writer mutants and left no residue.
- Accounting evidence: complete local all-lane gate; hosted exact-head
  [run 34082899360](https://github.com/SuperBadLabs/Fogell/actions/runs/34082899360),
  post-merge fast
  [run 34083468324](https://github.com/SuperBadLabs/Fogell/actions/runs/34083468324),
  and post-merge mutant
  [run 34083468329](https://github.com/SuperBadLabs/Fogell/actions/runs/34083468329)
  all green.
- Accounting review: Copilot approval recommended, Codex clean, both exact
  head, formal coverage passed, and zero review threads.
- Canonical disposition: FG-254 DONE.

## Successor snapshot and cautions

- Fetch before branching. This receipt observed `origin/main` at
  `cfb567a5483e37b2902631b390399e9b347946b6`, tree
  `6bf2918747a74df27e3dc7aeceae5a1e79f44d51`.
- Board snapshot: rows=249, DONE=182, open=67, open P0/P1/P2/P3=1/22/37/7;
  tier1=13, admitted=187, tier3=28; 308/308 expected hand-written receipts;
  321 citable receipts.
- PR #424 is the only observed open PR and owns the sole P0, FG-026b. Its
  owner worktree has an uncommitted `Store.fs` modification. Coordinate; do
  not race, merge, rebase, publish, or clean it.
- Preserve the exact runtime tuple, archived private HEAD/CLI closure, fresh
  controller/home, owner-UID and no-egress fences, queue/token/Replay/PATH/tool
  bindings, receipt isolation, PID1 descriptor attestation, and cleanup order.
- The local lab runtime pin is not a generally published oracle image, and the
  receipt proves only the first absent-Makefile failure boundary.
- FG-256 split the hosted topology: required PR/main gates are fast, while
  mutants run after each `main` merge and nightly. Local unset lane selection
  still runs everything.
- Custody-owned accounting containers were removed. The pre-existing
  `fogell-fg254` container and all other owners' containers/worktrees were
  left untouched.

Handoff branch: `codex/custodian-handoff-fg254-2026-09-07`. The documentation
PR and its merge history identify the commit containing these artifacts.
