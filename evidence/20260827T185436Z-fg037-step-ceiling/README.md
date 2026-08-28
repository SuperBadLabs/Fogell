# FG-037 retained step-ceiling evidence

The exact 250-step control is tier-1 PROVEN on Jenkins 2.568.1 and Fogell.
The adjacent 251-step input and the 400-step input are deliberately DIVERGED:
Jenkins fails with an empty workspace before the sentinel step, while Fogell
succeeds, writes the sentinel, and emits every ordered marker. The retained raw
251 console binds that refusal to its 251-argument ArrayUtil NoSuchMethodError.

These receipts are intentional capability differences. They must remain outside
differential/receipts and are not part of the compatibility scorecard.

`source/fg037-measured-source.bundle` is a thin Git bundle rooted at retained
prerequisite `804bf7967` and carries the signed measurement commit `65674f9`
and tree `7e09d22`; `source/allowed_signers` pins its signer independently of
custodian or runner Git configuration. The repository gate imports it into an isolated scratch
repository, clears repository/object environment, proves both descendants are
absent before import, and verifies the bundle, ancestry, exact commit/tree, and
signature. Thus the executable behind the receipts remains reconstructable
after the historical source branch disappears without an ambient-object false
pass.
