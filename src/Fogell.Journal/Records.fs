namespace Fogell.Journal

open Fogell.Domain

/// ADR 0003. One durable record per observable transition.
///
/// The unit of durability is the STEP, not the stage. Forge resumes at a stage
/// boundary and re-executes the interrupted stage — at-least-once, which is
/// harmless for a test stage and a double deploy for a deploy stage. Jenkins
/// resumes mid-step from a serialized continuation and is strictly better here.
/// This is the record set that closes that gap.
type Record =
    /// A step is ABOUT to run. Written and made durable BEFORE execution, so a
    /// crash between the write and the effect is recoverable — we know the step
    /// may have started, which is the only honest state.
    | StepStarted of stage: string * stepIndex: int * stepName: string
    /// A step finished with a known outcome. Only this record makes a step safe
    /// to skip on resume.
    | StepFinished of stage: string * stepIndex: int * status: BuildStatus
    /// A stage boundary — the point at which the journal is fsynced (group
    /// commit). Everything before it is durable.
    | StageCommitted of stage: string
    | BuildFinished of status: BuildStatus
    /// FG-112. The sha256 of the pipeline definition this journal belongs to.
    /// A resume against a CHANGED definition would hybrid-execute two
    /// pipelines against one key space — the digest lets it refuse by name.
    | ScriptDigest of sha256: string
    /// FG-046b. This build's own identity, minted once by its first attempt and
    /// read by every later one. Approval action ids derive from it.
    ///
    /// It exists because deriving that identity from the journal's PATH cannot
    /// be made correct: `resolve` canonicalises symlinks, but a hard link is a
    /// second name for the same inode with no distinguished original, so two
    /// spellings of one journal produced two different ids — and an answer
    /// written under the first was invisible to an attempt opened through the
    /// second, which then asked the human again. A name the journal CARRIES has
    /// no aliases.
    | BuildIdentity of id: string
    /// FG-112. The workspace this journal's build ran in — ROOT and JOB
    /// separately, because distinct pairs can combine to one path while their
    /// controller-side state (artifacts, stashes, SCM records — all under the
    /// root) differs. A resume pointed elsewhere refuses by name.
    | WorkspaceIdentity of root: string * job: string
    /// FG-046b. A human's answer to an `input` prompt, recorded the moment it
    /// arrives and INDEPENDENTLY of the step's own outcome. That independence is
    /// the whole point: an approval is a human act, expensive and unrepeatable,
    /// and a crash between the answer and the step finishing must not ask for it
    /// twice. MEASURED on Jenkins 2.568.1: a pending input survives a controller
    /// restart with the SAME action id, so an approval addressed before the
    /// restart still lands after it. UNPROVEN BY RECEIPT — the differential
    /// harness has no approver on the Jenkins side, so this is probe-measured
    /// (ADR 0005) and lane-asserted (scripts/run-approval-lane.sh).
    ///
    /// `occurrence` counts the prompts under one durability key, from 1. The key
    /// is the TOP-LEVEL step, so two prompts inside one `timeout` share it —
    /// without the ordinal, the first human's answer was cached and handed
    /// straight to the second gate, which nobody had reviewed.
    | InputDecision of
        stage: string *
        stepIndex: int *
        occurrence: int *
        approved: bool *
        submitter: string
    /// FG-046b. An answer that arrived but was REFUSED — it was first observed
    /// after the prompt's deadline had already passed, or after a failFast
    /// sibling had already failed. The decision record stays (a human really did
    /// answer, and the audit trail should say so); this says it was not acted on
    /// and must never be.
    ///
    /// Durable because the alternative replays it. A refused-as-late answer that
    /// is durable but unmarked survives a kill in the window before the abort is
    /// recorded, and the resumed attempt — with a FRESH deadline — finds it on
    /// record, honours it, and walks past the gate the timeout had already
    /// closed. The safety bound is then defeated by a crash rather than by an
    /// approval.
    | InputDecisionVoided of stage: string * stepIndex: int * occurrence: int
    /// FG-046b. An answer that has been READ but not yet ruled eligible. It says
    /// a human answered — it is not lost, and an operator can see it — but it is
    /// NOT actionable, so resume will not act on it.
    ///
    /// It exists because eligibility cannot be decided before the write. A
    /// deadline-bound prompt must refuse an answer first observed after expiry,
    /// and the read itself takes time: the inbox read, the append and the FSYNC
    /// can all straddle the deadline. Recording the answer as actionable and
    /// voiding it afterwards leaves a window — crash between the two and the
    /// resumed attempt finds a usable approval for a gate that had already
    /// closed. So a bounded prompt records the answer provisionally, the walker
    /// rules on it against the deadline, and only then is it promoted to an
    /// actionable [InputDecision]. An UNBOUNDED prompt skips all of this: with no
    /// deadline an answer cannot be late, so it is actionable when written.
    /// FG-046b. This prompt was waiting under a DEADLINE. Written the first time
    /// a bounded prompt polls, so a later attempt can tell that the gate had a
    /// safety bound on it — which the journal otherwise does not say.
    ///
    /// It exists for the OFFLINE answer: a host killed while a bounded prompt is
    /// pending, an answer written to the inbox afterwards, and startup adopting
    /// it as actionable. The resumed walker then builds a FRESH deadline and
    /// honours it immediately, so an answer that arrived long after the original
    /// bound expired opens the gate anyway. Eligibility cannot be established
    /// after the fact — the original deadline died with the attempt that set it
    /// (see FG-046c) — so a bounded prompt is simply not adoptable, and this
    /// record is how startup knows.
    | InputPromptBounded of stage: string * stepIndex: int * occurrence: int
    | InputAnswerProvisional of
        stage: string *
        stepIndex: int *
        occurrence: int *
        approved: bool *
        submitter: string

