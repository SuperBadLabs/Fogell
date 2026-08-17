---
title: McLoving source survey — read from code, not from the benchmark runbook
audience: internal
category: engineering-survey
purpose: Correct the McLoving column of COMPARISON-MATRIX.md, which was built from a benchmark runbook that covered roughly half the system.
lifecycle: live
last-verified: 2026-08-17
---

# McLoving source survey

**Why this exists.** `docs/COMPARISON-MATRIX.md`'s McLoving column was written from
`RUNBOOK-mcloving.md` — a document produced to run a benchmark, whose author states they ran
none of the system (§9). Sizing the source found **167,314 lines, 173 files, 30 crates, 3
binaries**, and roughly half the crates are never mentioned by that runbook. This survey reads
the source instead.

**It is not a complete reading.** 167k lines were not read. This is a partitioned survey
targeting the cells the matrix was most likely to get wrong, and it says so rather than
implying coverage it does not have. Every claim below cites a path.

**Headline: the matrix was wrong in BOTH directions.** It understated McLoving's platform
reach and its Jenkins evidence, and it overstated the reach of McLoving's Jenkins front end by
collapsing "no Groovy path in the runtime" into "no Jenkins comparison". Those are different
claims.

## 1. Jenkins evidence — the correction that matters most

**MATRIX SAID: "McLoving has never been differentially compared to Jenkins at all."
THAT IS FALSE.**

`crates/jenkins-differential/src/lib.rs` records an EXECUTED differential, schema
`mcloving.jenkins.native-differential/v1`, case `corpus-052-cinqict_jenkinsdev`:

- Jenkins **2.568.1**, 90-plugin manifest `e33fa876…`, network-less container image `f4f65e6c…`
- McLoving ran compiled bytes through the shipped controller + embedded Linux worker against
  fresh PostgreSQL
- 16-file Jenkins capture set, capture manifest `0a2e33c7…`, sealed external envelope with a
  35-file manifest `8cd2c506…`; verifier accepts only a 30-file self-excluding manifest
- **Certified equivalence: 1 of 1 admitted cases, 1 of 228 corpus cases**
  (`docs/architecture/JENKINS_NATIVE_DIFFERENTIAL_V1.md`)

**Against Fogell's `tier1=0` of 228, McLoving has proven ONE MORE CORPUS FILE than Fogell
has.** Fogell's 163 receipts are a hand-written population it authored for itself; McLoving's
single receipt is a real third-party corpus job. On the shared denominator, the engine the
matrix described as having no Jenkins evidence is ahead.

There is a wider migration programme with no matrix row at all: **DIFF-002**
state/policy parity (20 named scenarios, `crates/state-policy-differential`), **DIFF-003**
external-boundary differential (13 named boundaries, `crates/boundary-differential`),
**MIG-006** aggregate (`crates/differential-aggregate`), **MIG-007** sealed packaging
(`crates/migration-package`).

## 2. Jenkinsfile front end — exists, and is narrower than "exists" implies

A Jenkinsfile→pipeline path DOES exist, and the matrix was right that it is not in the runtime:

- Compiler is **Clojure/Groovy**, `compat/jenkins-worker/src/mcloving/compat/compiler.clj`,
  launched only by `run-worker.sh` under rootless Podman
- Groovy builds a CONVERSION-phase AST only — **"no source is evaluated"**
  (`docs/architecture/JENKINS_COMPILER_WORKER_V1.md`)
- Output is deterministic strict-YAML Pipeline IR; `crates/jenkins-compiler-admission`
  reparses and revalidates it and must accept before the launcher reports `compiled`
- **Standalone tooling, NOT wired to the runtime.** No controller or execution-spine crate
  depends on it; the only workspace reverse dependency is `crates/migration-package`. DIFF-001's
  bytes were fed to the controller by hand.

**The admitted surface is one job.** `corpus-052-cinqict_jenkinsdev`; admitted syntax is
`pipeline`, `agent any`, `stages`, literal stage names, `steps`, literal `sh`.

