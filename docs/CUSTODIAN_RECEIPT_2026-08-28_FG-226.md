# Fogell custody receipt — 2026-08-28 — FG-226

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-08-28_FG-226.md`](CUSTODIAN_HANDOFF_2026-08-28_FG-226.md).

## Published closure

- impact dimension: remove babashka from the blocking audit-tool boundary by
  porting eight tools to gated native F#/fflat executables;
- source PR: [#194](https://github.com/SuperBadLabs/Fogell/pull/194), merged;
- reviewed signed source head:
  `75f3babb63413ec9adff24b9e9272b4195b5018e`;
- source tree: `de483cd19e273f4332431e5d55ca8f06c489813f`;
- source merge: `29c9e50b3bb1c2140b65873352b35c3f14457550`;
- source gates: runs `33221945820` and `33221960353`, both successful;
- source reviews: Copilot `5055932123` and Codex `5458991745`, exact-head and
  clean;
- accounting PR: [#196](https://github.com/SuperBadLabs/Fogell/pull/196),
  merged;
- reviewed signed accounting head:
  `87c1b8d01ddd6afe895d25c9d8c0ecee13214569`;
- accounting tree: `e64fed36109066e4fba5606c9dfeb5f8ff9344c7`;
- accounting merge: `456b8d42ba6746121db09edce24c49a55c42694f`;
- accounting gates: runs `33224349838` and `33224367071`, both successful;
- accounting reviews: Copilot `5056082694` and Codex `5459269278`, exact-head
  and clean;
- signature: good ED25519,
  `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`;
- authoritative exact-head gate: 926/926 project tests plus FG-207 8/8, the
  live compatibility check, every blocking proof/lane, and final `OK`;
- canonical board state: FG-226 DONE;
- board snapshot: rows=210, DONE=135, open=75, open P0/P1/P2/P3=3/25/35/12.

## Successor cautions

- Fetch before branching; this receipt observed `origin/main` at
  `456b8d42ba6746121db09edce24c49a55c42694f`.
- Preserve the non-reproducible-build decision, Linux-only boundary, weaker
  fflat supply-chain pin, output-order deviations, and the weaker evidence for
  `sync-scm-cases` and `probe-input`.
- The fflat cache performance benefit remains UNVERIFIED.
- PR #163 is dirty against current `main`; reconcile it as new work rather than
  merging its historical head.
- The three open P0 rows are FG-026, FG-041, and FG-224. This is inventory, not
  an automatic next-ticket choice.
- Inspect branch, worktree, ancestry, publication, and ownership before any
  cleanup; historical artifacts are not proof of abandonment.

Handoff branch: `codex/custodian-handoff-fg226-2026-08-28-r2`. The documentation
PR and its merge record identify the commit containing these artifacts.
