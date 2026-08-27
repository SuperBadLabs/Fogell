# Fogell custodian handoff — 2026-08-25

Status: **local aggregate assembled, signed, and closed by the documented sequenced authoritative composite; nothing pushed**.

This is the operational handoff for the next custodian. It records local state only. It is not a publication, merge, or parent-ticket completion claim.

## Read this first

- Host: `heman` (the working SSH route used during custody was `srikanth@heman-1`).
- Canonical repository: `/home/srikanth/projects/fogell`.
- Aggregate worktree: `/home/srikanth/projects/fogell-worktrees/custodian-fg166-197-130-123`.
- Aggregate branch: `agent/custodian-fg166-197-130-123`.
- `origin/main` remained `8c2930428d32c6b77fd68334e5cd09b2b3c79972`.
- The local aggregate is 36 signed commits ahead of that base. All 36 verified with `git verify-commit`; zero signature failures were observed.
- The gated aggregate candidate was clean and signed at commit `78566659860fa6d6c3a7d0b596f6eb0afc6490d7`, tree `998f9c785351ffac46d07264830cd42ed71c5126`. After installing this handoff, the only untracked paths are this document and `docs/CUSTODIAN_RECEIPT_2026-08-25.md`; the final documentation commit closes them.
- Signing key seen on the aggregate and individual commits: ED25519 `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`.
- No branch was pushed. No PR was opened. Nothing was merged.

Final aggregate-gate tuple from the exact post-gate attestation:

```text
FINAL_AGGREGATE_GATE_VERDICT=PASS (documented sequenced authoritative composite)
FINAL_AGGREGATE_GATE_HEAD=78566659860fa6d6c3a7d0b596f6eb0afc6490d7
FINAL_AGGREGATE_GATE_TREE=998f9c785351ffac46d07264830cd42ed71c5126
FINAL_AGGREGATE_GATE_LOG=/private/tmp/fg044bd-aggregate-sequenced-closure-20260825T0208Z/composite.raw.log
FINAL_AGGREGATE_GATE_LOG_SHA256=fb11da836b0cdc0edcbf4f6196965d968afdd0d45295cacb2d6f1c120bfd8e66
FINAL_AGGREGATE_GATE_EVIDENCE_MANIFEST=/private/tmp/fg044bd-aggregate-sequenced-closure-20260825T0208Z/MANIFEST.sha256
FINAL_AGGREGATE_GATE_EVIDENCE_MANIFEST_SHA256=0f02561126a8330f02b1f02920b8f5b1c177c3ef7a6c5ddd6a72419a32752975
FINAL_AGGREGATE_POST_STATUS=clean; signed commit verified; pre/post/final HEAD, tree, and four FG-044b(d) pins identical
FINAL_AGGREGATE_DB_AND_PROCESS_CLEANUP=no active Fogell/build gate process; no current-run FG044bd or approval-harness scratch; ordinary suites retained 110 fogell-exec and 38 fogell-journal fixture directories
```

These values are bound to the exact sealed post-gate attestation, not inferred from HEAD alone.

## Highest-priority next action

Fix the pre-existing process-global `FOGELL_CREDENTIALS` / `FOGELL_CREDENTIALS_FILE` fixture isolation race in `Fogell.Differential.Tests`, then rerun the unmodified stock `./scripts/build-and-test.sh` on the exact aggregate head.

Two consecutive stock gates on FG-044b(d)'s exact source candidate failed identically in the unrelated FG-044b(c) fixture:

```text
Fogell.Differential.FG-044b(c) credential key boundary refuses atomically.
mixed valid and prefixed-key requests refuse atomically in either sibling order

exact controls still bind all three credential kinds
expected: success
actual: failure
```

Both runs reported Differential `210 passed, 1 failed, 0 ignored, 0 errored` out of 211. The failure is a parallel-test environment collision: `credentialKeyBoundaryRefusal` and `credentialCompanionPreservation` both rewrite the same process-wide credential variables while other `FogellSide.run` calls can observe them. It is not an FG-044b(d) stash-semantic failure.