module Record =

    /// FG-046b. A field that came from OUTSIDE the engine (an approval's
    /// submitter is whatever the approver wrote) cannot be allowed to carry a
    /// tab or a newline into a tab-separated, line-per-record journal: the
    /// resulting line would decode to None, `Journal.read` stops at the first
    /// undecodable line, and every durable record BEHIND it would be forgotten
    /// — steps re-running is exactly the at-least-once outcome ADR 0003 exists
    /// to reject. Separators become spaces; nothing else is touched.
    let private oneLine (s: string) =
        if isNull s then
            ""
        else
            s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')

    /// Text encoding, one record per line, tab separated. Deliberately plain: a
    /// journal that cannot be read with `cat` during an incident is worse than a
    /// slightly larger one.
    let encode =
        function
        | StepStarted(stage, i, name) -> $"step-started\t{stage}\t{i}\t{name}"
        | StepFinished(stage, i, status) -> $"step-finished\t{stage}\t{i}\t{BuildStatus.toWireString status}"
        | StageCommitted stage -> $"stage-committed\t{stage}"
        | BuildFinished status -> $"build-finished\t{BuildStatus.toWireString status}"
        | ScriptDigest d -> $"script-digest\t{d}"
        | WorkspaceIdentity(r, j) -> $"workspace-identity\t{r}\t{j}"
        | BuildIdentity id -> $"build-identity\t{oneLine id}"
        | InputDecision(stage, i, occurrence, approved, who) ->
            let verdict = if approved then "approved" else "rejected"
            $"input-decision\t{oneLine stage}\t{i}\t{occurrence}\t{verdict}\t{oneLine who}"
        | InputDecisionVoided(stage, i, occurrence) -> $"input-decision-voided\t{oneLine stage}\t{i}\t{occurrence}"
        | InputPromptBounded(stage, i, occurrence) -> $"input-prompt-bounded\t{oneLine stage}\t{i}\t{occurrence}"
        | InputAnswerProvisional(stage, i, occurrence, approved, who) ->
            let verdict = if approved then "approved" else "rejected"
            $"input-answer-provisional\t{oneLine stage}\t{i}\t{occurrence}\t{verdict}\t{oneLine who}"

    let decode (line: string) : Record option =
        match line.Split '\t' with
        | [| "step-started"; stage; i; name |] ->
            match System.Int32.TryParse i with
            | true, idx -> Some(StepStarted(stage, idx, name))
            | _ -> None
        | [| "step-finished"; stage; i; status |] ->
            match System.Int32.TryParse i, BuildStatus.ofWireString status with
            | (true, idx), Some s -> Some(StepFinished(stage, idx, s))
            | _ -> None
        | [| "stage-committed"; stage |] -> Some(StageCommitted stage)
        | [| "build-finished"; status |] -> BuildStatus.ofWireString status |> Option.map BuildFinished
        | [| "script-digest"; d |] -> Some(ScriptDigest d)
        | [| "workspace-identity"; r; j |] -> Some(WorkspaceIdentity(r, j))
        | [| "build-identity"; id |] when id <> "" -> Some(BuildIdentity id)
        | [| "input-decision-voided"; stage; i; occurrence |] ->
            match System.Int32.TryParse i, System.Int32.TryParse occurrence with
            | (true, idx), (true, occ) -> Some(InputDecisionVoided(stage, idx, occ))
            | _ -> None
        | [| "input-prompt-bounded"; stage; i; occurrence |] ->
            match System.Int32.TryParse i, System.Int32.TryParse occurrence with
            | (true, idx), (true, occ) -> Some(InputPromptBounded(stage, idx, occ))
            | _ -> None
        | [| "input-answer-provisional"; stage; i; occurrence; verdict; who |] ->
            match System.Int32.TryParse i, System.Int32.TryParse occurrence, verdict with
            | (true, idx), (true, occ), "approved" -> Some(InputAnswerProvisional(stage, idx, occ, true, who))
            | (true, idx), (true, occ), "rejected" -> Some(InputAnswerProvisional(stage, idx, occ, false, who))
            | _ -> None
        | [| "input-decision"; stage; i; occurrence; verdict; who |] ->
            match System.Int32.TryParse i, System.Int32.TryParse occurrence, verdict with
            | (true, idx), (true, occ), "approved" -> Some(InputDecision(stage, idx, occ, true, who))
            | (true, idx), (true, occ), "rejected" -> Some(InputDecision(stage, idx, occ, false, who))
            // an unrecognised verdict is NOT a silent approval and not a silent
            // rejection: it fails to decode, which stops the read where the
            // damage starts rather than guessing a human's answer
            | _ -> None
        | _ -> None
