# FG-224 exact-head review 21 closure

Collected at `2026-08-26T19:43:45Z` on HeMan from the authoritative FG-224 worktree.

## Exact candidate

- Branch: `agent/fg-224-runnable-controller`
- Base commit: `bd9122ca0ae7ed13f44b3bc10236d57d870e8703`
- Base tree: `57eac6201c2c3644b7f7a3ad3d3fa5528c6c816f`
- Candidate product tree: `8c8007b309aa525502eda56a2e4d08d9f7c269b3`
- Candidate-diff SHA-256: `3ae417aa1f2f866f8401835f3a34e1b28235a9674f47364e4757eb925031f507`
- Delta: 3 files changed, 175 insertions, 2 deletions
- .NET SDK: `10.0.301`

The complete audited product delta was staged before snapshotting.
`candidate.diff` is the index-versus-HEAD diff and the candidate product tree is
derived from `git write-tree`. This evidence directory is output and is
intentionally absent from that product tree.

## Exact-head finding closure

Codex review `5034312725` on base commit `bd9122c` found that migration 0006's
cross-tenant work-root backfill can see zero preexisting organizations when the
normal schema-owning maintenance role has neither superuser nor BYPASSRLS.

The closure is forward-only:

- Checksum-pinned migration 0006 remains byte-identical.
- Migration 0009 temporarily removes FORCE and disables RLS only on the source
  `organizations` relation, idempotently inserts every missing UUID root, then
  enables and forces RLS again before the migration transaction commits.
- The migration runner keeps the DDL, backfill, restoration, and checksum-ledger
  insertion inside one PostgreSQL transaction. Failure rolls every change back;
  success cannot expose an unprotected committed state.
- A scratch database owned by a real NOLOGIN, NOSUPERUSER, NOBYPASSRLS role is
  seeded before 0005. The regression proves historical 0006 misses the root,
  only 0009 remains pending, exactly one root is repaired, and the owner is
  fail-closed again with RLS both enabled and forced.
- Migration 0009 is checksum-pinned and direct data replay is idempotent through
  `ON CONFLICT DO NOTHING`.

## Closure matrix

| Proof | Result | Artifact |
| --- | --- | --- |
| Authoritative fresh-database gate with legitimate FG-094 inputs | PASS, 884/884 main-suite tests; final `OK` | `fg224-review21-final-gate.log` |
| Runnable controller | PASS; full review-20 vertical proof unchanged | `fg224-review21-runnable-proof.log` |
| Migration lane | PASS, 10/10 including exact restricted-owner upgrade | `tests-Fogell.Store.Tests.log` and final gate |
| Independent migration audit | CLEAR; transactional RLS restoration, checksum immutability, and idempotence verified | recorded in the exact review round |
| Release build | PASS, zero warnings and zero errors | `build.log` |

The 884 main-suite tests are Controller API 28, Differential 265, Domain 34,
Execution 100, Groovy 224, Journal 31, Pipeline Parser 120, and Store 82. The
eight `tests-*.log` files were extracted by the fail-closed sealer from this
exact candidate.

## Verification

`SHA256SUMS` excludes itself and binds every other regular file in this
directory. Verify standalone with `sha256sum -c SHA256SUMS` from this directory.
