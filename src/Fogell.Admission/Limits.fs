namespace Fogell.Admission

open System.Text
open Fogell.Ir

/// FG-004 admission limits, applied BEFORE schema compilation.
///
/// Forge's parser has no measured input bounds; on hostile input an FParsec
/// grammar with deep recursion can exhaust the stack, which turns an untrusted
/// Jenkinsfile into a controller crash. These caps are deliberately generous
/// for real files (the largest corpus Jenkinsfile is ~57 KB / ~1,100 lines) and
/// hostile ones fail closed with a named code.
type Limits =
    { MaxSourceBytes: int
      MaxNodes: int
      MaxDepth: int
      MaxScalarBytes: int
      MaxCollectionItems: int }

    static member defaults =
        { MaxSourceBytes = 262_144
          MaxNodes = 16_384
          MaxDepth = 64
          MaxScalarBytes = 16_384
          MaxCollectionItems = 4_096 }

module Limits =

    let private quoteIsEscaped (source: string) quoteIndex =
        let mutable precedingBackslashes = 0
        let mutable i = quoteIndex - 1

        while i >= 0 && source.[i] = '\\' do
            precedingBackslashes <- precedingBackslashes + 1
            i <- i - 1

        precedingBackslashes % 2 = 1

    /// For every source index, record the next slash on the same physical
    /// line that can terminate a slashy literal. Groovy's slashy escape rule
    /// is deliberately narrower than ordinary-string escaping: a slash whose
    /// immediately preceding character is a backslash is content, while a
    /// backslash before any other character is just raw content.
    ///
    /// Computing this table once is load-bearing. Looking for a closing slash from
    /// every possible opener makes a line of repeated escaped slashes
    /// quadratic; the source cap bounds memory and this pass stays O(n).
    let private nextSlashyClosers (source: string) =
        let closers = Array.create source.Length -1
        let mutable nextCloser = -1

        for i = source.Length - 1 downto 0 do
            if source.[i] = '\r' || source.[i] = '\n' then
                nextCloser <- -1

            closers.[i] <- nextCloser

            if source.[i] = '/' && (i = 0 || source.[i - 1] <> '\\') then
                nextCloser <- i

        closers

    /// Cheap pre-parse guard. Performs a UTF-8 byte-count pass, then counts
    /// brace depth and token-ish nodes with a second linear scan. Neither pass
    /// recurses, so this guard cannot itself exhaust the stack. It is
    /// intentionally approximate: its job is to make the *parser* safe to run,
    /// not to be a second grammar. Slashy-versus-division is grammar context,
    /// so each parser's slashy production applies the same MaxScalarBytes cap
    /// when that grammar commits to a slashy value.
    let precheck (limits: Limits) (source: string) : Result<unit, AdmissionError> =
        let sourceBytes =
            if isNull source then 0 else Encoding.UTF8.GetByteCount source

        if System.String.IsNullOrWhiteSpace source then
            Error(AdmissionError.at EmptySource 1L 1L "source is empty")
        elif sourceBytes > limits.MaxSourceBytes then
            Error(
                AdmissionError.at
                    SourceTooLarge
                    1L
                    1L
                    $"source is {sourceBytes} UTF-8 bytes, limit is {limits.MaxSourceBytes}"
            )
        else
            let slashyClosers = nextSlashyClosers source
            let mutable depth = 0
            let mutable nodes = 0
            let mutable line = 1L
            let mutable column = 1L
            let mutable scalarContentStart = -1
            let mutable quote = '\000'
            let mutable delimiterLength = 0
            let mutable lineComment = false
            let mutable blockComment = false
            // Slashy-versus-division cannot be decided from the immediately
            // preceding character: `return /x/` and command-form `echo /x/`
            // both follow an identifier, while `return a / b` is division.
            // These fields are a bounded lexical context, not a grammar. They
            // only decide whether a complete same-line slashy span shields its
            // contents from this guard; the real parser still decides whether
            // the source is valid and owns the slashy's scalar-byte refusal.
            let mutable needsOperand = true
            let mutable statementHead = true
            let mutable commandHead = false
            let mutable sawLineBreak = false
            let mutable awaitingControlParen = false
            let controlParens = System.Collections.Generic.Stack<bool>()
            let mutable expressionNesting = 0
            let mutable err = None
            let mutable i = 0

            let recordNode () =
                nodes <- nodes + 1

                if nodes > limits.MaxNodes then
                    err <-
                        Some(
                            AdmissionError.at
                                TooManyNodes
                                line
                                column
                                $"node count exceeds {limits.MaxNodes}"
                        )

            let markTriviaBreak () =
                sawLineBreak <- true
                // Command-form arguments must begin on their head's line.
                // Do not reset HaveOperand generally: Fogell permits a binary
                // operator after newline trivia (`a\n / b`). A `}` is also a
                // completed operand when it closes a closure; this lexical
                // guard cannot prove otherwise, so it never resets one. That
                // conservative choice can expose slashy-looking structure to
                // the limits, but cannot hide real structure from them.
                commandHead <- false

            while err.IsNone && i < source.Length do
                let c = source.[i]

                if c = '\r' then
                    line <- line + 1L
                    column <- 1L
                elif c = '\n' then
                    if i = 0 || source.[i - 1] <> '\r' then line <- line + 1L
                    column <- 1L
                else
                    column <- column + 1L

                if lineComment then
                    if c = '\r' || c = '\n' then
                        lineComment <- false
                        markTriviaBreak ()
                elif blockComment then
                    if c = '\r' || c = '\n' then markTriviaBreak ()

                    if c = '*' && i + 1 < source.Length && source.[i + 1] = '/' then
                        i <- i + 1
                        column <- column + 1L
                        blockComment <- false
                elif quote <> '\000' then
                    let closes =
                        if delimiterLength = 3 then
                            c = quote
                            && i + 2 < source.Length
                            && source.[i + 1] = quote
                            && source.[i + 2] = quote
                            && not (quoteIsEscaped source i)
                        else
                            c = quote && not (quoteIsEscaped source i)

                    if closes then
                        let closingStart = i

                        if delimiterLength = 3 then
                            i <- i + 2
                            column <- column + 2L

                        let scalarBytes =
                            Encoding.UTF8.GetByteCount(source, scalarContentStart, closingStart - scalarContentStart)

                        if scalarBytes > limits.MaxScalarBytes then
                            err <-
                                Some(
                                    AdmissionError.at
                                        ScalarTooLong
                                        line
                                        column
                                        $"string literal exceeds {limits.MaxScalarBytes} UTF-8 bytes"
                                )

                        quote <- '\000'
                        scalarContentStart <- -1
                        delimiterLength <- 0
                        needsOperand <- false
                        statementHead <- false
                        commandHead <- false
                        sawLineBreak <- false
                else
                    match c with
                    | ' ' | '\t' -> ()
                    | '\r' | '\n' -> markTriviaBreak ()
                    | '/' when i + 1 < source.Length && source.[i + 1] = '/' ->
                        lineComment <- true
                        i <- i + 1
                        column <- column + 1L
                    | '/' when i + 1 < source.Length && source.[i + 1] = '*' ->
                        blockComment <- true
                        i <- i + 1
                        column <- column + 1L
                    | '\'' | '"' ->
                        quote <- c
                        delimiterLength <-
                            if i + 2 < source.Length && source.[i + 1] = c && source.[i + 2] = c then
                                3
                            else
                                1

                        scalarContentStart <- i + delimiterLength
                        recordNode ()

                        if delimiterLength = 3 then
                            i <- i + 2
                            column <- column + 2L
                    | '/'
                        when slashyClosers.[i] >= 0
                             && (needsOperand || statementHead || commandHead) ->
                        // Comments won above. A complete candidate in an
                        // operand or command-argument position shields every
                        // quote and delimiter through its cached closing index. If no
                        // closing slash exists this arm is not entered: the slash is
                        // processed as ordinary code and all fallback
                        // node/depth accounting remains visible.
                        recordNode ()

                        if err.IsNone then
                            let closingIndex = slashyClosers.[i]
                            column <- column + int64 (closingIndex - i)
                            i <- closingIndex
                            needsOperand <- false
                            statementHead <- false
                            commandHead <- false
                            sawLineBreak <- false
                    | '{' | '[' | '(' ->
                        depth <- depth + 1
                        recordNode ()

                        if depth > limits.MaxDepth then
                            err <-
                                Some(
                                    AdmissionError.at
                                        NestingTooDeep
                                        line
                                        column
                                        $"nesting depth {depth} exceeds limit {limits.MaxDepth}"
                                )
                        elif c = '(' then
                            controlParens.Push awaitingControlParen
                            awaitingControlParen <- false
                            expressionNesting <- expressionNesting + 1
                            needsOperand <- true
                            statementHead <- false
                            commandHead <- false
                            sawLineBreak <- false
                        elif c = '[' then
                            expressionNesting <- expressionNesting + 1
                            needsOperand <- true
                            statementHead <- false
                            commandHead <- false
                            sawLineBreak <- false
                        else
                            // A brace begins either a statement body or an
                            // expression literal; both allow an operand first.
                            needsOperand <- true
                            statementHead <- true
                            commandHead <- false
                            sawLineBreak <- false
                    | '}' | ']' | ')' ->
                        depth <- depth - 1
                        recordNode ()

                        if c = ')' then
                            expressionNesting <- max 0 (expressionNesting - 1)

                            let closedControl =
                                if controlParens.Count = 0 then false else controlParens.Pop()

                            if closedControl then
                                // `if (...) /x/` is an unbraced statement
                                // body, not division by the condition.
                                needsOperand <- true
                                statementHead <- true
                            else
                                needsOperand <- false
                                statementHead <- false

                        elif c = ']' then
                            expressionNesting <- max 0 (expressionNesting - 1)
                            needsOperand <- false
                            statementHead <- false
                        else
                            needsOperand <- false
                            statementHead <- false

                        commandHead <- false
                        sawLineBreak <- false
                    | c when System.Char.IsLetterOrDigit c || c = '_' ->
                        let start = i

                        while
                            i + 1 < source.Length
                            && (System.Char.IsLetterOrDigit source.[i + 1] || source.[i + 1] = '_')
                            do
                            i <- i + 1
                            column <- column + 1L

                        let word = source.Substring(start, i - start + 1)
                        recordNode ()

                        if err.IsNone then
                            let beginsNewStatement =
                                statementHead
                                || (sawLineBreak && expressionNesting = 0 && not needsOperand)

                            match word with
                            | "if" | "while" | "for" | "switch" | "catch" ->
                                awaitingControlParen <- true
                                needsOperand <- true
                                statementHead <- false
                                commandHead <- false
                            | "return" | "throw" | "case" | "in" ->
                                // These keywords require an expression next;
                                // their final identifier character must never
                                // force a following slash to division.
                                awaitingControlParen <- false
                                needsOperand <- true
                                statementHead <- false
                                commandHead <- false
                            | "else" ->
                                // An unbraced else body begins a statement just
                                // like a completed control header.
                                awaitingControlParen <- false
                                needsOperand <- true
                                statementHead <- true
                                commandHead <- false
                            | "def" | "final" | "new" | "instanceof" | "as" ->
                                awaitingControlParen <- false
                                needsOperand <- true
                                statementHead <- false
                                commandHead <- false
                            | "true" | "false" | "null" ->
                                awaitingControlParen <- false
                                needsOperand <- false
                                statementHead <- false
                                commandHead <- false
                            | _ ->
                                awaitingControlParen <- false
                                // A leading identifier can be a Groovy
                                // command head (`echo /x/`); a number cannot.
                                // The next identifier begins the first argument
                                // expression, so `echo value / x / 2` is
                                // division and must leave its depth visible.
                                // Keeping numeric heads out is what preserves
                                // `10 / 2 / 5` as two divisions.
                                let identifierHead =
                                    System.Char.IsLetter word.[0] || word.[0] = '_'

                                commandHead <- identifierHead && beginsNewStatement
                                needsOperand <- false
                                statementHead <- false

                            sawLineBreak <- false
                    | ';' ->
                        needsOperand <- true
                        statementHead <- true
                        commandHead <- false
                        awaitingControlParen <- false
                        sawLineBreak <- false
                    | '+' | '-' when i + 1 < source.Length && source.[i + 1] = c && not needsOperand ->
                        // Postfix increment/decrement leaves a complete value.
                        i <- i + 1
                        column <- column + 1L
                        needsOperand <- false
                        statementHead <- false
                        commandHead <- false
                        sawLineBreak <- false
                    | '/' | '=' | '+' | '-' | '*' | '%' | '&' | '|' | '^' | '!' | '~'
                    | '<' | '>' | ',' | ':' | '?' | '.' ->
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        sawLineBreak <- false
                    | _ ->
                        // Unknown punctuation is deliberately not promoted to
                        // an expression ender. If the grammar accepts it, a
                        // later explicit token will establish slash context;
                        // if it does not, the parser will fail closed.
                        commandHead <- false
                        sawLineBreak <- false

                i <- i + 1

            if err.IsNone && quote <> '\000' then
                let scalarBytes =
                    Encoding.UTF8.GetByteCount(source, scalarContentStart, source.Length - scalarContentStart)

                if scalarBytes > limits.MaxScalarBytes then
                    err <-
                        Some(
                            AdmissionError.at
                                ScalarTooLong
                                line
                                column
                                $"string literal exceeds {limits.MaxScalarBytes} UTF-8 bytes"
                        )

            // An unclosed block comment is already conclusively malformed.
            // Refuse it in this single linear pass instead of handing a suffix
            // containing many `/*` candidates to backtracking raw scanners,
            // where each candidate could otherwise rescan to EOF.
            if err.IsNone && blockComment then
                err <-
                    Some(
                        AdmissionError.at
                            MalformedSyntax
                            line
                            column
                            "unterminated block comment"
                    )

            match err with
            | Some e -> Error e
            | None -> Ok()
