---
title: Fogell Execution Board
audience: mixed
category: engineering-board
purpose: Command board for taking Fogell from an empty F# workspace to a private prerelease with differential Jenkins evidence.
lifecycle: live
last-verified: 2026-07-30
---

# Fogell — Execution Board

Mission: **a faster and better Jenkins.** Near-100% compatibility is the goal;
`adr/0001` defines what "compatible" is permitted to mean, and `adr/0004`
forbids claiming it without a receipt.

Lineage: front end inspired by **Forge** (same Groovy-parser approach,
`adr/0002`); architecture and evidence discipline inspired by **McLoving**
(`adr/0007`, `adr/0008`). One primary language, F# (`adr/0006`).

## Operating contract

- Repository: private, HeMan `~/projects/fogell`.
- One coherent commit per ticket. No work on a dirty checkout.
- Every ticket states a falsifiable acceptance criterion. "Done" means the
  criterion was measured, not that code was written.
- Corpus manifest verified before any scoring run (`corpus/README.md`).
- No push, tag, release or publication without owner authorization.
- Imported Jenkinsfiles are untrusted. Parsing is unconditional; **execution is
  allowlisted by executed surface**, on a no-egress network, in a disposable
  workspace.
- Incomplete pattern matches fail the build (FS0025/FS0026).
- No scalar compatibility percentage is ever published.

## Baseline scorecard

Measured on luigi against Jenkins 2.568.1 and the pinned 228-file corpus. Full
derivation in `architecture/BASELINE.md`.

| Signal | Jenkins oracle | Fogell today | End-state gate |
|---|---:|---:|---|
| Corpus files | 228 | — | manifest unchanged, 228/228 |
| Declarative-valid | 80 | — | per-file disposition recorded |
| Compiled / CPS entry | 199 | — | reference only |
| **Reached agent scheduling** | **119** | — | the only scoring denominator |
| Front-end acceptance (inherited) | — | 214/228 = 93.9% | no regression, ever |
| Known front-end rejections | — | 14 | each closed or reasoned |
| **Execution parity proven** | — | **0** | grows only with sealed receipts |
| Per-step cost (durable) | 54.13 ms | — | < 8 ms at equal guarantees |
| Idle RSS | 1,571 MB | — | < 150 MB |
| Steps per stage | 251 = compile error | — | no ceiling |

---

## Wave 0 — Foundation and evidence

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-000 | P0 | **DONE** | Solution skeleton: `Fogell.slnx`, project graph, `Directory.Build.props` with FS0025/FS0026 as errors | `dotnet build` clean, zero warnings, on HeMan and luigi | — `dotnet build -c Release` clean, 0 warnings, 0 errors; `Fogell.slnx` + `Fogell.Domain` + tests wired
| FG-001 | P0 | **DONE** | Pin toolchain: `global.json`, `rust-toolchain`-equivalent note, CI-reproducible SDK version | two hosts produce byte-identical build output for `Fogell.Domain` | — `global.json` pins SDK 10.0.100 rollForward latestFeature; built on HeMan SDK 10.0.301 and luigi 10.0.110
| FG-002 | P0 | TODO | **Differential harness** (`adr/0004`): pinned Jenkins image digest + Fogell, same host, compare terminal result, ordered step sequence, canonical workspace hash | one file passes end-to-end and emits a sealed receipt with both sides' hashes |
| FG-003 | P0 | **DONE** | Corpus gate: verify `CORPUS-SHA256SUMS` before any scoring run; refuse to score on drift | tampering with one corpus byte fails the gate non-zero | — `scripts/verify-corpus.sh`: clean corpus 228/228 exit 0; one appended byte -> exit 1 naming the file
| FG-004 | P0 | TODO | Admission bounds: source bytes, node count, nesting depth, scalar length, collection sizes, capped **before** schema compilation | fuzz 10k malformed inputs: zero crashes, zero stack overflows, every rejection named |
| FG-005 | P1 | TODO | Evidence directory convention: per-ticket dir with diff, tests log, tree, `SHA256SUMS` | a ticket's receipt verifies standalone |
| FG-006 | P1 | **DONE** | `Fogell.Domain`: build/node/attempt/status model, worst-of status aggregation | property test: aggregation is associative and commutative | — `Fogell.Domain`: 18 Expecto tests pass. worstOf proven a commutative monoid exhaustively over all 25 pairs and 125 triples; ofMany order-independence property-tested; 7-condition publication guard and retry-never-rewrites covered

