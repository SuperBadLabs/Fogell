# FG-015b — PR #110 cycle ordering and scalar-index review closure

Base/head reviewed: `a549f48d91b2fe2f58bf306161d2e95a49aa57a3`.

Oracle: pinned Jenkins `2.568.1` on HeMan. The direct probe sources are under
`probes/`; their Jenkins-only output is retained in the three
`jenkins-2.568.1-*.log` files.

## Measured ordering boundary

- Acyclic integer sort remains ordinary ascending order.
- Sorting a one-element cyclic list or two top-level aliases of the same cyclic
  list completes. Two distinct self-cycles raise `java.lang.StackOverflowError`.
- Two distinct wrapper lists containing the same cyclic list also raise
  `StackOverflowError`; identity therefore short-circuits only at the comparator
  entry, not at arbitrary recursive depth.
- Distinct list→map→same-list cycles raise the same error. `catch (Throwable)`
  and `catch (Error)` intercept it; `catch (Exception)` does not.
- Jenkins' sandbox rejects `min()` and `max()` before comparison. `unique(false)`
  chases distinct cycles into `StackOverflowError`. Fogell does not model those
  three methods: their existing sandbox/model denials are explicit and cannot
  enter a host comparer. This review slice does not claim `unique` parity.
- Repository audit found one generic runtime comparer reachable from `Value`:
  the `sort()` arm. It now routes exclusively through `Value.tryCompare`; no bare
  `compare`, `sortWith compare`, `min`, `max`, or `distinct` path remains.

## Measured scalar-index boundary

- Plain scalar index assignment evaluates receiver, index, and RHS, then Jenkins'
  sandbox rejects the write as a catchable `RejectedAccessException`.
- For an in-range String index, compound updates evaluate receiver/index once,
  read the character, evaluate the RHS, apply the operator, and then encounter
  the same catchable write rejection. Postfix updates read/apply before rejection
  and have no RHS. A valid negative index follows the same path.
- A positive String index beyond the end raises catchable
  `StringIndexOutOfBoundsException`; a too-negative String index raises catchable
  `ArrayIndexOutOfBoundsException`. Both happen before RHS evaluation.
- A non-integer String key, Integer receiver, Boolean receiver, null receiver,
  and non-integer List key all fail while reading, before a compound RHS. The
  sandbox cases use `RejectedAccessException`; null uses `NullPointerException`.
- Existing Map and typed-integer List update behavior is unchanged. No scalar
  receiver is mutated.

## Implementation and proof

`Value.tryCompare` preserves the previous acyclic value ordering but carries a
typed cycle/unorderable result. `sort()` translates a repeated comparison pair to
a survivable `CyclicOrderingComparison`, including unwrapping FSharp.Core's
`Array.Sort` comparator wrapper; scripted catch ancestry publishes the measured
`StackOverflowError` boundary without process recursion.

Scalar compound/postfix paths now share the measured String read split and a
catchable `RejectedIndexOperation` for sandbox read/operator/write phases. Plain
index writes retain RHS-before-write ordering. Positive and negative String bounds
retain distinct exception classes.

Two focused differential cases are tier-1 `PROVEN`, including workspace plants
that make reached and suppressed RHS effects load-bearing:

- `fg015b-cycle-sort-safety`
- `fg015b-scalar-index-timing`

Focused unit plants cover typed catch ancestry, aliases, nested/mixed cycles,
unmodelled ordering entry points, scalar read/write/operator timing, negative
indexes, and unchanged map/list controls. The corpus is expected to remain at
admitted 197 / tier-3 30 because this is an interpreter-only closure.
