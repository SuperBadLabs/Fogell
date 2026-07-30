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
| Declarative files in corpus | 92 (contain `pipeline {`) | — | reference |
| Fogell declarative acceptance | — | **87/93 = 93.5%** | no regression, ever |
| Fogell scripted acceptance | — | **109/135 = 80.7%** | no regression, ever |
| Fogell total accepted | — | **196/228 = 86.0%** | no regression, ever |
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
| FG-002 | P0 | **DONE** | **Differential harness** (`adr/0004`) | **8/8 tier-1 PROVEN** — same terminal result, same normalised output, same real workspace hash on both sides. Contract printed into every receipt. `scripts/run-differential.sh` exits non-zero unless every case is fully proven |
| FG-002b | P0 | **DONE** | Collect Jenkins' workspace so the hash can actually be compared | `Trace.collectRemote` runs a caller-supplied command that hashes the workspace where it lives, through the *same* exclusion filter and manifest form as a local hash. Verified non-vacuous: `shell-and-file` matches on `artifact.txt` content hash `82bbb1ee`, and the workspace hash is not the empty-manifest hash |
| FG-003 | P0 | **DONE** | Corpus gate: verify `CORPUS-SHA256SUMS` before any scoring run; refuse to score on drift | tampering with one corpus byte fails the gate non-zero | — `scripts/verify-corpus.sh`: clean corpus 228/228 exit 0; one appended byte -> exit 1 naming the file
| FG-004 | P0 | **DONE** | Admission bounds: source bytes, node count, nesting depth, scalar length, capped **before** schema compilation | `Limits.precheck` is a single linear scan (no recursion, so it cannot itself overflow). Tests cover empty, 300 KB source, 200-deep nesting, 20 KB scalar, and a 40k-brace bomb — each returns a named code with a position. Full 10k fuzz sweep deferred to FG-004b |
| FG-005 | P1 | **DONE** | Evidence directory convention: per-ticket dir with diff, tests log, tree, `SHA256SUMS` | `scripts/seal-evidence.sh`; `evidence/README.md` records why `base-commit.txt` + `tree.txt` exist (a prior gate baseline bound only a binary hash and silently compared across trees) |
| FG-004b | P1 | TODO | Fuzz sweep: 10k generated malformed inputs through admission + parser | zero crashes, zero unhandled exceptions, every rejection carries a code and position |
| FG-006 | P1 | **DONE** | `Fogell.Domain`: build/node/attempt/status model, worst-of status aggregation | property test: aggregation is associative and commutative | — `Fogell.Domain`: 18 Expecto tests pass. worstOf proven a commutative monoid exhaustively over all 25 pairs and 125 triples; ofMany order-independence property-tested; 7-condition publication guard and retry-never-rewrites covered

## Wave 1 — Front end (Forge-inspired)

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-010 | P0 | **DONE** | `Fogell.Pipeline.Parser` — typed Declarative parser (Forge approach) | **87/93 declarative corpus files accepted (93.5%)**. Criterion corrected: the earlier "194/228 declarative" figure was a mismeasurement — `forge validate` prints `OK` for BOTH paths and my shell classifier matched `OK*` before checking for "scripted". Only **92 of 228** corpus files contain a `pipeline {` block at all (naive regex agrees). 6 remaining failures: 3 `no_stages`, 3 `malformed_syntax` |
| FG-011 | P0 | **DONE** | `Fogell.Groovy` AST + `Fogell.Groovy.Parser` — scripted escape hatch | **109/135 scripted files accepted (80.7%)**. Every construct proven necessary against Forge is present from the start: shebang, `@Library`->`library()`, `import`, trailing commas, slashy strings, `=~`/`==~`, typed closure params, `final`, `x++`, C-style `for`, ranges, `switch`, `instanceof`, spread-dot, safe-nav, multi-assign |
| FG-012 | P0 | **DONE** | Dispatch: declarative vs scripted, stricter than Forge's bare regex | `looksDeclarative` strips comments and string literals before matching. Tests prove `pipeline {` inside a line comment, a block comment, and a string literal all dispatch scripted |
| FG-013 | P0 | **DONE** | `Fogell.Groovy.Interpreter` — bounded, capability-limited evaluation (`adr/0002`) | Sandbox is **structural**: the `Value` type has no case wrapping a host object, so there is nothing to reflect over. Deny-by-default on every call; steps become *requested effects*, never direct actions. Budgets stop infinite loops, huge ranges, unbounded recursion, and catastrophic regex. 30 tests. **The tests found a real hole**: `new File(...)` parsed as a variable and slipped past the gate — constructors now route through `admitCall` |
| FG-014 | P1 | TODO | Close the 14 known Declarative rejections (`FOGELL-DAY1-BACKLOG.md`) via **minimal repros**, never the reported error position | acceptance ≥ 205/228 with zero regressions |
| FG-015 | P1 | TODO | Close the 6 remaining Groovy constructs: nested-quote GString, spread-dot, ranges, `switch`, `instanceof`, multi-assign | each has a passing minimal repro; corpus does not regress |
| FG-016 | P1 | **PARTIAL** | Error reporting: named code + line/column for every rejection | Codes and positions are carried on every `AdmissionError` and asserted in tests. Source *excerpt* rendering still outstanding — retitled FG-016b |
| FG-016b | P2 | TODO | Render a source excerpt with a caret under the offending column for every rejection | golden-output test per error code |
| FG-017 | P2 | TODO | Matrix expansion (`matrix` / `axes`), one corpus file uses it | expanded plan matches Jenkins' stage list for that file |

