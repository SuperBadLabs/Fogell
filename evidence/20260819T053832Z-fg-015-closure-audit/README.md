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
| spread-dot | success, projects `[a, b]` | same result, output and workspace | closed by the measured follow-on | `differential/receipts/fg015-spread-dot.receipt.txt` and [boundary evidence](../20260820T230832Z-fg-015-spread-dot) |

The initial failure was structural: the parser lowered both `rows.name` and
`rows*.name` to `EProp(rows, "name")`, so the interpreter could not know that
projection was requested. The follow-on measured Jenkins before implementation
and now preserves `*.` as `ESpreadProp`. Ordinary `EProp` remains unchanged.

The bounded interpreter matches the measured boundary: ordered list projection;
null receiver to null; null elements omitted; null property values retained;
non-list receivers delegated to ordinary property lookup; catchable missing
properties; and the measured `*.child?.name` chain. Both the original failure and
the adjacent boundary are tier-1 PROVEN.

All six recovered FG-015 constructs are now closed. FG-015b remains deliberately
out of scope: `xs[index] = value` is mutation and shares no safe implementation
root with this read/projection operator.
