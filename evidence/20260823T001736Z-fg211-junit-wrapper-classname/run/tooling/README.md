> **Collector-time scaffold snapshot**
>
> The embedded status below describes the bundle when collection started,
> before the retained `run/` was published. After publication, the enclosing
> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained
> provenance, not the live status of the enclosing run.

# FG-211 JUnit reached-owner testcase identity evidence

Status: **UNEXECUTED SCAFFOLD**. No retained `run/` exists yet. Run the collector
only after the implementation handoff and authoritative local lanes are green.

This bundle is configured to retain six independent differential jobs against
Jenkins 2.568.1 and JUnit plugin `1416.vd753e036de5e`. Two arbitrary-root rows
prove direct classname-bearing pass/failure/error/skipped cases; the marker row
uses `classname=""` for its skipped case to prove attribute presence. The
owner-name and dotted-name rows each exercise both the document root and a direct
`testsuite` owner. Two terminal rows prove that unresolved reached identity is
not a zero result and that a valid sibling cannot hide it.

Planned tier-1 receipts:

- `fg211-junit-wrapper-classname-pass`
- `fg211-junit-wrapper-classname-markers`
- `fg211-junit-owner-name-fallback`
- `fg211-junit-dotted-name-fallback`
- `fg211-junit-missing-identity`
- `fg211-junit-invalid-sibling-poisoning`

Successful rows will compare exact typed totals, continuation, terminal status,
ordered normalized output, and workspace state. Terminal rows will compare
FAILURE, exact ordered output including the literal null-className line,
successor absence, and workspace state. Reported-reason presence remains
separately compared by the failure contract. Focused coverage pins the raw line,
dedicated missing-identity classification, absent totals, `allowEmptyResults`
resistance, and both invalid-sibling orders.

The collector will reuse the FG-177 immutable oracle pin, capture the exact
container and JUnit jar plus private bytecode for `TestResultSummary`,
`TestResult`, `SuiteResult`, and `CaseResult` before and after the run, copy its
inputs and tooling, verify every receipt seal and case digest, and bind the
retained directory with `MANIFEST.sha256`. The expected jar digest is
`7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142`.

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

Run `bash evidence/20260823T001736Z-fg211-junit-wrapper-classname/collect.sh`
from the repository root. The collector refuses to overwrite a retained
`run/` directory.