## Wave 2 — Durable spine (McLoving-inspired)

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-020 | P0 | **DONE** | `Fogell.Store` on PostgreSQL (`adr/0007`): schema + migrations under advisory lock with a checksummed version ledger | 8 concurrent `Migrate()` calls all succeed and leave **exactly one** ledger row. A migration whose text changed after being applied fails loudly rather than diverging silently |
| FG-021 | P0 | **DONE** | Atomic admission: build + node + attempt + event + outbox in one transaction, idempotency-keyed | Replay returns the original ids and emits no second event/outbox row. **16 concurrent submissions of one key yield exactly one build** — arbitrated by a unique constraint, not check-then-insert. Composite FKs make cross-tenant parent substitution unrepresentable |
| FG-022 | P0 | **DONE** | Attempt fences + lease owner + expiry using `clock_timestamp()`, not `now()` | **16 concurrent publishers → exactly 1 winner, exactly 1 terminal event.** Refused: stale fence, wrong owner, expired lease, double publication. Each offer increments the fence |
| FG-023 | P0 | **DONE** | Controller restore epoch: restore invalidates every active lease; pre-restore agents cannot publish | `ActivateRestore()` bumps the epoch and moves live attempts to `reconciliation_required`; a pre-restore holder's publication is refused |
| FG-024 | P0 | **DONE** | Append-only per-step journal with a selectable fsync policy (`adr/0003`) | Append-only so a torn write can only truncate the tail; a partial final line is recovered-up-to rather than trusted or fatal. Measured **on HeMan** (fsync ~0.7 ms): `EveryStep` **0.859 ms/step** (2 syncs — started *and* finished), `EveryStage` **0.005 ms/step**. Cross-host comparison against the luigi Jenkins figure is invalid; the host-independent claim is **~2 syncs/step vs Jenkins' ~6.9** |
| FG-025 | P0 | **DONE** | **Exactly-once resume**: replay from the last durable step | Proven with a **real SIGKILL** of a separate process mid-stage. Marker file shows `step-0` executed **exactly once** across the crash; the interrupted `step-1` is reported for reconciliation and **never re-run** (re-running is at-least-once, which ADR 0003 rejects); the unreached `step-2` runs. Resuming a finished build is a no-op |
| FG-026 | P0 | TODO | Fenced effect checkpoint ledger with `prepared`/`applied`/`confirmed`/`uncertain`; payload digest immutable | payload substitution rejected; uncertain effects listed for reconciliation |
| FG-027 | P1 | TODO | Retry semantics: never rewrite an attempt; one child attempt with immutable link; exhausted budget → dead-letter | replaying the retry decision returns the same child |
| FG-028 | P1 | TODO | Tenant isolation: forced RLS, transaction-local org setting, composite foreign keys | absent context exposes no rows; cross-tenant write rejected |
| FG-029 | P2 | TODO | SQLite single-user mode, explicitly labelled non-production | `KNOWN-LIMITATIONS` states it; startup logs the mode |

