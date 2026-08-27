# Fogell threat model

Status: **current-tree security model** (FG-073). This document describes the
reviewed source in this repository, not accepted architecture in the abstract
and not changes reported from another branch or worktree. Its implementation
claims are deliberately narrower than Fogell's target architecture.

## How to read security claims

Fogell's ADRs mix decisions, target architecture, and implemented mechanisms.
`Status: Accepted` means a design was accepted; it does **not** mean every
required property in that ADR exists. This document uses four explicit states:

| state | meaning |
|---|---|
| **Implemented in this tree** | Production source and focused tests or executable evidence exist in this tree. A cited ticket may still be PARTIAL for publication or a larger residual. |
| **Partial** | A useful mechanism exists, but it does not establish the larger security boundary named beside it. |
| **Required / absent** | An ADR, operating rule, or ticket requires the control, but this tree does not implement it. |
| **Outside this tree** | Work may exist elsewhere, but it supplies no control to this model. |

The narrow deployment model this tree can support without making a stronger
claim is a **trusted controller and operator running one mutually trusting
execution domain on a dedicated Linux VM**. PostgreSQL now enforces
organization-scoped rows for the restricted runtime role, but the API still has
one global operator bearer and local execution still shares an OS identity with
the controller. The VM boundary, not that shared identity, must separate this
domain from mutually hostile workloads.
Fogell is therefore not yet a hostile multi-tenant execution service.

## Scope and assets

This model covers the runnable single-node controller and API, PostgreSQL store,
pipeline parsing and Groovy interpretation, Linux step execution, workspaces,
credentials, logs, and the still-planned remote controller-agent seam. The
Jenkins oracle and differential lab are covered only where their operation
affects an execution or evidence claim.

Assets, in descending consequence if corrupted or disclosed:

1. Controller authority: API bearer token, database credentials, signing or
   agent credentials, restore epoch, and the ability to admit or cancel work.
2. Tenant identity and controller truth: organizations, projects, builds,
   attempts, leases/fences, events, logs, outbox records, and idempotency keys.
3. Pipeline secrets and outputs: credential values/files, console logs, JUnit
   results, archived artifacts, and workspace contents.
4. Execution integrity: the exact Jenkinsfile and revision, requested steps,
   terminal result, durable journal, and evidence receipts.
5. Agent and host availability: CPU, memory, process table, disk, network, and
   workspace capacity.

## Actors and assumptions

| actor | capability assumed here |
|---|---|
| Pipeline author | Controls Jenkinsfile text, step arguments, and other submitted pipeline inputs. Treat as malicious at parser/interpreter boundaries; an author admitted to sanctioned OS capabilities such as `sh`/`bat` must belong to the current mutually trusting execution domain. |
| Authenticated API caller | Holds the one controller bearer token. This is an operator-level principal, not a tenant identity. |
| Stale agent | Retains an old offer, fence, owner identity, or pre-restore state. May replay old publication. |
| Hostile current agent | Holds the current offer and can fabricate execution, logs, or a terminal result. Treat as malicious for the target model. |
| Same-UID process | Can inspect resources available to the agent account, including `/proc` and files that UID owns. |
| Operator/deployer | Trusted to configure the controller, database, VM/account separation, network policy, and secret placement correctly. |
| Network/dependency peer | May observe, tamper with, or deny traffic unless transport, egress, and dependency controls prevent it. |

A trusted operator is not a substitute for tenant isolation. A trusted current
agent is an operational assumption in this tree, not a property established by
fences.

## Trust boundaries and current controls

### 1. API client to controller

**Implemented in this tree.** `Fogell.Controller.Host` is a runnable single-node
host. Startup requires an API token file, rejects tokens shorter than 32 bytes,
and accepts only HTTPS or loopback HTTP. Every build route authenticates before
parsing path data, and token comparison is fixed-time
([`Authorization.fs`](../src/Fogell.Controller.Api/Authorization.fs),
[`Config.fs`](../src/Fogell.Controller.Host/Config.fs); FG-060/FG-224).
The host's HTTPS setting is a listener requirement, not proof that certificates,
reverse proxies, or transport policy were deployed correctly.

