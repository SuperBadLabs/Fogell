# Fogell pipeline contract

Status: Accepted product requirements under
[ADR 0010](../adr/0010-production-first-ci.md). This document is not a claim that
all requirements are implemented, nor a declaration of a released v1 profile.
The [production release gates](../PRODUCT_DIRECTION.md#release-gates) track the
remaining evidence. Current behavior and deployment limits remain documented in
the [controller runbook](../runbooks/controller-host.md) and
[threat model](../THREAT_MODEL.md).

## Authority and representation

Fogell defines its execution, resource, and recovery contract. Jenkins is an
external reference for explicitly promised migration behavior. A Jenkins quirk
does not automatically become a requirement for every future Fogell capability.

The current frontend parses Jenkinsfile syntax and interprets its AST directly.
Keep that implementation while defining the supported profile. A new native
syntax, profile selector, general translator, or alternative execution engine
does not exist merely because this document names a contract. Any future
authoring format must use the same execution and recovery guarantees and needs
a separate scoped design.

## Required semantics for the initial supported profile

| Area | Contract requirement | Current evidence or remaining release work |
| --- | --- | --- |
| Admission | Identify the supported constructs and required capabilities. Reject statically identifiable unsupported behavior with an actionable diagnostic before its effects. Runtime-dependent refusals must remain explicit and must never silently skip behavior. | Existing admission and runtime refusal checks remain. A versioned profile and complete eligibility report are release work; parser acceptance alone is insufficient. |
| Steps and concurrency | Declare step order, branch joining, failure precedence, and cancellation propagation. Unsupported agent selections or integrations must not silently fall back to another execution mode. | Existing tests and migration receipts prove particular paths. The pilot must enumerate its exact offered agent and step surface. |
| Resources | Operator policy governs time, output, artifacts, workspace, and storage admission. Parallel branches share the appropriate attempt/build budgets; pipeline bindings cannot increase trusted operator limits. | Output and artifact controls have landed. Workspace/stash enforcement and total historical storage remain open release gates. These budgets do not imply an exact process-memory bound. |
| Effects and recovery | Keep durable build/attempt truth and explicit uncertainty. Retry creates a distinct attempt; resume must not replay an uncertain external effect as if it were known safe. | Existing journals, fences, and reconciliation are the foundation. Exactly-once is claimed only for a specifically proven effect boundary, never arbitrary shell or network effects. Backup/restore and operator procedures require the release drills. |
| Logs and artifacts | Preserve admitted safe output and completed artifacts according to the configured policy. Report quota/refusal causes accurately. Interrupted publication must not expose a partial file as a completed artifact. | The controller runbook documents landed behavior and residuals. Cross-build retention and operator recovery completeness remain release work. |
| Secrets and identity | State which identities may run which workloads, where secrets can be read, and which external isolation controls are mandatory. | The current threat model governs; same-UID execution and a global bearer token do not establish hostile multi-tenancy or per-user authorization. |
| Evolution | Name a profile/version for every released support promise. Changes that invalidate it need a new version or an explicit migration/deprecation process. | No released profile/version mechanism is introduced here. Existing proven behavior remains protected by regression tests until a versioned change is designed and validated. |

## Jenkins migration scope

The first profile should describe exact supported forms and options for the
workflows selected for the pilot, with source locations and dispositions for
unsupported constructs. Candidate areas are sequential stages, shell commands,
logs and artifacts, environment/credential bindings, bounded timeout/retry,
and parallel execution. This is a candidate inventory, not blanket support for
every Jenkins spelling or combination in those areas.

For each selected form, record the Fogell semantics, required worker/tool
capabilities, proven Jenkins version/plugin context, receipt, known differences,
and an operator action for a refused migration. Existing generated ledgers and
receipts are the starting evidence; do not hand-edit their dispositions.

General Groovy/CPS, arbitrary shared libraries, binary plugins, arbitrary agent
labels, and a complete Jenkins UI/API replacement are outside the initial
promise. A needed subset can be proposed with a named workflow and measured
acceptance. An existing supported form is not removed by this scope decision.

If a workflow cannot be represented safely, the migration outcome must say so
and leave the operator a documented path to keep running it on Jenkins. There
is no claim of an implemented automatic migration or rollback service.
