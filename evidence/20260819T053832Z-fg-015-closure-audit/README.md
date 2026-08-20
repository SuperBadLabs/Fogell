# FG-015 closure audit

Baseline: `85473b4814d85fc1d2203a62c1b91b285c1b6825`.
Oracle: Jenkins core 2.568.1 in the pinned `jenkins-lab` container.

The six names were recovered from the original board commit `6e7e21c` and
cross-checked against the FG-011 parser/interpreter landing commit `104942a`:
nested-quote GString, spread-dot, ranges, `switch`, `instanceof`, multi-assign.
The current shortened row was not used as the specification.

| construct | Jenkins 2.568.1 | Fogell | classification | durable evidence |
|---|---|---|---|---|
| nested-quote GString | success, quoted `alpha beta` reaches `sh` as one argument and writes `<alpha beta>` | same result, output and workspace | already closed | `differential/receipts/fg015-nested-quote-gstring.receipt.txt` |
| inclusive range | success, visits `1`, `2`, `3` | same result, output and workspace | already closed | `differential/receipts/fg015-range.receipt.txt` |
| `switch` | success, `case 'b'` assigns and `break` exits | same result, output and workspace | already closed | `differential/receipts/fg015-switch.receipt.txt` |
| `instanceof` | success, String branch writes `yes` | same result, output and workspace | already closed | `differential/receipts/fg015-instanceof.receipt.txt` |
| multi-assign | success, binds `L:R` | same result, output and workspace | already closed | `differential/receipts/fg015-multi-assign.receipt.txt` |
| spread-dot | success, projects `[a, b]` | failure before `sh`; empty workspace | divergent | `evidence/20260819T053832Z-fg-015-closure-audit/fg015-spread-dot.receipt.txt` (one retained run; `DIVERGED (3)` counts result, output and workspace difference dimensions) |

The spread-dot failure is structural, not a missing builtin. The parser lowers
both `rows.name` and `rows*.name` to `EProp(rows, "name")`; by interpreter time
there is no remaining bit that says projection was requested. The strict script
path therefore raises `UnknownProperty "name"` on the list receiver. No parser or
interpreter repair is bundled into this audit.

## Precise remaining spread-dot task

1. Measure Jenkins on list-of-maps projection for present, missing, and null
   properties; a null list receiver; and a non-list receiver. These decide the
   boundary before code is written.
2. Add a distinct AST node such as `ESpreadProp(target, name)`; keep ordinary
   `EProp` unchanged and add parser-shape tests proving the two spellings cannot
   collapse again.
3. Interpret the new node only for the Jenkins-measured receiver/value shapes,
   preserving list order and null positions. Refuse every unmeasured shape by
   name rather than guessing.
4. Replace the preserved divergent probe with tier-1 differential cases for the
   measured shapes, then change this ticket from PARTIAL to DONE.

FG-015b is deliberately out of scope. It concerns mutation through
`xs[index] = value`; spread-dot is a read/projection operator and shares no safe
implementation root with list index assignment.
