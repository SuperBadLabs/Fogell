namespace Fogell.Journal

open Fogell.Domain

/// What resume decided about one step.
type StepDisposition =
    /// Finished durably. MUST NOT run again — this is the exactly-once guarantee.
    | AlreadyFinished of BuildStatus
    /// Started, but no outcome was recorded. We do not know whether its effect
    /// happened. It is surfaced for reconciliation rather than silently re-run:
    /// re-running is at-least-once, and that is the semantics ADR 0003 rejects.
    | Interrupted
    /// Never reached. Safe to run.
    | NotStarted

/// The plan for continuing an interrupted attempt.
type ResumePlan =
    { /// Per (stage, index) decision.
      Steps: Map<string * int, StepDisposition>
      /// Stages whose boundary was committed — everything in them is durable.
      CommittedStages: Set<string>
      /// Set when the build already reached a terminal state; resume is a no-op.
      Terminal: BuildStatus option
      /// The definition this journal belongs to, when recorded.
      ScriptDigest: string option
      /// The workspace the build ran in, when recorded.
      WorkspaceIdentity: string option
      /// Steps that started without finishing. Non-empty means a human or a
      /// policy must decide, because the engine genuinely does not know.
      NeedsReconciliation: (string * int) list }

module Resume =

    /// Build a resume plan from a journal.
    let plan (records: Record list) : ResumePlan =
        let steps =
            records
            |> List.fold
                (fun acc r ->
                    match r with
                    | StepStarted(stage, i, _) ->
                        // do not downgrade a finished step if records repeat
                        match Map.tryFind (stage, i) acc with
                        | Some(AlreadyFinished _) -> acc
                        | _ -> Map.add (stage, i) Interrupted acc
                    | StepFinished(stage, i, status) -> Map.add (stage, i) (AlreadyFinished status) acc
                    | StageCommitted _
                    | BuildFinished _
                    | ScriptDigest _
                    | WorkspaceIdentity _ -> acc)
                Map.empty

        { Steps = steps
          CommittedStages =
            records
            |> List.choose (function
                | StageCommitted s -> Some s
                | _ -> None)
            |> Set.ofList
          Terminal =
            records
            |> List.tryPick (function
                | BuildFinished s -> Some s
                | _ -> None)
          ScriptDigest =
            records
            |> List.tryPick (function
                | ScriptDigest d -> Some d
                | _ -> None)
          WorkspaceIdentity =
            records
            |> List.tryPick (function
                | WorkspaceIdentity p -> Some p
                | _ -> None)
          NeedsReconciliation =
            steps
            |> Map.toList
            |> List.choose (fun (k, v) ->
                match v with
                | Interrupted -> Some k
                | AlreadyFinished _
                | NotStarted -> None) }

    let dispositionOf (plan: ResumePlan) (stage: string) (index: int) =
        Map.tryFind (stage, index) plan.Steps |> Option.defaultValue NotStarted

    /// Should this step execute now?
    ///
    /// `false` for a durably finished step — that is exactly-once. `false` for an
    /// interrupted one too, because re-running it is precisely the at-least-once
    /// behaviour this design exists to avoid; it is reported instead.
    let shouldExecute (plan: ResumePlan) (stage: string) (index: int) =
        match dispositionOf plan stage index with
        | AlreadyFinished _ -> false
        | Interrupted -> false
        | NotStarted -> true

    let isResumable (plan: ResumePlan) = plan.Terminal.IsNone
