# Wave 3 operations progress, not a qualification pass

Date: 2026-09-29. Local branch candidate for the first paired-backup helper:
`eafebe8a` (`scripts/paired-backup.py`, `tests/test_paired_backup.py`). The
controller/output product candidate remains the W2-qualified `1d2973c0`.

The helper created one paired recovery point from a synthetic one-file state
root and the disposable PostgreSQL 16 gate container
`fogell-gate-postgres-local-job-1-2186190-29931`. The host lacked PostgreSQL
client binaries, so temporary `/tmp/fogell-fg308-local-tools` shims executed
PostgreSQL 16 `pg_dump`, `pg_restore`, and `psql` inside that disposable
container. The maintenance connection was a local test connection. `create`
and `check` returned success. The point's manifest records schema `0017`,
one state entry, database archive SHA-256
`1d51fed5d9ad42150556aafa4b99ba70e120f2550eae2481379c9df59d78eea5`,
state archive SHA-256
`448b863f94c2a33d0325aa8b79e358cdc530f7daf29b54669e2878f94f94e7b4`,
and inventory SHA-256
`444b2bf43e8ce528ff12ded7de1e537335ce97b89438cc7967cc3aeaa2aabacd`.
The exact local manifest is retained as `local-backup-manifest.json`.

The PostgreSQL custom archive was then restored with `pg_restore --exit-on-error
--single-transaction` into a fresh disposable database. The restored database
reported latest schema `0017`, 17 migration records, and 16 public tables.
The restore database was dropped afterward. This proves a small local point is
readable and restorable; it does not prove application recovery, a scheduled
series of recovery points, or a production-sized restore.

The paired backup and operator checks passed 10 focused tests together. They
cover archive tampering, special files, failed-attempt records, alert clearing,
and invalid backup-age thresholds. The operator check's tenant-scoped query
was exercised under a
temporary `NOBYPASSRLS` role with only `SELECT` grants in the disposable
database. The role was removed. Threshold calibration and alert delivery on
the Luigi host have not been done. The helper retains nonsecret failed-attempt
records and the operator check clears that alert only after a newer verified
point, while preserving the record.

FG-308 and FG-309 remain **Doing**. FG-308 still needs scheduled multiple
recovery points, a staged paired restore with application checks, and bounded
retention catch-up without affecting active/uncertain work. FG-309 still needs
an operator delivery channel, deduplication and clear behavior, measured host
thresholds, and induced end-to-end alerts. FG-310's 72-hour unattended run
starts only after those dependencies are ready.
