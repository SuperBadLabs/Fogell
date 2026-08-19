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

## Engineering bastion

**HeMan is Fogell's engineering bastion.** The canonical working checkout is
`/home/srikanth/projects/fogell`. Investigation, editing, local model-assisted
review, build and test work happen there. HeMan owns the mounted corpus and
reaches the pinned Jenkins oracle on Luigi, so it is the only environment that
can run the whole pre-publication proof.

**GitHub is the publication boundary.** It holds protected history, final review,
required checks and merge state. It is not the normal edit-test loop, and a green
GitHub check is not a substitute for the HeMan gate: the hosted runner has neither
the corpus nor the pinned Jenkins oracle.

The working loop is therefore:

1. branch, inspect and edit on HeMan;
2. use HeMan's local Qwen agent as an additional review input where useful;
3. build, test and generate corpus/differential evidence from HeMan;
4. run the full local gate and inspect the final diff on HeMan;
5. publish the already-proven commit to GitHub; and
6. use GitHub only for final review, required checks and merge.

## Layout

    docs/adr/           numbered decisions, each citing measured evidence
    docs/architecture/  contracts
    docs/related-work/  sibling-project dossiers (McLoving), informational only
    src/                F# projects
    tests/              unit + differential
    corpus/             pinned Jenkinsfile corpus reference (hashes only)
    evidence/           sealed measurement receipts
    scripts/            harnesses

## Inherited measurements

See `docs/architecture/BASELINE.md` — every number was measured on luigi
against Jenkins 2.568.1 and a hash-pinned 228-file corpus.
