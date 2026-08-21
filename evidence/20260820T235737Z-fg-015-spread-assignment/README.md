# FG-015 spread-property assignment boundary

Baseline and reviewed head before this follow-on:
`6f52d12916568ceb05b8c7d586763050cff7778d`.

Oracle: Jenkins core 2.568.1 in the pinned `jenkins-lab` container. The
fresh controller and image verification is in `oracle-verification.txt`.

## Direct Jenkins boundary

Seven uncaught minimal probes planted `before-assignment.txt`, performed the
assignment, then planted a result and `after-assignment.txt`. Jenkins accepted
every assignment target syntactically, ran the before marker, failed at the
assignment, and never ran the result/after effects.

A separate caught probe proved the unchanged values while retaining the exact
runtime classes and post-catch state:

| target | Jenkins 2.568.1 behavior |
|---|---|
| list of maps, `rows*.name = 'x'` | catchable `MissingPropertyException` on `java.util.ArrayList.name`; values remain `[a, b]` |
| list of objects | same catchable ArrayList `MissingPropertyException`; values remain `[a, b]` |
| null element | same catchable ArrayList `MissingPropertyException`; null-element projection remains `[a, b]` |
| missing-property element | same catchable ArrayList `MissingPropertyException`; the first map remains `a` |
| null receiver | catchable `NullPointerException`; receiver remains null |
| nested `groups*.child*.name = 'x'` | catchable ArrayList `MissingPropertyException`; nested values remain `[a, b]` |
| safe-after-spread `groups*.child?.name = 'x'` | catchable ArrayList `MissingPropertyException`; projected values remain `[a, b]` |

The caught Jenkins pipeline succeeds and writes `after-caught-assignment.txt`.
Its sealed receipt is
`catchability-receipts/catchability.receipt.txt`.
That receipt's top-level `DIVERGED (3)` compares Jenkins with Fogell; it is not
the Jenkins build result. Its sealed Jenkins side is `success` and contains all
seven caught output lines plus the post-catch workspace marker.

## Before and after Fogell

The retained preimplementation receipts show the review defect directly. Five
shapes are `DIVERGED (3)`: Jenkins fails after only the before marker while
Fogell succeeds and performs the result/after effects. `DIVERGED (3)` means
three compared difference dimensions (result, output, workspace), not three
durable runs. The object and missing-element probes happened to fail on both
engines for unrelated earlier evaluation/parser reasons and do not close the
silent-fallback class.

Spread-property assignment is a write boundary, not the read-only projection
slice implemented by FG-015. Fogell now refuses every assignment target whose
tree contains `ESpreadProp` during public execution preflight, before workspace
preparation or any stage/post/RHS effect, with the stable reason
`unsupported_spread_assignment`. A defensive interpreter guard prevents direct
consumers from reaching the former target-plus-RHS/no-write fallback.

The post-fix receipts are deliberately `NOT COMPARABLE`: Jenkins raises a
catchable runtime exception after earlier effects, whereas Fogell makes a
conservative named preflight refusal before all effects. Catchability parity is
not claimed. Implementing the two measured exception classes and precise
runtime timing remains separate work; silent success is closed here.

Coverage includes plain `=`, compound assignment, increment/decrement, and
spread nested below property, safe-property, and index target wrappers, plus
closures, preamble helpers, nested stages, nested step blocks, stage post, and
pipeline post. Spread reads and ordinary assignments remain admitted. FG-015b
list index-assignment semantics, spread method calls, and broader collection
coercion remain out of scope.

The [final gate bundle](../20260820T185232Z-fg-015-spread-assignment) binds the
exact incremental candidate to its base, tree, corpus, build, and test results.

## Exact-head review closure

The exact-head review found that the first preflight enumeration omitted
`when { expression { ... } }` source. The execution seam now collects every
such body recursively through `allOf`, `anyOf`, and `not` for every stage from
`Pipeline.flattenStages`, which includes top-level, sequentially nested, and
parallel stages. A planted `allOf` false sibling proves it cannot hide the
unsupported assignment or allow an earlier stage, guarded stage, post block,
or workspace preparation to run.

The same pass narrows the assignment predicate to the l-value receiver chain.
Parser-shape and preflight probes admit spread reads used only in positional or
named call arguments, a trailing closure, and an index key — including the
reviewer examples `foo(rows*.name).bar = 1` and
`xs[rows*.name[0]] = 'x'`. A method-call result is also a fresh l-value receiver;
only a new spread operator after that call is a spread write.
Nested closures containing an actual spread assignment remain visible through
the separate statement traversal. No Jenkins boundary receipt changed.

The [exact-head review gate bundle](../20260820T193817Z-fg-015-spread-assignment-review)
seals this incremental correction against its published base.

## Method-result receiver boundary

A subsequent exact-head review found the remaining over-refusal:
`rows*.child.first().name = 'x'`. The projection supplies the receiver of
`first()`; the actual assignment is an ordinary property write on the returned
child. `assignmentTargetContainsSpreadProperty` now stops at every `ECall`
result rather than recursing into its receiver or inputs.

Exact AST and preflight probes cover ordinary property, safe-property, and
index wrappers after `first()`, plus named and trailing-closure call forms. A
runtime probe proves the reviewer case mutates only the first returned child
(`a` to `x`, with the second remaining `b`). Direct property/safe/index
wrappers rooted in `ESpreadProp` still refuse, as does a new `*.` after the
method call. A real spread assignment inside a call closure is still caught by
the separate recursive statement traversal.

The [method-result review gate bundle](../20260820T195656Z-fg-015-spread-assignment-call-boundary)
seals this incremental correction against exact base `bcfbf9c`.

## Unanalyzable preamble boundary

The next exact-head review found a fail-open before the spread-write scanner:
a Jenkins-valid default-parameter helper makes the bounded Groovy analyzer
reject the complete captured preamble, and the scanner had treated that error
as proof that no spread assignment existed. A following top-level
`rows*.name = 'x'` therefore failed Jenkins before the pipeline but allowed
Fogell to run stage and workspace effects.

The shared execution preflight now refuses any nonblank captured preamble that
it cannot parse for execution analysis, before workspace preparation or effects,
with the stable `unsupported_preamble_analysis` reason. This is deliberately
conservative: Fogell has no complete statement splitter for Groovy, so it does
not guess around the unsupported declaration. Blank and fully analyzed
preambles remain admitted. The retained direct Jenkins measurement and the
incremental gate bundle are under
[`20260821T012214Z-fg-015-preamble-analysis`](../20260821T012214Z-fg-015-preamble-analysis).
