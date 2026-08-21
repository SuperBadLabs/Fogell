# FG-015b — PR #110 cycle-detector complexity closure

Base/head reviewed: `f7ff114a047adae18d6244c1ee828aacb44d952f`.

This is a review hardening slice. It adds no Groovy construct and changes no
Jenkins compatibility verdict.

## Complexity boundary

- `Value.scanReferenceCycles` is an iterative DFS with separate reference-
  identity sets for the active ancestry and completed acyclic nodes. Each
  distinct list/map reference is entered once and each outgoing edge is charged
  once: O(V+E). A later sibling alias is skipped in O(1), while a reference met
  on the active path remains a real cycle.
- The scanner exposes visit counts for load-bearing tests. A depth-30 binary
  shared DAG (over one billion expanded leaf paths) visits 31 references and 61
  edges, rather than expanding those paths. A 50,000-reference chain completes
  without host-stack recursion.
- Direct list cycles and mixed list/map cycles remain detected. Repeated acyclic
  aliases remain legal.

## Caller audit

- `Value.tryEq` and `Value.tryCompare` had the analogous repeated-pair expansion
  on equal shared DAGs. They now memoize only reference pairs that have fully
  completed with equality/order zero. Active-path repeats still report the
  existing equality/ordering cycle faults; unequal leaves still return false or
  their nonzero order. Tests pin equal, unequal, direct-cycle and mixed-cycle
  pairs.
- Exact collection display necessarily remains proportional to the expanded
  output Jenkins asks for. It cannot be made O(V+E) without changing the text.
  This slice instead removes an accidental display from the hosted validation
  path: collection values use bounded type markers in refusal diagnostics.
  Scalar diagnostic spellings are unchanged.
- A shared DAG passed to invalid `deleteDir` now crosses the cycle scan and
  reaches hosted validation promptly. The validator returns its stable refusal
  without recording an effect and without constructing exponential text.
- The pre-existing recursive-depth residual for equality and exact display is
  still documented under FG-191. This slice closes the new shared-DAG
  exponential revisit without broadening into a separate value-walker rewrite.

## Proof

- Focused suites: Fogell.Groovy 169/169 and Fogell.Differential 114/114.
- Adversarial scanner tests pin exact reference/edge counts, 50,000-node
  stack-safety, sibling aliases, direct cycles, and mixed list/map cycles.
- Adversarial equality/ordering tests pin equal and unequal shared DAGs plus
  direct and mixed cycle faults.
- Bounded Qwen review of the exact functional diff returned `VERDICT: PASS`.
- Corpus remains 228/228 verified, admitted 197 / tier-3 30; scorecard remains
  228/228 proven cases and all 229 receipt seals verify.
- The exact `scripts/build-and-test.sh` result is retained in `full-gate.log`.
