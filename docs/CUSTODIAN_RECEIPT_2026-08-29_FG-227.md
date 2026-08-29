# Fogell custody receipt — 2026-08-29 — FG-227

This receipt indexes the detailed
[`CUSTODIAN_HANDOFF_2026-08-29_FG-227.md`](CUSTODIAN_HANDOFF_2026-08-29_FG-227.md).

## Published reconciliation

- impact dimension: restore claim integrity at the audit-tool evidence
  boundary;
- retired source: [PR #200](https://github.com/SuperBadLabs/Fogell/pull/200),
  closed unmerged at conflicting head
  `8b12db61cbe29754c7739ca22a52466a31b8065f`;
- final source: [PR #216](https://github.com/SuperBadLabs/Fogell/pull/216),
  merged;
- reviewed signed head:
  `77a8cbeb7d439a5db0b576e2d6bbab2a325233fd`;
- source and merge tree:
  `d2295a4af00691156d6beaf56cba881691fbf04b`;
- merge commit: `ea6480ed12141ab51f0f5d7fdd84cef8e50b2768`;
- hosted gates: runs `33261234630` and `33261263738`, both successful;
- exact-head reviews: Copilot `5058519485`, all five files and no comments;
  Codex comment `5463374666`, no major issues; zero review threads;
- source signature: good ED25519,
  `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`;
- authoritative exact-head gate: full `scripts/build-and-test.sh` with dedicated
  PostgreSQL, regression baseline, Jenkins oracle, stale-ref base, and every
  blocking lane passed;
- canonical state: FG-041 DONE, FG-226 DONE, FG-227 PARTIAL;
- board snapshot: rows=211, DONE=137, open=74, open P0/P1/P2/P3=2/24/35/13.

## Successor cautions

- Publish no fflat-vs-FSI ratio, break-even, four-core floor, or whole-gate net
  until the six FG-227 acceptance conditions are satisfied on one reviewed
  head.
- A direct `dotnet fsi scripts/fsx/*.fsx` command can exit zero without invoking
  the audit `main`; a retained runner must prove execution and repository-root
  identity before timing.
- Preserve FG-226's deliberate fail-closed deviation and nine-arm proof
  inventory. One arm is an independently exercised unreachable endpoint; eight
  are request-ready HTTP fixture states.
- Fetch before branching. This receipt observed `origin/main` at
  `ea6480ed12141ab51f0f5d7fdd84cef8e50b2768` before handoff publication.
- PR #163 is the sole open PR and is conflicting/dirty; its historical green
  runs are not current evidence.
- The old #200 worktree and the remote reconciliation branches remain
  ownership-sensitive. Inspect them before cleanup.

Handoff branch: `codex/custodian-handoff-fg227-2026-08-29`. The documentation PR
and its merge record identify the commit containing these artifacts.
