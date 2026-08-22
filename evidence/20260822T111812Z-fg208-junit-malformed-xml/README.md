# FG-208 malformed JUnit XML evidence

This compact bundle binds three independent Jenkins 2.568.1 / Fogell
differential jobs for the malformed-report recovery implemented by pinned
JUnit plugin `1416.vd753e036de5e`.

`fg208-junit-malformed-xml` publishes one matched `.xml` file containing the
literal bytes `not-xml`. Both engines continue, return `1,1,0,0`, expose the
derived `passCount` as `Integer` rather than `Long`, finish UNSTABLE, and write
a deterministic marker. `fg208-junit-malformed-mixed` pairs one consistent
passing report with one malformed `.xml`; both engines retain the valid case,
add one synthetic failed test, return `2,1,0,1`, continue, and finish UNSTABLE.

`fg208-junit-empty-any-extension` publishes zero-byte `.txt` and uppercase
`.XML` reports. Both engines synthesize one `[empty]` failure per file before
extension gating, return `2,2,0,0,Integer`, continue, and finish UNSTABLE.

The ordinary differential receipts compare terminal result, ordered normalized
output, and canonical workspace hash, so both report-ingest paths are tier-1
observable. The cases are separate jobs and each starts by removing its own
reports and marker.

The retained run brackets all three jobs with the FG-177 Jenkins oracle, immutable
container identity, exact JUnit jar digest, and private bytecode for
`TestResultSummary`, `TestResult`, `SuiteResult`, and `CaseResult`. Before/after
captures must be byte-identical. The run copies its inputs and collector,
verifies all three receipt seals and case digests, records `STATUS=COMPLETE`, and
binds every retained file with `MANIFEST.sha256`.

This proves the retained lowercase-`.xml` parse-failure recovery, its aggregation
with a valid sibling, and extension-independent zero-byte recovery. An unretained
read-only oracle audit also observed malformed-file multiplicity, suppression,
and the case-sensitive extension gate for non-empty parse failures; focused
Fogell tests and captured bytecode pin those unretained edges. Testcase-child
authority over missing, invalid, or inconsistent suite
aggregate attributes remains a separate report-ingest residual. Duration,
object/UI surface, numeric-width arithmetic, and other JUnit options remain out
of scope.

Run `bash evidence/20260822T111812Z-fg208-junit-malformed-xml/collect.sh` from
the repository root. The collector refuses to overwrite its retained `run/`.
