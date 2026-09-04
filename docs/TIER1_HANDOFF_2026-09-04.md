# Fogell tier-1 handoff — 2026-09-04 — from "focus exclusively on tier-1" to the fence

Status: **tier-1 moved from 1 to 5 of 228 in one tenure (2026-09-04T05:00Z
to 13:45Z), and the no-egress fence the operating contract names now
exists on both sides, proven per run.** Two tickets closed and accounted
DONE: FG-242 (the second and third corpus receipts, and the measured size
of the inert-surface pool) and FG-244 (the fence, the corpus lane, and the
fourth and fifth receipts under it), plus FG-244's post-merge follow-up.
Every merge went through protected `main`, the last three on the owner's
instruction to merge once the hosted gate was green.

This is the outgoing record for the tier-1 session: what was closed, what
was measured, what the fence is and is not, what the next tier-1 ninja
should do first, and the mistakes this tenure made so the next one does
not repeat them.

## Read this first

- Host: `heman`. Worktree used:
  `${HOME}/projects/fogell/.claude/worktrees/tier-1-focus-3e87df`, clean on
  `origin/main` at handoff. Once this handoff merges, nothing unpublished
  lives in it.
- `origin/main` observed before this handoff: `0872e259` (merge of PR
  #402). It moved under this session six times; fetch before branching and
  before the final gate, and expect to renumber a ticket (FG-241 was taken
  on main mid-gate; this tenure's became FG-242).
- Tickets carry their own publication records:
  [`tickets/FG-242.md`](tickets/FG-242.md) and
  [`tickets/FG-244.md`](tickets/FG-244.md). The runbook is
  [`runbooks/no-egress-fence.md`](runbooks/no-egress-fence.md).
- The ONE place the capability inventory lives is FG-242's exhaustion list.
  The board cites it; do not restate it anywhere.

## What was closed, in order

1. **FG-242** (PRs #377 → #379, merge `6a05fb3f`; accounting #380 → #382 →
   #384 → #386, merge `5a768d3e`). Two echo-only corpus files walked the
   tier-1 path: `hamitmizrak_Devops_Jenkinsfile` and
   `allcloud-io_jenkins-pipeline-tutorial`, PROVEN (tier 1) in seven seconds
   for the pair. The survey instrument was validated against FG-200's known
   answer before any number from it was read, then re-run without a
   bare-closure rule the verifier showed hid scripted `node { }` files.
   Result: exactly three corpus pipelines are echo-only and all three now
   hold receipts. Five capability classes gate the fourth receipt; that
   inventory is the ticket's and is cited from the board.
2. **FG-244** (PRs #389 → #392 → #394 → #395 → #396 → #397 → #398 → #399,
   merge `d7b7a65d`; accounting #401, merge `367abaa9`; follow-up #402,
   merge `0872e259`). The fence, the corpus lane, the executed-surface
   allowlist, and two `sh`-bearing corpus receipts under the fence:
   `cinqict_jenkinsdev` and `devops-ws_learn-pipeline-java`, the latter the
   first corpus receipt with a non-empty workspace, identical on both sides.

Ledger at handoff: `tier1=5`, `admitted=195`, `tier3=28`. The case count
is the generator's, not this page's: build it with
`scripts/build-audits.sh generate-scorecard` (the binary is gitignored),
then `FOGELL_CORPUS=/sn8100/work/exchange/crucible-gate/corpus scripts/bin/generate-scorecard --check`
prints it (301 of 301 at the time of writing, after the peer's FG-243 and
FG-133 receipts landed under this session).

## The fence, in one screen

- **Jenkins side.** `jenkins-lab` is a rootless podman container
  (slirp4netns) on luigi whose network namespace our user namespace owns,
  so `nsenter -t <pid> -U --preserve-credentials -n` enters it as our user
  and `nft` loads a ruleset there — no root, no change to the oracle. The
  rule passes loopback and the REPLY direction of established flows only;
  every new outbound SYN, UDP, and the original direction of any flow the
  container itself opened (even before the fence) is REJECTED in ~15 ms.
  The ruleset lives in the namespace and evaporates on container restart.
- **Fogell side.** HeMan ignores user-level `IPAddressDeny` and forbids
  unprivileged user namespaces (both measured), so the run is placed in a
  transient systemd user scope and a root nft rule keyed to that scope's
  cgroup allows only the oracle port and the collector's ssh port on the
  Jenkins host, plus loopback minus both stub resolvers. nft binds the rule
  to the cgroup's identity at load time, so it is loaded from inside the
  live scope, proven there, and deleted on exit after every process in the
  scope is killed.
- **The lane** (`scripts/run-corpus-differential.sh`) refuses, in order: a
  file outside the pinned corpus; a drifted manifest; a file whose sha256
  and stem are not on `differential/corpus-allowlist.tsv`; a missing CLI;
  a second lane of this user; an oracle that is busy or does not answer; a
  lease it cannot take (a `flock` on the Jenkins host bound to the lane's
  pid); an unreadable container start instant; a Jenkins fence that exists
  already, cannot be applied or cannot be proven; a Fogell fence that
  cannot be proven. It snapshots the allowlisted bytes and executes the
  snapshot, watches the lease holder and polls the namespace fence during
  the run, and promotes the run's receipts as a batch only after the fence
  is confirmed to have stood throughout and the container has not
  restarted. On exit it quiesces the container (only init and the
  earliest-started `jenkins.war` process survive) and removes the fence it
  applied; a failed quiesce leaves the fence up on purpose.
- **What it is not.** The Jenkins side is a network boundary: the merged
  rule held against everything the later verifier rounds and bot reviews
  tried, after two earlier Jenkins-side holes were found and fixed on the
  way (a backgrounded survivor fetching an external URL after the fence
  came down; a connection opened before the fence carrying a request
  through the first, stateless rule). Ten FG-244 PRs drew both bots, and
  the verifier ran well over a dozen rounds across the tenure, each
  recorded on the ticket.
  The Fogell side stops accidental egress from the executed surface and
  does NOT contain a hostile file: the same UID can hop through the
  collector's ssh key, `systemd-run --user`, passwordless sudo, or any
  address local to HeMan. That is a property of running Fogell as the
  operator (FG-073); the allowlist-by-reading rule is the control. Read the
  file before you allowlist it. The ticket's "Limits, stated" section is
  exact and current.

## Measured facts worth keeping (on the tickets with their probes unless noted)

- The inert-surface pool is exactly three files, all receipted. What gates
  the next receipt is FG-242's inventory — the one place that list lives —
  read with FG-244's "Limits, stated" and "What the fence would unlock,
  sized". This page does not restate it.
- A stateless `tcp flags ack accept` rule lets a connection opened before
  the fence carry data (an HTTP 301 came back through it); `ct direction
  reply` blocks it. Conntrack works in the rootless namespace.
- `nft` refuses a `socket cgroupv2` path that does not exist and binds to
  the cgroup id at load; a rule for a recreated scope silently matches
  nothing. Load from inside the scope; sweep only tables whose scope
  directory is gone and whose name carries your uid.
- Bash defers a trapped signal behind a foreground child; run the fenced
  command in the background and `wait` it. Errexit is live inside an EXIT
  trap; locals are gone when it fires. Each of these cost a measured
  failure before it was known. (Not on the ticket, measured by the
  verifier: a `wait` on a disowned pid returns at once without waiting —
  the lane comments say so beside the `disown`s.)
- A remote `flock … -c 'sleep infinity'` outlives its ssh client; hold the
  lease with `exec cat` on stdin fed by `tail --pid=<lane pid>`.
- `/proc/<pid>/stat` must be parsed after the comm field's closing
  parenthesis; a process named `a b` beat a naive field count.
- On HeMan, "loopback" means any address local to the host, LAN and
  tailscale included.

## Working mechanics learned (private memory holds the same)

- Build ad-hoc with an isolated `NUGET_PACKAGES` (the FG-237 lock hash is
  nuget.org's; the global cache holds the SDK library-pack copy and fails
  NU1403). `scripts/build-audits.sh generate-scorecard` and
  `tools/Fogell.Corpus.Score` want the same variable; `FOGELL_CORPUS` is the
  PARENT of `jenkinsfiles/`.
- Never kill a gate by pattern: `pgrep -f '^bash scripts/build-and-test.sh'`
  matches every worktree's gate. Filter by `/proc/<pid>/cwd`. This tenure
  killed a peer's gate twice before learning it.
- After a patch script, grep for the new text before running anything
  that kills. This tenure ran an impostor test against an unpatched
  quiesce and SIGKILLed the Jenkins JVM (podman records the container
  finished at 09:53:53Z, exit 137, and restarted at 09:54:08Z; home intact;
  the peer was told). Test a
  kill-selection change with the kill disabled first.
- Copilot never re-reviews a moved head, so every review-driven amend
  after the first push is a replacement PR. This tenure ran seven of them
  for FG-244's fence PR (eight PRs in that chain, ten for FG-244 with the
  accounting and follow-up), each finding real but smaller items. The
  peer's advice ended
  it: brief the verifier for a from-scratch FULL-SURFACE pass over every
  isolation and fail-closed claim BEFORE the first push, not a delta check.
- The owner's "merge it when the hosted gate is green" was given for #399
  and applied to its accounting and follow-up; post-merge bot findings
  were fixed in the next PR, and the last three are deferred and recorded
  in memory: a caller-set runtime dir that is group- or world-writable is
  accepted (Codex badged it P1 — the class is a lane lock in a shared
  directory, a denial-of-service on the lane, not a fence breach; probe it
  before inheriting either label), a symlinked runtime path with a
  trailing slash defeats the `-L` test, and the staged-temp removal in
  promotion runs under errexit.

## Boundaries the next ninja must preserve

- The lane is single-tenant across users and hosts: the lease enforces it
  for corpus lanes; the hand-written lane does not take the lease and must
  not run while a Jenkins fence is applied (its git-step cases reach the
  SCM daemon).
- A Jenkins fence found already applied is somebody else's to recover
  (`jenkins quiesce`, then `jenkins remove`); `apply` and the lane refuse
  to replace it.
- Membership in the corpus is not permission to execute. A new file goes
  on the allowlist only after a person has read it and written its
  executed surface in one line.
- Keep FG-242's inventory the one place the capability list lives.

## What was deliberately not done

- No toolchain was pinned in the container or on HeMan; the sixteen
  toolchain files stay unrun.
- No `writeFile` modelling, no approval orchestration, no shared-library
  loading: engine work outside a tier-1 tenure's remit.
- The three deferred items above, one of them P1-badged by its class.
- The fence is not persisted across container restarts on purpose: the
  lane proves it every time, and a run that skips the lane has no fence.

## Cleanup and ownership state

- Lab: `jenkins-lab` running (restarted once, 09:54:08Z), 8 executors, 28
  jobs, no fence applied, egress restored, lease lock free on luigi. No
  `fogell_fence_*` table on HeMan. Scratch databases `fogell-fg241`,
  `fogell-fg244` and `fogell-handoff` removed. No leaked temp directories
  of this lane.
- Branches: everything merged; the worktree may be removed.

## Safe opening move for the next ninja

1. Fetch; read FG-242's inventory and FG-244's "Limits, stated".
2. Decide which capability to pay for. Cheapest per receipt: pin a
   toolchain (`mvn` and `gradlew` cover the most files) in the container
   AND on HeMan, then re-read those files' `sh` bodies for what else they
   need, allowlist one, and run
   `scripts/run-corpus-differential.sh <corpus file>`.
3. Before the first push of anything that claims isolation, give the
   verifier the full-surface brief. One round, not nine.

## Later the same day — the FG-245 tenure (17:00Z onward)

Read with the sections above; this is the addendum, not a replacement.

- **Landed: FG-245, filed PARTIAL** (merged through PR #413 as `d06394cc`;
  Copilot coverage of the merged head is the one remainder, see the
  ticket). Four `sh`-bearing corpus files PROVEN tier-1 under the
  fence as FAILURE receipts on an empty workspace —
  `sixeyed_jenkins-pipeline-demos`, `llknur_jenkinsfile-pipeline-project`,
  `linuxacademy_cicd-pipeline-train-schedule-cd` and `-dockerdeploy` (the
  last two byte-identical). Ledger `tier1=9`, `admitted=191`, `tier3=28`;
  307 of 307 case receipts after the rebase onto #412. The ticket is
  [`tickets/FG-245.md`](tickets/FG-245.md).
- **The opening move above was tried and is closed off.** Both "next in
  line" scripted files are refused by the walker at 1:1
  (`no_pipeline_block`): every scripted `node { }` file waits on a scripted
  walker, not on the fence or a toolchain. The pinned-toolchain move was
  read, not built: `make`/`mvn` exist on HeMan only, `docker-compose` on
  neither, and `mvn` prints a wall clock, so it needs a comparison rule
  before pinning buys anything.
- **The class that paid instead:** a file whose FIRST shell command fails on
  the empty workspace, with the failure text emitted by a program both
  sides already have (coreutils, javac, dash). Read the file, predict the
  exact failing line, probe the command on both sides in the target
  locale, allowlist, run the lane. Each receipt proves the walk, the trace,
  the merged stream, failure propagation and stage skipping — not the
  intended build. Say so on the allowlist row.
- **Engine fix on the way:** durable-task 686 executes `script.sh.copy`
  (JENKINS-70874), so `$0` ends in `.copy`; Fogell ran `script.sh` and the
  gradlew file diverged on dash's `not found` line. `ProcessGroup.fs` now
  writes the copy beside the original and runs it; case
  `sh-script-identity` seals `$0` and its execute bit for a plain and a
  shebang script, and the original's presence beside the plain one. Any file whose output names `$0`
  (dash's `can't cd`, `not found`) depended on this.
- **Lane mechanics learned:** promotion is all-or-nothing on the
  differential's exit code, so never batch a file you expect to diverge
  with files you expect to prove — run it alone and read the divergence.
  A probe case runs through the single-case recipe against luigi without
  the fence (it only echoes) and its receipt goes to a scratch directory
  until the case is final: the seal binds the case digest, so a header
  comment added afterwards means a re-run.
- **Lab state at this handoff:** `jenkins-lab` up, no fence on either side,
  lease free, no `fogell_fence_*` table on HeMan; scratch database
  `fogell-fg245` on port 55446 (remove after the gate).
- **Next candidates, read this pass:** `ljpengelen_jenkinsfile` (127 lines,
  `cd back-end && bin/ci` first — dash's `can't cd` now names the same `$0`
  on both sides; read the whole file before allowlisting);
  `charlires_golang-docker-jenkins` if `make` is pinned in the container
  (GNU make's "No rule to make target" is the same text on 4.3 and 4.4 —
  verify); the scripted walker for `camiloribeiro_cdeasy` and
  `tomasbjerre` is the largest single unlock but is engine work.

## Later still — the FG-247 tenure (22:00Z onward)

Read with the two sections above; this is the second addendum.

- **Measured, PARTIAL until accounted: FG-247.** Three more failure
  receipts on an empty workspace under the fence — the `linuxacademy`
  `-autodeploy`, `-canary` and `-kubernetes` siblings of FG-245's pair, the
  same `./gradlew` line, one file per lane, eleven seconds each. Ledger
  `tier1=12`, `admitted=188`, `tier3=28`; 307 of 307 case receipts. No
  engine change. The ticket is [`tickets/FG-247.md`](tickets/FG-247.md).
- **The first pick above is closed off.** `ljpengelen_jenkinsfile` was read
  to the end: its first stage runs under `agent { dockerfile { … label
  "webapps" } }`. The lab's one node is labelled `built-in`; a hand-written
  probe job with that agent queued on `‘Jenkins’ doesn’t have label
  ‘webapps’` (cancelled and deleted), while `Run.Host` on the file ran the
  stage and failed at `cd back-end`. Fogell ignores every stage agent but
  `AgentUnmodelled`; Jenkins never starts. Not allowlisted, not run.
- **The class is exhausted at twelve, measured.** A `dotnet fsi` script over
  the built `Fogell.Pipeline.Parser` classified all 191 admitted files by
  their agents, `tools`, credentials and first executed leaf step: 122 are
  scripted (walker-refused), and of the 69 declarative files every one but
  the three receipted carries a blocker at or before its first command; the
  table is on the ticket. Fifteen declarative files run under a `label`,
  `docker` or `dockerfile` agent the lab cannot allocate and Fogell does not
  model — that is the one reading this pass adds beside FG-242's inventory.
- **Lane mechanics unchanged.** One file per lane; probe the first command
  on both sides as `sh -xe script.sh.copy` in an empty directory, not as a
  bare command, so `$0` is what the receipt will carry; `generate-scorecard`
  needs `tools/Fogell.Corpus.Score` built (`fogell-score`) as well as
  `scripts/build-audits.sh generate-scorecard`.
- **Lab state at this handoff:** `jenkins-lab` up (StartedAt unchanged since
  09:54:08Z), no fence on either side, lease free, queue empty, no job of
  this tenure on the oracle (three `probe-*` jobs there belong to another
  session), no `fogell_fence_*` table on HeMan.
- **Next candidates:** none in this class. The next receipt needs a
  capability from FG-242's inventory, an execution rule for unallocatable
  stage agents (refuse or model `label`/`docker`/`dockerfile`, which gates
  fifteen files), or `make` pinned in the container for `charlires`.
