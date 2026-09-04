# Fogell custodian handoff — 2026-09-04 — FG-236

Status: **FG-236 is implemented, signed, independently reviewed, green on its
exact source and accounting heads, merged into protected `main`, accounted
DONE, and green after the source merge.** The post-accounting `main` gate was
still running only its long build lane at the base snapshot for this handoff;
all eight shorter jobs were green.

This is the outgoing-custody record for the confidentiality closure published
through source PR [#387](https://github.com/SuperBadLabs/Fogell/pull/387) and
accounting PR [#408](https://github.com/SuperBadLabs/Fogell/pull/408). It
records the disclosure boundary, the exact evidence, the publication history,
the current queue and infrastructure observations, and the constraints the next
custodian must preserve.

## Read this first

- Host: `heman`.
- Canonical repository: `${HOME}/projects/fogell`.
- Tenure interval: `2026-09-03T22:23:16Z` to `2026-09-04T16:58:48Z`.
- `origin/main` observed before this handoff branch:
  `fe090f98c21334613d5ad30ce1739838b7e98fcf`.
- Observed `origin/main` tree:
  `6daadaf4bdc4dd014dde195eda8abfbddb2daebd`.
- Latest completed exact-main gate at the snapshot:
  [run 33894667223](https://github.com/SuperBadLabs/Fogell/actions/runs/33894667223),
  successful on source merge `1368041ce36ced15f3fcfa40ae69db0e70241bf0`.
- Post-accounting exact-main
  [run 33898013561](https://github.com/SuperBadLabs/Fogell/actions/runs/33898013561)
  was in progress on `fe090f98c21334613d5ad30ce1739838b7e98fcf`;
  eight shorter jobs had passed and `lane (build)` remained active.
- Handoff branch: `codex/custodian-handoff-fg236-2026-09-04`.
- This document's PR and merge record identify the commit containing the
  handoff. Do not add a predicted self-reference.

Fetch before branching. Queue rows, open PRs, gate status, containers,
worktrees, and local services below are observations at this snapshot, not
durable locks on external state.

## Impact dimension closed

The selected dimension was **credential confidentiality across output framing,
late registration, progressive publication, and terminal truth**.

FG-236 reproduced a P0 disclosure: a single-line credential's registered
base64 form could be split by GNU `base64` into 76- and 44-character physical
lines. Fogell removed CR/LF before line-local masking, so neither fragment
matched the registered form. Both progressive and buffered output disclosed the
encoding, the step returned success, and no warning was emitted. The unwrapped
`base64 -w0` control masked correctly. FG-235's multiline-credential refusal
was intentionally narrower and did not close transforms that insert separators
after registration.

Fogell now redacts decoded stdout and stderr as independent raw streams before
physical-line framing. The failure-linked matcher accepts at most one LF, CR,
or CRLF in each inter-character gap of a complete registered form, removes
those separators only when the full form matches, publishes proven-safe
prefixes incrementally, and keeps pending state bounded by the registered
forms. A second physical separator breaks adjacency rather than creating
attacker-controlled pending growth.

The masking inventory remains live for later parallel registrations. The same
registration lock spans inventory sampling, raw matching, framing, trace
admission, and publication. Registration bars open streams atomically; pending
fragments remain held through true EOF, while already committed pre-binding
output is retained only as left context and is not rewritten retroactively.
Per-character stream, order, timestamp, and canonical-token provenance survive
late remasking. This prevents literal `****` from being mistaken for a
protected token and keeps a collapsed cross-line token's timestamp at its final
contributing character.

Progressive callbacks, stored trace, returned buffers, timestamp prefixes, and
timestamp/value composition boundaries are re-screened against registered
forms. Engine-authored warnings and timeout/cancellation narration stay on the
ordinary masked path. Reader faults and missing true EOF fail closed; a stalled
or failed external publisher cannot stop synchronous raw admission or turn a
truncated stream green.

The detailed implementation chronology and boundaries live in
[`tickets/FG-236.md`](tickets/FG-236.md). The canonical board row is DONE.

## Source publication receipt

PR #387 merged at `2026-09-04T16:21:24Z`:

```text
reviewed signed head  f01be0d94f89eb5d56608e8cbfaf734922e429a9
source parent         a34912199b8d2a0e1356bf3f987f84c19b2d4ed3
source tree           2558e043539890c015694020b35d053b5046c6d6
protected base        9fe6e1e92c31a324404bee10848d078456cdf08e
merge commit          1368041ce36ced15f3fcfa40ae69db0e70241bf0
merge parents         9fe6e1e92c31a324404bee10848d078456cdf08e
                      f01be0d94f89eb5d56608e8cbfaf734922e429a9
merge tree            2558e043539890c015694020b35d053b5046c6d6
```

The source head verifies locally with ED25519 key
`SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`. GitHub reports the merge
commit signature valid, and the merge tree is exactly the reviewed source tree.

The exact source/proof tree passed:

- 1,108/1,108 project tests: Execution 161/161, Differential 370/370, and
  Groovy 236/236 among the eight projects;
- 301/301 receipt seals;
- FG-094 baseline/current accepted 200/200, baseline/current tier-1 1/3,
  zero losses, and zero gains;
- 42 focused FG-236 regressions;
- the baseline plus all 44 compiling semantic mutants, including progressive
  and terminal registered timestamp-prefix screening;
- a 200,000-character hostile near-prefix against a 10,000-character
  registered form; and
- FG-235's separate multiline-credential refusal proof.

Hosted exact-head
[run 33891190406](https://github.com/SuperBadLabs/Fogell/actions/runs/33891190406)
passed all ten checks; its build lane took 34m37s. The prior exact-source run
`33888066282` had reached later passing blocking proofs but GitHub canceled it
at the former 30-minute lane ceiling. Commit `f01be0d9` raised that finite
ceiling to 45 minutes; it did not weaken or skip a proof.

Exact-head Copilot review
[`5115131637`](https://github.com/SuperBadLabs/Fogell/pull/387#pullrequestreview-5115131637)
reviewed all 17 files and added no comment. Codex code-review comment
[`5543023744`](https://github.com/SuperBadLabs/Fogell/pull/387#issuecomment-5543023744)
found no major issue, and security-review comment
[`5543123346`](https://github.com/SuperBadLabs/Fogell/pull/387#issuecomment-5543123346)
found no security issue on the same head. The review-coverage audit passed and
all 12 review threads were resolved.

The review cycle was material. In particular, it found that an
engine-generated timestamp could itself equal a transformed form registered
after the earlier implementation snapshot. Corrective signed commit
`87b9393bc9ea661466addf3b6196220abfc59eb8` added registered-form screening to
both progressive and terminal timestamp prefixes and grew the blocking proof
to 44 mutants. PR #387 superseded #371 after the earlier PR's Copilot review
transport failed and could not be re-requested through GitHub's reviewer API.

Post-source-merge exact-main
[run 33894667223](https://github.com/SuperBadLabs/Fogell/actions/runs/33894667223)
passed all ten checks.

## Accounting publication receipt

PR #408 merged at `2026-09-04T16:57:51Z`:

```text
reviewed signed head  f3412f0b8abe6c83b68b9a872475690c35474c4c
accounting parent     1368041ce36ced15f3fcfa40ae69db0e70241bf0
accounting tree       6daadaf4bdc4dd014dde195eda8abfbddb2daebd
protected base        1368041ce36ced15f3fcfa40ae69db0e70241bf0
merge commit          fe090f98c21334613d5ad30ce1739838b7e98fcf
merge parents         1368041ce36ced15f3fcfa40ae69db0e70241bf0
                      f3412f0b8abe6c83b68b9a872475690c35474c4c
merge tree            6daadaf4bdc4dd014dde195eda8abfbddb2daebd
```

The accounting head carries the same verified ED25519 signature. GitHub
reports the merge signature valid, and the merge tree is exactly the reviewed
accounting tree.

Hosted exact-head
[run 33894933136](https://github.com/SuperBadLabs/Fogell/actions/runs/33894933136)
passed all ten checks; its build lane took 31m47s. Copilot review
[`5115519133`](https://github.com/SuperBadLabs/Fogell/pull/408#pullrequestreview-5115519133)
recommended approval after reviewing 2/2 files with zero comments. Codex code
comment
[`5543481177`](https://github.com/SuperBadLabs/Fogell/pull/408#issuecomment-5543481177)
found no major issue, and security comment
[`5543525906`](https://github.com/SuperBadLabs/Fogell/pull/408#issuecomment-5543525906)
found no security issue. Exact-head review coverage passed and no review thread
existed.

The accounting merge moved the derived board from 180 to 181 DONE, from 57 to
56 open, and from two to one open P0. Board-number and queue-row audits and
their planted-bad-state proofs passed before publication.

## Boundaries the next custodian must preserve

- Redaction must remain before CR/LF framing for every secret-bearing raw
  stdout and stderr stream. Line-local masking alone reopens the disclosure.
- Stdout and stderr have independent state and identities. Separate processes
  and parallel streams must not compose an invented credential.
- Accept at most one physical line ending per inter-character gap. Do not split
  registered forms into common fragments and mask those fragments separately.
- Keep the pending-memory and work bounds. A long registered form must not
  stall unrelated safe lines, and a hostile near-prefix must not cause replay
  or unbounded growth.
- Keep the masking inventory live through lexical-scope exit and later sibling
  registration. Registration, matching, trace admission, and publication share
  one synchronization boundary.
- Preserve raw matcher provenance through callbacks, trace storage, transformed
  leak scans, final returned-buffer scans, and terminal truth. Literal star
  runs are ordinary data; matcher-produced canonical tokens are structural.
- Timestamp prefixes must be screened alone and across their value boundary
  against both current and newly registered transformed forms.
- A reader fault, escaped descendant that prevents EOF, or missing provenance
  callback EOF is an infrastructure refusal, not success. Capture cutoff may
  expose only a proven-safe public prefix.
- Historical green runs and clean reviews do not cover a byte-changing
  successor. Any source or proof change restarts local validation, the hosted
  exact-head gate, Copilot and Codex review, security review, and thread audit.
- The gate's build lane now has a finite 45-minute timeout because the complete
  proof takes more than 30 minutes on hosted runners. Do not reduce it without
  first making the same blocking evidence reliably faster.

## Current repository and queue snapshot

At `fe090f98c21334613d5ad30ce1739838b7e98fcf`, native audits report:

```text
rows=237; DONE=181; open=56; open P0/P1/P2/P3=1/19/31/5
compatibility ledger: tier1=5; tier3=28; admitted=195 of 228
scorecard: 301 proven of 301 expected cases
receipt inventory: 306 receipts
```

Board accounting, queue-row, claim-citation, scorecard regeneration, and
stale-reference checks were clean. Claim audit scanned 69 source files and
resolved every citation; 27 claims remain explicitly admitted UNPROVEN.

The sole mechanically open P0 is FG-026b: route every controller-managed
external-effect producer in a bounded registry through the FG-026 ledger and
prove crash-window reconciliation, same-attempt no-op replay, substitution and
stale-ownership refusal, tenant-scoped uncertainty surfacing, and a closed-world
dispatch invariant. This is a priority input, not an automatic assignment.

GitHub reported one open PR:
[#374](https://github.com/SuperBadLabs/Fogell/pull/374), an old FG-237/FG-238
PARTIAL accounting head `16fc762bd6958cbd914dc533817367a4ca5c99ae`.
Its historical checks and Copilot review are green, but GitHub currently
reports `DIRTY` / `CONFLICTING`. The live board already contains FG-237 and
FG-238. Do not merge or mechanically rebase #374: first compare its intended
facts with current tickets and board, then close it as superseded or reconstruct
only genuinely missing evidence on a fresh branch.

Read the concurrently merged
[`WIZARD_HANDOFF_2026-09-04.md`](WIZARD_HANDOFF_2026-09-04.md) and
[`WIZARD_RECEIPT_2026-09-04.md`](WIZARD_RECEIPT_2026-09-04.md) for the
repository-wide and low-priority-end closures that landed during this tenure.

## HeMan local LLM

At the base snapshot, a local OpenAI-compatible llama.cpp discovery endpoint
answered at `http://127.0.0.1:8000/v1`. Its advertised model was
`qwen3.8-flash-next`, with a 32,768-token serving context. Requests can use
`/v1/chat/completions` and
`chat_template_kwargs: { "enable_thinking": false }` for concise review output.
Recheck availability before relying on it; local service state is transient.

It independently reviewed the final source correction and reported no
findings. Treat it as supplemental adversarial capacity only: it does not
replace repository tests, blocking proofs, hosted checks, canonical Copilot and
Codex coverage, or human judgment. Never send credentials or private external
content merely because the endpoint is local.

## Cleanup and ownership state

- The canonical worktree is clean on `main` at the base snapshot. The two local
  FG-236 source/accounting publication branches remain as history; their remote
  branches were deleted by the successful merges.
- No FG-236-named Podman container exists. Numerous older running, created, and
  exited Fogell containers remain, including `fogell-fg235`; none was stopped,
  removed, or claimed during handoff preparation.
- Numerous historical worktrees remain under
  `${HOME}/projects/fogell-worktrees`, `.claude/worktrees`, and `/tmp`, including
  one prunable missing-gitdir entry. Their names and age are not ownership
  evidence. None was pruned or deleted.
- No image, volume, branch, worktree, or container owned by another session was
  removed.

## Safe opening move for the next custodian

1. Fetch `origin`, record the new `origin/main`, and branch from that exact
   head. Do not assume this snapshot is still current.
2. Run `scripts/build-audits.sh`, then board-number, queue-row, claim-citation,
   scorecard, and stale-reference audits before trusting queue totals.
3. Inspect PR #374 as a conflicting historical artifact; compare its facts to
   current `main` before deciding whether anything remains to preserve.
4. Inventory Podman and worktrees read-only. Resolve ownership explicitly
   before cleanup; do not infer abandonment from age or a merged-looking name.
5. Choose one impact dimension deliberately. FG-026b is the sole P0 but remains
   a large bounded-registry and crash-reconciliation problem, not a mandate to
   widen scope silently.
6. Run the narrow falsifying proof first. For a full gate, use an isolated
   Podman PostgreSQL and explicit FG-094 baseline, Jenkins oracle, and
   stale-reference base wherever the scripts require them.
7. Publish only a signed, proven head. Require exact-head hosted checks, fresh
   Copilot and Codex code review, a separate security review, zero unresolved
   findings, current-main ancestry, and a guarded merge; verify the merge tree
   and post-merge main gate.

The baton is clean. The next custodian inherits a separator-transparent,
late-registration-safe masking boundary backed by executable hostile proof,
not a line-fragment heuristic.
