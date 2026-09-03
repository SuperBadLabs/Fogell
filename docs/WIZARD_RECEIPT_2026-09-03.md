# Fogell wizard receipt — 2026-09-03

This receipt indexes the detailed
[`WIZARD_HANDOFF_2026-09-03.md`](WIZARD_HANDOFF_2026-09-03.md). Every head
below is signed with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`; every merge was made
with `--match-head-commit` on a `CLEAN` PR after `scripts/review-coverage.py`
reported both reviewers on the exact head; every GitHub-generated merge is
reported `verified`.

## Published closures

| ticket | source head → PR → merge | hosted run | accounting head → PR → merge |
|---|---|---|---|
| FG-234 | `7252dce0ff97b28ec4144a8838fc24eb448d68b9` → #329 → `82760b3740771a209ce631f82242baba66d53c52` | `33688775074` | `a7ec06cfbef530e8765dec3b7ef6b6d914c01cbe` → #330 → `4fcd536b` |
| FG-207 | (landed 2026-08-24 as `340f2f17…` inside PR #148) | — | `2f2f1c1c5465f119e865454e867926153fda2944` → #333 → `f04df48f` |
| FG-123 | `f0a269866d8c827995be2106418b9de6496e9e07` → #345 → `9e32a710038ea508978f9e154f70bea6816ef964` (replaced #336, #341) | `33700918233` | `296eca10abddbc8c1c09ba37051a7dff44f48b6a` → #348 → `24262d76` |
| FG-239, FG-240 | filing `97e3d13b3e681b8a72ba8522d4247bbca48397d8` → #357 → `8d318ff4`; fix `cb983ca7bc6632dc34b6f2922373e980a5e62120` → #360 → `b16696ca63e8e15f8a0395c96900485980a8aae2` | `33710826242` | `21786a985d0d3d7eaf9b120e4532a0030194108c` → #362 → `46c40c1c` |
| FG-203 | `fb56d0db4be049874e28a8e5d3bfb89d5efa9117` → #365 → `cacb9d98eb27e93c340d16c2d57d92d3feecf3a8` (fix landed 2026-08-24 as `fce392c4…` inside PR #148) | `33714056731` | `9ebd79ff9dc36a92c47fd6ca301c883ee3a95349` → #366 → `be32c7eb` |
| FG-129 | `658b808045d9d75b463911a708c3746b63431df9` → #367 → `94eb91abfa092bb17f3f3e34953295d670b3ae8e` (comparison landed 2026-08-24 as `d27616c5…` inside PR #148) | `33716296359` | `839440774e30b29586c9cadbd796ce48a98667de` → #368 → `8dcd7e0a` |

Each ticket file under `tickets/` carries the review identities (Copilot
review ids, Codex clean comment ids, verifier rounds and their verdicts) and
the local gate's measured totals for its exact head. Every source head passed
the unmodified authoritative HeMan gate against a fresh PostgreSQL 16
container with the FG-094 baseline and oracle fixtures before its push;
every accounting head passed the same gate; every hosted run passed all ten
jobs (`plan`, five `lane` jobs, `controller`, `hang-proof`, `database`,
aggregate `gate`), PR #366's `hang-proof` after one `--failed` re-run.

## Evidence added

- Receipts sealed tier-1 PROVEN on the pinned lab (2026-09-02/03):
  `options-ansicolor-gstring`, `-env`, `-expression`, `-declared-env`,
  `-env-unknown`, `-binding`; `script-env-alias-after-wrapper`;
  `compile-refusal-timeout-unit-stage`, `-pipeline` (nine). The control
  `options-ansicolor` was regenerated with an unchanged seal.
- 296 receipt seals verify; scorecard 295 of 295 expected cases.
- Tests added: FG-234 (8), FG-123 (12 in the FG-123a list, 5 rows in its
  TERM table), FG-239/FG-240 (6); the Differential project passes 349.
- Seal proof arms 14.7–14.9 (FG-234).

## Board snapshot

rows=233; DONE=173; open=60; open P0/P1/P2/P3 = 2/19/32/7; compatibility
ledger tier1=1, tier3=28, admitted=199 of 228. Derived by
`scripts/bin/audit-board-numbers` on `origin/main` at `8dcd7e0a`.

## Successor cautions

- Fetch before branching; `origin/main` was `8dcd7e0a` when this receipt was
  written and moved eight times during the tenure.
- Rows from PR #148's range may still be stale; FG-197 (P1) is known to be.
- Compile-shaped refusals are sealable (FG-129); do not write "seals none".
- Copilot does not re-review a moved head; finish every amend before the one
  push, or expect a replacement PR.
- No scratch container, probe job or unpublished branch from this tenure
  remains; the worktree
  `.claude/worktrees/fogell-feature-work-f6ab08` is clean and disposable.

Handoff branch: `claude/wizard-handoff-2026-09-03`. The documentation PR and
its merge record identify the commit containing these artifacts.