The sequenced composite is valid diagnostic evidence, not a substitute for fixing the stock gate. It changed one derived copy of `scripts/build-and-test.sh`: only the `Fogell.Differential.Tests` invocation gained `-- --sequenced`. The repository script stayed byte-identical. That composite passed Differential 211/211, the full remaining gate, FG-094 compatibility over 228 files, the approval lane, and final `OK`; focused FG-044b(d) passed Execution 1/1 and Differential 4/4. The next custodian should isolate or serialize the global credential fixture in the actual tests and then require the ordinary repository script to pass without a derived wrapper.

## Packages closed during the final custody window

### FG-026 — durable effect checkpoint Store foundation

Implemented scope:

- migration `0003_effect_checkpoints.sql`;
- immutable effect identity and payload digest;
- four persisted states: prepared, applied, confirmed, uncertain;
- fenced authority checks, replay semantics, transition ordering, stale-effect reconciliation, and tenant-scoped uncertain listing;
- Store-level live PostgreSQL proofs.

This is the durable Store foundation. Do not silently broaden the claim to external-connector/controller integration unless that wiring is separately inspected and proven.

Commit identities:

- individual signed commit: `a27c9b6ab7382e55662f37ef9d24a562ecd05f6f`;
- individual tree: `41aea58e82b230308f350fc816e4e6921c1c1f67`;
- individual parent: `f8ce29ff21058484f1330fa9ebc3add2d7d38195`;
- individual worktree: `/home/srikanth/projects/fogell-worktrees/fg-026-effect-ledger`;
- aggregate signed commit: `feb799638df6caac01faab5c8244e733020da542`;
- aggregate tree: `905f39fac26897f018587b379bfc02cc850f1dae`;
- aggregate parent: `2029b027dceb43a63f66412a2b00413d45276386`.

Proof state:

- effective R2 mutations: 44/44 credited;
- survivors / compile-invalid / vacuous: 0 / 0 / 0;
- restored focused Store slice: 10/10 with `FG026_LIVE_PG=1 FG026_SCHEMA=0003 FG026_CONCURRENCY=16`;
- restored full Store at R2 seal time: 42/42;
- aggregate repository-wide authoritative gate: PASS;
- aggregate live FG-026 slice: 10/10;
- installed migration checksum: `1ce854dd3de720521eac6afc7322f2b098b644f07460a959b0df8edcf9f319c6`.

Retained HeMan evidence:

- R2 sealed bundle: `/tmp/fg026-mutation-evidence-r2-20260824T213928Z`;
- `FINAL.md` SHA-256: `1b9e8f9d451eab12d7f04b0a31540c42000dce9477e1a2cdf30479fe05a95a3f`;
- `MANIFEST.md` SHA-256: `1f7c7dc801a974ef279d07fb0d8a7e512dac79423e08f169b88ea3bb11b61cbb`;
- `PAYLOAD.sha256` SHA-256: `8f20d0c034af8fb5958b4ab76d7846c2ef0ff1c552ddbd204910b9ae5efd5cae`;
- aggregate gate stripped log: `/tmp/fg026-aggregate-authoritative-full-gate-20260824T1800Z.stripped.log`, SHA-256 `d6171aee27448a77c273256e0e730a38f99e4581427ea12b3a49bd6ac851f84c`;
- aggregate live coverage stripped log: `/tmp/fg026-aggregate-authoritative-fg026-live-coverage-20260824T1800Z.stripped.log`, SHA-256 `891cf8e8bc6b43c775c680a8dbc7b63a087e97f571708563e6d21157c787d929`.

The R2 bundle reports 403 payload hashes, directories mode 500, files mode 444, and zero writable entries. `/tmp` is still ephemeral storage; preserve it before reboot or cleanup.

### FG-027b — persistent retry-decision Store foundation

Implemented scope:

- migration `0004_retry_decisions.sql`;
- immutable persisted parent snapshot and child snapshot;
- durable child-created or budget-exhausted/dead-letter decision;
- exact replay after child state mutation and under hostile replay inputs;
- restore-epoch serialization and row-lock concurrency;
- atomic child + decision + event + outbox persistence;
- strict tenant, authority, lineage, boundary, and corruption refusal;
- tenant-scoped deterministic dead-letter listing.

