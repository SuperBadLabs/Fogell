# FG-228 pinned stash-symlink measurement

The two probes cover selected file and directory symbolic links whose targets are
inside and outside the workspace, once with Jenkins' default Ant excludes and once
with `useDefaultExcludes: false`.

Pinned Jenkins 2.568.1 restores both file links as symbolic links with their
original link text and follows both directory links, restoring the directory
descendants as ordinary files. The internal file link is then dangling because
its unselected target was not stashed; the external file link resolves while its
target exists. The behavior is identical under both default-exclude modes.
Fogell intentionally refuses the stash before restore because preserving an
external file link and following an external directory both cross the workspace
trust boundary.

The post-fix receipts are therefore expected `DIVERGED (3)` records, not tier-1
compatibility evidence. Each reproduced on all three attempts and retains the
exact Jenkins result, output, and workspace listing. The active differential case
set excludes these probes so the full compatibility lane continues to require
every active case to be tier-1 proven.

The implementation proof lives in the Execution and Differential suites; the
ticket document describes the save/restore descriptor boundaries, public
round-trip/sentinel assertions, and blocking traversal/following mutant.
