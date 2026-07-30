# Architecture charter

Fogell is an end-to-end F# CI engine that accepts Jenkins pipelines where
compatibility is proven, reports unsupported behavior precisely, and executes
them with per-step durability.

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

## Compatibility contract

Behavior is classified as **proven compatible** (differential receipt exists),
**accepted** (parses and runs, parity unproven), or **rejected** (named error).
Binary Jenkins plugin compatibility is not promised; plugin *steps* are
implemented natively and ranked by corpus demand.

## Performance contract

A speed or capacity claim requires equivalent semantics, equivalent durability
guarantees, the same host and storage, and raw receipts. A number measured on a
non-durable path may not be compared against a durable one. A faster incorrect
result is a defect.
