# FG-015b — PR #110 exact-head cycle closure

Base/head reviewed: `80ab8a244a3f918bf9bd6c786764751ea349bf75`.

Oracle: pinned Jenkins `2.568.1` on HeMan. The direct probe sources and
Jenkins-only outputs are retained in this bundle.

## Measured boundary

- Explicit display and interpolation render direct self references as
  `[(this Collection)]` and `[self:(this Map)]`; nested wrappers around those
  direct-self values render the same markers.
- Longer list/map reference cycles raise `java.lang.StackOverflowError` during
  explicit display or interpolation.
- Passing any cyclic typed collection directly to hosted `echo`, `sh`, or
  `stash` raises `StackOverflowError` before the step dispatches. This includes
  direct-self values whose ordinary display has a marker. Interpolating the
  direct-self value first produces an ordinary String and remains legal.
- Same-reference cyclic list/map equality returns true. Distinct cyclic list or
  map equality, inequality, and `contains` raise `StackOverflowError` with
  `Error` ancestry; `Exception` cannot catch them.
- Cyclic `hashCode`, `toSet`, `unique`, and map-key insertion also raise
  `StackOverflowError`. Fogell implements the map-key path in this slice;
  unmodelled hash/set/unique APIs retain their explicit safe refusals.
- Direct cyclic `<=>` on the pinned CPS controller raises a catchable
  `GroovyRuntimeException`; the separately measured default `sort()` cycle path
  remains a `StackOverflowError` boundary and is not broadened here.

## Implementation boundary

- `Value.hasReferenceCycle` inspects the active reference path. Repeated acyclic
  aliases remain legal; direct and longer cycles are detected without host
  recursion.
- Display, equality, ordering, hosted-argument coercion, and map-key hashing use
  one typed `CyclicValue` fault family. All variants preserve
  `StackOverflowError` ancestry through script `try`/`catch`.
- Hosted positional and named values are checked after argument evaluation but
  before the single `Perform` boundary. No display placeholder can reach the
  walker or executor.
- Display-only direct-self markers are unchanged. A rendered GString is a plain
  string and can be dispatched.

## Caller audit

- Interpreter interpolation, string concatenation, explicit `toString`, `join`,
  equality/inequality, `contains`, switch matching, sort comparison, and map-key
  write/update paths now either return a complete value or raise `CyclicValue`.
- The one hosted `Perform` boundary checks every positional and named typed
  value before the walker can call `Value.toDisplay`; the walker’s descriptor,
  wrapper, executor, and failure-diagnostic conversions therefore cannot receive
  a cyclic value from hosted execution.
- `GString.renderValue` already uses `tryToDisplay` and converts a detected cycle
  to an explicit pre-effect refusal. Its internal binding-change comparison uses
  `tryEq` only to decide that a cyclic binding changed; it never dispatches it.
- Remaining `Value.toDisplay` uses are test formatting or refusal diagnostics.
  Truthiness reads only collection emptiness and performs no recursive walk.

## Proof

Two new tier-1 differential cases have empty-workspace parity:

- `fg015b-cyclic-hosted-coercion`
- `fg015b-cyclic-equality-hash`

The generated scorecard is 228/228. All 229 receipt seals verify, and corpus
classification remains admitted 197 / tier-3 30.

Bounded Qwen review of the exact staged functional diff returned `VERDICT:
PASS` with no action required. Focused cycle tests passed 14/14, and the exact
`scripts/build-and-test.sh` gate passed.
