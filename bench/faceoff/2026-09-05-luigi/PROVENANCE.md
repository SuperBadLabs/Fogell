# Provenance — 2026-09-05 Fogell vs Jenkins face-off on luigi

`luigi.tsv` / `luigi.log` are the output of one run of `~/faceoff2/faceoff.py`
on luigi, 2026-09-05, comparing Fogell's `Run.Host` against the local
`jenkins-bench` controller (`127.0.0.1:18085`) on the same per-stage workload
at sizes 50 and 100, five serial heats each, marginal cost by the delta method.

Copied directly off luigi in the session that produced them.

## What was measured

| engine | marginal cost, median | min | estimator spread | within-run ratio vs Jenkins |
|---|---|---|---|---|
| Fogell `e60cde34` | 45.1 ms/stage | 42.9 | Δ5% | **13.5× faster** |
| Jenkins 2.568.1 | 607.2 ms/stage | 605.1 | Δ0% | — |

Calibration probe at n=100: Fogell 5.4 s, Jenkins 66.2 s.

## Why this run is the conservative one

luigi's storage is roughly four times slower on fsync than mario's
(`docs/architecture/BASELINE.md` records luigi at 7.68 ms; mario benchmarks at
1.9 ms). Fogell's per-step durable journal is fsync-bound, so slow storage
penalises Fogell more than it penalises Jenkins' own per-build bookkeeping.

The same engine build measured **23.1 ms/stage on mario and 45.1 on luigi**,
and the ratio fell from 17.2× to 13.5× accordingly. **13.5× is a floor, not a
headline**: it is what the engine does on degraded storage. Neither number
transfers to other hardware — only the within-run ratio is portable, and only
for the host it was taken on.

## Engine and controller identity

- **Fogell**: `e60cde34f7010c3166b1ef088069320bcc83965c` (`origin/main`),
  Release, `net10.0`, built on HeMan with dotnet 10.0.301, run against luigi's
  .NET 10.0.11 runtime. `BUILD-IDENTITY.txt` ships beside the assemblies.
- **Jenkins**: 2.568.1, pre-existing `jenkins-bench` container, 4 executors,
  already unsecured — **no security configuration was changed on luigi**.

luigi's copy of `faceoff.py` was older than mario's and lacked `--jenkins-url`;
mario's newer harness was installed (original kept at `faceoff.py.luigi-old`)
so both hosts ran byte-identical harness code.

## Limits

Same as the mario run: within-run ratios only; trivial steps measure per-stage
engine machinery, not throughput; sizes capped at 100 by Jenkins' compile
ceilings; operator measurement, not sealed differential evidence.
