# FG-210 JUnit zero-result and allowEmptyResults evidence

This bundle retains six independent differential jobs against Jenkins 2.568.1
and JUnit plugin `1416.vd753e036de5e`. The default and explicit-false rows prove
that a matched, well-formed aggregate containing no recognized result is
terminal. The no-match default row proves the distinct missing-report terminal
path. Literal `allowEmptyResults: true` permits both conditions, returns
`0,0,0,0` with all four values as `java.lang.Integer`, emits the pinned notices,
continues, and finishes SUCCESS. The sibling row proves that a zero-result report
does not poison a valid passing report, returning `1,0,0,1,Integer` and SUCCESS.

The collector reuses the FG-177 immutable oracle pin, captures the exact
container and JUnit jar plus private bytecode for `TestResultSummary`,
`TestResult`, `SuiteResult`, and `CaseResult` before and after the run, copies
its inputs and tooling, verifies every receipt seal and case digest, and binds
the retained directory with `MANIFEST.sha256`. The expected jar digest is
`7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142`.

The bounded claim is the aggregate-zero/default-plus-option contract. An
unretained read-only oracle matrix additionally found that multiple zero-result
files still produce one terminal invocation, both JUnit instability-suppression
flags cannot suppress that terminal failure, and direct suite `<failure>` or
`<skipped>` alone synthesizes no result. Direct suite `<error>` synthesis and
zero-result sibling order are adjacent FG-209 observations, not receipt claims
from this bundle.

This is not a general arbitrary-XML or namespace claim. Unretained probes found
that an arbitrary root with a direct testcase carrying `classname` is counted,
while the same shape without `classname` faults on the pinned plugin. That
wrapper/classname surface remains an FG-177 ingest residual. JUnit duration,
object/UI surface, numeric-width arithmetic, coercion beyond exact typed/literal
booleans, and other options also remain outside FG-210.

The pinned plugin has a separate path after explicit
`currentBuild.result = 'FAILURE'`. Fogell does not support that assignment, and
a failed non-failFast parallel sibling was measured not to be equivalent. That
explicit preexisting-result path is therefore unclaimed and unretained.

Failure receipts compare the presence of a terminal reason rather than exact
plugin diagnostic wording. The exact pinned diagnostics are held by focused
coverage and the unretained oracle observations. Successful allow-empty rows do
compare their emitted notices as ordinary ordered output.

Run `bash evidence/20260822T134946Z-fg210-junit-zero-result/collect.sh` from the
repository root. The collector refuses to overwrite its retained `run/`
directory.
