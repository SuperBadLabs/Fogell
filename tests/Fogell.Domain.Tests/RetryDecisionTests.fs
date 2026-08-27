module RetryDecisionTests

open System
open Expecto
open Fogell.Domain

let private attemptId (value: string) = AttemptId(Guid.Parse value)
let private nodeId (value: string) = NodeId(Guid.Parse value)
let private organizationId (value: string) = OrganizationId(Guid.Parse value)

let private parent state ordinal =
    { Id = attemptId "10000000-0000-0000-0000-000000000001"
      NodeId = nodeId "20000000-0000-0000-0000-000000000002"
      OrganizationId = organizationId "30000000-0000-0000-0000-000000000003"
      Ordinal = ordinal
      RetryOf = Some(attemptId "40000000-0000-0000-0000-000000000004")
      State = state
      Fence = Fence 91L
      RestoreEpoch = RestoreEpoch 7L
      LeaseOwner = Some "stale-parent-owner"
      LeaseExpiresAt = Some(DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)) }

let private proposed = attemptId "50000000-0000-0000-0000-000000000005"
let private other = attemptId "60000000-0000-0000-0000-000000000006"

let private expectChild decision =
    match decision.Outcome with
    | ChildCreated child -> child
    | BudgetExhausted -> failtest "expected a child decision"

let private decideOk parent limit childId prior =
    match RetryDecision.decide parent limit childId prior with
    | Ok decision -> decision
    | Error error -> failtestf "expected a decision, got %A" error

let private expectMalformed result =
    match result with
    | Error(MalformedPriorDecision _) -> ()
    | otherResult -> failtestf "expected malformed prior refusal, got %A" otherResult

