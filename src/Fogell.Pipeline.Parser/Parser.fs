module Fogell.Pipeline.Parser.Parser

open FParsec
open Fogell.Ir
open Fogell.Admission
open Fogell.Pipeline.Parser.Lexeme

// ---------------------------------------------------------------------------
// Steps
// ---------------------------------------------------------------------------

/// A step call. Four shapes appear in real Jenkinsfiles:
///   command form            sh 'make'
///   paren form              sh(script: 'make', returnStdout: true)
///   named args, no parens   archiveArtifacts artifacts: '*.jar', fingerprint: true
///   block form              withEnv(['A=1']) { … }  /  script { … }
///
/// Argument *values* are captured as source text, not evaluated (ADR 0002).
let private stepParser, private stepRef = createParserForwardedToRef<Step, unit> ()

let private namedArg: P<string * string> =
    attempt (
        identifier .>>? symbol ":" .>>. (
            (stringLiteral |>> id)
            <|> (many1Satisfy (fun c -> c <> ',' && c <> ')' && c <> '\n' && c <> '}') .>> ws
                 |>> fun s -> s.Trim())))

let private positionalArg: P<string> =
    stringLiteral
    <|> (balancedRaw '[' ']')
    <|> (many1Satisfy (fun c -> c <> ',' && c <> ')' && c <> '\n' && c <> '{' && c <> '}') .>> ws
         |>> fun s -> s.Trim())

let private argList: P<(string * string) list * string list> =
    let one =
        (namedArg |>> Choice1Of2) <|> (positionalArg |>> Choice2Of2)

    sepBy one (symbol ",")
    |>> fun items ->
            let named = items |> List.choose (function Choice1Of2 x -> Some x | _ -> None)
            let pos = items |> List.choose (function Choice2Of2 x -> Some x | _ -> None)
            named, pos

let private stepBlock: P<Step list> =
    between (symbol "{") (symbol "}") (ws >>. many (attempt stepParser))

stepRef.Value <-
    ws
    >>. pipe4 position identifier
            (opt (attempt (balancedRaw '(' ')')) )
            (opt (attempt (ws >>. argList)))
            (fun pos name parens inlineArgs -> pos, name, parens, inlineArgs)
    .>>. opt (attempt stepBlock)
    |>> fun ((pos, name, parens, inlineArgs), block) ->
            let named, positional =
                match parens with
                | Some raw ->
                    // re-parse the captured paren body for named/positional
                    let body = if raw.Length >= 2 then raw.Substring(1, raw.Length - 2) else ""
                    match runParserOnString (ws >>. argList .>> eof) () "args" body with
                    | ParserResult.Success((n, p), _, _) -> n, p
                    | ParserResult.Failure _ -> [], (if body.Trim() = "" then [] else [ body.Trim() ])
                | None ->
                    match inlineArgs with
                    | Some(n, p) -> n, p
                    | None -> [], []

            { Name = name
              Positional = positional
              Named = named
              Block = defaultArg block []
              RawArgs = defaultArg parens ""
              Position = pos }

// ---------------------------------------------------------------------------
// Sections
// ---------------------------------------------------------------------------

let private keyValueBody: P<(string * string) list> =
    // `NAME = value` lines inside environment { } / tools { }
    many (
        attempt (
            ws
            >>. identifier
            .>> symbol "="
            .>>. (stringLiteral
                  <|> (many1Satisfy (fun c -> c <> '\n') |>> fun s -> s.Trim()))
            .>> ws))

let private environmentSection: P<(string * string) list> =
    keyword "environment" >>. between (symbol "{") (symbol "}") keyValueBody

let private toolsSection: P<(string * string) list> =
    keyword "tools" >>. between (symbol "{") (symbol "}") keyValueBody