## Wave 1 — Front end (Forge-inspired)

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-010 | P0 | TODO | `Fogell.Pipeline.Parser` — typed Declarative parser (Forge approach, 995-line reference) | 194/228 corpus files accepted, matching the inherited baseline |
| FG-011 | P0 | TODO | `Fogell.Groovy` AST + `Fogell.Groovy.Parser` — scripted escape hatch | 20/228 scripted files accepted; my 216-line patch set reapplied |
| FG-012 | P0 | TODO | Dispatch: declarative vs scripted. Forge uses one regex on `pipeline\s*\{` — must not misfire on the token inside a string or comment | probe suite: `pipeline {` in a comment/string dispatches scripted, not declarative |
| FG-013 | P0 | TODO | `Fogell.Groovy.Interpreter` — bounded evaluation (`adr/0002`), capability-limited, no arbitrary reflection or file/network access | untrusted-input probe: `new File('/etc/passwd')` and reflection are rejected with a named code |
| FG-014 | P1 | TODO | Close the 14 known Declarative rejections (`FOGELL-DAY1-BACKLOG.md`) via **minimal repros**, never the reported error position | acceptance ≥ 205/228 with zero regressions |
| FG-015 | P1 | TODO | Close the 6 remaining Groovy constructs: nested-quote GString, spread-dot, ranges, `switch`, `instanceof`, multi-assign | each has a passing minimal repro; corpus does not regress |
| FG-016 | P1 | TODO | Error reporting: named code + line/column + source excerpt for every rejection | every rejection path has a test asserting code and position |
| FG-017 | P2 | TODO | Matrix expansion (`matrix` / `axes`), one corpus file uses it | expanded plan matches Jenkins' stage list for that file |

## Wave 2 — Durable spine (McLoving-inspired)

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-020 | P0 | TODO | `Fogell.Store` on PostgreSQL (`adr/0007`): schema + migrations under advisory lock with a version ledger | concurrent controller start installs each migration exactly once |
| FG-021 | P0 | TODO | Atomic admission: build + node + attempt + event + outbox in one transaction, idempotency-keyed | replaying a key returns original ids, emits no second event |
| FG-022 | P0 | TODO | Attempt fences + lease owner + expiry using `clock_timestamp()`, not `now()` | 16 concurrent terminal publishers → exactly one winner |
| FG-023 | P0 | TODO | Controller restore epoch: restore invalidates every active lease; pre-restore agents cannot publish | test proves stale publisher rejected after restore |
| FG-024 | P0 | TODO | Per-step journal, fsync batched at stage boundaries (`adr/0003`) | durable per-step cost < 8 ms on luigi-class storage; number published only from the durable path |
| FG-025 | P0 | TODO | **Exactly-once resume**: replay from last durable step, not stage start | SIGKILL mid-stage; resume does not re-execute a completed step; a marker file proves single execution |
| FG-026 | P0 | TODO | Fenced effect checkpoint ledger with `prepared`/`applied`/`confirmed`/`uncertain`; payload digest immutable | payload substitution rejected; uncertain effects listed for reconciliation |
| FG-027 | P1 | TODO | Retry semantics: never rewrite an attempt; one child attempt with immutable link; exhausted budget → dead-letter | replaying the retry decision returns the same child |
| FG-028 | P1 | TODO | Tenant isolation: forced RLS, transaction-local org setting, composite foreign keys | absent context exposes no rows; cross-tenant write rejected |
| FG-029 | P2 | TODO | SQLite single-user mode, explicitly labelled non-production | `KNOWN-LIMITATIONS` states it; startup logs the mode |

