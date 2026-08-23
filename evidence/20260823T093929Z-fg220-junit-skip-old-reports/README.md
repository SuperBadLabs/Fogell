# FG-220 — JUnit `skipOldReports` retained proof

Status: **COMPLETE**. The atomic collector published 31 payload files under
`run/`. `run/STATUS` is `COMPLETE`, every entry in `run/MANIFEST.sha256`
verifies, and that manifest has SHA-256
`da0fc9b54e0609081aa4a5c27d752c221f259e1f8b74961f83f689b567db97d8`.

## Bounded claim

On a fresh run, literal `skipOldReports: true` filters a matched JUnit report
before XML parsing exactly when

```text
lastModifiedMillis < buildStartTimeInMillis - 3000
```

The comparison is strict, so equality and future timestamps are retained.
Default and explicit false do not filter. If every matched report is skipped,
the existing no-result behavior applies, and `allowEmptyResults: true` admits a
typed zero summary.

Pinned Jenkins/JUnit supplies `min(Run.getStartTimeInMillis(),
Run.getTimeInMillis())` to `TestResult`; `TestResult` uses the
`hudson.tasks.junit.TestResultfiletime.precision.margin` system property with a
pinned default of 3000 ms. Fogell's claim is limited to its captured fresh-run
origin. A resumed persisted run refuses true because its original cutoff is not
durable.

## Retained proof

The collector refused overwrite, staged privately, and published `run/` only
after all validations passed. It performed these checks:

- bracketed collection with identical pinned Jenkins/controller/plugin/container
  identities;
- retained private bytecode for JUnit's `ParseResultCallable`, `TestResult`, and
  `JUnitResultArchiver`, plus Jenkins core `Run`;
- bound the 3000 ms bytecode default and captured the live JVM property/environment
  override state before and after;
- mechanically established 36 direct JUnit calls and a corpus-wide zero
  `skipOldReports` occurrence result, covering multiline call arguments;
- regenerated the mixed and all-old/allow-empty canonical receipts and required
  byte identity with the promoted tier-1 receipts;
- validated the strict `< cutoff - margin` branch, exact case identities,
  configured target overrides, workspace cleanup, and receipt seals; and
- published atomically with `STATUS` and `MANIFEST.sha256`.

Both regenerated receipts are tier-1 PROVEN and byte-identical with their
promoted copies. The before/after oracle, JAR, bytecode, runtime-margin, core,
and container identities are identical; both Jenkins workspaces and their
`@tmp` companions are absent after collection.

## Scope boundary

Resumed execution with true until the original cutoff is durable, runtime
margin overrides beyond capture/refusal of an unexpected value, filesystem
races, timestamp resolution, clock skew, symlink behavior, non-Linux behavior,
nonliteral coercion, remaining JUnit object/raw UI or numeric behavior, and
unrelated glob/report-ingest residuals are not claimed.

## Reproduction

```sh
bash evidence/20260823T093929Z-fg220-junit-skip-old-reports/collect.sh
```

The collector refuses to overwrite the retained completed run.
