> **Collector-time scaffold snapshot**
>
> The embedded status below describes the bundle when collection started,
> before the retained `run/` was published. After publication, the enclosing
> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained
> provenance, not the live status of the enclosing run.

# FG-212 JUnit missing testcase name evidence

Status: **IN PROGRESS**. Retained recollection is pending after the safe
exception-envelope redesign; no final run digest or tier-1 count is claimed yet.

This bundle is staged to retain three independent differential jobs against
Jenkins 2.568.1 and JUnit plugin `1416.vd753e036de5e`:

- `fg212-junit-missing-name-classname-markers`
- `fg212-junit-missing-name-owner-fallback`
- `fg212-junit-missing-name-terminal`

The classname-marker row gives all four direct cases no testcase `name` and
requires exact pass/failure/error/skipped totals, including an explicitly empty
`classname=""` control. The owner-name row gives missing-name cases to both an
arbitrary root and a direct `testsuite` owner. The terminal row has no testcase
name, testcase classname, or owner name and sets literal
`allowEmptyResults: true`; it must compare the visible normalized envelope
`Failed to read ${WORKSPACE}/reports/result.xml`, plus FAILURE, reported-reason
presence, successor absence, and workspace parity.

Successful rows require exact typed totals, continuation, terminal status,
ordered normalized output, and workspace state. The exact oracle root cause
`Cannot invoke "String.contains(java.lang.CharSequence)" because "nameAttr" is null`
is the structured Fogell `Diagnostic` and live-oracle/focused-test pin. The
terminal differential receipt does not compare that NPE text. Focused tests also
pin absent-versus-empty name, the distinct FG-211 and FG-212 diagnostics,
within-report child-first/document-order fatal precedence, absent partial totals,
and `allowEmptyResults` resistance. Across separate matched files, the existing
generic-unreadable precedence remains unchanged.

The collector reuses the FG-177 immutable oracle pin. It captures the exact
container and JUnit jar plus private bytecode for `TestResultSummary`,
`TestResult`, `SuiteResult`, and `CaseResult` before and after the run, copies
its inputs and tooling, verifies every receipt seal and case digest, and binds
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

The collector refuses to overwrite an existing `run/` directory.
