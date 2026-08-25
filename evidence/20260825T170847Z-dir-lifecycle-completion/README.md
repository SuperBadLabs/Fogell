# Workspace lifecycle parity completion evidence

Captured on 2026-08-25 from HeMan worktree
`/home/srikanth/projects/fogell-worktrees/custodian-fg222-port`, based on signed
commit `4fbd64e8e9bfd72e6cd200dd8252f26ccc6795c1`. The exact tested tracked
candidate is `CANDIDATE.patch.gz` (deterministic gzip SHA-256
`abc972066ed889dff2801fe44960dfa2ce1b4ccbb70cee3f75108322fd7ef4b7`);
its decompressed binary patch has SHA-256
`697bd86bc3b9f73b0cf4a3b9a911e397bf357cfa6ac235d3df8c7ffed4a2f1c8`
and applies to that base.

## Outcome

- Single impact dimension: Jenkins-compatible physical workspace lifecycle for
  logical `dir` scopes.
- Root cause removed: `dir` no longer eagerly creates its logical cwd.
- Proven materializers: admitted shell/bat and SCM launches create the cwd at
  their execution boundary; SCM does so only after retained-history admission.
- Wrong cleanup model rejected: an empty cwd created by `sh 'true'` remains.
- Full pinned Jenkins differential: **282/282 tier-1 PROVEN**, **0 partial**.
- Authoritative repository gate: terminal `OK`; **795/795 tests** across eight
  projects, plus blocking durability, security, receipt, compatibility, restart,
  and approval proofs.
- Compatibility non-regression: **228 files**, accepted **200 -> 200**, tier-1
  **1 -> 1**, zero losses and zero gains.

## Decisive controls

The retained receipts under `oracle/residuals/` include the four former
workspace-only divergences and their opposing control:

- `fg177-wrapper-body-result`: pure wrapper return leaves no physical directory.
- `post-exit-env-read-fault`: pure/fault-handling body leaves no physical directory.
- `script-closure-mutates-enclosing`: closure assignment leaves no physical directory.
- `script-return-is-closure-local`: closure-local return leaves no physical directory.
- `post-exit-env-read`: `dir('s') { sh 'true' }` retains empty `s/` on both engines.

Each differential case was executed against pinned Jenkins core `2.568.1` and
the Trace-v2 remote workspace collector. The full receipt archive was created
with GNU tar using sorted names, epoch mtime, numeric owner/group zero.

## Files

- `CANDIDATE.patch.gz` — deterministic gzip of the exact binary-capable tracked
  diff tested on the signed base.
- `gate/full-gate.log` — complete authoritative gate output.
- `oracle/full-282.log` — complete live differential verdict stream.
- `oracle/full-282-receipts.tar.gz` — all 282 fresh receipts.
- `oracle/residuals/` — the five decisive receipts above.
- `SHA256SUMS` — hashes for every evidence payload in this directory.

The first gate attempt without PostgreSQL and the second attempt without the
external compatibility paths are intentionally not retained here. Both refused
at their documented environment prerequisites. `full-gate.log` is the complete
run with the isolated PostgreSQL 16 test database and the exact prior 228-project
baseline/oracle paths configured.
