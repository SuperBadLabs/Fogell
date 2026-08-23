> **Collector-time scaffold snapshot**
>
> The embedded status below describes the bundle when collection started,
> before the retained `run/` was published. After publication, the enclosing
> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained
> provenance, not the live status of the enclosing run.

# FG-216 JUnit case-sensitive pattern evidence

Status: **COLLECTOR-TIME SNAPSHOT**. Live completion state and the
manifest digest are intentionally omitted from this embedded provenance copy.

This bundle measures two differential jobs against Jenkins 2.568.1, JUnit
plugin `1416.vd753e036de5e`, Jenkins core 2.568.1, and Ant 1.10.17:

- `fg216-junit-pattern-case-sensitive-selection`
- `fg216-junit-pattern-case-only-miss`

The selection row requires a lowercase passing report to be selected while a
differently cased failing sibling remains inert: exact counts `1,0,0,1`,
duration `1.25`, SUCCESS, and continuation. The terminal row requires a sole
case-only mismatch to follow the existing no-report failure and suppress its
successor.

The bounded claim is case-sensitive literal path matching for JUnit report
patterns on the pinned Linux boundary. Shared archive/stash matching, Ant
default excludes, symlink traversal, `skipOldReports`, platform path rules, and
richer Ant syntax are outside the bundle.

The collector reuses the FG-177 immutable oracle pin and captures controller
and container identity, exact JUnit/Jenkins-core/Ant jar digests, and the JUnit
parser, Jenkins FileSet factory, and Ant scanner bytecode before and after both
jobs. It requires byte-exact promoted/fresh receipts, tier-1 verdicts, valid
seals, matching case digests, and configured-target override proof, then binds
every retained file with `MANIFEST.sha256` through one atomic publication.

Run `bash evidence/20260823T065346Z-fg216-junit-case-sensitive-patterns/collect.sh`
from the repository root.
