> **Collector-time scaffold snapshot**
>
> The embedded status below describes the bundle when collection started,
> before the retained `run/` was published. After publication, the enclosing
> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained
> provenance, not the live status of the enclosing run.

# FG-214 JUnit duration evidence

Status: **COLLECTING**. The collector refuses to overwrite a retained `run/`.

This bundle measures two independent differential jobs against Jenkins 2.568.1
and JUnit plugin `1416.vd753e036de5e`:

- `fg214-junit-duration-positive`
- `fg214-junit-duration-empty`

The positive row proves that a wrapper-only `time` is ignored, a suite without
`time` sums its direct testcase durations, and a present suite `time` overrides
its child's different value. The exact aggregate is `7.75` seconds. The empty
row proves literal `allowEmptyResults: true` returns duration `0.0`. Both rows
require property/getter parity, boxed `java.lang.Float` plus `Number`
provenance, refusal of Double/BigDecimal provenance, exact counts, terminal
SUCCESS, and continuation.

The bounded claim is the read-only `duration` property and zero-argument
`getDuration()` on Fogell's nominal JUnit summary, together with the pinned
binary32 parsing, clamping, suite-authority, and aggregation needed to produce
that value. Generic decimals, duration arithmetic/order/ranges/truthiness,
indexing, hashing, mutation, reflection, direct methods, and hosted-step
coercion remain outside the bundle.

The collector reuses the FG-177 immutable oracle pin and captures controller
identity, the exact JUnit jar digest, the public `TestResultSummary` surface,
and private bytecode for `TestResultSummary`, `TestResult`, `SuiteResult`,
`CaseResult`, and `TimeToFloat` before and after both jobs. It copies exact
canonical inputs, promoted receipts, and tooling; generates fresh receipts;
requires byte-exact promoted/fresh receipt parity, tier-1 verdicts, valid seals,
and matching case digests; and binds every retained file with
`MANIFEST.sha256` through one atomic publication.

Run `bash evidence/20260823T051241Z-fg214-junit-duration/collect.sh` from the
repository root.
