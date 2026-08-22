# FG-209 JUnit testcase-child authority evidence

This bundle retains four independent differential jobs against Jenkins 2.568.1
and JUnit plugin `1416.vd753e036de5e`. Each report contains the same four named
testcase children: one pass, one failure, one error, and one skip. The enclosing
suite attributes are respectively missing, nonnumeric, negative, or numeric but
internally inconsistent. In every row the pinned oracle returns
`4,2,1,1`, exposes `passCount` as `java.lang.Integer`, continues the stage, and
finishes UNSTABLE.

The collector reuses the FG-177 immutable oracle pin, captures the exact
container and JUnit jar plus private bytecode for `TestResultSummary`,
`TestResult`, `SuiteResult`, and `CaseResult` before and after the run, copies
its inputs and tooling, verifies every receipt seal and case digest, and binds
the retained directory with `MANIFEST.sha256`. The expected jar digest is
`7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142`.

The bounded claim is child authority, not arbitrary XML acceptance. An
unretained read-only oracle matrix also found that nested suites recurse. A
suite-level direct `<error>` synthesizes one case: error-only classifies it as
failed, while a direct `<skipped>` sibling classifies it as skipped regardless of
element order; skipped-only synthesizes nothing. A testcase containing both
`<skipped>` and `<failure>` is also skipped regardless of element order. Those
oracle edges are focused-test inputs, not receipt claims from this bundle.

At collection time, well-formed reports with no recognized result remained
separate. The same unretained matrix found that empty suites fail with
`None of the test reports contained any result` regardless of missing,
nonnumeric, negative, consistent, or inconsistent aggregate attributes. A direct
suite `<skipped>` alone takes that zero-result path, while a direct suite
`<error>` alone synthesizes a failed case. A zero-result sibling does not poison
a valid sibling. FG-209 did not promote the terminal zero-result path to a
retained parity case; FG-210 later closed its default and typed
`allowEmptyResults` contract. Arbitrary-root direct-testcase/classname behavior
remains a separate unretained FG-177 ingest edge.

Run `bash evidence/20260822T123400Z-fg209-junit-child-authority/collect.sh`
from the repository root. The collector refuses to overwrite its retained
`run/` directory.