**The step mapping catalog holds exactly ONE mapping** —
`jenkins.workflow-durable-task-step.sh.literal.v1`, literal `sh` → `/bin/sh -xe -c`
(`migration/mario-jenkins-oracle-228/corpus-v1/mapping-v1/catalog.yaml`). Policy:
`unknown_step: unsupported`, `unknown_plugin: unsupported`.

**Shared libraries: 23 live observations, ZERO executable cases**
(`docs/architecture/JENKINS_SHARED_LIBRARY_ADMISSION_V1.md`). `crates/jenkins-shared-library`
verifies prefetched trees; it has "no SCM or credential authority and never evaluates Groovy".

## 3. Platform subsystems the matrix has no row for

All confirmed by source, all absent from `RUNBOOK-mcloving.md`:

| crate | what it is | doc |
|---|---|---|
| `windows-job` | Win32 Job Object capsule, `#![cfg(windows)]` | `AGENT_RUNTIME.md` §Windows |
| `secret-broker` | Jenkins credential mapping, grant/redeem, 15-min TTL, delivery via arg/env/file/stdin, zeroized | `SECRET_MAPPING_V1.md` |
| `source-acquirer` | git via `git-upload-pack`, pinned commits, submodule allowlists, receipts | `SOURCE_ACQUISITION_V1.md` |
| `provisioner` | dynamic cloud agent provisioning, idempotency keys, fencing, quota/network/volume policy | `DYNAMIC_PROVISIONER_V1.md` |
| `cache` | `CacheKind::{Dependency, Build}`, tenant/trust-isolated | `CACHE_SERVICE_V1.md` |
| `dependency-resolver` | maven/npm/pypi → canonical signed plan (~9.5k lines) | `DEPENDENCY_RESOLUTION_V1.md` |
| `release-provenance` | signed releases, SBOM from `Cargo.lock`, transparency, deployment receipts | `RELEASE_PROVENANCE_V2.md` |
| `state-transfer` | pure Jenkins↔McLoving state transforms, canonical digest | `STATE_TRANSFER_V1.md` |
| `external-connector` | single-action outbound effects, signed outcome receipts, `ShadowReplayer` | `EXTERNAL_CONNECTOR_V1.md` |
| `destination-observer` | signed read-only pre/post effect observation | `DESTINATION_OBSERVER_V1.md` |
| `input-adapter` | outbound HTTP capture into classified signed receipts | `EXTERNAL_INPUT_ADAPTER_V1.md` |

**Windows is real.** `AGENT_RUNTIME.md:198-212` — "the agent runs as a native Windows
service"; `docs/verification/WINDOWS_PARITY_V1.md` is a Linux/Windows parity matrix with
WIN-001/002/003 verified, `cmd.exe /D /S /C` and `powershell.exe -File` executors, and service
install/start/stop. The IR carries `ProcessMode { Direct, WindowsCmd, PowerShell }`
(`crates/pipeline-ir/src/model.rs:148-155`).

**Triggers exist.** `docs/architecture/TRIGGER_INGRESS_V1.md:16-18` — `scm_webhook` and timer
triggers over the public API with a PostgreSQL delivery state machine.

## 4. Spine claims verified against source — two runbook claims corrected