let private agentSpec: P<AgentSpec> =
    let inner =
        choice
            [ attempt (keyword "any" >>% AgentAny)
              attempt (keyword "none" >>% AgentNone)
              attempt (keyword "label" >>. stringLiteral |>> AgentLabel)
              attempt (
                  keyword "docker"
                  >>. (attempt (between (symbol "{") (symbol "}") (
                          ws >>. many (attempt (identifier .>>. (stringLiteral <|> (many1Satisfy (fun c -> c <> '\n') |>> fun s -> s.Trim())) .>> ws)))
                       |>> fun kvs ->
                               let img = kvs |> List.tryPick (fun (k, v) -> if k = "image" then Some v else None)
                               AgentDocker(defaultArg img "", None))
                       <|> (stringLiteral |>> fun img -> AgentDocker(img, None))))
              attempt (keyword "dockerfile" >>. opt (balancedBody '{' '}') |>> fun _ -> AgentDockerfile None)
              (identifier .>> opt (attempt (balancedRaw '{' '}')) |>> AgentUnmodelled) ]

    keyword "agent"
    >>. (attempt (between (symbol "{") (symbol "}") (ws >>. inner))
         <|> inner)

let private postConditionName: P<PostCondition> =
    choice
        [ attempt (keyword "always" >>% Always)
          attempt (keyword "success" >>% Success)
          attempt (keyword "failure" >>% Failure)
          attempt (keyword "unstable" >>% Unstable)
          attempt (keyword "aborted" >>% Aborted)
          attempt (keyword "changed" >>% Changed)
          attempt (keyword "cleanup" >>% Cleanup)
          attempt (keyword "fixed" >>% Fixed)
          attempt (keyword "regression" >>% Regression)
          attempt (keyword "notBuilt" >>% NotBuilt) ]

let private postSection: P<(PostCondition * Step list) list> =
    keyword "post"
    >>. between (symbol "{") (symbol "}") (
        ws >>. many (attempt (postConditionName .>>. stepBlock)))

let rec private whenCondition: P<WhenCondition> =
    parse {
        let! _ = ws
        return! choice
                    [ attempt (keyword "branch" >>. stringLiteral |>> WhenBranch)
                      attempt (keyword "environment" >>. (
                          identifier .>> symbol "=" .>>. stringLiteral |>> WhenEnvironment))
                      attempt (keyword "expression" >>. balancedBody '{' '}' |>> WhenExpression)
                      attempt (keyword "allOf" >>. between (symbol "{") (symbol "}") (many (attempt whenCondition)) |>> WhenAllOf)
                      attempt (keyword "anyOf" >>. between (symbol "{") (symbol "}") (many (attempt whenCondition)) |>> WhenAnyOf)
                      attempt (keyword "not" >>. between (symbol "{") (symbol "}") whenCondition |>> WhenNot)
                      (identifier .>>. (attempt (balancedRaw '{' '}') <|> attempt (balancedRaw '(' ')') <|> (restOfLine true |>> fun s -> s.Trim()))
                       |>> WhenUnmodelled) ]
    }

let private whenSection: P<WhenCondition> =
    keyword "when" >>. between (symbol "{") (symbol "}") (ws >>. whenCondition)

// ---------------------------------------------------------------------------
// Stages
// ---------------------------------------------------------------------------

let private stageParser, private stageRef = createParserForwardedToRef<Stage, unit> ()

let private stagesBody: P<Stage list> =
    between (symbol "{") (symbol "}") (ws >>. many (attempt stageParser))

/// One `stage('Name') { … }`. Sections may appear in any order, so this
/// accumulates whatever it finds rather than demanding a fixed sequence — which
/// is also what makes an unknown section a *named* rejection instead of a
/// generic syntax error.
type private StageSection =
    | SecAgent of AgentSpec
    | SecEnv of (string * string) list
    | SecSteps of Step list
    | SecWhen of WhenCondition
    | SecPost of (PostCondition * Step list) list
    | SecNested of Stage list * bool
    | SecOther of string

