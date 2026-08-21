# FG-015 method-call write boundary

Reviewed base: `e9c618bad1f638d2b5f7ed73f6f91ff38b4517fc`.

The two PR #106 P1 cases were measured directly on the pinned Jenkins 2.568.1
controller before implementation. `oracle-verification.txt` records the oracle
identity; the controller and image IDs were rechecked immediately after the
probe and remained `5dc7acbe...` and `7a193ff7...`.
`pre-fix-baseline.receipt.txt` deliberately retains the three-attempt
`DIVERGED` baseline: Jenkins' exact normalized output beside the old Fogell
failure. It is measurement that motivated the fix, not a parity claim. The
post-fix proof is the standing sealed tier-1 receipt
`differential/receipts/fg015-spread-index-outer-writes.receipt.txt`.

## Safe-property assignment after a method call

Jenkins measured behavior:

| target | result |
|---|---|
| `rows*.child.first()?.name = 'safe'` | mutates only the returned first child map |
| compound / postfix writes on the same receiver | persist on that first map |
| null receiver | catchable `NullPointerException`, no mutation |
| scalar receiver | catchable `MissingPropertyException`, no mutation |

Receiver and RHS effects both run before the runtime fault: the retained output
orders `safe-null-target`, `safe-null-rhs`, then the catch, and likewise orders
the missing-property RHS before its catch.

Fogell's generic assignment fallback had evaluated the target and RHS but did
not write an `ESafeProp` target. The interpreter now treats every non-null safe
property write like `EProp`, uses the existing reference-map mutation path, and
raises a distinct typed null-assignment fault. Narrow NPE and missing-property
catches therefore agree with the measured classes. Plain, compound, increment,
and decrement forms all lower through the same `SAssign` path and are pinned.
An uncaught null fault while rendering an interpolated step argument crosses
the GString evaluation boundary as a hard `UnsupportedExpression`; a script's
own `try/catch` still runs inside the expression before that boundary. This is
deliberately not described as a catch seam outside the script interpreter.

Every other generic assignment fallback was audited at the same time. A direct
list-index receiver gets the named `unsupported_list_index_assignment` refusal
before its RHS, a syntactically unsupported target gets
`unsupported_assignment_target` before its RHS, and non-null unsupported
property/index receivers fault catchably rather than inheriting lax read
semantics and succeeding without a write.

## Direct index after a spread-derived method result

Jenkins also measured:

| target | result |
|---|---|
| `rows*.children.first()[0] = [name: 'list-x']` | persists into the first source list (`list-x:a1:b0`) |
| null method result followed by `[0] = ...` | catchable `NullPointerException` |
| `rows*.holder.first()['slot'] = 'map-x'` | persists into the first source map (`map-x:b`) |
| ordinary map index control | persists (`ordinary-x`) |

The first spelling is list-index mutation owned by FG-015b. Static analysis
cannot determine whether the method result is the measured list or map, so a
direct `EIndex` l-value now carries spread-read provenance through method-call
receivers and receives the existing stable
`unsupported_spread_index_assignment` preflight refusal before workspace or
effects. The map variant is a deliberate, documented conservative over-refusal.

This traversal does not inspect free-call inputs, method arguments, named
arguments, trailing closures, or index keys. It also applies only when the
assignment target itself is `EIndex`; outer property and safe-property writes
on method/index results remain admitted and are receipt-proven.

## Proof surface

`fg015-spread-index-outer-writes.Jenkinsfile` now covers safe method-result map
writes plus null and scalar catchability as a standing tier-1 receipt. Focused
AST tests pin direct-index provenance across method calls without expanding the
actual spread-write path. Interpreter tests pin mutation, typed catchability,
receiver/RHS order, and both no-RHS refusal fallbacks. Execution-preflight tests
cover the exact reviewer list target, compound/inc/dec variants, the conservative
map target, and workspace/RHS/later/post effect plants.