Scope boundary: parent FG-027 is **not DONE**. There is still no runtime/scheduler/controller entrypoint or public API integration that decides when terminal attempts should be retried and drives this Store operation. Terminal-status retry policy remains external. Do not describe the migration and repository layer as end-to-end retry semantics.

Commit identities:

- individual signed commit: `f738896cf9865d52b8d303bd246975304fae528b`;
- individual tree: `a203b5963e681e103e98e0bd20649932b35c8028`;
- individual parent: `feb799638df6caac01faab5c8244e733020da542`;
- individual worktree: `/home/srikanth/projects/fogell-worktrees/fg-027-retry-decisions`;
- aggregate signed commit: `16f69b9b9df8f34b164851462dfad75706cf1185`;
- aggregate tree: `a203b5963e681e103e98e0bd20649932b35c8028`;
- aggregate parent: `feb799638df6caac01faab5c8244e733020da542`.

Proof state:

- effective mutations: 30/30 credited;
- survivors / compile-invalid / vacuous: 0 / 0 / 0;
- three zero-credit wrong-arm histories retained separately;
- focused live PostgreSQL FG-027b: 12/12 with `FG027B_LIVE_PG=1 FG027B_SCHEMA=0004 FG027B_CONCURRENCY=16`;
- full Store: 55/55;
- exact `0004` checksum: `cea314ca6fdbb18dd5fea9d3edb1efff2c9d61acee9bec4f68d4048c2e980096`;
- aggregate repository-wide gate at `16f69b9`: PASS, including Store 55/55, FG-094 compatibility, approval lane, and final `OK`.

Retained HeMan evidence:

- sealed mutation bundle: `/tmp/fg027b-final-evidence-82201060-20260825T0020Z`;
- `FINAL.txt` SHA-256: `1e45dfa6bea91c97998c1784e5d22642c3ecf0ea4705e890e31ff624f75fb24f`;
- bundle `MANIFEST.sha256` SHA-256: `6ba415f979163ab4f18f69a8431f282c67a0491903c109e3f05347435a3aa50c`;
- individual authoritative evidence: `/tmp/fg027b-authoritative-8220-20260825T0030Z`, `SHA256SUMS` SHA-256 `0441e04d2fa35de5ea7628820886678e84e9cc3745307b4aad7fc2c256828259`;
- aggregate authoritative evidence: `/tmp/fg027b-aggregate-authoritative-16f69b9-20260825T0045Z`, `SHA256SUMS` SHA-256 `bce1720f16c6895a994a49a4572c2f318613e6be0fc5781f7d3825966017504c`.

The aggregate evidence was frozen with files mode 444, directory mode 500, zero writable entries, the fresh database absent after cleanup, and zero Fogell/dotnet processes.

### FG-044b(d) — stash applies Ant default excludes

Implemented scope:

- stash now uses Ant 1.10.17's exact 28 case-sensitive default excludes by default;
- an explicit `useDefaultExcludes: false` opts out of only those defaults;
- caller `excludes` remain authoritative;
- literal includes do not override defaults;
- malformed values fail before creating or replacing controller-side stash storage;
- seeded same-name stash inventory and bytes survive malformed replacement;
- `allowEmpty`, copy, restore, archive, and JUnit behavior remain bounded to their existing contracts.

Scope boundary: this closes the FG-044b(d) stash residual only. Do not mark parent FG-044b DONE solely from this commit. The local aggregate also contains separate FG-044b(b) and FG-044b(c) candidates, but their publication/status and the FG-044b(c) test-fixture race must be handled honestly and separately.

Commit identities:

- individual signed source commit: `250593b8e80227bb23471786cbb6ef5b4135a46f`;
- individual tree: `998f9c785351ffac46d07264830cd42ed71c5126`;
- individual parent: `16f69b9b9df8f34b164851462dfad75706cf1185`;
- individual worktree: `/home/srikanth/projects/fogell-worktrees/fg-044bd-stash-default-excludes`;
- aggregate signed commit: `78566659860fa6d6c3a7d0b596f6eb0afc6490d7`;
- aggregate tree: `998f9c785351ffac46d07264830cd42ed71c5126`;
- aggregate parent: `16f69b9b9df8f34b164851462dfad75706cf1185`.

Proof state:

