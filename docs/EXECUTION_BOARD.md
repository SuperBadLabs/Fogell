# Fogell execution board

Mission: a faster and better Jenkins. Near-100% compatibility is the goal;
`docs/adr/0001` defines what "compatible" is allowed to mean.

## Wave 0 — evidence before code

| id | pri | status | item |
|---|---|---|---|
| FG-000 | P0 | TODO | Differential harness: pinned Jenkins vs Fogell, same host, compare terminal result + ordered steps + canonical workspace hash. Seal receipts. (ADR 0004) |
| FG-001 | P0 | TODO | Import Forge's two parsers + interpreter as the front end; verify 214/228 acceptance reproduces on HeMan |
| FG-002 | P0 | TODO | Bound admission: source bytes, node count, nesting depth. Forge's FParsec parser currently has no measured limits |
| FG-003 | P1 | TODO | Bisect the 14 known Declarative rejections (`docs/FOGELL-DAY1-BACKLOG.md`) via minimal repros, never reported error position |

## Wave 1 — durable spine

| id | pri | status | item |
|---|---|---|---|
| FG-010 | P0 | TODO | Append-only per-step journal, fsync batched at stage boundaries (ADR 0003) |
| FG-011 | P0 | TODO | Exactly-once resume: fenced so a resumed build cannot double-apply an external effect |
| FG-012 | P1 | TODO | Re-measure the step ladder on the **durable** path; retire the non-durable numbers from any claim |

## Wave 2 — the behaviors we promised to beat

| id | pri | status | item |
|---|---|---|---|
| FG-020 | P0 | TODO | Reap the step's process group at step end, with an opt-out (Jenkins leaks `nohup` children) |
| FG-021 | P0 | TODO | Dead-process detection in seconds with a diagnostic naming the restart (Jenkins: ~10 min, `exit code -1`) |
| FG-022 | P1 | TODO | Secret masking that is not encoding-specific, and never silent |
| FG-023 | P2 | TODO | No hidden scheduling latency; quiet period defaults to 0 and is visible |

## Wave 3 — step coverage by measured demand

Ranked by corpus set-cover, not popularity. Among the 119 Jenkins-ready files:
`archive` 34, `junit` 26, `credentials` 22, `timeout` 21, `input` 18, `dir` 15,
`parallel` 15, `withEnv` 11, `stash` 7, `retry` 6.

## Standing rules

- No scalar compatibility percentage, ever (ADR 0001).
- Every claim cites a sealed receipt.
- Corpus manifest verified before any scoring run.
- Incomplete pattern matches fail the build.
