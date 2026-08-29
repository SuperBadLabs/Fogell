---
title: Fogell vs McLoving — capability and performance, Jenkins as touchstone
audience: internal
category: engineering-comparison
purpose: Steering document. What each engine does, what is PROVEN about it, and where they overlap.
lifecycle: live
last-verified: 2026-08-17
---

# Fogell vs McLoving, with Jenkins as the touchstone

> **HAND-MAINTAINED, NOT GENERATED.** `docs/COMPATIBILITY-SCORECARD.md` is generated and is the
> authority for every Fogell corpus number; figures here are quoted from it, never recomputed.
> Numeric tokens use the `tier1=` form so `scripts/bin/audit-board-numbers` re-derives them.
>
> **REVISION 3, 2026-08-17.** Every `[D]` doc-cited cell was re-verified against CODE, with
> architecture docs disallowed as evidence. **Eight confirmed and are now `[S]` with `file:line`
> citations. Three were wrong. One stays `[D]`** — the shared-library case count, which this
> revision first upgraded on no evidence and review sent back. Of the three that were wrong, one
> matters more than any capability row here:
>
> - **`synchronous_commit=on` does not exist.** The string appears NOWHERE in McLoving's
>   repository. Revision 2 stated that a 201 response implies a fsynced WAL, citing a doc. There
>   is no such setting in code. The cell is now `[U]`. **This is the load-bearing claim under
>   McLoving's durability story and under §7's argument that Fogell's speed is a
>   no-durability artifact** — that argument still stands on Fogell's side, which genuinely
>   does not journal, but McLoving's half of it is now unproven rather than doc-backed.
> - **The dynamic provisioner claim was backwards.** Revision 2 said no production provider is
>   claimed; there is a real outbound HTTPS client with a pinned CA.
> - **Windows has no installer.** The binary IS a Windows service host, but nothing in code
>   registers it — the parity doc's install/start/stop is not implemented where it was looked for.
>
> Two confirmations also came back stronger than stated: triggers are FIVE kinds, not two, and
> lease-expiry requeue PRESERVES the fence rather than incrementing it.
>
> **REVISION 2, 2026-08-17.** Revision 1's McLoving column was built from a benchmark runbook
> that covered roughly half the system, and **was wrong in both directions**. Ten cells are
> corrected below, each marked **`[WAS: …]`** rather than silently edited. Source for the
> corrections: [MCLOVING-SOURCE-SURVEY.md](MCLOVING-SOURCE-SURVEY.md).

## How to read this

Every cell carries its evidence basis, because the engines are not measurable on the same
terms and a bare checkmark hides that.

| basis | means |
|---|---|
| **R** | **Receipted** — a differential receipt against a real Jenkins names it. |
| **B** | **Benchmarked** — measured on the trifecta bench. For McLoving, against a hand-authored twin. |
| **S** | **Source-cited** — read from the code, with a path. |
| **D** | **Doc-cited** — an architecture doc in the engine's own repo. Not executed, not read in code. **REVISION 3 DID NOT ELIMINATE THIS BASIS, THOUGH IT CLAIMED TO.** All twelve `[D]` cells were checked against code: eight confirmed and became `[S]`, three were wrong, and one — the shared-library case count — was upgraded with no code path and reverted here after review. |
| **U** | **Unmeasured** — believed, nobody has run it. |
| **N/A** | The row cannot be asked of this column. |

`N/A` is not "absent". **No cell is blank** — a blank reads as "no", and an absence read as a
result is the failure this document has already made once.

**No score, no total, no winner column** — the reason `adr/0001` forbids a scalar compatibility
percentage. A total across incommensurable rows invents a comparison nobody performed.

**Nothing in the McLoving column was executed by us.** Its strongest basis is `S`. Its own
receipts are recorded where they exist and attributed to McLoving's evidence, not ours.

---

## 1. Jenkins evidence — the row that reorders everything

