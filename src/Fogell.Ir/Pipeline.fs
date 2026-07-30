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
      /// Named arguments whose value was SINGLE-quoted, and so is literal.
      ///
      /// FG-046. Groovy interpolates a double-quoted GString and leaves a
      /// single-quoted string alone, and a step that renders text itself — `input`'s
      /// message and confirmation label — has to honour that. Shell steps get away
      /// without it because the shell performs its own `$VAR` expansion, which
      /// coincides with Groovy's for the common case; `input` has no shell.
      /// Mirrors Stage.EnvironmentLiteralNames.
      LiteralNamedArgs: Set<string>
      /// Argument values with escaped dollars PRESERVED, for consumers that
      /// interpolate. Keyed by argument name, or `#0`, `#1`… for positionals.
      ///
      /// FG-046. `Named`/`Positional` deliberately hold the plain value, because the NUL
      /// sentinel that marks an escaped dollar must never reach a consumer that forwards
      /// text verbatim — it once reached a shell. But `input` renders its prompt itself
      /// and must show `\$TARGET` literally, so it needs the escape-preserving form. This
      /// is ADDITIVE on purpose: a consumer that forgets it shows a `$` expanded that
      /// should not have been, which is cosmetic, whereas one that forgets to strip a
      /// sentinel corrupts a command.
      InterpolationSource: (string * string) list
      /// Argument names (or `#0`, `#1`…) whose value was written UNQUOTED, and is
      /// therefore a Groovy EXPRESSION rather than text — `input message: env.TARGET`.
      /// Jenkins evaluates it; emitting the source text shows `env.TARGET` to the user.
      ExpressionArgs: Set<string>
      /// Indices of POSITIONAL arguments that were single-quoted. `input 'Deploy ${X}?'`
      /// is literal on Jenkins, and the positional form is as common as the named one.
      LiteralPositionalArgs: Set<int>
      /// Raw source of the arguments, retained because ADR 0002 says the
      /// interpreter — not the parser — decides what an expression means.
      RawArgs: string
      Position: Position }

type WhenCondition =
    | WhenBranch of string
    | WhenTag of string
    /// FG-048b. Conditions whose truth depends on build CONTEXT that a plain pipeline
    /// job does not have — an SCM changelog, a multibranch CHANGE_ID, a timer trigger,
    /// a restart. MEASURED on the pinned Jenkins: with that context absent, every one of
    /// them is FALSE and the stage is skipped, and the build is a success. Modelling that
    /// is what turns a fail-closed refusal into a working build for the corpus files
    /// using them.
    /// Receipt: `when-context-conditions`.
    | WhenBuildingTag
    | WhenChangeRequest
    | WhenChangeset of pattern: string
    | WhenChangelog of pattern: string
    | WhenTriggeredBy of cause: string
    | WhenIsRestartedRun
    /// `beforeAgent` / `beforeInput` / `beforeOptions`: an evaluation-ORDER directive, not
    /// a condition. It never changes whether the stage runs, so it is neutral — true
    /// under conjunction. Treating it as unmodelled made the whole `when` fail closed.
    | WhenEvaluationOption
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
      /// FG-045. Stage-level `options { }`. Previously discarded outright, so a
      /// `timeout` declared here bounded nothing and the stage ran unbounded.
      Options: Step list
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