let tests =
    testList
        "FG-027a pure retry decision"
        [ test "fresh child preserves immutable scope and resets execution authority" {
              let original = parent (Terminal Failure) 0
              let snapshot = original
              let decision = decideOk original 3 proposed None
              let child = expectChild decision

              Expect.equal decision.ParentId original.Id "decision binds its parent"
              Expect.equal
                  decision.ParentOrganizationId
                  original.OrganizationId
                  "decision binds the parent tenant"
              Expect.equal decision.ParentNodeId original.NodeId "decision binds the parent node"
              Expect.equal decision.ParentOrdinal original.Ordinal "decision binds the parent ordinal"
              Expect.equal decision.ParentRetryOf original.RetryOf "decision binds known ancestry"
              Expect.equal
                  decision.ParentRestoreEpoch
                  original.RestoreEpoch
                  "decision binds the parent restore epoch"
              Expect.equal decision.AttemptLimit 3 "decision records the original limit"
              Expect.equal child.Id proposed "caller supplies the fresh child identity"
              Expect.equal child.NodeId original.NodeId "node scope is preserved"
              Expect.equal child.OrganizationId original.OrganizationId "organization scope is preserved"
              Expect.equal child.Ordinal 1 "ordinal advances exactly once"
              Expect.equal child.RetryOf (Some original.Id) "ancestry is explicit"
              Expect.equal child.State Queued "new child begins queued"
              Expect.equal child.Fence Fence.initial "new child has no old fence authority"
              Expect.equal child.RestoreEpoch original.RestoreEpoch "restore epoch is preserved"
              Expect.isNone child.LeaseOwner "lease owner is cleared"
              Expect.isNone child.LeaseExpiresAt "lease expiry is cleared"
              Expect.equal child (Attempt.retryOf proposed original) "the canonical retry constructor owns authority reset"
              Expect.equal original snapshot "the parent record remains byte-for-byte semantic history"
          }

          test "every terminal status is admitted because status retryability is caller policy" {
              for status in [ NotBuilt; Success; Unstable; Failure; Aborted ] do
                  let result = RetryDecision.decide (parent (Terminal status) 0) 2 proposed None
                  Expect.isOk result $"terminal %A{status} is structurally decidable"
          }

          test "active and reconciliation states fail closed" {
              for state in
                  [ Queued; Offered; Accepted; Running; Finalizing; Cancelling; ReconciliationRequired ] do
                  Expect.equal
                      (RetryDecision.decide (parent state 0) 2 proposed None)
                      (Error(ParentNotTerminal state))
                      $"%A{state} must not produce retry history"
          }

          test "zero-based attempt limit admits below boundary and dead-letters at or above it" {
              Expect.isTrue
                  ((decideOk (parent (Terminal Failure) 1) 3 proposed None).Outcome
                   |> function ChildCreated _ -> true | _ -> false)
                  "ordinal 1 may create ordinal 2 when limit is 3"

              for ordinal in [ 2; 3 ] do
                  Expect.equal
                      (decideOk (parent (Terminal Failure) ordinal) 3 proposed None).Outcome
                      BudgetExhausted
                      $"ordinal %d{ordinal} is exhausted for limit 3"
          }

          test "invalid limits and parent ordinals fail closed" {
              for limit in [ 0; -1 ] do
                  Expect.equal
                      (RetryDecision.decide (parent (Terminal Failure) 0) limit proposed None)
                      (Error(InvalidAttemptLimit limit))
                      "a fresh limit must be positive"

              for ordinal in [ -1; Int32.MaxValue ] do
                  Expect.equal
                      (RetryDecision.decide (parent (Terminal Failure) ordinal) 3 proposed None)
                      (Error(InvalidParentOrdinal ordinal))
                      "ordinal arithmetic must be safe"
          }

          test "empty parent scope identities fail closed" {
              let valid = parent (Terminal Failure) 0
              let cases =
                  [ { valid with Id = AttemptId Guid.Empty }, "attempt"
                    { valid with NodeId = NodeId Guid.Empty }, "node"
                    { valid with OrganizationId = OrganizationId Guid.Empty }, "organization" ]

              for invalid, label in cases do
                  Expect.equal
                      (RetryDecision.decide invalid 3 proposed None)
                      (Error(InvalidParentIdentity label))
                      $"empty %s{label} identity must be refused"
          }

          test "fresh child identity must be nonempty and distinct" {
              let terminal = parent (Terminal Failure) 0
              for invalid in [ AttemptId Guid.Empty; terminal.Id ] do
                  Expect.equal
                      (RetryDecision.decide terminal 3 invalid None)
                      (Error(InvalidProposedChildIdentity invalid))
                      "invalid child identity must not be admitted"
          }

          test "a known direct ancestor identity cannot be reused by fresh or replayed history" {
              let terminal = parent (Terminal Failure) 0
              let ancestor = terminal.RetryOf |> Option.get

              Expect.equal
                  (RetryDecision.decide terminal 3 ancestor None)
                  (Error(InvalidProposedChildIdentity ancestor))
                  "the directly known ancestor is not a fresh child identity"

              let original = decideOk terminal 3 proposed None
              let child = expectChild original

              expectMalformed
                  (RetryDecision.decide
                      terminal
                      3
                      other
                      (Some { original with Outcome = ChildCreated { child with Id = ancestor } }))
          }

          test "an exhausted decision does not consume a child identity" {
              let terminal = parent (Terminal Failure) 2
              for irrelevant in [ AttemptId Guid.Empty; terminal.Id; terminal.RetryOf |> Option.get ] do
                  Expect.equal
                      (decideOk terminal 3 irrelevant None).Outcome
                      BudgetExhausted
                      "no child exists whose identity could alias"
          }

          test "valid prior child is returned exactly despite hostile id and budget drift" {
              let terminal = parent (Terminal Failure) 0
              let original = decideOk terminal 3 proposed None

              for driftedLimit, driftedId in [ 0, AttemptId Guid.Empty; 1, terminal.Id; 99, other ] do
                  Expect.equal
                      (RetryDecision.decide terminal driftedLimit driftedId (Some original))
                      (Ok original)
                      "mutable configuration and proposed identity cannot rewrite history"
          }

          test "valid prior exhaustion is returned exactly despite hostile inputs" {
              let terminal = parent (Terminal Failure) 2
              let original = decideOk terminal 3 proposed None
              Expect.equal
                  (RetryDecision.decide terminal 99 terminal.Id (Some original))
                  (Ok original)
                  "persisted exhaustion is authoritative after structural validation"
          }

          test "prior exhaustion binds the decision-relevant parent snapshot" {
              let terminal = parent (Terminal Failure) 2
              let original = decideOk terminal 3 proposed None
              let driftedParents =
                  [ { terminal with OrganizationId = organizationId "31000000-0000-0000-0000-000000000003" }
                    { terminal with NodeId = nodeId "21000000-0000-0000-0000-000000000002" }
                    { terminal with Ordinal = terminal.Ordinal + 1 }
                    { terminal with RetryOf = None }
                    { terminal with RestoreEpoch = RestoreEpoch 8L } ]

              for driftedParent in driftedParents do
                  expectMalformed
                      (RetryDecision.decide driftedParent 99 other (Some original))

              Expect.equal
                  (RetryDecision.decide { terminal with State = Terminal Success } 99 other (Some original))
                  (Ok original)
                  "terminal result retryability remains caller policy"
          }

          test "prior must belong to the exact parent" {
              let terminal = parent (Terminal Failure) 0
              let original = decideOk terminal 3 proposed None
              let differentParent = { terminal with Id = other }
              Expect.equal
                  (RetryDecision.decide differentParent 3 proposed (Some original))
                  (Error(PriorParentMismatch(differentParent.Id, terminal.Id)))
                  "same-shaped history for another parent is not replayable"
          }

          test "recorded limit and outcome must agree with parent ordinal" {
              let terminal = parent (Terminal Failure) 0
              let childDecision = decideOk terminal 3 proposed None

              expectMalformed
                  (RetryDecision.decide terminal 3 proposed (Some { childDecision with AttemptLimit = 0 }))

              expectMalformed
                  (RetryDecision.decide terminal 3 proposed (Some { childDecision with AttemptLimit = 1 }))

              expectMalformed
                  (RetryDecision.decide
                      terminal
                      3
                      proposed
                      (Some { childDecision with Outcome = BudgetExhausted }))
          }

          test "every persisted child field is structurally load-bearing" {
              let terminal = parent (Terminal Failure) 0
              let original = decideOk terminal 3 proposed None
              let child = expectChild original
              let malformedChildren =
                  [ { child with Id = terminal.Id }
                    { child with NodeId = NodeId(Guid.NewGuid()) }
                    { child with OrganizationId = OrganizationId(Guid.NewGuid()) }
                    { child with Ordinal = child.Ordinal + 1 }
                    { child with RetryOf = None }
                    { child with State = Offered }
                    { child with Fence = Fence 1L }
                    { child with RestoreEpoch = RestoreEpoch 8L }
                    { child with LeaseOwner = Some "forged" }
                    { child with LeaseExpiresAt = Some DateTimeOffset.MaxValue } ]

              for malformedChild in malformedChildren do
                  expectMalformed
                      (RetryDecision.decide
                          terminal
                          3
                          proposed
                          (Some { original with Outcome = ChildCreated malformedChild }))
          }

          test "a prior child is malformed when its recorded budget was already exhausted" {
              let terminal = parent (Terminal Failure) 2
              let forgedChild =
                  { terminal with
                      Id = proposed
                      Ordinal = 3
                      RetryOf = Some terminal.Id
                      State = Queued
                      Fence = Fence.initial
                      LeaseOwner = None
                      LeaseExpiresAt = None }

              expectMalformed
                  (RetryDecision.decide
                      terminal
                      3
                      proposed
                      (Some
                          { ParentId = terminal.Id
                            ParentOrganizationId = terminal.OrganizationId
                            ParentNodeId = terminal.NodeId
                            ParentOrdinal = terminal.Ordinal
                            ParentRetryOf = terminal.RetryOf
                            ParentRestoreEpoch = terminal.RestoreEpoch
                            AttemptLimit = 3
                            Outcome = ChildCreated forgedChild }))
          } ]
