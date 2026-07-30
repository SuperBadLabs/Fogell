namespace Fogell.Domain

open System

/// Where an attempt is in its lifecycle. Distinct from [BuildStatus]: an
/// attempt can be `ReconciliationRequired` (we lost contact and must decide)
/// without that being a build *result*. Conflating the two is how engines end
/// up reporting a failure they never observed.
type AttemptState =
    | Queued
    | Offered
    | Accepted
    | Running
    | Finalizing
    | Cancelling
    | ReconciliationRequired
    | Terminal of BuildStatus

module AttemptState =

    let isActive =
        function
        | Queued
        | Offered
        | Accepted
        | Running
        | Finalizing
        | Cancelling -> true
        | ReconciliationRequired
        | Terminal _ -> false

    /// Legal transitions. Anything absent here is a bug, not a state.
    let canTransition (from: AttemptState) (to': AttemptState) : bool =
        match from, to' with
        | Queued, Offered
        | Offered, Accepted
        | Offered, Queued // offer expired, back to the queue
        | Accepted, Running
        | Running, Finalizing
        | Running, Cancelling
        | Accepted, Cancelling
        | Cancelling, Finalizing -> true
        | (Queued | Offered | Accepted | Running | Finalizing | Cancelling), ReconciliationRequired -> true
        | (Running | Finalizing | Cancelling | ReconciliationRequired), Terminal _ -> true
        | Queued, Terminal Aborted -> true // cancelled before it ever ran
        | _ -> false

/// One immutable execution attempt of a node. Retries never rewrite an
/// attempt; they create a child with an incremented ordinal and a link back.
type Attempt =
    { Id: AttemptId
      NodeId: NodeId
      OrganizationId: OrganizationId
      Ordinal: int
      RetryOf: AttemptId option
      State: AttemptState
      Fence: Fence
      RestoreEpoch: RestoreEpoch
      LeaseOwner: string option
      LeaseExpiresAt: DateTimeOffset option }

/// One unit of scheduling — a stage, in Jenkins terms.
type Node =
    { Id: NodeId
      BuildId: BuildId
      OrganizationId: OrganizationId
      Name: string
      RequiredCapabilities: Set<string>
      RequiredTrustPool: string }

type Build =
    { Id: BuildId
      ProjectId: ProjectId
      OrganizationId: OrganizationId
      Number: int
      IdempotencyKey: string
      CancellationRequested: bool }

module Attempt =

    /// A terminal publication is admissible only from the exact current fence,
    /// the exact lease owner, the current restore epoch, an unexpired lease and
    /// an active state. Every condition is a real defect class observed in the
    /// engines this one replaces.
    let mayPublish
        (now: DateTimeOffset)
        (currentEpoch: RestoreEpoch)
        (expectedFence: Fence)
        (claimant: string)
        (attempt: Attempt)
        : bool =
        AttemptState.isActive attempt.State
        && attempt.Fence = expectedFence
        && attempt.RestoreEpoch = currentEpoch
        && attempt.LeaseOwner = Some claimant
        && (match attempt.LeaseExpiresAt with
            | Some expiry -> expiry > now
            | None -> false)

    /// Retry produces a child; the parent stays as history.
    let retryOf (newId: AttemptId) (parent: Attempt) : Attempt =
        { parent with
            Id = newId
            Ordinal = parent.Ordinal + 1
            RetryOf = Some parent.Id
            State = Queued
            Fence = Fence.initial
            LeaseOwner = None
            LeaseExpiresAt = None }