## Wave 3 — Execution and process lifecycle

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-030 | P0 | TODO | Linux executor: fresh normalized workspace per attempt; reject absolute paths, traversal, symlink components | path-abuse probe suite all rejected |
| FG-031 | P0 | TODO | Process group per step; SIGTERM then grace then SIGKILL (`adr/0005` match) | a `trap ... TERM` handler runs and its output reaches the log |
| FG-032 | P0 | TODO | **Reap the step's process group at step end**, with an opt-out (`adr/0005` beat) | `nohup`'d child is dead after success *and* after abort; opt-out keeps it |
| FG-033 | P0 | TODO | **Dead-process detection in seconds**, diagnostic naming the restart (`adr/0005` beat) | kill the step process: terminal verdict < 10 s with a message stating the cause |
| FG-034 | P1 | TODO | `timeout` and cancellation semantics identical to Jenkins' interrupt contract | differential receipt vs Jenkins on a trapped-SIGTERM pipeline |
| FG-035 | P1 | TODO | `retry(N)` = N total attempts, no backoff (`adr/0005` match) | differential receipt: attempt count matches Jenkins |
| FG-036 | P1 | TODO | `parallel`: siblings finish by default; `failFast` interrupts them | differential receipt for both modes |
| FG-037 | P1 | TODO | **No steps-per-stage ceiling** (`adr/0005` beat) | 400-step stage runs; Jenkins fails at 251; receipt records both |
| FG-038 | P2 | TODO | Windows executor via Job Objects, P/Invoke confined to one project (`adr/0006`) | platform-parity suite; requires a Windows host — blocked until one exists |

## Wave 4 — Step coverage by measured demand

Ranked by corpus set-cover among the 119 Jenkins-ready files, not popularity.

| id | pri | status | item | demand |
|---|---|---|---|---:|
| FG-040 | P0 | TODO | `sh` / `bat` with real subprocess, streaming log, exit-code propagation | universal |
| FG-041 | P0 | TODO | `echo`, `dir`, `withEnv`, `environment` scoping (lexical, stage overrides pipeline) | 35 / 15 / 11 |
| FG-042 | P0 | TODO | `archiveArtifacts` + byte-exact retrieval at a stable URL | 34 files |
| FG-043 | P1 | TODO | `junit` test-result ingest and model | 26 files |
| FG-044 | P1 | TODO | `credentials` / `withCredentials`, scope-limited bindings | 22 files |
| FG-045 | P1 | TODO | `timeout`, `retry` (see FG-034/035) | 21 / 6 |
| FG-046 | P1 | TODO | `input` with **durable** approval state across restart (`adr/0005` match) | 18 files |
| FG-047 | P1 | TODO | `stash` / `unstash`, controller-side storage surviving `deleteDir()` | 7 files |
| FG-048 | P1 | TODO | `when` conditions incl. `expression` via the bounded interpreter | 26 files |
| FG-049 | P1 | TODO | `post` blocks: correct *selection*, not just presence — Jenkins prints all branches in the console | 32 files |
| FG-050 | P2 | TODO | `agent { label }` / `agent { docker }` and capability matching | 19 / 9 |
| FG-051 | P2 | TODO | `tools` registry with auto-provision (7 corpus files failed *Jenkins* on this) | 7 files |
| FG-052 | P2 | TODO | `checkout scm`, `git` step | 15 files |
| FG-053 | P3 | TODO | `parameters`, `triggers`, `options` | 12 / 6 / 25 |

## Wave 5 — Controller, API, agents

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-060 | P0 | TODO | `Fogell.Controller.Api`: submit / status / logs / cancel / explain; bearer token ≥ 32 bytes; deny by default | authz matrix test; unauthenticated request returns 401, never data |
| FG-061 | P0 | TODO | Scheduler: capability-filtered claim, FIFO within pool, wait diagnostics distinguishing empty queue from capability mismatch | mismatch reports the missing capability set |
| FG-062 | P1 | TODO | `Fogell.Agent.Protocol` + `Runtime` (`adr/0008`): mTLS, versioned, agent-local durable log, offset recovery | **network partition ≥ 45 s costs zero log lines** — the Jenkins parity bar |
| FG-063 | P1 | TODO | Agent death handling: announce reconnect grace with a budget, then ABORTED with a named cause | matches Jenkins' behavior, which is exemplary here |
| FG-064 | P1 | TODO | Progressive console over the API (`adr/0005` match) | partial output served mid-run; line count grows between two polls |
| FG-065 | P2 | TODO | Jenkins-shaped REST compatibility shim, **off by default**, explicitly bounded | enabled only via env var; limitations documented |
| FG-066 | P2 | TODO | `Fogell.Cli`: validate / plan / run / build / resume / list / history | each subcommand has a golden-output test |
| FG-067 | P3 | TODO | `Fogell.Web` read-only build UI | renders a build's stages and log |

