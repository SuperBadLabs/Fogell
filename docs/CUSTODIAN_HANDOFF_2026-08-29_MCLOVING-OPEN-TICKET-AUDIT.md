# Fogell custodian handoff — 2026-08-29 — McLoving open-ticket audit

Status: **read-only related-work audit complete; no Fogell ticket status or
duplication-axis decision changed.**

This is a handoff appendix for the next custodian. It compares every ticket
open on Fogell's execution board with the authoritative local `origin/main` of
the sister project McLoving, identifies implementation mechanisms worth
porting, and records the places where apparent similarity does not satisfy the
Fogell acceptance.

## Read this first

- Host: `heman`.
- Fogell repository: `/home/srikanth/projects/fogell`.
- Fogell source commit audited:
  `164c97ee387e1628203cbcf570db59bf8439aa14`.
- Fogell source tree:
  `cf6e6ff8ce828bb8166f4d595b031e825b324397`.
- Fogell board accounting at that commit: 212 rows, 139 DONE, 73 open;
  open P0/P1/P2/P3 = 2/23/35/13.
- McLoving repository: `/sn8100/work/forge/McLoving`.
- Authoritative McLoving baseline: local `origin/main` at
  `bed6c0f52cd2ddcb8dea2859cb134ef15d402ef3`, committed
  `2026-08-27T21:33:36-05:00`.
- The checked-out McLoving head
  `7207852e0dfdd3b9e1c0478781a07e71994b9f50` is NOT current main. It diverged
  at `1348e343c35666f53f3aa9190de183e4214b6859`; `origin/main...HEAD` contains
  81 main-only and 8 branch-only commits.
- The useful Wave4B compiler work was later carried to main through squash
  commits. No useful capability in this audit exists only on the stale
  checked-out branch.
- All McLoving paths and line numbers below refer to `bed6c0f` unless stated
  otherwise. Use `git show bed6c0f:<path>`; do not silently read the checked-out
  Wave4B file when the two differ.

This audit was read-only. No McLoving or Fogell source, ticket, receipt, or
board row was changed to produce it.

## Classification contract

The classification answers, "how much implementation can Fogell reuse?" It
does NOT answer, "has the Fogell ticket been completed?"

- **COPY-grade** — the acceptance's core algorithm and proof shape
  substantially exist in McLoving. Because McLoving is Rust/Clojure and Fogell
  is F#, this still means a reviewed port plus Fogell-owned tests.
- **ADAPT** — a real implementation seam is reusable, but Fogell semantics,
  integration, or evidence remain material.
- **ADJACENT** — useful precedent exists, but it does not discharge the named
  acceptance.
- **NONE** — no useful analogue was found.

This vocabulary deliberately differs from
`docs/DUPLICATION-AXIS-RECOMMENDATIONS.md`. That document asks whether sibling
work can discharge an engine-specific board acceptance. This document asks
whether code, contracts, or proof shapes are worth porting. A COPY-grade row
here is not a `DUPLICATED` board decision.

The boundary remains non-negotiable: McLoving receipts do not transfer.
McLoving-vs-Jenkins evidence licenses no Fogell compatibility or DONE claim.
Any port carries provenance, receives Fogell review, and is re-measured against
Fogell's pinned Jenkins oracle where the acceptance requires it.

## Executive result

All 73 open Fogell tickets were read by acceptance field and classified:

| reuse class | count |
|---|---:|
| COPY-grade | 12 |
| ADAPT | 34 |
| ADJACENT | 14 |
| NONE | 13 |

The strongest transferable work is not McLoving's Jenkins front end. It is the
control plane around it: durable retry and effect reconciliation, atomic
Windows containment, outbound agent recovery, capability scheduling,
controller/API/CLI/UI structure, and release/evidence integrity.

## Highest-value implementation donors

### Durable retry — FG-027, FG-027a, FG-027b — COPY-grade

McLoving implements the same core contract Fogell is assembling:

- a retry never rewrites an attempt;
- one immutable child carries the incremented ordinal and `retry_of` link;
- replay returns the exact prior child;
- retry, dead-letter, and terminal reconciliation are mutually exclusive under
  one advisory lock;
- exhaustion writes a checksummed dead-letter decision; and
- aggregate reopening, child creation, event, and outbox changes are one
  transaction.

Read:

- `docs/architecture/CONTROLLER_TRUTH.md:24-33,98-111`;
- `crates/controller-store/src/lib.rs:4472-4670`;
- `crates/controller-store/src/dag.rs:528-618`; and
- `crates/controller-store/tests/postgres_truth.rs:9131-9212`.

