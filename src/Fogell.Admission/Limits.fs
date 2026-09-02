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
    /// not to be a second grammar.
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
            let mutable scalarStart = -1
            let mutable quote = '\000'
            let mutable err = None
            let mutable i = 0

            while err.IsNone && i < source.Length do
                let c = source.[i]

                if c = '\n' then
                    line <- line + 1L
                    column <- 1L
                else
                    column <- column + 1L

                if quote <> '\000' then
                    // inside a string literal: only look for its terminator
                    if c = quote && not (quoteIsEscaped source i) then
                        let scalarBytes =
                            Encoding.UTF8.GetByteCount(source, scalarStart + 1, i - scalarStart - 1)

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
                        scalarStart <- -1
                else
                    match c with
                    | '\'' | '"' ->
                        quote <- c
                        scalarStart <- i
                        nodes <- nodes + 1
                    | '{' | '[' | '(' ->
                        depth <- depth + 1
                        nodes <- nodes + 1

                        if depth > maxDepth then
                            maxDepth <- depth

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
                        // count identifier/number starts only, not every char
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
                    Encoding.UTF8.GetByteCount(source, scalarStart + 1, source.Length - scalarStart - 1)

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