## Wave 3 — Execution and process lifecycle

| id | pri | status | item | acceptance |
|---|---|---|---|---|
| FG-030 | P0 | **DONE** | Linux executor: fresh normalized workspace per attempt; reject absolute paths, traversal, symlink components | 6 tests: absolute, traversal, mid-path traversal, symlinked component, outside-root, and reuse all refused with a named error |
| FG-031 | P0 | **DONE** | Process group per step; SIGTERM then grace then SIGKILL (`adr/0005` match) | A `trap ... TERM` handler runs and writes its marker before death (graceful, no escalation). A step that ignores TERM is escalated to SIGKILL with `Escalated = true` |
| FG-032 | P0 | **DONE** | **Reap the step's process group at step end**, with an opt-out (`adr/0005` beat) | A real backgrounded daemon (pid recorded to a file) is dead after **success** and after **timeout**; the opt-out keeps it alive. Leaks are counted and reported in the diagnostic, never ignored |
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
| FG-040 | P0 | **DONE** | `sh` / `bat` with real subprocess, streaming log, exit-code propagation | Exit 0 → Success, exit 7 → Failure with the code named in the diagnostic; stdout/stderr separated; output streams line-by-line during the run; env passed through; unimplemented steps fail closed by name |
| FG-041 | P0 | **PARTIAL** | `echo`, `dir`, `environment` scoping (lexical, stage overrides pipeline) | `echo` and environment scoping proven by differential receipt (`env-scoping`, stage overrides pipeline, shared vars inherited). `dir` nests and auto-creates, refusing traversal. `withEnv` still outstanding — split to FG-041b |
| FG-041b | P1 | TODO | `withEnv(['A=1']) { … }` block-scoped environment | differential receipt |
| FG-042 | P0 | **PARTIAL** | `archiveArtifacts` with ant-glob expansion | Proven by receipt: multi-pattern archive (`out.txt, target/*.jar`) and the empty-match case, which Jenkins **fails** rather than passing quietly. Artifacts land outside the workspace so archiving cannot perturb the compared hash. Retrieval over a stable URL awaits the API — FG-042b |
| FG-043 | P1 | **DONE** | `junit` test-result ingest | Proven by receipt for passing and failing reports. Failing tests → **UNSTABLE**, not FAILURE (the build worked, the code did not) — matching Jenkins. A malformed report is reported, never silently counted as zero |
| FG-042b | P2 | TODO | Artifact retrieval at a stable URL, byte-exact | requires the controller API (FG-060) |
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
| FG-060 | P0 | **DONE** | `Fogell.Controller.Api`: submit / status / logs / cancel / explain; bearer ≥32 bytes; deny by default | A weak token is refused **at startup**, not per request; comparison is **fixed-time**. All five routes return 401 unauthenticated. Malformed pipeline → 422 with code **and** source position. Submit 201 fresh / 200 replay. Non-UUID → 400, unknown build → 404, never 500. 15 tests |
| FG-061 | P0 | **DONE** | Scheduler: capability-filtered claim, FIFO within pool, wait diagnostics | Tenant advisory lock + `FOR UPDATE SKIP LOCKED`: **16 concurrent schedulers over 4 attempts yield exactly 4 claims, none twice**. Capability containment checked in SQL (`<@`), not application code. FIFO by admission order. Wait diagnostics distinguish empty queue / trust-pool mismatch / **named missing capabilities** |
| FG-062 | P1 | TODO | `Fogell.Agent.Protocol` + `Runtime` (`adr/0008`): mTLS, versioned, agent-local durable log, offset recovery | **network partition ≥ 45 s costs zero log lines** — the Jenkins parity bar |
| FG-063 | P1 | TODO | Agent death handling: announce reconnect grace with a budget, then ABORTED with a named cause | matches Jenkins' behavior, which is exemplary here |
| FG-064 | P1 | **DONE** | Progressive console over the API (`adr/0005` match) | `GET …/logs?from=N` returns chunks plus a `next_sequence` cursor, so a client tails a running build rather than waiting for completion |
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