- effective mutations: 36/36 credited;
- survivors / compile-invalid / vacuous: 0 / 0 / 0;
- two invalid zero-credit histories retained;
- focused Execution: 1/1;
- focused Differential: 4/4;
- restored Execution: 80/80;
- sequenced Differential: 211/211;
- stock default-parallel gate: failed twice identically in the pre-existing FG-044b(c) shared-environment fixture, described above;
- sequenced composite: PASS, but it is diagnostic/workaround evidence, not the final stock-gate closure.

Retained custodian-Mac evidence:

- sealed mutation bundle: `/private/tmp/fg044bd-mutation-evidence-final-20260824T2040Z`;
- `FINAL` SHA-256: `b74b817e56b55812048a8e229dba7b6ebf7412542502cd16bf5330eee893b0b5`;
- bundle `MANIFEST` SHA-256: `faaeddee4dec10fe587abb0313fd00c789ed276aa3db60794284638e0021c2c9`;
- bundle payload manifest SHA-256 recorded inside `FINAL`: `2e98eafc1f648eb9aa3c81edba0e0ef8b191e87cc26ca63ba523620c18f5f9a2`;
- first stock gate stripped log: `/private/tmp/fg044bd-authoritative-full-gate-20260824T2046Z.stripped.log`, SHA-256 `46f46cb846becac4ca8a6b239bc12f1bddc5e4a8b3913356294a88c2c32c3a98`;
- second stock gate stripped log: `/private/tmp/fg044bd-authoritative-full-gate-rerun-20260824T2051Z.stripped.log`, SHA-256 `36a8aa87da5765de3c99adcceced15ab8152efb74a198dce2e82afba5ed84b88`;
- sequenced composite: `/private/tmp/fg044bd-authoritative-sequenced-composite-20260824T2056Z`;
- sequenced composite `MANIFEST.sha256` SHA-256: `b47c131da9ffaf26bbda14eefddc01d69f75b02e24a0326ae1eaa77bd63e1216`;
- composite stripped log SHA-256: `6b58b5da5f9c9a5bfe3079fa9b74a42ca40045d31327d3c386163c9b771cb2b6`;
- focused Execution stripped log SHA-256: `c5a2b6550233521afaf95d638f1540467fc227ebfa4ea59348c7c36af282df30`;
- focused Differential stripped log SHA-256: `29117a9de70b44d8b792602bb4c61361dec3642a635e9d5e8501747abd9e0dd2`.

The mutation bundle was sealed with directories mode 500, files mode 444, zero writable entries, zero active Fogell processes, and zero mutant scratch directories. `/private/tmp` is ephemeral; preserve it before reboot or cleanup.

## Aggregate history and package count

The aggregate is exactly 36 commits ahead of `origin/main`. These are the aggregate commit subjects in chronological order:

```text
eb53774 FG-221 model JUnit symlink traversal
8007f50 FG-072 prove interpreter sandbox boundary
b5b0f0f FG-190 carry trivia breaks through grouped expressions
c4c9696 FG-180 admit double-quoted constant keys
4679790 build: lock NuGet dependency graphs (FG-074)
cb8638c build: gate release provenance (FG-093)
3221d2e FG-094 gate exact-set compatibility regressions
54a483b FG-117 eliminate stale-reference false positives
fa55d6c FG-116 pin failFast approval replay safety
e9b7f00 FG-118: make timestamp survivor coverage exact
2871369 FG-116/117/118: reconcile local candidates
981f212 FG-027a: add pure retry decision law
6c4e021 FG-115 pin late approval cancellation audit
ef9445d FG-173 compare physical empty workspace leaves
8f39eb6 FG-173 preserve workspace manifest audit identity
6a4e825 FG-027a/115/173: reconcile local candidates
5a271b0 FG-166 carry receipt freshness mapping forward
0b5beec FG-197 replace ordinary process polling with events
8dae314 FG-130a reject fail-fast option arguments
d1e94b9 FG-123a reject ansiColor trailing blocks
340f2f1 FG-207 group step completion durability
4cc3b53 FG-124a decode scripted numeric escapes
fce392c FG-203 preserve live env aliases
84354e2 Regenerate scorecard after FG-203
728d8ce FG-126a reject measured invalid-eight escapes
a278425 FG-064a bind log chunks to their attempt lineage
3281b65 FG-085a prove bounded backup restore fidelity
a15adb4 FG-060a bind build controls to route project
d27616c FG-129 seal compile-refusal equivalence
0f8ef93 fix: bind credential keys at identifier boundaries
f8ce29f feat: render bounded admission source excerpts
5347862 FG-044b(b): preserve credential companion shadows
2029b02 test(parser): seal malformed-input sweep (FG-004b)
feb7996 FG-026 add durable effect checkpoint ledger
16f69b9 FG-027 persist retry decisions
7856665 FG-044 apply Ant default excludes to stash
```

