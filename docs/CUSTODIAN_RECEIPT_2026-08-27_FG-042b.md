# Fogell custody receipt — 2026-08-27 — FG-042b

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-08-27_FG-042b.md`](CUSTODIAN_HANDOFF_2026-08-27_FG-042b.md).

## Published closure

- impact dimension: authenticated, byte-exact, attempt-scoped artifact
  retrieval;
- source PR: [#172](https://github.com/SuperBadLabs/Fogell/pull/172), merged;
- reviewed signed head: `cc7293a4f70592c0f5b398671f18fa5c70bd3788`;
- reviewed tree: `07048143ed39c651aea7910e6a0c740890770c2d`;
- source parent: `f0dcf58b13767446c903abdb6b19a787e93cd321`;
- merge commit: `e27fc3539ccbb350493212c480adcefc9012e349`;
- signature: good ED25519,
  `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`;
- local and hosted acceptance: 924/924 tests plus every blocking proof;
- live proof: exact six-byte artifact download and unauthenticated nondisclosure;
- hosted checks: three exact-head `gate` jobs, all successful;
- exact-head review coverage: Copilot and Codex covered; no final findings;
- canonical board state: FG-042 and FG-042b DONE;
- board snapshot: rows=209, DONE=133, open=76, open P0/P1/P2/P3=3/26/35/12.

## Successor cautions

- Fetch before branching: local `main` was stale at handoff preparation.
- Open PRs #161 and #163 were conflicting with current `main`; their old green
  checks and reviews do not cover a reconciliation commit.
- The live queue contains older prose that still calls FG-042 open. Resolve
  state from canonical rows, ticket documents, ancestry, and GitHub publication.
- Historical worktrees are preserved and must not be deleted without inspecting
  their branch, cleanliness, publication state, and owner.
- Preserve FG-042b's known-path, terminal-attempt, Linux descriptor-validation,
  and same-UID isolation boundaries. The detailed handoff records the full
  contract and nonclaims.

Handoff branch: `codex/custodian-handoff-fg042b-2026-08-27`. The documentation
PR and its merge record identify the commit containing these artifacts.
