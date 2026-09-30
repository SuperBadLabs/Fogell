namespace Fogell.Ir

/// Version 1 native pipeline: ordered stages and explicit, literal operations.
type Operation =
    | Run of command: string
    | Echo of message: string
    | Archive of patterns: string
    | TestReport of patterns: string

type Step =
    { Operation: Operation
      TimeoutSeconds: int
      Environment: (string * string) list
      WorkingDirectory: string
      Always: bool }

type Stage = { Name: string; Steps: Step list }
type Pipeline = { Version: int; Environment: (string * string) list; Stages: Stage list }

module Pipeline =
    let flattenStages (stages: Stage list) = stages
    let totalSteps (pipeline: Pipeline) = pipeline.Stages |> List.sumBy (fun stage -> stage.Steps.Length)
