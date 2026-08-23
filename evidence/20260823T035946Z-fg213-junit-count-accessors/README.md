# FG-213 JUnit count-accessor evidence

Status: **COMPLETE**. The retained `run/` is immutable.

This bundle measures two independent differential jobs against Jenkins 2.568.1
and JUnit plugin `1416.vd753e036de5e`:

- `fg213-junit-count-accessors-positive`
- `fg213-junit-count-accessors-empty`

The positive row has four cases: one pass, one failure, one error, and one
skip. The empty row has no report files and therefore all four counts are zero.
Both rows read `totalCount`, `failCount`, `skipCount`, and `passCount`
through both property and zero-argument getter syntax. Their marker requires
property/getter parity, exact counts, `instanceof Integer` for every value,
and `instanceof Long` for none.

The bounded claim is the four read-only summary count accessors. Existing
total/fail/skip arithmetic compatibility is retained by Fogell but is not
exercised by these canonical scripts; pass-count arithmetic remains refused.
New arithmetic beyond the retained surface, duration/getDuration, rendering,
indexing, mutation, identity, reflection, spread access, and hosted-step
coercion remain outside this bundle.

The collector reuses the FG-177 immutable oracle pin and captures controller
identity, the exact JUnit jar digest, the public `TestResultSummary` surface,
and its private bytecode before and after both jobs. It copies exact canonical
inputs, promoted receipts, and tooling; generates fresh receipts; requires
byte-exact promoted/fresh receipt parity, tier-1 verdicts, valid seals, and
matching case digests; and binds every retained file with
`MANIFEST.sha256`. It refuses to overwrite an existing `run/`.

Both receipts are PROVEN tier 1 and all retained seals verify. The public
surface records all four getters returning primitive `int`; the required JUnit
jar digest is
`7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142`.
Oracle, jar, public-surface, private-bytecode, and container captures are
byte-identical before and after collection. The retained manifest contains 26
rows and has digest
`3d82ffd8848d077743daef8f4c6cd4d7269eddffe7a90b29e75434b0dc1e5e9e`.
Canonical inputs and promoted receipts are byte-identical to the retained
inputs and fresh receipts.

Run `bash evidence/20260823T035946Z-fg213-junit-count-accessors/collect.sh`
from the repository root.