The router retains at most the configured pipeline-byte limit plus one byte and
classifies both known-length and chunked overflow as `pipeline_too_large` before
UTF-8 decoding or parsing. Admission then applies FG-004 limits. Request-selected
execution placement is refused; the controller owns the admitted trust pool.
Status, log, and cancellation operations bind organization, project, and build
instead of treating `projectId` as decorative. The scheduling-explanation route
is organization-scoped and may query a diagnostic trust-pool value; it does not
admit or move work
([`Router.fs`](../src/Fogell.Controller.Api/Router.fs); FG-060a/FG-224).

**Partial, not tenant authorization.** The bearer has no subject, role,
organization membership, or project claim. A holder is a global operator who
may select organization/project identifiers. Forced database RLS limits a
transaction after the controller selects its organization; it does not turn the
bearer into tenant identity or RBAC. The current host also has no public
organization/project provisioning API, token rotation protocol, rate limiter,
or per-principal audit model.

### 2. Controller to PostgreSQL and tenant data

**Implemented in this tree.** Composite tenant keys prevent cross-organization
parent substitution. The restricted runtime role is required to be
`NOSUPERUSER NOBYPASSRLS`; eleven tenant tables have ENABLE plus FORCE RLS, and
Store transactions set the organization context transaction-locally. Live tests
prove absent context sees no tenant rows, reused connections do not retain
context, malformed context fails, and direct cross-tenant writes are rejected
([`0005_forced_tenant_rls.sql`](../src/Fogell.Store/migrations/0005_forced_tenant_rls.sql),
[`Store.fs`](../src/Fogell.Store/Store.fs); FG-028).

Startup keeps distinct runtime and maintenance capabilities, actively proves
they reach the same live database, applies checksum-pinned migrations through
maintenance only, and refuses a runtime identity that can bypass RLS or lacks
the exact controller surface (FG-224). Fences, owner, lease expiry, restore
epoch, and state jointly arbitrate terminal publication; restore and expiry
paths publish reasoned reconciliation atomically.

The FG-026 Store foundation persists immutable payload digests through
`prepared`, `applied`, `confirmed`, and terminal `uncertain`, and lists stale
effects per tenant. FG-027b persists retry arbitration and exact replay. These
are durable primitives, not a claim that every registered external-effect step
uses `PrepareEffect`/`AdvanceEffect` or that controller retry policy is complete.
The current runnable worker has no generic external connector that discharges
those integration residuals.

**Boundary.** Transaction-local RLS context is selected by trusted controller
code; PostgreSQL custom settings are not an identity provider. Code already
able to issue arbitrary SQL as the runtime role could select another
organization. Preventing such SQL execution and binding requests to tenant
identity remain controller/authentication responsibilities.

### 3. Untrusted Jenkinsfile to parser and interpreter

**Implemented in this tree.** Default admission limits bound source characters,
node count, nesting, and scalar length before schema compilation (FG-004,
[`Limits.fs`](../src/Fogell.Admission/Limits.fs) and
`Parser.parseWithLimits`). The standing FG-004b sweep sends 10,000 replay-pinned
malformed sources through that boundary and requires typed positioned refusal.

The Groovy `Value` graph is transitive-schema audited so an unreviewed CLR host
carrier cannot hide under an existing union case. Free and member calls deny by
default; the named escape inventory and generic fallbacks traverse the real
parser/interpreter/host path, and null-safe calls are admitted before the null
short-circuit. Evaluator work, recursion, range, materialization, and regex
budgets bound admitted computation
([`Sandbox.fs`](../src/Fogell.Groovy.Interpreter/Sandbox.fs),
[`prove-sandbox-denials.sh`](../scripts/prove-sandbox-denials.sh); FG-013/FG-072).

**Hard boundary.** Registered steps are sanctioned capabilities. In particular,
`sh` and `bat` intentionally execute arbitrary agent code. Parser or interpreter
admission does not make a shell command safe, constrain the files it can read,
or constrain its network. Syntax rejected before interpretation is also not a
sandbox denial. ADR 0002 and FG-072 state these non-goals.

