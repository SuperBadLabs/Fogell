# Throughput under simultaneous load — 2026-09-06, mario

Single-build latency is the wrong axis for a CI server: real pipelines do not
have 500 stages, but a real server does have many builds arriving at once. This
launches all six projects SIMULTANEOUSLY on each engine, 12 cores, and measures
time until the last one finishes. A fourth arm, `bare`, runs the same six as
plain parallel shells with no CI engine — the floor, measured in the SAME
session so the engine split is valid.

## Result — the engine is noise

| arm | median wall clock | vs floor |
|---|---:|---|
| fogell | 339,516 ms | **−1%** |
| **bare (no engine)** | **342,375 ms** | — |
| jenkins | 344,575 ms | +1% |
| mcloving | 353,559 ms | +3% |

All four within 4%. **Fogell measured faster than running with no engine at
all**, which is impossible — the noise floor exceeds every difference here. The
honest reading is that under saturating concurrent load the engine contributes
nothing measurable.

**Every ordering from the sequential standoff dissolves.** McLoving led 6 of 6
by 1.28× when builds ran one at a time; here it is last, within noise.

## Two findings that outlast the numbers

**Concurrency bought only 24%, not 6×.** Six builds sequentially totalled
~446 s (Fogell); concurrently ~340 s. Each Maven build is already multithreaded
and forking, so six at once thrash 12 cores. `camel` alone takes 122 s; under
contention ~330 s. In every arm the slowest single build ≈ the total, so the
run is gated by one build fighting the other five.

**Regime decides the answer, and the realistic regime favours "engine doesn't
matter".** Bare `git-plugin` is 16.3 s; through the engines 51–73 s — on a
single short build the engine is the MAJORITY of wall clock. That does not
contradict this result, it is swamped by it. Once a box is CPU-saturated, which
is the normal state of a CI server, engine choice is noise.

The corollary is uncomfortable for both engines: if the box is saturated and
concurrency barely helps, the only lever that matters is **not running the work
at all** — caching, incrementality, test selection, build avoidance. Develocity's
build cache, which these runs deliberately disable, was worth 46 s on a 92 s
build: an order of magnitude more than any engine difference measured all day.

## Limits

n=2 per arm, and the noise floor demonstrably exceeds the differences — treat
this as "indistinguishable", not as a ranking. CPU was saturated hard; at
sub-saturation concurrency engine overhead would re-emerge. Throughput only:
fairness, queue behaviour, isolation and mid-flight failure handling are what
actually separate CI servers under load and none are measured here.

Jenkins was raised from 4 to 6 executors so all arms attempt equal parallelism.
Its 4-executor default is a deliberate governance feature; on 12 cores,
throttling may well beat running all six, and that is a separate test worth
running.

**Fogell was tested as `Run.Host`, a single-build runner with no scheduler.**
Fogell's server is `Controller.Host`, with lease-based scheduling and a worker
pool. Six `Run.Host` processes measure the executor, not the scheduler — and the
scheduler is the component a load test is really aimed at.
