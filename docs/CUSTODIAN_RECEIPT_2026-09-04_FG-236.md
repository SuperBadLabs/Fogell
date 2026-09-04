# Fogell custody receipt — 2026-09-04 — FG-236

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-09-04_FG-236.md`](CUSTODIAN_HANDOFF_2026-09-04_FG-236.md).

## Published closure

- impact dimension: credential confidentiality across raw-stream framing,
  late registration, progressive publication, and terminal truth;
- reproduced disclosure: separator-wrapped registered forms crossed the old
  line-local masking boundary and appeared in both progressive and buffered
  output without a warning;
- source PR: [#387](https://github.com/SuperBadLabs/Fogell/pull/387), merged;
- reviewed signed source head:
  `f01be0d94f89eb5d56608e8cbfaf734922e429a9`;
- source tree: `2558e043539890c015694020b35d053b5046c6d6`;
- source merge: `1368041ce36ced15f3fcfa40ae69db0e70241bf0`,
  GitHub-verified and tree-identical to the source head;
- source exact-head gate:
  [33891190406](https://github.com/SuperBadLabs/Fogell/actions/runs/33891190406),
  all ten checks successful, build lane 34m37s;
- exact-source validation: 1,108/1,108 project tests, 301/301 seals, FG-094
  baseline/current 200/200 accepted with tier-1 1/3 and zero losses/gains, 42
  focused tests, and all 44 semantic mutants;
- source reviews: Copilot `5115131637`, Codex clean `5543023744`, Codex
  security `5543123346`; exact-head coverage passed and all 12 threads were
  resolved;
- source post-merge exact-main gate:
  [33894667223](https://github.com/SuperBadLabs/Fogell/actions/runs/33894667223),
  all ten checks successful;
- accounting PR: [#408](https://github.com/SuperBadLabs/Fogell/pull/408),
  merged;
- reviewed signed accounting head:
  `f3412f0b8abe6c83b68b9a872475690c35474c4c`;
- accounting tree: `6daadaf4bdc4dd014dde195eda8abfbddb2daebd`;
- accounting merge: `fe090f98c21334613d5ad30ce1739838b7e98fcf`,
  GitHub-verified and tree-identical to the accounting head;
- accounting exact-head gate:
  [33894933136](https://github.com/SuperBadLabs/Fogell/actions/runs/33894933136),
  all ten checks successful, build lane 31m47s;
- accounting reviews: Copilot `5115519133`, Codex clean `5543481177`, Codex
  security `5543525906`; exact-head coverage passed and no thread existed;
- canonical state: FG-236 DONE.

## Review-driven folds

- The final correction screens newly registered transformed forms in both
  progressive and terminal timestamp prefixes and their value boundary.
- The blocking proof grew to 44 mutants, including distinct progressive and
  terminal registered timestamp-prefix failures.
- Hosted build evidence now has a finite 45-minute budget. The predecessor run
  was canceled at 30 minutes while still progressing; the replacement passed
  in 34m37s.
- PR #387 superseded #371 after Copilot review transport on the earlier PR
  failed and could not be re-requested.
- HeMan's local `qwen3.8-flash-next` supplied a supplemental no-findings source
  review; canonical review and test requirements were not delegated to it.

## Successor snapshot and cautions

- Fetch before branching. This receipt observed `origin/main` at
  `fe090f98c21334613d5ad30ce1739838b7e98fcf`, tree
  `6daadaf4bdc4dd014dde195eda8abfbddb2daebd`.
- Latest completed exact-main gate was
  [33894667223](https://github.com/SuperBadLabs/Fogell/actions/runs/33894667223)
  on the source merge. Post-accounting run
  [33898013561](https://github.com/SuperBadLabs/Fogell/actions/runs/33898013561)
  was still in progress with eight shorter jobs green and the build lane active
  at the base snapshot.
- Board snapshot: rows=237, DONE=181, open=56, open P0/P1/P2/P3=1/19/31/5;
  tier1=5, admitted=195, tier3=28 of 228; 301/301 scorecard cases; 306
  receipts.
- FG-026b is the sole open P0. Re-derive the board and choose deliberately.
- Open PR #374 is currently `DIRTY` / `CONFLICTING`; its old green checks do
  not authorize merge. Current `main` already contains FG-237 and FG-238 rows
  (corrected 2026-09-04: it did not; see the handoff's correction note).
- Preserve raw-stream-before-framing redaction, independent stream identities,
  one-separator adjacency, live registration inventory, bounded pending state,
  protected-token provenance, timestamp-prefix screening, and true-EOF
  fail-closed behavior.
- The local LLM endpoint at `127.0.0.1:8000` is supplemental only and must not
  receive credentials or replace canonical proof and review.
- Numerous older containers and worktrees remain ownership-sensitive. No
  cleanup authority was inferred from age or naming, and none was removed.
- Read the 2026-09-04 wizard handoff and receipt as companion repository-wide
  context.

Handoff branch: `codex/custodian-handoff-fg236-2026-09-04`. The documentation
PR and its merge record identify the commit containing these artifacts.