### 4. Interpreter/step to the process environment

**Implemented in this tree.** Build-visible GString lookup, shell/bat, and build
Git no longer inherit the host environment. Their launch profiles start cleared
and receive only explicit authority: a fixed system PATH, run-scoped neutral
Fogell HOME, synthetic Jenkins metadata, or declared pipeline overlays.
Controller-side Git instead receives a separately typed, explicit allowlist
snapshot from the controller environment. The real-host
blocking proof plants controller secrets and requires them absent across GString,
shell, and recording Git launchers
([`prove-control-env-isolation.sh`](../scripts/prove-control-env-isolation.sh);
FG-222).

This is environment isolation, not full process isolation. Explicit credentials,
prepared private-SCM material, files readable by the build UID, network services,
kernel state, and command output remain separate capabilities. A new process
launch path must choose a reviewed profile; falling back to ambient inheritance
would reopen controller authority.

### 5. Step to workspace, filesystem, and processes

**Implemented in this tree.** Fogell-managed workspace paths reject absolute
paths, parent traversal, symlink components, outside-root resolution, and
workspace reuse ([`Workspace.fs`](../src/Fogell.Execution/Workspace.fs);
FG-030). Linux steps run in process groups; timeout/cancellation use SIGTERM,
grace, then SIGKILL, and normal completion reaps descendants. The runnable
controller binds cleanup to captured Linux process birth identity, tracks inner
groups before release, and treats ambiguous extinction as reconciliation rather
than guessed success (FG-031/FG-032/FG-224).

**Partial, not isolation.** A shell runs with all filesystem permissions of the
agent account. Lexical workspace checks do not confine arbitrary shell paths.
A determined process can create a new session and escape its process group.
Process groups are lifecycle cleanup, not hostile containment; ADR 0008 and the
source comment require VM-level isolation for mutually untrusted work. Cgroups,
Windows Job Objects, and hostile-workload VM provisioning are accepted design
targets, not complete cross-platform controls in this tree.

ADR 0008 permits a ticket-specific compatibility scanner to follow links when a
pinned plugin does. FG-221's JUnit scanner exception is bounded by pattern-aware
work limits and physical-target identity, but remains a compatibility behavior,
not filesystem isolation, credential separation, or permission proof.

### 6. Secret delivery and logs

**Implemented but partial.** Secret bindings create unique companion files no
broader than mode 0600, restore exact owner read/write through the open descriptor
before writing any secret byte even under a restrictive umask, scope environment
bindings, and attempt best-effort revocation on lexical exit including catchable
host failures. They mask registered text literal/case-folded/base64 forms plus
the exact base64 of binary file credentials at least eight bytes long on
streaming and buffered output, and warn on detectable text
reverse/hex/char-split forms plus binary-file hex at that same floor
(`src/Fogell.Execution/Secrets.fs` and
`src/Fogell.Execution/Executor.fs`; FG-070/FG-071).

File-credential byte-derived forms are immutable strings prepared once from an
owned byte snapshot and shared by its repeated lexical bindings, so neither
output volume nor file-credential rebinding multiplies those full encodings. The
eight-byte floor keeps ordinary low-entropy text such as `dead` from becoming
proof of a two-byte file credential leak; file values below eight bytes receive
no byte-derived base64/hex protection, though valid UTF-8 content may still
contribute text-derived literal/case-folded forms. WalkerCtx retains binding
metadata for the run to preserve output protection; revocation
removes the companion file and lexical environment overlay but does not zeroize
the credential store's source bytes, derived managed strings, or other process
memory. `SecretBinding` adds no second raw-byte copy.

Mode 0600 separates OS users, not processes with the same UID. A same-UID process
can read the secret file and `/proc/<pid>/environ`; masking is defence-in-depth,
not access control, and arbitrary transformations cannot all be masked. FG-070b
requires a distinct UID or user namespace and a probe showing both resources are
unreadable. It remains TODO. Do not place mutually hostile builds under one UID.