stageRef.Value <-
    ws
    >>. keyword "stage"
    >>. pipe2 position
            (attempt (between (symbol "(") (symbol ")") stringLiteral) <|> stringLiteral)
            (fun p n -> p, n)
    .>>. between (symbol "{") (symbol "}") (
        ws
        >>. many (
            attempt (
                choice
                    [ attempt (agentSpec |>> SecAgent)
                      attempt (environmentSection |>> SecEnv)
                      attempt (keyword "steps" >>. stepBlock |>> SecSteps)
                      attempt (whenSection |>> SecWhen)
                      attempt (postSection |>> SecPost)
                      attempt (keyword "stages" >>. stagesBody |>> fun ss -> SecNested(ss, false))
                      attempt (keyword "parallel" >>. stagesBody |>> fun ss -> SecNested(ss, true))
                      attempt (keyword "options" >>. stepBlock |>> fun _ -> SecOther "options")
                      attempt (keyword "input" >>. (attempt (balancedRaw '{' '}') <|> balancedRaw '(' ')') |>> fun _ -> SecOther "input")
                      attempt (keyword "tools" >>. between (symbol "{") (symbol "}") keyValueBody |>> fun _ -> SecOther "tools")
                      attempt (keyword "matrix" >>. balancedRaw '{' '}' |>> fun _ -> SecOther "matrix")
                      attempt (keyword "axes" >>. balancedRaw '{' '}' |>> fun _ -> SecOther "axes")
                      (identifier .>>. (attempt (balancedRaw '{' '}') <|> attempt (balancedRaw '(' ')')) |>> fun (n, _) -> SecOther n) ])))
    |>> fun ((pos, name), sections) ->
            let pick f = sections |> List.tryPick f
            { Name = name
              Agent = pick (function SecAgent a -> Some a | _ -> None)
              Environment = defaultArg (pick (function SecEnv e -> Some e | _ -> None)) []
              Steps = defaultArg (pick (function SecSteps s -> Some s | _ -> None)) []
              When = pick (function SecWhen w -> Some w | _ -> None)
              Post = defaultArg (pick (function SecPost p -> Some p | _ -> None)) []
              Nested = defaultArg (pick (function SecNested(s, _) -> Some s | _ -> None)) []
              IsParallel = defaultArg (pick (function SecNested(_, p) -> Some p | _ -> None)) false
              Position = pos }

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------

type private TopSection =
    | TopAgent of AgentSpec
    | TopEnv of (string * string) list
    | TopTools of (string * string) list
    | TopOptions of Step list
    | TopParameters of Step list
    | TopTriggers of Step list
    | TopStages of Stage list
    | TopPost of (PostCondition * Step list) list
    | TopOther of string

let private topSection: P<TopSection> =
    choice
        [ attempt (agentSpec |>> TopAgent)
          attempt (environmentSection |>> TopEnv)
          attempt (toolsSection |>> TopTools)
          attempt (keyword "options" >>. stepBlock |>> TopOptions)
          attempt (keyword "parameters" >>. stepBlock |>> TopParameters)
          attempt (keyword "triggers" >>. stepBlock |>> TopTriggers)
          attempt (keyword "stages" >>. stagesBody |>> TopStages)
          attempt (postSection |>> TopPost)
          attempt (keyword "libraries" >>. balancedRaw '{' '}' |>> fun _ -> TopOther "libraries")
          (identifier .>>. (attempt (balancedRaw '{' '}') <|> attempt (balancedRaw '(' ')')) |>> fun (n, _) -> TopOther n) ]

/// Leading trivia a real Jenkinsfile carries before `pipeline {`: a shebang,
/// `@Library` annotations, `import` lines, and top-level `def`s. All are legal
/// and none belong to the Declarative schema, so they are skipped here rather
/// than rejected.
let private preamble: P<unit> =
    let shebang = attempt (skipString "#!" >>. skipRestOfLine true)
    let annotation =
        attempt (skipString "@" >>. skipMany1Satisfy isIdentCont
                 >>. opt (balancedRaw '(' ')') >>. ws >>. opt (attempt (symbol "_")) >>% ())
    let importLine = attempt (keyword "import" >>. skipRestOfLine true)
    let defLine =
        attempt (
            keyword "def"
            >>. skipMany1Satisfy isIdentCont
            >>. ws
            >>. opt (attempt (balancedRaw '(' ')'))
            >>. ws
            >>. (attempt (balancedRaw '{' '}' >>% ())
                 <|> (opt (symbol "=") >>. skipRestOfLine true)))

    ws >>. skipMany (choice [ shebang; annotation; importLine; defLine ] .>> ws)

