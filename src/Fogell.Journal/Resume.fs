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
      /// The (workspace root, job name) the build ran in, when recorded.
      WorkspaceIdentity: (string * string) option
      /// FG-046b. This build's own identity, when its first attempt recorded
      /// one. Approval action ids derive from it rather than from any pathname.
      BuildIdentity: string option
      /// FG-046b. Answers already given to `input` prompts, per
      /// (stage, index, occurrence): (approved, submitter). Durable and
      /// independent of whether the step that asked ever finished. The
      /// occurrence separates prompts sharing one durability key — two `input`s
      /// in a `timeout` block are two gates, not one.
      InputDecisions: Map<string * int * int, bool * string>
      /// FG-046b. Steps a prior attempt started as `input`. Needed because a
      /// disposition alone does not say WHICH step was interrupted, and the
      /// re-run rule below applies to `input` and to nothing else.
      InputSteps: Set<string * int>
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
                    | WorkspaceIdentity _
                    | BuildIdentity _
                    | InputDecision _
                    | InputDecisionVoided _ -> acc)
                Map.empty

        let inputSteps =
            records
            |> List.choose (function
                | StepStarted(stage, i, "input") -> Some(stage, i)
                | _ -> None)
            |> Set.ofList

        // last answer wins: a re-answered prompt (the first answer's attempt
        // died before the step could act on it) is still one human decision
        let inputDecisions =
            records
            |> List.fold
                (fun acc r ->
                    match r with
                    | InputDecision(stage, i, occ, approved, who) -> Map.add (stage, i, occ) (approved, who) acc
                    // a VOID removes it: the answer exists in the audit trail but
                    // is not actionable, and leaving it in this map is how a
                    // refused-as-late approval gets replayed by a resumed attempt
                    | InputDecisionVoided(stage, i, occ) -> Map.remove (stage, i, occ) acc
                    | _ -> acc)
                Map.empty

        { Steps = steps
          InputDecisions = inputDecisions
          InputSteps = inputSteps
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
                | WorkspaceIdentity(r, j) -> Some(r, j)
                | _ -> None)
          BuildIdentity =
            records
            |> List.tryPick (function
                | BuildIdentity id -> Some id
                | _ -> None)
          NeedsReconciliation =
            steps
            |> Map.toList
            |> List.choose (fun (k, v) ->
                match v with
                // the same rule as [inputAnswered], which cannot be called yet
                | Interrupted when
                    Set.contains k inputSteps
                    && (inputDecisions |> Map.exists (fun (s, i, _) _ -> (s, i) = k))
                    ->
                    None
                | Interrupted -> Some k
                | AlreadyFinished _
                | NotStarted -> None) }

    let dispositionOf (plan: ResumePlan) (stage: string) (index: int) =
        Map.tryFind (stage, index) plan.Steps |> Option.defaultValue NotStarted

    /// FG-046b. An interrupted `input` whose answer is already on record is the
    /// ONE step that is safe to re-run. Every other step is refused because we
    /// cannot know whether its effect happened; an `input`'s only effect IS the
    /// answer, and the answer is durable, so running it again consults the
    /// record instead of asking a human twice. Both halves are required: an
    /// interrupted input with NO decision still refuses (nobody has answered),
    /// and a decision recorded against a non-`input` step grants nothing.
    ///
    /// STATED LIMIT — it covers a BARE top-level `input` only. The durability
    /// unit is the top-level step, so `timeout(…) { input … }` journals as
    /// `timeout`: the answer is still recorded and still survives (the human is
    /// never re-asked for it), but the interrupted WRAPPER refuses reconciliation
    /// as any other wrapper does. That is deliberate, not an omission — a wrapper
    /// body may hold other steps whose effects already landed, and re-running the
    /// body to reach the prompt would re-run those too, which is exactly the
    /// at-least-once outcome ADR 0003 rejects. Ten of the corpus's twenty-two
    /// `input` files use the wrapped shape, so the limit is stated on the board
    /// rather than buried here.
    ///
    /// That a bare top-level `input` asks exactly ONE prompt — occurrence 1 — is
    /// load-bearing beyond this function: this is the only place a resumed
    /// attempt re-runs a prompt to consult a recorded answer, which is what
    /// keeps WalkerCtx.NextInputOccurrence's DERIVED ordinal safe. Widening the
    /// exemption to wrappers requires a durable ordinal first. The test below is
    /// still written as "some occurrence under this key was answered" rather
    /// than hard-coding 1, so such a widening cannot get a silent free pass from
    /// a stale constant.
    let inputAnswered (plan: ResumePlan) (stage: string) (index: int) =
        Set.contains (stage, index) plan.InputSteps
        && plan.InputDecisions |> Map.exists (fun (s, i, _) _ -> s = stage && i = index)

    /// Should this step execute now?
    ///
    /// `false` for a durably finished step — that is exactly-once. `false` for an
    /// interrupted one too, because re-running it is precisely the at-least-once
    /// behaviour this design exists to avoid; it is reported instead. The single
    /// exception is [inputAnswered], stated above.
    let shouldExecute (plan: ResumePlan) (stage: string) (index: int) =
        match dispositionOf plan stage index with
        | AlreadyFinished _ -> false
        | Interrupted -> inputAnswered plan stage index
        | NotStarted -> true

    let isResumable (plan: ResumePlan) = plan.Terminal.IsNone
