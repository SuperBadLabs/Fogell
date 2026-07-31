# Review checklist — standing rules the reviews kept re-finding

Each rule below was violated repeatedly before it was written down; the counts
come from the execution board's finding telemetry. A PR that touches the listed
area answers the listed question **in its description**, or the review will ask
it — historically at a cost of 2–14 rounds.

## FG-102 — output-narration contract (17 findings)

**Rule: no line is suppressed from the trace comparison on wording alone.**
Every suppression in `Trace.fs` must be one of, in order of preference:

1. **Emitted identically by Fogell** — the two logs then COMPARE and no
   suppression exists at all. This is what the secret-interpolation warning and
   the def-keyword advisory ended as, each after a round of the wording-only
   defect. Parity beats exclusion: log tooling sees what Jenkins users scrape.
2. **Context-gated** — the line counts as narration only when the line that
   must accompany it does: an exception head only before a stack frame,
   `Terminated` only after interrupt narration, the warning head only before
   its body.
3. **Exact-sentence or doubled-context prefix** — permitted only for narration
   Fogell cannot reproduce (plugin wording that varies), and the comment must
   name the receipt that measured it.

The timeout narration family is the worked example of form 1: `Timeout set to
expire in …` (Jenkins' 30-day-month duration wording), the
Cancelling/Sending/Terminated cluster, and `Timeout has been exceeded` are all
EMITTED by Fogell in measured wording and positions — including `Terminated`,
which Fogell must say itself because its SIGTERM reaches the whole group and no
wrapper shell survives to print it. All five suppressions are deleted; the
receipts compare the sentences. The remaining wording-differs exclusions
(`ERROR:` reasons, the masking banner's multi-variable join) are the
cross-engine-boolean category the comparison contract documents.

**Stated residual, closed as a class (2026-07-31):** attribution of engine
narration on a text-only channel is approximate — every discriminator this
normaliser can use (a boundary sentence, an annotation grammar, a path shape)
is itself printable, so output DELIBERATELY constructed to mimic the engine
can be misclassified in either direction. Twenty-plus review rounds refined
the gates until the surviving scenarios all require intent; the misclassified
surface is bounded to narration-shaped lines, and the terminal result and
workspace hash — which no output spoof can touch — remain fully compared.
Further narrowing trades real false-divergences for hypothetical mimics and
is declined.

Six of eight historical suppressions were DEAD when finally audited (FG-002f),
and the fifth instance of this defect was added *while fixing the fourth*.
Checklist question for any `Trace.fs` change:

- [ ] Does any new/changed suppression key on text a build could print, without
      a context gate? If yes: emit the line from Fogell instead, or gate it.
- [ ] Does `tests/Fogell.Differential.Tests` carry a look-alike row proving a
      build printing similar text still compares as output?

## FG-103 — width and fail-closed contract (6 + 19 findings)

**Rule 1: a duration is `int64` from parse to executor.** `timeout(time: 30,
unit: 'DAYS')` is 2,592,000,000 ms — past `Int32.MaxValue`. The narrowing
wrapped negative twice, both times aborting a valid build instantly; the second
time because a compiler error was silenced rather than asked why the types
differed. There is no compliant `int` conversion on a duration before the
executor boundary — the class is banned, not audited per instance.

- [ ] Any new `int (...)` on a value that is milliseconds? Use `int64`/
      `TimeSpan` end to end. `Thread.Sleep` takes a `TimeSpan`.
- [ ] `tests/Fogell.Execution.Tests` has the 30-day-budget acceptance test;
      a new wrapper step that carries a deadline extends it.

**Rule 2: a check that cannot decide says so — it never reports the safe
answer.** The leak scan returned 0 ("nothing survived") when the `/proc` read
itself failed, which made FG-032's headline claim rest on a gate that could
not fail. Unknown is `-1`, treated as still-populated downstream, and the
diagnostic says "check unavailable". The same shape appears as: unknown
timeout unit → refuse to guess a deadline; unparsable credential binding →
fail rather than bind nothing; unreadable test report → Error, not zero tests.

- [ ] Does any new failure path catch-and-default to the value that means
      "all clear"? Name the unknown instead.
- [ ] Does the exit status of every subprocess/gate actually propagate?
      (`PIPESTATUS`, no `| head` on a checked command, no discarded results.)

## Provenance of these rules

FG-100 (36 review rounds) and FG-101 (94 findings across predecessors) both
ended as "one model, stated once, that every consumer routes through". When a
review keeps finding the same defect class, the fix is a structure that makes
the class inexpressible — not a longer memory. That is FG-105's mandate for
the walker itself.
