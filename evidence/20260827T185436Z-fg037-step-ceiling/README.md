# FG-037 retained step-ceiling evidence

The exact 250-step control is tier-1 PROVEN on Jenkins 2.568.1 and Fogell.
The adjacent 251-step input and the 400-step input are deliberately DIVERGED:
Jenkins fails with an empty workspace before the sentinel step, while Fogell
succeeds, writes the sentinel, and emits every ordered marker. The retained raw
251 console binds that refusal to its 251-argument ArrayUtil NoSuchMethodError.

These receipts are intentional capability differences. They must remain outside
differential/receipts and are not part of the compatibility scorecard.
