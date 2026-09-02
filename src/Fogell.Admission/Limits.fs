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
            let mutable depth = 0
            let mutable maxDepth = 0
            let mutable nodes = 0
            let mutable line = 1L
            let mutable column = 1L
            let mutable scalarContentStart = -1
            let mutable quote = '\000'
            let mutable delimiterLength = 0
            let mutable lineComment = false
            let mutable blockComment = false
            let mutable err = None
            let mutable i = 0

            while err.IsNone && i < source.Length do
                let c = source.[i]

                if c = '\n' then
                    line <- line + 1L
                    column <- 1L
                else
                    column <- column + 1L

                if lineComment then
                    if c = '\n' then lineComment <- false
                elif blockComment then
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
                else
                    match c with
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
                        nodes <- nodes + 1

                        if delimiterLength = 3 then
                            i <- i + 2
                            column <- column + 2L
                    | '{' | '[' | '(' ->
                        depth <- depth + 1
                        nodes <- nodes + 1

                        if depth > maxDepth then maxDepth <- depth

                        if depth > limits.MaxDepth then
                            err <-
                                Some(
                                    AdmissionError.at
                                        NestingTooDeep
                                        line
                                        column
                                        $"nesting depth {depth} exceeds limit {limits.MaxDepth}"
                                )
                    | '}' | ']' | ')' ->
                        depth <- depth - 1
                        nodes <- nodes + 1
                    | c when System.Char.IsLetterOrDigit c || c = '_' ->
                        if i = 0 || not (System.Char.IsLetterOrDigit source.[i - 1] || source.[i - 1] = '_') then
                            nodes <- nodes + 1
                    | _ -> ()

                    if err.IsNone && nodes > limits.MaxNodes then
                        err <-
                            Some(
                                AdmissionError.at
                                    TooManyNodes
                                    line
                                    column
                                    $"node count exceeds {limits.MaxNodes}"
                            )

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

            match err with
            | Some e -> Error e
            | None -> Ok()
