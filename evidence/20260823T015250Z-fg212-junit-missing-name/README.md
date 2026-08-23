# FG-212 JUnit missing testcase name evidence

Status: **COMPLETE**. The retained `run/` is immutable.

This bundle retains three independent differential jobs against Jenkins 2.568.1
and JUnit plugin `1416.vd753e036de5e`:

- `fg212-junit-missing-name-classname-markers`
- `fg212-junit-missing-name-owner-fallback`
- `fg212-junit-missing-name-terminal`

The classname-marker row gives all four direct cases no testcase `name` and
requires exact pass/failure/error/skipped totals, including an explicitly empty
`classname=""` control. The owner-name row gives missing-name cases to both an
arbitrary root and a direct `testsuite` owner. The terminal row has no testcase
name, testcase classname, or owner name and sets literal
`allowEmptyResults: true`; it proves the visible normalized envelope
`Failed to read ${WORKSPACE}/reports/result.xml`, plus FAILURE, reported-reason
presence, successor absence, and workspace parity.

Successful rows compare exact typed totals, continuation, terminal status,
ordered normalized output, and workspace state. The exact oracle root cause
`Cannot invoke "String.contains(java.lang.CharSequence)" because "nameAttr" is null`
is the structured Fogell `Diagnostic` and live-oracle/focused-test pin. The
terminal differential receipt does not compare that NPE text. Focused tests also
pin absent-versus-empty name, the distinct FG-211 and FG-212 diagnostics,
construction-before-tally precedence, absent partial totals, and
`allowEmptyResults` resistance. Follow-up oracle and private-bytecode inspection
settled the mixed-fault boundary not exercised by the three canonical inputs:
distinct report paths are globally sorted; missing-name and unrecovered read
faults are immediate, so the first one in the construction pass wins; missing
identity is deferred until tally after all reports construct. A later missing
name therefore outranks an earlier unresolved identity, while two immediate
problems retain sorted-file order.

All 3/3 receipts are PROVEN tier 1 and all 3/3 seals verify. The retained
manifest contains 22 rows and its digest is
`12f2b2ca2112632c454e3e80941dbf5564e19a2ba1f15921b73de7c2efaf58f6`.
Oracle, private-bytecode, and controller-container captures are byte-identical
before and after the run. Canonical cases and promoted receipts are byte-exact
to the retained inputs and receipts.

The collector reused the FG-177 immutable oracle pin. It captured the exact
container and JUnit jar plus private bytecode for `TestResultSummary`,
`TestResult`, `SuiteResult`, and `CaseResult` before and after the run, copies
its inputs and tooling, verified every receipt seal and case digest, and bound
the retained directory with `MANIFEST.sha256`. The required jar digest is
`7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142`.

The bounded claim is exact unnamespaced reached-owner behavior. The document
root is reached initially; traversal then follows direct `testsuite` edges only
and processes those children before their owner. A missing testcase name is
accepted when explicit testcase classname or owner raw name supplies the class
fallback. Without either fallback it terminates before marker classification.

This is not general arbitrary-XML, namespace, class/package display, duration,
stdout/stderr, summary-object, raw-UI, numeric-arithmetic, or option parity.
Empty-name display and history semantics, exotic XML inputs, and arbitrary
nested wrappers remain outside.

The collector remains reproducible and refuses to overwrite this completed run.