Port the state machine and concurrency matrix. Do not port PostgreSQL-specific
storage details blindly into Fogell's existing Store boundary.

### External effects — FG-026b — ADAPT, strong

The old Fogell dossier claim that McLoving lacked a four-state effect ledger is
stale. Current main has immutable payload digests and monotonic
`prepared/applied/confirmed/uncertain` checkpoints. Lease expiry routes
possibly applied effects to explicit reconciliation rather than runnable
requeue. `EXT-002` also wires a typed, digest-pinned out-of-process connector,
independent observer, and deny-authority shadow replay through the product
path, with substitution, crash, cancellation, timeout, retry, and ambiguity
tests.

Read:

- `docs/architecture/CONTROLLER_TRUTH.md:14-48`;
- `docs/architecture/RUNTIME_EFFECT_INTEGRATION_V1.md:18-75,91-153`;
- `crates/controller-store/src/lib.rs:3351,3997,4432`;
- `crates/execution-spine/src/effect_runtime.rs`; and
- `docs/EXECUTION_BOARD.md`, `EXT-002`.

Fogell should copy the transition and recovery design. It must still implement
its own bounded, closed-world producer registry and prove that every modelled
Fogell producer reaches FG-026. McLoving's `EXEC-005` also remains pending for
five non-effect helper paths; do not inflate `EXT-002` into a claim that every
McLoving helper is product-reachable.

### Windows execution — FG-038a — COPY-grade

McLoving's audited FFI capsule creates a workload suspended and atomically in a
kill-on-close Job using `PROC_THREAD_ATTRIBUTE_JOB_LIST`. It restricts inherited
handles, returns the exact process identity for durable recording, and resumes
only after that recording. Native forced-crash gates cover creation boundaries
and descendant cleanup.

Read:

- `crates/windows-job/src/lib.rs:1-8,204-359`;
- `crates/agent-runtime/src/executor/windows.rs:111-217`;
- `.github/workflows/windows-agent.yml:49-118`; and
- `docs/verification/WINDOWS_PARITY_V1.md`.

This is the best direct donor in McLoving. Port the capsule into the single
P/Invoke project required by ADR 0006 and retain Fogell's own background-child
acceptance. It does not close FG-038b/FG-113: McLoving's Windows lane is
McLoving Linux/Windows parity, not Fogell and pinned Jenkins on the same
Windows runner.

### Remote agent — FG-062 — COPY-grade; FG-063 — ADAPT

McLoving has outbound mTLS, certificate-bound tenant/trust-pool identity,
protocol-major/minor negotiation, monotonically fenced sessions, journal-
before-ack acceptance in SQLite WAL with `synchronous=FULL`, durable log/result
spool descriptors, bounded RPCs, lease-loss cancellation, and exact restart
reconciliation. Lost terminal responses converge through idempotent replay.

Read:

- `docs/architecture/AGENT_RUNTIME.md:5-41,86-138`;
- `crates/agent-protocol/proto/agent.proto:5-84,174-216`;
- `crates/agent-runtime/src/lib.rs`; and
- `bins/agent/tests/remote_work.rs`.

The protocol and journal are COPY-grade for FG-062. McLoving uses lease fencing
and reconciliation rather than Fogell's exact announced reconnect-grace then
`ABORTED(agent-death)` contract, so FG-063 remains ADAPT.

### Matrix, parameters, triggers, and agents — FG-017, FG-050, FG-053b,
FG-053c — ADAPT

- `crates/controller-store/src/dag.rs:112-199` is a pure bounded Cartesian
  matrix compiler with duplicate, empty-axis, overflow, and size tests in
  `crates/controller-store/tests/dag_contract.rs:10-118`.
- Exact platform/capability subset and trust-pool matching live in
  `crates/controller-store/src/scheduler.rs:143-179` and
  `docs/architecture/CAPABILITY_VOCABULARY_V1.md`.
- Typed parameter declarations and values live in
  `crates/pipeline-ir/src/model.rs:70-110,263-285`.
- Typed trigger admission, deduplication, claim leases, retry, and dead-letter
  live in `docs/architecture/TRIGGER_INGRESS_V1.md`.

Port the backend contracts. McLoving's Jenkins compiler accepts only a tiny
sealed subset and does not parse Jenkins `matrix`, labels, Docker agents,
parameters, or triggers. Measure Jenkins matrix order before copying
McLoving's choice to sort axes and values.

