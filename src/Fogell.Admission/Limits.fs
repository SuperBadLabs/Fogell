namespace Fogell.Admission

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

    /// Cheap pre-parse guard. Counts brace depth and token-ish nodes with a
    /// single linear scan — no recursion, so this itself cannot overflow. It is
    /// intentionally approximate: its job is to make the *parser* safe to run,
    /// not to be a second grammar.
    let precheck (limits: Limits) (source: string) : Result<unit, AdmissionError> =
        if System.String.IsNullOrWhiteSpace source then
            Error(AdmissionError.at EmptySource 1L 1L "source is empty")
        elif source.Length > limits.MaxSourceBytes then
            Error(
                AdmissionError.at
                    SourceTooLarge
                    1L
                    1L
                    $"source is {source.Length} bytes, limit is {limits.MaxSourceBytes}"
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
                    if c = quote && (i = 0 || source.[i - 1] <> '\\') then
                        if i - scalarStart > limits.MaxScalarBytes then
                            err <-
                                Some(
                                    AdmissionError.at
                                        ScalarTooLong
                                        line
                                        column
                                        $"string literal exceeds {limits.MaxScalarBytes} bytes"
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

            match err with
            | Some e -> Error e
            | None -> Ok()
