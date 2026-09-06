# FG-253 unavailable-agent allocation probe

Captured on HeMan at 2026-09-06T01:37:15Z from base commit
`e60cde34f7010c3166b1ef088069320bcc83965c` while the FG-253 source and
fixtures were uncommitted in the canonical checkout.

`inputs.sha256` binds the exact implementation, focused-test source, positive
case and receipt, and unavailable-label probe used for this observation.

This bundle is evidence for the conservative boundary, not a Tier-1 receipt.
The positive compatibility claim is separately sealed by
`differential/receipts/agent-label-built-in.receipt.txt`.

## Jenkins arm

The tracked unavailable-label probe was installed as a uniquely named
`CpsFlowDefinition` job, triggered through the crumb-bound Jenkins HTTP API,
and polled for at most 30 seconds. The queue API reported the unavailable
label, build 1 remained nonterminal, the console stopped at `[Pipeline] node`,
and the sentinel workspace file returned HTTP 404.

An EXIT cleanup cancelled every queue item whose reason named
`fg253-not-offered`, stopped build 1, deleted the job, waited one second, and
then verified that the job returned HTTP 404 and zero matching queue items
remained. The successful observation is in `jenkins-queue-probe.log`.

## Fogell arm

The Release `Fogell.Run.Host` built from the working tree ran the same tracked
probe with a fresh temporary workspace root and journal path. It exited 2 with
the stable `unsupported_agent` preflight reason. A recursive inventory of the
temporary root contained only the root directory: neither workspace nor
journal was created. The output is in `fogell-preflight-probe.log`.

The two outcomes are intentionally not called compatible: Jenkins queues while
Fogell refuses before execution. The fail-closed choice prevents the former
silent divergence, where Fogell executed the stage as if its agent were
`any`.
