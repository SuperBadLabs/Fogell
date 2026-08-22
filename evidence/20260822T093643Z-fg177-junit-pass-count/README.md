# FG-177 JUnit pass-count evidence

This compact bundle binds two independent Jenkins 2.568.1 / Fogell
differential jobs for the `passCount` property returned by pinned JUnit plugin
`1416.vd753e036de5e`. `fg177-junit-pass-count` has seven actual test cases:
four pass, one fails, one errors, and one is skipped. The zero control has one
failure, one error, one skip, and no passing case. Both assert that `passCount`
is an `Integer`, finish UNSTABLE, and write a deterministic count marker only
when all four counts match.

The ordinary differential receipts compare terminal result, ordered normalized
output, and canonical workspace hash, so both property reads are tier-1
observable. The two cases are separate jobs: no accumulated result from one
JUnit invocation can influence the other.

The retained run brackets both jobs with the FG-177 Jenkins oracle, immutable
container identity, exact JUnit jar digest, and private bytecode/public surface
from `javap -c -p`. Before/after captures must be byte-identical. The run copies
its inputs and collector, verifies both receipt seals and case digests, records
`STATUS=COMPLETE`, and binds every retained file with `MANIFEST.sha256`.

This proves the two measured, internally consistent report shapes. It does not
widen the remaining JUnit object surface: duration, getter-call syntax,
rendering, indexing, mutation, identity/equality, truthiness, reflection,
spread access, and hosted-step coercion remain outside this bundle.

Run `bash evidence/20260822T093643Z-fg177-junit-pass-count/collect.sh` from the
repository root. The collector refuses to overwrite its retained `run/`.
