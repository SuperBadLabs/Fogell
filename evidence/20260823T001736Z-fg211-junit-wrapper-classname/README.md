# FG-211 JUnit reached-owner testcase identity evidence

Status: **COMPLETE**. The retained `run/` is immutable.

This bundle retains six independent differential jobs against
Jenkins 2.568.1 and JUnit plugin `1416.vd753e036de5e`. Two arbitrary-root rows
prove direct classname-bearing pass/failure/error/skipped cases; the marker row
uses `classname=""` for its skipped case to prove attribute presence. The
owner-name and dotted-name rows each exercise both the document root and a direct
`testsuite` owner. Two terminal rows prove that unresolved reached identity is
not a zero result and that a valid sibling cannot hide it.

Retained tier-1 receipts:

- `fg211-junit-wrapper-classname-pass`
- `fg211-junit-wrapper-classname-markers`
- `fg211-junit-owner-name-fallback`
- `fg211-junit-dotted-name-fallback`
- `fg211-junit-missing-identity`
- `fg211-junit-invalid-sibling-poisoning`

Successful rows compare exact typed totals, continuation, terminal status,
ordered normalized output, and workspace state. Terminal rows compare FAILURE,
exact ordered output including the literal null-className line, successor
absence, and workspace state. Reported-reason presence remains
separately compared by the failure contract. Focused coverage pins the raw line,
dedicated missing-identity classification, absent totals, `allowEmptyResults`
resistance, and both invalid-sibling orders.

The collector reused the FG-177 immutable oracle pin, captured the exact
container and JUnit jar plus private bytecode for `TestResultSummary`,
`TestResult`, `SuiteResult`, and `CaseResult` before and after the run, copied
its inputs and tooling, verified every receipt seal and case digest, and bound
the retained directory with `MANIFEST.sha256`. The exact jar digest is
`7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142`.

All six receipts are PROVEN tier 1 and all six seals verify. The retained
manifest contains 28 rows and its digest is
`505c586c6296a73fab9c86c471501f00fd3d6e7b98fa0b163013660515828963`.
Oracle, private-bytecode, and container captures are byte-identical before and
after the run. Canonical cases and promoted receipts are byte-exact to the
retained inputs and receipts.

The bounded claim is exact unnamespaced reached-owner behavior. The document
root is reached initially; traversal then follows direct `testsuite` edges
only. Every reached owner owns its direct testcases, and each such case requires
identity from explicit testcase classname, then owner raw name, then a dotted
testcase name. Unresolved identity poisons the invocation.

An unretained oracle and source-bytecode control covers the adjacent
owner-marker rule: an arbitrary root's direct `<error>` synthesizes one failed
case (`1,1,0,0`, UNSTABLE), while a direct `<skipped>` sibling wins in either
order (`1,0,1,0`, SUCCESS); all counts are Integer and continuation runs.
Focused coverage holds this control. It is deliberately not a seventh receipt
claim from this bundle.

This is not general arbitrary-XML, namespace, class/package display, duration,
stdout/stderr, summary-object, raw-UI, numeric-arithmetic, or option parity.
Missing testcase names and empty-classname semantics beyond the retained
presence pin, exotic XML inputs, and arbitrary nested wrappers remain outside.

The collector remains reproducible from the repository root and refuses to
overwrite this completed `run/` directory.
