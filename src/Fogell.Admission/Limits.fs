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
/// Complete and incomplete candidates both carry their already-computed
/// boundary, so parser consumers never need to rescan a source suffix.
[<Struct>]
type SlashySpanBoundary =
    | Unclassified
    | NonConsuming
    | Complete of ClosingIndex: int
    | Incomplete of PhysicalEndIndex: int

[<Sealed>]
type SlashySpans internal (boundaries: SlashySpanBoundary array, recoveryBoundaries: SlashySpanBoundary array) =
    static member Empty = SlashySpans(Array.empty, Array.empty)

    member _.Length = boundaries.Length

    member _.Boundary(index: int) =
        if index < 0 || index >= boundaries.Length then
            Unclassified
        else
            boundaries.[index]

    /// Complete slashies that admission can treat as authoritative after the
    /// grammar has failed before reaching them. Parser-only hints, including
    /// the ambiguous return-command fallback, are deliberately excluded.
    member _.RecoveryBoundary(index: int) =
        if index < 0 || index >= recoveryBoundaries.Length then
            Unclassified
        else
            recoveryBoundaries.[index]

    member _.Slice(start: int, length: int) =
        if length <= 0 then
            SlashySpans.Empty
        else
            let sliced =
                Array.init length (fun relativeIndex ->
                    let outerIndex = start + relativeIndex

                    if outerIndex < 0 || outerIndex >= boundaries.Length then
                        Unclassified
                    else
                        match boundaries.[outerIndex] with
                        | NonConsuming -> NonConsuming
                        | Complete outerCloser when outerCloser >= start && outerCloser < start + length ->
                            Complete(outerCloser - start)
                        | Complete outerCloser when outerCloser >= start + length -> Incomplete length
                        | Incomplete outerPhysicalEnd ->
                            Incomplete(min length (max 0 (outerPhysicalEnd - start)))
                        | _ -> Unclassified)

            let recoverySliced =
                Array.init length (fun relativeIndex ->
                    let outerIndex = start + relativeIndex

                    if outerIndex < 0 || outerIndex >= recoveryBoundaries.Length then
                        Unclassified
                    else
                        match recoveryBoundaries.[outerIndex] with
                        | Complete outerCloser when outerCloser >= start && outerCloser < start + length ->
                            Complete(outerCloser - start)
                        | Complete outerCloser when outerCloser >= start + length -> Incomplete length
                        | Incomplete outerPhysicalEnd ->
                            Incomplete(min length (max 0 (outerPhysicalEnd - start)))
                        | _ -> Unclassified)

            SlashySpans(sliced, recoverySliced)

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
    let private nextSlashyBoundaries (source: string) =
        let closers = Array.create source.Length -1
        let physicalEnds = Array.create source.Length source.Length
        let mutable nextCloser = -1
        let mutable physicalEnd = source.Length

        for i = source.Length - 1 downto 0 do
            if source.[i] = '\r' || source.[i] = '\n' then
                nextCloser <- -1
                physicalEnd <- i

            closers.[i] <- nextCloser
            physicalEnds.[i] <- physicalEnd

            if source.[i] = '/' && (i = 0 || source.[i - 1] <> '\\') then
                nextCloser <- i

        closers, physicalEnds

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
            let slashyClosers, physicalEnds = nextSlashyBoundaries source
            // Only the DFA-classified opener positions are exported. A
            // structural subscanner can then share this exact slashy/division
            // decision instead of growing a second, drifting lookbehind rule.
            let recognizedSlashyBoundaries = Array.create source.Length Unclassified
            let recoverySlashyBoundaries = Array.create source.Length Unclassified
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
            let mutable returnCommandHeadEnd = -1
            // Fogell.Groovy.Parser's unary production recursively invokes
            // itself once for every prefix `!` or `-`. Trivia does not break
            // that recursion (`! /* c */ - value`), so charge the consecutive
            // grammar chain here before untrusted source reaches the parser.
            // A completed primary or binary seam resets it to the inherited
            // outer chain; postfix `--` and `->` are handled by their earlier,
            // non-recursive branches below.
            let mutable unaryChainDepth = 0
            let mutable unaryFloor = 0
            let mutable postfixPrimary = false
            let unaryFrames = System.Collections.Generic.Stack<int * int>()
            // Every successful postfix suffix adds one nested AST receiver.
            // Keep that left spine under the same depth
            // contract as structural and unary recursion; downstream AST
            // walkers and the interpreter recurse through it too.
            let mutable postfixChainDepth = 0
            let mutable postfixFloor = 0
            let mutable postfixMemberStage = 0
            let mutable constructorPrimary = false
            let postfixFrames = System.Collections.Generic.Stack<int * int * int>()
            // Groovy permits a postfix index to cross a physical ending only
            // while an expression group owns the parse. Parentheses, lists and
            // `${...}` increment that ownership; a closure/statement body
            // temporarily resets it even when nested in an expression group.
            let mutable postfixGroupDepth = 0
            let postfixGroupFrames = System.Collections.Generic.Stack<int>()
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
                System.Collections.Generic.Stack<char * int * int * int * int>()

            let mutable pendingInterpolation: (char * int * int * int * int) option = None
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

            let recordUnaryOperator () =
                unaryChainDepth <- unaryChainDepth + 1
                // Each recursive unary production becomes one EUnary node.
                recordNode ()

                let grammarDepth = depth + unaryChainDepth + postfixChainDepth

                if err.IsNone && grammarDepth > limits.MaxDepth then
                    err <-
                        Some(
                            AdmissionError.at
                                NestingTooDeep
                                line
                                column
                                $"grammar depth {grammarDepth} exceeds limit {limits.MaxDepth}"
                        )

            let recordPostfixStep () =
                postfixChainDepth <- postfixChainDepth + 1

                let grammarDepth = depth + unaryChainDepth + postfixChainDepth

                if err.IsNone && grammarDepth > limits.MaxDepth then
                    err <-
                        Some(
                            AdmissionError.at
                                NestingTooDeep
                                line
                                column
                                $"grammar depth {grammarDepth} exceeds limit {limits.MaxDepth}"
                        )

            let resetUnaryChain () =
                // Unary frames outside the current structural primary remain
                // active while its nested expression is parsed.
                unaryChainDepth <- unaryFloor
                postfixChainDepth <- postfixFloor
                postfixMemberStage <- 0
                constructorPrimary <- false
                postfixPrimary <- false

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
                        pendingInterpolation <-
                            Some(quote, delimiterLength, scalarContentStart, depth, unaryChainDepth)
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
                        let mutable shorthandPostfixDepth = 0

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
                            shorthandPostfixDepth <- shorthandPostfixDepth + 1

                            let grammarDepth =
                                depth
                                + unaryChainDepth
                                + postfixChainDepth
                                + shorthandPostfixDepth

                            if err.IsNone && grammarDepth > limits.MaxDepth then
                                err <-
                                    Some(
                                        AdmissionError.at
                                            NestingTooDeep
                                            line
                                            (column + int64 (cursor - i))
                                            $"grammar depth {grammarDepth} exceeds limit {limits.MaxDepth}"
                                    )

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
                            postfixPrimary <- true
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
                        if not needsOperand then resetUnaryChain ()

                        postfixPrimary <- false
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
                        when i > 0
                             && source.[i - 1] = '\\' ->
                        // Raw/balanced parser consumers need the cached hint
                        // even when malformed source presents an escaped slash
                        // where an opener would otherwise be expected. It is
                        // not an authoritative Groovy opener, so keep walking
                        // the DFA instead of shielding through its hint. This
                        // reconstructs operand context at the cached delimiter:
                        // `\\/ /x/` reaches a real slashy, while
                        // `\\/ amount / x / 2` keeps both later slashes as
                        // division. The parser table retains its O(1) hint.
                        recognizedSlashyBoundaries.[i] <- NonConsuming
                    | '/'
                        when slashyClosers.[i] >= 0
                             && returnCommandHead
                             && not needsOperand
                             && not statementHead
                             && not commandHead
                             && not (nextTokenCanStartUnary slashyClosers.[i]) ->
                        // `return tool /x/;` ultimately falls back to the
                        // command statement `tool /x/`, so balancedRaw still
                        // needs this slashy boundary. Before that fallback,
                        // however, returnStmt speculatively parses
                        // `tool / x /` as division. Do not jump to the cached
                        // cached delimiter here: every group and node the recursive
                        // division parse can visit must remain visible to the
                        // admission limits.
                        resetUnaryChain ()
                        recordNode ()

                        if err.IsNone then
                            recognizedSlashyBoundaries.[i] <- Complete slashyClosers.[i]

                            // With same-line parser trivia between the
                            // return-command head and slash, the Groovy grammar
                            // abandons speculative division and parses the
                            // following command call's slashy argument. The
                            // scanner already preserves returnCommandHead over
                            // spaces, tabs and block comments, so the source
                            // gap covers all of that trivia without rescanning.
                            // Without a gap (`tool/x/+1`) the same characters
                            // remain a division chain. A physical break cannot
                            // continue the command form.
                            if i > returnCommandHeadEnd + 1 && not sawLineBreak then
                                recoverySlashyBoundaries.[i] <- Complete slashyClosers.[i]

                            needsOperand <- true
                            statementHead <- false
                            commandHead <- false
                            awaitingControlParen <- false
                            returnFallbackPending <- false
                            returnCommandHead <- false
                            postfixPrimary <- false
                            sawLineBreak <- false
                    | '/'
                        when slashyClosers.[i] >= 0
                             && (needsOperand || statementHead || commandHead) ->
                        // Comments won above. A complete candidate in an
                        // operand or command-argument position shields every
                        // quote and delimiter through its cached closing index. If no
                        // closing slash exists this arm is not entered: the slash is
                        // processed as ordinary code and all fallback
                        // node/depth accounting remains visible.
                        if not needsOperand then resetUnaryChain ()

                        recordNode ()

                        if err.IsNone then
                            let closingIndex = slashyClosers.[i]
                            recognizedSlashyBoundaries.[i] <- Complete closingIndex
                            recoverySlashyBoundaries.[i] <- Complete closingIndex

                            column <- column + int64 (closingIndex - i)
                            i <- closingIndex
                            needsOperand <- false
                            statementHead <- false
                            commandHead <- false
                            returnFallbackPending <- false
                            returnCommandHead <- false
                            postfixPrimary <- true
                            sawLineBreak <- false
                    | '/' when needsOperand || statementHead || commandHead ->
                        // Preserve the DFA decision even without a same-line
                        // closing delimiter. The precheck itself must leave all following
                        // structure visible, while balancedRaw must know this
                        // was an operand-position slashy candidate so crossing
                        // a physical ending becomes its semantic refusal.
                        recognizedSlashyBoundaries.[i] <- Incomplete physicalEnds.[i]
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        resetUnaryChain ()
                        sawLineBreak <- false
                    | '{' | '[' | '(' ->
                        let opensInterpolation =
                            match pendingInterpolation with
                            | Some context when c = '{' ->
                                interpolationQuotes.Push context
                                pendingInterpolation <- None
                                true
                            | _ -> false

                        // A postfix index cannot cross a physical ending in
                        // top-level/statement Groovy grammar. It can continue
                        // inside an expression-bearing group, and calls and
                        // trailing closures can cross regardless.
                        if
                            c = '['
                            && postfixPrimary
                            && sawLineBreak
                            && postfixGroupDepth = 0
                        then
                            resetUnaryChain ()
                        elif not needsOperand && not postfixPrimary then
                            resetUnaryChain ()

                        // Plain and safe member steps parse their optional
                        // arguments and trailing closure inside the same
                        // postfix-loop invocation. A bare call/index/closure,
                        // or a call after spread-property, is a new step.
                        let memberOwnsOpener =
                            postfixPrimary
                            && not needsOperand
                            && ((c = '(' && postfixMemberStage = 1)
                                || (c = '{' && postfixMemberStage > 0))

                        let constructorOwnsArgs =
                            constructorPrimary
                            && postfixPrimary
                            && not needsOperand
                            && c = '('

                        let isPostfixOpener =
                            postfixPrimary
                            && not needsOperand
                            && not memberOwnsOpener
                            && not constructorOwnsArgs

                        if isPostfixOpener then recordPostfixStep ()

                        let memberStageAfterClose =
                            if memberOwnsOpener && c = '(' then
                                2 // the same member step may still own a trailing closure
                            elif isPostfixOpener && c = '(' then
                                2 // a bare call step also owns its optional trailing closure
                            else
                                0

                        unaryFrames.Push(unaryFloor, unaryChainDepth)
                        unaryFloor <- unaryChainDepth
                        postfixPrimary <- false
                        postfixFrames.Push(postfixFloor, postfixChainDepth, memberStageAfterClose)
                        postfixFloor <- postfixChainDepth
                        postfixMemberStage <- 0
                        constructorPrimary <- false
                        postfixGroupFrames.Push postfixGroupDepth

                        postfixGroupDepth <-
                            if c = '(' || c = '[' || opensInterpolation then
                                postfixGroupDepth + 1
                            else
                                0

                        depth <- depth + 1
                        recordNode ()
                        returnFallbackPending <- false
                        returnCommandHead <- false

                        let grammarDepth = depth + unaryChainDepth + postfixChainDepth

                        if grammarDepth > limits.MaxDepth then
                            let message =
                                if unaryChainDepth = 0 && postfixChainDepth = 0 then
                                    $"nesting depth {depth} exceeds limit {limits.MaxDepth}"
                                else
                                    $"grammar depth {grammarDepth} exceeds limit {limits.MaxDepth}"

                            err <-
                                Some(
                                    AdmissionError.at
                                        NestingTooDeep
                                        line
                                        column
                                        message
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

                        let parentUnaryFloor, inheritedUnaryChain =
                            if unaryFrames.Count = 0 then 0, 0 else unaryFrames.Pop()

                        unaryFloor <- parentUnaryFloor
                        unaryChainDepth <- inheritedUnaryChain
                        postfixPrimary <- true

                        let parentPostfixFloor, inheritedPostfixChain, restoredMemberStage =
                            if postfixFrames.Count = 0 then
                                0, 0, 0
                            else
                                postfixFrames.Pop()

                        postfixFloor <- parentPostfixFloor
                        postfixChainDepth <- inheritedPostfixChain
                        postfixMemberStage <- restoredMemberStage

                        postfixGroupDepth <-
                            if postfixGroupFrames.Count = 0 then
                                0
                            else
                                postfixGroupFrames.Pop()

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
                            let outerQuote, outerDelimiterLength, outerScalarStart, outerDepth, outerUnaryDepth =
                                interpolationQuotes.Peek()

                            if depth = outerDepth then
                                interpolationQuotes.Pop() |> ignore
                                quote <- outerQuote
                                delimiterLength <- outerDelimiterLength
                                scalarContentStart <- outerScalarStart
                                // `${...}` completed, but the surrounding
                                // GString primary has not. Keep unary frames
                                // that wrap that literal active until its quote.
                                unaryChainDepth <- outerUnaryDepth
                    | c when System.Char.IsLetterOrDigit c || c = '_' ->
                        let startsAsOperand = needsOperand

                        if not startsAsOperand then resetUnaryChain ()

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
                                resetUnaryChain ()
                                awaitingControlParen <- true
                                needsOperand <- true
                                statementHead <- false
                                commandHead <- false
                            | "return" | "throw" | "case" | "in" ->
                                resetUnaryChain ()
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
                                resetUnaryChain ()
                                // An unbraced else body begins a statement just
                                // like a completed control header.
                                awaitingControlParen <- false
                                needsOperand <- true
                                statementHead <- true
                                commandHead <- false
                            | "new" ->
                                // Constructor syntax is one primary whose
                                // identifier and optional call arguments are
                                // parsed after this keyword.
                                constructorPrimary <- true
                                postfixPrimary <- false
                                awaitingControlParen <- false
                                needsOperand <- true
                                statementHead <- false
                                commandHead <- false
                            | "def" | "final" | "instanceof" | "as" ->
                                resetUnaryChain ()
                                awaitingControlParen <- false
                                needsOperand <- true
                                statementHead <- false
                                commandHead <- false
                            | "true" | "false" | "null" ->
                                postfixPrimary <- true
                                awaitingControlParen <- false
                                needsOperand <- false
                                statementHead <- false
                                commandHead <- false
                            | _ ->
                                postfixPrimary <- true
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

                                if returnCommandHead then returnCommandHeadEnd <- i
                                needsOperand <- false
                                statementHead <- false

                            sawLineBreak <- false
                    | ';' ->
                        resetUnaryChain ()
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
                        postfixPrimary <- true
                        needsOperand <- false
                        statementHead <- false
                        commandHead <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '-' when i + 1 < source.Length && source.[i + 1] = '>' ->
                        resetUnaryChain ()
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
                    | ('!' | '-') when needsOperand ->
                        postfixPrimary <- false
                        recordUnaryOperator ()
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '?' when postfixPrimary && i + 1 < source.Length && source.[i + 1] = '.' ->
                        // Safe navigation extends the completed primary; its
                        // member/call suffix is still parsed under outer unary
                        // frames.
                        recordPostfixStep ()
                        constructorPrimary <- false
                        i <- i + 1
                        column <- column + 1L
                        postfixMemberStage <- 1
                        postfixPrimary <- false
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '*' when postfixPrimary && i + 1 < source.Length && source.[i + 1] = '.' ->
                        // Spread-dot is one postfix continuation token.
                        recordPostfixStep ()
                        constructorPrimary <- false
                        i <- i + 1
                        column <- column + 1L
                        postfixMemberStage <- 0
                        postfixPrimary <- false
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '.' when postfixPrimary && (i + 1 >= source.Length || source.[i + 1] <> '.') ->
                        recordPostfixStep ()
                        constructorPrimary <- false
                        postfixMemberStage <- 1
                        postfixPrimary <- false
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | '?' ->
                        resetUnaryChain ()
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
                        resetUnaryChain ()
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
                        resetUnaryChain ()
                        needsOperand <- true
                        statementHead <- false
                        commandHead <- false
                        awaitingControlParen <- false
                        returnFallbackPending <- false
                        returnCommandHead <- false
                        sawLineBreak <- false
                    | _ ->
                        resetUnaryChain ()
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
                | Some(_, _, start, _, _) -> earliestOpenScalar <- min earliestOpenScalar start
                | None -> ()

                for _, _, start, _, _ in interpolationQuotes do
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
            | None -> Ok(SlashySpans(recognizedSlashyBoundaries, recoverySlashyBoundaries))

    /// On a grammar failure, recover only the scalar fact already classified
    /// by the admission DFA. Successful parses remain authoritative for
    /// slashy-versus-division; this fallback prevents an earlier unsupported
    /// token from hiding a later, complete over-limit slashy span.
    let firstOverlongClassifiedSlashy
        (limits: Limits)
        (source: string)
        (spans: SlashySpans)
        : AdmissionError option =
        if isNull source || spans.Length <> source.Length then
            None
        else
            let mutable opener = 0
            let mutable overlong: (int * int) option = None

            while overlong.IsNone && opener < source.Length do
                match spans.RecoveryBoundary opener with
                | Complete closingIndex when closingIndex > opener && closingIndex < source.Length ->
                    let contentBytes =
                        Encoding.UTF8.GetByteCount(source, opener + 1, closingIndex - opener - 1)

                    if contentBytes > limits.MaxScalarBytes then
                        overlong <- Some(closingIndex, contentBytes)

                    opener <- closingIndex + 1
                | _ -> opener <- opener + 1

            match overlong with
            | None -> None
            | Some(closingIndex, _) ->
                let mutable line = 1L
                let mutable column = 1L

                for index in 0 .. closingIndex do
                    match source.[index] with
                    | '\r' ->
                        line <- line + 1L
                        column <- 1L
                    | '\n' ->
                        if index = 0 || source.[index - 1] <> '\r' then line <- line + 1L

                        column <- 1L
                    | _ -> column <- column + 1L

                Some(
                    AdmissionError.at
                        ScalarTooLong
                        line
                        column
                        $"string literal exceeds {limits.MaxScalarBytes} UTF-8 bytes"
                )

    let precheck (limits: Limits) (source: string) : Result<unit, AdmissionError> =
        precheckWithSlashySpans limits source |> Result.map ignore
