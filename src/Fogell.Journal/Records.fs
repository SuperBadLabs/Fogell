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

module Record =

    /// Text encoding, one record per line, tab separated. Deliberately plain: a
    /// journal that cannot be read with `cat` during an incident is worse than a
    /// slightly larger one.
    let encode =
        function
        | StepStarted(stage, i, name) -> $"step-started\t{stage}\t{i}\t{name}"
        | StepFinished(stage, i, status) -> $"step-finished\t{stage}\t{i}\t{BuildStatus.toWireString status}"
        | StageCommitted stage -> $"stage-committed\t{stage}"
        | BuildFinished status -> $"build-finished\t{BuildStatus.toWireString status}"

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
        | _ -> None
