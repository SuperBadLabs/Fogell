namespace Fogell.Differential

open System
open Fogell.Ir
open Fogell.Groovy.Interpreter

/// FG-105. `when { }` evaluation. Contract (FG-048): None means "cannot be
/// evaluated at all", which is NOT false — running a stage Jenkins would skip
/// and skipping one Jenkins would run are both divergences, so an unmodelled
/// condition makes the caller fail the build with a named reason instead of
/// guessing a direction. Pure given the env resolver: no walker state.
module WalkerWhen =

    /// FG-048. Evaluate a `when` condition. Returns None when the condition
    /// cannot be evaluated at all, which is NOT the same as false: running
    /// a stage Jenkins would skip and skipping one Jenkins would run are
    /// both divergences, so an unmodelled condition fails the build with a
    /// named reason instead of guessing a direction.
    let rec evalWhen
            (restarted: bool)
            (envForWith: (string * string) list -> Stage -> (string * string) list)
            (stage: Stage)
            (cond: WhenCondition)
            : bool option =
        let env = envForWith [] stage |> Map.ofList

        match cond with
        | WhenEnvironment(name, value) ->
            Some(Map.tryFind name env = Some value)

        // MEASURED: on a plain (non-multibranch) pipeline job Jenkins
        // SKIPS a `branch` or `tag` stage, because BRANCH_NAME/TAG_NAME do
        // not exist. So absent-means-false is Jenkins' behaviour, not a
        // guess, and it is what lets these two conditions be modelled
        // instead of refused — `branch` and `tag` together account for
        // most `when` usage in the corpus.
        //
        // The glob path (variable PRESENT, pattern matched) is NOT
        // receipt-proven: this harness has no multibranch job to exercise
        // it. It is implemented from Jenkins' documented ant-style glob.
        // Receipt: `when-scm-and-equals`.
        | WhenBranch pattern -> Some(WalkerRules.matchesGlob pattern (Map.tryFind "BRANCH_NAME" env))
        | WhenTag pattern -> Some(WalkerRules.matchesGlob pattern (Map.tryFind "TAG_NAME" env))

        // REVIEW FIX (Codex, PR #13): both operands were stored as bare text,
        // so `equals expected: 2, actual: '2'` compared equal and ran a stage
        // Jenkins skips — Jenkins compares the underlying objects, and an
        // Integer is not a String. The parser now keeps each operand's SOURCE
        // form (quotes included), so a quoted and an unquoted 2 differ.
        // This is a source-level approximation of Jenkins' object comparison,
        // not the real thing: `equals expected: 2, actual: 2.0` would still
        // be called unequal. Stated rather than implied.
        | WhenEquals(expected, actual) ->
            // Jenkins compares OBJECTS: Integer 2 is not String "2". An operand is
            // a quoted literal (a String), a bare number (an Integer), or an
            // expression like `env.X` — and an environment variable is a String.
            //
            // REGRESSION, caught by both reviewers: PR #13 round 5 established the
            // type distinction by keeping each operand's source form, and my
            // expression-resolution fix then stripped the quotes and made
            // `equals expected: 2, actual: '2'` equal again. The fix has to
            // preserve provenance, not just resolve.
            let classify (raw: string) =
                let t = raw.Trim()

                if t.Length >= 2 && (t.StartsWith "'" || t.StartsWith "\"") && t.EndsWith(string t[0]) then
                    // A String literal. Remove ONLY the one reconstructed delimiter:
                    // `Trim('\'', '"')` stripped every quote at both ends, so
                    // `equals expected: '"foo', actual: 'foo'` compared "foo" with
                    // foo as equal and ran a stage Jenkins skips.
                    Choice1Of3(t.Substring(1, t.Length - 2))
                elif t = "true" || t = "false" then
                    // a Boolean literal. REVIEW FIX (Codex, PR #16 round 4): bare
                    // `true` fell through to the expression branch and became a
                    // String, so `equals expected: true, actual: 'true'` compared
                    // equal and ran a stage Jenkins skips.
                    Choice3Of3 t
                elif t.Length > 0 && (Char.IsDigit t[0] || (t[0] = '-' && t.Length > 1)) then
                    // a numeric literal — a different Groovy type entirely
                    Choice2Of3 t
                else
                    // an expression; environment values are Strings
                    let bare = if t.StartsWith "env." then t.Substring 4 else t

                    match Map.tryFind bare env with
                    | Some v -> Choice1Of3 v
                    | None ->
                        // REVIEW FIX (Codex, PR #16 round 5): an unresolved
                        // expression became its own SOURCE TEXT, so
                        // `equals expected: 'env.MISSING', actual: env.MISSING`
                        // compared two identical strings and ran a stage Jenkins
                        // skips — Jenkins compares a String against null. Null is
                        // its own class: equal only to another null.
                        Choice2Of3 "\u0000null"

            match classify expected, classify actual with
            | Choice1Of3 a, Choice1Of3 b -> Some(a = b)
            | Choice2Of3 a, Choice2Of3 b -> Some(a.Trim() = b.Trim())
            | Choice3Of3 a, Choice3Of3 b -> Some(a = b)
            // Different Groovy types are never equal, which is the whole point.
            | _ -> Some false

        // Neutral: an evaluation-ORDER directive never decides whether a stage runs.
        | WhenEvaluationOption -> Some true

        // FG-048b. MEASURED on the pinned Jenkins: on a plain job — no SCM
        // changelog, no multibranch metadata, not a restart, triggered by a user —
        // every one of these is FALSE and its stage is skipped, with the build
        // succeeding. Before this they failed CLOSED, refusing up to 15 corpus
        // files outright, and a refusal is still a broken lift-and-shift.
        //
        // The BOUNDARY, stated rather than implied: only the context-absent case
        // is receipt-proven. A real multibranch build where CHANGE_ID exists, or a
        // build with an actual changelog, is NOT covered by that measurement —
        // those paths use the variable when present and are unproven until this
        // harness can produce such a build (FG-048c).
        // Receipt: `when-context-conditions`.
        | WhenBuildingTag -> Some(Map.containsKey "TAG_NAME" env)
        | WhenChangeRequest -> Some(Map.containsKey "CHANGE_ID" env)
        | WhenIsRestartedRun ->
            // True exactly when this attempt RESUMES an interrupted journal —
            // the FG-112 restart lane asserts a stage guarded by it runs only
            // on the resumed attempt.
            Some restarted
        | WhenTriggeredBy _ ->
            // MEASURED false on a user-started build, and it stays false until real
            // cause metadata exists.
            //
            // REVIEW FIX (both reviewers, PR #16): reading `BUILD_CAUSE` out of the
            // environment made the gate SPOOFABLE — a Jenkinsfile declaring
            // `environment { BUILD_CAUSE = 'TimerTrigger' }` would open a stage
            // Jenkins skips, and nothing else in the engine ever produces that
            // variable. A gate whose input the gated party controls is not a gate.
            // Receipt: `when-context-conditions`.
            Some false

        | WhenChangeset _
        | WhenChangelog _ ->
            // Both need an SCM changelog. There is none, and Jenkins itself warns
            // "empty changelog, probably because this is the first build" and
            // evaluates false.
            Some false

        | WhenNot inner -> evalWhen restarted envForWith stage inner |> Option.map not

        // REVIEW FIX (Codex, PR #13 round 2): an unevaluable operand used to
        // dominate, so `allOf { <false>, triggeredBy(...) }` failed the build
        // even though the false operand ALREADY decides that Jenkins skips.
        // Three-valued short-circuiting: a decisive known operand wins, and
        // only a genuinely undetermined result is reported unevaluable. This
        // shrinks the fail-closed surface without guessing anything.
        | WhenAllOf conds ->
            let results = conds |> List.map (evalWhen restarted envForWith stage)

            if results |> List.contains (Some false) then Some false
            elif results |> List.exists Option.isNone then None
            else Some true

        | WhenAnyOf conds ->
            let results = conds |> List.map (evalWhen restarted envForWith stage)

            if results |> List.contains (Some true) then Some true
            elif results |> List.exists Option.isNone then None
            else Some false

        | WhenExpression source ->
            // ADR 0002: expressions stay as source text and the bounded
            // interpreter decides what they mean. The sandbox's step
            // vocabulary is EMPTY here — a `when` predicate has no
            // business invoking build steps.
            match Fogell.Groovy.Parser.Parser.parse source with
            | Result.Error _ -> None
            | Result.Ok script ->
                // REVIEW FIX (Codex, PR #13): only bare names were bound, so
                // the NORMAL Jenkins predicate `env.FOO == 'bar'` resolved
                // `env` to null, compared null to a string, and SKIPPED a
                // stage Jenkins runs. Both spellings are bound now.
                let asValues = env |> Map.map (fun _ v -> VStr v)

                let genv =
                    Env.ofValues (asValues |> Map.add "env" (VMap asValues))

                let outcome = Interpreter.run Budget.defaults Set.empty genv script

                match outcome.Fault, outcome.Returned with
                | Some _, _ -> None
                | None, Some v -> Some(Value.isTruthy v)
                // A predicate that produced no value cannot be read as
                // false; that is the vacuous-pass shape.
                | None, None -> None

        | WhenUnmodelled _ -> None
