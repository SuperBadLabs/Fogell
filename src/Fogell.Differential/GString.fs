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

    /// A strict render met Groovy this interpreter does not model (an unmodelled
    /// method, an unsupported construct). Refusing BY NAME is the fail-closed
    /// choice: the alternative — stringifying an invented null — ran
    /// `deploy null` with the build green.
    exception UnsupportedExpression of detail: string

    let private renderValue value =
        match Value.tryToDisplay value with
        | Value.Text rendered -> rendered
        | Value.DisplayCycleDetected ->
            raise (UnsupportedExpression "expression threw: StackOverflowError displaying cyclic collection")

    let internal interpolateCore
        (strict: bool)
        (known: Map<string, string>)
        (carried0: Map<string, Value>)
        (publish: Map<string, Value> -> unit)
        (advise: string * Value -> unit)
        (value: string)
        : string * Map<string, Value> =
        // REVIEW FIXES (Codex, PR #14 rounds 7 and 8):
        //  * `"\$BUILD_NUMBER"` is the LITERAL text `$BUILD_NUMBER` in Groovy.
        //    The parser keeps the backslash so this pass can honour it and then
        //    remove it, instead of expanding what Jenkins leaves alone.
        //  * `"$env.BUILD_NUMBER"` and `"${env.BUILD_NUMBER}"` are the ordinary
        //    Jenkins spellings. The old pattern matched only a bare identifier,
        //    so `$env` resolved to nothing and `.BUILD_NUMBER` was left behind,
        //    while the braced dotted form was not matched at all.
        // Bindings CREATED by one placeholder and read by a later one, in the SAME
        // GString. Placeholders share Groovy's script Binding — measured (receipt
        // `gstring-shared-binding`): echo "${x = 'ok'; x}-${x}" prints `ok-ok` —
        // so each placeholder must see what its predecessors assigned. Values, not
        // display strings: `${n = 2; n}+${n * 3}` must reach the second placeholder
        // as the NUMBER 2, or arithmetic silently becomes concatenation.
        let mutable carried: Map<string, Value> = carried0

        let resolveName (name: string) =
            // `env.X` is the same variable as a bare `X`. The bracketed
            // `env['X']` form is deliberately NOT handled: Jenkins' sandbox
            // rejects it outright (measured — see the pattern below).
            let isEnvPath = name.StartsWith "env."
            let bare = if isEnvPath then name.Substring 4 else name

            match (if isEnvPath then None else Map.tryFind bare carried) with
            | Some v -> renderValue v
            | None ->

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
            | Result.Error why ->
                if strict then
                    // Valid Groovy this parser cannot parse must REFUSE, not fall back
                    // to the raw placeholder: `"${'out.txt' as String}"` searched for a
                    // file literally named ${...} and stayed green while Jenkins
                    // archives out.txt. A modelling limit, named — never silent text.
                    raise (UnsupportedExpression $"unparsable expression '{source}': {why}")

                None
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

                // Carried script-binding values overlay the environment seeds — a
                // reassignment wins, exactly as Groovy's Binding does — but they do
                // NOT enter the `env` map: `${x = 'ok'}` does not create env.x.
                let seeded = Map.fold (fun m k v -> Map.add k v m) asValues carried
                let bindings = seeded |> Map.add "env" (VMap(ref asValues))

                // STRICT propagates into expressions, not just bare names — and it is
                // enforced at READ time, in the interpreter, not by a static scan.
                // This landed in three review steps, each one wrong until measured:
                // first the interpreter's null-for-unknown ran `${MISSING + '-sfx'}`
                // as `null-sfx` where Groovy fails; then a static free-variable scan
                // raised on `${true ? 'ok' : MISSING}` where Groovy — lazy — never
                // reads MISSING; then the intersection refinement missed the arm
                // actually TAKEN (`${true ? MISSING : 'ok'}`). No static answer
                // exists: laziness makes the read set a runtime fact, so the check
                // lives at the read (Interpreter.runStrictVars) and is exact by
                // construction. Receipts `gstring-unresolved-property`,
                // `gstring-unresolved-in-expression`.
                let runInterpreter = if strict then Interpreter.runStrictVars else Interpreter.run

                let outcome =
                    runInterpreter Budget.defaults Set.empty (Env.ofValues bindings) script

                // What SURVIVES a placeholder, and what the user is TOLD about it —
                // shared by the success and fault paths, because Groovy performs an
                // assignment the moment it executes: `${x = 'kept'; MISSING}` fails
                // the argument, and Jenkins' post block still reads x. Absorbing only
                // on success rolled those assignments back.
                //
                // A NEW key persists only when the interpreter reports it as a
                // BINDING assignment — a `def` is a local of its own placeholder, and
                // carrying it would let `${def x = 1; x}` leak x to the next
                // placeholder where Groovy would not. (Closure-locality of `def` is
                // standard Groovy, asserted from the language rather than measured.)
                // Changed EXISTING keys always persist: reassignment wins.
                //
                // Each fresh binding is announced through `advise` — Jenkins prints
                // its def-keyword advisory for exactly this event, and Fogell emits
                // the same line so the two logs COMPARE instead of being suppressed.
                let absorb (outcome: Outcome) =
                    carried <-
                        // FG-179: `Env.Vars` holds ref cells now, and this consumer wants a
                        // SNAPSHOT — the values as they stood when the script ended, not a
                        // live view into scopes that are already gone.
                        Env.snapshot outcome.Env
                        |> Map.fold
                            (fun acc k v ->
                                // outcome.Env.Vars IS the script Binding now — locals
                                // died with their scopes inside the interpreter, so a
                                // changed or new key here is a real binding update
                                if k = "env" then acc
                                elif (match Map.tryFind k bindings with
                                      // FG-191: cycle-aware — a cyclic value
                                      // reads as CHANGED (re-render is the safe
                                      // side), never as a process-killing walk
                                      | Some old ->
                                          match Value.tryEq old v with
                                          | Value.Answer same -> not same
                                          | Value.CycleDetected -> true
                                          | Value.Unmodelled -> true
                                      | None -> true) then
                                    Map.add k v acc
                                else
                                    acc)
                            carried

                    for n, createdAs in outcome.NewBindings do
                        advise (n, createdAs)

                    publish carried

                match outcome.Fault, outcome.Returned with
                | None, Some v ->
                    absorb outcome
                    Some(renderValue v)
                | Some(UnknownProperty name), _ ->
                    absorb outcome
                    raise (MissingProperty name)
                | Some(NullReceiverAssignment target), _ ->
                    // Assignment through null is a real runtime exception, not
                    // lax placeholder lookup. Never let the non-strict render
                    // fallback turn it back into an unevaluated successful value.
                    absorb outcome
                    raise (UnsupportedExpression $"expression threw: NullPointerException assigning through null {target} receiver")
                | Some(Fault.Unsupported what), _ when strict ->
                    absorb outcome
                    raise (UnsupportedExpression what)
                | Some fault, _ when strict ->
                    // EVERY fault fails a strict render. `${1 / 0}` THROWS in Groovy
                    // and Jenkins fails the build; falling through to None emitted the
                    // unevaluated placeholder and SUCCEEDED — a swallowed exception
                    // wearing an interpolation's clothes. Same for a sandbox denial
                    // or an exhausted budget: named refusal, never silent raw text.
                    absorb outcome

                    let detail =
                        match fault with
                        | Thrown v -> $"expression threw: {renderValue v}"
                        | BudgetExhausted what -> $"evaluation budget exhausted: {what}"
                        | Denied d -> $"sandbox denied: {d}"
                        | f -> string f

                    raise (UnsupportedExpression detail)
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
                elif
                    (c = '\'' || c = '"')
                    && i + 2 < text.Length
                    && text[i + 1] = c
                    && text[i + 2] = c
                then
                    // A TRIPLE-quoted Groovy literal: an interior quote is content, so
                    // toggling per character left the scanner outside its string at
                    // `it's` and a content brace terminated the placeholder early.
                    let close = text.IndexOf(System.String(c, 3), i + 3)
                    i <- if close < 0 then text.Length else close + 2
                elif c = '\'' || c = '"' then
                    quote <- c
                elif c = '/' && i + 1 < text.Length && text[i + 1] = '*' then
                    // A Groovy BLOCK COMMENT inside the placeholder. Its braces are
                    // text: `${1 /* } */ + 1}` closes at the FINAL brace and Jenkins
                    // evaluates the whole expression to 2 (receipt
                    // `gstring-comment-in-placeholder`). Before this branch, the
                    // comment's `}` closed the placeholder, `1 /*` went to the parser,
                    // and the malformed text was passed through to the shell. Checked
                    // before the slashy rule: the lexer gives comments precedence.
                    let close = text.IndexOf("*/", i + 2)
                    i <- if close < 0 then text.Length else close + 1
                elif c = '/' && i + 1 < text.Length && text[i + 1] = '/' then
                    // A line comment runs to the END OF THE LINE — not the end of the
                    // text. A triple-quoted GString's placeholder can span lines, and
                    // `${1 // note\n + 1}` resumes the expression after the newline;
                    // jumping to text.Length left the placeholder unclosed and forwarded
                    // it raw. Only when no newline follows does the comment end the text.
                    let nl = text.IndexOf('\n', i)
                    i <- if nl < 0 then text.Length else nl
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
                        if strict then
                            // an unterminated `${` is invalid Groovy — Jenkins refuses
                            // the file before any step runs; copying it verbatim let a
                            // build succeed that Jenkins never builds
                            raise (UnsupportedExpression $"unterminated interpolation placeholder in '{value}'")

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

                            // The fast path is a NAME LOOKUP: a bare identifier, or
                            // `env.NAME`. A longer chain is an expression — flattening
                            // `env.TARGET.length` into one lookup asked the environment
                            // for a variable literally named "TARGET.length", rendered
                            // `null`, and ran a step the sandbox REJECTS (measured:
                            // "No such field found: field java.lang.String length",
                            // receipt `gstring-string-property-fails`).
                            if
                                not isGroovyLiteral
                                && Text.RegularExpressions.Regex.IsMatch(inner, @"^(env\.)?[A-Za-z_][A-Za-z0-9_]*$")
                            then
                                resolveName inner
                            else
                                match evalExpression inner with
                                | Some v -> v
                                | None -> value.Substring(i, closeAt - i + 1)

                        sb.Append rendered |> ignore
                        i <- closeAt + 1
                elif value[i] = '$' then
                    // Bare `$a.b.c` is a PROPERTY CHAIN in Groovy (no parens allowed in
                    // this form). A simple name or `env.NAME` is a lookup; anything
                    // longer is an expression — flattening it into one lookup asked the
                    // environment for "TARGET.length" and rendered `null` where the
                    // sandbox REJECTS the property (the braced form's measurement,
                    // receipt `gstring-string-property-fails`, and the same evaluator
                    // decides both spellings).
                    let m =
                        Text.RegularExpressions.Regex.Match(
                            value.Substring i, @"^\$([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")

                    if m.Success then
                        let chain = m.Groups[1].Value

                        let rendered =
                            if Text.RegularExpressions.Regex.IsMatch(chain, @"^(env\.)?[A-Za-z_][A-Za-z0-9_]*$") then
                                resolveName chain
                            else
                                match evalExpression chain with
                                | Some v -> v
                                | None -> "$" + chain

                        sb.Append rendered |> ignore
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
        expanded.Replace("\u0000", "$"), carried

    /// Lenient: an unknown name erases to "". Kept for consumers whose Jenkins-side
    /// failure semantics have not been measured yet (`environment`, `when`-equals);
    /// step ARGUMENTS go through [render], which is strict.
    let interpolate (known: Map<string, string>) (value: string) =
        interpolateCore false known Map.empty ignore ignore value |> fst

    /// The Java-side type name Jenkins' def-keyword advisory prints.
    let javaTypeName (v: Value) =
        match v with
        | VStr _ -> "String"
        | VInt _ -> "Integer"
        | VInteger _ -> "Integer"
        | VArithmeticInteger _ -> "Integer"
        | VBool _ -> "Boolean"
        | VList _ -> "ArrayList"
        | VRange _ -> "IntRange"
        | VMap _ -> "LinkedHashMap"
        | VNull -> "null"
        | _ -> "Object" 

    /// Groovy's SCRIPT BINDING, at run scope. An assignment made by a GString
    /// placeholder outlives its render call: `echo "${x = 'ok'; x}"` then
    /// `echo "$x"` prints `ok` twice on Jenkins, because both read one Binding
    /// (receipt `gstring-binding-across-steps`). One instance per BUILD; parallel
    /// branches share it, exactly as Jenkins' one Binding is shared, and access is
    /// serialised here so a concurrent render cannot lose an update.
    type ScriptBinding() =
        let gate = obj ()
        let mutable vars: Map<string, Value> = Map.empty

        /// One lock across the whole read-evaluate-write transaction. A separate
        /// Read/Merge pair let two parallel branches read the same snapshot and
        /// then each publish its own — last writer erasing the other branch's
        /// assignment, so a post-parallel read could raise MissingProperty on a
        /// variable that was genuinely assigned.
        member _.Transact(f: Map<string, Value> -> (Map<string, Value> -> unit) -> 'a * Map<string, Value>) : 'a =
            lock gate (fun () ->
                // `publish` commits progressively INSIDE the lock, so a fault after an
                // earlier placeholder's assignment keeps that assignment — Groovy has
                // already performed it — while two branches still cannot interleave.
                let result, updated = f vars (fun m -> vars <- m)
                vars <- updated
                result)

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
    /// Render with a run-scoped script binding: assignments made by this argument's
    /// placeholders become visible to every LATER rendered argument in the build.
    let renderInto
        (binding: ScriptBinding)
        (advise: string * Value -> unit)
        (env: Map<string, string>)
        (step: Step)
        (key: string)
        (raw: string)
        : string =
        let go strict text =
            binding.Transact(fun current publish -> interpolateCore strict env current publish advise text)

        match kindOf step key with
        | Literal -> raw
        | Expression -> go true ("${" + raw + "}")
        | Interpolating -> go true (sourceOf step key raw)

    let renderWith (binding: ScriptBinding) (env: Map<string, string>) (step: Step) (key: string) (raw: string) : string =
        renderInto binding ignore env step key raw

    /// Interpolate RAW text against the run-scoped binding — for consumers whose
    /// argument is not a step argument (withEnv's `NAME=value` entries). An
    /// assignment made by a placeholder here enters the same Binding every other
    /// render reads; the stateless [interpolate] silently discarded it.
    let interpolateInto
        (binding: ScriptBinding)
        (advise: string * Value -> unit)
        (known: Map<string, string>)
        (value: string)
        : string =
        // STRICT: a withEnv entry is a GString evaluated BEFORE the step is invoked,
        // so `withEnv(["T=$MISSING"])` fails on Jenkins exactly as a step argument
        // does — erasing the name to "" would run a body Jenkins refuses.
        binding.Transact(fun current publish -> interpolateCore true known current publish advise value)

    /// Stateless render — each call gets a fresh, discarded binding. For contexts
    /// where no build-scoped Binding exists (and for the acceptance matrix's
    /// stateless rows).
    let render (env: Map<string, string>) (step: Step) (key: string) (raw: string) : string =
        // STRICT: this is the step-argument path, where erasing an unknown name to ""
        // runs a command the author never wrote. Raises [MissingProperty]; the walker
        // fails the build with Jenkins' own diagnosis (measured, see the exception).
        match kindOf step key with
        | Literal -> raw
        | Expression -> interpolateCore true env Map.empty ignore ignore ("${" + raw + "}") |> fst
        | Interpolating -> interpolateCore true env Map.empty ignore ignore (sourceOf step key raw) |> fst
