# FG-208 malformed JUnit XML evidence

This compact bundle binds two independent Jenkins 2.568.1 / Fogell
differential jobs for the malformed-report recovery implemented by pinned
JUnit plugin `1416.vd753e036de5e`.

`fg208-junit-malformed-xml` publishes one matched `.xml` file containing the
literal bytes `not-xml`. Both engines continue, return `1,1,0,0`, expose the
derived `passCount` as `Integer` rather than `Long`, finish UNSTABLE, and write
a deterministic marker. `fg208-junit-malformed-mixed` pairs one consistent
passing report with one malformed `.xml`; both engines retain the valid case,
add one synthetic failed test, return `2,1,0,1`, continue, and finish UNSTABLE.

The ordinary differential receipts compare terminal result, ordered normalized
output, and canonical workspace hash, so both report-ingest paths are tier-1
observable. The cases are separate jobs and each starts by removing its own
reports and marker.

The retained run brackets both jobs with the FG-177 Jenkins oracle, immutable
container identity, exact JUnit jar digest, and private bytecode for
`TestResultSummary`, `TestResult`, `SuiteResult`, and `CaseResult`. Before/after
captures must be byte-identical. The run copies its inputs and collector,
verifies both receipt seals and case digests, records `STATUS=COMPLETE`, and
binds every retained file with `MANIFEST.sha256`.

This proves only the retained lowercase-`.xml` parse-failure recovery and its
aggregation with a valid sibling. An unretained read-only oracle audit also
observed empty XML, per-file multiplicity, suppression, uppercase `.XML`, and
non-XML extension behavior; focused Fogell tests and captured bytecode pin the
implemented boundary, but those edges are not presented as additional receipt
claims. Testcase-child authority over missing, invalid, or inconsistent suite
aggregate attributes remains a separate report-ingest residual. Duration,
object/UI surface, numeric-width arithmetic, and other JUnit options remain out
of scope.

Run `bash evidence/20260822T111812Z-fg208-junit-malformed-xml/collect.sh` from
the repository root. The collector refuses to overwrite its retained `run/`.
