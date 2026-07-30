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
- Each attempt gets a fresh normalized workspace under a canonical root;
  absolute paths, traversal and symlink components are rejected.
- The step's stdout/stderr are written locally, fsynced, and hashed; the
  controller consumes by offset and survives reconnect with no loss.
- Session and certificate epochs increase monotonically; only the current
  session may act.
- Linux uses process groups and cgroups; Windows uses Job Objects. Timeout and
  cancellation signal the whole group with SIGTERM, wait a configured grace,
  then SIGKILL — matching the trappable-SIGTERM contract in ADR 0005.
- Process groups are lifecycle containment, not a hostile multi-tenant
  boundary. Untrusted multi-tenant workloads require VM-level isolation.
