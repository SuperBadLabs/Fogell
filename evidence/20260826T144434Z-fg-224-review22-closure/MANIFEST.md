# FG-224 exact-head review 22 closure

Collected at `2026-08-26T20:33:51Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `198395adea0f64af7b511d60a0a61e029feb67f4`
- Base tree: `ce447f675edc99bebbdcf8c4c436eb402554408d`
- Candidate product tree: `b5f98ea9c31f42bb341058fc6311ff4f35318e41`
- Candidate-diff SHA-256: `5f1c12ed817b3dc765df85da323a5b39c8f20bce380fd040aaa4ad005ad92d21`
- Delta: 12 files changed, 824 insertions, 116 deletions
- .NET SDK: `10.0.301`

The complete audited product delta was staged before snapshotting.
`candidate.diff` is the index-versus-HEAD diff and the candidate product tree is
derived from `git write-tree`. This evidence directory is output and is
intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5034495595` on base commit `198395a` found two blockers:

1. `ActivateRestore` performed a fleet-wide attempts update without tenant
   context under forced RLS, allowing the epoch bump to commit while old-epoch
   attempts remained queued.
2. HTTP admission used generic execution preflight but omitted the persisted
   journal key constraints enforced later by Run.Host, allowing a 201 response
   for work that could never start.

The closure makes both boundaries durable and shared:

- Restore iterates the global UUID-only organization root registry while setting
  transaction-local tenant context. The epoch bump, attempt → node → build
  reconciliation roll-up, and one `restore_epoch_advanced` event/outbox pair per
  moved attempt commit atomically under a real NOBYPASSRLS maintenance role.
- Fresh attempt creation takes the shared metadata-row lock. Forward-only,
  checksum-pinned migration 0010 adds a search-path-hardened BEFORE INSERT
  trigger that rejects stale epochs even from direct SQL. Deterministic tests
  prove both restore-first and insert-first serializations.
- `preflightPersistedExecution` is the single journal-key validator used by the
  API, Run.Host before mutation, and `runPersisted` at the deepest guard. It
  rejects duplicate flattened stage names and TAB/LF/CR delimiters.
- A read-only durable fingerprint lookup preserves exact replay of a legacy
  admission before applying stricter fresh-source validation. Exact replay stays
  200, changed bytes stay 409, and a fresh unsafe source is 422 without binding
  the idempotency key.
- Runtime readiness now requires the least-privilege `UPDATE(singleton)` grant
  needed by PostgreSQL locking reads, while `restore_epoch` update authority
  remains maintenance-only. The controller runbook and runnable proof carry the
  same grant.
- Backup/restore and N−1 rollback fixtures were advanced to the 0010-era schema;
  both full operational drills pass.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 891/891 main-suite tests; final `OK` | `fg224-review22-final-gate.log` |
| Runnable controller | PASS; full HTTP-to-terminal/reconciliation vertical proof | `fg224-review22-runnable-proof.log` |
| Store | PASS, 87/87 including forced-RLS restore, two-way epoch races, hostile direct insert, and readiness capability | `tests-Fogell.Store.Tests.log` |
| Controller API | PASS, 30/30 including journal-key refusal and legacy exact replay | `tests-Fogell.Controller.Api.Tests.log` |
| Backup/restore and migration rollback drills | PASS against migrations through 0010 | final gate and independent exact-tree audit |
| Release build | PASS, zero warnings and zero errors | `build.log` |

The 891 main-suite tests are Controller API 30, Differential 265, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 87. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
