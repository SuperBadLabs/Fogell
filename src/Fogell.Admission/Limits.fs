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

/// Read-only slashy/division classifications for one exact source string.
/// The backing representation and its unterminated-candidate sentinel remain
/// private to admission; parser consumers can query or slice without mutating
/// the DFA result.
[<Sealed>]
type SlashySpans internal (closers: int array) =
    static member Empty = SlashySpans Array.empty

    member _.Length = closers.Length

    member _.IsClassified(index: int) =
        index >= 0 && index < closers.Length && closers.[index] <> -1

    member _.Slice(start: int, length: int) =
        if length <= 0 then
            SlashySpans.Empty
        else
            let sliced =
                Array.init length (fun relativeIndex ->
                    let outerIndex = start + relativeIndex

                    if outerIndex < 0 || outerIndex >= closers.Length then
                        -1
                    else
                        let outerCloser = closers.[outerIndex]

                        if outerCloser = -2 then
                            -2
                        elif outerCloser >= start && outerCloser < start + length then
                            outerCloser - start
                        else
                            -1)

            SlashySpans sliced

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
    let precheckWithSlashySpans (limits: Limits) (source: string) : Result<SlashySpans, AdmissionError> =
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
            // Only the DFA-classified opener positions are exported. A
            // structural subscanner can then share this exact slashy/division
            // decision instead of growing a second, drifting lookbehind rule.
            let recognizedSlashyClosers = Array.create source.Length -1
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
            let mutable returnFallbackPending = false
            let mutable returnCommandHead = false
            let controlParens = System.Collections.Generic.Stack<bool>()
            // A `case` expression ends at a colon at the same structural and
            // expression nesting where the keyword began. Its third field is
            // the number of same-level ternaries whose colons must win first.
            let mutable caseContext: (int * int * int) option = None
            // A double-quoted GString temporarily leaves quote mode for each
            // `${...}` expression. Keep the outer delimiter on an explicit
            // stack so interpolation structure is counted by this same
            // non-recursive guard, including nested GStrings.
            let interpolationQuotes =
                System.Collections.Generic.Stack<char * int * int * int>()

            let mutable pendingInterpolation: (char * int * int * int) option = None
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

            let nextTokenCanStartUnary closingIndex =
                let mutable cursor = closingIndex + 1
                let mutable scanning = true

                while scanning && cursor < source.Length do
                    match source.[cursor] with
                    | ' ' | '\t' | '\r' | '\n' -> cursor <- cursor + 1
                    | '/' when cursor + 1 < source.Length && source.[cursor + 1] = '/' ->
                        cursor <- cursor + 2

                        while
                            cursor < source.Length
                            && source.[cursor] <> '\r'
                            && source.[cursor] <> '\n'
                            do
                            cursor <- cursor + 1
                    | '/' when cursor + 1 < source.Length && source.[cursor + 1] = '*' ->
                        let closingComment = source.IndexOf("*/", cursor + 2, System.StringComparison.Ordinal)

                        if closingComment < 0 then
                            cursor <- source.Length
                            scanning <- false
                        else
                            cursor <- closingComment + 2
                    | _ -> scanning <- false

                if cursor >= source.Length then
                    false
                else
                    let next = source.[cursor]

                    System.Char.IsLetterOrDigit next
                    || next = '_'
                    || next = '\''
                    || next = '"'
                    || next = '('
                    || next = '['
                    || next = '{'
                    || next = '/'
                    || next = '!'
                    || next = '-'

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
                    let escapedPhysicalBreak =
                        (c = '\r' && quoteIsEscaped source i)
                        || (c = '\n'
                            && (quoteIsEscaped source i
                                || (i > 0 && source.[i - 1] = '\r' && quoteIsEscaped source (i - 1))))

                    if delimiterLength = 1 && (c = '\r' || c = '\n') && not escapedPhysicalBreak then
                        // Ordinary Groovy strings cannot cross an unescaped
                        // physical ending. Refuse here instead of allowing an
                        // invalid prefix quote to shield a later Declarative
                        // pipeline from the structural safety guard.
                        err <-
                            Some(
                                AdmissionError.at
                                    MalformedSyntax
                                    line
                                    column
                                    "ordinary string literal crosses a physical line ending"
                            )
                    elif
                        quote = '"'
                        && c = '$'
                        && i + 1 < source.Length
                        && source.[i + 1] = '{'
                        && not (quoteIsEscaped source i)
                    then
                        // The recursive Groovy grammar parses `${...}` as a
                        // real expression. Resume ordinary structural scanning
                        // at its opening brace, then restore this outer string
                        // when the matching brace closes.
                        pendingInterpolation <- Some(quote, delimiterLength, scalarContentStart, depth)
                        quote <- '\000'
                        scalarContentStart <- -1
                        delimiterLength <- 0
                    elif
                        quote = '"'
                        && c = '$'
                        && i + 1 < source.Length
                        && (System.Char.IsLetter source.[i + 1] || source.[i + 1] = '_')
                        && not (quoteIsEscaped source i)
                    then
                        // `$ref.tail` is the non-braced GString interpolation
                        // form. It is non-recursive, but each identifier still
                        // becomes an AST node and therefore participates in a
                        // caller's custom MaxNodes contract.
                        let mutable cursor = i + 1

                        let consumeIdentifier () =
                            recordNode ()
                            cursor <- cursor + 1

                            while
                                cursor < source.Length
                                && (System.Char.IsLetterOrDigit source.[cursor] || source.[cursor] = '_')
                                do
                                cursor <- cursor + 1

                        consumeIdentifier ()

                        while
                            err.IsNone
                            && cursor + 1 < source.Length
                            && source.[cursor] = '.'
                            && (System.Char.IsLetter source.[cursor + 1] || source.[cursor + 1] = '_')
                            do
                            cursor <- cursor + 1
                            consumeIdentifier ()

                        let consumedAfterDollar = cursor - i - 1
                        i <- cursor - 1
                        column <- column + int64 consumedAfterDollar
                    else
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
                            returnFallbackPending <- false
                            returnCommandHead <- false
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
                        returnFallbackPending <- false
                        returnCommandHead <- false
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
                             && (needsOperand
                                 || statementHead
                                 || commandHead
                                 || (returnCommandHead && not (nextTokenCanStartUnary slashyClosers.[i]))) ->
                        // Comments won above. A complete candidate in an
                        // operand or command-argument position shields every
                        // quote and delimiter through its cached closing index. If no
                        // closing slash exists this arm is not entered: the slash is
                        // processed as ordinary code and all fallback
                        // node/depth accounting remains visible.
                        recordNode ()

                        if err.IsNone then
                            let closingIndex = slashyClosers.[i]
                            recognizedSlashyClosers.[i] <- closingIndex
                            column <- column + int64 (closingIndex - i)
                            i <- closingIndex
                            needsOperand <- false
                            statementHead <- false
                            commandHead <- false
                            returnFallbackPending <- false
                            returnCommandHead <- false
                            sawLineBreak <- false
                    | '/' when needsOperand || statementHead || commandHead ->
                        // Preserve the DFA decision even without a same-line
                        // closer. The precheck itself must leave all following
                        // structure visible, while balancedRaw must know this
                        // was an operand-position slashy candidate so crossing
                        // a physical ending becomes its semantic refusal.
                        recognizedSlashyClosers.[i] <- -2
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '{' | '[' | '(' ->
                        let opensInterpolation =
                            match pendingInterpolation with
                            | Some context when c = '{' ->
                                interpolationQuotes.Push context
                                pendingInterpolation <- None
                                true
                            | _ -> false

                        depth <- depth + 1
                        recordNode ()
                        returnFallbackPending <- false
                        returnCommandHead <- false

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
                            // `${...}` is an expression group, not a statement
                            // block. In particular, its grammar does not admit
                            // command-form slashy arguments: `${amount / x /
                            // 2}` is division even though the first identifier
                            // visually resembles a command head. Ordinary
                            // braces still begin a statement body or literal.
                            needsOperand <- true
                            statementHead <- not opensInterpolation
                            commandHead <- false
                            sawLineBreak <- false
                    | '}' | ']' | ')' ->
                        depth <- depth - 1
                        recordNode ()

                        match caseContext with
                        | Some(caseDepth, _, _) when depth < caseDepth -> caseContext <- None
                        | _ -> ()

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
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false

                        if c = '}' && interpolationQuotes.Count > 0 then
                            let outerQuote, outerDelimiterLength, outerScalarStart, outerDepth =
                                interpolationQuotes.Peek()

                            if depth = outerDepth then
                                interpolationQuotes.Pop() |> ignore
                                quote <- outerQuote
                                delimiterLength <- outerDelimiterLength
                                scalarContentStart <- outerScalarStart
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
                            let followsReturn = returnFallbackPending
                            returnFallbackPending <- false
                            returnCommandHead <- false

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

                                if word = "return" then returnFallbackPending <- true

                                if word = "case" then
                                    caseContext <- Some(depth, expressionNesting, 0)
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

                                let returnCrossedLine = followsReturn && sawLineBreak

                                commandHead <-
                                    identifierHead && (beginsNewStatement || returnCrossedLine)

                                returnCommandHead <-
                                    followsReturn && identifierHead && not returnCrossedLine
                                needsOperand <- false
                                statementHead <- false

                            sawLineBreak <- false
                    | ';' ->
                        needsOperand <- true
                        statementHead <- true
                        commandHead <- false
                        awaitingControlParen <- false
                        caseContext <- None
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '+' | '-' when i + 1 < source.Length && source.[i + 1] = c && not needsOperand ->
                        // Postfix increment/decrement leaves a complete value.
                        i <- i + 1
                        column <- column + 1L
                        needsOperand <- false
                        statementHead <- false
                        commandHead <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '-' when i + 1 < source.Length && source.[i + 1] = '>' ->
                        // Closure parameters and modern switch cases hand off
                        // to a statement body. Only the adjacent arrow has this
                        // meaning; ordinary subtraction/comparison remains an
                        // expression and cannot promote a following command.
                        i <- i + 1
                        column <- column + 1L
                        needsOperand <- true
                        statementHead <- true
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '?' ->
                        let safeNavigation = i + 1 < source.Length && source.[i + 1] = '.'

                        match caseContext with
                        | Some(caseDepth, caseExpressionNesting, ternaryDepth)
                            when depth = caseDepth
                                 && expressionNesting = caseExpressionNesting
                                 && not safeNavigation ->
                            caseContext <- Some(caseDepth, caseExpressionNesting, ternaryDepth + 1)
                        | _ -> ()

                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | ':' ->
                        let caseTerminator, closesCaseTernary =
                            match caseContext with
                            | Some(caseDepth, caseExpressionNesting, ternaryDepth)
                                when depth = caseDepth && expressionNesting = caseExpressionNesting ->
                                if ternaryDepth > 0 then
                                    caseContext <- Some(caseDepth, caseExpressionNesting, ternaryDepth - 1)
                                    false, true
                                else
                                    true, false
                            | _ -> false, false

                        let beginsLabeledBody =
                            not closesCaseTernary && (commandHead || caseTerminator)

                        needsOperand <- true
                        statementHead <- beginsLabeledBody
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false

                        if caseTerminator then caseContext <- None

                        sawLineBreak <- false
                    | '/' | '=' | '+' | '-' | '*' | '%' | '&' | '|' | '^' | '!' | '~'
                    | '<' | '>' | ',' | '.' ->
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | _ ->
                        // Unknown punctuation is deliberately not promoted to
                        // an expression ender. If the grammar accepts it, a
                        // later explicit token will establish slash context;
                        // if it does not, the parser will fail closed.
                        commandHead <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false

                i <- i + 1

            if err.IsNone then
                let mutable earliestOpenScalar =
                    if quote = '\000' then source.Length else scalarContentStart

                match pendingInterpolation with
                | Some(_, _, start, _) -> earliestOpenScalar <- min earliestOpenScalar start
                | None -> ()

                for _, _, start, _ in interpolationQuotes do
                    earliestOpenScalar <- min earliestOpenScalar start

                if earliestOpenScalar < source.Length then
                    let scalarBytes =
                        Encoding.UTF8.GetByteCount(source, earliestOpenScalar, source.Length - earliestOpenScalar)

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
            | None -> Ok(SlashySpans recognizedSlashyClosers)

    let precheck (limits: Limits) (source: string) : Result<unit, AdmissionError> =
        precheckWithSlashySpans limits source |> Result.map ignore