| | Jenkins | Fogell | McLoving |
|---|---|---|---|
| Differential vs real Jenkins | reference | 181 hand-written cases proven `[R]` (163 at this matrix's revision 3; the count is the scorecard's, restated here BY HAND — see the basis note) | **1 corpus case certified equivalent** `[S]` — `crates/jenkins-differential`, Jenkins 2.568.1, 90-plugin manifest, sealed 35-file envelope **[WAS: "never compared at all" — false]** |
| Corpus files proven | n/a | **`tier1=0` of 228** `[R]` — generated scorecard | **1 of 228** `[S]` — `JENKINS_NATIVE_DIFFERENTIAL_V1.md` |
| Population of the evidence | n/a | hand-written, authored for this engine | a real third-party corpus job |
| Wider differential programme | n/a | none beyond the case suite `[S]` | DIFF-002 state/policy (20 scenarios), DIFF-003 boundaries (13), MIG-006 aggregate `[S]` |

**On the denominator both engines share, McLoving has proven one more corpus file than Fogell
has.** Fogell's case count (181 as of 2026-08-17) is the larger number and the weaker claim: it is a population it wrote for
itself. This is the single most consequential correction in revision 2.

## 2. Input language

| | Jenkins | Fogell | McLoving |
|---|---|---|---|
| Accepts a Jenkinsfile at runtime | yes | yes — declarative + scripted `[R]` | **no** `[S]` — no controller crate depends on the compiler |
| Jenkinsfile compiler exists | n/a | n/a — it IS the engine | **yes, standalone** `[S]` — Clojure/Groovy worker, `compat/jenkins-worker/`, rootless Podman **[WAS: "no front end" — overstated]** |
| Groovy evaluated | yes | bounded interpreter `[R]` | **never** `[S]` — `compat/jenkins-worker/src/mcloving/compat/compiler.clj:133` stops at `CompilePhase/CONVERSION`; zero `GroovyShell`/`evaluate`/`GroovyClassLoader` hits |
| Admitted Jenkinsfile syntax | all | declarative + scripted, `admitted=168` of 228 `[R]` | **`pipeline`, `agent any`, `stages`, literal names, `steps`, literal `sh`** `[S]` |
| Step mapping catalogue | plugins | 14 modelled steps `[S]` | **exactly 1 mapping** `[S]` — literal `sh` → `/bin/sh -xe -c` |
| Shared libraries | executed | not supported `[S]` | inventoried, **zero executable cases** `[D]` — `JENKINS_SHARED_LIBRARY_ADMISSION_V1.md`. **STILL DOC-CITED: REVISION 3 UPGRADED THIS TO `[S]` WITHOUT A CODE PATH AND THAT WAS WRONG.** The code check covered whether Groovy is evaluated, not how many cases are executable; the count has no code citation. Caught in review on PR #92 |
| Authoring format | Groovy | Groovy | strict YAML IR `[S]` — `pipeline-ir` |
| Corpus rejected | n/a | `tier3=59` of 228 `[R]` | `N/A` — corpus is Groovy |

**The compiler is real and it is one job wide.** Admitted surface is
`corpus-052-cinqict_jenkinsdev`; policy is `unknown_step: unsupported`.

## 3. Pipeline surface

| | Jenkins | Fogell | McLoving |
|---|---|---|---|
| Sequential stages | yes | yes `[R]` | yes, `MAX_STAGES=128` `[S]` |
| Steps per stage | **250 succeeds; 251 fails before a workspace effect** `[R]` | **400 succeeds with every ordered step executed** `[R]`; no configured ceiling in the list-backed implementation `[S]` — [`FG-037`](tickets/FG-037.md) | **1 — but enforced at EXECUTION, not admission** `[S]` **[WAS: "enforced" — imprecise]** |
| Parallel branches | yes | yes `[R]` | not via the user surface `[S]`; **store supports join nodes and fan-out** `[S]` **[WAS: "not expressible" — true only of the front end]** |
| `matrix` / `axes` | yes | absent `[S]` — `FG-017` | **store has `MAX_MATRIX_AXES`/`MAX_MATRIX_CELLS`**, no YAML surface `[S]` **[WAS: `N/A`]** |
| Conditional stages | yes | yes `[R]`, `FG-175` DONE | `Succeeded`/`Completed` edge conditions in the store `[S]` |
| `script { }` blocks | yes | yes `[R]` | `N/A` |
| DAG shape | arbitrary | follows the Jenkinsfile `[R]` | **general DAG in the store; linear chain is a COMPILER choice** `[S]` — `controller-store/src/dag.rs` **[WAS: "linear chain only"]** |
| Step types | plugins | 14 `[S]` | **1** — `Step::Process` only `[S]`; modes `Direct\|WindowsCmd\|PowerShell` |

