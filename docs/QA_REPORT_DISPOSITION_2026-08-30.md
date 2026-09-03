# QA report disposition — 2026-08-30

This record disposes the independent three-lane QA report against `main` at
`de68454732ffe7b2622062c41fbaa57f8d40f9aa`. It distinguishes corrections made
in this change from confirmed residuals and observations that do not justify a
repository change. It does not import the external evidence bundle as an FG-005
seal and does not represent a new live Jenkins differential run.

## Corrections made

| Finding | Disposition |
| --- | --- |
| FG-224 claimed identity-bound inner cleanup while later paths signal numeric pids/process groups without revalidating recorded start ticks | Confirmed. FG-224 is PARTIAL and its overbroad DONE wording is retracted. |
| A LinuxKit zombie-only process group can produce `process_extinction_unconfirmed` after useful execution succeeded | Confirmed as a separate FG-224 blocker. The controller runbook now requires a reaping pid 1 and documents the reconciliation/artifact consequence. |
| FG-222 lacked the explicit complete-boundary Codex record required by its own acceptance contract | Confirmed. FG-222 is PARTIAL pending that named record. Current 946/946 primary-test and 287/287 recomputed-seal counts are kept distinct from the retained 282/282 live receipt builds. |
| FG-212 through FG-221 were absent from canonical wave accounting | Confirmed. Canonical P2 rows now include FG-212 through FG-220 as DONE and FG-221 as PARTIAL. |
| FG-135's canonical status cell disagreed with its row detail and queue | Confirmed. The canonical status is DONE. |
| Standing risk 1 said zero proven receipts | Confirmed. It now reports governed `tier1=1` of 228. |
| FG-085 hostile proof waited forever when inherited stdin remained open | Reproduced. The fake `psql` consumes stdin only for SQL-protocol calls; the complete 15-mutant proof passes with a never-EOF producer. |
| Dependency inventory boundary | Reproduced while running the gate from the custodian checkout. Repository audits now use Git's tracked-plus-nonignored-untracked inventory, retain an explicit nested-worktree exclusion, ignore operator-global excludes, and reject any other embedded Git repository before trusting `git ls-files`. |
| FG-037's expected-failure unlink prompted on a write-protected marker when stdin was a terminal | Reproduced. The unlink probe is explicitly forced and noninteractive; the complete root-workspace proof passes with a terminal. |

After those status corrections, canonical board accounting is `rows=223`,
`DONE=153`, `open=70`, with open P0–P3 `2 / 22 / 35 / 11`.

## Confirmed residuals, not closed here

- FG-224 still needs identity revalidation before every inner pid/process-group
  signal and regression coverage across managed cleanup, the EOF watchdog, and
  controller cleanup. The LinuxKit extinction probe must also distinguish
  zombie remnants from useful live producers. These are runtime changes, not
  documentation reconciliation.
- FG-228 remains TODO. The reported stash file- and directory-symlink traversal
  is consistent with its existing containment scope; no DONE claim was made.
- FG-026b remains the honest open P0 integration boundary: the Store primitive
  is not yet consumed by the production controller path.
- No live Luigi `run-differential.sh` invocation or 2026-08-30 sealed evidence
  bundle is claimed. The reported HeMan corpus/regression results remain an
  operator observation, not newly committed evidence.

## Findings that do not justify the proposed change

- The FSharp.Core NU1403 failure did not reproduce from a pristine current tree
  with SDK 10.0.301, `--locked-mode`, `--no-cache`, an isolated package cache,
  and nuget.org as the source; all 26 projects restored. Lockfiles are therefore
  unchanged. Reconsider only with a clean reproducible package-source record.

  **Corrected 2026-09-02 (FG-237).** The finding was real and this paragraph
  was wrong. The lock pinned FSharp.Core 10.1.301 to the hash of the copy in
  the SDK's implicit `FSharp/library-packs` source, not the nuget.org package;
  an isolated cache and `--no-cache` remove cached packages, not an implicit
  source, so the probe above could not see it. With
  `-p:DisableImplicitLibraryPacksFolder=true` the same restore fails NU1403 on
  HeMan. The lockfiles are regenerated and the property is now set in
  `Directory.Build.props`; see `docs/tickets/FG-237.md`.
- Malformed `withCredentials` requests returning 201 and then failing before
  credential-bound effects matches the current execution-time contract proved
  by FG-044b. Admission-time 422 with no durable build or consumed idempotency
  key is a separate policy enhancement, not a correction to FG-044b's claim.
- FG-044b's bounded closure and FG-228 handoff remain unchanged.

## Required publication evidence

This disposition is complete only when the focused proof, board/queue audits,
the locked full repository gate, exact-head review coverage, hosted checks, and
merge ancestry are recorded on the pull request. A green gate does not promote
FG-222 or FG-224 back to DONE; each retains the acceptance conditions stated in
its ticket.
