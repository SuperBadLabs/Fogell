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
- Security claims and deployment assumptions are bounded by the
  [current-tree threat model](docs/THREAT_MODEL.md); an accepted ADR is not proof
  that every required control is implemented.

## Run it

`Fogell.Controller.Host` is a runnable, single-node Linux controller. On HeMan,
the fastest end-to-end proof builds the exact checkout, provisions an isolated
database and least-privilege runtime role, submits a Jenkinsfile over HTTP,
observes progressive logs, and waits for durable terminal success:

```bash
dotnet restore --locked-mode
dotnet build -c Release --no-restore
FOGELL_PG_CONTAINER=fogell-fg060a \
FOGELL_PG_PORT=55445 \
FOGELL_BUILD_CONFIGURATION=Release \
./scripts/prove-runnable-controller.sh
```

The final line begins `FG-224 PROOF PASS`. For a persistent controller and the
authenticated submit/status/log workflow, follow the
[controller host runbook](docs/runbooks/controller-host.md).

## Engineering bastion

**HeMan is Fogell's engineering bastion.** The canonical working checkout is
`$HOME/projects/fogell`. Investigation, editing, local model-assisted
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
5. publish the already-proven commit to GitHub;
6. obtain review of the exact head, then run
   `scripts/review-coverage.py --pr N` from HeMan; and
7. use GitHub only for final review metadata, required checks and merge.

## Layout

    docs/adr/           numbered decisions, each citing measured evidence
    docs/architecture/  contracts
    docs/THREAT_MODEL.md current controls, residuals, and hard non-claims
    docs/related-work/  sibling-project dossiers (McLoving), informational only
    src/                F# projects
    tests/              unit + differential
    corpus/             pinned Jenkinsfile corpus reference (hashes only)
    evidence/           sealed measurement receipts
    scripts/            harnesses

## Measured speed — three-engine face-off

One run on mario, 2026-08-29: the same trivial per-stage pipeline at sizes 50
and 100, five serial heats per engine per size, against a local McLoving
controller+agent pair and a local Jenkins controller. Marginal cost per stage
uses the delta method (50 → 100), so per-build fixed overhead cancels.

| engine | marginal cost, median | within-run ratio vs Jenkins |
|---|---|---|
| Fogell | 14.3 ms/stage | 27.7× faster |
| McLoving | 226.5 ms/stage | 1.7× faster |
| Jenkins | 394.8 ms/stage | — |

The within-run ratio is the only portable claim: Jenkins absolutes moved 38%
within a session for identical work. Sizes stop at 100: probing found Jenkins
cannot compile 400 steps in one stage (255-argument limit) or 250 stages
(64 KB method limit), and McLoving's agent takes one step per stage. Steps are trivial, so this measures per-stage
engine machinery, not workload throughput. This is an operator measurement
with engine builds unpinned, not sealed differential evidence; raw data and
limits: [bench/faceoff/2026-08-29-mario/](bench/faceoff/2026-08-29-mario/PROVENANCE.md).

## Inherited measurements

See `docs/architecture/BASELINE.md` — every number was measured on luigi
against Jenkins 2.568.1 and a hash-pinned 228-file corpus.