**A latent defect worth naming.** A two-step stage passes `validate`, passes `plan`, is accepted
at submit — and fails only when an agent claims it (`execution-spine/src/lib.rs:150`). In a
system designed around fail-closed admission, admission is accepting something unexecutable.

## 4. Platform — where revision 1 was most wrong

| | Jenkins | Fogell | McLoving |
|---|---|---|---|
| Windows execution | yes | **not supported, stated plainly** `[S]` — `FG-113` | **yes — real Win32 spawn** `[S]` — `agent-runtime/src/executor/windows.rs:113`, `cmd.exe` `:358`, `powershell.exe` `:366`, `windows-job/src/lib.rs:258` `CreateProcessW` with `JOB_LIST`. A full peer of `executor/unix.rs`. Runs as a Windows service host (`bins/agent/src/main.rs:164`) but **NOTHING IN CODE INSTALLS IT** — no `CreateServiceW`/`OpenSCManager`/`sc.exe` anywhere **[WAS: "linux only" — false; then doc-cited via AGENT_RUNTIME.md, now code-cited]** |
| SCM checkout | yes | `checkout`/`git` steps `[R]` | **git acquisition, pinned commits, submodule allowlists** `[S]` — `source-acquirer` **[WAS: absent]** |
| Secrets / credentials | credentials plugin | masking `[R]`; same-UID open `[S]` — `FG-070b` | **grant/redeem broker, 15-min TTL, arg/env/file/stdin, zeroized** `[S]` — `secret-broker` **[WAS: "token length only"]** |
| Triggers / webhooks | yes | absent `[S]` — `FG-053c` | **FIVE kinds** `[S]` — `controller-store/src/trigger_ingress.rs:18-24`: `ScmWebhook, Schedule, Upstream, RemoteApi, Plugin`; dispatch `controller-api/src/lib.rs:3726` **[WAS: `U`; then "scm_webhook + timer" — undercounted the enum]** |
| Dynamic agent provisioning | cloud plugins | absent `[S]` | **real outbound HTTPS provider client** `[S]` — `provisioner/src/lib.rs:2677` `provider_create`, `:2803` `.send()`, `reqwest` with pinned CA `:850`, HTTPS enforced `:3899`. Bespoke `mcloving.provisioner.v1` protocol to one endpoint; no cloud SDK, no `trait` abstraction **[WAS: absent; then "no production provider claimed" — REFUTED, that was backwards]** |
| Build caching | plugins | absent `[U]` | **`CacheKind::{Dependency, Build}`, tenant-isolated** `[S]` **[WAS: absent]** |
| Dependency resolution | plugins | absent `[U]` | **maven/npm/pypi → signed canonical plan** `[S]` — `dependency-resolver` **[WAS: absent]** |
| Release provenance / SBOM | plugins | absent `[S]` — `FG-080` open | **signed releases, SBOM, deployment receipts** `[S]` **[WAS: absent]** |
| External effects | plugins | absent `[U]` | signed single-action connector + shadow replayer `[S]` **[WAS: absent]** |

## 5. Execution, durability, security

