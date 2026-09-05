# Mechanism: three extra process spawns per step

Follow-up to the bisect, which put the ~15 → ~25 ms/stage step at `16982e1a`
(PR #261, FG-224 process-lifecycle closure) without identifying what in that
change costs the time.

Measured on **HeMan**, not mario — the differential is a syscall count, so it
needs no benchmark host, and this avoids contending with any other session on
mario. Workload: 10 stages × one `sh('true')`, both engines under
`strace -f -c`.

## The result

| syscall | `76072354` | `e60cde34` | per stage |
|---|---|---|---|
| `vfork` | 10 | 40 | **+3.0** |
| `execve` | 41 | 71 | **+3.0** |
| `wait4` | 35 | 102 | +6.7 |
| `unlink` | 15 | 45 | +3.0 |
| `openat` | 358 | 489 | +13.1 |
| `mmap` | 1032 | 1549 | +51.7 |
| `mprotect` | 2038 | 2365 | +32.7 |
| **`fsync`** | **58** | **58** | **0.0** |

Current main spawns **four processes per step where the old build spawned
one**. The `mmap`/`mprotect`/`openat` increases are the ordinary cost of three
more process startups mapping their own libraries, not independent findings.

## Why this is the cost, and not durability

**`fsync` is byte-identical between the two builds: 58 and 58.** The durable
journal does exactly the same disk work before and after. Whatever doubled the
per-stage cost, it was not the price of durability.

The wall-clock delta is a near-constant *addition*, not a multiplier, and it
holds across two very different machines:

| host | `76072354` | `e60cde34` | delta |
|---|---|---|---|
| mario (30 heats, pooled) | 15.0 ms/stage | 23.7 ms/stage | +8.7 |
| HeMan (3 heats, median) | 3.7 ms/stage | 16.1 ms/stage | +12.4 |

A constant few-millisecond addition per step, tracking a fixed count of extra
`fork`+`exec` pairs, is what process-spawn cost looks like. A multiplicative or
storage-bound cost would not behave this way — note HeMan's old build is 4×
faster per stage than mario's, yet pays a *similar absolute* penalty.

## Hypotheses tested and refuted along the way

Recorded so nobody spends the time again.

1. **FG-245's `script.sh.copy`** — both sides of `86d6f570` already regressed.
   (It does show up here as part of the `unlink` +3.0/stage, but it is not the
   step.)
2. **The three 20 ms `Thread.Sleep` poll sites** added by `16982e1a` — a 1 ms
   diagnostic build tracks main exactly. Those loops check before sleeping and
   the first check almost always succeeds.
3. **The per-step `/proc` scan** (`scanLiveGroupMembers`, which enumerates every
   PID on the box and calls `getpgid` on each). Plausible, and wrong: the scan
   **predates** `16982e1a`. Both builds make ~32,630 `getpgid` calls for a
   10-stage run — old 32616/32638/32636, new 32675/32654/32640 across three
   paired runs. It is a real and surprising cost (~3,260 `getpgid` per stage,
   scaling with the box's process count) but it is **not** the regression.
4. **arm64 work.** The only arch-conditional change is `5a27fb93` (FG-238,
   O_DIRECTORY/O_NOFOLLOW per architecture), which landed ~09-04, four days
   after the step. The bisect shows no second step there: 23.0 → 21.8 → 23.7.

## What is still not proven

That *removing* the extra spawns recovers the time. The causal chain here is
inference from a matched count and a hardware-independent constant addition,
not a build with the supervision harness removed. Confirming it needs a
variant that supervises with fewer processes — which is a design question for
FG-224, not a measurement.