## Wave 6 — Security

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-070 | P0 | TODO | Secrets never placed in the child environment where a transformation can leak them; broker or fd delivery | `echo $TOK \| rev` cannot print the secret |
| FG-071 | P0 | TODO | Masking is defence-in-depth, documented as defeatable, and **never silent** (`adr/0005` beat) | a masking miss emits a warning; Jenkins emits none |
| FG-072 | P0 | TODO | Interpreter sandbox: no reflection, no file/network/process access except through steps | escape-attempt suite fully rejected with named codes |
| FG-073 | P1 | TODO | Threat model document: untrusted Jenkinsfile, hostile agent, multi-tenant boundary | reviewed and committed |
| FG-074 | P1 | TODO | Dependency audit + pinned lockfile; no unpinned transitive fetch at build time | offline build succeeds from a warm cache |

## Wave 7 — Operations

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-080 | P1 | TODO | Reproducible packaging: single self-contained artifact + image, digest recorded | two builds of the same commit produce the same digest |
| FG-081 | P1 | TODO | Migration + rollback rehearsal: forward, back, forward; logical DB hash identical | zero foreign-key violations at each phase |
| FG-082 | P1 | TODO | Crash / kill / reboot campaign: running + queued builds reconcile, no escaped child | integrity check clean; one recovery event per build |
| FG-083 | P2 | TODO | Saturation: bounded queue, explicit HTTP 503 with `Retry-After`, no unbounded executor | N-trigger lanes admit exactly the configured capacity |
| FG-084 | P2 | TODO | Soak: ≥ 10 min sustained, memory and FD bounded, zero restarts | RSS ceiling and FD range recorded |
| FG-085 | P2 | TODO | Backup / restore with byte-verified restore | restored DB hash matches source |

## Wave 8 — Release gates

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-090 | P0 | TODO | Compatibility scorecard generator: per-file tiers from `adr/0001`, machine-readable ledger, **no scalar percentage** | scorecard + ledger, each hashed |
| FG-091 | P0 | TODO | `KNOWN-LIMITATIONS.md` generated from the ledger, not hand-written | every tier-3 rejection appears with its named code |
| FG-092 | P0 | TODO | Differential receipt count is the headline metric | tier-1 file count published with receipts attached |
| FG-093 | P1 | TODO | Provenance tuple binds commit + tree + artifact digest + corpus manifest; gate refuses to run on mismatch | mismatch fails closed (learned from a misnamed baseline that silently compared across trees) |
| FG-094 | P1 | TODO | Regression gate: front-end acceptance and tier-1 count may never decrease; a fail-closed fix that lowers raw acceptance is a PASS when the file is Jenkins-negative | `vinsguru`-class case: engine rejects what Jenkins rejects → gate passes |

---

## Standing risks

1. **Acceptance ≠ compatibility.** 93.9% inherited acceptance against 0 proven
   receipts. FG-002 exists because of this and blocks the release lane.
2. **Interpreter sandbox.** ADR 0002 buys coverage and inherits Jenkins' script
   security problem. FG-013 and FG-072 are the mitigation and they are P0.
3. **Windows is claimed nowhere yet.** FG-038 is blocked on hardware; until then
   the charter must not promise platform parity.
4. **Untrusted corpus execution.** Parsing is safe; running is not. The
   allowlist-by-executed-surface rule is not optional.
5. **Non-durable numbers.** Forge's ~0 ms/step is the non-durable path. Any
   published latency must come from the durable path (FG-024).
