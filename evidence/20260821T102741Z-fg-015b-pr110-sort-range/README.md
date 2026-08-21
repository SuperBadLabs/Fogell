# FG-015b — PR #110 sort identity and IntRange closure

Base/head reviewed: `22fff89040aaef3ae06e215fb5ed6e6310b383b7`.

Oracle: pinned Jenkins `2.568.1` on HeMan. Direct probe sources and Jenkins-only
outputs are retained under `probes/` and in the measurement logs.

## List method identity boundary

- No-argument `List.sort()` sorts the receiver in place and returns that same
  identity. The source, an alias, and the returned value all observe later writes.
- A default comparison cycle raises `StackOverflowError` before receiver contents
  are replaced.
- `sort(false)` and `sort(true)` are sandbox-rejected in the pinned controller.
  Comparator/key closures encounter Jenkins CPS/sandbox mismatch behavior; Fogell
  now refuses those overloads explicitly instead of ignoring the closure.
- `each` returns the source identity. `findAll`, `reverse`, and `collect` return
  fresh mutable lists. `find`, `any`, `every`, `first`, and `last` return scalars
  or elements rather than list identities.

## IntRange boundary

- `IntRange` is list-like for indexed reads (including valid negative indexes),
  equality with an ordinary list, display, ascending/descending traversal, and
  the fresh list results of `reverse()` and `collect()`.
- Positive out-of-range reads return null; too-negative reads raise catchable
  `ArrayIndexOutOfBoundsException` ancestry.
- Plain, compound, postfix, and aliased index replacement all raise catchable
  `UnsupportedOperationException` ancestry without mutation. Plain and compound
  RHS effects occur before the failed write; postfix has no RHS.
- No-argument range `sort()` reaches the same immutable-operation fault. The
  boolean overload remains at the existing sandbox/model refusal boundary.
- Fogell now represents ranges as distinct immutable `VRange` values. This keeps
  list-index mutation scoped to actual `VList` identities.

## Proof

Two new differential cases are tier-1 `PROVEN`:

- `fg015b-sort-receiver-identity`
- `fg015b-intrange-readonly`

The compatibility scorecard is 226/226, all 227 receipt seals verify, and the
corpus remains admitted 197 / tier-3 30.

Bounded Qwen review of the exact staged functional diff returned `VERDICT:
PASS`. Its three follow-ups were verification notes: the scorecard generator
intentionally reports the 226/226 case population while seal verification is a
separate gate; the sealed IntRange receipt proves exact display; and all
list-like range operations receive a temporary read-only cell whose results
cannot mutate the `VRange` value. Focused tests passed 4/4 and the exact
`scripts/build-and-test.sh` gate passed.
