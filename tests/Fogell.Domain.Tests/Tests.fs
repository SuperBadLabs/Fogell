module Fogell.Domain.Tests

open System
open Expecto
open FsCheck
open Fogell.Domain

let private allStatuses = [ NotBuilt; Success; Unstable; Failure; Aborted ]

let private statusGen = Gen.elements allStatuses |> Arb.fromGen

/// FsCheck generator registration for BuildStatus.
type StatusArb() =
    static member BuildStatus() = statusGen

/// FG-006 acceptance: worstOf is associative and commutative, with NotBuilt as
/// identity. That is what makes parallel stage aggregation order-independent.
let aggregationProperties =
    testList
        "BuildStatus.worstOf forms a commutative monoid"
        [ test "commutative (exhaustive)" {
              for a in allStatuses do
                  for b in allStatuses do
                      Expect.equal
                          (BuildStatus.worstOf a b)
                          (BuildStatus.worstOf b a)
                          $"worstOf %A{a} %A{b} must commute"
          }

          test "associative (exhaustive)" {
              for a in allStatuses do
                  for b in allStatuses do
                      for c in allStatuses do
                          Expect.equal
                              (BuildStatus.worstOf (BuildStatus.worstOf a b) c)
                              (BuildStatus.worstOf a (BuildStatus.worstOf b c))
                              $"worstOf must associate over %A{(a, b, c)}"
          }

          test "NotBuilt is the identity" {
              for a in allStatuses do
                  Expect.equal (BuildStatus.worstOf NotBuilt a) a "left identity"
                  Expect.equal (BuildStatus.worstOf a NotBuilt) a "right identity"
          }

          testPropertyWithConfig
              { FsCheckConfig.defaultConfig with arbitrary = [ typeof<StatusArb> ] }
              "ofMany is order-independent"
              (fun (statuses: BuildStatus list) ->
                  let forward = BuildStatus.ofMany statuses
                  let reversed = BuildStatus.ofMany (List.rev statuses)
                  forward = reversed)

          test "an abort is not masked by a later failure" {
              Expect.equal (BuildStatus.ofMany [ Aborted; Failure ]) Aborted "operator intent dominates"
          }

          test "wire round-trip is total over the type" {
              for a in allStatuses do
                  Expect.equal (BuildStatus.ofWireString (BuildStatus.toWireString a)) (Some a) "round trip"
          } ]

let transitionProperties =
    testList
        "AttemptState transitions"
        [ test "a terminal attempt never transitions again" {
              for s in allStatuses do
                  for target in
                      [ Queued; Offered; Accepted; Running; Finalizing; Cancelling; ReconciliationRequired ] do
                      Expect.isFalse
                          (AttemptState.canTransition (Terminal s) target)
                          $"Terminal %A{s} -> %A{target} must be illegal"
          }

          test "every active state may enter reconciliation" {
              for s in [ Queued; Offered; Accepted; Running; Finalizing; Cancelling ] do
                  Expect.isTrue
                      (AttemptState.canTransition s ReconciliationRequired)
                      $"%A{s} -> ReconciliationRequired"
          }

          test "an expired offer returns to the queue" {
              Expect.isTrue (AttemptState.canTransition Offered Queued) "offer expiry requeues"
          }

          test "queued work cannot jump straight to running" {
              Expect.isFalse (AttemptState.canTransition Queued Running) "must be offered and accepted first"
          } ]

let private attempt state fence epoch owner expiry =
    { Id = AttemptId(Guid.NewGuid())
      NodeId = NodeId(Guid.NewGuid())
      OrganizationId = OrganizationId(Guid.NewGuid())
      Ordinal = 0
      RetryOf = None
      State = state
      Fence = fence
      RestoreEpoch = epoch
      LeaseOwner = owner
      LeaseExpiresAt = expiry }

let publicationGuard =
    let now = DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)
    let future = Some(now.AddMinutes 5.0)
    let epoch = RestoreEpoch 3L
    let fence = Fence 7L
    let good = attempt Running fence epoch (Some "agent-a") future

    testList
        "terminal publication guard"
        [ test "the exact current holder may publish" {
              Expect.isTrue (Attempt.mayPublish now epoch fence "agent-a" good) "happy path"
          }
          test "a stale fence may not" {
              Expect.isFalse (Attempt.mayPublish now epoch (Fence 8L) "agent-a" good) "fence mismatch"
          }
          test "a pre-restore epoch may not" {
              Expect.isFalse
                  (Attempt.mayPublish now (RestoreEpoch 4L) fence "agent-a" good)
                  "restore invalidates prior leases"
          }
          test "another owner may not" {
              Expect.isFalse (Attempt.mayPublish now epoch fence "agent-b" good) "wrong lease owner"
          }
          test "an expired lease may not" {
              let expired = { good with LeaseExpiresAt = Some(now.AddSeconds -1.0) }
              Expect.isFalse (Attempt.mayPublish now epoch fence "agent-a" expired) "lease expiry is enforced"
          }
          test "a leaseless attempt may not" {
              Expect.isFalse
                  (Attempt.mayPublish now epoch fence "agent-a" { good with LeaseOwner = None })
                  "no lease, no publication"
          }
          test "an already-terminal attempt may not" {
              Expect.isFalse
                  (Attempt.mayPublish now epoch fence "agent-a" { good with State = Terminal Success })
                  "terminal is final"
          } ]

let retrySemantics =
    testList
        "retry never rewrites history"
        [ test "a child links to its parent and resets the lease" {
              let parent =
                  attempt (Terminal Failure) (Fence 4L) (RestoreEpoch 1L) (Some "agent-a") None

              let childId = AttemptId(Guid.NewGuid())
              let child = Attempt.retryOf childId parent
              Expect.equal child.Ordinal (parent.Ordinal + 1) "ordinal increments"
              Expect.equal child.RetryOf (Some parent.Id) "immutable link to parent"
              Expect.equal child.State Queued "child starts queued"
              Expect.equal child.Fence Fence.initial "fence resets"
              Expect.isNone child.LeaseOwner "no inherited lease"
              Expect.notEqual child.Id parent.Id "parent identity is preserved"
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs
        []
        argv
        (testList
            "Fogell.Domain"
            [ aggregationProperties
              transitionProperties
              publicationGuard
              retrySemantics
              RetryDecisionTests.tests ])
