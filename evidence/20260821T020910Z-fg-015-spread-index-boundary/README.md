# FG-015 spread-index l-value boundary

Reviewed base: `b0d39449abe984cafd1f43a526f87a43c9c8b957`.

The exact-head review case was `rows*.child[0].name = 'x'`. Jenkins 2.568.1
was probed directly from HeMan before implementation, using the same pinned
controller recorded in `oracle-verification.txt`. `oracle-measurement.txt`
retains the normalized direct output.

## Measured outer writes

An index result is a fresh l-value receiver for an outer write. All measured
forms succeeded and persisted into only the selected first row:

| target | retained Jenkins output |
|---|---|
| `rows*.child[0].name = 'plain'` | `plain:plain:b` |
| `rows*.child[0]?.name = 'safe'` | `safe:safe:b` |
| `+=`, postfix `++`, postfix `--` on `.count` | `compound:3:10` |
| `rows*.children[0][1].name = 'nested'` | `nested:nested:b1` |
| `rows*.children[0].first().name = 'method'` | `method:method:b0` |

The safe-property spelling is not a null-skipping write. When the indexed
receiver is null, Jenkins raises a catchable `NullPointerException`, the catch
runs, and the two source values remain `null:b`.

Fogell had refused all of these as `unsupported_spread_assignment`, even
though the spread is a read that computes the indexed receiver. `EIndex` is
now an l-value receiver boundary just like `ECall`. Ordinary outer `EProp`
continues through the existing map assignment path. The bounded
`ESafeProp(EIndex(...), name)` path uses the same non-null mutation and
catchable null-fault behavior; no general list-index mutation was added.
The two focused cases are tier-1 `PROVEN` in `tier1-receipts/` and are promoted
to the standing differential suite.

## Measured direct projected-index writes

A direct index l-value is a different semantic family:

| target | Jenkins persistence |
|---|---|
| `rows*.child[0] = ...` | succeeds; temporary projection changes, sources stay `a:b` |
| `rows*.child[0] += ...` | succeeds; sources retain absent `extra` values |
| `rows*.count[0]++` / `--` | succeeds; source counts stay `1:10` |
| `rows*.children[0][1] = ...` | succeeds and persists as `nested-direct:b1` |

The last form persists because the first index selects a referenced source
list; the others write only the temporary projection. Correctly implementing
that distinction is list index-assignment work owned by FG-015b. This review
fix does not absorb it.

`assignmentTargetIsSpreadDerivedIndex` therefore recognizes only a direct
`EIndex` l-value whose receiver reaches `ESpreadProp` before a method-call
boundary. Plain, compound, increment, decrement, and nested-index targets get
the distinct stable `unsupported_spread_index_assignment` refusal before
workspace, target, RHS, catch, stage, or post effects. An index key containing
a spread read remains irrelevant to the write path. An outer property,
safe-property, or method-result write after the index remains admitted.
The interpreter carries the same defensive guard, so direct consumers cannot
fall back to RHS-only success.

## Proof surface

Focused AST tests pin the exact outer and direct target shapes so `EIndex`
cannot regress to either blanket recursion or blanket admission. Interpreter
tests pin persistent map mutation, compound/postfix forms, safe-null
catchability, and the before-RHS defensive refusal. Shared execution-preflight
tests cover stage bodies, closures, `when` expressions, preamble, epilogue,
workspace/effect plants, and every direct assignment form.

The direct-index case is retained as oracle evidence rather than promoted to
the compatibility score: Fogell intentionally refuses it before Jenkins runs,
so it is not a parity claim. The two admitted outer cases are sealed tier-1
receipts and exercise both persistence and catchability end to end.
