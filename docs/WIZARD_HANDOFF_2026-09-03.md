# Fogell wizard handoff — 2026-09-03 — the low-priority end of the queue

Status: **seven tickets closed from the low-priority end of the queue in one
tenure (2026-09-02T16:40Z to 2026-09-03T05:10Z), every one merged into
protected `main` through a source PR and an accounting PR, with the board
accounted DONE.** The tenure ran in parallel with the custodian's FG-004b,
FG-233, FG-235 and FG-236 work and touched none of those files.

This is the outgoing record for the "grand wizard" session: what was closed,
what was found that the next wizard should know before choosing a ticket,
what was deliberately not done, and how to start without inheriting a stale
branch, a stale row, or a false narrative.

## Read this first

- Host: `heman`. Worktree used:
  `${HOME}/projects/fogell/.claude/worktrees/fogell-feature-work-f6ab08`,
  clean on `origin/main` at handoff. It may be removed; nothing unpublished
  lives in it.
- `origin/main` observed before this handoff publication:
  `8dcd7e0a` (merge of PR #368). Fetch before branching; it moved under this
  session eight times, three of them while a gate was running.
- The companion [`WIZARD_RECEIPT_2026-09-03.md`](WIZARD_RECEIPT_2026-09-03.md)
  binds every head, merge, hosted run and review identity. Each ticket's
  file under `tickets/` carries its own publication record.

## What was closed, in order

| ticket | P | class | what changed | source PR → merge | accounting PR → merge |
|---|---|---|---|---|---|
| FG-234 | P3 | tamper-evidence | the `recovered-seal:` hash binds the whole RECOVERED region and its position | #329 → `82760b37` | #330 → `4fcd536b` |
| FG-207 | P3 | accounting | grouped step-finish force had landed in PR #148 unaccounted; proof re-run | — | #333 → `f04df48f` |
| FG-123 | P3 filed, **A** measured | correctness | `ansiColor(<arg>)` evaluated as Jenkins does; six receipts | #345 → `9e32a710` | #348 → `24262d76` |
| FG-239, FG-240 | P2, P3 | effects on a refused model | unusable `timeout` unit validated before the SCM block | #357 (filing) → `8d318ff4`; #360 → `b16696ca` | #362 → `46c40c1c` |
| FG-203 | P3 | accounting + receipt | live env alias had been fixed in PR #148 unaccounted; second receipt | #365 → `cacb9d98` | #366 → `be32c7eb` |
| FG-129 | P2 | accounting + receipts | compile-refusal comparison had landed in PR #148 unaccounted; fifteen stale comments, two receipts | #367 → `94eb91ab` | #368 → `8dcd7e0a` |

Board at handoff: rows=233; DONE=173; open=60; open P0–P3 = 2 / 19 / 32 / 7.
Receipts: 296 sealed and verifying; scorecard 295 of 295 expected cases;
corpus tier1=1, admitted=199, tier3=28 of 228 (unchanged by this tenure).

## Three findings the next wizard should act on before choosing

1. **PR #148 left rows stale, and some still are.** The FG-224 branch merged
   on 2026-08-27 (`20d4b638`) with 65 agent commits for about thirty ticket
   ids. Four of its fixes — FG-207, FG-123a, FG-203, FG-129 — sat on `main`
   with TODO rows until this tenure; three sub-slices (FG-124a, FG-126a,
   FG-130a) are now recorded on their parent rows as PARTIAL. **FG-197
   (P1)** also landed there ("replace ordinary process polling with events",
   `0b5beec3`, with the `FG-197 event-driven process completion` tests) and
   its row still reads TODO; its acceptance demands a luigi re-measurement,
   and it is at the custodian's end of the queue. Before implementing any
   open ticket, run
   `git log --oneline origin/main --grep='<id>'` and
   `git log --format='%h %s' 20d4b638^1..20d4b638^2 | rg <id>`.
2. **Compile-shaped refusals ARE sealable.** Since `d27616c5` (FG-129,
   2026-08-24) a refused pair compares disposition, result and workspace
   hash; four receipts now carry `sealed-output: omitted`. Every "UNPROVEN by
   receipt (FG-129: seals none)" claim written after that date was stale;
   the surviving comments now read "sealable since FG-129 landed; none
   sealed for this shape". Each such shape (`timestamps` arguments and
   blocks, stage `retry` counts, `unstable` arity, duplicate sections,
   FG-132's duplicate `options`) is one lane run away from a receipt.
3. **The verifier refuted a narrative from git history.** FG-203's first cut
   said the filed construction was "never real". The read-only verifier
   found the fix commit, its receipt and its test, and the closure was
   rewritten. Measure, then read `git log`, before writing "not real".

## Measured facts worth keeping (each is on its ticket with the probe)

- Jenkins evaluates an option's argument before setting `TERM` — GString
  placeholders, `env.` reads and unquoted expressions alike — in the script's
  own binding, and BEFORE the `environment` block applies (`env.MAPNAME` over
  a declared `MAPNAME` is the text `null`, SUCCESS). A bare unknown name
  fails with `MissingPropertyException`. (FG-123)
- `timeout(time: 1, unit: 'NOPE')` at either level is a compile refusal
  (`Expecting "class java.util.concurrent.TimeUnit" for parameter "unit"`);
  nothing runs, not even `post`. (FG-239, FG-240)
- An alias of `env` follows the live environment on every read; a write
  through it inside `script { }` persists on Jenkins and is refused by name
  on Fogell (FG-178's boundary). (FG-203)

## Working mechanics learned (private memory holds the same)

- **Probing the oracle directly**: `luigi:18083` accepts anonymous REST;
  the crumb from `/crumbIssuer/api/json` is bound to the session cookie, so a
  client without a cookie jar gets 403 on `createItem`. Create a
  `flow-definition` job, build, poll `/1/api/json?tree=result,building`, read
  `/consoleText`, delete. The differential CLI's `Jenkins.fs` does the same.
- **`Fogell.Run.Host` reuses a journal**: a second run with the same journal
  path prints `already-terminal: success` and runs nothing. Fresh journal per
  run.
- **`setsid nohup <gate> &` makes `$!` a wrapper PID** that exits at once;
  wait on `pgrep -f '^bash scripts/build-and-test.sh'` instead.
- **Copilot reviews only the PR-open head.** Every review-driven amend after
  the first push means closing the PR and opening a replacement; FG-123 took
  three PRs. Do the adversarial self-pass and the verifier BEFORE the push.
- **Codex's hosted `hang-proof` job flaked once** on a docs-only PR (#366,
  FG-231 arm `container-stop-hangs`); `gh run rerun <run> --failed` passed
  and the PR merged. One occurrence; not filed.
- **Never chain `git commit --amend` after a `git rebase` that can
  conflict**: on FG-129 the chain amended the merge commit at the rebase
  base. `git rebase --abort`, then resolve and `--continue`.

## Boundaries the next wizard must preserve

- FG-234's provenance recipe (`recoveredRegionSeal` over `seal=…`,
  `recovered-lines=N`, every region line) and its position rule are
  load-bearing for the seal proof's arms 14.7–14.9; the writer and verifier
  share `recoveredBlockLines`.
