namespace Fogell.Differential

open System
open Fogell.Ir
open Fogell.Groovy.Interpreter

/// FG-105. Step-argument rendering: environment resolution for a step's scope,
/// the ONE render pass over a step's arguments in source order, and the two
/// advisory dialects Jenkins emits around them (the Binding-field `def`
/// advisory and the insecure-interpolation warning). Contract: rendering is
/// EVALUATION — it happens exactly once per argument, in source order, and its
/// side effects (Binding assignments, advisories) go through WalkerCtx.
module WalkerArgs =

    // Jenkins' Binding-field advisory, emitted in ITS words so the two logs
    // COMPARE — a suppression keyed on this wording alone was the same
    // unconditional-shape-match defect as the retired secret-warning dialect:
    // a build printing the sentence was silently dropped from comparison.
    let adviseNewBinding (runCtx: WalkerCtx) (name: string, value: Value) =
        runCtx.Emit (
            $"Did you forget the `def` keyword? WorkflowScript seems to be setting a field named {name} "
            + $"(to a value of type {GString.javaTypeName value}) which could lead to memory leaks or other issues."
        )

    /// Values in an `environment { }` block interpolate `${NAME}` / `$NAME`
    /// against what is already visible, which is why `PATH = "/x:${PATH}"` is
    /// the idiom in 33 corpus files. Without expansion that assignment WIPES
    /// the inherited PATH — the failure that exposed this was a `tr: not
    /// found` in a differential case, i.e. a build broken by our own env
    /// handling.
    ///
    /// Resolution is a left-to-right fold, so a declaration sees the process
    /// environment plus every earlier declaration, and a later scope wins.
    /// An unknown name expands to empty, matching Groovy's null-to-string.
    // FG-100. The string model lives in `GString`, not here. It was a closure
    // inside `run`, which made it unreachable from tests and invited every consumer
    // to re-derive the rules — 52 findings' worth.
    let interpolate (known: Map<string, string>) (value: string) = GString.interpolate known value

    let envForWith (jenkinsProvided: (string * string) list) (pipeline: Pipeline) (overlay: (string * string) list) (stage: Stage) =
        // REVIEW FIX (Codex, PR #14 round 5): the previous version UNIONED the
        // two scopes' literal-name sets, so a pipeline `VALUE = '$X'` followed
        // by a stage `VALUE = "$X"` left the stage's GString literal — the
        // name was still in the set. Codex had warned in round 4 to "carry
        // provenance with each binding or resolve each scope using its own
        // provenance", and I took the union shortcut anyway. Each scope is now
        // resolved against ITS OWN provenance.
        let withKind (names: Set<string>) (bindings: (string * string) list) =
            bindings |> List.map (fun (k, v) -> k, v, not (Set.contains k names))

        // Jenkins-provided values are plain strings, never GStrings; a
        // withEnv overlay was already resolved where its quote form was known.
        let scoped =
            withKind (jenkinsProvided |> List.map fst |> Set.ofList) jenkinsProvided
            @ withKind pipeline.EnvironmentLiteralNames pipeline.Environment
            @ withKind stage.EnvironmentLiteralNames stage.Environment
            @ withKind (overlay |> List.map fst |> Set.ofList) overlay

        scoped
        |> List.fold
            (fun acc (k, v, interpolates) ->
                Map.add k (if interpolates then interpolate acc v else v) acc)
            Map.empty
        |> Map.toList

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
