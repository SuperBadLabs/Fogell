# Fogell wizard handoff — 2026-09-04 — five more closures from the low-priority end

Status: **five tickets closed from the low-priority end of the queue in one
tenure (2026-09-03T21:35Z to 2026-09-04T14:20Z), every one merged into
protected `main` through a source PR and an accounting PR, with the board
accounted DONE.** The tenure ran in parallel with the custodian's FG-236,
FG-237/238, FG-242 and FG-244 work and touched none of those files; it held
merges for the custodian's FG-244 by agreement for about two and a half hours.

This is the outgoing record for the second "grand wizard" session: what was
closed, what the next wizard should know before choosing, what was
deliberately not done, and how to start without inheriting a stale branch, a
stale row or a false narrative. It continues
[`WIZARD_HANDOFF_2026-09-03.md`](WIZARD_HANDOFF_2026-09-03.md).

## Read this first

- Host: `heman`. Worktree used:
  `${HOME}/projects/fogell/.claude/worktrees/fogell-feature-work-c8bc79`,
  clean on `origin/main` at handoff. Nothing unpublished lives in it.
- `origin/main` observed before this handoff publication: `a31dd993` (merge
  of PR #404). Fetch before branching: six merges that were not this tenure's landed on
  `main` after `a068258d` (#364, #379, #386, #399, #401, #402), at least one
  of them (#364) while a gate of this tenure was running.
- The companion [`WIZARD_RECEIPT_2026-09-04.md`](WIZARD_RECEIPT_2026-09-04.md)
  binds every head, merge, hosted run and review identity. The FG-205,
  FG-241, FG-243 and FG-133 ticket files carry their own publication records;
  FG-140's identities live in the receipt only.

## What was closed, in order

| ticket | P | class | what changed | source PR → merge | accounting PR → merge |
|---|---|---|---|---|---|
| FG-205 | P2 | ordering semantics + accounting | `sort` follows Jenkins' hash fallback: cycles fault anywhere in a compared element, acyclic maps and mixed-class scalars refuse by name; the process-kill had been closed by FG-015b, unaccounted | #373 → `1a148032` | #375 → `2f2af43a` |
| FG-140 | P3 | accounting | the slashy-brace fix landed under FG-141 on 2026-08-18 with receipt `when-slashy-brace`; re-measured; its probe found FG-241 | — | #376 → `9d83e3c8` |
| FG-241 | P2 | correctness | an invalid regex pattern is Jenkins' catchable `PatternSyntaxException`, not `false`; the matching budget refuses by name | #381 → `0ba67215` (#378 superseded) | #385 → `db35bd11` (#383 superseded); flipped DONE with FG-243 |
| FG-243 | P3 | comparator | a multi-line exception message is the head's when two JVM-shaped frames confirm it; seals FG-241's `when` shape | #391 → `7a75e5bd` (#388, #390 superseded) | #393 → `b5209ab9` |
| FG-133 | P3 | diagnostics | an option refusal says why: unknown to Jenkins, known but unimplemented, or pipeline-only at stage scope, from the two measured Jenkins sets | #400 → `41c6058b` | #404 → `a31dd993` (#403 superseded) |

Board at handoff: rows=237; DONE=180; open=57; open P0–P3 = 2 / 19 / 31 / 5.
Receipts: 306 sealed and verifying; scorecard 301 of 301 expected cases;
corpus tier1=5, admitted=195, tier3=28 of 228 (the custodian's FG-242 and
FG-244 moved tier1 from 1 to 5 during this tenure).

## Findings the next wizard should act on before choosing

1. **Ticket ids move under you.** A concurrent session took FG-242 while this
   session's comparator filing was drafted as FG-242; it shipped as FG-243.
   Take the next id from `origin/main` right before committing, and grep the
   other live worktrees (`git worktree list`) for the id too.
2. **Review bots read the acceptance column literally.** FG-241's first
   accounting PR (#383) was closed on a Codex P2 because the filed acceptance
   named a sealed `when`-shape receipt that FG-243 had not yet made possible;
   the row stayed PARTIAL with the remainder named until FG-243 sealed it.
   Do not flip DONE while any item in the acceptance column is unmet.
3. **The claim audit parses any backticked token near "receipt" as a
   citation.** A test comment that abbreviated `compile-refusal-option-unknown-stage`
   as `-unknown-stage` failed the gate. Spell receipt names in full in source
   and test comments.
4. **The stale-reference audit matches a deleted LOCAL identifier against the
   word in any comment.** Deleting a local `rank` failed the gate on
   `Fogell.Domain/Status.fs`'s "Severity rank" comment; the comment was
   reworded.
5. **A peer session's cleanup killed this session's gate twice** with
   `kill $(pgrep -f '^bash scripts/build-and-test.sh')`. The peer now filters by
   `/proc/<pid>/cwd`; do the same, and coordinate over session messages at
   ticket boundaries — the hold for FG-244 cost little: only FG-133's gate,
   rebase and push waited on it.

## Measured facts worth keeping (each is on its ticket with the probe)

- Jenkins' `sort` is `NumberAwareComparator`: a map, a list or two scalars of
  different classes on a compared pair order by `hashCode()`; unequal
  elements that tie on hash come out reversed from either insertion order; a
  cyclic element anywhere in either side overflows (catchable Error). (FG-205)
- A regex pattern that does not compile throws `PatternSyntaxException`,
  intercepted by `Exception`, `IllegalArgumentException` and its own class,
  not by `ArithmeticException`; uncaught in a `when` expression it fails the
  build and the next stage is skipped for the earlier failure. (FG-241)
- On an agent, Jenkins narrates such a failure behind
  `hudson.remoting.ProxyException:` with a three-line message before the
  first frame; the comparator now treats up to eight message lines as the
  head's when two consecutive JVM-shaped frames follow. (FG-243)
- Jenkins 2.568.1 enumerates 39 valid option names at pipeline scope and 24
  at stage scope (a strict subset); `retry(2)` and `buildDiscarder(…)` at
  pipeline scope run; a pipeline-only name inside a stage block is a compile
  refusal with the stage list. (FG-133)

## Working mechanics learned (private memory holds the same)

- **Sealing one case without the full lane**: build
  `tools/Fogell.Differential.Cli` (assembly `fogell-diff.dll`) after any
  interpreter change, source `scripts/jenkins-workspace-v2.sh`, configure it
  for `luigi`/`jenkins-lab`, set `FOGELL_JENKINS_URL=http://luigi:18083`,
  and run the CLI on the one case file. Changing a case file's COMMENT changes
  its digest: re-seal.
- **Ad-hoc builds need a fresh `NUGET_PACKAGES`** since FG-237 pinned
  FSharp.Core to nuget.org; the gate already uses its own cache.
- **Do not amend-and-gate before grepping that every scripted edit applied.**
  Three fold scripts this tenure silently skipped `///`-prefixed comment
  lines or a wrapped sentence, and each cost a seven-minute gate.
- **Every claim you cite in an accounting record, re-read at the source.** The
  accounting verifier caught one Copilot verdict recorded as approval that was
  "needs a closer look", and two local gate passes claimed from logs that had
  been overwritten.
- **The hosted `hang-proof` job flaked again** on a docs-only PR (#404, arm
  `container-stop-hangs`, the same arm as #366); `gh run rerun <run> --failed`
  passed. Two occurrences now, both docs-only; not filed.

## Boundaries the next wizard must preserve

- FG-205: `Value.tryCompare` decides identity first, then same-class
  scalars, then the hash fallback (cycle → fault, acyclic → `Unorderable`).
  Routing Java hash order was argued against on the ticket; do not add it
  without measuring GString and Integer/Long hashing.
- FG-241: `RegexPatternInvalid` is in `catchRetryable`'s list and in the
  `try` statement's intercept set; the bound value's message is the host
  engine's, so no case may print it.
- FG-243: across a message span only two consecutive JVM-shaped frames
  confirm a head; the next-line rule keeps FG-002f's known limit. The
  remoting wrapper on the ErrorAction marker is accepted only behind `Also:`.
- FG-133: the two Jenkins option sets word the refusal only; the descriptor
  table and `stageHonouredOptions` still decide which names are refused.

## What was deliberately not done

- Pipeline-level `retry` stays refused (FG-053(b)); Jenkins runs it.
- Acyclic map/list/mixed-scalar sorts stay refused where Jenkins succeeds
  (FG-205's design).
- `e.class` and `e.message` on a caught exception value are unmodelled
  (`UnknownProperty`); a qualified type in a `catch` clause is a parse
  refusal.
- FG-124's non-numeric escape letters, FG-126's other invalid escapes,
  FG-130's remaining valued options: the next candidates from this end, each
  needing oracle probes first. FG-117 and FG-227 (P3 PARTIAL) and FG-051 and
  FG-053c (P3 TODO) are the rest of the low end.

## Cleanup and ownership state

- No scratch PostgreSQL container from this tenure remains (`fogell-fg205-db`,
  `-fg140-db`, `-fg241-db`, `-fg243-db`, `-fg133-db` and `fogell-handoff-db`
  were removed after their gates).
- No transient probe job from this tenure remains on `luigi:18083`
  (`fg205-probe-*`, `fg140-probe-*`, `fg241-probe-*`, `fg133-probe-*` were
  deleted after each measurement).
- Fourteen `claude/*` remote branches from this tenure (one per PR head)
  are merged or superseded; publication history, not authority to delete.
- No historical worktree, branch, container, image or volume of another
  session was touched.

## Safe opening move for the next wizard

1. Fetch `origin`, record `origin/main`, branch from it, run
   `scripts/bin/audit-board-numbers` and `python3 scripts/audit-queue-rows.py`.
2. Pick from the low-priority end; check the id against
   `git log --oneline origin/main --grep='<id>'` and the other live worktrees.
3. Measure on the oracle first (one probe per shape, jobs deleted after), then
   on `Fogell.Run.Host` with a fresh journal; write the case; seal it.
4. Self-adversarial pass, then the read-only verifier, fold, grep that every
   fold applied, ONE gate, ONE push, `@codex review` at open, poll every
   check for `pass`, coverage, exact-head merge, separate accounting PR.
5. Remove the scratch database and any probe jobs before writing the
   accounting record.

The baton is clean: five closures, each bounded by a receipt or a re-run
proof, none by a sentence.
