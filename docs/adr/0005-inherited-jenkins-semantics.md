# ADR 0005: Which Jenkins behaviors to match, and which to beat

Status: Accepted

Compatibility means matching Jenkins' *contracts*, not reproducing its defects.
Derived from a 48-entry black-box behavioral spec of Jenkins 2.568.1
(`/sn8100/work/exchange/jenkins-behavior-spec`).

## Match

- Interrupt is a **trappable SIGTERM** with a grace window before hard kill
  (measured ~4 s); scripts rely on trapping it.
- `retry(N)` is N total attempts, no backoff.
- `parallel` lets siblings finish; `failFast` interrupts them.
- Approval/`input` state survives a controller restart.
- Agent-side output is buffered locally and recovered by offset on reconnect: a
  46 s network partition cost **zero** log lines.
- `stash` storage is controller-side and survives `deleteDir()`.
- Progressive console: partial output served during a run.
- Retention prunes at build completion, immediately.

## Beat

- **No 250-step ceiling.** Jenkins fails to compile 251 steps in one stage
  (JVM 255-argument limit). Measured: 400 steps run fine.
- **Diagnose dirty-death data loss.** Jenkins' `PERFORMANCE_OPTIMIZED` mode
  fails silently on SIGKILL — truncated console, no resume attempt, no error.
- **Detect dead step processes in seconds.** Jenkins took ~10 minutes to fail a
  shell whose process died with the controller, then reported `exit code -1`
  with no mention of the restart.
- **Reap the step's process group.** Measured: `nohup`'d children survive both
  success and abort; `JENKINS_NODE_COOKIE=dontKillMe` is moot because nothing
  is killed.
- **Secret masking must not be encoding-specific.** Jenkins masks the literal,
  base64 and case-folded forms but leaks on `rev`, hex, substring and
  character-split — silently, with the build green.
- **No hidden scheduling latency.** Default `quietPeriod=5` plus a ~5 s queue
  cycle puts a ~10 s floor under trigger→start.
