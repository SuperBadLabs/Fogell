namespace Fogell.Differential

open System
open Fogell.Ir
open Fogell.Groovy.Interpreter

/// FG-100. THE string model: one place that decides what a Groovy string argument MEANS.
///
/// 52 review findings were spent re-deriving these rules per consumer — `environment`,
/// `withEnv`, `input`, `echo`, `equals` and `when { expression }` each learned them
/// separately, and four rounds of PR #17 went on nothing else. The rules are not hard; the
/// cost came from every new consumer meeting them for the first time.
///
/// A Groovy string argument is exactly one of three things, and confusing them is what
/// every one of those findings was:
///
///   Literal        single-quoted. Groovy never touches it. `input 'Deploy ${X}?'` shows
///                  the braces.
///   Interpolating  double-quoted, triple-double or slashy. Groovy expands `${...}` and
///                  `$name` BEFORE the step is called — for EVERY step, `sh` included.
///                  What survives to a shell is an ESCAPED `\$` and anything single-quoted,
///                  which Groovy never touched. (My first draft of this ticket claimed the
///                  opposite for `sh`; `tests/Fogell.Groovy.Tests` already disproved it.)
///   Expression     unquoted. `input message: env.TARGET` is CODE, evaluated by Groovy, not
///                  text to scan for placeholders.
///
/// Receipts: `withenv-scoping`, `input-positional-literal`, `input-gstring-expression`,
/// `input-escaped-dollar`, `input-slashy-literal`, `input-division-in-gstring`,
/// `input-expression-args`, `echo-credential-masking`, `env-escaped-and-dotted`.
type ArgKind =
    | Literal
    | Interpolating
    | Expression

