# Provenance — 2026-08-29 three-engine face-off on mario

`mario-final.tsv` and `mario-final.log` are the output of one run of
`~/faceoff2/faceoff.py` on mario, completed 2026-08-29T23:58 local time,
comparing Fogell's `Run.Host`, a local McLoving controller+agent pair, and a
local Jenkins controller (`http://127.0.0.1:18086`) on the same per-stage
workload at sizes 50 and 100, five serial heats each, marginal cost by the
delta method (50 → 100).

**These copies are transcriptions, not the originals.** They were captured
verbatim from a terminal session reading
`mario:~/faceoff2/out/mario-final.{tsv,log}` on 2026-08-31 and committed on
2026-09-01 while mario was unreachable. Before treating byte identity as
load-bearing, diff them against the originals on mario.

Known limits of the run, stated by the harness itself or observable here:

- **Within-run ratios are the only portable claim.** Jenkins absolutes moved
  38% within a session for identical work; the calibration probe in the log
  exists to detect such drift across runs.
- **Engine build identities are not pinned.** The log records neither the
  Fogell commit, the McLoving commit, nor the Jenkins version — only the
  Jenkins URL. This is an operator measurement, not sealed differential
  evidence, and it carries no FG-005-style seal.
- **Sizes are capped at 100** because Jenkins cannot compile 400 steps in a
  stage (255-argument limit) or 250 stages (64 KB method limit), and
  McLoving's agent accepts exactly one step per stage. The comparison runs on
  ground all three engines can stand on.
- **Steps are trivial**, so the numbers measure per-stage engine machinery,
  not workload throughput.
