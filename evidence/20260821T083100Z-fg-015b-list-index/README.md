# FG-015b — typed list-index mutation closure

Base: `11ca7b315cdb0ec5b5ca962f0bab3eec28fee946` (`origin/main`, exact
post-PR109 merge used for the final candidate and verification)

The Jenkins semantic matrix was measured before production edits against the
then-current base `bac2c28da56406782608e7bf96fe5cd993ebb031`. The implementation
was subsequently rebased onto the exact base above. The three FG-015b cases and
the three newly merged FG-177 GenuineNull cases were rerun directly against the
same pinned Jenkins controller after that rebase; focused tests and the full gate
were also rerun from the rebased tree.

Oracle: Jenkins `2.568.1` at the pinned HeMan lab. `measurements/oracle-version.txt`
records the live `X-Jenkins` header. The full direct-Jenkins output is retained in
`measurements/jenkins-2.568.1-full-matrix.txt`; every source used to produce it is
under `probes/` with the direct oracle runner.

## Measured boundary

- A Groovy list is a reference object. Direct aliases, closure arguments, nested
  selection and method results share mutations.
- A spread projection is a new list. Replacing a projected slot is temporary;
  selecting a source list through a second index or `first()` retains the source
  identity and persists.
- Plain assignment returns/evaluates the RHS. Compound and postfix updates
  evaluate receiver and index once. A too-negative plain write evaluates the RHS
  and then throws; compound/inc/dec throw while reading the old value, before an
  update RHS.
- Negative in-range indexes select from the end. Index `0` grows an empty list,
  index `size` appends, and a larger positive index fills intermediate slots with
  null. Too-negative indexes raise catchable
  `ArrayIndexOutOfBoundsException`.
- A compound integer update at `size` or beyond evaluates its RHS, then raises a
  catchable `NullPointerException` because the old value is null; postfix
  increment/decrement raises the same class without a RHS. Null plus a string is
  the measured exception: `xs[size] += 'x'` succeeds and appends `nullx`.
- Null and scalar receivers fault catchably. Jenkins' sandbox rejects null,
  string and range list keys after the plain RHS; those shapes remain outside the
  typed-integer implementation slice. Existing map index mutation remains valid.
- A direct self-cycle displays `(this Collection)` and same-reference equality is
  true. Distinct self-cycles and a list→map→same-list display end in catchable
  `StackOverflowError` on Jenkins; Fogell translates cycle detection to a
  survivable fault rather than recursing in the host process.

## Implemented slice

`VList` now owns a reference cell. Integer list reads/writes implement the measured
normalisation and extension rules with the existing iteration budget as the
allocation bound. Index `+=`/`-=`/`*=`/`/=` and postfix `++`/`--` retain distinct
AST nodes, so the parser no longer lowers them into a duplicated l-value.

Direct spread-derived index targets are admitted and resolved from their runtime
identity. This does **not** admit an actual spread-property l-value: those retain
the separate pre-effect `unsupported_spread_assignment` contract. Map writers are
unchanged. Non-integer list keys refuse explicitly rather than reporting an RHS-only
success.

## Proof

The three retained standing cases are tier-1 `PROVEN`: result, ordered output and
canonical workspace hash match Jenkins. Their sealed receipts are both in
`differential/receipts/` and copied under `measurements/`:

- `fg015b-list-index-identity`
- `fg015b-list-index-boundaries`
- `fg015b-list-index-self-cycle`

Focused AST/interpreter/preflight/cycle tests cover the temporary-versus-persistent
projection split, alias visibility, update order, catch timing, positive extension,
actual spread-assignment refusal, and host-safe cycles. Corpus score/admission is
expected to remain unchanged because this is an interpreter value-model change,
not a grammar admission change.

Generated closeout artifacts (`candidate.diff`, exact status/base/tree records,
gate outputs, Qwen review and `SHA256SUMS`) are added only after the candidate is
otherwise complete, so their provenance describes the exact reviewed tree.

On the rebased baseline, the three FG-015b cases raise the differential suite
from 217 to 220 expected/proven cases. Receipt sealing verifies 221 receipts in
total, because the repository intentionally contains one receipt outside the
expected-case denominator. Corpus admission remains 197 with 30 tier-3 rejects.
