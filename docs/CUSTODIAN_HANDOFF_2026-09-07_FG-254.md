# Fogell custodian handoff — 2026-09-07 — FG-254

Status: **FG-254 is implemented, signed, independently reviewed, merged,
accounted DONE, and green after both source and accounting merges. The Tier-1
baton is ready for the next custodian.**

This is the outgoing-custody record for the disposable, runtime-pinned GNU
Make oracle and the resulting Tier-1 compatibility receipt. Source PR
[#432](https://github.com/SuperBadLabs/Fogell/pull/432) and accounting PR
[#435](https://github.com/SuperBadLabs/Fogell/pull/435) are the authoritative
publication records.

## Read this first

- Host: `HeMan`.
- Canonical repository: `${HOME}/projects/fogell`.
- Handoff snapshot: `2026-09-07T05:31:00Z`
  (`2026-09-07T00:31:00-05:00`).
- `origin/main` observed before this handoff branch:
  `cfb567a5483e37b2902631b390399e9b347946b6`.
- Observed `origin/main` tree:
  `6bf2918747a74df27e3dc7aeceae5a1e79f44d51`.
- Handoff branch: `codex/custodian-handoff-fg254-2026-09-07`.
- This document's PR and merge history identify the commit containing the
  handoff. Do not add a predicted self-reference.

Fetch before branching. Queue rows, PRs, checks, worktrees, and containers
below are observations, not durable locks on external state.

## Tier-1 dimension closed

FG-254 closes one deliberately narrow compatibility gap. The selected corpus
file, `charlires_golang-docker-jenkins.Jenkinsfile`, reached GNU Make on HeMan
while the pinned Jenkins oracle image lacked `make`. Fogell now constructs a
network-disabled, digest-checked derivative of the pinned Jenkins 2.568.1
image containing the exact GNU Make 4.3 executable already measured on HeMan.

One isolated compatibility lane proved that both engines fail at the first
`make build-base` because the empty workspace has no Makefile. They produce
the same four compared output lines, skip the same two later stages, and leave
the same empty workspace. The file moved from admitted to Tier 1:

```text
compatibility ledger  tier1=13; admitted=187; tier3=28 of 228
hand-written receipts 308 of 308 expected cases proven
```

The claim stops there. It does not prove the intended Docker build, later Make
targets, `ls`, or JUnit; none executes. The derivative is a local lab oracle
artifact, not a generally published supply-chain image.

## Immutable runtime and receipt identities

```text
selected corpus source sha256  35091bc2909001e1fa14b136e30a6cf983406342c7a3ab6924f08b225c05f1a9
promoted receipt sha256        2b5a887fd16aba73f892bdfab485d21e40d2e8674296ccd5411b8c2ade019860
promoted receipt seal          fe3dc56d1d187712bebdee932c619cd7cb269a1fee95b3ddebf6751d9daa4dfa
GNU Make executable sha256     d78b8f1d099fbcfb6f2f49ab87223b9b68fb3956642f92d6ec6de812e8afa965
derived Jenkins image id       ddc4e247ca53c13baab8df6d13ab6646a4663ce2e843187661c74b8860a163f3
derived image digest           sha256:dfdd9ae5effae9bc2484e25944437a23cf06949074de7c56cd5cd42981843990
plugin closure count           154
plugin closure digest          3b31a5bf08550cfa0155f99dd22dd61a17934ba49e474f11252ec40dd854783b
private CLI closure            b184ff137b1c63e3349dce6b23a79d36073017e7577ffc43c5978aac71f16a17
fresh controller identity      f01f16934070963c4ccd4867c205aafa67f0befbb597bc3c57e86d3cc89865ae
```

The final exact-source lane ran from `2026-09-07T00:05:51Z` through
`00:07:01Z`, produced one PROVEN receipt, preserved its promoted bytes, and
left no task-owned runtime residue.

## Source publication receipt

PR #432 merged as:

```text
reviewed signed head  4366214e781a538c07b87453e4e0503d826e13ff
source tree           cccc53f98b0f3323cf91475a9145d4051c4e0bfc
protected base        3381a6bb294b1fc28f06c264723a5975dfb93d32
merge commit          7578fd4905e8b7e2b08296318635be23b3a04c02
merge parents         3381a6bb294b1fc28f06c264723a5975dfb93d32
                      4366214e781a538c07b87453e4e0503d826e13ff
merge tree            cccc53f98b0f3323cf91475a9145d4051c4e0bfc
```

The protected merge is GitHub-verified and tree-identical to the reviewed
source head. The exact source passed the authoritative local gate: 1,139 of
1,139 project tests, 321 of 321 receipt seals, every fast and slow/mutant
lane, and final bare `OK`. Hosted exact-head
[run 34068813444](https://github.com/SuperBadLabs/Fogell/actions/runs/34068813444)
passed every leaf job and the protected aggregate `gate`. Post-merge exact-main
[run 34071018691](https://github.com/SuperBadLabs/Fogell/actions/runs/34071018691)
also passed every job.

Exact-head Codex comment
[`5563273569`](https://github.com/SuperBadLabs/Fogell/pull/432#issuecomment-5563273569)
was clean. Exact-head Copilot review `5127220550` made one portability
suggestion: the Python-invoked PID1 attestation reader did not itself need an
executable bit. The accounting follow-up folds that suggestion, and all eleven
source-review threads are resolved.

## Accounting and portability-fold receipt

PR #435 merged as:

```text
reviewed signed head  bc3f1b7846a35b9b618824542ba91de977d9bc85
accounting tree       6bf2918747a74df27e3dc7aeceae5a1e79f44d51
protected base        bf3f29b8add56621ea46c109a34c7975a22ed7b9
merge commit          cfb567a5483e37b2902631b390399e9b347946b6
merge parents         bf3f29b8add56621ea46c109a34c7975a22ed7b9
                      bc3f1b7846a35b9b618824542ba91de977d9bc85
merge tree            6bf2918747a74df27e3dc7aeceae5a1e79f44d51
```

The protected merge is GitHub-verified and tree-identical to the reviewed
accounting head. The follow-up changes the reader prerequisite in
`prove-fg231-proof-bounds.sh` from executable to regular non-symlink, matching
how Python consumes it. A focused proof with the reader deliberately
non-executable killed six reader mutants and two compiled writer mutants,
passed all eight runtime controls, and left no residue.

The exact accounting head passed the same complete local 1,139-test,
321-seal, all-lane gate. Hosted exact-head
[run 34082899360](https://github.com/SuperBadLabs/Fogell/actions/runs/34082899360)
passed all nine leaf jobs and aggregate `gate`. Copilot recommended approval
at 5/5 with zero comments; exact-head Codex comment
[`5565021097`](https://github.com/SuperBadLabs/Fogell/pull/435#issuecomment-5565021097)
was clean; formal review coverage passed both reviewers on the exact head; and
the PR had zero review threads.

Post-merge required fast
[run 34083468324](https://github.com/SuperBadLabs/Fogell/actions/runs/34083468324)
passed every job and aggregate `gate` in 6m44. Post-merge slow/mutant
[run 34083468329](https://github.com/SuperBadLabs/Fogell/actions/runs/34083468329)
also passed.

## Boundaries the next custodian must preserve

- Keep the selected corpus file alone in its lane. Promotion is all-or-nothing
  on the differential exit, and unrelated expected divergence must not share
  this receipt run.
- Keep the archived exact-HEAD private runner, its private CLI build, and every
  consumed helper and policy input bound to that commit. Do not consume a
  mutable canonical checkout after archival.
- Keep the exact base-image/digest, derived-image identity, Make paths and
  hashes, and 154-plugin closure checks before execution and promotion.
- Keep a fresh random-ID controller and home. Do not inherit the persistent
  lab JVM, jobs, plugins, or state.
- Keep the owner-UID fence before Luigi starts its controller and before HeMan
  opens its SSH listener; then retain the namespace/no-egress fences.
- Keep the queue item, `queueId`, cryptographic token, Replay definition,
  PATH, resolved tool, corpus case, nonce marker, and manifest-node bindings.
- Preserve cleanup ordering: kill the tunnel; prove the exact controller and
  home absent; only then reopen access.
- PID1 self-attestation remains descriptor-bound. The controller creates a
  fresh nonce and exact payload naming PID, `NSpid`, and executable. The reader
  opens with `O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`, requires a regular file owned
  by the current uid with one link and bounded size, and reads that same
  descriptor. The writer remains `CreateNew` and non-overwriting.
- The reader is invoked by Python and must be a regular non-symlink; do not
  restore an executable-bit prerequisite without a new demonstrated need.
- Any source, proof, receipt, or runtime-policy change requires new exact-head
  local/hosted evidence and fresh review. Historical green runs cover only the
  identities above.

## Gate topology inherited from FG-256

PR #434 landed before the accounting fold. `.github/workflows/gate.yml` now
runs the fast required lanes for PRs and `main`; `.github/workflows/gate-mutants.yml`
runs the slow mutant lane after every `main` merge and nightly. An unset local
`FOGELL_GATE_LANES` still runs all lanes. The accounting publication exercised
all three paths: complete local, fast required hosted, and post-merge mutants.
Do not mistake the faster protected gate for removal of mutation coverage.

## Current repository, queue, and ownership snapshot

At the observed `main` identity, native audits report:

```text
rows=249; DONE=182; open=67; open P0/P1/P2/P3=1/22/37/7
compatibility ledger: tier1=13; tier3=28; admitted=187 of 228
scorecard: 308 of 308 expected hand-written receipts proven
claim inventory: 321 receipts; 33 lane scenarios; 23 proof cases
```

Board-number, queue-row, scorecard, and claim-citation audits were clean. The
claim audit scanned 69 source files, resolved every citation, and found 27
claims explicitly admitted UNPROVEN.

The only open PR observed was
[#424](https://github.com/SuperBadLabs/Fogell/pull/424), FG-026b, at head
`ff5798b303895c13d63af4c07e8e52b9b70d7acc`. Its listed checks were green,
but its owner worktree on branch `claude/fg-026b-effect-dispatch` contained an
uncommitted modification to `src/Fogell.Store/Store.fs`. FG-026b is the sole
open P0. Do not publish, rebase, merge, replace, clean, or infer staleness;
coordinate with its owner and re-query both the remote head and worktree.

The next mechanically open P1 rows include FG-014, FG-027/027a/027b, FG-197,
FG-199, and further queue items. Several are PARTIAL or have historical
worktrees. The board ordering is not cleanup authority or proof of absent
ownership; choose an unowned impact dimension only after a fresh inventory.

## Cleanup and local ownership state

- The canonical worktree was clean on `main` before this handoff branch.
- Task-owned accounting proof containers
  `fogell-fg254-accounting-proof` and `fogell-fg254-accounting-gate` were
  removed and verified absent.
- A pre-existing running container named `fogell-fg254` was already present
  when this custody began. It was not created, stopped, or removed here.
- Other running ownership-sensitive containers included
  `fogell-p2-idempotency-3866556752`, `mcloving-w2-test`, `mcloving-w3`,
  `fogell-fg004b-db`, and `fogell-fg231`. Several unrelated exited containers
  also remain. None was changed.
- Numerous historical and active worktrees remain under
  `${HOME}/projects/fogell-worktrees`, `.claude/worktrees`, and `/tmp`. The
  dirty FG-026b worktree is affirmative concurrent-ownership evidence. Age or
  a merged-looking branch name is not cleanup authority.
- No branch, image, volume, worktree, or container owned by another session
  was removed.

## Safe opening move for the next custodian

1. Fetch `origin`; record the exact new `origin/main`; branch from it.
2. Build and run the board-number, queue-row, claim, scorecard, gate-lane, and
   stale-reference audits before trusting this snapshot's counts.
3. Re-query open PRs and coordinate with PR #424's owner. Do not race the sole
   P0 or disturb its dirty worktree.
4. Inventory worktrees and containers read-only. Establish ownership before
   cleanup, even if an artifact looks old or its branch appears merged.
5. Re-read [`tickets/FG-254.md`](tickets/FG-254.md), the runtime manifest,
   receipt, and proof before changing this compatibility lane.
6. Select one unowned P1 impact dimension from the current board and begin
   with a narrow falsifying proof.
7. Publish only a signed exact head with current-main ancestry, complete local
   all-lane evidence, required hosted fast evidence, available exact-head
   reviews, zero unresolved findings, guarded merge-tree verification, and
   post-merge fast plus mutant results appropriate to the change.
