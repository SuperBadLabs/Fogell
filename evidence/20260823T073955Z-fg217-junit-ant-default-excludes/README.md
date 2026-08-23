# FG-217 JUnit Ant default-excludes evidence

Status: **COMPLETE**. The immutable `run/` contains 31 manifest-bound payload
files plus `MANIFEST.sha256`. `MANIFEST.sha256` has SHA-256
`f3d229905f5afcb98fb36634dc6d10b9214627441f9945163acdc03d231eaa6e`.

This bundle measures two differential jobs against Jenkins 2.568.1, JUnit
plugin `1416.vd753e036de5e`, Jenkins core 2.568.1, and Ant 1.10.17:

- `fg217-junit-ant-default-excludes-selection`
- `fg217-junit-ant-default-excludes-terminal`

The selection row requires a visible passing report to remain authoritative
while a failing report beneath `.svn` stays inert: exact counts `1,0,0,1`,
duration `1.25`, SUCCESS, and continuation. The terminal row requires a sole
default-excluded report, even when named literally, to follow the existing
no-report failure and suppress its successor.

The bounded claim is JUnit report selection's pinned Ant default-exclude set:
`AbstractFileSet` starts with `useDefaultExcludes=true`, scanner setup calls
`addDefaultExcludes`, and the exact 28 Ant 1.10.17 patterns exclude matching
files before JUnit parses them. Matching remains case-sensitive. Shared
archive/stash matching, post-enumeration traversal and fault timing, runtime
mutation of Ant's global exclude set, symlink traversal, `skipOldReports`, and
richer Ant syntax are outside the bundle.

The collector reuses the FG-177 immutable oracle pin and captures controller
and container identity, exact JUnit/Jenkins-core/Ant jar digests, and the JUnit
parser, Jenkins FileSet factory, and Ant scanner bytecode before and after both
jobs. It explicitly checks the true default, the scanner call, and byte-exact
ordered parity with all 28 retained constants. It also requires byte-exact
promoted/fresh receipts, tier-1 verdicts, valid seals, matching case digests,
configured-target override proof, and final workspace cleanup/absence proof,
then binds every retained file through one atomic `MANIFEST.sha256` publication.

Run `bash evidence/20260823T073955Z-fg217-junit-ant-default-excludes/collect.sh`
from the repository root.
