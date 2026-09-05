# Fogell custodian handoff — 2026-09-04 — FG-251

Status: **FG-251 is implemented, signed, independently reviewed, green on its
exact source and accounting heads, merged into protected `main`, accounted
DONE, and green after the accounting merge.**

This is the outgoing-custody record for the secure API bearer-file closure
published through source PR [#416](https://github.com/SuperBadLabs/Fogell/pull/416)
and accounting PR [#426](https://github.com/SuperBadLabs/Fogell/pull/426).
It records the exact evidence, the deliberately bounded security claim, review
availability, current queue state, and the safe opening move for the next
curator.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- Recorded custody interval: `2026-09-04T20:02:17Z` to the handoff snapshot at
  `2026-09-05T02:19:30Z`.
- `origin/main` observed before this handoff branch:
  `9c05ad5a46483086a68d5b89763e4d1e3ee12f63`.
- Observed `origin/main` tree:
  `a5480b5169149da70afa29bc463246de814fa49a`.
- Post-accounting exact-main
  [run 33935825486](https://github.com/SuperBadLabs/Fogell/actions/runs/33935825486)
  completed successfully with all ten jobs green.
- Handoff branch: `codex/custodian-handoff-fg251-2026-09-04`.
- This document's PR and merge record identify the commit containing the
  handoff. Do not add a predicted self-reference.

Fetch before branching. Queue rows, open PRs, checks, worktrees, containers,
and local services below are observations, not durable locks on external
state.

## Impact dimension closed

The selected dimension was **startup integrity and bounded consumption of the
one global controller API bearer**.

Before FG-251, `FOGELL_API_TOKEN_FILE` was validated as a pathname and then
reopened with `File.ReadAllText`. Startup did not reject a final symlink,
require a regular file owned by the effective service uid, enforce exact
`0400` or `0600` mode, or bound the bytes read. A pathname replacement could
therefore make validation and consumption address different objects; a FIFO
could hold startup indefinitely; and an oversized file could consume
unbounded startup memory.

On Linux, the controller now opens the absolute path once with
architecture-correct `O_NOFOLLOW`, `O_NONBLOCK`, and `O_CLOEXEC`. It classifies
that same descriptor with `statx(AT_EMPTY_PATH)`, requires the kernel to return
the needed type/uid/mode/size fields, accepts only a regular file owned by the
effective uid with exact mode `0400` or `0600`, rejects a metadata size above
4096 bytes, and reads at most 4097 bytes through the owning `SafeFileHandle`.
The extra byte makes post-metadata growth a bounded refusal. A non-Linux host
or an architecture without a tabulated no-follow flag refuses rather than
guessing.

The detailed implementation, hostile proof, and refusal contract live in
[`tickets/FG-251.md`](tickets/FG-251.md). The canonical board row is DONE.

## Source publication receipt

PR #416 merged at `2026-09-05T00:41:57Z`:

```text
reviewed signed head  bfe9db1807944e6f066aa7512e1a1963b32bde9a
source parent         9762d7a689ed21ba4eb87c4dff753859a9382fbc
source tree           1876715b38809420a0019b28bf6f1ff09d918104
protected base        9762d7a689ed21ba4eb87c4dff753859a9382fbc
merge commit          4dc4420a2cfc707c30e23e329bc636adc9f688d0
merge parents         9762d7a689ed21ba4eb87c4dff753859a9382fbc
                      bfe9db1807944e6f066aa7512e1a1963b32bde9a
merge tree            1876715b38809420a0019b28bf6f1ff09d918104
```

The source head verifies locally with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`. The protected merge has the
same tree as the reviewed source head.

The exact source tree passed:

- all 1,133 project tests: Controller 47, Differential 373, Domain 41,
  Execution 161, Groovy 243, Journal 31, Pipeline Parser 141, and Store 96;
- the focused eight-test descriptor-bound token-file list;
- the blocking FG-251 proof, including architecture, metadata, pathname-reopen,
  descriptor, mode, decoding, and growth mutants;
- every other blocking gate lane, including PostgreSQL, evidence, restart,
  approval, and live Jenkins checks;
- the unchanged 228-file compatibility corpus, baseline/current acceptance
  200/200, baseline/current tier-1 1/9, zero losses, and zero gains; and
- all 316 receipt seals.

Hosted exact-head
[run 33932226683](https://github.com/SuperBadLabs/Fogell/actions/runs/33932226683)
passed all ten jobs. Exact-head Codex correctness comment
[`5547961132`](https://github.com/SuperBadLabs/Fogell/pull/416#issuecomment-5547961132)
reported no major issue, and separate security comment
[`5548041785`](https://github.com/SuperBadLabs/Fogell/pull/416#issuecomment-5548041785)
reported no security issue.

Copilot did **not** review this head. Its weekly code-review quota was exhausted
until 2026-09-07; GitHub emitted error events, not review verdicts. Owner
exception comment
[`5547947458`](https://github.com/SuperBadLabs/Fogell/pull/416#issuecomment-5547947458)
records the unavailable reviewer and the compensating specialist-agent, local
Qwen, Codex correctness/security, hostile-mutation, exact local, and hosted
controls. PR #415 was closed unmerged and is superseded; none of its earlier
head is used as evidence.

## Accounting publication receipt

PR #426 merged at `2026-09-05T01:19:44Z`:

```text
reviewed signed head  6972642752f74fe59cf68736c9a0027de3944dd9
accounting tree       a5480b5169149da70afa29bc463246de814fa49a
protected base        4dc4420a2cfc707c30e23e329bc636adc9f688d0
merge commit          9c05ad5a46483086a68d5b89763e4d1e3ee12f63
merge parents         4dc4420a2cfc707c30e23e329bc636adc9f688d0
                      6972642752f74fe59cf68736c9a0027de3944dd9
merge tree            a5480b5169149da70afa29bc463246de814fa49a
```

The accounting head carries the same verified ED25519 signature, and the merge
tree is exactly the reviewed accounting tree. Hosted exact-head
[run 33934012859](https://github.com/SuperBadLabs/Fogell/actions/runs/33934012859)
passed all ten jobs. Exact-head Codex correctness comment
[`5548181364`](https://github.com/SuperBadLabs/Fogell/pull/426#issuecomment-5548181364)
was clean; security comment
[`5548219763`](https://github.com/SuperBadLabs/Fogell/pull/426#issuecomment-5548219763)
reported no security issue. Owner exception comment
[`5548161199`](https://github.com/SuperBadLabs/Fogell/pull/426#issuecomment-5548161199)
records the same Copilot availability boundary. Post-accounting exact-main run
33935825486 then passed all ten jobs.

## Boundaries the next curator must preserve

- Metadata and bytes must come from one descriptor. Reintroducing any pathname
  read or pathname metadata check after open reopens substitution.
- Keep `O_NOFOLLOW`, `O_NONBLOCK`, and `O_CLOEXEC` architecture-correct and
  fail closed on unknown Linux architectures. Do not substitute a guessed
  numeric flag.
- Keep `statx(AT_EMPTY_PATH)` on the opened descriptor and require the returned
  mask before interpreting type, uid, mode, or size.
- Keep exact service ownership and exact `0400`/`0600`; every execute, group,
  other, set-id, and sticky bit remains forbidden.
- Keep both bounds: reject sampled size above 4096 and read at most 4097 bytes.
  The second bound protects growth after metadata validation.
- Preserve strict UTF-8, the existing optional UTF-8 BOM stripping,
  non-UTF-8/UTF-16 BOM refusal, trailing CR/LF trimming, and the existing
  32-non-padding-character token rule.
- A FIFO, directory, symlink, insecure replacement, incomplete metadata,
  unsupported platform, or wrong owner is a stable configuration refusal, not
  a retrying or blocking path.
- The closure does not protect against same-uid in-place mutation of the opened
  inode, validate ancestor components, implement rotation, create tenant RBAC,
  or prove TLS deployment. Do not silently promote those limits into claims.
- Any source or proof change requires fresh exact-head mutation/local/hosted
  evidence and fresh review. Historical green evidence covers only the SHAs
  named above.

## Review-coverage warning

Do not treat `scripts/review-coverage.py` output alone as proof that Copilot
reviewed a head while quota errors are present. During this custody it counted
Copilot error/quota events as coverage even though no review verdict existed.
The owner explicitly confirmed that GitHub Copilot had reached its code-review
limit. Inspect the event body and the actual review objects manually; an error
event is unavailability, not approval. FG-199 owns this process defect and has
been reopened on the concurrent FG-247 branch, although `main` at this snapshot
still records it DONE.

## Current repository and queue snapshot

At `9c05ad5a46483086a68d5b89763e4d1e3ee12f63`, native audits report:

```text
rows=243; DONE=183; open=60; open P0/P1/P2/P3=1/19/33/7
compatibility ledger: tier1=9; tier3=28; admitted=191 of 228
scorecard: 307 of 307 expected case receipts proven
claim inventory: 316 receipts; 33 lane scenarios; 23 proof cases
```

Board-number and claim-citation audits were clean. Claim audit scanned 69
source files and resolved every citation; 27 claims remain explicitly admitted
UNPROVEN.

The sole mechanically open P0 on `main` is FG-026b, runtime adoption of the
FG-026 external-effect ledger. It is active in PR
[#424](https://github.com/SuperBadLabs/Fogell/pull/424), observed open at head
`3852f7d3c3db078cf771812edd469c4478544881`, based on the FG-251 source merge.
Its exact-head gate run 33934551515 passed, but GitHub reported the PR behind
current `main`; a collaborator worktree already contained unpublished
successor commit `f20441c4a9712629676172ce4c2f0d428124385c`. Do not publish,
replace, rebase, or clean that work without coordinating with its owner.

PR [#427](https://github.com/SuperBadLabs/Fogell/pull/427) was also open at
head `610f2bb236eedd757127762194e4b943fb56cc0c`, based on current `main`. It
contains FG-247 receipt/accounting work plus newly filed FG-253/FG-246 and the
FG-199 reopening. GitHub reported it mergeable but blocked while exact-head
[run 33937956255](https://github.com/SuperBadLabs/Fogell/actions/runs/33937956255)
was still in progress. Re-query the head and all checks before acting.

## HeMan local LLM and lane-log note

The local OpenAI-compatible endpoint at `http://127.0.0.1:8000/v1` advertised
`qwen3.8-flash-next` with a 32,768-token serving context. It was used as a
supplemental adversarial reader for syscall ABI, handle lifetime, read bounds,
and mutants. It produced no concrete defect before its response cap; later
requests were transiently unreliable. Recheck availability before using it,
never send credentials, and do not substitute it for canonical proof or
review.

The supplied lane-log excerpt was healthy but incomplete: visible builds ended
with zero warnings/errors, the FG-251 proof passed, and FG-235 was still in a
later build step when the excerpt ended. The line
`bash: cannot set terminal process group ... Inappropriate ioctl for device`
with `no job control in this shell` is normal non-interactive Bash job-control
noise in that lane context, not a test failure. Judge the enclosing command's
exit status and final lane result instead.

## Cleanup and ownership state

- The canonical worktree was clean on `main` before creating this handoff
  branch. The local FG-251 source/accounting branches remain as history; their
  publication state is recorded above.
- The disposable `fogell-fg249` PostgreSQL container created during this
  custody was removed and verified absent.
- Other running Fogell/McLoving containers remained, including
  `fogell-p2-idempotency-3866556752`, `mcloving-w2-test`, `mcloving-w3`,
  `fogell-fg004b-db`, and `fogell-fg231`. None belonged to this custody and none
  was stopped or removed.
- Numerous historical and active worktrees remain under
  `${HOME}/projects/fogell-worktrees`, `.claude/worktrees`, and `/tmp`.
  In particular, the FG-026b and FG-247 Claude worktrees are active evidence of
  concurrent ownership. Age and a merged-looking branch name are not cleanup
  authority.
- No branch, image, volume, worktree, or container owned by another session was
  removed.

## Safe opening move for the next curator

1. Fetch `origin`, record the exact new `origin/main`, and branch from it. Do
   not assume this snapshot or either open PR head is current.
2. Run `scripts/build-audits.sh`, then the board-number, claim, gate-lane,
   scorecard, and stale-reference audits before trusting queue totals.
3. Coordinate with the owners of PRs #424 and #427 and their worktrees before
   rebasing, superseding, merging, or cleaning anything.
4. Inspect review objects and error bodies directly. Until FG-199's error-event
   classification is fixed, do not let the coverage helper turn quota failure
   into a review.
5. Inventory Podman and worktrees read-only and resolve ownership before
   cleanup.
6. Choose one impact dimension deliberately. FG-026b is the sole P0 but is
   already active concurrent work, not permission to race its owner.
7. Start with a narrow falsifying proof. Publish only a signed exact head with
   local and hosted evidence, fresh available reviews or an explicit owner
   exception, zero unresolved findings, current-main ancestry, and a guarded
   merge; verify the merge tree and post-merge `main` gate.

The baton carries one less ambient-secret hazard: controller startup now binds
the global bearer to a single bounded, securely classified descriptor, with
the claim and its limits written down.
