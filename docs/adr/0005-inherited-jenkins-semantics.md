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
- `env['NAME']` is **rejected** by Jenkins' script sandbox — `staticMethod
  org.codehaus.groovy.runtime.DefaultGroovyMethods getAt` is not an approved
  signature, and the build fails. Only `env.NAME` and `${env.NAME}` work. An
  automated review asked for the bracketed form to be supported; the receipt says
  supporting it would make Fogell RUN what Jenkins REFUSES, which is the same
  divergence direction rejected for stage-level `failFast`.
- `withCredentials` binds the credential's **value** into the named variable —
  measured: `env | grep -c '^TOKEN='` is 1 and `${#TOKEN}` is the secret's length —
  masks it in the log as `****`, and **unsets** it after the block. This is why
  FG-070's "no secret in the environment" design had to be demoted from the default:
  every real pipeline reads `$TOKEN`, so a path-only binding breaks lift-and-shift.
- `withCredentials([file(...)])` binds the requested variable to a **path** to a
  temporary file, not to the content. (Proven: `credentials-file`.)
- `withCredentials([usernamePassword(...)])` masks **both** the username and the
  password. (Proven: `credentials-userpass-masking`. A review asked for the username to
  be exported unmasked, citing an unverified comment of mine that said Jenkins does not
  mask it; the receipt showed Jenkins masks both.)
- `stash` with the default `allowEmpty: false` and no matching files **fails the
  build** — later steps do not run. (Proven: `stash-empty-fails`.)
- On a plain job — no SCM, not multibranch, first build, started by a user — every
  context-dependent `when` condition (`buildingTag`, `changeRequest`, `changeset`,
  `changelog`, `triggeredBy`, `isRestartedRun`) is **false** and its stage is skipped,
  with the build succeeding. (Proven: `when-context-conditions`.)
- `options { timeout(...) }` at pipeline or stage level **aborts** the build when it
  expires, exactly as the `timeout` step does; the following stage does not run.
  (Proven: `options-timeout-pipeline`, `options-timeout-stage`.)
- The data-bound parameter for `when { changeset }` and `when { changelog }` is
  **`pattern`**; `glob` is REJECTED with a compilation error. `triggeredBy` uses `cause`.
  (Proven: `when-scm-pattern-keys`. Recorded because I invented `glob`/`regexp` once and
  a wrong data-bound name inverts the gate in both directions — accepting what Jenkins
  refuses, and refusing what real Jenkinsfiles write.)
- `beforeAgent` / `beforeInput` / `beforeOptions` are DIRECTIVES, legal only directly
  under `when`. Nested inside `allOf`/`anyOf`/`not`, Jenkins refuses to COMPILE the
  pipeline, and its error names the complete set of valid conditionals: allOf, anyOf,
  branch, buildingTag, changeRequest, changelog, changeset, environment, equals,
  expression, isRestartedRun, not, tag, triggeredBy — all fourteen are modelled.
- A `when` block containing only directives and no condition is **rejected**:
  *"Empty when closure, remove the property or add some content."* (Measured.)
- `input` prints its message and `Proceed or Abort` and then WAITS. Under a `timeout`,
  the deadline expiring makes the build **ABORTED** and the following step does not run.
  (Proven: `input-timeout-aborts`.)
- A **stage's** `options { timeout }` bounds that stage's STEPS, not its `post`: the
  `aborted` arm still runs after the deadline expires. A **pipeline-level** timeout DOES
  bound `post`. (Proven: `cancellation-selects-post-arm`, `options-timeout-wraps-post`.)
- A `stash` is stored with the **build**, not in the workspace, which is what makes it
  survive `deleteDir()`. (Proven: `stash-unstash`.)
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
