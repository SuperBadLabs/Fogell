# ADR 0005: Which Jenkins behaviors to match, and which to beat

Status: Accepted

Compatibility means matching Jenkins' *contracts*, not reproducing its defects.
Derived from a 48-entry black-box behavioral spec of Jenkins 2.568.1
(`/sn8100/work/exchange/jenkins-behavior-spec`).

## Match

- Interrupt is a **trappable SIGTERM** with a grace window before hard kill
  (measured ~4 s); scripts rely on trapping it.
- `retry(N)` is N total attempts, no backoff. A body that fails once and then
  succeeds is a **SUCCESS** build: a retried failure leaves no mark on the
  result. (Proven: `retry-exhausts`, `retry-succeeds` receipts.)
- `timeout`'s default unit is **MINUTES**, not seconds. `timeout(3)` is three
  minutes; an engine that assumes seconds is wrong by 60x in the direction that
  kills working builds. (Proven: `timeout-seconds` receipt.)
- `parallel` lets siblings finish; `failFast` interrupts them. Three things about
  it were **measured, not assumed**, and each corrected a wrong implementation:
  - `failFast true` is a **stage-level** directive, a sibling of `parallel`.
    Jenkins *rejects* it inside the `parallel { }` block —
    `Expected a stage @ line 9`. `parallelsAlwaysFailFast()` is the
    pipeline-wide equivalent.
  - Declarative Jenkins emits **no `[branchName]` prefix** on parallel branch
    output; that belongs to the scripted `parallel` map form. Adding one is not
    a courtesy, it is a divergence.
  - A build whose sibling was interrupted by `failFast` is **FAILURE**, not
    ABORTED. The failing branch is the cause; the interruption is collateral,
    and letting it dominate reports the wrong terminal state.
  (Proven: `parallel-siblings-finish`, `parallel-failfast`,
  `parallel-always-failfast` receipts.)
- `post` **selection and order**, measured across four consecutive builds of one
  job rather than read from documentation. Order:
  **always → changed → fixed → regression → result-arm → cleanup**.
  `changed` fires on a **first** build — with no previous result, Jenkins treats
  the result as changed. `fixed` needs a previous FAILURE/UNSTABLE and a current
  SUCCESS; `regression` needs a previous SUCCESS and anything worse now.
  (Proven for build #1 by `post-order-failure`/`post-order-success`; the
  history-dependent arms are measured but not receipt-proven — FG-049b.)
- A `when`-skipped stage leaves the build **SUCCESS** and its **`post` block does
  not run**. (Proven: `when-conditions`.)
- On a plain (non-multibranch) job, `when { branch … }` and `when { tag … }` are
  **skipped**, because `BRANCH_NAME`/`TAG_NAME` do not exist. This is what allows
  them to be modelled rather than refused. (Proven: `when-scm-and-equals`.)
- `withEnv` is genuinely block-scoped: after the block an added variable is
  **unset** and a shadowed one **reverts**. (Proven: `withenv-scoping`.)
- `timeout` bounds the **block**, not each step inside it. (Proven:
  `timeout-block-deadline`.)
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