### Product shell — FG-224, FG-067 — COPY-grade; FG-066 — ADAPT

McLoving ships a runnable controller, public REST API, extensive CLI, and a
static CSP-locked API-only UI. The UI loads build lists, graph, paginated logs,
tests, artifacts, and approvals. Useful paths:

- `bins/controller/src/main.rs`;
- `bins/cli/src/lib.rs:40-225`;
- `crates/controller-api/src/lib.rs`; and
- `crates/controller-api/ui/app.js:100-260`.

The controller orchestration and read-only UI journeys are COPY-grade
analogues. The CLI is an ADAPT because Fogell's required verbs and golden
output contract differ. McLoving exposes its native API and has no
Jenkins-shaped, off-by-default compatibility shim for FG-065.

### Release and evidence — FG-074, FG-080, FG-093 and evidence-tool tickets

McLoving's release path runs a locked/offline Cargo build in a pinned,
networkless, read-only container; emits an exact output set, lock-derived SBOM,
deterministic self-hashing bundle, and component digests; then binds source,
builder, policy gates, bundle, SBOM, signer, transparency evidence, independent
timestamp, and rollback ancestry. A private non-deserializable
`VerifiedRelease` value is the only deployment-authority witness.

Read:

- `docs/architecture/RELEASE_PROVENANCE_V2.md:41-125,127-213`;
- `crates/release-provenance/src/lib.rs:40-105,508-610,770-881`;
- `scripts/release-builder-contained.sh`; and
- `scripts/release-build-inner.sh`.

This strongly informs FG-074, FG-080, and FG-093, but does not replace
Fogell's NuGet graph proof, hostile raw-checkout audit, sealed descriptor
handoff, or required byte-identical Fogell artifact/image rebuild.

Three small evidence-tool ports are especially attractive:

- FG-163: use one terminal `strip_suffix`, never a global replace
  (`crates/object-store/src/lib.rs:518-545`).
- FG-166: carry explicit forward `{source,evidence}` joins and compare exact
  keyed file sets (`scripts/build-jenkins-corpus-index.py` and
  `scripts/verify-jenkins-corpus.py`).
- FG-169: hash complete files through detached exact-set manifests
  (`crates/jenkins-differential/src/lib.rs:260-357,430-493`), adapted for
  Fogell's blanked self-reference and delimited unsealed regions.

FG-207 also has a useful narrow pattern: McLoving records terminal phase and
complete result descriptors in one immediate SQLite transaction. Fogell can
adapt that idea so a failed step plus reason pays one durability barrier, not
two.

## Exhaustive disposition

### COPY-grade — 12

`FG-027`, `FG-027a`, `FG-027b`, `FG-038a`, `FG-062`, `FG-067`, `FG-085`,
`FG-163`, `FG-166`, `FG-169`, `FG-222`, `FG-224`.

### ADAPT — 34

`FG-004b`, `FG-016`, `FG-017`, `FG-026b`, `FG-044b`, `FG-046c`, `FG-048c`,
`FG-050`, `FG-053b`, `FG-053c`, `FG-063`, `FG-066`, `FG-074`, `FG-080`,
`FG-082`, `FG-093`, `FG-094`, `FG-117`, `FG-121`, `FG-126`, `FG-127`,
`FG-128`, `FG-129`, `FG-132`, `FG-133`, `FG-135`, `FG-136`, `FG-137`,
`FG-173`, `FG-177`, `FG-197`, `FG-204`, `FG-207`, `FG-225`.

### ADJACENT — 14

`FG-014`, `FG-038b`, `FG-045b`, `FG-051`, `FG-113`, `FG-115`, `FG-116`,
`FG-118`, `FG-124`, `FG-130`, `FG-140`, `FG-180`, `FG-190`, `FG-192`.

### NONE — 13

`FG-016b`, `FG-029`, `FG-065`, `FG-070b`, `FG-083`, `FG-084`, `FG-120`,
`FG-123`, `FG-139`, `FG-178`, `FG-203`, `FG-205`, `FG-227`.

## Per-cluster notes the lists do not show

### Parser and interpreter

McLoving has useful property-test and diagnostic primitives:

- arbitrary-input panic-freedom and explicit parse limits
  (`crates/pipeline-ir/tests/admission_properties.rs` and
  `crates/pipeline-ir/src/strict_yaml.rs`);
