> **Collector-time scaffold snapshot**
>
> The embedded status below describes the bundle when collection started,
> before the retained `run/` was published. After publication, the enclosing
> `run/STATUS` is authoritative: `COMPLETE` means this scaffold is retained
> provenance, not the live status of the enclosing run.

# FG-221 — JUnit symlink traversal

Status: **COLLECTOR-TIME SNAPSHOT**. Live completion state and the
manifest digest are intentionally omitted from this embedded provenance copy.

## Bounded claim

On pinned Linux, JUnit's private report selector follows healthy file and
directory symlinks under their logical scanner paths, including a directory
target outside the workspace. A branch is pruned only after the same canonical
directory target has already been followed five times, so `loop -> .` yields the
base report plus five logical aliases and terminates.

Ant's literal fast path excludes a dangling file symlink. A wildcard scan keeps
that lexical entry, and JUnit's `File.length()` path turns it into one zero-byte
synthetic failure. A dangling directory link has no recursive descendants. File
existence, length, and `skipOldReports` metadata are read from the final target.
The behavior is private to JUnit; archive/stash retain their existing matcher.

This is an explicit compatibility-scanner exception to the lexical path rule
in ADR 0008, not a new filesystem authority grant. `Workspace.resolveUnder`
continues to reject symlink components in Fogell-managed path arguments. The
JUnit scanner follows links only with the existing agent account's permissions;
a pipeline shell already has those permissions, and hostile multi-tenant
isolation remains the VM boundary.

## Retained proof

`collect.sh` brackets a fresh two-case differential with identical pinned
Jenkins 2.568.1, JUnit `1416.vd753e036de5e`, Ant 1.10.17, core, and controller
identities. A direct FileSet matrix proves healthy file/directory/external-target
selection, the dangling literal/wildcard split, dangling directory and file-loop
controls, and exactly six results for a directory self-loop.

The canonical loop receipt proves six passing reports, binary32 duration `6.0`,
continuation, success, and workspace parity. The dangling receipt proves typed
zero for the allowed literal miss, one wildcard-selected synthetic failure,
continuation, UNSTABLE, and workspace parity. The pinned corpus contains 36
direct JUnit calls, 25 recursive patterns, and no symlink indicator on a direct
JUnit call line; this is correctness, not corpus recovery.

Arbitrary multi-node cycles, chains of more than five distinct link targets,
root links, Windows junctions and platform path behavior, races/permissions,
and symlink timestamp behavior beyond the measured final-target rule remain
outside scope.

## Reproduction

```sh
bash evidence/20260823T104948Z-fg221-junit-symlink-traversal/collect.sh
```

The collector refuses overwrite and publishes atomically only after validation.
