# ADR 0009: Capability waves ranked by corpus demand

Status: Accepted

The board's binding track becomes capability: what the engine must be ABLE TO
DO before another corpus file can be proven, ranked by how many corpus files
each capability unlocks. Receipt walking (Track 2) stays the measurement that
says whether a capability bought what its count promised; it no longer orders
the work, because it has run out of files it can reach.

**Evidence for the design.** Every capability-free pool in the 228-file corpus
is exhausted, and each exhaustion was measured rather than assumed. FG-242
counted the inert-surface pool (files calling nothing but `echo` with literal
arguments) at **3**, all receipted. FG-247 surveyed every admitted file with the
engine's own parser and counted the no-toolchain class (first shell command
fails identically on an empty workspace) at **12**, all receipted, with no
thirteenth. The four receipts since — FG-254, FG-257, FG-258, FG-261 — each
had to BUILD or PIN something before the file could run: an exact GNU Make 4.3
on both engines, a two-space `stages` prerequisite, a hosted `println`,
pipeline-environment expression evaluation. FG-254 also measured what one
toolchain pin costs: a network-disabled, digest-checked derivative of the pinned
Jenkins image, built and receipted for one file.

The denominator is also not what the scorecard reads as. The corpus scorer
(`tools/Fogell.Corpus.Score`) classifies a file with no `pipeline { }` block by
parsing it through the Groovy parser and reports `scripted-ok` or
`scripted-err`; `scripts/fsx/generate-scorecard.fsx` then folds `scripted-ok`
into "admitted". The last committed snapshot, `corpus/BASELINE-DECLARATIVE-SCORE.tsv`,
reads 87 declarative-ok, 6 declarative-err, 109 scripted-ok and 26 scripted-err.
So the corpus is majority Scripted Pipeline, and roughly 109 of the files the
ledger calls admitted are files the walker refuses at 1:1 with
`no_pipeline_block` (FG-245 measured the refusal; FG-247's survey counted 122
such files among the admitted rows at its reading). A capability ranking that
ignored the form split would rank Declarative-only capabilities against a
denominator more than half of which they can never touch.

Demand is counted per construct, by file, on the pinned corpus, and the counts
live in ONE PLACE: the dated table in [FG-263](../tickets/FG-263.md), which
quotes the pattern behind every number so it can be re-run, and which the
instrument that ticket builds will replace. This ADR restates no FG-263 count;
the scorer's form split above is quoted from the baseline TSV, and the only
FG-263-adjacent figures repeated here are the withdrawn ones, as the record of
the mistake. Two caveats travel with every count. They are survey readings over
source text, not runs, and a count is corrected on FG-263's table when a
landed capability unlocks fewer files than it promised, never defended. And a
substring reading is not a construct reading: the first 2026-09-10 pass
counted `script` at 80, `mail` at 25 and `build` at 179, overstating the
construct count by 1.7-, 4- and 45-fold against FG-263's table; the second
pass counted commented-out lines. The pre-push verifier refuted both, the
figures are withdrawn, and FG-263 records every reading so the next survey
does not repeat them.

**The falsifiable claim.** Every capability class on the board carries the
count of corpus files it unlocks, from FG-263's instrument (split by pipeline
form and by receiver), and names the files when it lands. Within one milestone of a class landing,
`docs/COMPATIBILITY-LEDGER.tsv` carries at least one tier-1 receipt for a file
the class lists, or the class's row is reopened as unproven. A class whose
receipt never arrives falsifies its count, and the count moves.

**Therefore.**

- **Track 4 — Capability is binding as of 2026-09-10.** Its rows are ranked by
  corpus files unlocked. A Track 4 row is DONE only on a tier-1 corpus receipt
  from the class it names; code alone, or the hand-written suite alone, leaves
  it PARTIAL. Track 2 is not retired: every walk still lands there. Track 1 is
  not re-ranked: a class-A false success still outranks any capability.
