namespace Fogell.Ir

/// Canonical pipeline model. This is *not* a static IR in the McLoving sense —
/// ADR 0002 rejected lowering, so expression-shaped things stay as source text
/// for the interpreter to evaluate. What this model does provide is the
/// *structure* Jenkins' Declarative schema guarantees, validated once.
type AgentSpec =
    | AgentAny
    | AgentNone
    | AgentLabel of string
    | AgentDocker of image: string * args: string option
    | AgentDockerfile of dir: string option
    /// `agent { kubernetes { ... } }` and friends — recognised, not modelled.
    | AgentUnmodelled of kind: string

/// A step is a call: a name, positional arguments, named arguments, and an
/// optional trailing block. `sh 'make'`, `archiveArtifacts artifacts: '*.jar'`,
/// and `withEnv(['A=1']) { ... }` are all this shape.
type Step =
    { Name: string
      Positional: string list
      Named: (string * string) list
      Block: Step list
      /// Raw source of the arguments, retained because ADR 0002 says the
      /// interpreter — not the parser — decides what an expression means.
      RawArgs: string
      Position: Position }

type WhenCondition =
    | WhenBranch of string
    | WhenTag of string
    | WhenEquals of expected: string * actual: string
    | WhenEnvironment of name: string * value: string
    | WhenExpression of source: string
    | WhenAllOf of WhenCondition list
    | WhenAnyOf of WhenCondition list
    | WhenNot of WhenCondition
    | WhenUnmodelled of kind: string * source: string

type PostCondition =
    | Always
    | Success
    | Failure
    | Unstable
    | Aborted
    | Changed
    | Cleanup
    | Fixed
    | Regression
    | NotBuilt

type Stage =
    { Name: string
      Agent: AgentSpec option
      Environment: (string * string) list
      /// Names whose value was written with a LITERAL quote form (single, triple
      /// single, or slashy). Groovy does not interpolate those, so neither may we
      /// — expanding `'$BUILD_NUMBER'` runs a different value than Jenkins and can
      /// produce a false differential match.
      EnvironmentLiteralNames: Set<string>
      Steps: Step list
      When: WhenCondition option
      Post: (PostCondition * Step list) list
      /// Nested `stages { }` (sequential) and `parallel { }` children.
      Nested: Stage list
      IsParallel: bool
      /// `failFast true` as a STAGE-level directive — a sibling of `parallel`,
      /// which is the only placement Jenkins accepts (it rejects it inside the
      /// `parallel { }` block with "Expected a stage"). Also set by the
      /// pipeline-wide `parallelsAlwaysFailFast()` option. Only meaningful when
      /// IsParallel is true.
      FailFast: bool
      Position: Position }

type Pipeline =
    { Agent: AgentSpec
      Environment: (string * string) list
      /// See Stage.EnvironmentLiteralNames.
      EnvironmentLiteralNames: Set<string>
      Options: Step list
      Parameters: Step list
      Triggers: Step list
      Tools: (string * string) list
      Stages: Stage list
      Post: (PostCondition * Step list) list }

module Pipeline =

    let empty =
        { Agent = AgentNone
          Environment = []
          EnvironmentLiteralNames = Set.empty
          Options = []
          Parameters = []
          Triggers = []
          Tools = []
          Stages = []
          Post = [] }

    /// Flatten nested/parallel stages into execution order for planning.
    let rec flattenStages (stages: Stage list) : Stage list =
        stages
        |> List.collect (fun s -> s :: flattenStages s.Nested)

    let rec countSteps (steps: Step list) : int =
        steps |> List.sumBy (fun s -> 1 + countSteps s.Block)

    let totalSteps (p: Pipeline) : int =
        flattenStages p.Stages
        |> List.sumBy (fun s ->
            countSteps s.Steps
            + (s.Post |> List.sumBy (fun (_, ss) -> countSteps ss)))