| runbook claim | verdict | source |
|---|---|---|
| Linear DAG, stage k depends on k−1 | **compiler-only, not an engine limit** | `controller-api/src/lib.rs:4060-4080` builds the linear chain; `controller-store/src/dag.rs` is a GENERAL DAG — `Work\|Join\|Post` nodes, join nodes needing ≥2 deps, cycle validation, `MAX_DAG_NODES=256`, `MAX_DAG_EDGES=4096`, and **matrix expansion** (`MAX_MATRIX_AXES`, `MAX_MATRIX_CELLS`) |
| Exactly one step per stage enforced | **PARTIALLY REFUTED — enforced at EXECUTION, not admission** | IR validator requires only ≥1 step (`pipeline-ir/src/model.rs:690-693`, `MAX_STEPS=4096`); the `steps.len() != 1` rejection is in the executors (`execution-spine/src/lib.rs:150`, `bins/agent/src/worker.rs:437`). **A 2-step stage validates, plans, submits — then fails when an agent claims it.** |
| Parallel not expressible | **true at the user surface, false in the store** | no YAML `depends_on`; only build-creation route goes through the linear compiler. The store accepts arbitrary dependency vectors and fan-out. |
| Only `process` step type | **CONFIRMED** | `pipeline-ir/src/model.rs:132-134`, single enum variant |
| Embedded worker: one claim at a time | **CONFIRMED** | `bins/controller/src/main.rs:1473-1504`, single loop, awaits `run_claim` inline, no spawn |
| `MAX_STAGES=128` | **CONFIRMED**, not the binding graph limit | `pipeline-ir/src/model.rs:16-20` vs `dag.rs:10-17` |

**The one-step-per-stage finding is a latent defect worth naming**: a pipeline that passes
`validate` and `plan` and is accepted at submit can still be unexecutable, failing only when
claimed. That is admission accepting something the engine cannot run — the same shape as a
false success, in a system whose whole design is fail-closed admission.

## 5. Operability — the matrix understated this badly

**CLI**: `bins/cli` is a 919-line binary `mcloving-cli` with **22 subcommands** —
`validate plan apply submit pipeline-state set-pipeline-state watch status pipelines builds
graph logs cancel retry approve approvals explain artifacts artifact-download tests audit
completions` (`bins/cli/src/lib.rs:236-563`).

**HTTP API**: full REST under `/api/v1/organizations/{org}/…`
(`crates/controller-api/src/lib.rs:311-468`) — artifacts (list, metadata, **content download**,
upload, commit), logs, graph, approvals, credential grants, tests, audit, scheduler explain,
triggers with event post and delivery redrive, discovery scans, OIDC start/callback, session
refresh/logout, and `GET /openapi.json`.

**MATRIX SAID "Web UI: none". THAT IS WRONG** — a UI is served at `/`, `/app.js`, `/app.css`
(`crates/controller-api/src/lib.rs:466-468`).

## 6. What this survey does NOT establish

- **Nothing here was executed.** Every claim is source- or doc-cited. The matrix basis for
  these cells moves from *runbook-cited* to *source-cited*, and no further.
- **Coverage is partial by construction.** ~15 crates were surveyed at `lib.rs`-header depth;
  the spine was grepped for specific claims, not read. Crates not named here were not examined.
- **Nothing about quality or fitness.** A crate existing is not a crate working; McLoving's own
  `DYNAMIC_PROVISIONER_V1.md` claims no production provider.

## 7. Consequences for `COMPARISON-MATRIX.md`

Cells requiring correction, each to be recorded as a retraction rather than silently edited:

1. Jenkins differential — from "never compared" to **1/228 certified equivalent**
2. Windows — from "linux only" to **native Windows service, parity matrix, WIN-001/002/003**
3. Web UI — from "none" to **served at `/`**
4. Artifact retrieval — from object caps to **full download API**
5. CLI — from a name to **22 subcommands**
6. Triggers — from `U` to **scm_webhook + timer, documented**
7. Secrets — from token length to **credential broker with grant/redeem**
8. SCM — absent → **git acquisition with pinned commits**
9. Parallel/matrix — from "not expressible" to **not expressible via the user surface; the
   store supports join nodes and matrix expansion**
10. New rows needed: dependency resolution, caching, dynamic provisioning, release provenance,
    state transfer, external effects

**The steering conclusion in §9 of the matrix must be re-examined**, not merely patched. It
argued the two engines are complementary because Fogell has the front end and McLoving the
spine. McLoving turns out to have a Jenkins compiler, a corpus receipt Fogell lacks, Windows,
a UI, a CLI, triggers, secrets and SCM. The remaining Fogell advantage is the breadth of
Groovy it executes at runtime — which is real and large, but narrower than the matrix implied.
