# Fogell custodian handoff — 2026-08-28 — FG-226

Status: **FG-226 is implemented, signed, reviewed on its exact source head,
green in both hosted gate contexts, merged into `main`, and accounted DONE.**

This is an outgoing-custody record. It describes the repository after source
PR [#194](https://github.com/SuperBadLabs/Fogell/pull/194) and accounting PR
[#196](https://github.com/SuperBadLabs/Fogell/pull/196) merged. It is not an
instruction to replay a historical branch or trust a stale check.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- Observed `origin/main`:
  `456b8d42ba6746121db09edce24c49a55c42694f`.
- Observed `origin/main` tree:
  `e64fed36109066e4fba5606c9dfeb5f8ff9344c7`.
- Final source branch: `codex/fg-226-custodian-closure`.
- Final accounting branch: `codex/fg-226-accounting-closure-r2`.
- Handoff branch: `codex/custodian-handoff-fg226-2026-08-28`.
- This document's PR and merge record identify the commit containing the
  handoff; do not insert a predicted self-reference into the file.

Fetch before branching. A local branch name, worktree, or earlier green run is
not evidence that the checkout is current.

## Impact dimension closed

FG-226 removes babashka from the blocking audit-tool boundary. Eight `.bb`
tools became F# scripts compiled by pinned `fflat` 2.1.3 into eight native
linux-x64 executables during the same gate that consumes them. A ninth F# file,
`scripts/fsx/prelude.fsx`, holds their shared Java/Clojure compatibility seam.

The closure is deliberately stronger than a source-language translation:

1. The binaries are built, not committed. Two builds differ because fflat
   embeds a per-build module GUID, so committing a binary would create no
   reproducible source-to-binary identity. Building inside the consumer run
   makes that identity true by construction.
2. The build refuses missing or stale binaries, selects exactly one matching
   pinned fflat payload, and supplies fflat's absent Linux brotli link inputs
   through its own preflight rather than host accident.
3. The shared prelude is gated against a 65,536-code-unit Java differential and
   seven planted divergences, including whitespace, regex shorthand, trim,
   line splitting, and relative-program resolution.
4. The port comparison established byte identity for board accounting, strict
   claim audit, scorecard generation, and review-round reporting. The
   stale-reference audit has the same finding set with deliberate stable
   ordering. `count-options` is multiset-identical with deliberate tie ordering.
5. Every native checker remains behind its falsifying proof. Fresh-checkout
   guards name exact `MISSING`/`STALE` inventories; captured subprocess failures
   retain bounded actionable diagnostics rather than becoming false-green or
   opaque refusals.
6. Hosted failure and review rounds found and closed stale-binary execution,
   fail-open process exits, Jenkins setup/restart false-greens, Linux linker
   assumptions, vacuous proofs, ambiguous path commentary, and discarded
   diagnostics. Every changed source head was retired and freshly reviewed.

The complete equivalence evidence, review history, costs, deviations, and
boundaries are in [`docs/tickets/FG-226.md`](tickets/FG-226.md). The canonical
FG-226 board row is DONE.

## Boundaries and nonclaims

Preserve these statements when evolving the toolchain:

- Equivalence to the babashka twins is not a proof that either implementation
  is semantically correct.
- `sync-scm-cases` is compile-verified only because a live comparison pushes to
  the fixture remote. `probe-input` has an AOT HTTP smoke test but no live
  babashka comparison because that requires the pinned Jenkins lab.
- `audit-stale-refs` and `count-options` intentionally reorder output; do not
  describe all eight ports as byte-identical.
- fflat output is not reproducible, the exact-version NuGet pin is weaker than
  the former version-plus-tarball-digest babashka pin, and the one-maintainer
  toolchain remains a supply-chain owner.
- Whether restoring the 496 MB extracted fflat cache is faster than downloading
  the 199 MB package remains UNVERIFIED. The first relevant run missed the
  cache and could not answer the question.
- The executables are linux-x64. Windows support is untouched.
- The ticket deliberately does not classify every surviving `.bb` mention.
  Historical provenance and compatibility twins are not live gate entrypoints.

## Source publication receipt

PR #194 merged at `2026-08-29T00:18:47Z`, which was 2026-08-28 in
America/Chicago:

```text
reviewed signed head  75f3babb63413ec9adff24b9e9272b4195b5018e
source tree           de483cd19e273f4332431e5d55ca8f06c489813f
base                  1e195bc7f17a534c058f9f3ff1b15f5aeeebf7e9
merge commit          29c9e50b3bb1c2140b65873352b35c3f14457550
merge parents         1e195bc7f17a534c058f9f3ff1b15f5aeeebf7e9
                      75f3babb63413ec9adff24b9e9272b4195b5018e
```

The source commit verifies with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`.

Exact-head GitHub evidence:

- push gate run `33221945820`, job `99017644686`: success;
- pull-request gate run `33221960353`, job `99017684932`: success;
- Copilot formal review `5055932123`: exact full head, no comments;
- Codex clean comment `5458991745`: reviewed `75f3babb63`, no major issues;
- `scripts/review-coverage.py --pr 194` covered both reviewers on the unchanged
  head immediately before merge, with zero inline comments.

The authoritative gate passed 926/926 project tests plus FG-207's 8/8. Every
blocking proof and lane terminated `OK`; both hosted contexts also passed the
Store recheck, runnable-controller vertical slice, backup/restore drill, and
migration rollback rehearsal.

## Accounting publication receipt

PR #196 merged at `2026-08-29T01:08:13Z`, which was 2026-08-28 in
America/Chicago:

```text
reviewed signed head  87c1b8d01ddd6afe895d25c9d8c0ecee13214569
source tree           e64fed36109066e4fba5606c9dfeb5f8ff9344c7
base                  29c9e50b3bb1c2140b65873352b35c3f14457550
merge commit          456b8d42ba6746121db09edce24c49a55c42694f
merge parents         29c9e50b3bb1c2140b65873352b35c3f14457550
                      87c1b8d01ddd6afe895d25c9d8c0ecee13214569
```

The accounting commit verifies with the same ED25519 key. Exact-head evidence:

- push gate run `33224349838`, job `99024887587`: success;
- pull-request gate run `33224367071`, job `99024939566`: success;
- Copilot formal review `5056082694`: exact head, both files, no comments;
- Codex clean comment `5459269278`: reviewed `87c1b8d01d`, no major issues;
- `scripts/review-coverage.py --pr 196` covered both reviewers on the unchanged
  head immediately before merge;
- `scripts/bin/review-rounds 196` reported zero comments, and the GitHub inline
  comment inventory was zero.

Accounting PR #195 was retired after Copilot found an ambiguous local-date/UTC
date statement. Its hosted runs were cancelled; #196 states both timezones and
records the review round. Source candidates #181 and #186 through #193 were
likewise retired whenever a valid finding changed the head.

## Current repository and queue snapshot

At `456b8d42ba6746121db09edce24c49a55c42694f`, the derived board state is:

```text
rows=210; DONE=135; open=75; open P0/P1/P2/P3=3/25/35/12
compatibility ledger: tier1=1; tier3=28; admitted=199
```

The three canonical open P0 rows are FG-026, FG-041, and FG-224. This is
inventory, not an instruction to choose one automatically.

One pull request remains open: [#163](https://github.com/SuperBadLabs/Fogell/pull/163)
at head `1c30031fb6298646ab5a66eeeba745f5891036b7`, based on
`804bf7967cf3708eb3bb44387d59a24310c89607`. GitHub reports it dirty against
current `main`. Its historical state is reconciliation input, not merge
evidence.

## Safe opening move for the next custodian

1. Fetch `origin`, record exact `origin/main`, and branch from it.
2. Run `scripts/build-audits.sh`, the native board audit, and the canonical row
   audit before trusting queue totals or an old worktree.
3. Choose an impact dimension explicitly. Reconcile PR #163 only as fresh work
   from current `main`; do not merge its historical head.
4. Run the ticket's narrow falsifying proof, then the authoritative HeMan gate
   with PostgreSQL and, on this corpus host, the external FG-094 baseline and
   Jenkins oracle.
5. Publish only a signed, already-proven head. Require exact-head Copilot and
   Codex coverage plus both hosted gate contexts. A finding that changes the
   head requires a fresh PR and fresh reviews.
6. Merge with an exact-head guard, fetch the merge identity, and only then move
   canonical accounting to DONE.

The watch is complete. Take the baton; welcome to the jungle.