Controller logs are fenced, attempt-bound, sequence-ordered, and page-bounded,
but they contain whatever admitted steps emit after the implemented masking and
detection passes. Fogell does not claim arbitrary secret transformation can be
recognized or that retained artifacts/JUnit/workspaces are automatically secret
free.

Abrupt controller/Run.Host death or a failed deletion can leave an owner-only
file beneath the controller-side `_secrets` state tree; there is no startup stale-
secret reconciliation in this tree. Mode 0600 limits other UIDs' access to that
residue and does not protect it from the current shared workload identity. State-root
retention, backup, and cleanup policy must treat that directory as credential
material.

### 7. Agent to controller

**Implemented against stale publication, not malicious agents.** The current
controller uses a local Linux worker, not the target remote-agent protocol.
Fence, owner, lease expiry, and restore epoch checks prevent an old or
superseded holder from publishing logs, reconciliation, or terminal result.
Capability/trust-pool matching occurs in the SQL claim; readiness and post-offer
guards refuse missing launchers or durable state, and lease scanning owns stale
disposition (FG-061/FG-224).

**Required / absent for the target boundary.** The versioned mTLS agent protocol,
agent-local durable log with offset recovery, session/certificate epochs, and
bounded agent-death handling are ADR 0008 requirements carried by FG-062/FG-063,
both TODO. More fundamentally, a current correctly fenced agent can still lie
about execution, logs, workspace hashes, or results. Fences prove freshness and
exclusive publication; they do not attest execution or contain a malicious
current agent. Trust-pool labels provide scheduler routing, not attestation or
containment; do not describe them or the present fence fields as hostile-agent
proof.

**Trusted local seam.** The controller and Run.Host currently communicate by
state-root event and journal files while running as the same UID. A sanctioned
build receives `HOME` and `WORKSPACE` paths beneath that shared state root, so it
can derive and navigate toward the sibling controller protocol trees wherever
the shared UID has permission. The shared identity also collapses controller
credential separation: a token file owned at 0400/0600 is readable by a workload
running as that owner, and host policy may expose controller database capability
through process state such as `/proc`. Lease owner, fence, lease expiry, attempt
state, and restore epoch reject stale database publication; they do not
authenticate local event or journal bytes against code running as that UID. The
local Controller/Run.Host protocol is therefore trusted same-UID IPC, not an
authenticated hostile-workload boundary.

### 8. Network, oracle, dependencies, and release inputs

**Required / absent.** ADR 0004 requires live third-party pipeline execution to
be allowlisted by executed surface, on a no-egress network, in a disposable
workspace. FG-200 measured that the no-egress fence does not exist: the Jenkins
lab resolved DNS and fetched external HTTPS, and the Fogell development host had
full egress (`docs/tickets/FG-200.md`). An inert `echo` receipt was safe because
its executed surface had no network capability; it does not generalize to `sh`.

**Implemented, with a narrow dependency claim.** Every solution project has a
NuGet lock, locked mode is evaluated per project, and the blocking FG-074 proof
regenerates the graph and performs a source-cleared warm-cache restore plus
`--no-restore` build. This detects graph/content drift; it is not OS no-egress,
a cold-airgap build, package safety, vulnerability freshness, SBOM, signature,
or reproducible-artifact proof.

Release-provenance and evidence-sealing controls are present as partial
foundations (FG-093/FG-223), while reproducible packaging remains FG-080. A
checksum-valid bundle or locked graph proves only the inputs and boundary its
checker actually binds; neither attests a hostile host, kernel, package
publisher, or downstream command that reads different bytes.

The FG-223 evidence sealer proves fail-closed transactional integrity of the
declared command and sealed bundle; it does not prove the command was a good
semantic test. FG-093 supplies verification machinery for an externally supplied
manifest/artifact tuple and sealed handoff, but the canonical artifact, trusted
manifest authority, and live release invocation remain absent; the machinery
does not authenticate, sign, or authorize a manifest. The corpus
scorecard/regression proof is intentionally UNVERIFIED in corpus-free GitHub CI.
Differential receipts attest only the bounded behavior they measured, with their
recorded inputs and oracle.

