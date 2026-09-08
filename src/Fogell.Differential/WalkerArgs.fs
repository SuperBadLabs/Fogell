namespace Fogell.Differential

open System
open System.Collections.Concurrent
open Fogell.Ir
open Fogell.Groovy.Interpreter

/// FG-105. Step-argument rendering: environment resolution for a step's scope,
/// the ONE render pass over a step's arguments in source order, and the two
/// advisory dialects Jenkins emits around them (the Binding-field `def`
/// advisory and the insecure-interpolation warning). Contract: rendering is
/// EVALUATION — it happens exactly once per argument, in source order, and its
/// side effects (Binding assignments, advisories) go through WalkerCtx.
module WalkerArgs =

    type EnvironmentFailureKind =
        | Parse
        | Lookup
        | Arity
        | Evaluation
        | Coercion

    type EnvironmentEvaluationException(scope: string, name: string, kind: EnvironmentFailureKind, detail: string) =
        inherit Exception($"environment {scope} binding '{name}' cannot be evaluated ({kind}): {detail}")
        member _.Scope = scope
        member _.Name = name
        member _.Kind = kind
        member _.Detail = detail

    type EnvironmentExpressionError =
        { Scope: string
          Name: string
          Kind: EnvironmentFailureKind
          Detail: string }

    let renderEnvironmentExpressionError error =
        $"unsupported_environment_expression: environment {error.Scope} binding '{error.Name}' cannot be evaluated ({error.Kind}): {error.Detail}"

    type private StaticScalarKind =
        | Text of amplification: int * constantChars: int
        | Integer
        | BoundedScalar
        | DynamicScalar

    // Jenkins' Binding-field advisory, emitted in ITS words so the two logs
    // COMPARE — a suppression keyed on this wording alone was the same
    // unconditional-shape-match defect as the retired secret-warning dialect:
    // a build printing the sentence was silently dropped from comparison.
    /// The advisory's TEXT, apart from its emission: FG-123 renders the
    /// `ansiColor` option argument before the SCM block and must print any
    /// advisory it raises AFTER the provenance line Jenkins prints first, so it
    /// captures the text here and emits it there. One definition of the wording.
    let defKeywordAdvisory (name: string, value: Value) =
        $"Did you forget the `def` keyword? WorkflowScript seems to be setting a field named {name} "
        + $"(to a value of type {GString.javaTypeName value}) which could lead to memory leaks or other issues."

    let adviseNewBinding (runCtx: WalkerCtx) (name: string, value: Value) =
        runCtx.Emit(defKeywordAdvisory (name, value))

    /// Legacy quoted values use Declarative's left-to-right resolver and can see
    /// earlier siblings. Unquoted expression values instead use the immutable
    /// environment visible before the block, as measured for FG-260's helper.
    /// A later scope still wins; an unknown legacy quoted name expands to empty.
    // FG-100. The string model lives in `GString`, not here. It was a closure
    // inside `run`, which made it unreachable from tests and invited every consumer
    // to re-derive the rules — 52 findings' worth.
    let interpolate (known: Map<string, string>) (value: string) = GString.interpolate known value

    let private failEnvironment kind scope name detail =
        raise (EnvironmentEvaluationException(scope, name, kind, detail))

    let private preambleFunctions (pipeline: Pipeline) =
        if String.IsNullOrWhiteSpace pipeline.Preamble then
            []
        else
            match Fogell.Groovy.Parser.Parser.parse pipeline.Preamble with
            | Result.Error why -> failEnvironment Parse "pipeline" "<preamble>" $"parse failed: {why}"
            | Result.Ok script ->
                script
                |> List.choose (function
                    | Fogell.Groovy.SFunc(name, parameters, body) -> Some(name, parameters, body)
                    | _ -> None)

    /// Prove a deliberately small, total scalar language before the run creates
    /// or wipes a workspace. Validation starts at environment RHS expressions
    /// and follows only helpers they call; unrelated scripted helpers retain
    /// their historical behavior. The accepted operations cannot perform an
    /// effect and cannot defer lookup, arity or result-shape errors to runtime.
    let internal preflightEnvironmentExpressionErrors
        (pipeline: Pipeline)
        : Result<unit, EnvironmentExpressionError> =
        try
            let functions = preambleFunctions pipeline
            let maxAmplification = 16
            let maxConstantChars = 64 * 1024
            let maxHelperDepth = 16
            let maxAnalysisWork = 10_000
            let mutable analysisWork = 0

            let bind result next =
                match result with
                | Ok value -> next value
                | Error error -> Error error

            let text amplification constantChars =
                if amplification > maxAmplification then
                    Error(Evaluation, $"derived string amplification exceeds {maxAmplification}")
                elif constantChars > maxConstantChars then
                    Error(Evaluation, $"derived string constants exceed {maxConstantChars} characters")
                else
                    Ok(Text(amplification, constantChars))

            let asText = function
                | Text(amplification, constantChars) -> text amplification constantChars
                | Integer -> text 0 20
                | BoundedScalar -> text 0 5
                | DynamicScalar -> text 1 0

            let combineText left right =
                bind (asText left) (fun leftText ->
                    bind (asText right) (fun rightText ->
                        match leftText, rightText with
                        | Text(leftAmplification, leftConstants), Text(rightAmplification, rightConstants) ->
                            text (leftAmplification + rightAmplification) (leftConstants + rightConstants)
                        | _ -> Error(Evaluation, "internal environment text-shape error")))

            let rec expression locals stack value =
                analysisWork <- analysisWork + 1
                let unsupported detail = Error(Evaluation, detail)

                if analysisWork > maxAnalysisWork then
                    unsupported $"static analysis work exceeds {maxAnalysisWork} expressions"
                else
                    match value with
                    | Fogell.Groovy.ENull
                    | Fogell.Groovy.EBool _ -> Ok BoundedScalar
                    | Fogell.Groovy.EInt _ -> Ok Integer
                    | Fogell.Groovy.EStr literal -> text 0 literal.Length
                    | Fogell.Groovy.EVar name ->
                        match Map.tryFind name locals with
                        | Some kind -> Ok kind
                        | None -> Error(Lookup, $"unknown property: {name}")
                    | Fogell.Groovy.EProp(Fogell.Groovy.EVar "env", _) when not (Map.containsKey "env" locals) ->
                        Ok DynamicScalar
                    | Fogell.Groovy.EGString parts ->
                        parts
                        |> List.fold
                            (fun state part ->
                                bind state (fun accumulated ->
                                    match part with
                                    | Fogell.Groovy.GLit literal ->
                                        bind (text 0 literal.Length) (combineText accumulated)
                                    | Fogell.Groovy.GExpr item ->
                                        bind (expression locals stack item) (combineText accumulated)))
                            (text 0 0)
                    | Fogell.Groovy.EBinary("+", left, right) ->
                        bind (expression locals stack left) (fun leftKind ->
                            bind (expression locals stack right) (fun rightKind ->
                                match leftKind, rightKind with
                                | Text _, _
                                | _, Text _ -> combineText leftKind rightKind
                                | _ -> Error(Coercion, "operator '+' requires at least one guaranteed string operand")))
                    | Fogell.Groovy.ECall(Fogell.Groovy.FreeCall name, arguments, None) ->
                        let positional =
                            arguments
                            |> List.choose (function
                                | Fogell.Groovy.APos argument -> Some argument
                                | Fogell.Groovy.ANamed _ -> None)

                        if List.length positional <> List.length arguments then
                            Error(Arity, $"helper '{name}' requires positional arguments")
                        else
                            let sameName = functions |> List.filter (fun (candidate, _, _) -> candidate = name)
                            let matching = sameName |> List.filter (fun (_, parameters, _) -> List.length parameters = List.length positional)

                            match matching with
                            | [] when List.isEmpty sameName && Map.containsKey name WalkerRules.stepDescriptors ->
                                Error(Evaluation, $"hosted step '{name}' is not allowed during environment evaluation")
                            | [] when List.isEmpty sameName -> Error(Lookup, $"unknown helper: {name}")
                            | [] -> Error(Arity, $"helper '{name}' has no arity {List.length positional}")
                            | _ :: _ :: _ -> Error(Arity, $"helper '{name}/{List.length positional}' is ambiguous")
                            | [ (_, parameters, body) ] ->
                                let signature = name, List.length parameters

                                if Set.contains signature stack then
                                    unsupported $"recursive helper '{name}/{List.length parameters}'"
                                elif Set.count stack >= maxHelperDepth then
                                    unsupported $"helper call depth exceeds {maxHelperDepth}"
                                else
                                    let rec argumentsKinds state remaining =
                                        match state, remaining with
                                        | Error error, _ -> Error error
                                        | Ok kinds, [] -> Ok(List.rev kinds)
                                        | Ok kinds, argument :: rest ->
                                            argumentsKinds
                                                (expression locals stack argument
                                                 |> Result.map (fun kind -> kind :: kinds))
                                                rest

                                    bind (argumentsKinds (Ok []) positional) (fun kinds ->
                                        let helperLocals = List.zip parameters kinds |> Map.ofList

                                        match body with
                                        | [ Fogell.Groovy.SReturn(Some returned) ] ->
                                            expression helperLocals (Set.add signature stack) returned
                                        | _ ->
                                            unsupported
                                                $"helper '{name}/{List.length parameters}' must contain one scalar return")
                    | Fogell.Groovy.ECall(Fogell.Groovy.MethodCall(receiver, "replace"), arguments, None) ->
                        match arguments with
                        | [ Fogell.Groovy.APos before; Fogell.Groovy.APos after ] ->
                            bind (expression locals stack receiver) (fun receiverKind ->
                                bind (expression locals stack before) (fun beforeKind ->
                                    bind (expression locals stack after) (fun afterKind ->
                                        match receiverKind, beforeKind, afterKind, before, after with
                                        | Text _, Text _, Text _, Fogell.Groovy.EStr oldValue, Fogell.Groovy.EStr newValue
                                            when oldValue.Length > 0 && newValue.Length <= oldValue.Length ->
                                            Ok receiverKind
                                        | Text _, Text _, Text _, Fogell.Groovy.EStr oldValue, _ when oldValue.Length = 0 ->
                                            Error(Coercion, "replace requires a non-empty literal search string")
                                        | Text _, Text _, Text _, Fogell.Groovy.EStr oldValue, Fogell.Groovy.EStr newValue
                                            when newValue.Length > oldValue.Length ->
                                            Error(Coercion, "replace cannot expand its receiver in the total environment subset")
                                        | Text _, Text _, Text _, _, _ ->
                                            Error(Coercion, "replace arguments must be string literals")
                                        | _ ->
                                            Error(Coercion, "replace requires a guaranteed string receiver and two string arguments"))))
                        | _ -> Error(Arity, "replace requires exactly two positional arguments")
                    | Fogell.Groovy.EList _
                    | Fogell.Groovy.EMap _ -> Error(Coercion, "expression returned a non-scalar value")
                    | Fogell.Groovy.ECall(Fogell.Groovy.FreeCall name, _, Some _) ->
                        unsupported $"helper '{name}' cannot take a closure during environment evaluation"
                    | Fogell.Groovy.ECall(Fogell.Groovy.MethodCall(_, name), _, _)
                    | Fogell.Groovy.ECall(Fogell.Groovy.SafeMethodCall(_, name), _, _) ->
                        unsupported $"method '{name}' is not in the total environment-expression subset"
                    | Fogell.Groovy.EProp _
                    | Fogell.Groovy.ESpreadProp _
                    | Fogell.Groovy.ESafeProp _ -> unsupported "only direct env property reads are supported"
                    | Fogell.Groovy.EIndex _
                    | Fogell.Groovy.EUnary _
                    | Fogell.Groovy.EBinary _
                    | Fogell.Groovy.ETernary _
                    | Fogell.Groovy.EElvis _
                    | Fogell.Groovy.EClosure _ -> unsupported "construct is not in the total environment-expression subset"

            let expressions =
                [ "pipeline", pipeline.Environment
                  for stage in Pipeline.flattenStages pipeline.Stages do
                      $"stage '{stage.Name}'", stage.Environment ]
                |> List.collect (fun (scope, bindings) ->
                    bindings
                    |> List.choose (fun binding ->
                        if binding.Kind = EnvironmentExpression then
                            Some(scope, binding)
                        else
                            None))

            expressions
            |> List.fold
                (fun state (scope, binding) ->
                    bind state (fun _ ->
                        match Fogell.Groovy.Parser.Parser.parse binding.Value with
                        | Error why ->
                            Error
                                { Scope = scope
                                  Name = binding.Name
                                  Kind = Parse
                                  Detail = string why }
                        | Ok [ Fogell.Groovy.SExpr value ] ->
                            match expression Map.empty Set.empty value with
                            | Ok _ -> Ok()
                            | Error(kind, why) ->
                                Error
                                    { Scope = scope
                                      Name = binding.Name
                                      Kind = kind
                                      Detail = why }
                        | Ok _ ->
                            Error
                                { Scope = scope
                                  Name = binding.Name
                                  Kind = Evaluation
                                  Detail = "RHS must be one scalar expression" }))
                (Ok())
        with :? EnvironmentEvaluationException as ex ->
            Error
                { Scope = ex.Scope
                  Name = ex.Name
                  Kind = ex.Kind
                  Detail = ex.Detail }

    let internal preflightEnvironmentExpressions (pipeline: Pipeline) : Result<unit, string> =
        preflightEnvironmentExpressionErrors pipeline
        |> Result.mapError renderEnvironmentExpressionError

    let private scalarText scope name = function
        | VNull -> "null"
        | VBool value -> if value then "true" else "false"
        | VInt value
        | VInteger value
        | VArithmeticInteger value -> string value
        | VFloat value -> Value.javaFloatDisplay value
        | VStr value -> value
        | _ -> failEnvironment Coercion scope name "expression returned a non-scalar value"

    let private faultText = function
        | UnknownProperty property -> $"unknown property: {property}"
        | Denied denial -> $"sandbox denied: {denial}"
        | Unsupported construct -> $"unsupported evaluation: {construct}"
        | BudgetExhausted what -> $"evaluation budget exhausted: {what}"
        | Thrown value ->
            match Value.tryToDisplay value with
            | Value.Text text -> $"expression threw: {text}"
            | Value.DisplayCycleDetected -> "expression threw a cyclic value"
        | fault -> $"expression faulted: {fault}"

    /// FG-260. Evaluate one unquoted environment RHS as effect-free Groovy.
    /// The empty step vocabulary makes every hosted step fail closed; the
    /// before/after checks also reject writes to the Jenkins environment or
    /// script binding even when a helper later returns an apparently safe value.
    let private evaluateExpression scope name visible functions source =
        match Fogell.Groovy.Parser.Parser.parse source with
        | Result.Error why -> failEnvironment Parse scope name $"parse failed: {why}"
        | Result.Ok script ->
            let values = visible |> Map.map (fun _ value -> VStr value)
            let envMap = ref values

            let initial =
                functions
                |> List.fold
                    (fun env (functionName, parameters, body) ->
                        Env.withFunc functionName parameters body env)
                    (Env.ofValues (values |> Map.add "env" (VMap envMap)))

            let outcome = Interpreter.runStrictVars Budget.defaults Set.empty initial script

            match outcome.Fault with
            | Some(UnknownProperty _ as fault) -> failEnvironment Lookup scope name (faultText fault)
            | Some fault -> failEnvironment Evaluation scope name (faultText fault)
            | None -> ()

            if not (List.isEmpty outcome.Effects) then
                failEnvironment Evaluation scope name "expression attempted a hosted effect"

            if not (List.isEmpty outcome.NewBindings) then
                failEnvironment Evaluation scope name "expression assigned outside its owned scope"

            let changedSeed =
                Env.snapshot outcome.Env
                |> Map.toList
                |> List.tryPick (fun (key, value) ->
                    if key = "env" then None
                    else
                        match Map.tryFind key values with
                        | None -> Some key
                        | Some original ->
                            match Value.tryEq original value with
                            | Value.Answer true -> None
                            | _ -> Some key)

            if envMap.Value <> values || changedSeed.IsSome then
                failEnvironment Evaluation scope name "expression mutated the visible environment"

            match outcome.Returned with
            | Some value -> scalarText scope name value
            | None -> failEnvironment Coercion scope name "expression produced no value"

    /// Resolve Declarative environment scopes once. Every RHS in one scope sees
    /// the same pre-block snapshot; declarations are evaluated in source order
    /// but siblings never leak into one another. Stage results are lazy and
    /// cached by their source identity, so retries, post conditions and parallel
    /// consumers cannot re-evaluate a scope.
    let internal envForWithUsing
        evaluate
        (jenkinsProvided: (string * string) list)
        (pipeline: Pipeline)
        : ((string * string) list -> Stage -> (string * string) list) =
        let functions = preambleFunctions pipeline

        let resolve scope (visible: (string * string) list) (bindings: EnvironmentBinding list) =
            let snapshot = visible |> Map.ofList
            let scoped = ResizeArray<string * string>(List.length bindings)
            let mutable quotedVisible = snapshot

            for binding in bindings do
                let value =
                    match binding.Kind with
                    | EnvironmentLiteral -> binding.Value
                    | EnvironmentGString ->
                        // Preserve the historical/measured Declarative
                        // resolver: quoted entries can see earlier quoted
                        // siblings. Quote provenance must not opt them into
                        // the strict expression snapshot below.
                        interpolate quotedVisible binding.Value
                    | EnvironmentExpression ->
                        evaluate scope binding.Name snapshot functions binding.Value

                scoped.Add(binding.Name, value)
                quotedVisible <- Map.add binding.Name value quotedVisible

            // Preserve declaration order for callers that layer withEnv values;
            // Map.ofList at the consumption boundary provides last-wins lookup.
            visible @ List.ofSeq scoped

        let pipelineResolved = resolve "pipeline" jenkinsProvided pipeline.Environment
        let stages = ConcurrentDictionary<int64 * int64 * string, Lazy<(string * string) list>>()

        let stageKey (stage: Stage) = stage.Position.Line, stage.Position.Column, stage.Name

        let parents =
            let rec index parent candidates =
                [ for stage in candidates do
                      yield stageKey stage, parent
                      yield! index (Some stage) stage.Nested ]

            index None pipeline.Stages |> Map.ofList

        let rec resolvedStage (stage: Stage) =
            let key = stageKey stage

            stages.GetOrAdd(
                key,
                fun _ ->
                    lazy
                        (let inherited =
                            match Map.tryFind key parents |> Option.flatten with
                            | Some parent -> resolvedStage parent
                            | None -> pipelineResolved

                         resolve $"stage '{stage.Name}'" inherited stage.Environment)
            ).Value

        fun overlay stage ->
            let stageResolved = resolvedStage stage

            // Preserve envForWith's historical public contract: callers that
            // use list lookup (not only Map.ofList) receive one effective,
            // last-wins value per name.
            stageResolved @ overlay |> Map.ofList |> Map.toList

    let envForWith (jenkinsProvided: (string * string) list) (pipeline: Pipeline) =
        envForWithUsing evaluateExpression jenkinsProvided pipeline

    /// ONE render pass for a step's arguments — source order, side effects
    /// once — plus the insecure-interpolation warning, computed HERE so every
    /// consumer gets it: ordinary steps, wrapper branches, `dir`, `input`.
    /// Rendering only in runStepInner left wrappers expanding secrets with no
    /// warning where Jenkins warns on the step invocation.
    ///
    /// The warning wants a REAL GString: Interpolating kind AND a live `$` in
    /// the source. Every double-quoted argument is Interpolating by kind, but
    /// `echo "abc"` with no placeholder is an ordinary constant — warning on
    /// a coincidental secret value there flags interpolation that never
    /// happened. (An escaped dollar is a sentinel at this point, so any `$`
    /// in the source is live.)
    /// The insecure-interpolation warning for a set of GString-rendered texts,
    /// factored out so BOTH render paths — step arguments and withEnv's
    /// `NAME=value` entries — say what Jenkins says.
    let warnSecretInterpolation (runCtx: WalkerCtx) (ctx: BranchCtx) (stepName: string) (texts: string list) =
        let leaked =
            ctx.Secrets
            |> List.filter (fun b ->
                // a file() credential exports the PATH, not the content
                let exported = if b.ValueVariableCarriesPath then b.FilePath else b.Value
                exported <> "" && texts |> List.exists (fun t -> t.Contains exported))
            |> List.map (fun b -> b.ValueVariable)
            |> List.distinct

        if not (List.isEmpty leaked) then
            runCtx.Emit $"Warning: A secret was passed to \"{stepName}\" using Groovy String interpolation, which is insecure."
            runCtx.Emit $"""Affected argument(s) used the following variable(s): [{String.concat ", " leaked}]"""

    let renderStepArgs (runCtx: WalkerCtx) (envForWith: (string * string) list -> Stage -> (string * string) list) (ctx: BranchCtx) (stage: Stage) (step: Step) : Step =
        let env = envForWith ctx.EnvOverlay stage |> Map.ofList

        // SOURCE order, exactly as recorded: `step label: "...", "..."` must
        // evaluate label first because Groovy does, and evaluation mutates
        // the shared Binding. The partitioned lists cannot say who came
        // first; ArgumentOrder can.
        let order =
            if List.isEmpty step.ArgumentOrder then
                (step.Positional |> List.mapi (fun i _ -> $"#{i}")) @ (step.Named |> List.map fst)
            else
                step.ArgumentOrder

        let renderedByKey =
            order
            |> List.map (fun key ->
                let raw =
                    if key.StartsWith "#" then
                        step.Positional |> List.tryItem (int (key.Substring 1)) |> Option.defaultValue ""
                    else
                        step.Named
                        |> List.tryPick (fun (k, v) -> if k = key then Some v else None)
                        |> Option.defaultValue ""

                key, raw, GString.renderInto runCtx.ScriptBinding (adviseNewBinding runCtx) env step key raw)

        let renderedPositional =
            renderedByKey |> List.filter (fun (k, _, _) -> k.StartsWith "#")

        let renderedNamed =
            renderedByKey |> List.filter (fun (k, _, _) -> not (k.StartsWith "#"))

        let interpolatedTexts =
            renderedPositional @ renderedNamed
            |> List.filter (fun (k, raw, _) ->
                GString.kindOf step k = Interpolating && (GString.sourceOf step k raw).Contains "$")
            |> List.map (fun (_, _, r) -> r)

        warnSecretInterpolation runCtx ctx step.Name interpolatedTexts

        { step with
            Positional = renderedPositional |> List.map (fun (_, _, r) -> r)
            Named = renderedNamed |> List.map (fun (k, _, r) -> k, r) }
