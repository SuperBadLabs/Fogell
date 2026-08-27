# ADR 0002: Interpret the AST; do not lower to a static IR

Status: Accepted

Fogell interprets the parsed pipeline AST directly. It does not compile to a
static, validated IR.

**Evidence.** Lowering the Forge AST to a McLoving-style static IR was measured
across the corpus: of 105 files that parsed, **78 (74.3%) could not be lowered**
without evaluating Groovy at admission time. Blocking constructs, by file count
among the 119 Jenkins-ready: scripted `node{}` 44, method dispatch 40, `if` 35,
arithmetic 33, `script{}` 32, user functions 21, try/catch 18.

Post-lowering coverage collapsed from 88.2% to **22.7%** — indistinguishable
from a no-Groovy strict-schema engine at 24.4%. The front end does not move the
number if a static IR sits behind it.

**Consequence.** Fogell inherits the sandbox problem: interpreting untrusted
Groovy requires a capability-bounded interpreter, not a general one. That cost
is accepted deliberately; it is the price of the tier-1 claim in ADR 0001.

The interpreter boundary is a closed value model plus deny-by-default call and
method admission. FG-072 keeps the explicit escape-name inventory and exercises
it through the parser, interpreter, and host, including the requirement that a
typed denial names the attempted capability, states the capability boundary,
and halts before a successor step. A positive control keeps sanctioned
registered steps, script-defined functions, and pure interpreter builtins
reachable; refusing every script is not security assurance. Six record/workspace
predicates are covered by six planted states: separate
non-failure and duplicate-terminal records prove that terminal value and
uniqueness are independently enforced, beside typed classification, attempted
name, boundary reason, and successor halt. Two more planted statuses prove timeout
and signal termination cannot masquerade as a completed proof. Admission occurs
before a null-safe method call may short-circuit on a null receiver; only argument
evaluation remains lazy after admission.

This boundary is deliberately narrower than agent or deployment isolation.
Registered steps are sanctioned capabilities, so the sandbox proof does not
vouch for their implementations or for commands launched by `sh`/`bat`. OS
account permissions and no-egress enforcement, secret delivery and masking,
syntax rejected by the parser before evaluation, and evaluator time/work/depth
budgets are separate controls. The sandbox proof also does not establish VM
containment or hostile multi-tenant isolation.