| | Jenkins | Fogell | McLoving |
|---|---|---|---|
| Topology | controller + agents | one cold process per build `[B]` | embedded worker **or** mTLS agents `[S]` |
| Concurrency | executors | parallel branches in-process `[B]` | embedded: **one claim at a time** `[S]` — `bins/controller/src/main.rs:1473` |
| Store on execution path | XML | **none** `[S]` — does not journal | PostgreSQL `[S]` |
| fsync on accept | configurable | **no policy selectable for a real run** `[S]` | **UNSUPPORTED BY CODE** `[U]` — the string `synchronous_commit` appears **NOWHERE** in the repository: not in `.rs`, `.sql`, `.toml`, `.yaml` or `deploy/`. The only real fsync is workspace file durability (`agent-runtime/src/executor/mod.rs:437`). **[WAS: "201 implies fsynced WAL", doc-cited — that claim is documentation with no implementation behind it]** |
| Terminal publication | n/a | fenced in store, off the exec path `[S]` | fenced exactly-once `[S]` — `controller-store/src/lib.rs:5091`, predicate `fence AND restore_epoch AND lease_owner` `:5116`; identical replay commits, divergent rolls back `:5130` |
| Crash / replay | resume | `script {}` is one durability unit: crash mid-block REFUSES resume by name, no child duplicates `[S]` — FG-171's lane scenario (the row's earlier re-run claim was falsified by measurement) | lease expiry → requeue, **fence PRESERVED not incremented** `[S]` — `scheduler.rs:563`, `:724` omits `fence` from the SET list |
| Effect ledger | n/a | four-state fenced Store ledger with immutable payload digests `[S]` — `FG-026`; no production connector/reconciler consumer — `FG-026b` P0 | outbox + object store `[S]` |
| Agent transport | JNLP/SSH | absent `[S]` — `FG-062` | mTLS mandatory, **plaintext impossible** `[S]` — `bins/controller/src/main.rs:1216` builds `ServerTlsConfig` unconditionally; per-RPC rejection `:1377`; no optional-TLS branch |
| Agent death handling | yes | absent `[S]` — `FG-063` | process-group revalidation `[S]` — `bins/agent/src/lib.rs:848` binds boot-id + `/proc/<pid>/stat` start-ticks; mismatch → `RetireStale` `:748`, so a recycled PGID is never `killpg`-ed |
| Tenant isolation | folders/RBAC | absent `[S]` — `FG-028` | org/project scoping + `FORCE ROW LEVEL SECURITY` in **19 migrations** `[S]` — e.g. `migrations/0003_public_api.sql:27` |
| Sandbox | Groovy sandbox | structural interpreter boundary `[S]` — `src/Fogell.Groovy.Interpreter/Value.fs`, `src/Fogell.Groovy.Interpreter/Sandbox.fs`, `src/Fogell.Groovy.Interpreter/Interpreter.fs`, `tests/Fogell.Groovy.Tests/Tests.fs`, and `scripts/prove-sandbox-denials.sh`; the type graph reachable from `Value` is exact-gated and named plus generic calls are denied through the real host (`FG-072`, executed receipt in the ticket); sanctioned-step internals and OS/egress/VM controls are outside this claim | n/a — no interpreter |

## 6. Operability

| | Jenkins | Fogell | McLoving |
|---|---|---|---|
| REST API | yes | `Fogell.Controller.Api`, **off the execution path** `[S]` | full REST `[S]` — `controller-api/src/lib.rs:311-468` |
| Artifact retrieval | yes | absent `[S]` — `FG-042b` | **list, metadata, content download, upload, commit** `[S]` **[WAS: "object caps" only]** |
| Logs / graph API | yes | progressive console `[S]` — `FG-064` DONE | `builds/{id}/logs`, `builds/{id}/graph` `[S]` |
| CLI | yes | absent `[S]` — `FG-066` | **22 subcommands** `[S]` — `bins/cli/src/lib.rs:236` **[WAS: bare name]** |
| Web UI | yes | absent `[S]` — `FG-067` | **served at `/`, `/app.js`, `/app.css`** `[S]` **[WAS: "none" — false]** |
| OpenAPI spec | plugin | absent `[U]` | `GET /openapi.json` `[S]` |
| Auth | realms | absent `[U]` | OIDC start/callback, session refresh `[S]` |
| Approvals / audit | plugins | approval lane exists `[R]` | `approvals`, `audit`, `credential-grants` routes `[S]` |
| Health endpoint | yes | `U` | **none** `[S]` — zero `/health`​/`/healthz`/`/readyz` hits repo-wide; readiness via `scheduler/explain` |

## 7. Performance

`luigi:~/trifecta-bench/results/trifecta-1785471289423.json`, **dated 2026-07-31**, medians ms,
15 iterations. Unchanged in revision 2.

| engine | `echo-e2e` | `parallel` 8×10 `sh` | failures |
|---|---:|---:|---|
| jenkins | 1657.51 | 12350.75 | 0 |
| jenkins-perfopt | 970.43 | 3829.13 | 0 |
| **fogell** | **321.80** | **1003.08** | 0 |
| **mcloving** | **452.53** | `N/A` — not expressible via the front end | 15 of 15 |

