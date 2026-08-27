# Fogell custodian handoff — 2026-08-26

Custodian tenure is closed. This handoff records the work already in flight; it
does not authorize another impact dimension.

The owner subsequently directed this custodian to continue through successful
merge. Review 34 closes the two final exact-head database-startup findings, and
FG-225 records the broader controller trust-boundary redesign instead of
extending FG-224 through another unbounded review cycle.

## Repository and publication boundary

- Host: `heman` (Tailscale alias; project host was introduced as `heman-1`).
- Worktree: `/home/srikanth/projects/fogell-worktrees/fg-223-runnable-controller`.
- Branch: `agent/fg-224-runnable-controller`.
- Pull request: `SuperBadLabs/Fogell#148`.
- Incoming signed head: `6281d4f4304f68fa5c3b9a1b1070e61ee0254569`.
- The commit containing this file is the outgoing head; resolve it with
  `git rev-parse HEAD` rather than copying a predicted self-reference.
- Do not merge without the owner. FG-224 remains **PARTIAL** until exact-head
  review coverage, hosted gates, publication, and merge are complete.

## Last completed slice: Review 33

Two exact-head Codex findings were valid and are closed in the outgoing tree.

1. Comment `3868227937`: the controller admitted a bare `input`, then launched
   Run.Host without an approver, producing a deterministic post-admission
   failure. Fresh controller admission now rejects every input that is not
   provably bounded by a usable explicit, inherited-stage, or pipeline timeout.
   The refusal occurs before durable admission and idempotency binding; exact
   legacy admissions still replay first. The standalone Run.Host filesystem
   inbox was deliberately not exposed because build code shares its OS identity
   and could forge the file that authorizes itself.
2. Comment `3868291517`: maintenance migration and runtime operation could target
   different databases while role checks still looked healthy. Startup now uses
   a random cross-session PostgreSQL advisory-lock challenge to prove both
   capabilities reach the same live database. Connection/query/lock uncertainty
   fails closed. Aliases to one database pass; a second fully migrated database
   fails.

Changed paths in the outgoing slice:

- `src/Fogell.Controller.Api/Router.fs`
- `src/Fogell.Controller.Host/Program.fs`
- `src/Fogell.Differential/Fogell.fs`
- `src/Fogell.Store/Store.fs`
- `tests/Fogell.Controller.Api.Tests/Tests.fs`
- `tests/Fogell.Differential.Tests/Tests.fs`
- `tests/Fogell.Store.Tests/Tests.fs`
- `docs/EXECUTION_BOARD.md`
- `docs/tickets/FG-224.md`
- `docs/runbooks/controller-host.md`
- this handoff

## Proof inventory

- Differential: 279/279, sequenced.
- Store: 96/96 against live PostgreSQL.
- Controller API: 36/36 against live PostgreSQL.
- Approval boundary: eight adversarial mutants killed, including omitted nested
  stage and pipeline-post traversal, wrapper overreach, timeout-sibling leakage,
  stage-timeout leakage into its own post, invalid-timeout acceptance, removal of
  controller preflight, and preflight-before-legacy-replay.
- Database pair: sixteen concurrent same-database probes pass; a different fully
  migrated database on the same PostgreSQL cluster is rejected.
- Authoritative outgoing-tree gate: 920/920 plus all blocking audits and drills;
  the exact command uses the FG-094 baseline and Jenkins oracle listed below.

Authoritative environment:

```text
FOGELL_REGRESSION_BASELINE=/home/srikanth/projects/fogell-baselines/fg-094-candidate/baseline.json
FOGELL_JENKINS_ORACLE=/home/srikanth/projects/chengis/chengis/test/resources/jenkins-oracle/Jenkins-RAnvil-Chengis-228-projects.tsv
```

The incoming `6281d4f` hosted gates were green but are historical after the
outgoing commit: push run `33033547756` / job `98391202409`, and pull-request run
`33033551208` / job `98391213497`. Require new runs on the outgoing exact head.

## Execution board snapshot

The board has 209 ticket rows: 129 done and 80 open. Open priorities are P0=5,
P1=27, P2=36, P3=12. FG-224 remains P0/PARTIAL; its row and ticket contain the
Review 33 closure and 920-test accounting.

## Next custodian checklist

1. Verify the outgoing commit signature, tree, clean worktree, and remote branch.
2. Require both push and pull-request `gate` runs on that exact SHA to finish
   green; older green runs are not substitutes.
3. Require fresh Codex and Copilot coverage on that exact SHA. Confirm zero
   current non-outdated unresolved threads; outdated historical threads may stay.
4. If a new exact-head finding lands, treat it as a bounded continuation of
   FG-224. Do not open a new dimension from this handoff.
5. Do not merge without explicit owner direction.

## Cleanup and residual boundaries

The disposable Review 33 PostgreSQL container is removed after final local
verification. No evidence directory was added for this review-only closure.
Remote agents, authenticated approval brokerage, mTLS, HA election, packaging,
and automatic replay of ambiguous external effects remain outside FG-224.