## Abuse cases and disposition

| abuse case | current result | required response / owner |
|---|---|---|
| Use reflection, class loading, direct file/network/process APIs, or unsafe methods from interpreted Groovy | Denied by the closed value/call boundary; the blocking FG-072 proof checks named/generic denial and halt. | Keep the transitive carrier inventory and end-to-end proof blocking. |
| Invoke `sh` to read host files or contact the network | Allowed with the agent account's authority; no egress fence exists. | Dedicated VM/account plus default-deny egress before hostile execution; ADR 0004 / FG-200 residual. |
| Read controller credentials from ambient environment | GString, shell, and both Git profiles start from cleared explicit environments; the blocking FG-222 proof plants and rejects inherited authority. | Keep every process launcher profile-explicit; separately protect readable files, sockets, and network services. |
| Escape a managed path with `../`, an absolute path, or a symlink component | Managed workspace resolver refuses it. Arbitrary shell paths remain possible. | Keep resolver tests; enforce VM/account boundary for shell. |
| Leave descendants after completion or timeout | Ordinary descendants are reaped; a hostile process can leave the group. | Lifecycle claim only; hostile isolation requires VM boundary. |
| Read another build's credential as the same UID | Possible; 0600 and masking do not stop it. | FG-070b distinct UID/user namespace plus original-user probe. |
| Recover a credential file left by abrupt host death or failed deletion | Possible beneath the controller `_secrets` tree; lexical `finally` covers catchable host failures, not process death or a deletion error. | Isolate the controller identity, protect the state root, and add bounded stale-secret recovery before claiming crash-safe revocation. |
| Replay a stale or pre-restore terminal publication | Refused by fence/owner/lease/epoch checks. | Keep real-PostgreSQL concurrency and restore tests. |
| Fabricate output/result as the current worker | Not prevented by freshness fencing; the current local worker is trusted. | Remote mTLS/session work plus an explicit trust/attestation model; FG-062 is necessary but not sufficient. |
| Read or tamper with controller credentials, state, or the local Run.Host protocol as the build UID | Possible where the shared UID can read the token, process state, or state root; cleared environments and path checks do not isolate controller capabilities or journals/events, and fences do not authenticate those bytes. | Put controller and workload under separate identities or VMs with controller credentials/state inaccessible; the current local worker is one mutually trusting domain. |
| Use the global bearer to select another organization/project | Allowed to the bearer by design; request-selected trust pools are refused and Store work is RLS-scoped after selection. | Add tenant principals, membership/RBAC, and scoped credentials before tenant-facing exposure. |
| Address a build through the wrong project path | Refused by organization/project/build-qualified reads and writes. | Keep FG-060a route-binding tests; this is object binding, not caller authorization. |
| Exhaust memory with a known-length or chunked pipeline body | Router retains at most the configured maximum plus one byte and returns 413. Fogell adds no explicit application-level request deadline, rate limiter, or aggregate-concurrency cap; server/deployment defaults are outside this proof. | Keep both body-shape tests; add deployment-level deadline, rate, and concurrency controls separately. |
| Forge an approval by writing Run.Host inbox files | Fresh controller admission refuses unbounded `input`; the filesystem inbox is standalone trusted orchestration only. | Build an authenticated controller approval broker before exposing approvals. |
| Replay or substitute an external-effect payload | The Store ledger rejects digest substitution and lists stale prepared/applied effects; runtime use is not universal. | Require connector/step integration to call the ledger before claiming effect safety. |
| Fetch a changed or compromised dependency | Changed graph/content is rejected against locks; correctly locked malicious content is still accepted. | Add trusted-feed, advisory/SBOM/signature and reproducibility controls as separate evidence. |
| Replace or loosen the API token file | Startup reads one absolute file but does not itself reject symlinks, permissive mode, or ownership drift after read. | Deploy a service-owned regular non-symlink file at 0400/0600 and control rotation/restart. |

## Deployment requirements until the gaps close

