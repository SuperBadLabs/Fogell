# FG-015 spread-dot follow-on

Baseline: `0eedd5fccfcad62cbf03bbbdb94a4bd18d3cc63a`.
Oracle: Jenkins core 2.568.1 in the pinned `jenkins-lab` container; exact
verification is recorded in `oracle-verification.txt`.

## Direct boundary measurement before implementation

`fg015-spread-dot-boundary.Jenkinsfile` was run against Jenkins before parser
or interpreter code changed. Its retained receipt is
`boundary-receipts/fg015-spread-dot-boundary.receipt.txt`; the Fogell side
diverges there because the baseline erased `*.` to ordinary `EProp`.

| expression | Jenkins 2.568.1 result |
|---|---|
| `[[name: 'a'], [name: 'b']]*.name` | `[a, b]`, in source order |
| `[[name: 'a'], null, [name: 'b']]*.name` | `[a, b]`; a null element is omitted |
| `[[name: 'a'], [:], [name: null]]*.name` | `[a, null, null]`; null property results remain |
| `[[name: 'a'], 42]*.name` | catchable `MissingPropertyException` |
| `null*.name` | `null` |
| `groups*.child*.name` | `[a, b]`; the null intermediate is omitted |
| `map*.key` for a map without key `key` | `null`, the ordinary map-property result |
| `'ab'*.length` | catchable `MissingPropertyException`, the ordinary String-property result |
| `groups*.child?.name` | `[a, b]`; safe navigation immediately after a spread keeps projecting |

The implemented boundary is therefore exact and bounded: null receiver to null;
a list projects in order, omitting null receivers but retaining null property
values; a non-list uses unchanged ordinary property lookup; and a missing
property keeps the existing catchable fault. Projection refuses above the
interpreter's collection-iteration budget.

## Closure evidence

- `differential/receipts/fg015-spread-dot.receipt.txt` promotes the original
  retained failure to tier 1.
- `differential/receipts/fg015-spread-dot-boundary.receipt.txt` proves the
  complete measured boundary, including both catchability controls.
- Parser-shape tests keep `ESpreadProp`, `EProp`, and `ESafeProp` distinct.
- The [final gate bundle](../20260820T180445Z-fg-015-spread-dot) binds the
  exact candidate diff to the clean tree, corpus, build, and test results.

This closes FG-015's recovered spread-property construct. It does not claim
spread method calls or broader collection coercion. FG-015b remains separate:
`xs[index] = value` is mutation and shares no implementation root with this read.
