# ADR 0008: Agent protocol and execution boundary

Status: Accepted

Agents dial out to the controller over mTLS with a versioned protocol. Accepted
work is journaled locally before execution. Java is not required on agents.

**Evidence for the design.** The behavioral spec measured Jenkins' agent model
directly: a 46-second network partition mid-`sh` cost **zero** log lines,
because the step's output is written to an agent-local durable log and the
controller tails it from an offset. That mechanism — agent as durable
participant, controller as tailer — is the property to match, and it also
explains Jenkins' worst failure: with the process gone, the log stops growing
and it takes ~10 minutes to conclude anything.

**Therefore.**
- Each attempt gets a fresh normalized workspace under a canonical root.
  Fogell-managed path arguments are resolved by `Workspace.resolveUnder` and
  reject absolute paths, traversal and symlink components. Compatibility
  scanners that reproduce a pinned Jenkins plugin may follow links when the
  measured plugin does, but only through a ticket-specific, evidence-backed
  implementation; they do not redefine the workspace resolver's policy.
- The step's stdout/stderr are written locally, fsynced, and hashed; the
  controller consumes by offset and survives reconnect with no loss.
- Session and certificate epochs increase monotonically; only the current
  session may act.
- Linux uses process groups and cgroups; Windows uses Job Objects. Timeout and
  cancellation signal the whole group with SIGTERM, wait a configured grace,
  then SIGKILL — matching the trappable-SIGTERM contract in ADR 0005.
- Process groups are lifecycle containment, not a hostile multi-tenant
  boundary. Untrusted multi-tenant workloads require VM-level isolation.

The agent account and its VM are therefore the filesystem authority boundary,
not the lexical workspace directory. A pipeline shell already runs with that
account's filesystem permissions. Following a link in a measured compatibility
scanner must never grant controller credentials or broader OS permissions, and
must remain explicit in the ticket's scope and tests.

Ticket evidence establishes the pinned plugin behavior that motivates an
exception; it does not prove filesystem isolation or credential separation.
Those are deployment properties of the agent-account and VM boundary and must
be verified independently.

## Process environment boundary (FG-222)

Build processes do not inherit the controller process environment. At build
admission Fogell constructs an explicit baseline containing only:

- a fixed system `PATH` (`/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin`);
- a run-scoped, mode-0700 Fogell-owned `HOME` beneath an explicit execution root,
  selected by neither controller `HOME` nor controller `TMPDIR`; and
- Fogell's synthetic Jenkins metadata (`BUILD_*`, `JOB_*`, `WORKSPACE`,
  `EXECUTOR_NUMBER`, and `NODE_NAME`).

Pipeline/stage `environment`, `withEnv`, credential bindings, and SCM-produced
`GIT_*` values overlay that map explicitly. GString evaluation, shell launches,
and build-side Git consume the same map and may not fall back to ambient state.
Differential receipts render a run-scoped inherited value as its canonical token
only when that fold decided the comparison, preventing nonce-path reseals without
rewriting byte-equal literals.

Controller-side Jenkinsfile fetches are a different authority. They use an
opaque SCM launch profile containing only controller-approved Git transport
configuration; that profile is neither an input accepted by build launchers nor
an overlay on the build map. Password-bearing userinfo, query, fragment, and
unsafe decoded-path credential channels in controller SCM URLs are refused before
Git starts; username-only SSH userinfo is allowed. Credentialed
workspace checkout is not implied by this design: a future implementation must
materialize a credential-free prepared source outside the workspace rather than
hand controller SCM authority to Git running inside a build.

This isolates environment variables, not filesystem or network authority. A
same-account workload can still inspect paths the agent account can read, so
different-UID/VM containment remains required for hostile multi-tenant work.
