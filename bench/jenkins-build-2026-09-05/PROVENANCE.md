# Fogell vs Jenkins on one real build — Jenkins core 2.577

2026-09-05, mario. The first real-workload comparison in this repo: every prior
face-off measured per-stage machinery with trivial steps.

## Result

Wall clock, trigger to terminal, 3 heats each, engine order alternating:

| engine | median | raw |
|---|---|---|
| Fogell `e60cde34` | **133,726 ms** | 124037, 134055, 133726 |
| Jenkins 2.568.1 | **136,102 ms** | 138567, 136102, 131968 |

**Difference: 2,376 ms on ~134 s — 1.8%, and the ranges overlap.** On this
workload the two engines are indistinguishable.

That is the expected result, and it reconciles with the microbenchmark rather
than contradicting it. This pipeline has 3 stages. At the measured 398.1 vs
23.7 ms/stage, Fogell's per-stage advantage is worth 3 × 374 ms ≈ 1.1 s — the
same order as the 2.4 s observed. The per-stage advantage is real; on three
stages it is simply worth about one second out of a hundred and thirty.

**The per-stage number only becomes material at high stage counts.** Jenkins
needs ~100 stages before its overhead (≈40 s) is visible against real build
work. Below that, the engine is not the bottleneck — the build is.

## What was built

Jenkins core 2.577 from `jenkinsci/jenkins` at the `PROJECTS.tsv`-pinned commit
`82c37634b354dd2832ae80772ef2548cc388671f` (tag `jenkins-2.577`). 4 reactor
modules, 67 goals. Artifact `jenkins-core-2.577.jar`, 7,987,007 bytes.

## Fairness controls

One **identical** `headtohead.Jenkinsfile` drives both engines. The container
bind-mounts the source tree, JDK 21, Maven and `~/.m2` at the *same absolute
paths* as the host, so neither the file nor the command differs by a byte.

- **`-o` (offline)** — neither engine is measured downloading from Maven Central.
- **Develocity build cache disabled** (`-Ddevelocity.cache.local.enabled=false`).
  Left on, it returned `6 goals from cache, saving at least 46s` and would have
  handed a free win to whichever engine ran second. Both now run
  `67 goals, 67 executed`.
- **`clean package`** — full recompile per heat; no incremental-state advantage.
- **Fresh Jenkins job per heat** — Jenkins' per-build cost grows with job history.
- **Order alternates per round** so drift does not land on one side.
- **Wall clock for both.** Jenkins' self-reported `duration` excludes queue and
  post-build bookkeeping a user waits through anyway. It is recorded (it tracked
  wall clock within ~700 ms) but not compared on.
- **No CPU or memory cap** on the Jenkins container, matching the face-off runs.

## Deviations — this is NOT "Jenkins' CI running on Fogell"

- **Not Jenkins' own Jenkinsfile.** Fogell refuses it: `malformed_syntax` at
  62:28, on `axes.values().combinations {` — a trailing closure argument. This
  is a substitute pipeline performing the same build.
- **No `checkout scm`** — Fogell has no SCM step; source is pre-cloned and
  referenced absolutely.
- **No plugin steps** — no `withMaven`, `junit`, `archiveArtifacts`, `timestamps`.
- **Tests skipped**, spotbugs/checkstyle/enforcer disabled. A real Jenkins core
  CI build runs all of these and takes far longer.

## A void first run, kept as a warning

The first matrix reported Jenkins at ~2 s per heat and a `0.02x` ratio. Jenkins
had not run the build at all — it died in ~2 s with
`java.lang.InternalError: Error loading java.security file`, because the JDK was
bind-mounted but `/etc/java-21-openjdk` was not, and Ubuntu's JDK `conf` entries
are symlinks into it.

**A fast failure is indistinguishable from a fast win in a ratio**, and the
driver summarised it without complaint. `headtohead.py` now refuses to print
medians if either arm recorded any failure. The face-off harness has an
estimator-disagreement guard for the same class of error; this driver had none.

## Limits

Three heats per engine, one workload shape, one host, one day. The shape matters:
a single long stage dominates, which is the case *least* favourable to Fogell's
per-stage advantage. A pipeline of many short stages would separate them. This is
an operator measurement, not sealed differential evidence.