This is a commit count, not a claim that every subject is one independently publishable ticket. The reconciliation and scorecard commits are intentionally visible.

## Current board/document truth

The aggregate's `docs/EXECUTION_BOARD.md` still says:

- FG-026: TODO;
- FG-027: TODO;
- FG-027a: PARTIAL pure-domain foundation;
- FG-044b: TODO, with (d) described as an open residual.

Those rows predate the local Store/stash commits above. Do not simply flip all parents to DONE. Update the board only after reconciling each exact scope:

- FG-026 now has a durable Store ledger and exhaustive Store proof, but any runtime/external-effect driver claim needs separate evidence;
- FG-027a plus FG-027b now cover the pure law and durable Store persistence, but runtime/controller/public-API integration remains unfinished, so parent FG-027 stays open;
- FG-044b(d) is implemented and mutation-proven, but parent FG-044b publication/state must account for the separate (b)/(c) candidates and the current stock-gate fixture race.

The only committed custodian handoff in the repository is `docs/CUSTODIAN_HANDOFF_2026-08-23_FG-221.md`, and it explicitly marks itself as a superseded intake snapshot. Do not use its FG-221 staging checklist as current state.

## Operational constraints on HeMan

- Preserve every listed worktree. Do not reset, recreate, or prune them casually.
- Preserve unrelated dirty worktrees; the aggregate and the three final individual worktrees were clean when inspected.
- Use `rg` / `rg --files` for inspection and `apply_patch` for deliberate source edits.
- Keep mutation installation bounded to the exact production files and restore all pinned candidate files on every exit path.
- Store tests use PostgreSQL in container `fogell-fg060a`, reachable at `127.0.0.1:55445`, user `fogell`.
- Create a fresh uniquely named database per live Store or mutation run. Prove it absent before creation and after `dropdb --force`. Never reuse an evidence database.
- Retain exact migration checksums from the ledger (`0003` and `0004` above).
- Before and after gates, attest branch, HEAD, tree, parent, signature, exact status/name-only paths, file hashes, database absence, and zero relevant Fogell/dotnet processes.
- The private compatibility inputs used during custody were:
  - corpus: `/sn8100/work/exchange/crucible-gate/corpus` (228 Jenkinsfiles);
  - baseline: `/home/srikanth/projects/fogell-baselines/fg-094-candidate/baseline.json`;
  - oracle: `/home/srikanth/projects/ghenghis/chengis/test/resources/jenkins-oracle/Jenkins-RAnvil-Chengis-228-projects.tsv`;
  - stale-reference base: `origin/main`.
- Evidence under `/tmp` on HeMan and `/private/tmp` on the custodian Mac is ephemeral. Verify hashes, then copy it to durable storage before any reboot, cleanup, or host rotation.
- Do not run multiple mutation packages or repository gates concurrently in the same worktree. Shared `bin/obj`, source installs, process environment, and database names make that unsafe.

## Publication boundary

No push, PR, review request, merge, tag, or release was performed during this closure. The next custodian must not infer authority to publish from the existence of signed local commits.

Before any publication:

1. fix the global credential-fixture isolation and pass the stock repository gate on the exact aggregate head;
2. verify the populated final aggregate-gate tuple and its evidence hashes at the top of this document;
3. reconcile board/ticket wording without overstating FG-026, FG-027, or FG-044b parent completion;
4. decide whether the 36-commit aggregate is the intended review unit or whether packages must be published separately;
5. obtain explicit authority to push/open PRs;
6. require exact-head reviews and CI before merge;
7. merge only the reviewed head and verify merge parents afterward.

Welcome to the jungle. Leave the evidence better than you found it.