1. Run only one mutually trusting tenant/security domain per agent VM. Before
   admitting hostile pipelines, separate controller and workloads with distinct
   identities or VMs and make controller state, secrets, journals, and event
   files inaccessible to the workload identity. A dedicated shared account is
   operational hygiene, not that isolation boundary.
2. Use HTTPS for non-loopback listeners. Treat the bearer as global operator
   authority and store it in a service-owned regular non-symlink file at mode
   0400/0600; the host does not enforce those file metadata properties, and the
   mode protects only other UIDs—not a workload sharing the service owner.
3. Keep runtime and maintenance database identities separate. The runtime role
   must remain `NOSUPERUSER NOBYPASSRLS`; keep the maintenance credential out of
   requests, workers, build profiles, logs, and child-readable files.
4. After applying requirement 1's identity/VM separation, keep controller
   credentials out of explicit build overlays, prepared SCM inputs,
   workload-readable filesystem paths, process state, and sockets. Cleared
   process environments do not protect those other channels.
5. Apply default-deny egress outside Fogell and allow only explicitly reviewed
   destinations. If no such enforcement exists, do not execute untrusted `sh`,
   `bat`, Git, SCM, package-manager, or plugin surfaces.
6. Treat current workers as trusted. Fences reject stale work but do not make a
   malicious current agent truthful.
7. Keep the durable state root and PostgreSQL within one tested backup/restore
   policy. Missing or ambiguous local execution evidence requires reconciliation,
   not reconstruction from optimistic assumptions. Treat `_secrets` as credential
   material and apply external stale-file cleanup until recovery owns it.
8. Do not expose Run.Host's filesystem approval inbox through the controller.
   Fresh hosted `input` remains unsupported unless provably deadline-bounded.

## Hard non-claims

- Fogell does **not** currently enforce no-egress networking.
- `sh`/`bat` are arbitrary agent code, not sandboxed Groovy.
- Process groups clean up ordinary descendants; they are not hostile isolation.
- Same-UID execution does not isolate secrets, even when files are mode 0600.
- Secret revocation is best-effort and lexical; abrupt host death and deletion
  failure can leave owner-only files in controller state.
- The local Controller/Run.Host event and journal seam is trusted same-UID IPC,
  not an authenticated hostile-workload boundary.
- Stale-agent fences do not prove a malicious current agent executed honestly.
- The global bearer token is not tenant identity, RBAC, or project authorization.
- Forced RLS limits the restricted runtime role after trusted code selects a
  transaction context; it is not identity and does not contain arbitrary SQL.
- Cleared child environments do not isolate readable files, sockets, network,
  kernel state, prepared SCM material, or explicitly supplied credentials.
- The effect checkpoint and retry ledgers are durable Store foundations, not
  evidence that every step/connector or controller policy consumes them.
- NuGet locks and source-cleared warm-cache restore do not establish package
  safety, vulnerability freshness, cold-airgap operation, or reproducibility.
- The runnable controller is single-node Linux and has no remote mTLS agent
  protocol, hostile-agent attestation, or authenticated approval broker.
- An accepted ADR records a decision or requirement; it is not evidence that the
  control is implemented.
- A differential receipt proves the bounded behavior it measured, not deployment
  isolation, egress policy, credentials safety, or unexecuted step internals.

## Review and change triggers

Review this model whenever a new registered step gains filesystem, process,
network, SCM, credential, artifact, or controller capability; when a `Value`
carrier, builtin, or step descriptor changes; when agent protocol or tenant
authentication changes; when a new compatibility exception crosses a workspace
boundary; when controller/workload OS identities change; or when state-root
protocol, ownership, mode, ACL, or namespace changes. Review it when FG-026/
FG-027 runtime integration, FG-062, FG-063, FG-070b, FG-080, FG-093, FG-221,
FG-222, FG-223, FG-224, or FG-225 changes state, and when a controller process-
launch profile, token/database capability, approval path, restore policy, or
egress rule changes. A board status or ADR edit alone does not close a risk: the
cited source and evidence must exist in the reviewed tree.
