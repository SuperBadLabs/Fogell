# Fogell custody receipt — 2026-09-04 — FG-251

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-09-04_FG-251.md`](CUSTODIAN_HANDOFF_2026-09-04_FG-251.md).

## Published closure

- impact dimension: startup integrity and bounded consumption of the global
  controller API bearer;
- defect removed: validation and consumption can no longer reopen different
  pathname targets, accept unsafe type/ownership/mode, block on a FIFO, or read
  an unbounded token file;
- source PR: [#416](https://github.com/SuperBadLabs/Fogell/pull/416), merged;
- reviewed signed source head:
  `bfe9db1807944e6f066aa7512e1a1963b32bde9a`;
- source tree: `1876715b38809420a0019b28bf6f1ff09d918104`;
- source merge: `4dc4420a2cfc707c30e23e329bc636adc9f688d0`,
  tree-identical to the reviewed source head;
- source exact-head gate:
  [33932226683](https://github.com/SuperBadLabs/Fogell/actions/runs/33932226683),
  all ten jobs successful;
- exact-source validation: 1,133/1,133 project tests, eight focused tests, all
  FG-251 hostile mutants, compatibility 200/200 with tier-1 1/9 and zero
  losses/gains, and 316/316 seals;
- source reviews: Codex correctness `5547961132`, Codex security `5548041785`,
  both clean; Copilot unavailable because its weekly quota was exhausted, with
  owner exception `5547947458` and compensating controls recorded explicitly;
- accounting PR: [#426](https://github.com/SuperBadLabs/Fogell/pull/426),
  merged;
- reviewed signed accounting head:
  `6972642752f74fe59cf68736c9a0027de3944dd9`;
- accounting tree: `a5480b5169149da70afa29bc463246de814fa49a`;
- accounting merge/current snapshot:
  `9c05ad5a46483086a68d5b89763e4d1e3ee12f63`, tree-identical to the
  accounting head;
- accounting exact-head gate:
  [33934012859](https://github.com/SuperBadLabs/Fogell/actions/runs/33934012859),
  all ten jobs successful;
- accounting reviews: Codex correctness `5548181364`, Codex security
  `5548219763`, both clean; owner Copilot exception `5548161199`;
- post-accounting exact-main gate:
  [33935825486](https://github.com/SuperBadLabs/Fogell/actions/runs/33935825486),
  all ten jobs successful; and
- canonical state: FG-251 DONE.

## Successor snapshot and cautions

- Fetch before branching. This receipt observed `origin/main` at
  `9c05ad5a46483086a68d5b89763e4d1e3ee12f63`, tree
  `a5480b5169149da70afa29bc463246de814fa49a`.
- Board snapshot: rows=243, DONE=183, open=60, open P0/P1/P2/P3=1/19/33/7;
  tier1=9, tier3=28, admitted=191 of 228; 307/307 scorecard cases; 316
  citable receipts.
- FG-026b is the sole open P0 and is active in PR #424. PR #427 is concurrent
  FG-247/accounting work. Coordinate before acting on either worktree or PR.
- Preserve the one-open-descriptor invariant, architecture-correct no-follow
  flag, `statx(AT_EMPTY_PATH)` mask/type/uid/mode/size checks, exact
  `0400`/`0600`, close-on-exec/nonblocking behavior, strict decoding, and both
  the 4096 metadata and 4097 read bounds.
- Same-uid in-place inode mutation, ancestor validation, token rotation, tenant
  authorization, and TLS remain outside the closed claim.
- Copilot quota/error events are not reviews. The current coverage helper can
  misclassify them; manually inspect review objects until FG-199 is corrected.
- HeMan's local `qwen3.8-flash-next` was useful supplemental capacity but was
  transiently unreliable and is not canonical evidence.
- The non-interactive Bash terminal-process-group warning in the supplied lane
  excerpt was harmless job-control noise; the visible FG-251 proof passed.
- The custody-owned disposable PostgreSQL container was removed. Other
  containers and historical/active worktrees remain ownership-sensitive and
  were not disturbed.

Handoff branch: `codex/custodian-handoff-fg251-2026-09-04`. The documentation
PR and its merge record identify the commit containing these artifacts.
