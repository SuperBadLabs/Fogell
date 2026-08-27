# FG-221 — JUnit symlink traversal

Status: **COMPLETE (evidence bundle only)**. The collector published `run/`
only after the pinned oracle, JUnit/core/Ant identities and bytecode, direct Ant
symlink matrix, corpus boundary, two canonical receipts, cleanup, and manifest
checks all passed. The completed run contains 37 manifest-bound payload files.
Its `MANIFEST.sha256` digest is
`8ed2bf90e861fe519a195621c62521c912153afb3f8716a8f9b8fc9878b502ff`.
Bundle completion
does not make FG-221 DONE; publication remains tracked in `docs/tickets/FG-221.md`.

## Bounded behavior and safety envelope

On pinned Linux, JUnit's private report selector follows healthy file and
directory symlinks under their logical scanner paths, including a directory
target outside the workspace. A branch follows one canonical directory target
at most five times, so `loop -> .` yields the base report plus five aliases.

Ant's literal fast path excludes a dangling file symlink; a wildcard retains it
as one zero-byte synthetic failure. The implementation resolves and retains each
selected physical target, opens it once for length/timestamp/XML, prunes
unrelated include prefixes before traversal, polls cancellation during scans,
shares a fixed work budget across patterns and entries, and propagates authority
or I/O errors rather than converting them into `allowEmptyResults` success.
Archive/stash matching remains unchanged.

This is an explicit compatibility-scanner exception to ADR 0008's lexical path
rule, not a new filesystem authority grant. `Workspace.resolveUnder` remains
strict. The agent account and VM are the authority boundary.

That authority boundary is an architectural policy decision. This collector
does not prove credential isolation, VM containment, or deployment permissions;
its manifest-bound tooling snapshot correctly records external-target security
policy as outside the retained proof.

## Retained proof

`collect.sh` brackets a fresh two-case differential with identical pinned
Jenkins 2.568.1, JUnit `1416.vd753e036de5e`, Ant 1.10.17, core, and controller
identities. A direct FileSet matrix proves healthy file/directory/external-target
selection, the dangling literal/wildcard split, dangling directory and file-loop
controls, and exactly six results for a directory self-loop.

The two differential receipts bind only the self-loop and dangling
literal/wildcard behaviors. Healthy external-directory selection and final-target
timestamp behavior are supported by the direct pinned-Ant matrix and focused
Fogell test, respectively; they are not tier-1 receipt claims in this bundle.
The focused Execution suite passes 63/63, including security regressions, but its
result is local verification rather than a promoted differential receipt.

The pinned corpus contains 36 direct JUnit calls, 25 recursive patterns, and no
symlink indicator on a direct JUnit call line; this is correctness, not corpus
recovery.

## Reproduction

```sh
bash evidence/20260823T104948Z-fg221-junit-symlink-traversal/collect.sh
```

The collector refuses overwrite, verifies its generated manifest, and publishes
atomically only after validation.
