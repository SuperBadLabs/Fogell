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
    | InputDecision of stage: string * stepIndex: int * approved: bool * submitter: string

module Record =

    /// Text encoding, one record per line, tab separated. Deliberately plain: a
    /// journal that cannot be read with `cat` during an incident is worse than a
    /// slightly larger one.
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

    let encode =
        function
        | StepStarted(stage, i, name) -> $"step-started\t{stage}\t{i}\t{name}"
        | StepFinished(stage, i, status) -> $"step-finished\t{stage}\t{i}\t{BuildStatus.toWireString status}"
        | StageCommitted stage -> $"stage-committed\t{stage}"
        | BuildFinished status -> $"build-finished\t{BuildStatus.toWireString status}"
        | ScriptDigest d -> $"script-digest\t{d}"
        | WorkspaceIdentity(r, j) -> $"workspace-identity\t{r}\t{j}"
        | InputDecision(stage, i, approved, who) ->
            let verdict = if approved then "approved" else "rejected"
            $"input-decision\t{oneLine stage}\t{i}\t{verdict}\t{oneLine who}"

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
        | [| "input-decision"; stage; i; verdict; who |] ->
            match System.Int32.TryParse i, verdict with
            | (true, idx), "approved" -> Some(InputDecision(stage, idx, true, who))
            | (true, idx), "rejected" -> Some(InputDecision(stage, idx, false, who))
            // an unrecognised verdict is NOT a silent approval and not a silent
            // rejection: it fails to decode, which stops the read where the
            // damage starts rather than guessing a human's answer
            | _ -> None
        | _ -> None
