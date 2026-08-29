# Fogell custody receipt — 2026-08-29 — FG-223

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-08-29_FG-223.md`](CUSTODIAN_HANDOFF_2026-08-29_FG-223.md).

## Published closure

- impact dimension: make evidence sealing fail closed and bind every measured
  prerequisite to the exact captured source;
- source PR: [#209](https://github.com/SuperBadLabs/Fogell/pull/209), merged;
- reviewed signed source head:
  `ec85ef5816baad21f868dc9d46dffbc36b33d30a`;
- source tree: `ecab44314d1457da2f0f3fe1ad5a45fe1f1ae2f8`;
- source merge: `e6ac836b96e66562fb370ddd5dbe9db44d6e20d2`;
- source gates: runs `33251218480` and `33251225260`, both successful;
- source reviews: Copilot `5058026163` and Codex `5462291328`, exact-head
  covered; the sole false-premise thread was rebutted and resolved;
- accounting PR: [#211](https://github.com/SuperBadLabs/Fogell/pull/211),
  merged;
- reviewed signed accounting head:
  `8bb32ca753e8630cdddc82214a1e3700f9e38f68`;
- accounting tree: `1bc93c2b7ca841d91d584536827fdae1aba34762`;
- accounting merge: `cdf5a2f0a99861f9d9361d109b5a4d3b52ffb87b`;
- accounting gates: runs `33252694905` and `33252704505`, both successful;
- accounting reviews: Copilot `5058077075` and Codex `5462461035`,
  exact-head and clean;
- signature: good ED25519,
  `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`;
- authoritative exact-head gate: 926/926 project tests, FG-207 8/8, locks
  26/26 plus mutation controls, compatibility 228/228, every blocking lane,
  and final `OK`;
- canonical board state: FG-223 DONE;
- board snapshot: rows=210, DONE=136, open=74, open
  P0/P1/P2/P3=3/24/35/12.

## Successor cautions

- Fetch before branching; this receipt observed `origin/main` at
  `cdf5a2f0a99861f9d9361d109b5a4d3b52ffb87b`.
- Preserve the same-UID and prerequisite mutate/restore threat-model limits.
  The seal proves command integrity, not test semantics or publication
  authority.
- The older committed FG-223 evidence bundle predates the final source boundary
  and is not closure evidence for `ec85ef5816`.
- The mechanical P0 count includes FG-041, whose stated FG-041b residual is
  already DONE; reconcile that status and the stale FG-042 prose before using
  the P0 inventory as semantic priority.
- PRs #200 and #163 are conflicting against current `main`; historical green
  checks do not make either head merge-ready.
- Inspect branch, worktree, ancestry, publication, and ownership before any
  cleanup; historical artifacts are not proof of abandonment.

Handoff branch: `codex/custodian-handoff-fg223-2026-08-29`. The documentation
PR and its merge record identify the commit containing these artifacts.
