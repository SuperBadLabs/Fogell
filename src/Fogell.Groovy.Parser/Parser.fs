module Fogell.Groovy.Parser.Parser

open FParsec
open Fogell.Groovy
open Fogell.Admission

/// Scripted-Groovy parser. Grammar coverage is driven by measured corpus
/// demand, and every construct here was confirmed necessary by a minimal
/// reproduction — never by an FParsec error position, which reports where the
/// longest parse stopped rather than the cause.
///
/// Constructs proven necessary while measuring Forge, and included from the
/// start here: shebang, `@Library`, `import`, trailing commas, slashy strings,
/// `=~`/`==~`, typed closure params, `final`, `x++`, C-style `for`, ranges,
/// `switch`, `instanceof`, spread-dot, multi-assign.

type private TriviaState =
    { BreakInTrivia: bool
      TriviaEndIndex: int64
      GroupDepth: int
      MaxScalarBytes: int
      ScalarRefusal: AdmissionError option }

type private P<'a> = Parser<'a, TriviaState>

let private initialState (limits: Limits) =
    { BreakInTrivia = false
      TriviaEndIndex = -1L
      GroupDepth = 0
      MaxScalarBytes = limits.MaxScalarBytes
      ScalarRefusal = None }

let private keepFirstScalarRefusal state refusal =
    if state.ScalarRefusal.IsNone then
        { state with ScalarRefusal = Some refusal }
    else
        state

/// FG-190/192. Consume trivia FORWARD so a non-nesting block comment is read
/// with the same boundaries as Groovy. Record a break only when trivia was
/// actually consumed; zero-width `ws` must not overwrite the last token's
/// trailing-trivia fact. `TriviaEndIndex` makes that fact valid at exactly one
/// stream position, so a later token cannot observe stale state.
let private ws: P<unit> =
    fun stream ->
        let start = stream.Index
        let mutable sawBreak = false
        let mutable scanning = true
        let mutable unterminatedBlock = false

        while scanning && not stream.IsEndOfStream do
            match stream.Peek() with
            | ' '
            | '\t' -> stream.Skip()
            | '\r'
            | '\n' ->
                sawBreak <- true
                // FParsec tracks line/column separately from the raw index.
                // SkipNewline consumes CRLF as one logical newline and keeps
                // AdmissionError.Position tied to the physical source.
                stream.SkipNewline() |> ignore
            | '/' when stream.Peek(1) = '/' ->
                stream.Skip(2)

                while
                    not stream.IsEndOfStream
                    && stream.Peek() <> '\r'
                    && stream.Peek() <> '\n'
                    do
                    stream.Skip()
            | '/' when stream.Peek(1) = '*' ->
                stream.Skip(2)
                let mutable closed = false

                while not closed && not stream.IsEndOfStream do
                    if stream.Peek() = '*' && stream.Peek(1) = '/' then
                        stream.Skip(2)
                        closed <- true
                    else
                        if stream.Peek() = '\r' || stream.Peek() = '\n' then
                            sawBreak <- true
                            stream.SkipNewline() |> ignore
                        else
                            stream.Skip()

                if not closed then
                    unterminatedBlock <- true
                    scanning <- false
            | _ -> scanning <- false

        if unterminatedBlock then
            Reply(Error, expected "a terminated block comment")
        else
            if stream.Index <> start then
                stream.UserState <-
                    { stream.UserState with
                        BreakInTrivia = sawBreak
                        TriviaEndIndex = stream.Index }

            Reply(())

