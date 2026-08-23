# FG-218 — JUnit repeated internal separators

Status: **COMPLETE**. The immutable `run/` contains 34 manifest-bound payload
files plus `MANIFEST.sha256`. Its SHA-256 digest is
`64e04644f69e661801aa609567d21cd5cf3ed63814e53103ee7440e03ce496c6`.
The pinned oracle, Ant matcher matrix, corpus proof, canonical receipt, workspace
cleanup, and manifest validation all passed.

## Bounded claim

For JUnit report includes on the pinned Linux boundary, repeated **internal** path
separators tokenize like one separator. This closes the exact admitted-corpus
spelling `**//*target/surefire-reports/TEST-*.xml`. Case-sensitive matching and
Ant default excludes remain unchanged, and the public archive/stash matcher is
not modified.

Trailing-directory shorthand, rooted/UNC semantics, symlink traversal, richer
wildcards, platform-specific paths, `skipOldReports`, and the remaining JUnit
object/UI and numeric surface are outside FG-218.

## Retained proof

`collect.sh` brackets the canonical differential with identical pinned Jenkins,
JUnit, Jenkins-core, Ant-jar, private-bytecode, direct Ant matcher, and controller
identity captures. It also binds the corpus hash, manifest verification, exactly
two occurrences in one admitted file, the byte-exact input, the promoted and
fresh receipt, cleanup absence, build log, and validation output.

The direct Ant matrix holds fixture and case mode constant: singular, doubled,
and wider internal separator runs match; a case-only decoy and rooted include do
not. The canonical case then proves the exact corpus spelling returns typed
`1,0,0,1`, binary32 duration `1.25`, SUCCESS, continuation, and the same workspace
on both engines.

Before implementation, three independent live-oracle pairs were byte-identical:
the singular control was PROVEN and the doubled corpus spelling DIVERGED, with
Jenkins producing the values above while Fogell took the existing no-report
failure. Those scratch repetitions are deliberately unretained; the completed
bundle retains the independently regenerated post-fix proof and pinned mechanism.

## Reproduction

From the repository root on HeMan:

```sh
bash evidence/20260823T081437Z-fg218-junit-repeated-separators/collect.sh
```

The collector refuses to overwrite an existing `run/` and publishes it only
after every assertion succeeds.
