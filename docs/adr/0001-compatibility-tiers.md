# ADR 0001: Compatibility tiers

Status: Accepted

Three tiers, never collapsed into one number:

1. **Proven compatible** — a differential receipt exists against a pinned
   Jenkins version: same input, same workspace hash, same terminal result.
2. **Accepted** — parses and executes, parity unproven.
3. **Rejected** — named error code and source position.

False success is more damaging than explicit rejection. Evidence: the prior
engine reported 146 non-empty IRs and had exactly **5** files with proven
parity; a single percentage would have implied 64%.

Binary plugin compatibility is not promised. Plugin *steps* are implemented
natively, ranked by corpus demand, not by popularity lists.