- **Top-level Scripted Pipeline becomes a capability, not a refusal.** A
  bounded scripted walker executes `node`, `stage` and map-form `parallel` as
  hosted wrappers over the interpreter that already executes `script { }`
  bodies (FG-265). It reaches only the files the existing Groovy parser admits
  (the scorer's `scripted-ok` verdict); the `scripted-err` files fail that
  parser and wait on grammar work this decision does not include. Every
  scripted construct the walker does not model refuses by name before any
  step effect, exactly as the Declarative walker does. This
  extends ADR 0002's interpret-not-lower decision to the scripted form; it does
  not promise general Scripted Pipeline, and no receipt for a scripted file
  exists until FG-265 produces one.
- **Shared libraries load from a pinned ref, bounded.** `@Library('name@ref')`
  and operator-configured implicit libraries resolve from a hash-pinned git
  source the operator names (FG-269). Library `vars/*.groovy` bodies run through
  the same interpreter and sandbox as `script { }`, with the same closed
  vocabulary; `src/` class trees refuse (`unsupported_library_class`) until the
  interpreter has classes, and the `buildPlugin` files (FG-263 counts them) are
  named as NOT unlocked by this decision for that reason. Precondition for any receipt: the
  harness configures the identical library, at the identical ref, as a Global
  Pipeline Library in the pinned Jenkins, and under the no-egress fence the
  source is a loopback repository on both engines.
- **`parameters { }` and `triggers { }` are job properties.** Parameters are
  applied at admission (submitted values validated against the declared set,
  defaults resolved, persisted with the build, exposed as `params` and as
  environment) and are part of the admission fingerprint. Triggers are recorded
  with the project and never fired by this engine; a single-build differential
  cannot observe them, so no refusal is needed and none is claimed.
- **Container execution on the same host is a second containment mechanism
  beside ADR 0008's process groups.** `agent { docker }` and `agent { dockerfile }`
  run steps through a rootless container runtime with the workspace mounted at
  its own path and the image digest pinned on both engines. Timeout and
  cancellation kill the container, not only the process group. No remote agent
  protocol is implied; FG-062 stays where it is.
- **A label the worker does not offer refuses at admission** with
  `unsupported_agent_label`, naming the label, rather than queueing forever as
  Jenkins does. A build that can never be claimed cannot be receipted, and
  `ExplainWait` already names the missing capability.
- **The scorecard reports pipeline form.** `admitted` stops folding scripted
  files into a Declarative-shaped number: the generator emits
  `admitted_declarative`, `scripted_admitted` and `scripted_refused` as separate
  tokens, and `audit-board-numbers` verifies each against the ledger (FG-263).
  Until that lands, the board quotes the baseline TSV and types no new token.
- **An unknown step or an unresolved library refuses before any effect.**
  Today an unmodelled Declarative step reaches `Executor.runStep` at run time,
  after earlier steps in the same build have had effects. That is a class-A
  hazard wearing a class-D label, and FG-264 moves the refusal to preflight.

**Product rules, adopted 2026-09-10 alongside the McLoving board's equivalent
(reported by that session the same day as its ADR 0016; not verifiable from
this tree) so both trees get the same correction.** The charter's sentence "A
compatibility claim requires differential evidence against a pinned Jenkins
version and a hash-pinned corpus" defines what counts as a claim and says
nothing about what counts as a product. Both boards optimised proof and
starved use, because a receipt is legible and a missing feature is invisible
until someone tries to use the product. One operating rule with three clauses,
each falsifiable on the board (the board's contract carries it as one bullet):

- **A ticket names a user-visible verb** (run, read, set, load, bind, route,
  trigger, notify). A ticket whose only verb is verify, seal, attest, certify,
  count or receipt is bookkeeping: it may ride in the dispatch slot of the
  verb it serves, and may not occupy one on its own. FG-262 and FG-263 ride
  with FG-265; FG-277 rides with this board change.
- **Done means used, at the milestone.** A capability row still closes on a
  corpus receipt (that is what keeps this ADR under ADR 0004), but the phase
  closes only when Fogell runs its own gate: milestone M4 below. The
  fortnightly report states what the product can do and the count of gate
  steps Fogell cannot yet run for itself; that count must fall.
- **Process may not grow faster than product.** The board's existing ratchets
  only let rules and floors rise. The opposite ratchet is added: an operating
  rule may be added only when a rule is retired or a user-visible ticket
  closes in the same PR. A rule here means a bullet of the board's operating
  contract; standing risks and track designations are not rules. The ratchet
  takes effect for every PR after the one that introduces it: that PR retires
  no contract bullet and closes no user-visible ticket, and is the unpaid
  bootstrap, stated rather than disguised.

**Milestones, named as measurements.**

- **M1 — scripted and context-complete.** The ledger carries a tier-1 receipt
  for a scripted corpus file (FG-265) and for a Declarative file reading
  `params.X` and `currentBuild.currentResult` in `post { }`; the scorecard
  emits the form tokens; every existing receipt reseals byte-identical after
  the FG-262 move.
- **M2 — controller parity.** A `withCredentials` corpus file gets a tier-1
  receipt through `Fogell.Controller.Host` with two executors configured and
  the credentials file proven absent after terminal publication; `ExplainWait`
  names a missing label.
- **M3 — libraries and containers.** A corpus file loading a `vars/` library
  from a loopback pinned ref gets a receipt with the identical library
  configured in pinned Jenkins; an `agent { docker }` file gets a receipt under
  the fence with the container proven removed after SIGKILL of Run.Host.
- **M4 — Fogell builds Fogell.** The repository's own gate, expressed as a
  Jenkinsfile, runs to a durable terminal result under `Fogell.Controller.Host`
  on HeMan, with the steps it cannot run counted and published. Needs M2 at
  least; the count of unrunnable steps is the distance the fortnightly report
  tracks.

**Amends.** ADR 0001: the three tiers are unchanged; pipeline form (Declarative
or Scripted) becomes a fourth axis the scorecard must report alongside them.
ADR 0002: interpret-not-lower now covers the scripted top level. ADR 0008
describes the remote agent protocol and is not amended; the `built-in`-only,
one-node-per-build shape being replaced by the placement seam
(`AgentSpec -> Placement`, FG-272) is the current tree's (`Store.AdmitBuild`
inserts one node per build; the API hard-codes `["linux"]`), not a decision any
ADR recorded. Supersedes nothing.

**Out of scope by this decision, stated so nobody reads silence as a plan.**
`kubernetes` agents stay `unsupported_agent`. `tools { }` stays
`unsupported_tools` until a per-tool pin exists; FG-254 priced one. `bat` and
`powershell` wait on Windows, standing risk 3. Interpreter classes, decimals
and map-iteration methods are class-D rows in their own right and are the
stated prerequisite for `buildPlugin`. FG-263 carries each file count.
