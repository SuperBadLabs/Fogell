# Fogell successor custody update — 2026-08-25

Status: **the prior stock-gate blocker is fixed, the exact signed source head passes the unmodified authoritative gate, and nothing is published**.

This update succeeds `CUSTODIAN_HANDOFF_2026-08-25.md`. It records local state only; it is not a push, pull request, merge, release, or parent-ticket completion claim.

## Exact source candidate

- host: `heman`;
- worktree: `/home/srikanth/projects/fogell-worktrees/custodian-fg166-197-130-123`;
- branch: `agent/custodian-fg166-197-130-123`;
- gated source HEAD: `4f8f381e5815cab618df5d9991fe3669f39dbfcc`;
- gated source tree: `3f60aae5ae11e9fefd2c7e09035c866ab1e15226`;
- relation to `origin/main`: 40 local commits ahead, zero behind;
- signature: good ED25519 signature, key `SHA256:6cTB2VnhVlZd0WqZSzWP6UsYjYewpNL20zho8M7R1tY`;
- worktree state before and after the exact-head gate: clean;
- remote publication: no remote branch contains this head; nothing was pushed, opened, or merged.

The three signed successor commits are:

```text
fdb65878aa8139325bbe9fd840c0ad562d31a038 fix: isolate differential credential fixtures
918cfd581b1ed3b2a76138ebadbbc6f9294f1fd0 test: preserve credential store decoding coverage
4f8f381e5815cab618df5d9991fe3669f39dbfcc build: fail when a test suite silently skips
```

All three signatures verified with the key above.

## Closed blocker

The previous handoff's highest-priority action is complete.

`FG-044b(b)` and `FG-044b(c)` no longer rewrite process-global `FOGELL_CREDENTIALS` or `FOGELL_CREDENTIALS_FILE`. `FogellSide.runWithCredentials` accepts an immutable run-scoped credential map for hermetic callers, while every existing production entrypoint retains the environment-backed provider and its per-`withCredentials` evaluation point.

The credential wire decoder was mechanically extracted as `credentialStoreFromSpec` and is now covered without global state. Its tests pin:

- text values containing delimiter-like characters;
- byte-exact non-UTF-8 file values;
- username/password values;
- comments and CRLF input;
- null and empty sources;
- malformed base64, row shape, type, and user/password payloads failing closed.

The default-parallel Differential suite passed ten consecutive exact-source-head stress runs at 213/213, and the final authoritative gate also passed Differential 213/213. The old `--sequenced` workaround is no longer used or needed.

## Gate integrity improvement

`scripts/build-and-test.sh` now requires every zero-exit test project to emit an Expecto summary. Before this change, both database-backed projects returned zero and emitted no summary when PostgreSQL was unavailable, so a local green test phase could silently omit Controller API and Store coverage.

The negative control forced PostgreSQL to an unavailable port and reproduced the old state: Controller returned zero, emitted `skipped: no PostgreSQL`, and emitted no `EXPECTO!` summary. The new gate rejects that state with a project-named diagnostic. Independent review confirmed the same boundary for Store. The final authoritative run supplied live PostgreSQL and proved both suites actually ran.

## Exact-head authoritative gate

The unmodified `./scripts/build-and-test.sh` passed on gated source HEAD `4f8f381e5815cab618df5d9991fe3669f39dbfcc` with:

- live PostgreSQL at the retained `fogell-fg060a` container;
- the pinned 228-file corpus;
- the external FG-094 baseline;
- the pinned Jenkins oracle.

Results:

```text
Controller.Api  18/18
Differential   213/213
Domain          34/34
Execution       80/80
Groovy         224/224
Journal         31/31
Parser         120/120
Store            55/55
Total          775/775
FG-207 focused    8/8
FG-094 files    228/228, accepted set unchanged, tier-1 set unchanged
restart lane     PASS
approval lane    PASS
terminal         OK
```

Retained HeMan evidence:

```text
directory: /tmp/fogell-custodian-4f8f381-gate
before.txt:   cabdd6c0b1713e39524b69eae19ea397a8ceb70d00be9bb75b14e31e12ddf276
full-gate.log: 7be7c04e8001d6f9df30867248ccd5e05ba132c4cfdc4576ca43d0859142d417
after.txt:    bb6689e561eb7f1aac915c6e01ae30d875373abc21db3d05798c1d9f0187293b
```

`/tmp` is ephemeral. Preserve this directory in durable storage before reboot or cleanup if the exact log is required for publication review.

Focused exact-source-head evidence is retained separately:

```text
directory: /tmp/fogell-custodian-4f8f381-focused
default-parallel-stress.log:  464b4851eb631dd9e0ba17175f5a004722b8396ab093877b0661cb18a64f743b
db-skip-negative-control.log: 6fd67d2e99aad8bf2b7a27dbe9db988d3ae1db9199ee59a614146642994099ef
```

The first log records all ten 213/213 default-parallel runs. The second records the zero-exit/no-summary PostgreSQL skip and the new gate's reject verdict.

## Next highest-risk work

Do not cherry-pick local FG-222 commit `fa6d672` unchanged. It correctly targets ambient controller-environment exposure, but a successor audit found that its PATH-only baseline invalidates existing HOME-dependent differential receipts and may break private SCM configuration. Its old Differential test also mutates the same process-global credential variables removed here.

The next custodian should manually port FG-222 onto this aggregate, choose and measure a small explicit build-visible metadata allowlist, and keep any controller-only pre-Jenkinsfile SCM-fetch configuration separate so HOME, proxy, CA, SSH-agent, or credential authority required for private fetches never becomes a general shell/Git baseline. Preserve FG-197's event-driven process implementation and the credential-injection seam, and remove global credential mutation from the old test.

Before making compatibility claims, run the full pinned Jenkins differential suite, not only focused environment cases or the stock gate. At minimum remeasure `env-inherited-output-fold`, `parallel-inherited-env-fold`, `xtrace-continuation-inherited`, PATH/withEnv, credentials, checkout, and Git-step cases. Also require default-parallel Differential unit execution and a dynamic SCM-launch assertion; the old FG-222 proof checks WalkerGit only through a source-string guard.

Other material residuals remain:

- no forced PostgreSQL RLS / transaction-local tenant context;
- shell and Git workloads still inherit agent filesystem/network authority;
- no production Controller API host, scoped authorization, health endpoint, or request-size boundary; log reads and responses are also unbounded;
- terminal attempt publication does not advance the corresponding node/build status, so API snapshots can remain queued;
- `scripts/seal-evidence.sh` can still emit a checksum-valid bundle after failed corpus/build/test commands;
- credential files are chmodded after creation and cleanup failures are swallowed;
- rollback, reboot reconciliation, saturation, soak, log retention/compaction, monitoring, and deployment runbooks remain incomplete;
- NuGet graphs are locked, but the SDK feature roll-forward, hosted runner, CI actions, and PostgreSQL image remain mutable and no SBOM/vulnerability gate is present.

No apparent live credentials or private-key artifacts were found in the aggregate tree or successor commit range; fake credential fixtures remain intentionally present.

## Publication boundary

No push, PR, merge, tag, release, deployment, or external message was performed. Publication still requires explicit authority, durable preservation of the evidence, exact-head review, hosted checks, and verification that only the reviewed head is merged.
