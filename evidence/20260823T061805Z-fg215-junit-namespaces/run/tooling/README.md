> **Collector-time scaffold snapshot**
>
> The embedded status below describes the bundle when collection started,
> before the retained `run/` was published. After publication, the enclosing
> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained
> provenance, not the live status of the enclosing run.

# FG-215 JUnit XML namespace evidence

Status: **SCAFFOLD ONLY — NOT COMPLETE EVIDENCE**. The collector publishes an
immutable `run/` only after every check succeeds.

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

Run `bash evidence/20260823T061805Z-fg215-junit-namespaces/collect.sh` from the
repository root.
