# Architecture charter

Fogell is a self-hosted F# CI engine with its own
[pipeline contract](PIPELINE_CONTRACT.md) and an explicit Jenkins migration
surface. [ADR 0010](../adr/0010-production-first-ci.md) makes production
readiness the release priority. Full Jenkins compatibility is not a goal.
Implemented guarantees and remaining work are distinguished in the
[release gates](../PRODUCT_DIRECTION.md).

## Non-negotiable boundaries

- F# owns parsing, interpretation, scheduling, durable state, execution,
  cancellation, and recovery. There is no second runtime and no lowering
  boundary.
- The pipeline AST is interpreted, not lowered to a static IR.
- Unsupported behavior fails closed with a named error code and a source
  position.
- Durability is per-step and exactly-once on resume, or the limitation is
  stated explicitly.
- A compatibility claim requires differential evidence against a pinned
  Jenkins version and a hash-pinned corpus.
- No scalar compatibility percentage is ever published. Parse acceptance,
  controller acceptance, execution, and semantic parity are four claims.
- Incomplete pattern matches are build errors (FS0025/FS0026).
- Security and deployment claims must remain within the implemented controls,
  residuals, and hard non-claims in the
  [current-tree threat model](../THREAT_MODEL.md).

## Compatibility contract

Behavior is classified as **proven compatible** (differential receipt exists),
**accepted** (parses and runs, parity unproven), or **rejected** (named error).
Binary Jenkins plugin compatibility is not promised; plugin *steps* are
implemented natively when a supported user workflow justifies them. Corpus
coverage informs migration cost; it does not determine release readiness.
Existing proven behavior keeps its regression checks. Intentional future
divergence requires an explicit contract/version change and migration guidance.

## Performance contract

A speed or capacity claim requires equivalent semantics, equivalent durability
guarantees, the same host and storage, and raw receipts. A number measured on a
non-durable path may not be compared against a durable one. A faster incorrect
result is a defect.
