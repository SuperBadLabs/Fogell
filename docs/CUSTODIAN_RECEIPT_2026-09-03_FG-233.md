# Fogell custody receipt — 2026-09-03 — FG-233

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-09-03_FG-233.md`](CUSTODIAN_HANDOFF_2026-09-03_FG-233.md).

## Published closure

- impact dimension: collision-free PostgreSQL ownership in hosted and local
  gates, with Podman selected explicitly;
- original failure:
  [run 33693320962](https://github.com/SuperBadLabs/Fogell/actions/runs/33693320962),
  where an implicit Docker service tried fixed host port 55440 before checkout;
- source PR: [#354](https://github.com/SuperBadLabs/Fogell/pull/354), merged;
- reviewed signed source head:
  `8dc58bea0dba2169758cc0d89b84f73b10339ca6`;
- source tree: `ff62ad41a5c15531961a5cfb999a1934311e5f67`;
- source merge: `5e6b0ba917a15fc84bd53c63fb3e67f57cbe6549`,
  GitHub-verified and tree-identical to the source head;
- source PR gate:
  [33707534441](https://github.com/SuperBadLabs/Fogell/actions/runs/33707534441),
  all ten checks successful;
- exact-source HeMan gate: 1,042/1,042 tests, 293/293 seals, FG-094
  228 files/200 accepted/tier-1 1, every blocking proof, terminal `OK`;
- source reviews: Copilot `5097196524`, Codex clean `5519450655`, Codex
  security `5519419693`; exact-head coverage passed and no thread remained;
- source post-merge main gate:
  [33708394501](https://github.com/SuperBadLabs/Fogell/actions/runs/33708394501),
  all ten checks successful;
- accounting PR: [#359](https://github.com/SuperBadLabs/Fogell/pull/359),
  merged;
- reviewed accounting head:
  `6dc554a882e8d8523c6656e94390e01f7543d90b`;
- accounting tree: `6b0ef2f46cbffa224ddaa27b841a02d51c6dbd6f`;
- accounting merge:
  `4d08cc189bc2e7ad0afc34cbd930862dc68cd352`, GitHub-verified and
  tree-identical to the reviewed accounting head;
- final accounting gate:
  [33712761113](https://github.com/SuperBadLabs/Fogell/actions/runs/33712761113),
  all ten checks successful;
- final accounting reviews: Copilot `5097581245`, Codex clean
  `5520067877`, Codex security `5520073963`; exact-head coverage passed and
  all threads were resolved;
- accounting post-merge main gate:
  [33713270026](https://github.com/SuperBadLabs/Fogell/actions/runs/33713270026),
  all ten checks successful;
- canonical state: FG-233 DONE.

## Successor snapshot and cautions

- Fetch before branching. This receipt observed `origin/main` at
  `cca52659a70b573e15305354f6850c1a474a8ff5`, tree
  `017ab2e773357a025b62d9bf8ef20cda5c8f3622`.
- Latest observed main gate
  [33809984538](https://github.com/SuperBadLabs/Fogell/actions/runs/33809984538)
  was successful on that exact head.
- Board snapshot: rows=233, DONE=173, open=60, open
  P0/P1/P2/P3=2/19/32/7. Open P0s are FG-026b and FG-236.
- Open PR #364 was exact-head reviewed, thread-clean, mergeable, and ten-check
  green at observation time; recheck it before acting.
- Preserve explicit Podman selection, runtime-allocated CI/default-local
  loopback ports, validated readback, cleanup armed before start, and the
  blocking FG-233 hostile/mutation proof. `pg-test-db.sh` retains a supported,
  validated explicit-port opt-in for local callers.
- Read the concurrently merged `docs/WIZARD_HANDOFF_2026-09-03.md` as a
  companion repository-wide account.
- The dedicated FG-233 database was removed. Older containers and worktrees are
  ownership-sensitive and were not cleaned up.

Handoff branch: `codex/custodian-handoff-fg233-2026-09-03`. The documentation
PR and its merge record identify the commit containing these artifacts.
