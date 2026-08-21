# FG-015b — PR #110 exact-head display and live-iteration closure

Base/head reviewed: `b785e88ba882282cdd6e788ca7be8f57c699d1f3`.

Oracle: pinned Jenkins `2.568.1` on HeMan. Direct probe sources and their
Jenkins-only output are retained in `probes/` and the two measurement logs.

## Measured cyclic-display boundary

- A longer list→map→list reference cycle raises `java.lang.StackOverflowError`
  through `println`, GString interpolation, and explicit `toString()`.
- `catch (Exception)` does not intercept that error. `catch (Error)` and
  `catch (Throwable)` do.
- Direct list and map self-references remain ordinary Groovy display forms:
  `(this Collection)` and `(this Map)`.
- Fogell represents the longer-cycle failure as typed `CyclicDisplay`, sharing
  the measured StackOverflowError ancestry without host recursion or reflection.

## Measured live-list traversal boundary

- `each`, `collect`, `findAll`, and `for` capture the current value before the
  body runs, observe writes to unvisited indexes, and do not rewrite already
  visited values.
- Jenkins CPS traversal is not fail-fast for the measured mutations. Appends are
  visited, removals shorten the remaining walk, and positive index extension
  visits inserted null slots and the appended value.
- Fogell now reads each successive index from the `VList` reference cell and
  rechecks its live length. Actual visits remain bounded, so a body that appends
  forever stops at the interpreter loop budget.
- The same shared walk covers `find`, `any`, and `every` while preserving their
  first-match/first-failure short-circuit boundaries.
- `remove` remains outside Fogell's bounded method vocabulary; the measurement
  records Jenkins behavior but this review fix does not add a new collection API.

## Proof

Two new differential cases are tier-1 `PROVEN`:

- `fg015b-cycle-display-ancestry`
- `fg015b-live-list-iteration`

The compatibility scorecard is 224/224, all 225 receipt seals verify, and the
corpus remains admitted 197 / tier-3 30. Focused tests cover typed catch ancestry,
all supported live closure consumers, `for`, current/unvisited/visited writes,
append, and null-extension behavior.