module GString =

    /// The NUL sentinel the lexer substitutes for an escaped dollar, so interpolation can
    /// tell `\$X` (literal) from `$X` (expand) after unescaping has already happened.
    [<Literal>]
    let EscapedDollar = "\u0000"

    /// A bare `${NAME}` whose name is bound NOWHERE. Groovy does not render these as
    /// empty text — it throws, and the build FAILS.
    /// MEASURED (receipt `gstring-unresolved-property`, Jenkins 2.568.1):
    ///   groovy.lang.MissingPropertyException: No such property: MISSING_BARE_VAR
    ///   for class: groovy.lang.Binding — result FAILURE.
    /// The `env.X` path is DIFFERENT: `${env.MISSING}` renders the text `null` and the
    /// build passes (receipt `gstring-env-missing-null`). One is a Groovy property
    /// lookup on the script binding; the other is a map read that comes back null and
    /// is then stringified. Erasing either to "" silently runs a command the author
    /// never wrote — `deploy ${TARGET}` becoming `deploy ` is how a wrong environment
    /// gets deployed to.
    exception MissingProperty of name: string

    let internal interpolateCore (strict: bool) (known: Map<string, string>) (value: string) =
        // REVIEW FIXES (Codex, PR #14 rounds 7 and 8):
        //  * `"\$BUILD_NUMBER"` is the LITERAL text `$BUILD_NUMBER` in Groovy.
        //    The parser keeps the backslash so this pass can honour it and then
        //    remove it, instead of expanding what Jenkins leaves alone.
        //  * `"$env.BUILD_NUMBER"` and `"${env.BUILD_NUMBER}"` are the ordinary
        //    Jenkins spellings. The old pattern matched only a bare identifier,
        //    so `$env` resolved to nothing and `.BUILD_NUMBER` was left behind,
        //    while the braced dotted form was not matched at all.
        let resolveName (name: string) =
            // `env.X` is the same variable as a bare `X`. The bracketed
            // `env['X']` form is deliberately NOT handled: Jenkins' sandbox
            // rejects it outright (measured — see the pattern below).
            let isEnvPath = name.StartsWith "env."
            let bare = if isEnvPath then name.Substring 4 else name

            match Map.tryFind bare known with
            | Some v -> v
            | None ->
                match Environment.GetEnvironmentVariable bare with
                | null when strict && isEnvPath ->
                    // MEASURED (`gstring-env-missing-null`): `${env.MISSING}` is a
                    // null map read, stringified — the build sees the text `null`.
                    "null"
                | null when strict ->
                    // MEASURED (`gstring-unresolved-property`): a bare unknown name
                    // is a failed Groovy property lookup, and the build FAILS.
                    raise (MissingProperty name)
                | null -> ""
                | v -> v

        // `${...}` may hold a real Groovy EXPRESSION, not just a variable path:
        // `input message: "Approve build ${1 + 1}?"` shows "Approve build 2?" on
        // Jenkins. The bounded interpreter already in this project evaluates it,
        // with an EMPTY step vocabulary so a prompt cannot invoke build steps.
        let evalExpression (source: string) =
            match Fogell.Groovy.Parser.Parser.parse source with
            | Result.Error _ -> None
            | Result.Ok script ->
                // The INHERITED process environment participates, exactly as the
                // simple-name fast path's fallback does. MEASURED (receipt `gstring-inherited-env-resolves`, 2.568.1):
                // declarative resolves a bare `${PATH}` from the agent environment and
                // SUCCEEDS — the MissingPropertyException fires only for a name bound
                // NOWHERE. Seeding only `known` here made `${PATH.toUpperCase()}` fail
                // while `${PATH}` resolved: two rules for one name, split by whether a
                // method call follows. `known` wins over the OS on collision.
                let allVars =
                    Seq.append
                        (Environment.GetEnvironmentVariables()
                         |> Seq.cast<Collections.DictionaryEntry>
                         |> Seq.map (fun e -> string e.Key, string e.Value))
                        (Map.toSeq known)
                    |> Map.ofSeq

                let asValues = allVars |> Map.map (fun _ v -> VStr v)
                let bindings = asValues |> Map.add "env" (VMap asValues)

                // STRICT propagates into expressions, not just bare names. The
                // interpreter reads an unknown variable as null, so
                // `${MISSING + '-suffix'}` evaluated to `null-suffix` and RAN —
                // while Groovy's property lookup fails before the `+` is ever
                // reached and Jenkins fails the build (same measurement as the
                // bare form, receipt `gstring-unresolved-property`). The AST is
                // scanned for names bound nowhere and the first one raises.
                if strict then
                    let free = Fogell.Groovy.Ast.freeVars (bindings |> Map.toSeq |> Seq.map fst |> Set.ofSeq) script

                    match Set.toList free |> List.sort with
                    | name :: _ -> raise (MissingProperty name)
                    | [] -> ()

                let outcome =
                    Interpreter.run Budget.defaults Set.empty { Vars = bindings; Funcs = Map.empty } script

                match outcome.Fault, outcome.Returned with
                | None, Some v -> Some(Value.toDisplay v)
                | _ -> None

        // A GString placeholder is scanned, not regex-matched. `[^}]*` stops at
        // the first `}` even when it belongs to a nested expression or a quoted
        // string, so `"Result: ${'}'}"` truncated to `'` and was emitted verbatim.
        // Groovy's boundary is the BALANCED brace, with quotes respected.
        /// Does a `/` at this position OPEN a slashy string, or divide?
        /// Groovy decides by what precedes it: a value (identifier, digit, closing
        /// bracket) means division; anything else opens a literal.
        let slashOpensLiteral (text: string) (idx: int) =
            let mutable j = idx - 1

            while j >= 0 && (text[j] = ' ' || text[j] = '\t') do
                j <- j - 1

            if j < 0 then
                true
            else
                let p = text[j]
                not (Char.IsLetterOrDigit p || p = '_' || p = ')' || p = ']' || p = '}')

        let findClose (text: string) (openIdx: int) =
            let mutable i = openIdx + 2 // past "${"
            let mutable depth = 1
            let mutable quote = '\000'
            let mutable closeAt = -1

            while closeAt < 0 && i < text.Length do
                let c = text[i]

                if quote <> '\000' then
                    if c = '\\' then i <- i + 1
                    elif c = quote then quote <- '\000'
                elif c = '\'' || c = '"' then
                    quote <- c
                elif c = '/' && slashOpensLiteral text i then
                    // `/` opens a SLASHY string — `${/}/}` holds a literal whose
                    // `}` is content — but it is also DIVISION. Groovy disambiguates
                    // by what precedes it: after a value (identifier, number,
                    // closing bracket) a slash is an operator, otherwise it opens a
                    // literal. Treating every slash as a quote broke `${a / b}`.
                    quote <- c
                elif c = '{' then
                    depth <- depth + 1
                elif c = '}' then
                    depth <- depth - 1
                    if depth = 0 then closeAt <- i

                i <- i + 1

            closeAt

        let expanded =
            let sb = Text.StringBuilder()
            let mutable i = 0

            while i < value.Length do
                if i + 1 < value.Length && value[i] = '$' && value[i + 1] = '{' then
                    match findClose value i with
                    | -1 ->
                        sb.Append value[i] |> ignore
                        i <- i + 1
                    | closeAt ->
                        let inner = value.Substring(i + 2, closeAt - i - 2)

                        let rendered =
                            // `true`, `false` and `null` LOOK like identifiers to the fast
                            // path, but they are Groovy literals — `${true}` is the
                            // boolean, not an environment variable named "true". Resolving
                            // them as names returned "" and broke every unquoted boolean
                            // argument once named arguments started rendering:
                            // `allowEmptyArchive: true` reached the executor as "" and an
                            // empty archive Jenkins permits was failed. Literals go to the
                            // interpreter, which evaluates them as themselves.
                            let isGroovyLiteral = inner = "true" || inner = "false" || inner = "null"

                            if
                                not isGroovyLiteral
                                && Text.RegularExpressions.Regex.IsMatch(inner, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")
                            then
                                resolveName inner
                            else
                                match evalExpression inner with
                                | Some v -> v
                                | None -> value.Substring(i, closeAt - i + 1)

                        sb.Append rendered |> ignore
                        i <- closeAt + 1
                elif value[i] = '$' then
                    // Bare `$name` stays identifier-only, as Groovy does.
                    let m =
                        Text.RegularExpressions.Regex.Match(
                            value.Substring i, @"^\$([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")

                    if m.Success then
                        sb.Append(resolveName m.Groups[1].Value) |> ignore
                        i <- i + m.Length
                    else
                        sb.Append value[i] |> ignore
                        i <- i + 1
                else
                    sb.Append value[i] |> ignore
                    i <- i + 1

            sb.ToString()

        // Restore escaped dollars as literal text, after expansion so they
        // cannot themselves be expanded.
        expanded.Replace("\u0000", "$")

    /// Lenient: an unknown name erases to "". Kept for consumers whose Jenkins-side
    /// failure semantics have not been measured yet (`environment`, `when`-equals);
    /// step ARGUMENTS go through [render], which is strict.
    let interpolate (known: Map<string, string>) (value: string) = interpolateCore false known value

    /// What KIND is this argument? `key` is a named argument's name, or `#0`, `#1`… for a
    /// positional. Every consumer asked this question differently before FG-100; there is
    /// now one answer.
    let kindOf (step: Step) (key: string) : ArgKind =
        let isPositional = key.StartsWith "#"

        let literal =
            if isPositional then
                match Int32.TryParse(key.Substring 1) with
                | true, i -> step.LiteralPositionalArgs.Contains i
                | _ -> false
            else
                step.LiteralNamedArgs.Contains key

        if literal then Literal
        elif step.ExpressionArgs.Contains key then Expression
        else Interpolating

    /// The text to interpolate FROM: the escape-preserving form when one was recorded, the
    /// plain value otherwise. `Named`/`Positional` deliberately hold the plain value so a
    /// sentinel can never reach a consumer that forwards text verbatim — that once put a
    /// NUL into a shell command.
    let sourceOf (step: Step) (key: string) (fallback: string) : string =
        step.InterpolationSource
        |> List.tryPick (fun (k, v) -> if k = key then Some v else None)
        |> Option.defaultValue fallback

    /// Render one argument according to its kind. THE entry point: a consumer that calls
    /// this cannot get the literal/GString/expression distinction wrong, because it no
    /// longer makes the distinction.
    let render (env: Map<string, string>) (step: Step) (key: string) (raw: string) : string =
        // STRICT: this is the step-argument path, where erasing an unknown name to ""
        // runs a command the author never wrote. Raises [MissingProperty]; the walker
        // fails the build with Jenkins' own diagnosis (measured, see the exception).
        match kindOf step key with
        | Literal -> raw
        | Expression -> interpolateCore true env ("${" + raw + "}")
        | Interpolating -> interpolateCore true env (sourceOf step key raw)
