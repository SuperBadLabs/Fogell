# Provenance — 2026-09-05 Fogell vs Jenkins face-off on mario

`mario.tsv` / `mario.log` are the output of one run of `~/faceoff2/faceoff.py`
on mario, 2026-09-05, comparing Fogell's `Run.Host` against a local Jenkins
controller on the same per-stage workload at sizes 50 and 100, five serial
heats each, marginal cost by the delta method (50 → 100). `mario-oldbuild.tsv`
/ `mario-oldbuild.log` are a second Fogell-only run of the same matrix, taken
within the same session against the engine build that was already deployed on
mario, as an A/B control.

These files were copied directly off mario in the same session that produced
them; they are not transcriptions.

## What was measured

| engine | marginal cost, median | min | estimator spread | within-run ratio vs Jenkins |
|---|---|---|---|---|
| Fogell `e60cde34` | 23.1 ms/stage | 20.1 | Δ13% | **17.2× faster** |
| Jenkins 2.568.1 | 398.1 ms/stage | 401.6 | Δ1% | — |

Calibration probe at n=100: Fogell 2.5 s, Jenkins 42.4 s.

## Engine identity — pinned, unlike the 2026-08-29 run

The 2026-08-29 face-off recorded neither the Fogell commit nor the Jenkins
version, and said so. Both are pinned here.

- **Fogell**: `e60cde34f7010c3166b1ef088069320bcc83965c` (`origin/main`, verified
  ancestor), Release, `net10.0` framework-dependent, built on HeMan with
  dotnet 10.0.301. The deployed tree carries `BUILD-IDENTITY.txt` recording all
  of this beside the assemblies.
- **Jenkins**: 2.568.1, image
  `docker.io/jenkins/jenkins@sha256:f4f65e6cd1405cd889b7f5ac33f9d5cdc2a099de6b87fe8a3933b9c5d53d1d02`
  — the same pinned digest as the sealed corpus oracle.

## Rig

Container `jenkins-faceoff` on `127.0.0.1:18086`, `--userns=keep-id --user
1000:1000`, home a fresh copy of `~/bench-jenkins-home` with jobs, workspaces,
build history and queue removed, and the security realm set to
`Unsecured`/`None` so the harness (which sends no credentials) can drive it.
4 executors, `quietPeriod` 0.

**No CPU or memory cap.** The sealed oracle runs capped at 8 CPUs / 12 GB;
capping the bench controller while Fogell runs unconstrained on the same box
would handicap Jenkins and inflate the ratio. Both engines had the whole host:
mario, 12 cores, 31 GB.

The sealed `jenkins-oracle-228` (0 executors, cannot execute) was never
started, stopped, configured or otherwise touched. It was verified up and
unmodified before and after.

## Limits

- **Within-run ratios are the only portable claim.** Jenkins absolutes have
  moved 38% within a session for identical work.
- **Steps are trivial** (`sh('true')`, one per stage), so this measures
  per-stage engine machinery, not workload throughput.
- **Sizes stop at 100**: Jenkins cannot compile 400 steps in one stage
  (255-argument limit) or 250 stages (64 KB method limit).
- **Fogell's estimators disagree by 13%** (23.1 median vs 20.1 min), just under
  the harness's 15% unreliability flag. Treat 23.1 as soft.
- This is an operator measurement, not sealed differential evidence. It carries
  no FG-005-style seal.

## Finding: Fogell's per-stage cost roughly doubled since 2026-08-30

Jenkins is a stable control across the two campaigns — **398.1 ms/stage today
vs 394.8 on 2026-08-29, a 0.8% difference** — so the host and rig reproduce.
Fogell over the same interval went **14.3 → 23.1 ms/stage**.

Because the 2026-08-29 engine build's identity was never recorded, that
comparison alone is weak. It was therefore re-measured directly: the engine
binary already on mario (built 2026-08-30 01:16) was run through the identical
matrix, on the identical rig, in the same session.

| engine build | marginal cost, median | min |
|---|---|---|
| deployed 2026-08-30 build | **11.8 ms/stage** | 11.2 |
| `e60cde34` (current main) | **23.1 ms/stage** | 20.1 |

That is a **~2× per-stage regression**, measured A/B rather than across
campaigns.

### Localisation — attempted, NOT resolved

An initial hypothesis that FG-245 (`86d6f570`, which makes every `sh` step
write, chmod, run and delete a `script.sh.copy` to match durable-task's
JENKINS-70874 behaviour) caused it was **tested and refuted**:

| commit | marginal cost, median | min |
|---|---|---|
| `ac3e934e` (FG-245^) | 22.8 ms/stage | 21.8 |
| `86d6f570` (FG-245) | 24.7 ms/stage | 21.8 |

Both already carry the regression, so it landed earlier. Three further points
were measured, one run of five heats each:

| commit | date | marginal cost, median | min | spread |
|---|---|---|---|---|
| `76072354` | 08-30 11:14 | 15.0 ms/stage | 17.0 | Δ13% |
| `16982e1a` | 08-31 02:12 (FG-224 process-lifecycle closure) | 24.7 ms/stage | 23.8 | Δ4% |
| `793b565e` | 09-01 04:20 | 18.8 ms/stage | 17.9 | Δ5% |

This points at the 08-30 → 08-31 window, but **it does not resolve it**: the
Sep-1 point sits between the two, which no single clean step explains, and the
unchanged 2026-08-30 build measured 14.3 ms/stage on 2026-08-29 against
11.8 today — a 21% swing on identical bytes. The between-build differences
being chased are the same size as the run-to-run spread. A real bisect needs
more heats per point than five.

**No regression cause is claimed here.** What is claimed is the A/B: current
main is about twice the per-stage cost of the 2026-08-30 build on this rig.
