# ADR 0003: Per-step durability, exactly-once resume

Status: Accepted

Build progress is journaled per step. Resume replays from the last durable
step, not the last stage boundary.

**Evidence.** Forge today resumes at *stage* granularity and re-executes the
interrupted stage — at-least-once. Measured: SIGKILL mid-build, then
`forge resume` logged "1 stage(s) already done", skipped `pre`, and re-ran the
interrupted `nap` stage in full. For an idempotent test stage that is harmless;
for a deploy stage it is a double deploy. Jenkins resumes mid-*step* from a
serialized CPS continuation and is strictly better here.

**Cost is bounded and known.** Jenkins pays ~6.9 fsyncs/step (54.13 ms on SSD;
0.89 ms on tmpfs proves ~98% is disk). One fsync per step is 7.70 ms — already
7× better than Jenkins at identical guarantees. Group commit at stage
boundaries reaches ~0.40 ms/step, ~134×.

**Therefore:** append-only journal, fsync batched at stage boundaries, fenced
so a resumed build cannot double-apply an external effect. Any published
per-step latency must be measured on the durable path.
