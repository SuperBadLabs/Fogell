# ADR 0007: PostgreSQL is the controller source of truth

Status: Accepted

Build, node, attempt, event and outbox records commit in one transaction.
Agent-local state is a crash-recovery journal, never global truth.

**Rationale.** Forge's file-and-`.lock` model cannot express fencing, and its
resume is at-least-once (ADR 0003). McLoving's Postgres design was measured on
luigi and the economics are settled: a five-row admission transaction costs the
same **one fsync** as a single insert (1.02 syncs/txn), and Postgres
group-commits automatically — **0.048 syncs/txn at 64 concurrent clients**,
166 → 3,680 tps. Durability is therefore not the bottleneck it is in Jenkins.

**Required properties**, each with a real-PostgreSQL test:
- atomic, idempotent admission keyed by project + idempotency key
- exactly one winner among concurrent terminal publishers
- attempt fences and a controller restore epoch; a pre-restore agent cannot
  publish or renew
- retries never rewrite an attempt; one child attempt with an immutable link
- external effects through a fenced, immutable-payload checkpoint ledger with an
  explicit `uncertain` state
- forced row-level security per tenant with a transaction-local organization
  setting; absent context exposes no rows
- migrations under an advisory lock with a version ledger

SQLite is permitted for single-user evaluation only and must be stated as such.
