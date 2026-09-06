# Crash consistency — Fogell vs McLoving vs Jenkins, 2026-09-06, mario

Prices McLoving's measured 1.28× speed advantage: does it buy that by promising
less when an engine dies mid-step?

## Method

A three-stage pipeline appends one marker line per stage. Stage 2 writes its
marker **and then sleeps 45 s**, so a SIGKILL at t+15 s always lands *after the
side effect has happened* but *before the step is recorded complete* — the exact
window where at-least-once, at-most-once and exactly-once diverge.

The engine is then restarted and the markers counted.

| marker count for s2 | meaning |
|---|---|
| 1 | the effect happened once across the crash |
| 2+ | the step re-executed — **duplicate side effect**, at-least-once |
| 0 | impossible here; the marker precedes the sleep |

Kill target per engine: Fogell — the `Run.Host` process, resumed by re-running
with the SAME journal. McLoving — the agent, restarted via `mc-agent-up.sh`.
Jenkins — the container (`podman kill`), then `podman start`.

## Result — reproduced identically twice

| engine | s2 markers | engine's own verdict |
|---|---|---|
| Fogell `e60cde34` | **1** | `exit 3` — `needs-reconciliation: s2#0 — a started step has no recorded outcome; refusing to guess` |
| McLoving | **1** | `aborted` (terminal, reached automatically) |
| Jenkins 2.568.1 | **1** | **no terminal verdict in 420 s** |

**No engine duplicated the side effect.** None re-ran the killed step. On the
property that matters most — not doing the work twice — all three hold.

They differ sharply in what they do next:

- **Fogell refuses, and names why.** It detects a started step with no recorded
  outcome and declines to guess, exiting with a named condition. No duplicate,
  no invented verdict, but the build does not resolve: an operator must
  reconcile. Textbook fail-closed, and the cost is a human in the loop.
- **McLoving self-resolves.** The restarted agent discharged the orphaned
  attempt — *"the controller confirmed its fenced authority is disowned;
  terminal evidence is preserved in the journal and its spools are reclaimed
  under the terminal spool rules"* — and the build reached `aborted`. Terminal,
  no duplication, no human needed.
- **Jenkins leaves it hanging.** No terminal verdict after 7 minutes.

**None of the three resumed to completion.** s3 never ran anywhere. Crash
*recovery* was demonstrated; exactly-once *resume* was not, by anyone.

## Verdict on the question this was built to answer

**McLoving's speed does not appear to be bought with crash safety.** It matched
Fogell on no-duplication and beat it on reaching a terminal verdict without
intervention. The repeated caveat in earlier provenance — "McLoving may be
faster because it promises less" — is **not supported** by this test.

## Limits

- Two runs, identical both times. One crash shape only: SIGKILL mid-step.
- **Fogell was tested as `Run.Host`, a single-build runner.** Fogell's resume
  machinery — `effect_checkpoints`, restart-discovered admission, attempt-keyed
  retry journals with deterministic resume — lives in `Controller.Host` and was
  exercised by the FG-224 proof, not here. Run.Host's refusal is the runner's
  conservative behaviour, not Fogell's full story. **A fair test needs the
  controller.**
- **Jenkins was tested harder than its normal deployment.** The build ran on the
  built-in node inside the container that was killed, so controller and executor
  died together. `durable-task` is designed for the case where the executor is a
  separate agent that survives a controller restart.

## A void first run, and a latent rig failure it exposed

`run0-VOID.log`. Two of three arms were invalid:

- **Jenkins**: `/home/srikanth/crashlab` was never bind-mounted, so stage 1
  could not write and failed instantly. Markers empty at kill *and* end.
- **McLoving**: the agent could not restart —
  `agent certificate PEM is invalid: certificate is expired or not yet valid`.

That second one is a finding about the rig, not the engine. `mc-up.sh` issues
mTLS certs with **`-days 1`**. They were issued 2026-09-02 and expired
2026-09-03. The agent survived three days and produced every McLoving number in
this repo only because mTLS is validated at connect and its session was already
established. **The rig had been one restart away from dead since Wednesday while
appearing perfectly healthy.** The crash test found it by accident.

`mc-recert.sh` regenerates the CA, server and agent certs plus the identity
binding **in place, with 30-day validity**, and restarts controller and agent
without touching the database — `mc-up.sh` would `podman rm -f` the postgres
container and destroy McLoving's state.
