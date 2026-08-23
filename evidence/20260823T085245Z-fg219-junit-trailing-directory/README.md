# FG-219 — JUnit trailing-directory include shorthand

Status: **COMPLETE**. The collector published `run/` only after the pinned
oracle, Ant bytecode and FileSet matrix, mechanically derived corpus boundary,
two canonical receipts, cleanup, and manifest checks all passed. The bundle
contains 37 manifest-bound payload files; `MANIFEST.sha256` has digest
`05297778131a8e67c2ed48ad8457ab19fc0b6e4637159b1e226c494abea48f53`.

## Bounded claim

On the pinned Linux boundary, a scanner-relative JUnit directory prefix ending in
one or repeated path separators recursively selects beneath that directory as Ant
does by appending `**`. For wildcard-bearing prefixes, the implementation also
preserves terminal `**` consuming zero components, so a shape such as
`reports/*/` can select a direct file through its suffix-free arm as well as
descendants through its suffixed arm. A wholly literal file prefix ending in a
separator remains empty, matching Ant's directory lookup.

The behavior is private to JUnit. Dot-relative spelling, archive/stash matching,
root/absolute/UNC
compatibility, platform traversal, symlinks, unrelated wildcard syntax,
`skipOldReports`, and the remaining JUnit object/UI and numeric surface are not
claimed.

## Retained proof

`collect.sh` brackets a fresh two-case differential with identical pinned
Jenkins, JUnit, core, Ant, tokenizer, matcher, and controller identities. The
direct FileSet matrix equates singular, repeated, and backslash trailing forms
with explicit `/**`, proves the terminal-zero arm and known Ant-jar witness, and
keeps case/rooted controls empty.

The corpus has zero trailing-directory includes among 36 direct JUnit calls and
zero archive/stash trailing include patterns. A distinct, non-admitted archive
exclude literal contains two trailing-directory components; it is retained only
to make the JUnit-only scope auditable.

Before implementation, three independent scratch runs were byte-identical: the
explicit `/**` control was PROVEN, while both `outer/reports/` and
`outer/reports//` DIVERGED with Jenkins recursively selecting the direct and
nested reports and Fogell taking the no-report failure. Those repetitions are
explicitly unretained. The bundle independently regenerates the two post-fix
receipts and binds their inputs and promoted copies.

## Reproduction

```sh
bash evidence/20260823T085245Z-fg219-junit-trailing-directory/collect.sh
```

The collector refuses overwrite and publishes atomically only after validation.
