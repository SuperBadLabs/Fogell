---
title: Handoff — McLoving absorbs from Fogell
audience: next session
category: engineering-board
purpose: Carry the 2026-09-05/06 measurement campaign into a McLoving-led session. States the decision, its evidence, exactly what to port, what NOT to port, the live rigs inherited, and the traps that cost time.
lifecycle: live
last-verified: 2026-09-06
---

# Handoff — McLoving absorbs from Fogell

## 0. FIRST: land the evidence

**Ten unpushed commits** hold the entire campaign, on branch
`claude/fogell-deploy-metrics-daa36f`, worktree
`/home/srikanth/projects/fogell/.claude/worktrees/fogell-deploy-metrics-daa36f`,
based on `e60cde34`. Push them before anything else — nothing below is
reproducible without them.

    ed2c9e9b  SCM configured — 306/307 live, one sealed receipt no longer reproduces
    c9ca167b  live parity re-derivation — 12 corpus files are false rejections
    1fdd31b8  crash test against Controller.Host
    56847c48  crash consistency — no engine duplicates
    7cf1254a  under concurrent load the engine is noise
    2a37b939  three-engine standoff inverts the microbenchmark
    be0b2006  Fogell and Jenkins build Jenkins core 2.577, and tie
    e65051ef  the per-stage regression is 3 extra process spawns
    64781296  bisect the per-stage regression to 16982e1a (PR #261)
    9ce381fb  2026-09-05 face-off on mario and luigi

Evidence lives in `bench/faceoff/2026-09-05-*`, `bench/jenkins-build-2026-09-05`,
`bench/standoff-2026-09-06`, `bench/crash-consistency-2026-09-06`,
`bench/parity-2026-09-06`. Every directory has a `PROVENANCE.md` stating its own
limits, including the runs that were void and why.

## 1. The decision

**McLoving is the vehicle. Fogell contributes two things and is otherwise done.**

Measured basis, not board-derived:

| finding | measurement |
|---|---|
| McLoving beats Fogell on real builds | 6 OSS projects, 36 builds, McLoving **1.28×** vs Jenkins on all six; Fogell **1.03×** |
| the microbenchmark ranks them BACKWARDS | trivial-step per-stage: Fogell 27.7×, McLoving 1.7× — inverted on real work |
| under concurrent load the engine is noise | 6 simultaneous builds: all three engines **and no engine at all** within 4% |
| McLoving's speed is NOT bought with weaker guarantees | crash mid-step: none duplicate. McLoving auto-resolves to `aborted`; Fogell needs an operator; Jenkins hangs |
| McLoving is further along | waves 0–3 closed incl. native product surface; Fogell's open rows are the same wave (UI, CLI, soak, backup/restore, reproducibility, partition) |

**Do not re-derive these.** They cost a full day and the provenance states the
limits.

## 2. What to absorb — exactly two things

### 2a. The Groovy interpreter — Fogell's only UNIQUE asset

Fogell's own board marks FG-072 `UNIQUE`: *"McLoving has no interpreter and
provably evaluates no Groovy"* — its compiler stops at
`CompilePhase/CONVERSION` and never evaluates. If McLoving's Wave 4 must
**execute** Jenkinsfiles rather than inventory and compile them, this is the
missing organ and Fogell is the only place it exists.

    src/Fogell.Groovy.Parser/       scripted parser (~705 lines)
    src/Fogell.Groovy.Interpreter/  structurally-sandboxed interpreter (~770 lines)
    src/Fogell.Groovy/              shared AST
    src/Fogell.Pipeline.Parser/     typed Declarative parser (~995 lines)

Dispatch between them is **one regex on `pipeline\s*\{`** (`Parser.looksDeclarative`).
Note the preamble handling: `preamble` skips shebang/`@Library`/imports/top-level
`def`, then `skipToPipeline` skips *anything* to `pipeline {`, and FG-188 CAPTURES
the skipped text so top-level helpers resolve inside `script { }`.

Total production F#: 38,198 lines over 60 files, 27% comments, test:production
ratio **0.90** (Jenkins core's is 0.13). It is small and unusually well tested.

### 2b. The differential/evidence apparatus — worth more than either engine

    tools/Fogell.Differential.Cli/  fogell-diff
    src/Fogell.Differential/        comparison walkers
    differential/cases/             300 cases → 307 expected receipts
    differential/receipts/          sealed receipts
    corpus/CORPUS-SHA256SUMS        the pinned 228-file corpus manifest
    scripts/jenkins-workspace-v2.sh the canonicalisation collector
    scripts/fsx/generate-scorecard.fsx  the scorecard/ledger generator (fflat)

And the doctrine, which is the part that actually transfers: ADR 0001's tier
discipline, **never publishing a scalar compatibility percentage**, fail-closed
with named error codes and source positions, and guards that refuse to summarise
when any arm failed.

## 3. What NOT to port

- **Forced RLS.** McLoving already has 19 migrations with forced RLS (per
  `docs/MCLOVING-SOURCE-SURVEY.md`). An earlier note in this campaign wrongly
  recommended porting Fogell's; it is duplicate work.
- **Fogell's acceptance-criteria style.** Its acceptances demand evidence
  *about Fogell specifically* — a differential receipt, a threat model discharged
  for Fogell, a byte-identical Fogell rebuild. That is why its own
  `DUPLICATION-AXIS-RECOMMENDATIONS.md` found only **1 of ~17** open rows
  borrowable from a sibling. Rigorous, and it makes a project structurally unable
  to benefit from anything outside itself. Do not rebuild that trap in McLoving.

## 4. Live rigs inherited

**mario** (12 cores, 31 GB, fast disk — the benchmark host):

| what | where | note |
|---|---|---|
| `jenkins-oracle-228` | `100.127.170.90:18080` | **SEALED. 0 executors. NEVER touch or restart.** |
| `jenkins-faceoff` | `100.127.170.90:18086` **and** loopback | bench Jenkins 2.568.1, 6 executors, **UNSECURED**; home `~/faceoff-jenkins-home` is an auth-disabled clone of `~/bench-jenkins-home`. Bind back to loopback-only when done. |
| McLoving controller+agent | `~/faceoff2/mcrun`, API `127.0.0.1:18492` | 3 procs; postgres `mcloving-faceoff2` on `:15499` |
| Fogell `Controller.Host` | `~/fogell-ctrl`, listens `18095` | postgres `fogell-crash-pg` on `:55450`; `ctrl-up.sh` / `ctrl-start.sh` provision and restart |
| build corpora | `~/standoff` (6 OSS projects, ~800 MB, warm `~/.m2`), `~/fogell-build-jenkins` (Jenkins 2.577 + Maven 3.9.9), `~/crashlab`, `~/parity` | |

**luigi**: `git daemon` on `9418` serving `~/fogell-scm/repo.git` (74 branches
incl. every `case/checkout-scm-*`) — **it was down before 2026-09-06 and I started
it**; `jenkins-bench :18085`, `ctrl :18084`, `jenkins-lab :18083`, agent `ag1`.

**McLoving source**: `/sn8100/work/forge/McLoving` plus ticket worktrees
(`-board-munch`, `-closure-gate`, `-closure-integrity`, `-deploy001`).
Board snapshot in Fogell's dossier is **2026-08-05 and a month stale** — 75 DONE
/ 29 PENDING / 2 ACTIVE, waves 0–3 closed, Wave 4 active at `SCM-001`.
**Re-derive it.**

## 5. Traps — the highest-value section

1. **McLoving's mTLS certs are issued `-days 1`.** They expired 2026-09-03 and the
   agent kept working for three days on an already-established session, then died
   on first restart. **Every McLoving number in this repo was produced by a rig one
   restart from dead.** Fixed via `~/faceoff2/mc-recert.sh` — regenerates CA,
   server, agent certs plus the identity binding **in place with 30-day validity**
   and restarts controller+agent **without touching the database** (`mc-up.sh`
   would `podman rm -f` the postgres). **Now expires 2026-10-06.** For SaaS,
   short-lived certs are correct — but they need automated rotation and expiry
   monitoring, neither of which exists here.
2. **Never pass `-Denforcer.skip=true`.** It disables `requireJavaVersion`, i.e.
   each project's own statement of what it needs. ActiveMQ declares `[24,)`
   ("we leverage MRJAR"); gagged, it would build a multi-release JAR without its
   multi-release classes and report success.
3. **`fogell-diff` needs FIVE hooks, not one**: `FOGELL_JENKINS_WORKSPACE(_CMD)`,
   `FOGELL_JENKINS_WIPE_CMD`, `FOGELL_JENKINS_ENV_CMD`,
   `FOGELL_JENKINS_GIT_VERSION_CMD`, `FOGELL_SCM_URL`. With one set: 292/307.
   With all: **306/307**. Source `scripts/jenkins-workspace-v2.sh` and copy the
   exports from `scripts/run-differential.sh`; write receipts to a scratch dir so
   committed sealed evidence is never overwritten.
4. **Pre- and post-FG-237 commits need SEPARATE `NUGET_PACKAGES` dirs** — their
   lock files pin different FSharp.Core hashes, and one shared cache gives NU1403.
5. **mario drifts ~40% within a session** on identical bytes. Any multi-build
   comparison needs several rounds of few heats, candidate order **reversed on
   alternate rounds**, and pooled medians. A conclusion that does not survive the
   reversed round is drift.
6. **`sh '...'` in a Jenkinsfile is a single-quoted Groovy string.** A command
   containing single quotes breaks it — and McLoving's YAML wrapper is immune, so
   the failure looks exactly like "McLoving is the only engine that works."
7. **Do not name a Python file `concurrent.py`** — it shadows the stdlib package.
8. **The governing lesson**: errors that crash cost a minute; errors that produce
   *plausible output* cost a conclusion. Four of the latter occurred in one day
   (`enforcer.skip`; an unmounted `/etc/java-21-openjdk` that turned a dead Jenkins
   into a "50× win"; a `strace -c` column parse that inverted a finding; the
   quoting bug). **Build guards that refuse to summarise when any arm failed** —
   that guard caught two of them.

## 6. Open questions — do not guess, profile

- **Why McLoving is ~1.28× faster on real builds is NOT established.** The
  log-volume hypothesis was tested and refuted (Fogell does not improve with
  output suppressed). Five mechanism guesses were made in one day and five were
  wrong; the one time a syscall profile was taken it found the answer immediately.
  **Profile first.**
- **`script-capture-escaped-descendant`**: a sealed tier-1 receipt that no longer
  reproduces — 12 consecutive failures across two hosts and three engine builds.
  Its digest is fresh, so the scorecard's freshness guard passes it. The behaviour
  changed between 2026-08-12 and 2026-08-30; **that window is unbisected**, and
  engine vs harness is not separated (they build from the same tree).
- **12 corpus files Fogell refuses that real Jenkins accepts** (`malformed_syntax`
  vs linter-valid). Ten of them were previously mischaracterised as "deliberate
  fail-closed tightening". They need per-file diagnosis.
- **59% of the corpus (135 scripted files) is unmeasurable** for acceptance parity:
  the Declarative linter cannot adjudicate them and getting Jenkins' verdict means
  CPS-compiling untrusted code.
- **Fogell over-accepts.** Given a Jenkinsfile with a quoting error, Jenkins
  refuses at `CompilationUnit.compile` and runs nothing; Fogell parses it and runs
  two stages including 19.7 s of Maven. No ledger case covers it. Whatever
  interpreter McLoving absorbs, it inherits this defect unless fixed.
- **SaaS**: neither project has hostile-tenant execution isolation. Fogell's own
  threat model says *"not yet a hostile multi-tenant execution service"* and that
  a **VM boundary** is required. McLoving's hardened rootless-Podman worker is
  closer but is not a hostile-tenant boundary either.
