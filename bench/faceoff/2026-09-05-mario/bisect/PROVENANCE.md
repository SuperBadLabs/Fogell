# Bisect — where Fogell's per-stage cost doubled

Follow-up to the 2026-09-05 mario face-off, which measured current main at
~2× the per-stage cost of the 2026-08-30 engine binary on an identical rig.

## Method

Eight first-parent candidates spanning 2026-08-30 → 2026-09-05, each built
Release on HeMan and staged on mario under `~/faceoff2/cands/<sha>`. The
harness's engine path was a symlink re-pointed per measurement, so nothing
else about the rig moved.

`bisect-driver.sh` runs **3 rounds × 5 heats** per candidate — 15 heats per
build per size, 30 rows per build — and **reverses the candidate order on even
rounds** so drift on the box does not land entirely on the builds measured
last. This matters: mario drifted substantially over the ~25-minute run. The
fast anchor `76072354` measured 984 ms at n=50 in round 1 and 1874 ms in
round 2 on identical bytes. Absolute times are not comparable across rounds;
pooled medians and the reversed ordering are what make the comparison hold.

## Result — the step is at `16982e1a`

Pooled over 30 heats per build (`pooled-summary.txt`):

| build | date | med n=50 | med n=100 | ms/stage |
|---|---|---|---|---|
| `76072354` | 08-30 11:14 | 1644 | 2395 | **15.0** |
| `b5f9edf3` | #234 | 1613 | 2374 | **15.2** |
| `de684547` | #235 | 1661 | 2254 | **11.9** |
| `66904473` | #240 | 1715 | 2478 | **15.3** |
| `16982e1a` | 08-31 02:12, **PR #261 FG-224 process-lifecycle closure** | 1996 | 3246 | **25.0** |
| `793b565e` | 09-01 | 2046 | 3198 | 23.0 |
| `ac3e934e` | 09-04 | 2262 | 3350 | 21.8 |
| `e60cde34` | 09-05 main | 2235 | 3419 | 23.7 |

Everything before `16982e1a` sits at 11.9–15.3 ms/stage; `16982e1a` and
everything after sits at 21.8–25.0. The n=100 median jumps ~800 ms at that
commit — about 8–9 ms per stage.

**The step survives adversarial ordering.** In round 2 the candidate list ran
newest-first, so the post-regression builds were measured while the box was
freshest and the pre-regression builds last. The step still appeared, between
`16982e1a` (25.5 ms/stage) and `66904473` (12.3). Ordering worked against the
conclusion and did not remove it.

`16982e1a` touches `src/Fogell.Execution/ProcessGroup.fs` (+532),
`src/Fogell.Controller.Host/ProcessGroup.fs` (+425),
`src/Fogell.Execution/Native.fs` (+104) and `tools/Fogell.Run.Host/Program.fs` —
the per-step process execution path, which is exactly what this workload
measures.

## Two mechanism hypotheses, both TESTED AND REFUTED

Neither is the cause. They are recorded so nobody spends the time twice.

**1. FG-245's `script.sh.copy`.** FG-245 (`86d6f570`) makes every `sh` step
write, chmod, run and delete a copy of the script to match durable-task's
JENKINS-70874 behaviour, and this workload is one `sh` per stage. Measured
either side: `ac3e934e` 22.8 ms/stage, `86d6f570` 24.7. Both already carry the
regression — it landed well before FG-245.

**2. The 20 ms poll intervals in `ProcessGroup.fs`.** `16982e1a` added
`Thread.Sleep 20` at three per-step sites (`waitForGroupExit`,
`waitForGroupExitExceptAnchor`, and the launcher-formation `pause`), which
predicted roughly the observed 9–10 ms/stage. A diagnostic build of
`e60cde34` with all three dropped to `Thread.Sleep 1` was measured alternating
against unmodified `e60cde34`, 3 rounds of 5 heats (`AB-*.tsv`):

| round | `e60cde34` | `e60cde34` + 1 ms poll |
|---|---|---|
| 1 | 21.6 | 80.3 (estimators disagreed 52%, discard) |
| 2 | 27.8 | 26.3 |
| 3 | 27.5 | 26.6 |

No effect. Those loops check once before sleeping, and the first check
evidently almost always succeeds, so the sleep rarely fires. **This was a
diagnostic probe, not a proposed fix** — a 1 ms spin would burn CPU to no
benefit.

## What is and is not established

**Established:** the regression enters at `16982e1a` (PR #261), and it is
roughly 15 → 25 ms/stage on this rig and workload.

**Not established:** which part of that ~1,100-line change costs the time. The
remaining candidates inside it — the `/proc` group scanning on every step, the
extra shell scaffolding in the generated launcher (gates, traps, authorisation
helper), and any additional process layer per step — were not tested.

**Caveat on the whole exercise:** the box drifted ~40% over the session. The
step is far larger than that drift and survives order reversal, but no absolute
number here should be quoted against a measurement taken on another day.
