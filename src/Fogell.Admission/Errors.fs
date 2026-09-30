namespace Fogell.Admission

open Fogell.Ir

/// Stable machine-readable native admission errors with source locations.
type ErrorCode =
    // --- admission limits (FG-004) ---
    | SourceTooLarge
    | TooManyNodes
    | NestingTooDeep
    | ScalarTooLong
    | TooManyCollectionItems
    // --- structural ---
    | EmptySource
    | NoStages
    | ExpectedStage
    | ExpectedSteps
    | DuplicateSection
    | UnknownSection
    | MalformedSyntax
    // --- deliberately unsupported ---
    | UnsupportedConstruct

module ErrorCode =

    let toWireString =
        function
        | SourceTooLarge -> "source_too_large"
        | TooManyNodes -> "too_many_nodes"
        | NestingTooDeep -> "nesting_too_deep"
        | ScalarTooLong -> "scalar_too_long"
        | TooManyCollectionItems -> "too_many_collection_items"
        | EmptySource -> "empty_source"
        | NoStages -> "no_stages"
        | ExpectedStage -> "expected_stage"
        | ExpectedSteps -> "expected_steps"
        | DuplicateSection -> "duplicate_section"
        | UnknownSection -> "unknown_section"
        | MalformedSyntax -> "malformed_syntax"
        | UnsupportedConstruct -> "unsupported_construct"

    let isInputDefect =
        function
        | EmptySource
        | NoStages
        | ExpectedStage
        | ExpectedSteps
        | DuplicateSection
        | MalformedSyntax -> true
        | SourceTooLarge
        | TooManyNodes
        | NestingTooDeep
        | ScalarTooLong
        | TooManyCollectionItems
        | UnknownSection
        | UnsupportedConstruct -> false

type AdmissionError =
    { Code: ErrorCode
      Message: string
      Position: Position }

    override this.ToString() =
        $"{ErrorCode.toWireString this.Code} at {this.Position}: {this.Message}"

module AdmissionError =

    [<Literal>]
    let private MaxExcerptWidth = 160

    let private tryPhysicalLineBounds (source: string) (requestedLine: int64) =
        if isNull source || requestedLine < 1L then
            None
        else
            let mutable line = 1L
            let mutable start = 0
            let mutable index = 0
            let mutable found = None

            while index < source.Length && Option.isNone found do
                match source.[index] with
                | '\r'
                | '\n' ->
                    if line = requestedLine then
                        found <- Some(start, index - start)
                    else
                        if source.[index] = '\r' && index + 1 < source.Length && source.[index + 1] = '\n' then
                            index <- index + 1

                        line <- line + 1L
                        start <- index + 1
                | _ -> ()

                index <- index + 1

            if Option.isSome found then
                found
            elif line = requestedLine then
                // The empty string and a source ending in a newline both have a
                // real, empty physical line at this position.
                Some(start, source.Length - start)
            else
                None

    let private clipLine (source: string) lineStart lineLength caretIndex =
        if lineLength <= MaxExcerptWidth then
            source.Substring(lineStart, lineLength), 0
        else
            // Reserve room for both possible ellipses. Keep an ordinary
            // character under the caret; an EOF caret keeps the right edge.
            let coreWidth = MaxExcerptWidth - 2
            let anchor = if caretIndex = lineLength then caretIndex else min caretIndex (lineLength - 1)
            let centered = max 0 (anchor - coreWidth / 2)
            let start = min centered (lineLength - coreWidth)
            let finish = start + coreWidth
            let prefix = if start > 0 then "…" else ""
            let suffix = if finish < lineLength then "…" else ""
            prefix + source.Substring(lineStart + start, coreWidth) + suffix, start

    let private caretPrefix (source: string) lineStart start caretIndex hasPrefix =
        let sourcePrefix =
            if caretIndex <= start then
                ""
            else
                String.init
                    (caretIndex - start)
                    (fun offset -> if source.[lineStart + start + offset] = '\t' then "\t" else " ")

        (if hasPrefix then " " else "") + sourcePrefix

    /// Render one bounded, source-aware admission diagnostic. The record's
    /// ToString() remains the stable source-free form; callers opt in only when
    /// they own the exact source bytes that produced this error.
    let render (source: string) (error: AdmissionError) =
        let header = string error

        match tryPhysicalLineBounds source error.Position.Line with
        | None -> $"{header}\n<source line unavailable>\n^"
        | Some(lineStart, lineLength) ->
            let caretIndex =
                if error.Position.Column <= 1L then
                    0
                elif error.Position.Column - 1L >= int64 lineLength then
                    lineLength
                else
                    int (error.Position.Column - 1L)

            let excerpt, start = clipLine source lineStart lineLength caretIndex
            let prefix = caretPrefix source lineStart start caretIndex (start > 0)
            $"{header}\n{excerpt}\n{prefix}^"

    let create code position message =
        { Code = code; Message = message; Position = position }

    let at code line column message =
        { Code = code
          Message = message
          Position = { Line = line; Column = column } }
