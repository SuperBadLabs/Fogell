# Fogell custodian handoff — 2026-09-03 — FG-233

Status: **FG-233 is implemented, signed, independently reviewed, green on its
exact source and accounting heads, merged into protected `main`, accounted
DONE, and green again after each merge.**

This is the outgoing-custody record for the CI reliability closure published
through source PR [#354](https://github.com/SuperBadLabs/Fogell/pull/354) and
accounting PR [#359](https://github.com/SuperBadLabs/Fogell/pull/359). It
records the failure that was fixed, the proof boundary, the exact publication
identities, current repository state, and the operational cautions inherited by
the next custodian.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- `origin/main` observed before this handoff publication:
  `cca52659a70b573e15305354f6850c1a474a8ff5`.
- Observed `origin/main` tree:
  `017ab2e773357a025b62d9bf8ef20cda5c8f3622`.
- Latest observed main gate:
  [run 33809984538](https://github.com/SuperBadLabs/Fogell/actions/runs/33809984538),
  successful on that exact main head.
- Handoff branch: `codex/custodian-handoff-fg233-2026-09-03`.
- This document's PR and merge record identify the commit containing the
  handoff. Do not add a predicted self-reference.

Fetch before branching. The queue, open PRs, worktrees, containers, green runs,
and review results below are observations at this snapshot, not durable locks on
external state.

## Impact dimension closed

The selected dimension was **deterministic, collision-free PostgreSQL ownership
in CI and local proof execution, with Podman as the explicit runtime**.

The failing main run
[#33693320962](https://github.com/SuperBadLabs/Fogell/actions/runs/33693320962)
never reached checkout. GitHub Actions expanded a `services:` PostgreSQL block
through Docker and tried to bind fixed host port 55440, which was already in
use. The failure was runner-global resource coupling, not an application or
database-test failure.

FG-233 removes all four Actions service containers. The workflow now selects
`FOGELL_CONTAINER_RUNTIME=podman` explicitly and delegates each
PostgreSQL-using job to `scripts/ci-postgres.sh`. Each job owns a disposable
rootless-Podman PostgreSQL, requests a runtime-allocated loopback port with
`127.0.0.1::5432`, reads the assigned port back from the runtime, exports only
validated state, and stops its own container under a guarded `always()`
cleanup step.

The local database helper defaults to the same runtime-allocated port contract;
its supported second argument remains an explicit, validated fixed-port opt-in
for a caller that needs one. The controller proof, controller inotify proof,
backup/restore drill, and migration rollback drill consume the selected port.
Cleanup is armed before container start, so a runtime that
creates a container and then exits nonzero does not strand it before state
export. Literal container names, explicit ports, runtime names, and observed
port mappings are validated before use. Diagnostic retention for the controller
proofs is selected by `FOGELL_KEEP_FG224_PROOF=1`,
`FOGELL_KEEP_FG231_PROOF=1`, and `FOGELL_KEEP_FG232_PROOF=1`, respectively.
Do not substitute a generic `KEEP` variable for those ticket-specific controls.

`scripts/prove-fg233-podman-gate.sh` is blocking. It kills ten planted
regressions covering restored Actions services, restored fixed ports, missing
job runtime selection, unguarded cleanup, fixed local/controller/inotify ports,
and severed port-readback consumers. Hostile controls also reject an invalid
runtime, an option-like target, a mismatched mapping, and a failed start without
unsafe cleanup.

The chronological implementation and review record is in
[`tickets/FG-233.md`](tickets/FG-233.md). The canonical board row is DONE.

## Source publication receipt

PR #354 merged at `2026-09-03T02:38:31Z`:

```text
reviewed signed head  8dc58bea0dba2169758cc0d89b84f73b10339ca6
source parent         0ffab75bca88e6b933a75d3b28b529c7e7e71602
source tree           ff62ad41a5c15531961a5cfb999a1934311e5f67
protected base        24262d769b099b80a6ae8706359773e3b2309c32
merge commit          5e6b0ba917a15fc84bd53c63fb3e67f57cbe6549
merge parents         24262d769b099b80a6ae8706359773e3b2309c32
                      8dc58bea0dba2169758cc0d89b84f73b10339ca6
merge tree            ff62ad41a5c15531961a5cfb999a1934311e5f67
```

The source head verifies locally with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`. GitHub reports the
merge commit signature valid, and its tree is exactly the reviewed source tree.

The exact final source head passed the unmodified authoritative HeMan gate
against an isolated rootless-Podman PostgreSQL on runtime-allocated port 41089:
1,042/1,042 project tests, 293/293 receipt seals, FG-094's 228 files with
200/200 accepted, tier-1 1/1, every blocking proof, restart and approval lanes,
and terminal `OK`. This final-source result supersedes the earlier
pre-publication 1,036-test/287-seal snapshot; the repository advanced between
the two measured heads. The external FG-094 baseline and Jenkins oracle were
bound explicitly. An earlier invocation without those bindings correctly
refused rather than presenting partial corpus work as success.

Hosted PR run
[#33707534441](https://github.com/SuperBadLabs/Fogell/actions/runs/33707534441)
passed `plan`, all five `lane` jobs, `controller`, `hang-proof`,
`database`, and aggregate `gate` on the exact source head. Each of the four
database-bearing jobs crossed the setup boundary that failed in run
33693320962. Copilot review `5097196524` recommended approval with no new
comments; Codex clean comment `5519450655` and security comment `5519419693`
covered the same head. The exact-head review-coverage audit passed, and no
unresolved source thread remained.

Post-source-merge main run
[#33708394501](https://github.com/SuperBadLabs/Fogell/actions/runs/33708394501)
passed all ten checks.

## Accounting publication receipt

PR #359 merged at `2026-09-03T03:57:44Z`:

```text
reviewed head         6dc554a882e8d8523c6656e94390e01f7543d90b
accounting parents    5f9ee70c868d48de710a78c12c6e1ec4e27e5cff
                      46c40c1cb4db0a7b10a2de40c6aeba745997a87e
accounting tree       6b0ef2f46cbffa224ddaa27b841a02d51c6dbd6f
protected base        46c40c1cb4db0a7b10a2de40c6aeba745997a87e
merge commit          4d08cc189bc2e7ad0afc34cbd930862dc68cd352
merge parents         46c40c1cb4db0a7b10a2de40c6aeba745997a87e
                      6dc554a882e8d8523c6656e94390e01f7543d90b
merge tree            6b0ef2f46cbffa224ddaa27b841a02d51c6dbd6f
```

GitHub reports the merge signature valid and the merge tree is exactly the
reviewed accounting tree. Final PR run
[#33712761113](https://github.com/SuperBadLabs/Fogell/actions/runs/33712761113)
passed all ten checks. Final Copilot re-review `5097581245` recommended
approval with zero new comments. Codex clean comment `5520067877` and
security comment `5520073963` covered the exact accounting head.
`scripts/review-coverage.py --pr 359` covered both canonical reviewers, and
all review threads were resolved.

Accounting review corrected real record defects before merge:

1. a present-tense pre-publication nonclaim was retired after hosted evidence
   existed;
2. the final-source measurement was explicitly distinguished from the earlier
   snapshot; and
3. the PR description was updated after concurrent FG-239/FG-240 closure
   changed the mechanically derived board totals.

The head was synchronized with then-current main, and the combined accounting
was mechanically proven before the SHA-guarded merge. Post-accounting-merge
main run
[#33713270026](https://github.com/SuperBadLabs/Fogell/actions/runs/33713270026)
passed all ten checks.

## Boundaries the next custodian must preserve

- Podman is the selected runtime. Do not reintroduce GitHub Actions
  `services:` or any implicit Docker dependency into the gate.
- CI and the default local PostgreSQL path must not reserve a fixed host port.
  Container port 5432 is stable; the default loopback host port is
  runtime-allocated and read back. `scripts/pg-test-db.sh` deliberately retains
  a validated explicit host-port second argument for opt-in local callers;
  that interface is not deprecated by FG-233.
- Cleanup must be both guarded and armed before start. A setup failure must not
  create either a stranded container or a misleading secondary cleanup failure.
- Consumers must use the exported validated port; merely allocating a dynamic
  port is not proof if a later command still embeds a fallback.
- The hostile/mutation proof is load-bearing. A textual grep alone does not
  replace its runtime, mapping, option-like-input, and failed-start controls.
- Historical successful heads and reviews are chronology, not evidence for a
  byte-changing successor. Any change to the lifecycle or proof restarts local
  gate, hosted gate, exact-head Copilot/Codex coverage, and thread resolution.

## Current repository and queue snapshot

At `cca52659a70b573e15305354f6850c1a474a8ff5`, the native audits derive:

```text
rows=233; DONE=173; open=60; open P0/P1/P2/P3=2/19/32/7
compatibility ledger: tier1=1; tier3=28; admitted=199
receipt inventory: 296 receipts; generated scorecard expects 295 case receipts
```

Board-number, queue-row, strict claim, and generated-scorecard checks all passed.
The two mechanically open P0s are:

- FG-026b — route controller-managed external-effect producers through the
  fenced effect ledger and prove crash-window reconciliation;
- FG-236 — prevent separator-inserting transformations from crossing the
  line-framed masking boundary.

That is inventory, not an automatic assignment. Re-derive priority and choose an
impact dimension deliberately.

The GitHub API reported one other open PR besides this handoff:
[#364](https://github.com/SuperBadLabs/Fogell/pull/364), FG-237/FG-238, head
`5a27fb93976b403da4632210c031fd92ee9bf9d7`. At this snapshot all ten checks
were green, both canonical reviewers covered the exact head, and unresolved
thread count was zero. It was mergeable, but this handoff did not merge it.
Fetch and recheck head, base, review content, conflicts, and repository policy
before acting.

Concurrent wizard handoff PR #370 merged while this artifact was being
published. Its `docs/WIZARD_HANDOFF_2026-09-03.md` and
`docs/WIZARD_RECEIPT_2026-09-03.md` are preserved in this branch and should be
read as a companion repository-wide account; neither supersedes this FG-233
failure-specific record.

## Cleanup and ownership state

- The dedicated `fogell-fg233-exact-final` PostgreSQL was removed after the
  exact-source gate. No FG-233 container appears in the current Podman inventory.
- Numerous older Fogell containers remain, including running and `Created`
  instances. Their names imply other tickets and custodians; none was removed,
  restarted, or claimed here.
- Numerous historical worktrees remain, including ownership-sensitive agent and
  Claude worktrees plus prunable `/tmp` entries. No worktree or branch was
  removed based on age, location, or merged-looking ancestry.
- No image or volume was deleted.
- The main worktree was clean before this handoff branch was created from
  current `origin/main`.

## Safe opening move for the next custodian

1. Fetch `origin`, record current `origin/main`, and branch from it.
2. Re-run board-number, queue-row, strict claim, scorecard, and stale-reference
   audits before trusting this snapshot.
3. Recheck PR #364 rather than inheriting its transient green/mergeable state.
4. Inventory Podman and worktrees read-only; resolve ownership before cleanup.
5. Pick one impact dimension deliberately. The two P0 rows are priority inputs,
   not automatic assignments.
6. Run the narrow falsifying proof first, then the authoritative HeMan gate with
   an isolated Podman PostgreSQL, FG-094 baseline, Jenkins oracle, and explicit
   stale-reference base where required.
7. Publish only a proven head. Require exact-head hosted checks, fresh Copilot
   and Codex review, zero unresolved findings, clean main ancestry, and a
   SHA-guarded merge; verify the merge tree and post-merge main gate.

The baton is clean. The next custodian inherits a collision-free Podman gate
with hostile proof, not a runner-specific workaround.
