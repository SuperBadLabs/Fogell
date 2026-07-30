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
