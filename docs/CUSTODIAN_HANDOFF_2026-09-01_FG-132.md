# Fogell feature-session handoff — 2026-09-01 — FG-132

Status: **FG-132's top-level refusal is implemented, gate-green locally, and
committed UNPUBLISHED on `claude/fogell-feature-work-0f2ecb`.** Publication,
exact-head review and merge remain, and are deliberately left to the owner's
authorization and the pre-push verifier.

This is an incoming brief from the parallel Claude feature session, not an
outgoing-custody record: the custodian's own watch was not interrupted and
nothing here supersedes the 2026-08-29 handoffs. It exists because the codex
worker's queue host (`mario`) was unreachable from HeMan when this was written
(`no route to host`), so the repository is the channel.

## Read this first

- Host: `heman`, worktree `.claude/worktrees/fogell-feature-work-0f2ecb`.
- Base: `origin/main` at `84a83de5` (the PR #281 / FG-229 merge).
- Ticket commit: `FG-132: refuse a duplicate top-level options section`.
- Everything below was measured this session; commands are stated so each
  claim can be re-derived rather than trusted.

## What FG-132 now is

The declarative parser refuses a duplicate TOP-LEVEL `options { }` section
through the same parse-time `rejectingDuplicateSections` mechanism FG-014
built for agent/tools/stages/post — the scope the row's Jenkins measurement
covers. Stage scope deliberately still concatenates (no measurement exists),
pinned by a test that says exactly that. The board row is PARTIAL and carries
the full accounting: the mechanism choice versus the row's original
IR-cardinality sketch, the mutation proof, corpus-neutrality (FG-094 gate
228 files, accepted 200=200, tier1 1=1, zero losses or gains; the five corpus
files with two `options` blocks are all the legal pipeline+stage pair), and
the FG-129 UNPROVEN-by-receipt admission.

Shared-file heads-up: one FG-123a test in
`tests/Fogell.Differential.Tests/Tests.fs` is reworked (its two-section arms
now expect the parse-time cardinality refusal, and the test is renamed to
match), and the FG-132 board row is edited. Coordinate before touching either
until the branch lands or is discarded.

## Gate-blocking findings a custodian should know before the next watch

1. **The gate fails closed on pristine `origin/main` in any UTF-8-locale
   shell.** `scripts/run-project-tests.sh` (FG-229, PR #281) strips ANSI with
   collation-dependent sed bracket ranges (`[@-~]`, `[ -\/]`); under
   `en_US.UTF-8` (GNU sed 4.9) they match nothing, ESC bytes survive, and
   every genuine Expecto success is refused as "non-success".
   Reproduce: `./scripts/prove-project-tests.sh; echo $?` → 1 under
   `LANG=en_US.UTF-8`, 0 under `LC_ALL=C`. Workaround until the fix lands:
   export `LC_ALL=C` for gate runs. A fix session is already working it
   (branch `claude/wonderful-liskov-e813a6`); do not duplicate.
2. **The pinned `jenkins-lab` container no longer exists on luigi**
   (`podman ps -a`, 2026-09-01: only pgbench-db, ctrl, ag1, mc-pg,
   jenkins-bench, mcloving-faceoff2). Every ticket needing a fresh
   measurement — the FG-124 receipt, FG-132's stage-scope remainder, the
   FG-126/FG-130 probes — is blocked until the oracle is re-provisioned.
3. **The shared postgres on 55440 still carries the stale migration-0008
   schema** and fails both DB suites closed. Use a dedicated
   `scripts/pg-test-db.sh <name> <port>` instance per run; this session's
   (`fogell-fg132-db`, 55491) was removed after use and the shared container
   was left untouched.

## What was NOT done, stated so absence is not read as oversight

- No push, no PR, no tag: the operating contract reserves publication for the
  owner, and the pre-push verifier has not seen this head.
- No stage-scope or `environment`/`parameters`/`triggers` cardinality guards:
  each wants its own measurement first (blocked on finding 2), and the board
  row enumerates them as the remainder.
- The FG-229 locale defect was not fixed on this branch: it is the
  custodian's mechanism, a separate ticket-shaped change, and mixing it into
  the FG-132 commit would break one-coherent-commit-per-ticket.