1. **Fogell's figures are a NO-DURABILITY configuration.** The execution path does not journal
   and no flag selects an `FsyncPolicy` for a real run. `321.80` compares to Jenkins
   PERFORMANCE_OPTIMIZED (`970.43`), **not** to Jenkins default or to McLoving, both of which
   persist. "5× faster than Jenkins" compares an engine that does not persist against ones that
   do.
2. **McLoving's parallel result is a front-end capability fact**, not a flake and not an engine
   limit — the store supports fan-out; the YAML surface does not expose it.
3. **The engine benchmarked is not the shipped engine** — the `fogell-run` wrapper "exists only
   in this clone/publish, not upstream"; ~0.11 s .NET cold-start floor.
4. **McLoving ran a hand-authored twin**, not the same pipeline — `cases.bb` line 2.

Cases are trivial by design and measure engine overhead — the only regime where three engines
compare at all. The real-project bench (`bench/PROJECTS.tsv`, `jenkins-bench` on luigi:18085)
has reached a Maven invocation and no further on either target.

## 8. What this says for steering — REWRITTEN in revision 2

**Revision 1 argued the engines are complementary: Fogell the front end, McLoving the spine.
The corrected rows do not support that as stated.**

McLoving has a Jenkins compiler, a corpus receipt Fogell lacks, Windows, SCM, secrets,
triggers, caching, dependency resolution, release provenance, a CLI, a UI and a full API. It is
not a spine waiting for a front end. It is a platform with a deliberately tiny front end.

**Fogell's remaining advantage is one thing, and it is real: the breadth of Groovy it executes
at runtime.** 181 receipted cases, declarative and scripted, `admitted=168` of 228 parsing,
14 modelled steps, parallel and conditionals — against McLoving's one-job compiler and
one-step catalogue. Nothing in McLoving's source suggests that breadth is close to being
matched, and its compiler explicitly never evaluates Groovy.

**So the question is narrower and sharper than revision 1 put it.** Not "which engine wins",
but: *is Fogell's Groovy breadth worth more than everything McLoving has already built around
it?* That is a judgement about where the remaining work is cheapest, and this document does
not answer it.

**What would settle it, and neither exists:**

- A measurement of what it costs to widen McLoving's compiler from one job toward the corpus —
  the mapping catalogue's `unknown_step: unsupported` policy makes that cost enumerable.
- A measurement of what it costs to give Fogell durability, agents and tenancy — every one of
  which is an open P0/P1 on its board and already built in McLoving.

Either is worth more than any further row here.

## 9. Verification

- Fogell corpus figures quoted from the generated scorecard; tokens `tier1=1`, `tier3=59`,
  `admitted=168` match the ledger BY HAND — `scripts/bin/audit-board-numbers` reads only
  `EXECUTION_BOARD.md`, so this file's copies are outside its reach and drift silently
  (they did: three `admitted=169` cells survived the token's move, caught by the
  FG-201-cycle verifier).
- Fogell steps from `src/Fogell.Differential/WalkerRules.fs`; gaps are open board rows by id.
- McLoving cells cite a path under `~/projects/mcloving-rel-001`; full survey and its stated
  coverage limits in [MCLOVING-SOURCE-SURVEY.md](MCLOVING-SOURCE-SURVEY.md).
- **The survey read ~15 crates at header depth and grepped the spine. It did not read 167k
  lines.** Revision 3 checked all twelve doc-cited cells against code, with docs disallowed as
  evidence: eight became `[S]` with `file:line`, three were wrong and are retracted in place, and
  **ONE REMAINS `[D]`** — the shared-library case count, which revision 3 upgraded without a code
  path and review caught. **The first draft of this line said NO cell rests on a doc, which was
  false the moment that upgrade was unjustified**; a sweep that reports total success is the
  claim to distrust. What remains unverified is marked `[U]`, and the
  survey's coverage limit is unchanged: crates not named were not examined.
- Performance from one named, dated result file. Re-run `luigi:~/trifecta-bench/harness/full-run.sh`
  to refresh; `jenkins-lab` is single-tenant.