/// Skip forward to the `pipeline {` token. Real Jenkinsfiles put helper
/// functions, `@Library` annotations, imports and `properties(...)` calls both
/// before *and* after the declarative block; none of that is part of the
/// Declarative schema. Iterative (skipManyTill), so a long file cannot
/// overflow the stack the way a recursive scan would.
let private skipToPipeline: P<unit> =
    skipManyTill anyChar (lookAhead (attempt (keyword "pipeline" >>. skipChar '{')))

let private pipelineParser: P<Pipeline> =
    preamble
    >>. skipToPipeline
    >>. keyword "pipeline"
    >>. between (symbol "{") (symbol "}") (ws >>. many (attempt topSection))
    .>> ws
    |>> fun sections ->
            let pick f = sections |> List.tryPick f
            { Agent = defaultArg (pick (function TopAgent a -> Some a | _ -> None)) AgentNone
              Environment = defaultArg (pick (function TopEnv e -> Some e | _ -> None)) []
              Tools = defaultArg (pick (function TopTools t -> Some t | _ -> None)) []
              Options = defaultArg (pick (function TopOptions o -> Some o | _ -> None)) []
              Parameters = defaultArg (pick (function TopParameters p -> Some p | _ -> None)) []
              Triggers = defaultArg (pick (function TopTriggers t -> Some t | _ -> None)) []
              Stages = defaultArg (pick (function TopStages s -> Some s | _ -> None)) []
              Post = defaultArg (pick (function TopPost p -> Some p | _ -> None)) [] }

/// Does this source look like a Declarative pipeline at all? Deliberately
/// stricter than Forge's bare regex: the token must not be inside a line
/// comment or a string, which Forge's version does not check (FG-012).
let looksDeclarative (source: string) : bool =
    let stripped =
        System.Text.RegularExpressions.Regex.Replace(
            source,
            @"//[^\n]*|/\*.*?\*/|'(?:[^'\\\n]|\\.)*'|""(?:[^""\\\n]|\\.)*""",
            " ",
            System.Text.RegularExpressions.RegexOptions.Singleline)

    System.Text.RegularExpressions.Regex.IsMatch(stripped, @"(^|[\s};])pipeline\s*\{")

/// Parse a Declarative Jenkinsfile. Admission limits are applied first so a
/// hostile input never reaches the recursive grammar.
let parseWithLimits (limits: Limits) (source: string) : Result<Pipeline, AdmissionError> =
    match Limits.precheck limits source with
    | Result.Error e -> Result.Error e
    | Result.Ok() ->
        if not (looksDeclarative source) then
            Result.Error(AdmissionError.at NoPipelineBlock 1L 1L "no declarative `pipeline { }` block found")
        else
            match runParserOnString (pipelineParser .>> ws) () "Jenkinsfile" source with
            | ParserResult.Success(p, _, _) ->
                if List.isEmpty p.Stages then
                    Result.Error(AdmissionError.at NoStages 1L 1L "pipeline declares no stages")
                else
                    Result.Ok p
            | ParserResult.Failure(msg, err, _) ->
                let pos = err.Position

                let firstLine =
                    msg.Split('\n')
                    |> Array.filter (fun l -> l.Trim() <> "")
                    |> Array.tryLast
                    |> Option.defaultValue "unparsable"

                Result.Error(AdmissionError.at MalformedSyntax pos.Line pos.Column (firstLine.Trim()))

let parse (source: string) : Result<Pipeline, AdmissionError> =
    parseWithLimits Limits.defaults source
