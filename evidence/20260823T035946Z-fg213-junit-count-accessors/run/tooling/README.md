> **Collector-time scaffold snapshot**
>
> The embedded status below describes the bundle when collection started,
> before the retained `run/` was published. After publication, the enclosing
> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained
> provenance, not the live status of the enclosing run.

# FG-213 JUnit count-accessor evidence

Status: **IN PROGRESS**. The collector scaffold exists; no retained `run/`
is claimed until collection completes.

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

Run `bash evidence/20260823T035946Z-fg213-junit-count-accessors/collect.sh`
from the repository root.
