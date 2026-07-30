namespace Fogell.Admission

open Fogell.Ir

/// Every rejection is a named code. The charter forbids "unsupported" without
/// a name, because a migration report needs a machine-readable reason and a
/// human-readable position. Codes are stable API: renaming one is a breaking
/// change to anyone consuming the ledger.
type ErrorCode =
    // --- admission limits (FG-004) ---
    | SourceTooLarge
    | TooManyNodes
    | NestingTooDeep
    | ScalarTooLong
    | TooManyCollectionItems
    // --- structural ---
    | EmptySource
    | NoPipelineBlock
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
        | NoPipelineBlock -> "no_pipeline_block"
        | NoStages -> "no_stages"
        | ExpectedStage -> "expected_stage"
        | ExpectedSteps -> "expected_steps"
        | DuplicateSection -> "duplicate_section"
        | UnknownSection -> "unknown_section"
        | MalformedSyntax -> "malformed_syntax"
        | UnsupportedConstruct -> "unsupported_construct"

    /// True when the input is bad, false when Fogell simply does not support it.
    /// The distinction matters for the compatibility tiers in ADR 0001: a
    /// malformed file is tier-3 forever; an unsupported construct is a backlog
    /// item.
    let isInputDefect =
        function
        | EmptySource
        | NoPipelineBlock
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

    let create code position message =
        { Code = code; Message = message; Position = position }

    let at code line column message =
        { Code = code
          Message = message
          Position = { Line = line; Column = column } }
