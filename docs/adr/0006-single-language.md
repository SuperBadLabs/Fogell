# ADR 0006: F# is the only primary language

Status: Accepted

Every production component — parser, interpreter, scheduler, journal,
execution, agent, API, CLI, web — is F# on .NET. There is no second managed
runtime, no JVM compatibility worker, and no polyglot boundary inside the
product.

**Rationale.** The measured 74.3% lowering tax was created by a front-end /
engine split (ADR 0002). A second runtime reintroduces the same class of cost in
a different place: process hops, serialization, two deployment units, two
supply chains, two failure models. The prior engines each paid for a boundary
they did not need.

**Consequences accepted.**
- Anything Jenkins does via a JVM plugin must be reimplemented natively or
  rejected with a named code. There is no plugin bridge.
- Native OS work (process groups, cgroups, Job Objects) is done through P/Invoke
  from F#, isolated in a single project per platform, with every entry point
  documented — mirroring how McLoving confines its 21 `unsafe` blocks to one
  Windows FFI crate.
- Build-time tools and harnesses may be Babashka or shell. They are not
  products and hold no runtime authority.