- structured offset/line/column spans and stable error codes;
- an explicit compiled/unsupported/rejected response split; and
- a small data-driven mapping descriptor in
  `migration/mario-jenkins-oracle-228/corpus-v1/mapping-v1/catalog.yaml`.

Those inform FG-004b, FG-016, FG-121, FG-129, FG-133, and FG-177. They do not
provide Fogell's Groovy semantics. McLoving delegates parsing to pinned Groovy,
never evaluates Groovy source, and admits one sealed Jenkins migration case
with `pipeline`, `agent any`, stages, and literal `sh`. It has no analogue for
Fogell's hosted closures, live `env` object, stage-scoped timestamps,
`ansiColor`, slashy scanner, cyclic value operations, or most parser residuals.

### Approvals and retry-in-stage semantics

McLoving has TTL-bound protected-environment approvals, durable fail-fast
sibling cancellation, immutable attempt identities, transactional retry, and
post nodes. These are useful patterns for FG-115, FG-116, FG-135 through
FG-137, and FG-204. They do not implement Jenkins `input` late-answer audit
semantics, `retry(conditions:)`, per-attempt Jenkins stage `post`, or nested
interpreter resume positions.

### Recovery and operations

McLoving has backup/PITR contracts, restore-epoch fencing, object
reconciliation, Windows crash/reboot receipts, and an executable PostgreSQL
backup/restore drill. These make FG-085 COPY-grade and FG-082 ADAPT. Its full
performance, destructive war, multi-day soak, and DR campaigns remain pending,
so there is no FG-083 `503 + Retry-After` saturation proof or FG-084 ten-minute
RSS/FD soak to take.

### Environment and isolation

`crates/agent-runtime/src/executor/unix.rs` uses `env_clear()` and applies only
an explicit child-environment allowlist. That is the direct mechanism Fogell
needs for FG-222's inherited-controller-environment defect.

Do NOT claim it provides FG-070b. McLoving's own pending `SEC-005` states that
submitted workloads run under the deployment service UID and can read its
0600 controller/API/database credentials and agent mTLS private key. Its
Windows Job Object also binds lifecycle, not privilege. Same-UID secret,
filesystem, process, IPC, endpoint, and resource containment remain explicitly
open in McLoving.

## Traps to keep visible

1. **Wrong checkout.** Read `origin/main` at a recorded commit, not the stale
   Wave4B worktree.
2. **Receipts do not transfer.** Re-run Fogell/Jenkins evidence.
3. **SQLite is agent-only.** McLoving's controller truth is PostgreSQL; it has
   no FG-029 single-user controller mode.
4. **Windows parity is not Windows differential.** No pinned Jenkins and
   McLoving run on the same Windows runner with Fogell's CRLF contract.
5. **Native API is not a Jenkins shim.** There is no FG-065 donor.
6. **Release provenance is not reproducibility by itself.** McLoving has a
   deterministic private Linux bundle but no acceptance proving Fogell's
   artifact plus image are byte-identical across two builds.
7. **The shipped topology is incomplete.** `EXEC-005`, `SECRET-002`,
   `REL-003`, performance, war, DR, and release-readiness work remain pending.
8. **Do not copy the stale Wave catalog reader.** Main retains `O_NOFOLLOW`,
   bounded single-descriptor reads, and compiled expected digest pins that the
   Wave checkout lacks.

## Recommended opening sequence for the next custodian

1. Fetch both repositories and record fresh exact heads before relying on this
   snapshot.
2. Close the small evidence defects first: FG-163 and FG-166, then combine the
   FG-128/FG-169 design so provenance and whole-document content are separate
   versioned digest domains.
3. Finish Fogell's retry runtime using McLoving's FG-027 analogue; the Store
   foundations already exist, so focus on runtime retryability policy and
   scheduler/controller invocation rather than recreating persistence.
4. Port the Windows Job Object capsule for FG-038a with Fogell-owned native CI
   and descendant-reap proof.
5. Use McLoving's agent protocol/journal model for FG-062.
6. Adapt the connector/reconciliation spine for FG-026b, retaining a bounded
   Fogell producer registry and bypass mutation.
7. Follow with the product vertical slice FG-224/FG-066/FG-067.

Treat this as an implementation map, not an instruction to select a ticket
automatically. Pick an impact dimension deliberately, prove its narrow Fogell
acceptance, and leave McLoving provenance in the commit message and review
record.

The watch is complete. The next custodian inherits a current map of the sister
project, including the places where its strongest-looking features still stop
short of Fogell's claim boundary.
