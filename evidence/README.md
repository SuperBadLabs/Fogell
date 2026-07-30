# Evidence

One directory per ticket, produced by `scripts/seal-evidence.sh <TICKET>`:

    <commit-timestamp>-<ticket-id>/
      base-commit.txt          the commit the work was measured at
      candidate.diff           the change itself
      diffstat.txt
      status-before-commit.txt
      tree.txt                 tracked files at that point
      corpus-gate.log          FG-003 corpus verification output
      build.log
      tests-<project>.log      per-project test summary
      SHA256SUMS               self-excluding manifest

A receipt must verify standalone: `cd <dir> && sha256sum -c SHA256SUMS`.
A ticket without a receipt is not DONE, no matter what the code does.

Lesson encoded here: a prior project's gate baseline was named after the release
it was meant to gate rather than the build it was captured from, and bound only
a binary hash. It silently compared across trees for days. `base-commit.txt` and
`tree.txt` exist so that cannot recur.
