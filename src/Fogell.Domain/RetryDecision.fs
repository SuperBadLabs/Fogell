namespace Fogell.Domain

open System

/// The durable result of deciding whether one immutable attempt may be retried.
type RetryDecisionOutcome =
    | ChildCreated of Attempt
    | BudgetExhausted

/// The attempt limit is captured with the result so replay validates the law
/// that originally produced it instead of consulting mutable configuration.
type RetryDecision =
    { ParentId: AttemptId
      /// The remaining fields bind replay to the decision-relevant parent snapshot.
      /// AttemptId is organization-scoped in durable identity, and ordinal/ancestry/epoch
      /// are inputs to this law, so ParentId alone is not a sufficient replay key.
      ParentOrganizationId: OrganizationId
      ParentNodeId: NodeId
      ParentOrdinal: int
      ParentRetryOf: AttemptId option
      ParentRestoreEpoch: RestoreEpoch
      AttemptLimit: int
      Outcome: RetryDecisionOutcome }

type RetryDecisionError =
    | ParentNotTerminal of AttemptState
    | InvalidParentOrdinal of int
    | InvalidParentIdentity of string
    | InvalidAttemptLimit of int
    | InvalidProposedChildIdentity of AttemptId
    | PriorParentMismatch of expected: AttemptId * actual: AttemptId
    | MalformedPriorDecision of string

module RetryDecision =

    let private validateParent (parent: Attempt) =
        match parent.State with
        | Terminal _ when parent.Ordinal < 0 || parent.Ordinal = Int32.MaxValue ->
            Error(InvalidParentOrdinal parent.Ordinal)
        | Terminal _ when parent.Id.Value = Guid.Empty ->
            Error(InvalidParentIdentity "attempt")
        | Terminal _ when parent.NodeId.Value = Guid.Empty ->
            Error(InvalidParentIdentity "node")
        | Terminal _ when parent.OrganizationId.Value = Guid.Empty ->
            Error(InvalidParentIdentity "organization")
        | Terminal _ -> Ok(parent.Ordinal + 1)
        | state -> Error(ParentNotTerminal state)

    let private validatePrior (parent: Attempt) nextOrdinal (decision: RetryDecision) =
        let malformed reason = Error(MalformedPriorDecision reason)

        if decision.ParentId <> parent.Id then
            Error(PriorParentMismatch(parent.Id, decision.ParentId))
        elif decision.ParentOrganizationId <> parent.OrganizationId then
            malformed "recorded parent organization differs from its parent"
        elif decision.ParentNodeId <> parent.NodeId then
            malformed "recorded parent node differs from its parent"
        elif decision.ParentOrdinal <> parent.Ordinal then
            malformed "recorded parent ordinal differs from its parent"
        elif decision.ParentRetryOf <> parent.RetryOf then
            malformed "recorded parent ancestry differs from its parent"
        elif decision.ParentRestoreEpoch <> parent.RestoreEpoch then
            malformed "recorded parent restore epoch differs from its parent"
        elif decision.AttemptLimit <= 0 then
            malformed "recorded attempt limit must be positive"
        else
            match decision.Outcome with
            | BudgetExhausted ->
                if nextOrdinal >= decision.AttemptLimit then Ok decision
                else malformed "budget exhaustion was recorded below the attempt limit"
            | ChildCreated child ->
                if nextOrdinal >= decision.AttemptLimit then
                    malformed "a child was recorded at or beyond the attempt limit"
                elif
                    child.Id.Value = Guid.Empty
                    || child.Id = parent.Id
                    || parent.RetryOf = Some child.Id
                then
                    malformed "child identity is empty or aliases known ancestry"
                elif child.NodeId <> parent.NodeId then
                    malformed "child node identity differs from its parent"
                elif child.OrganizationId <> parent.OrganizationId then
                    malformed "child organization identity differs from its parent"
                elif child.Ordinal <> nextOrdinal then
                    malformed "child ordinal is not exactly one after its parent"
                elif child.RetryOf <> Some parent.Id then
                    malformed "child does not link to its parent"
                elif child.State <> Queued then
                    malformed "child is not queued"
                elif child.Fence <> Fence.initial then
                    malformed "child fence is not initial"
                elif child.RestoreEpoch <> parent.RestoreEpoch then
                    malformed "child restore epoch differs from its parent"
                elif child.LeaseOwner.IsSome || child.LeaseExpiresAt.IsSome then
                    malformed "child inherited lease authority"
                else
                    Ok decision

    let private decideFresh
        (parent: Attempt)
        (nextOrdinal: int)
        (attemptLimit: int)
        (proposedChildId: AttemptId) =
        if attemptLimit <= 0 then
            Error(InvalidAttemptLimit attemptLimit)
        elif nextOrdinal >= attemptLimit then
            Ok
                { ParentId = parent.Id
                  ParentOrganizationId = parent.OrganizationId
                  ParentNodeId = parent.NodeId
                  ParentOrdinal = parent.Ordinal
                  ParentRetryOf = parent.RetryOf
                  ParentRestoreEpoch = parent.RestoreEpoch
                  AttemptLimit = attemptLimit
                  Outcome = BudgetExhausted }
        elif
            proposedChildId.Value = Guid.Empty
            || proposedChildId = parent.Id
            || parent.RetryOf = Some proposedChildId
        then
            Error(InvalidProposedChildIdentity proposedChildId)
        else
            let child =
                { parent with
                    Id = proposedChildId
                    Ordinal = nextOrdinal
                    RetryOf = Some parent.Id
                    State = Queued
                    Fence = Fence.initial
                    LeaseOwner = None
                    LeaseExpiresAt = None }

            Ok
                { ParentId = parent.Id
                  ParentOrganizationId = parent.OrganizationId
                  ParentNodeId = parent.NodeId
                  ParentOrdinal = parent.Ordinal
                  ParentRetryOf = parent.RetryOf
                  ParentRestoreEpoch = parent.RestoreEpoch
                  AttemptLimit = attemptLimit
                  Outcome = ChildCreated child }

    /// Decide once, or validate and return an exact previously persisted result.
    /// Retryability by terminal status is deliberately caller policy: this law
    /// governs identity, attempt budget, immutable ancestry, and replay only.
    let decide
        (parent: Attempt)
        (attemptLimit: int)
        (proposedChildId: AttemptId)
        (priorDecision: RetryDecision option)
        : Result<RetryDecision, RetryDecisionError> =
        match validateParent parent with
        | Error error -> Error error
        | Ok nextOrdinal ->
            match priorDecision with
            | Some decision -> validatePrior parent nextOrdinal decision
            | None -> decideFresh parent nextOrdinal attemptLimit proposedChildId
