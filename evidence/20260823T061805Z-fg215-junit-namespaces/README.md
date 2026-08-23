# FG-215 JUnit XML namespace evidence

Status: **COMPLETE**. The retained `run/` is immutable.

This bundle measures two independent differential jobs against Jenkins 2.568.1,
JUnit plugin `1416.vd753e036de5e`, and DOM4J 2.2.0:

- `fg215-junit-namespaced-local-names-positive`
- `fg215-junit-namespace-declaration-not-attribute`

The positive row combines default, prefixed, and independently mixed element
namespaces; prefixed identity and duration attributes; two same-local `time`
attributes whose first value must win; namespaced failure/skipped markers;
wrong-case, longer-name, and arbitrary-wrapper decoys; counts `3,1,1,1`;
duration `7.75`; UNSTABLE; and continuation. The terminal row proves that
`xmlns:name` is not an attribute and cannot provide the owner's identity
fallback.

The bounded claim is JUnit report ingestion's exact, case-sensitive local-name
lookup for direct elements and ordered attributes. Namespace URI and prefix are
ignored, the first matching attribute wins, and namespace declarations are
excluded. Traversal, ordering, aggregation, marker precedence, and fault phases
remain unchanged. General XML behavior is outside this bundle.

The collector reuses the FG-177 immutable oracle pin and captures controller
identity, exact JUnit and DOM4J jar digests, JUnit report-parser bytecode, DOM4J
`AbstractElement` bytecode, and container identity before and after both jobs.
It copies exact canonical inputs, promoted receipts, and tooling; generates
fresh receipts; requires byte-exact promoted/fresh receipt parity, tier-1
verdicts, valid seals, and matching case digests; and binds every retained file
with `MANIFEST.sha256` through one atomic publication.

Both receipts are PROVEN tier 1 and all retained seals verify. The required jar
digests are
`7dd505533996f81b403a5d71542209f776cd69fad5a416958681ff62971cd142`
for JUnit and
`3fae79e081096e1410645eb3557c63b79ca266d510ab479889511109becd1690`
for DOM4J. Oracle, jar, bytecode, and container captures are byte-identical
before and after collection. The retained manifest contains 30 rows and has
digest `62f7e1394e9d642981d23c4f07b75483cd6b04dbc52567b8cd9ced6799b6912b`.
Canonical inputs and promoted receipts are byte-identical to the retained
inputs and fresh receipts.

The adjacent `oracle-matrix/` retains eight sharper attribute rows, each stable
over three attempts: prefixed suite name, testcase classname, dotted testcase
name, suite and testcase time, both lexical orders of same-local attribute
collisions, and the namespace-declaration trap. It also includes three-run
controller-owned report JSON and console captures for both collision orders.
All 36 payload files verify against its `SHA256SUMS`; that manifest's digest is
`9e71d0bb0be8792cca84e6bc0dbc51012ea807496202bd71e375d523d5e686a5`.

Run `bash evidence/20260823T061805Z-fg215-junit-namespaces/collect.sh` from the
repository root.
