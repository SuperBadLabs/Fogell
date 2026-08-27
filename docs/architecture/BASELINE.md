# Measured baseline

Every figure below was measured by Claude on **luigi** (56 cores, root on SSD,
fsync 7.68 ms) against **Jenkins 2.568.1** and a **hash-pinned 228-file
corpus** of real public Jenkinsfiles. Evidence and harnesses:
`/sn8100/work/exchange/jenkins-behavior-spec`.

## The honest denominators

| gate | files |
|---|---:|
| corpus total | 228 |
| Jenkins Declarative-valid | 80 |
| Jenkins compiled / CPS entry | 199 |
| **Jenkins reached agent scheduling** | **119** |
| execution parity proven by any engine | **5** |

`119` is the only denominator worth scoring against: it is the set real Jenkins
was actually ready to execute.

## Front-end acceptance (`forge validate`, real dispatch)

| | result |
|---|---:|
| all 228 accepted | **214 — 93.9%** |
| typed Declarative path | 194 |
| scripted/Groovy path | 20 |
| rejected | **14** (all Declarative path) |
| of the 119 Jenkins-ready | **109 — 91.6%** |

Two independent parsers: `Pipeline.Parser` 995 lines (typed Declarative, fixed
schema) and `Groovy.Parser` 705 lines + interpreter 770 lines (escape hatch).
Dispatch is one regex on `pipeline\s*\{`.

## Engine, non-durable path

| | Forge | Jenkins | McLoving |
|---|---:|---:|---:|
| startup | 0.24 s | ~25 s | 1.0 s |
| idle RSS | 60 MB | 1,571 MB | 11.4 MB |
| 1-stage end-to-end | 0.40 s | ~3.5 s | 0.20 s |
| per in-engine step | ~0 ms | 54 ms | — |
| per subprocess | 2.17 ms | — | — |
| steps per stage | 400 exercised ([FG-037](../tickets/FG-037.md)) | **250 succeeds; 251 fails pre-effect** ([FG-037 evidence](../../evidence/20260827T170549Z-fg037-step-ceiling)) | 1 |

The flat step ladder is the **non-durable** path (`forge run` persists nothing).
It must be re-measured once per-step durability lands.

## Durability economics

Jenkins' per-step cost is **~98% disk** — 54.13 ms/step on SSD vs 0.89 ms on
tmpfs, i.e. roughly **6.9 fsyncs per step**. Targets on luigi-class storage:

| design | ms/step | vs Jenkins |
|---|---:|---:|
| 1 fsync per step | 7.70 | 7× |
| group commit, 1 per 10 steps | 0.79 | 69× |
| 1 fsync per stage | 0.40 | 134× |

PostgreSQL group-commits this automatically: measured 1.02 syncs/txn at 1
client falling to **0.048 at 64 clients** (166 → 3,680 tps).

## The gap that defines the project

**93.9% accepted vs 5 files proven.** Chengis reached 98.3% parse with the real
Apache Groovy compiler and still proved parity on only 5 of 228. Acceptance is
cheap; parity is the product.
