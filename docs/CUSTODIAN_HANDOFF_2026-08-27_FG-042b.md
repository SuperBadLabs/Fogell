# Fogell custodian handoff — 2026-08-27 — FG-042b

Status: **the FG-042b impact dimension is implemented, signed, reviewed on its
exact source head, green in all hosted gates, and merged into `main`.**

This is an immutable outgoing-custody record. It describes the repository as
observed after pull request
[#172](https://github.com/SuperBadLabs/Fogell/pull/172) merged. It is not an
instruction to replay old branches, remove old worktrees, or treat local
candidates as published work.

## Read this first

- Host: `heman`.
- Canonical repository: `/home/srikanth/projects/fogell`.
- Published source branch: `codex/fg-042b-artifact-retrieval-v2`.
- Reviewed signed source head:
  `cc7293a4f70592c0f5b398671f18fa5c70bd3788`.
- Source tree: `07048143ed39c651aea7910e6a0c740890770c2d`.
- Source parent: `f0dcf58b13767446c903abdb6b19a787e93cd321`.
- Merge commit: `e27fc3539ccbb350493212c480adcefc9012e349`.
- Merge parents, in order: the source parent above and the reviewed source
  head above.
- GitHub merged the PR at `2026-08-28T03:50:40Z`, which was
  `2026-08-27T22:50:40-05:00` on HeMan.
- The source commit has a good ED25519 signature for
  `srikanth.remani@gmail.com`, key
  `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`.
- Handoff branch: `codex/custodian-handoff-fg042b-2026-08-27`.
- Handoff publication: pull request
  [#173](https://github.com/SuperBadLabs/Fogell/pull/173). Its resulting merge
  identity is the publication record for this file; do not copy a predicted
  self-reference into it.

The local `main` branch was not the source of this handoff. It was stale at
`804bf7967cf3708eb3bb44387d59a24310c89607`; the handoff branch was created
directly from refreshed `origin/main` at the FG-042b merge above. A successor
should fetch first and branch from `origin/main`, not assume local `main` is
current.

## Impact dimension closed

FG-042b closes the artifact-retrieval dependency left by FG-042. An
authenticated caller can retrieve a known archived relative path at:

```text
GET /api/v1/organizations/{organizationId}/projects/{projectId}/builds/{buildId}/attempts/{attemptId}/artifacts/{path}
```

The published contract has five load-bearing properties:

1. Authorization precedes identifier parsing, and Store lineage must bind the
   organization, project, build, and terminal attempt before storage is read.
2. The worker atomically moves build-keyed staging bytes to an attempt-keyed
   snapshot before publishing terminal database truth. Retry children cannot
   mutate a prior attempt's URL.
3. Retry ancestry freezes unambiguous legacy parent bytes before child launch.
   Lazy legacy adoption is limited to the single-node terminal leaf and is
   serialized against retry decisions; ambiguous multi-node adoption refuses.
4. On Linux, the organization workspace, snapshot parent, attempt root, and
   final target are descriptor-validated. The final response is copied from the
   opened descriptor rather than from a re-resolved pathname.
5. A successful response is `application/octet-stream`, has the exact byte
   length, sets `X-Content-Type-Options: nosniff`, and supplies an attachment
   filename. Policy absence is 404; retryable storage failure is 503; an
   unsupported platform refuses explicitly.

The complete contract, acceptance evidence, and boundaries are in
[`docs/tickets/FG-042b.md`](tickets/FG-042b.md). The operator flow is in
[`docs/runbooks/controller-host.md`](runbooks/controller-host.md). The canonical
FG-042 and FG-042b rows in
[`docs/EXECUTION_BOARD.md`](EXECUTION_BOARD.md) are DONE.

## Verification and publication receipt

The unmodified authoritative `./scripts/build-and-test.sh` passed on the exact
source head with live PostgreSQL, the external FG-094 baseline, and the pinned
Jenkins oracle.

Suite results:

```text
Controller.Api   38/38
Differential    279/279
Domain           34/34
Execution       101/101
Groovy          225/225
Journal          31/31
Parser          120/120
Store            96/96
Total           924/924
```

Every blocking proof lane passed. The runnable-controller proof crossed the
real database, controller, worker, Run.Host, and shell; it archived bytes
`00 ff 41 0d 0a 42`, retrieved the same six bytes over HTTP with the expected
headers, and confirmed an unauthenticated request returned 401 without the
payload.

The external fixtures used by the gate were:

```text
FOGELL_REGRESSION_BASELINE=/home/srikanth/projects/fogell-baselines/fg-094-candidate/baseline.json
FOGELL_JENKINS_ORACLE=/home/srikanth/projects/chengis/chengis/test/resources/jenkins-oracle/Jenkins-RAnvil-Chengis-228-projects.tsv
```

GitHub recorded three successful exact-head `gate` jobs:

- run `33139145657`, job `98745845352`;
- run `33139148482`, job `98745854561`;
- run `33139196331`, job `98746003654`.

The repository's own `scripts/review-coverage.py --pr 172` reports both expected
reviewers covered the current head: Copilot through an exact-head pull-request
review and Codex through an exact-head clean issue comment. Copilot reviewed all
11 changed files and generated no comments. `scripts/review-rounds.bb 172`
reports zero inline comments across zero finding rounds. No earlier green SHA or
review was substituted for the merged source head.

## Scope boundaries to preserve

- Retrieval is by known path. Listing, metadata APIs, ranges, upload,
  promotion, retention, content addressing, and reproducible packaging remain
  outside FG-042b.
- No immutable-read guarantee is made while a same-UID process changes an
  artifact in place.
- Descriptor validation addresses traversal and symlink escape. It does not
  provide same-UID filesystem isolation, hard-link isolation, mount or
  namespace isolation, or protection from a host administrator.
- The route is Linux-specific. Do not weaken the descriptor checks to claim
  unsupported cross-platform behavior.
- There is no new retained repository evidence bundle for FG-042b. The durable
  records are the source tree, tests, proof script, hosted checks, exact-head
  reviews, ticket, and PR.
- The disposable `fogell-fg042b-api` verification container was removed during
  source closure.

## Current repository and queue snapshot

At the published source merge, the board's derived accounting is:

```text
rows=209; DONE=133; open=76; open P0/P1/P2/P3=3/26/35/12
```

The three open P0 rows in the canonical wave tables are FG-026 (TODO), FG-041
(PARTIAL), and FG-224 (PARTIAL). This is inventory, not a recommendation to
start one of them.

Some ranking prose predates recent merges. In particular, the live-queue text
still says FG-042 is open even though the canonical FG-042 and FG-042b rows are
DONE. It also describes FG-177 and FG-221 publication state from an older
snapshot. The next custodian should treat the canonical ticket row, ticket
document, merged ancestry, and hosted PR record as the evidence boundary. Queue
prose is a ranking aid and should be revalidated before it directs new work.

Two older pull requests were still open when this handoff was prepared, and
both were conflicting with the advanced `main`:

- [#163](https://github.com/SuperBadLabs/Fogell/pull/163), FG-073, head
  `1c30031fb6298646ab5a66eeeba745f5891036b7`;
- [#161](https://github.com/SuperBadLabs/Fogell/pull/161), FG-037, head
  `ef553330d0c4a5fd0f5c6d6fce66b5e2e0218c55`.

Their historical checks do not clear a conflict resolution or a changed head.
If a successor takes ownership of either PR, they must reconcile it against
current `main`, rerun the full local acceptance appropriate to the resulting
tree, obtain replacement exact-head reviews and checks, and re-audit findings
before merge.

HeMan also contains many historical ticket worktrees and a separate unmerged
local FG-073 handoff commit `1289fe3` on
`codex/custodian-handoff-2026-08-27`. Their presence is not evidence that they
are current, clean, published, abandoned, or safe to delete. Inspect each
worktree and its owning branch before any cleanup; this handoff intentionally
removes none of them.

## Safe opening move for the next custodian

1. Fetch `origin`, resolve the exact current `origin/main`, and start a new
   `codex/` branch from that commit.
2. Run the board accounting and queue audits before trusting totals or ranking
   prose.
3. Inventory open PRs and local worktrees. Decide explicitly whether the next
   dimension is fresh work, reconciliation of a published candidate, or board
   state repair; do not combine those scopes implicitly.
4. Read the selected ticket's acceptance and residual boundaries, then write
   the falsifying proof before broad implementation.
5. Before publication, run the unmodified authoritative gate with its required
   live database and external fixtures. After publication, require both expected
   reviewers and all required checks on the exact current head, triage every
   finding round, and merge only that head.

The watch is complete. Welcome to the jungle.
