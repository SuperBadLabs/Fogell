# ADR 0010: Production CI first, Jenkins migration by explicit scope

Status: Accepted — owner decision, 2026-09-15.

## Decision

Fogell is a self-hosted CI engine with its own execution contract and a supported
Jenkins migration surface. Full or near-100% Jenkins compatibility is not a
product goal, release requirement, or implied eventual destination.

The owner accepted this direction after the production-hardening review of
PRs [447](https://github.com/SuperBadLabs/Fogell/pull/447),
[448](https://github.com/SuperBadLabs/Fogell/pull/448),
[449](https://github.com/SuperBadLabs/Fogell/pull/449), and
[450](https://github.com/SuperBadLabs/Fogell/pull/450). Those changes address
measured output exhaustion, log persistence latency, aggregate output bypasses,
and artifact publication/recovery limits. Their usefulness does not depend on
expanding the Groovy or Jenkins plugin surface. The controller runbook and
threat model still identify workspace, historical storage, and isolation limits.

This supersedes the board's Track 2 binding priority, ADR 0001's corpus-ranked
capability selection, and the corpus-ranked Track 4 strategy proposed in
[PR #446](https://github.com/SuperBadLabs/Fogell/pull/446). ADR 0009 is proposed
there, not present on the current main branch; its evidence and candidate work
can be reused after re-triage. Its ranking must not be adopted unchanged.

## Consequences

- The [production release gates](../PRODUCT_DIRECTION.md#release-gates) select
  the next work. Reliability, recoverability, resource bounds, and a deployable
  user workflow govern release readiness. No corpus target substitutes for them.
- The [Fogell pipeline contract](../architecture/PIPELINE_CONTRACT.md) governs
  new capabilities. Requirements are not implementation claims.
- Existing Jenkinsfile parsing and interpretation remain. This decision does
  not introduce another DSL, a general translator, or a static-IR rewrite.
- A versioned migration profile must distinguish proven behavior, accepted but
  unproven input, and unsupported input. Its machine-readable eligibility report
  is planned work. Current receipts and generated ledgers remain the evidence
  source until that profile is implemented.
- Existing proven migration behavior keeps its regression checks. Intentional
  divergence requires an explicit versioned contract decision and migration
  guidance; a green build with silently dropped behavior is still a defect.
- New compatibility work needs a named user workflow, a bounded implementation
  and maintenance cost, and measurable acceptance. A construct's frequency in
  the corpus is useful evidence, but not sufficient authorization to build it.
- Full Groovy/CPS emulation, arbitrary shared libraries, and binary Jenkins
  plugin compatibility are not required. A native integration may be selected
  on its own CI value. Expanding that surface remains a separate scoped decision.
- Existing receipt seals, corpus integrity checks, security controls, and
  failure tests remain intact. A changed product goal does not erase defects or
  turn unmeasured behavior into supported behavior.

## Falsifiable delivery criterion

The next release proposal must name a supported deployment, supported pipeline
profile, and passed production release gates. A proposal justified solely by
more accepted Jenkinsfiles or a larger compatibility percentage fails this
decision. A compatibility-only ticket without a named supported workflow is
unselected, regardless of its old corpus ranking.

Success is an operator installing Fogell, running the declared workload safely,
recovering it according to the contract, and maintaining bounded resources over
time. It is not a claim that every Jenkins workload can move.