let private lexeme (p: P<'a>) = p .>> ws
let private symbol s : P<unit> = lexeme (skipString s)

let private liveBreak (stream: CharStream<TriviaState>) =
    stream.UserState.BreakInTrivia
    && stream.UserState.TriviaEndIndex = stream.Index

/// Strict seams end at a break even inside parentheses: command-form arguments,
/// typed-declaration lookahead, and `return` values are statement grammar, not
/// postfix continuation grammar.
let private notAfterLineBreak: P<unit> =
    fun stream ->
        if liveBreak stream then
            Reply(Error, expected "no line break")
        else
            Reply(())

/// A postfix index may cross recorded trivia only while an expression-bearing
/// delimiter owns the parse. Top-level statement separation remains strict.
let private indexMayContinue: P<unit> =
    fun stream ->
        if liveBreak stream && stream.UserState.GroupDepth = 0 then
            Reply(Error, expected "no line break before '['")
        else
            Reply(())

let private groupDepth: P<int> = fun stream -> Reply(stream.UserState.GroupDepth)

let private setGroupDepth depth: P<unit> =
    fun stream ->
        stream.UserState <- { stream.UserState with GroupDepth = depth }
        Reply(())

let private withExpressionGroup (p: P<'a>) : P<'a> =
    groupDepth >>= fun outer -> setGroupDepth (outer + 1) >>. p .>> setGroupDepth outer

let private withStatementBody (p: P<'a>) : P<'a> =
    groupDepth >>= fun outer -> setGroupDepth 0 >>. p .>> setGroupDepth outer

let private expressionGroup opening closing (p: P<'a>) : P<'a> =
    symbol opening >>. withExpressionGroup p .>> symbol closing

let private statementBody opening closing (p: P<'a>) : P<'a> =
    symbol opening >>. withStatementBody p .>> symbol closing

let private isIdentStart c = isLetter c || c = '_'
let private isIdentCont c = isLetter c || isDigit c || c = '_'
let private rawIdent: P<string> = many1Satisfy2 isIdentStart isIdentCont
let private identifier: P<string> = lexeme rawIdent

let private keyword (s: string) : P<unit> =
    lexeme (attempt (skipString s .>> notFollowedBy (satisfy isIdentCont)))

let private reserved =
    set [ "def"; "if"; "else"; "for"; "while"; "return"; "break"; "continue"; "throw"; "try"
          "catch"; "finally"; "new"; "true"; "false"; "null"; "in"; "switch"; "case"; "default"
          "instanceof"; "final"; "import"; "as" ]

let private plainIdent: P<string> =
    lexeme (attempt (rawIdent >>= fun n -> if reserved.Contains n then fail "reserved" else preturn n))

// --- literals --------------------------------------------------------------

let private decodeEscape =
    function
    | 'n' -> '\n'
    | 't' -> '\t'
    | 'r' -> '\r'
    | 'b' -> '\b'
    | 'f' -> '\f'
    | c -> c

/// FG-124. Scripted Groovy uses Java's numeric escape grammar: a Unicode
/// escape has one-or-more `u` characters and exactly four hex digits; an octal
/// escape has one or two digits, or three only when its first digit is 0-3.
/// Keeping this parser below the leading backslash lets both ordinary string
/// values and constant named-argument keys share the exact digit boundaries.
let private numericEscape: P<char> =
    attempt (
        (skipMany1 (skipChar 'u')
         >>. manyMinMaxSatisfy 4 4 (fun c ->
             (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
         |>> fun hex -> char (System.Convert.ToInt32(hex, 16)))
        <|> attempt (
                manyMinMaxSatisfy 1 1 (fun c -> c >= '0' && c <= '3')
                .>>. manyMinMaxSatisfy 2 2 (fun c -> c >= '0' && c <= '7')
                |>> fun (hi, lo) -> char (System.Convert.ToInt32(hi + lo, 8)))
        <|> (manyMinMaxSatisfy 1 2 (fun c -> c >= '0' && c <= '7')
             |>> fun digits -> char (System.Convert.ToInt32(digits, 8))))

/// A backslash followed by a physical line ending is a continuation and adds
/// no character to the decoded value. Jenkins accepts LF, CRLF and bare CR;
/// the longest spelling must be tried first so CRLF is consumed as one unit.
let private escaped: P<string> =
    skipChar '\\'
    >>. choice
            [ (skipChar '\r' >>. opt (skipChar '\n')) >>% ""
              skipChar '\n' >>% ""
              numericEscape |>> string
              anyChar |>> (decodeEscape >> string) ]

/// The narrow named-key form does not claim Groovy's physical line-continuation
/// escapes. Keep the ordinary escape decoder shared, but fail closed rather
/// than turning an unsupported backslash-break into key text.
let private escapedWithoutPhysicalBreak: P<string> =
    skipChar '\\'
    >>. ((numericEscape |>> string)
         <|> (satisfy (fun c -> c <> '\n' && c <> '\r') |>> (decodeEscape >> string)))

let private singleQuoted: P<Expr> =
    between
        (skipString "'")
        (skipString "'")
        (manyStrings (escaped <|> (satisfy (fun c -> c <> '\'' && c <> '\r' && c <> '\n') |>> string)))
    |>> EStr

let private tripleSingle: P<Expr> =
    between
        (skipString "'''")
        (skipString "'''")
        (manyTill (escaped <|> (anyChar |>> string)) (lookAhead (skipString "'''"))
         |>> String.concat "")
    |>> EStr

/// Slashy string `/regex/`. Only reachable where a primary expression is
/// expected, so `a / b` division is unaffected.
///
/// FG-141. `lexeme`-wrapped like every other literal: without the trailing
/// trivia skip, ANY operator after a slashy failed to chain — `/Deploy; / +
/// env.TARGET` stopped at the `+` with the slashy consumed and the rest
/// unreachable. It hid because the existing receipts put `)` directly against
/// the closing delimiter; the approval lane's Z2 rewrite met the space first.
let private slashy: P<Expr> =
    let content =
        manyChars (
            attempt (skipString "\\/" >>% '/')
            <|> satisfy (fun c -> c <> '/' && c <> '\r' && c <> '\n'))

    let captured =
        between
            (skipString "/")
            (skipString "/")
            (withSkippedString (fun skipped decoded -> decoded, skipped) content)

    // The grammar owns the ambiguous slashy-versus-division decision. The
    // immutable refusal field rewinds with an abandoned `attempt`, while an
    // accepted slashy is measured from its raw content before the parse result
    // can be admitted.
    lexeme (
        attempt (
            captured .>>. getPosition .>>. getUserState
            >>= fun (((decoded, raw), position), state) ->
                    let scalarBytes = System.Text.Encoding.UTF8.GetByteCount raw

                    if scalarBytes > state.MaxScalarBytes then
                        let refusal =
                            AdmissionError.at
                                ScalarTooLong
                                position.Line
                                position.Column
                                $"string literal exceeds {state.MaxScalarBytes} UTF-8 bytes"

                        setUserState (keepFirstScalarRefusal state refusal) >>% EStr decoded
                    else
                        preturn (EStr decoded)))

let private exprForward = createParserForwardedToRef<Expr, TriviaState> ()
let private exprRef = fst exprForward
let private exprImpl = snd exprForward
let private stmtForward = createParserForwardedToRef<Stmt, TriviaState> ()
let private stmtRef = fst stmtForward
let private stmtImpl = snd stmtForward

/// FG-180. An expression position that ALSO admits a command-form call —
/// forwarded because GStrings need it before the call grammar exists.
let private exprOrCommandForward = createParserForwardedToRef<Expr, TriviaState> ()
let private exprOrCommandRef = fst exprOrCommandForward
let private exprOrCommandImpl = snd exprOrCommandForward

/// GString: literal runs plus `${…}` and `$ref` interpolations, kept apart so
/// the interpreter — not the lexer — decides what an interpolation means.
let private gstring (q: string) : P<Expr> =
    let isTriple = q.Length = 3

    let part =
        choice
            [ // FG-180: `"${tool 'M3'}/bin"` — a placeholder holds a whole
              // expression, so the command form is admitted here too.
              attempt (skipString "${" >>. withExpressionGroup (ws >>. exprOrCommandRef .>> ws) .>> skipString "}") |>> GExpr
              attempt (skipChar '$' >>. rawIdent .>>. many (attempt (skipChar '.' >>. rawIdent))
                       |>> fun (h, tail) -> GExpr(List.fold (fun acc n -> EProp(acc, n)) (EVar h) tail))
              (escaped |>> GLit)
              (many1Satisfy (fun c ->
                  c <> '$'
                  && c <> '\\'
                  && c <> q.[0]
                  && (isTriple || (c <> '\r' && c <> '\n')))
               |>> GLit) ]

    between (skipString q) (skipString q) (many part)
    |>> fun parts ->
            // collapse to a plain string when nothing interpolates
            if parts |> List.forall (function GLit _ -> true | GExpr _ -> false) then
                EStr(parts |> List.map (function GLit s -> s | GExpr _ -> "") |> String.concat "")
            else
                EGString parts

/// A double-quoted named-argument key is constant source text, not a GString.
/// This deliberately has its own single-line boundary: the shared `gstring`
/// parser also serves triple-double literals, where physical breaks are valid.
let private doubleQuotedConstantName: P<Expr> =
    between
        (skipString "\"")
        (skipString "\"")
        (manyStrings (
            escapedWithoutPhysicalBreak
            <|> (satisfy (fun c -> c <> '"' && c <> '$' && c <> '\\' && c <> '\n' && c <> '\r') |>> string)))
    |>> EStr

let private literal: P<Expr> =
    lexeme (
        choice
            [ attempt tripleSingle
              attempt (gstring "\"\"\"")
              attempt singleQuoted
              attempt (gstring "\"")
              attempt (keyword "null" >>% ENull)
              attempt (keyword "true" >>% EBool true)
              attempt (keyword "false" >>% EBool false)
              attempt (pint64 |>> EInt) ])

// --- collections, closures, calls ------------------------------------------

let private closureParams: P<string list> =
    // `->` alone, or `a, b ->`, or typed `String s ->`
    let typedParam = attempt (plainIdent >>. ws >>. plainIdent) <|> plainIdent
    attempt (symbol "->" >>% [])
    <|> attempt (sepBy1 typedParam (symbol ",") .>> symbol "->")

let private closure: P<Closure> =
    statementBody "{" "}" (
        ws >>. (opt (attempt closureParams) |>> Option.defaultValue [])
        .>>. many (attempt stmtRef)
        |>> fun (ps, body) -> { Params = ps; Body = body })

let private mapKey: P<string> =
    let dollarKey = attempt (skipChar '$' >>. rawIdent |>> fun n -> "$" + n)
    let strKey =
        (attempt tripleSingle <|> singleQuoted) >>= function
            | EStr s -> preturn s
            | _ -> fail "map key must be a constant"
    lexeme (dollarKey <|> attempt rawIdent <|> strKey) .>> symbol ":"

let private listOrMap: P<Expr> =
    expressionGroup "[" "]" (
        choice
            [ attempt (symbol ":" >>% EMap [])
              attempt (sepEndBy1 (attempt (mapKey .>>. exprRef)) (symbol ",") |>> EMap)
              (sepEndBy exprRef (symbol ",") |>> EList) ])

let private arg: P<Arg> =
    // FG-180. A named argument's NAME may be a string literal — `parallel
    // 'UI Tests': { … }` is how real corpus files label parallel branches.
    // Constant strings only: Groovy assembles named arguments into a map
    // literal, and a computed key is a different construct this grammar does
    // not claim. The named-key double-quote parser refuses interpolation and
    // physical line breaks while sharing the ordinary string escape decoder;
    // the general GString parser stays unchanged because it also serves valid
    // multiline triple-double literals.
    let strName =
        lexeme (
            choice
                [ attempt tripleSingle
                  attempt singleQuoted
                  attempt doubleQuotedConstantName ])
        >>= function
            | EStr s -> preturn s
            | _ -> fail "named-argument name must be a constant string"

    choice
        [ attempt (plainIdent .>>? symbol ":" .>>. exprRef |>> ANamed)
          attempt (strName .>>? symbol ":" .>>. exprRef |>> ANamed)
          (exprRef |>> APos) ]

/// FG-174. Refuses a DUPLICATE NAMED ARGUMENT — the same rule the declarative parser
/// applies, for the same reason: Groovy assembles a call's named arguments into a MAP
/// LITERAL, and Jenkins rejects the pipeline before running anything. MEASURED on the
/// pinned lab in both spellings, and UNPROVEN by receipt for the reason FG-129 gives —
/// a compile-shaped refusal emits nothing comparable. See the declarative parser's note.
///
/// BOTH parsers enforce it because both produce calls, and a rule held by only one of
/// them is the shape of half the findings on this branch — the script path and the stage
/// path taking different views of the same source construct.
let private noDuplicateNames (args: Arg list) : P<Arg list> =
    args
    |> List.choose (function
        | ANamed(n, _) -> Some n
        | APos _ -> None)
    |> List.countBy id
    |> List.tryPick (fun (name, count) -> if count > 1 then Some name else None)
    |> function
        | Some name ->
            fail
                $"duplicate named argument `{name}`: Groovy builds a call's named arguments as a map literal, so Jenkins rejects the pipeline before running anything"
        | None -> preturn args

let private argsInParens: P<Arg list> =
    expressionGroup "(" ")" (sepEndBy arg (symbol ",")) >>= noDuplicateNames

/// Command form: `sh 'make'`, `echo "x"`, `stash name: 's', includes: '*'`.
/// Only admitted when what follows cannot start a binary operator, so
/// `a + b` is never read as a call to `a`.
let private commandArgs: P<Arg list> =
    attempt (sepBy1 arg (symbol ",")) >>= noDuplicateNames

/// FG-180. Command-form free call in EXPRESSION position: `def m = tool 'M3'`,
/// `def n = tool name: 'x', type: 'y'`, `"${tool 'M3'}/bin"`. Before this the
/// named form refused the whole script and the positional form parsed as TWO
/// statements — `m` bound to the variable `tool`, the argument a no-op — an
/// admission with a wrong AST (evidence/20260818T192607Z-fg-180-probes).
///
/// Admitted only where a HOST POSITION opts in via `exprOrCommand`
/// (initialisers, assignment RHS, `return`, GString placeholders) — never
/// inside the expression grammar itself, so a command call can head a value
/// but not sit inside a binary chain, which is Groovy's own rule. Two guards
/// keep every existing parse intact:
///   - the first argument sits on the HEAD's line: Groovy ends the command
///     form at a line break, and without this `def x = foo` would swallow the
///     next line's statement as an argument;
///   - the first argument starts with a token that cannot CONTINUE an
///     expression — a quote, a digit, or an identifier. Operators, `(`, `[`,
///     `{` and `/` never commit: those belong to the expression grammar's own
///     forms (paren call, index, closure, division/slashy), and `a - b` must
///     stay subtraction.
let private commandExpr: P<Expr> =
    let argStart =
        nextCharSatisfies (fun c -> c = '\'' || c = '"' || isDigit c || isIdentStart c)

    attempt (
        plainIdent
        .>>? notAfterLineBreak
        .>>? argStart
        .>>. commandArgs
        .>>. opt (attempt closure)
        |>> fun ((n, args), t) -> ECall(FreeCall n, args, t))

exprOrCommandImpl.Value <- attempt commandExpr <|> exprRef

let private exprOrCommand: P<Expr> = exprOrCommandRef

let private primary: P<Expr> =
    ws
    >>. choice
            [ attempt literal
              attempt listOrMap
              attempt (closure |>> EClosure)
              attempt (expressionGroup "(" ")" exprRef)
              attempt slashy
              // A constructor IS a call, so it must reach the sandbox's call gate.
              // Parsing it as a variable named "new X" made `new File(...)`
              // evaluate to null and slip past denial entirely.
              attempt (keyword "new" >>. identifier .>>. opt (attempt argsInParens)
                       |>> fun (n, args) -> ECall(FreeCall("new " + n), defaultArg args [], None))
              (plainIdent |>> EVar) ]

/// Postfix chain: property access, indexing, calls, spread-dot, safe-nav,
/// and a trailing closure that turns `x.each { }` into a call.
let private postfixChain (start: Expr) : P<Expr> =
    let step: P<Expr -> Expr> =
        choice
            [ attempt (symbol "*." >>. plainIdent |>> fun n e -> ESpreadProp(e, n))
              attempt (
                  symbol "?." >>. plainIdent .>>. opt (attempt argsInParens) .>>. opt (attempt closure)
                  |>> fun ((n, args), trailing) e ->
                          match args, trailing with
                          | None, None -> ESafeProp(e, n)
                          | a, t -> ECall(SafeMethodCall(e, n), defaultArg a [], t))
              attempt (
                  symbol "." >>. plainIdent .>>. opt (attempt argsInParens) .>>. opt (attempt closure)
                  |>> fun ((n, args), trailing) e ->
                          match args, trailing with
                          | None, None -> EProp(e, n)
                          | a, t -> ECall(MethodCall(e, n), defaultArg a [], t))
              // FG-187. A postfix index may NOT begin a new line. `def a = false` followed
              // by a line starting with `[1].each { … }` parsed as the single expression
              // `false[1].each { … }`: two statements became one, the second never ran, and
              // `each` faulted on a non-list receiver. Groovy ends a statement at a line
              // break once it is complete, so both lines run there.
              //
              // IT WAS NOT ONLY A FAILURE. Where the swallowed line indexes something real
              // — `def xs = [1, 2]` then `[0].each { … }` — the result is an ELEMENT rather
              // than a list and no error is raised at all.
              //
              // This masked FG-179 for months: every probe of closure capture was written
              // across newlines, so the confound sat in the evidence for both sides of that
              // argument and two observers agreed on a wrong cause.
              attempt (indexMayContinue >>. expressionGroup "[" "]" exprRef |>> fun i e -> EIndex(e, i))
              attempt (argsInParens .>>. opt (attempt closure)
                       |>> fun (args, t) e ->
                               match e with
                               | EVar n -> ECall(FreeCall n, args, t)
                               | _ -> ECall(MethodCall(e, "call"), args, t))
              attempt (closure |>> fun c e ->
                          match e with
                          | EVar n -> ECall(FreeCall n, [], Some c)
                          | _ -> ECall(MethodCall(e, "call"), [], Some c)) ]

    // `many` owns this flat repetition iteratively. The former recursive loop
    // invoked itself once per suffix, so a MaxNodes-sized `.x` chain could put
    // roughly sixteen thousand live parser frames on the process stack even
    // though it contained no nested grammar at all.
    many (attempt step)
    |>> List.fold (fun e applySuffix -> applySuffix e) start

let private unaryForward = createParserForwardedToRef<Expr, TriviaState> ()
let private unaryRef = fst unaryForward
let private unaryImpl = snd unaryForward

unaryImpl.Value <-
    ws
    >>. choice
            [ attempt (symbol "!" >>. unaryRef |>> fun e -> EUnary("!", e))
              attempt (symbol "-" >>. unaryRef |>> fun e -> EUnary("-", e))
              (primary >>= postfixChain) ]

let private binary (ops: string list) (next: P<Expr>) : P<Expr> =
    let opP =
        ops
        |> List.map (fun o ->
            // `+` must not match the first `+` of `++`, else `i++` parses as
            // `i + (+…)` and postfix increment becomes unreachable.
            let guard =
                if o = "+" then notFollowedBy (anyOf "=&|+")
                elif o = "-" then notFollowedBy (anyOf "=&|->")
                else notFollowedBy (anyOf "=&|")

            attempt (lexeme (skipString o .>> guard)) >>% o)
        |> choice

    chainl1 next (opP |>> fun op l r -> EBinary(op, l, r))

let private multiplicative = binary [ "*"; "/"; "%" ] unaryRef
let private additive = binary [ "+"; "-" ] multiplicative
let private shift = binary [ "<<" ] additive

let private rangeExpr =
    chainl1 shift (attempt (symbol "..") >>% (fun l r -> EBinary("..", l, r)))

let private instanceOfExpr =
    rangeExpr .>>. opt (attempt (keyword "instanceof" >>. identifier))
    |>> function
        | e, None -> e
        | e, Some t -> EBinary("instanceof", e, EStr t)

let private relational =
    chainl1 instanceOfExpr (
        choice
            [ attempt (symbol "<=") >>% "<="
              attempt (symbol ">=") >>% ">="
              attempt (lexeme (skipString "<" .>> notFollowedBy (anyOf "<="))) >>% "<"
              attempt (lexeme (skipString ">" .>> notFollowedBy (anyOf ">="))) >>% ">" ]
        |>> fun op l r -> EBinary(op, l, r))

let private regexMatch =
    chainl1 relational (
        choice [ attempt (symbol "==~") >>% "==~"; attempt (symbol "=~") >>% "=~" ]
        |>> fun op l r -> EBinary(op, l, r))

let private equality =
    chainl1 regexMatch (
        choice [ attempt (symbol "==") >>% "=="; attempt (symbol "!=") >>% "!=" ]
        |>> fun op l r -> EBinary(op, l, r))

let private logicalAnd = chainl1 equality (attempt (symbol "&&") >>% fun l r -> EBinary("&&", l, r))
let private logicalOr = chainl1 logicalAnd (attempt (symbol "||") >>% fun l r -> EBinary("||", l, r))

exprImpl.Value <-
    logicalOr
    >>= fun c ->
            choice
                [ attempt (symbol "?" >>. exprRef .>>. (symbol ":" >>. exprRef) |>> fun (a, b) -> ETernary(c, a, b))
                  attempt (symbol "?:" >>. exprRef |>> fun b -> EElvis(c, b))
                  preturn c ]

// --- statements ------------------------------------------------------------

let private block: P<Stmt list> = statementBody "{" "}" (ws >>. many (attempt stmtRef))
let private blockOrSingle: P<Stmt list> =
    attempt block <|> withStatementBody (stmtRef |>> List.singleton)

let private annotationStmt: P<Stmt> =
    // `@Library('x') _` preserved as the equivalent `library('x')` call so the
    // dependency survives into the AST rather than being discarded.
    attempt (
        skipChar '@' >>. identifier .>>. opt (attempt argsInParens)
        .>> ws .>> opt (attempt (symbol "_"))
        |>> fun (name, args) ->
                let lowered = if name = "Library" then "library" else name
                SExpr(ECall(FreeCall lowered, defaultArg args [], None)))

let private importStmt: P<Stmt> =
    attempt (
        keyword "import" >>. opt (keyword "static")
        >>. lexeme (many1Chars (satisfy (fun c -> isLetter c || isDigit c || c = '.' || c = '_' || c = '*')))
        |>> fun path -> SExpr(ECall(FreeCall "import", [ APos(EStr path) ], None)))

let private funcParams: P<string list> =
    // `String s` or bare `s` — the TYPE is erased, as `typedVar` and the
    // closure grammar already erase it: dispatch is by arity (FG-195), not by
    // static type. A DEFAULT (`x = true`) is NOT admitted: it changes the
    // function's callable arities, `SFunc` has nowhere to carry the value,
    // and dropping it silently would fault a valid zero-arg call at runtime —
    // the honest refusal stands until the AST can hold it (FG-180 residual).
    let param = attempt (plainIdent >>? ws >>? plainIdent) <|> plainIdent
    between (symbol "(") (symbol ")") (sepEndBy param (symbol ","))

let private defFunc: P<Stmt> =
    attempt (
        keyword "def" >>. plainIdent
        .>>. funcParams
        .>>. block
        |>> fun ((n, ps), b) -> SFunc(n, ps, b))

/// FG-180. `void f(Maven m) { … }`, `String g() { … }` — a TYPED function
/// declaration; the return type is erased exactly as parameter types are.
/// Commits only on `<ident> <ident> ( … ) {`, so `echo foo(1)` stays a
/// command call (no block follows) and `String s = "x"` stays `typedVar`
/// (no parameter list).
let private typedFunc: P<Stmt> =
    attempt (
        plainIdent >>? ws >>? plainIdent
        .>>? notAfterLineBreak // `echo msg` then `(x) { … }` on the next line
                               // is two statements, never a declaration —
                               // the FG-187 defect class one level up
        .>>. funcParams
        .>>. block
        |>> fun ((n, ps), b) -> SFunc(n, ps, b))

let private multiAssign: P<Stmt> =
    // `def (a, b) = expr` — the source is evaluated ONCE into a hidden binding,
    // then each name reads an index of it. The first lowering COPIED the source
    // expression into every binding, so an effectful RHS — `def (a, b) =
    // f('x')`, or since FG-180 the command form — ran once PER TARGET, its
    // step effects duplicated and the two names possibly bound from different
    // results (Codex P1, PR #98; the copy predates the command form). The
    // temp's name starts with SOH, which no identifier can contain, so author
    // code can neither read, shadow, nor collide with it.
    attempt (
        keyword "def"
        >>. between (symbol "(") (symbol ")") (sepBy1 plainIdent (symbol ","))
        .>> symbol "="
        .>>. exprOrCommand
        |>> fun (names, src) ->
                let tmp = "\u0001destructure"

                let binds =
                    names
                    |> List.mapi (fun i n -> SDef(n, Some(EIndex(EVar tmp, EInt(int64 i)))))

                SIf(EBool true, SDef(tmp, Some src) :: binds, []))

let private defVar: P<Stmt> =
    attempt (keyword "def" >>. plainIdent .>>. opt (attempt (symbol "=" >>. exprOrCommand)) |>> SDef)

let private finalStmt: P<Stmt> =
    attempt (
        keyword "final"
        >>. opt (attempt (plainIdent .>>? followedBy (plainIdent .>> ws .>> skipString "=")))
        >>. plainIdent .>> symbol "=" .>>. exprOrCommand
        |>> fun (n, v) -> SDef(n, Some v))

let private typedVar: P<Stmt> =
    // `String s = "x"` — commits only on `<Type> <name> =`
    attempt (
        plainIdent >>? ws >>? plainIdent .>>? symbol "=" .>>. exprOrCommand
        |>> fun (n, v) -> SDef(n, Some v))

let private ifStmt: P<Stmt> =
    attempt (
        keyword "if" >>. expressionGroup "(" ")" exprRef
        .>>. blockOrSingle
        .>>. opt (attempt (keyword "else" >>. blockOrSingle))
        |>> fun ((c, t), e) -> SIf(c, t, defaultArg e []))

let private forStmt: P<Stmt> =
    let forIn =
        attempt (
            expressionGroup "(" ")" (
                opt (attempt (keyword "def")) >>. plainIdent .>> keyword "in" .>>. exprRef)
            .>>. blockOrSingle
            |>> fun ((v, src), b) -> SForIn(v, src, b))

    // C-style `for (int i = 0; i < n; i++)` desugars to init + while, so no AST
    // case is needed and the interpreter sees ordinary constructs.
    let forC =
        expressionGroup "(" ")" (
            opt (attempt (opt (attempt plainIdent) >>. plainIdent .>> symbol "=" .>>. exprRef))
            .>> symbol ";" .>>. opt exprRef .>> symbol ";" .>>. opt stmtRef)
        .>>. blockOrSingle
        |>> fun (((init, cond), step), body) ->
                let inner = body @ (match step with Some s -> [ s ] | None -> [])
                let loop = SWhile(defaultArg cond (EBool true), inner)

                match init with
                | Some(v, e) -> SIf(EBool true, [ SDef(v, Some e); loop ], [])
                | None -> loop

    keyword "for" >>. (forIn <|> forC)

let private whileStmt: P<Stmt> =
    attempt (keyword "while" >>. expressionGroup "(" ")" exprRef .>>. blockOrSingle |>> SWhile)

let private tryStmt: P<Stmt> =
    attempt (
        keyword "try" >>. block
        .>>. opt (attempt (
            keyword "catch"
            >>. between (symbol "(") (symbol ")") (opt (attempt plainIdent) .>>. opt plainIdent)
            .>>. block))
        .>>. opt (attempt (keyword "finally" >>. block))
        |>> fun ((b, c), f) ->
                // `catch (Type name)` and `catch (name)` — a single identifier is the
                // BINDING (Groovy defaults its type to Exception), not a type.
                let catch =
                    c
                    |> Option.map (fun ((first, second), handler) ->
                        match first, second with
                        | Some t, Some b -> Some t, Some b, handler
                        | Some only, None -> None, Some only, handler
                        | None, b -> None, b, handler)

                STry(b, catch, defaultArg f []))

/// `switch (e) { case a: … default: … }` — a REAL NODE, `SSwitch`, carrying the arms in
/// source order.
///
/// IT USED TO BE LOWERED to nested ifs, "so the interpreter needs no extra case". That
/// saved one interpreter arm and cost three consecutive defects, each found by the
/// pre-push verifier on the fix for the last:
///
///   1. the arm-final `break` became an `SBreak` with no loop around it — at runtime a
///      `BreakSignal` escaping the engine, and once FG-183 refused misplaced breaks, a
///      REFUSAL OF VALID GROOVY.
///   2. consuming just that one left `case 'a': if (x) break; more()` in the tree. Outside
///      a loop it was refused, so the fix looked complete; INSIDE one the admission check
///      saw `inLoop = true` and the interpreter's loop handler caught the signal as a LOOP
///      break — success reported, work skipped.
///   3. refusing every remaining arm `break` then over-refused `case 'a': if (true) break`,
///      which Groovy accepts and continues past.
///
/// Three positions, each defensible against the previous failure and wrong about the next,
/// which is the signature of compensating for missing structure instead of supplying it. A
/// switch IS a break boundary; the lowering destroyed exactly that, and every downstream
/// stage then had to guess. The node restores it and all three questions stop being
/// questions.
///
/// FALLTHROUGH IS NOW MODELLED TOO, because the node can express it and nested ifs could
/// not: they are mutually exclusive, so an arm without a `break` stopped rather than
/// running into the next. The old comment called that a known gap, justified by the corpus
/// rather than by the language.
let private switchStmt: P<Stmt> =
    attempt (
        keyword "switch" >>. expressionGroup "(" ")" exprRef
        .>>. statementBody "{" "}" (
            ws
            >>. many (
                attempt (
                    (attempt (keyword "case" >>. exprRef .>> symbol ":") |>> Some
                     <|> (keyword "default" >>. symbol ":" >>% None))
                    .>>. many (attempt stmtRef))))
        |>> SSwitch)

let private returnStmt: P<Stmt> =
    // FG-180 (verifier's construction on this diff). The value must START on
    // the `return`'s own line: Groovy ends a `return` at a line break, and
    // without the guard `if (skip) return` followed by `sh 'make'` swallowed
    // the sh into the return — EXECUTING it on the path Groovy skips and
    // dropping it from the path Groovy runs. The pre-guard grammar had the
    // same swallow with a plain variable (`return` then `foo` on the next
    // line); the command form escalated a loud strict-mode fault into silent
    // wrong execution, so the guard closes both.
    attempt (keyword "return" >>. opt (attempt (notAfterLineBreak >>. exprOrCommand)) |>> SReturn)
let private throwStmt: P<Stmt> = attempt (keyword "throw" >>. exprRef |>> SThrow)

/// Command-form free call with no parentheses: `sh 'make'`, `echo "x"`.
let private commandCall: P<Stmt> =
    // `[` is excluded from starting command arguments — FG-180: `builds['a']
    // = { … }` was read as a call `builds(['a'])`, leaving `= { … }` to fail
    // as a statement of its own. An index-assignment swallowed into a call is
    // a wrong AST; the rare `foo [list]` command spelling loses to it.
    attempt (
        plainIdent .>>? notAfterLineBreak
        .>>? (notFollowedBy (choice [ symbol "="; symbol "." ; symbol "("; symbol "[" ]))
        .>>. commandArgs
        .>>. opt (attempt closure)
        |>> fun ((n, args), t) -> SExpr(ECall(FreeCall n, args, t)))

let private assignOrExpr: P<Stmt> =
    let assignOp =
        choice
            [ attempt (lexeme (skipString "=" .>> notFollowedBy (anyOf "=~"))) >>% None
              attempt (symbol "+=") >>% Some "+"
              attempt (symbol "-=") >>% Some "-"
              attempt (symbol "*=") >>% Some "*"
              attempt (symbol "/=") >>% Some "/" ]

    exprRef
    >>= fun lhs ->
            choice
                [ attempt ((attempt (symbol "++") >>% "+") <|> (attempt (symbol "--") >>% "-")
                           |>> fun op ->
                               match lhs with
                               | EIndex _ -> SIndexPostfixAssign(lhs, op)
                               | _ -> SAssign(lhs, EBinary(op, lhs, EInt 1L)))
                  attempt (assignOp >>= fun op ->
                              exprOrCommand
                              |>> fun rhs ->
                                      match op with
                                      | None -> SAssign(lhs, rhs)
                                      | Some o ->
                                          match lhs with
                                          | EIndex _ -> SIndexCompoundAssign(lhs, o, rhs)
                                          | _ -> SAssign(lhs, EBinary(o, lhs, rhs)))
                  preturn (SExpr lhs) ]

stmtImpl.Value <-
    ws
    >>. choice
            [ annotationStmt
              importStmt
              attempt defFunc
              attempt typedFunc
              attempt multiAssign
              finalStmt
              defVar
              ifStmt
              forStmt
              whileStmt
              tryStmt
              switchStmt
              returnStmt
              attempt (keyword "break" >>% SBreak)
              attempt (keyword "continue" >>% SContinue)
              throwStmt
              attempt typedVar
              attempt commandCall
              assignOrExpr ]
    .>> opt (attempt (skipMany1 (symbol ";")))

/// A `#!` shebang is legal Groovy but only on the first line.
let private shebang: P<unit> =
    let atStart (stream: CharStream<TriviaState>) =
        if stream.Index = 0L then Reply(()) else Reply(Error, expected "start of script")

    attempt (atStart >>. skipString "#!" >>. skipRestOfLine true)

let private program: P<Script> = opt shebang >>. ws >>. many (attempt stmtRef) .>> ws .>> eof

let parseWithLimits (limits: Limits) (source: string) : Result<Script, AdmissionError> =
    match Limits.precheck limits source with
    | Result.Error e -> Result.Error e
    | Result.Ok() ->
        match runParserOnString program (initialState limits) "script" source with
        | ParserResult.Success(_, state, _) when state.ScalarRefusal.IsSome ->
            Result.Error state.ScalarRefusal.Value
        | ParserResult.Success(s, _, _) -> Result.Ok s
        | ParserResult.Failure(_, _, state) when state.ScalarRefusal.IsSome ->
            Result.Error state.ScalarRefusal.Value
        | ParserResult.Failure(msg, err, _) ->
            let firstLine =
                msg.Split('\n')
                |> Array.filter (fun l -> l.Trim() <> "")
                |> Array.tryLast
                |> Option.defaultValue "unparsable"

            Result.Error(AdmissionError.at MalformedSyntax err.Position.Line err.Position.Column (firstLine.Trim()))

let parse (source: string) : Result<Script, AdmissionError> = parseWithLimits Limits.defaults source
