# Fogell

End-to-end F# CI engine targeting near-100% Jenkins compatibility.

Named for Superbad's Fogell — the real identity behind the McLovin fake ID.

## Why end-to-end

The measured 74.3% lowering tax (Groovy AST → static IR) is an artifact of
splitting the front end from the engine, not a property of Jenkins
compatibility. An engine that interprets the pipeline AST directly never pays
it. See `docs/adr/0002-interpret-not-lower.md`.

## Non-negotiable boundaries

- Acceptance is not compatibility. Every compatibility claim requires
  differential evidence against a pinned real Jenkins.
- Unsupported behavior fails closed with a named error code.
- No scalar compatibility percentage is ever published.
- Durability is per-step and exactly-once, or it is stated as neither.

## Layout

    docs/adr/           numbered decisions, each citing measured evidence
    docs/architecture/  contracts
    src/                F# projects
    tests/              unit + differential
    corpus/             pinned Jenkinsfile corpus reference (hashes only)
    evidence/           sealed measurement receipts
    scripts/            harnesses

## Inherited measurements

See `docs/architecture/BASELINE.md` — every number was measured on luigi
against Jenkins 2.568.1 and a hash-pinned 228-file corpus.