- FG-123's render site sits after the option and step argument checks that
  precede the SCM block; the pipeline-level `timeout` argument check now
  sits there too (FG-239/240). Do not move either behind the checkout.
- FG-203's liveness comes from in-place mutation of registered env maps
  (`refreshJenkinsEnvMaps`), not from reads "through the cell".
- The queue-row rule governs the three Track tables only; Wave rows carry
  their mechanism by design (declined twice on Codex threads with the
  checker's own scope).
- A ticket's H1 keeps the defect as filed; the Status line carries the state.

## What was deliberately not done

- FG-197's accounting (P1, luigi measurement required).
- Rewording the refusal receipts' contract prose ("workspace FILE LISTING
  below" on an empty workspace) — the renderer's one definition, a re-seal of
  every refusal receipt.
- The `four values` comment in `script-env-alias-after-wrapper.Jenkinsfile`
  (the receipt seals the case digest).
- FG-124's non-numeric escape letters, FG-126's other invalid escapes,
  FG-130's remaining valued options, FG-133's refusal-reason classes: the
  next candidates from this end, each needing oracle probes first.

## Cleanup and ownership state

- No scratch PostgreSQL container from this tenure remains (`fogell-fg234-db`,
  `-fg207-db`, `-fg239-db`, `-fg203-db` were removed after their gates).
- No transient probe job from this tenure remains on `luigi:18083`
  (`fg123-probe-*`, `fg203-probe-*`, `fg-timeout-probe-*` were deleted after
  each measurement); the three older `probe-*` jobs there predate it.
- Twelve `claude/*` remote branches from this tenure are all merged; they are
  publication history, not authority to delete.
- No historical worktree, branch, container, image or volume of another
  session was touched.

## Safe opening move for the next wizard

1. Fetch `origin`, record `origin/main`, branch from it, and run
   `scripts/bin/audit-board-numbers` and `python3 scripts/audit-queue-rows.py`.
2. Pick from the low-priority end and check the id against PR #148's range
   and `git log --grep` before writing a line of code.
3. Measure on the oracle first (one probe per shape, jobs deleted after),
   then on `Fogell.Run.Host` with a fresh journal; write the case; seal it.
4. Self-adversarial pass, then the read-only verifier, fold, ONE gate, ONE
   push, `@codex review` at open, poll every check for `pass`, coverage,
   exact-head merge, separate accounting PR.
5. Remove the scratch database and any probe jobs before writing the
   accounting record.

The baton is clean: seven closures, each bounded by a receipt or a re-run
proof, none by a sentence.
