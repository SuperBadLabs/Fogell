module Fogell.Pipeline.Parser.Tests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open Fogell.Ir
open Fogell.Admission
open Fogell.Pipeline.Parser

let private mk (stages: string) =
    $"pipeline {{\n  agent any\n  stages {{\n{stages}\n  }}\n}}\n"

let private ok source =
    match Parser.parse source with
    | Ok p -> p
    | Error e -> failtestf "expected a parse, got %s" (string e)

let private err source =
    match Parser.parse source with
    | Ok _ -> failtest "expected a rejection"
    | Error e -> e

let private expectDuplicateWhen key body =
    let e = err (mk $"    stage('B') {{ when {{ {body} }}\n steps {{ sh 'x' }} }}")
    Expect.equal e.Code MalformedSyntax "a named admission refusal"
    Expect.stringContains e.Message $"duplicate named argument `{key}`" "the duplicated key is preserved across parser backtracking"
    Expect.isGreaterThan e.Position.Line 0L "the refusal has a source position"

type private FuzzXorShift64(initialState: uint64) =
    let mutable state = initialState

    member _.NextUInt64() =
        state <- state ^^^ (state <<< 13)
        state <- state ^^^ (state >>> 7)
        state <- state ^^^ (state <<< 17)
        state

    member this.NextInt(exclusiveMaximum: int) =
        int (this.NextUInt64() % uint64 exclusiveMaximum)

type private MalformedFuzzCase =
    { Label: string
      Family: string
      Source: string
      Expected: ErrorCode * int64 * int64 }

/// FG-004: bounds are applied BEFORE the recursive grammar, so hostile input
/// can never reach it. Each limit has a named code and a position.
let admissionLimits =
    testList
        "FG-004 admission limits"
        [ test "empty source is named, not a crash" {
              Expect.equal (err "").Code EmptySource "empty_source"
          }

          test "oversized source is rejected before parsing" {
              let big = String.replicate 300_000 "x"
              Expect.equal (err big).Code SourceTooLarge "source_too_large"
          }

          test "deep nesting is rejected before the grammar recurses" {
              let deep = String.replicate 200 "{" + String.replicate 200 "}"
              let e = err ("pipeline " + deep)
              Expect.equal e.Code NestingTooDeep "nesting_too_deep"
              Expect.isGreaterThan e.Position.Column 0L "carries a position"
          }

          test "an overlong scalar is rejected" {
              let scalar = "'" + String.replicate 20_000 "a" + "'"
              Expect.equal (err $"pipeline {{ agent {scalar} }}").Code ScalarTooLong "scalar_too_long"
          }

          test "a pathological brace bomb terminates and is named" {
              // 40k braces: the precheck is a single linear scan, so this must
              // return a verdict rather than exhaust the stack.
              let bomb = String.replicate 40_000 "{"
              let e = err ("pipeline " + bomb)
              Expect.isTrue
                  (e.Code = NestingTooDeep || e.Code = TooManyNodes || e.Code = SourceTooLarge)
                  $"expected a bound to fire, got {ErrorCode.toWireString e.Code}"
          }

          test "every limit code is classified as input-defect or not" {
              // guards against a new code being added without a tier decision
              for c in
                  [ SourceTooLarge; TooManyNodes; NestingTooDeep; ScalarTooLong; TooManyCollectionItems ] do
                  Expect.isFalse (ErrorCode.isInputDefect c) "a limit is a Fogell bound, not a user defect"
          } ]

let private expectedWireName =
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

/// FG-016b. A rejection is useful to a human only when its stable code and
/// position lead back to the exact source line. These goldens deliberately
/// enumerate the union independently from ErrorCode.toWireString: a new code
/// without a rendering decision is a compile-time failure under FS0025.
let sourceExcerpts =
    let allCodes =
        [ SourceTooLarge
          TooManyNodes
          NestingTooDeep
          ScalarTooLong
          TooManyCollectionItems
          EmptySource
          NoPipelineBlock
          NoStages
          ExpectedStage
          ExpectedSteps
          DuplicateSection
          UnknownSection
          MalformedSyntax
          UnsupportedConstruct ]

    let diagnostic code line column message source =
        AdmissionError.render source (AdmissionError.at code line column message)

    testList
        "FG-016b source excerpts"
        [ for code in allCodes do
              let wire = expectedWireName code

              test $"{wire} has an exact source diagnostic" {
                  let actual = diagnostic code 1L 3L "sample rejection" "  broken"

                  Expect.equal
                      actual
                      $"{wire} at 1:3: sample rejection\n  broken\n  ^"
                      "code, position, physical line and caret are stable"
              }

          test "the source-free ToString contract is unchanged" {
              let e = AdmissionError.at MalformedSyntax 7L 9L "bad token"
              Expect.equal (string e) "malformed_syntax at 7:9: bad token" "legacy diagnostic bytes"
          }

          test "LF, CRLF and lone CR select the same tabbed physical line" {
              let expected = "malformed_syntax at 2:2: bad token\n\tbad\n\t^"

              for source in [ "first\n\tbad\nlast"; "first\r\n\tbad\r\nlast"; "first\r\tbad\rlast" ] do
                  Expect.equal (diagnostic MalformedSyntax 2L 2L "bad token" source) expected "newline form"
          }

          test "trailing space and tab remain exact before CRLF" {
              Expect.equal
                  (diagnostic MalformedSyntax 1L 7L "trailing whitespace" "\tbad \t\r\nnext")
                  "malformed_syntax at 1:7: trailing whitespace\n\tbad \t\n\t    \t^"
                  "the physical line and EOF caret preserve both trailing bytes"
          }

          test "empty source and an empty final line retain a visible caret" {
              Expect.equal
                  (diagnostic EmptySource 1L 1L "source is empty" "")
                  "empty_source at 1:1: source is empty\n\n^"
                  "the empty first line is real"

              Expect.equal
                  (diagnostic MalformedSyntax 2L 1L "at eof" "a\n")
                  "malformed_syntax at 2:1: at eof\n\n^"
                  "a trailing newline creates an empty EOF line"
          }

          test "invalid line and column positions are total and explicit" {
              Expect.equal
                  (diagnostic MalformedSyntax 1L 0L "bad column" "abc")
                  "malformed_syntax at 1:0: bad column\nabc\n^"
                  "a non-positive column clamps to the start of a real line"

              Expect.equal
                  (diagnostic MalformedSyntax 0L 0L "bad position" "abc")
                  "malformed_syntax at 0:0: bad position\n<source line unavailable>\n^"
                  "a non-positive line is not silently redirected"

              Expect.equal
                  (diagnostic MalformedSyntax 99L Int64.MaxValue "bad position" "abc")
                  "malformed_syntax at 99:9223372036854775807: bad position\n<source line unavailable>\n^"
                  "an absent line cannot throw"

              Expect.equal
                  (AdmissionError.render null (AdmissionError.at MalformedSyntax 1L 1L "missing source"))
                  "malformed_syntax at 1:1: missing source\n<source line unavailable>\n^"
                  "a defensive null source cannot throw"
          }

          test "columns at and beyond EOF clamp to the physical line end" {
              let expected = "malformed_syntax at 1:4: at eof\nabc\n   ^"
              Expect.equal (diagnostic MalformedSyntax 1L 4L "at eof" "abc") expected "exact EOF"

              Expect.equal
                  (diagnostic MalformedSyntax 1L Int64.MaxValue "at eof" "abc")
                  "malformed_syntax at 1:9223372036854775807: at eof\nabc\n   ^"
                  "a hostile column is bounded"
          }

          test "a long line is clipped around a middle caret" {
              let line = String.replicate 400 "x"
              let expectedLine = "…" + String.replicate 158 "x" + "…"
              let expectedCaret = String.replicate 80 " " + "^"

              Expect.equal
                  (diagnostic MalformedSyntax 1L 201L "middle" line)
                  $"malformed_syntax at 1:201: middle\n{expectedLine}\n{expectedCaret}"
                  "both ellipses and the caret stay in the bounded window"
          }

          test "a SourceTooLarge-sized line allocates only its displayed window" {
              let line = String.replicate 300_000 "x"
              let source = "prefix\n" + line + "\nsuffix"
              let error = AdmissionError.at SourceTooLarge 2L 150_001L "source is too large"
              let expectedLine = "…" + String.replicate 158 "x" + "…"
              let expectedCaret = String.replicate 80 " " + "^"

              // Warm the clipping path so this measures the renderer rather
              // than first-use JIT/type initialization. Per-thread allocation
              // excludes other concurrently running Expecto tests.
              AdmissionError.render ("prefix\n" + String.replicate 400 "x" + "\nsuffix") error |> ignore
              let before = GC.GetAllocatedBytesForCurrentThread()
              let actual = AdmissionError.render source error
              let allocated = GC.GetAllocatedBytesForCurrentThread() - before

              Expect.equal
                  actual
                  $"source_too_large at 2:150001: source is too large\n{expectedLine}\n{expectedCaret}"
                  "the hostile physical line still produces the exact bounded diagnostic"

              Expect.isLessThan
                  allocated
                  (64L * 1024L)
                  $"rendering must not copy the 300,000-character source line; allocated {allocated} bytes"
          }

          test "a line exactly at the bound is not clipped" {
              let line = String.replicate 160 "x"
              let caret = String.replicate 159 " " + "^"

              Expect.equal
                  (diagnostic MalformedSyntax 1L 160L "boundary" line)
                  $"malformed_syntax at 1:160: boundary\n{line}\n{caret}"
                  "the 160-character boundary remains literal"
          }

          test "long-line edge carets remain visible without false ellipses" {
              let line = String.replicate 400 "x"
              let clipped = String.replicate 158 "x"
              let rightCaret = String.replicate 159 " " + "^"

              Expect.equal
                  (diagnostic MalformedSyntax 1L 1L "left" line)
                  $"malformed_syntax at 1:1: left\n{clipped}…\n^"
                  "the left edge has no leading ellipsis"

              Expect.equal
                  (diagnostic MalformedSyntax 1L 401L "right" line)
                  $"malformed_syntax at 1:401: right\n…{clipped}\n{rightCaret}"
                  "the EOF edge has no trailing ellipsis"
          } ]

/// FG-004b. The fixed-seed generator is intentionally length-delimited and
/// every generated source is malformed by construction. This is a bounded
/// robustness sweep, not a grammar fuzzer claiming arbitrary coverage.
let malformedInputSweep =
    let seed = 0x46472D30303462UL
    let inputCount = 10_000

    let exact label family source code line column =
        { Label = label
          Family = family
          Source = source
          Expected = code, line, column }

    let depthSource prefix opener = prefix + String.replicate 65 opener

    let boundaries =
        let missingRootClose =
            "pipeline { agent any stages { stage('x') { steps { echo 'x' } } }"

        let escapedQuoteScalar =
            "'a\\'" + String.replicate Limits.defaults.MaxScalarBytes "b" + "'"

        [ exact "empty" "empty-or-trivia" "" EmptySource 1L 1L
          exact "trivia" "empty-or-trivia" " \t\r\n" EmptySource 1L 1L
          exact "no-pipeline" "no-pipeline" "node { echo 'x' }" NoPipelineBlock 1L 1L
          exact
              "source-limit-exact"
              "source-limit"
              (String.replicate Limits.defaults.MaxSourceBytes "x")
              NoPipelineBlock
              1L
              1L
          exact
              "source-limit-plus-one"
              "source-limit"
              (String.replicate (Limits.defaults.MaxSourceBytes + 1) "x")
              SourceTooLarge
              1L
              1L
          exact
              "one-contiguous-identifier"
              "node-limit"
              (String.replicate (Limits.defaults.MaxNodes + 1) "x")
              NoPipelineBlock
              1L
              1L
          exact
              "node-limit-exact"
              "node-limit"
              (String.replicate Limits.defaults.MaxNodes "x ")
              NoPipelineBlock
              1L
              1L
          exact
              "node-limit-plus-one"
              "node-limit"
              (String.replicate (Limits.defaults.MaxNodes + 1) "x ")
              TooManyNodes
              1L
              (int64 (2 * Limits.defaults.MaxNodes + 2))
          exact
              "brace-depth-exact"
              "brace-depth"
              (String.replicate Limits.defaults.MaxDepth "{")
              NoPipelineBlock
              1L
              1L
          exact
              "brace-depth-plus-one"
              "brace-depth"
              (depthSource "" "{")
              NestingTooDeep
              1L
              66L
          exact
              "bracket-depth-exact"
              "bracket-depth"
              (String.replicate Limits.defaults.MaxDepth "[")
              NoPipelineBlock
              1L
              1L
          exact
              "bracket-depth-plus-one"
              "bracket-depth"
              (depthSource "" "[")
              NestingTooDeep
              1L
              66L
          exact
              "parenthesis-depth-exact"
              "parenthesis-depth"
              (String.replicate Limits.defaults.MaxDepth "(")
              NoPipelineBlock
              1L
              1L
          exact
              "parenthesis-depth-plus-one"
              "parenthesis-depth"
              (depthSource "" "(")
              NestingTooDeep
              1L
              66L
          exact
              "single-scalar-content-limit-minus-one"
              "single-scalar-limit"
              ("'" + String.replicate (Limits.defaults.MaxScalarBytes - 1) "a" + "'")
              NoPipelineBlock
              1L
              1L
          exact
              "single-scalar-content-limit"
              "single-scalar-limit"
              ("'" + String.replicate Limits.defaults.MaxScalarBytes "a" + "'")
              ScalarTooLong
              1L
              (int64 Limits.defaults.MaxScalarBytes + 3L)
          exact
              "single-scalar-limit-plus-one"
              "single-scalar-limit"
              ("'" + String.replicate (Limits.defaults.MaxScalarBytes + 1) "a" + "'")
              ScalarTooLong
              1L
              (int64 Limits.defaults.MaxScalarBytes + 4L)
          exact
              "escaped-quote-keeps-scalar-open"
              "single-scalar-limit"
              escapedQuoteScalar
              ScalarTooLong
              1L
              (int64 Limits.defaults.MaxScalarBytes + 6L)
          exact
              "double-scalar-limit-plus-one"
              "double-scalar-limit"
              ("\"" + String.replicate (Limits.defaults.MaxScalarBytes + 1) "a" + "\"")
              ScalarTooLong
              1L
              (int64 Limits.defaults.MaxScalarBytes + 4L)
          exact
              "lf-resets-depth-position"
              "lf-depth"
              (depthSource "x\n" "{")
              NestingTooDeep
              2L
              66L
          exact
              "crlf-resets-depth-position"
              "crlf-depth"
              (depthSource "x\r\n" "{")
              NestingTooDeep
              2L
              66L
          exact
              "missing-pipeline-close"
              "missing-close"
              missingRootClose
              MalformedSyntax
              1L
              (int64 missingRootClose.Length + 1L) ]

    let token (rng: FuzzXorShift64) length =
        let alphabet = "0123456789abcdef"
        String(Array.init length (fun _ -> alphabet.[rng.NextInt alphabet.Length]))

    let generatedCase (rng: FuzzXorShift64) ordinal =
        let payload = token rng (8 + rng.NextInt 24)
        let label suffix = $"generated-{suffix}-{ordinal:D5}-len-{payload.Length:D2}"

        match ordinal % 6 with
        | 0 ->
            let length = ordinal / 6 + 5
            let whitespace = " \t\r\n"
            let source = String(Array.init length (fun _ -> whitespace.[rng.NextInt whitespace.Length]))
            exact (label "trivia") "empty-or-trivia" source EmptySource 1L 1L
        | 1 ->
            exact
                (label "no-pipeline")
                "no-pipeline"
                ($"node {{ echo 'x' }} // {ordinal:D5}-{payload}")
                NoPipelineBlock
                1L
                1L
        | 2 ->
            let source =
                "pipeline { agent any stages { stage('x') { steps { echo '"
                + $"{ordinal:D5}-{payload}"
                + "' } } }"

            exact
                (label "missing-close")
                "missing-close"
                source
                MalformedSyntax
                1L
                (int64 source.Length + 1L)
        | family ->
            let opener, familyName =
                match family with
                | 3 -> "{", "brace-depth"
                | 4 -> "[", "bracket-depth"
                | _ -> "(", "parenthesis-depth"

            let prefix = $"case_{ordinal:D5}_{payload} "

            exact
                (label familyName)
                familyName
                (depthSource prefix opener)
                NestingTooDeep
                1L
                (int64 prefix.Length + 66L)

    let cases () =
        let rng = FuzzXorShift64(seed)
        let generatedCount = inputCount - boundaries.Length

        boundaries
        @ [ for ordinal in 0 .. generatedCount - 1 -> generatedCase rng ordinal ]
        |> List.toArray

    let appendUInt64LittleEndian (hash: IncrementalHash) value =
        let bytes = Array.init 8 (fun shift -> byte (value >>> (8 * shift)))
        hash.AppendData bytes

    let appendInt32LittleEndian (hash: IncrementalHash) value =
        let unsigned = uint32 value
        let bytes = Array.init 4 (fun shift -> byte (unsigned >>> (8 * shift)))
        hash.AppendData bytes

    let appendLengthDelimitedUtf16 (hash: IncrementalHash) (value: string) =
        let bytes = Encoding.Unicode.GetBytes value
        appendInt32LittleEndian hash bytes.Length
        hash.AppendData bytes

    let corpusDigest (inputs: MalformedFuzzCase array) =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        appendUInt64LittleEndian hash seed
        appendInt32LittleEndian hash inputs.Length

        for input in inputs do
            appendLengthDelimitedUtf16 hash input.Label
            appendLengthDelimitedUtf16 hash input.Source

        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()

    let requiredFamilies =
        set
            [ "empty-or-trivia"
              "no-pipeline"
              "missing-close"
              "brace-depth"
              "bracket-depth"
              "parenthesis-depth"
              "source-limit"
              "node-limit"
              "single-scalar-limit"
              "double-scalar-limit"
              "lf-depth"
              "crlf-depth" ]

    testList
        "FG-004b deterministic malformed-input sweep"
        [ test "the fixed-seed length-delimited corpus is exact and replayable" {
              let first = cases ()
              let replay = cases ()
              let firstDigest = corpusDigest first
              let replayDigest = corpusDigest replay

              Expect.equal first.Length inputCount "exactly 10,000 malformed inputs are generated"
              Expect.equal replay.Length inputCount "the replay has the same exact size"

              for index in 0 .. inputCount - 1 do
                  Expect.equal replay.[index].Label first.[index].Label $"label replay at index {index}"

                  Expect.isTrue
                      (String.Equals(replay.[index].Source, first.[index].Source, StringComparison.Ordinal))
                      $"source code units replay at index {index}"

              Expect.equal replayDigest firstDigest "the complete length-delimited corpus replays byte-for-byte"
              Expect.equal
                  firstDigest
                  "774ac3ced0365dff265edef6cd1977a91ddd3654af584421beba0d8e706634ae"
                  "the fixed seed and recipe corpus are pinned"

              Expect.equal
                  (first |> Array.map (fun input -> input.Label) |> Set.ofArray |> Set.count)
                  inputCount
                  "every generated case label is unique"

              Expect.equal
                  (first |> Array.map (fun input -> input.Source) |> Set.ofArray |> Set.count)
                  inputCount
                  "every malformed source is unique"

              let observedFamilies = first |> Array.map (fun input -> input.Family) |> Set.ofArray
              Expect.equal observedFamilies requiredFamilies "every named malformed family is present"

              for family in requiredFamilies do
                  Expect.isGreaterThan
                      (first |> Array.filter (fun input -> input.Family = family) |> Array.length)
                      0
                      $"the {family} recipe is non-vacuous"

              printfn "FG004B_GENERATED=%d FG004B_CORPUS_SHA256=%s FG004B_FAMILIES=%d" first.Length firstDigest observedFamilies.Count
          }

          test "all 10,000 inputs return typed positioned refusals" {
              let inputs = cases ()
              let mutable refused = 0
              let mutable exactBoundaries = 0
              let mutable observedCodes = Set.empty

              for index in 0 .. inputs.Length - 1 do
                  let input = inputs.[index]
                  let result =
                      try
                          Parser.parse input.Source
                      with ex ->
                          let exceptionType = ex.GetType()
                          let typeName =
                              if String.IsNullOrWhiteSpace exceptionType.FullName then
                                  exceptionType.Name
                              else
                                  exceptionType.FullName

                          failtestf
                              "unhandled parser exception; seed=0x%016X; index=%d; label=%s; exception-type=%s"
                              seed
                              index
                              input.Label
                              typeName

                  match result with
                  | Ok _ ->
                      failtestf
                          "guaranteed-malformed input was accepted; seed=0x%016X; index=%d; label=%s"
                          seed
                          index
                          input.Label
                  | Error error ->
                      refused <- refused + 1
                      let wire = expectedWireName error.Code
                      observedCodes <- Set.add wire observedCodes

                      Expect.equal
                          (ErrorCode.toWireString error.Code)
                          wire
                          $"stable exhaustive wire code at index {index} ({input.Label})"

                      Expect.isFalse
                          (String.IsNullOrWhiteSpace error.Message)
                          $"nonblank refusal message at index {index} ({input.Label})"

                      Expect.isGreaterThanOrEqual
                          error.Position.Line
                          1L
                          $"positive refusal line at index {index} ({input.Label})"

                      Expect.isGreaterThanOrEqual
                          error.Position.Column
                          1L
                          $"positive refusal column at index {index} ({input.Label})"

                      let code, line, column = input.Expected
                      exactBoundaries <- exactBoundaries + 1
                      Expect.equal error.Code code $"exact boundary code at index {index} ({input.Label})"
                      Expect.equal error.Position.Line line $"exact boundary line at index {index} ({input.Label})"
                      Expect.equal error.Position.Column column $"exact boundary column at index {index} ({input.Label})"

              Expect.equal refused inputCount "all 10,000 inputs were refused"
              Expect.equal exactBoundaries inputCount "every refusal code and position is pinned"

              Expect.equal
                  observedCodes
                  (set
                      [ "empty_source"
                        "no_pipeline_block"
                        "malformed_syntax"
                        "source_too_large"
                        "too_many_nodes"
                        "nesting_too_deep"
                        "scalar_too_long" ])
                  "the sweep exercises every claimed admission/parser refusal class"

              printfn "FG004B_REFUSED=%d FG004B_EXACT_BOUNDARIES=%d FG004B_CODES=%d" refused exactBoundaries observedCodes.Count
          } ]

let declarativeDetection =
    testList
        "FG-012 declarative detection"
        [ test "a real declarative file is detected" {
              Expect.isTrue (Parser.looksDeclarative (mk "    stage('a') { steps { echo 'x' } }")) "detected"
          }
          test "a scripted file is not" {
              Expect.isFalse (Parser.looksDeclarative "node { sh 'make' }") "scripted"
          }
          test "the token inside a line comment does not count" {
              Expect.isFalse (Parser.looksDeclarative "// pipeline {\nnode { sh 'x' }") "comment"
          }
          test "the token inside a string does not count" {
              Expect.isFalse (Parser.looksDeclarative "node { echo 'pipeline { }' }") "string literal"
          }
          test "the token inside a block comment does not count" {
              Expect.isFalse (Parser.looksDeclarative "/* pipeline { */\nnode { sh 'x' }") "block comment"
          } ]

let structure =
    testList
        "declarative structure"
        [ // FG-174. A duplicate named argument is refused at PARSE time, because Jenkins
          // rejects the model and runs nothing — refusing at dispatch would let earlier
          // stages run first. MEASURED on the pinned lab: Jenkins logs only `Started by
          // user unknown or anonymous` and leaves the workspace empty, while Fogell took
          // the first flag, suppressed `exit 7` and reported success. UNPROVEN by
          // receipt: a compile-shaped refusal cannot be receipted (FG-129).
          test "a duplicate named argument is refused" {
              err (mk "    stage('B') { steps { sh script: 'exit 7', returnStatus: true, returnStatus: 'false' } }")
              |> ignore
          }

          test "a parenthesised duplicate keeps its key, code and position" {
              let e = err (mk "    stage('B') { steps { sh(script: 'x', returnStatus: true, returnStatus: false) } }")
              Expect.equal e.Code MalformedSyntax "a named code"
              Expect.stringContains e.Message "duplicate named argument `returnStatus`" "the inner refusal survives the reparse"
              Expect.isGreaterThan e.Position.Line 0L "and has a position"
          }

          test "DISTINCT named arguments are untouched" {
              // The refusal must not swallow the ordinary shape: this is the spelling the
              // whole corpus uses, and rejecting it would be far worse than the defect.
              let p = ok (mk "    stage('B') { steps { sh script: 'make', returnStatus: true } }")
              Expect.equal p.Stages.[0].Steps.Length 1 "the step parses"
          }

          test "a name repeated across DIFFERENT steps is fine" {
              // Duplication is per-call. One counter shared across the step block would
              // reject two ordinary `sh` steps that each set the same option.
              let p =
                  ok (mk "    stage('B') { steps { sh script: 'a', returnStatus: true\n sh script: 'b', returnStatus: true } }")

              Expect.equal p.Stages.[0].Steps.Length 2 "both steps parse"
          }

          // FG-175. A semantic refusal survives parser backtracking. `when` keeps its
          // deliberate opaque lane for unknown/plugin-owned conditions, but a duplicate
          // named key is a conclusive Groovy map-literal error and admission must see it
          // before any stage runs. The shared assertion checks the named code, duplicated
          // key and source position rather than merely asking for any parse failure.
          test "FG-175: a duplicate in when-equals is refused" {
              expectDuplicateWhen "actual" "equals expected: 1, actual: 1, actual: 2"
          }

          // The single-key conditions used to consume one pair and leave the second for
          // either the opaque fallback or a sibling condition. They now consume the
          // complete named group before applying the same duplicate guard as `equals`.
          test "FG-175: a duplicate in when-tag is refused" {
              expectDuplicateWhen "pattern" "tag pattern: 'v1', pattern: 'v2'"
          }

          test "FG-175: a duplicate in when-branch is refused" {
              expectDuplicateWhen "pattern" "branch pattern: 'a', pattern: 'b'"
          }

          test "FG-175: a duplicate in when-changelog is refused" {
              expectDuplicateWhen "pattern" "changelog pattern: 'a', pattern: 'b'"
          }

          test "FG-175: a duplicate in when-triggeredBy is refused" {
              expectDuplicateWhen "cause" "triggeredBy cause: 'a', cause: 'b'"
          }

          test "FG-175: a duplicate in when-changeset is refused" {
              expectDuplicateWhen "pattern" "changeset pattern: 'a', pattern: 'b'"
          }

          // `changeRequest` was a distinct route: its keyword parsed as the zero-argument
          // condition and the remaining named text became an implicitly-ANDed sibling.
          // Consuming the filter group closes that reinterpretation without refusing a
          // valid single filter Fogell does not yet model.
          test "FG-175: duplicate changeRequest filters cannot become an implicit sibling" {
              expectDuplicateWhen "target" "changeRequest target: 'a', target: 'b'"
          }

          test "the ORDINARY single-key conditions still parse" {
              // What the tripwires above must not be confused with.
              ok (mk "    stage('B') { when { tag pattern: 'v1' }\n steps { sh 'x' } }") |> ignore
              ok (mk "    stage('B') { when { branch 'main' }\n steps { sh 'x' } }") |> ignore
              match (ok (mk "    stage('B') { when { changeRequest target: 'main' }\n steps { sh 'x' } }")).Stages.[0].When with
              | Some(WhenAllOf [ WhenChangeRequest; WhenUnmodelled("changeRequest", _) ]) -> ()
              | other -> failtestf "a valid unsupported filter must remain fail-closed, got %A" other
          }

          test "FG-175: a duplicate in when-environment is refused" {
              expectDuplicateWhen "value" "environment name: 'T', value: 'a', value: 'b'"
          }

          test "FG-175: a duplicate nested in a when composition is refused" {
              expectDuplicateWhen "value" "allOf { branch 'main'; environment name: 'T', value: 'a', value: 'b' }"
          }

          test "FG-175: parenthesised named conditions use the same duplicate guard" {
              for key, body in
                  [ "actual", "equals(expected: 1, actual: 1, actual: 2)"
                    "pattern", "tag(pattern: 'v1', pattern: 'v2')"
                    "pattern", "branch(pattern: 'a', pattern: 'b')"
                    "pattern", "changelog(pattern: 'a', pattern: 'b')"
                    "cause", "triggeredBy(cause: 'a', cause: 'b')"
                    "pattern", "changeset(pattern: 'a', pattern: 'b')"
                    "target", "changeRequest(target: 'a', target: 'b')"
                    "value", "environment(name: 'T', value: 'a', value: 'b')" ] do
                  expectDuplicateWhen key body

              expectDuplicateWhen "target" "anyOf { branch 'main'; changeRequest(target: 'a', target: 'b') }"
          }

          test "FG-175: opaque values cannot hide a duplicate key" {
              for key, body in
                  [ "pattern", "tag pattern: patternFactory(1, 2), pattern: otherFactory([3, 4])"
                    "actual", "equals expected: [left: 1, right: 2], actual: helper(1, 2), actual: { x -> x }"
                    "value", "environment name: env.NAME, value: [one: 1, two: 2], value: lookup('x', 'y')" ] do
                  expectDuplicateWhen key body
          }

          test "opaque nonduplicate equals operands remain fail-closed" {
              let parsed =
                  ok (mk "    stage('B') { when { equals expected: [1], actual: [2] }\n steps { sh 'must-not-run' } }")

              match parsed.Stages.[0].When with
              | Some(WhenUnmodelled("equals", _)) -> ()
              | other -> failtestf "unsupported collection operands must not become modelled equals values, got %A" other
          }

          test "parenthesised valid named and positional conditions still parse" {
              ok (mk "    stage('B') { when { tag(pattern: 'v1') }\n steps { sh 'x' } }") |> ignore
              ok (mk "    stage('B') { when { branch('main') }\n steps { sh 'x' } }") |> ignore
              ok (mk "    stage('B') { when { equals(expected: 1, actual: 1) }\n steps { sh 'x' } }") |> ignore
              ok (mk "    stage('B') { when { environment(name: 'T', value: 'a') }\n steps { sh 'x' } }") |> ignore
          }

          test "the ORDINARY when conditions still parse" {
              // Both are in the corpus and are what the refusal must not touch.
              ok (mk "    stage('B') { when { equals expected: 1, actual: 1 }\n steps { sh 'x' } }") |> ignore
              ok (mk "    stage('B') { when { environment name: 'T', value: 'a' }\n steps { sh 'x' } }") |> ignore
          }

          test "stages and steps are recovered" {
              let p = ok (mk "    stage('Build') { steps { sh 'make'\n echo 'done' } }")
              Expect.equal p.Stages.Length 1 "one stage"
              Expect.equal p.Stages.[0].Name "Build" "stage name"
              Expect.equal p.Stages.[0].Steps.Length 2 "two steps"
              Expect.equal p.Stages.[0].Steps.[0].Name "sh" "first step"
              Expect.equal p.Stages.[0].Steps.[0].Positional [ "make" ] "step argument"
          }

          test "agent any at pipeline level" {
              Expect.equal (ok (mk "    stage('a') { steps { echo 'x' } }")).Agent AgentAny "agent any"
          }

          test "FG-014 inline plugin agents retain exact bytes at pipeline, stage and nested scope" {
              // DIRECTLY PROBED on Jenkins 2.568.1 at both scopes. The plugin owns
              // interpretation; the Declarative IR retains exact source and execution
              // refuses it until provisioning semantics exist.
              let pipelineArgs =
                  "   /* lead\n       still lead */ label: 'docker',\n      // between\n      yaml: \"${DOCKER_POD}\" /* tail */  \n  "

              let stageArgs =
                  "\t/* stage lead */ label: 'stage-pod', yaml: \"${DOCKER_POD}\"\n      /* stage tail */   "

              let nestedArgs = "  bogus: 'kept' /* nested tail */   "

              let source =
                  "def DOCKER_POD = 'apiVersion: v1'\npipeline {\n  agent {\n    kubernetes"
                  + pipelineArgs
                  + "}\n  stages {\n    stage('outer') {\n      agent { kubernetes"
                  + stageArgs
                  + "}\n      stages {\n        stage('inner') { agent { kubernetes"
                  + nestedArgs
                  + "} steps { echo 'x' } }\n      }\n    }\n  }\n}"

              let pipeline = ok source
              Expect.equal
                  pipeline.Agent
                  (AgentUnmodelled("kubernetes", Some pipelineArgs))
                  "pipeline provenance includes every boundary byte"

              Expect.equal
                  pipeline.Stages.[0].Agent
                  (Some(AgentUnmodelled("kubernetes", Some stageArgs)))
                  "stage provenance includes tabs, comments, GString source and trailing trivia"

              Expect.equal
                  pipeline.Stages.[0].Nested.[0].Agent
                  (Some(AgentUnmodelled("kubernetes", Some nestedArgs)))
                  "nested provenance is byte-exact and unknown plugin-owned keys remain accepted"
          }

          test "FG-014 block plugin agent remains admitted without inline provenance" {
              let source =
                  "pipeline { agent { kubernetes { label 'docker'; yaml 'apiVersion: v1' } } stages { stage('a') { steps { echo 'x' } } } }"

              Expect.equal
                  (ok source).Agent
                  (AgentUnmodelled("kubernetes", None))
                  "legacy block form remains structurally distinct"
          }

          test "FG-014 inline plugin agent rejects Jenkins-invalid argument boundaries" {
              let stageAgent args =
                  $"pipeline {{ agent any stages {{ stage('a') {{ agent {{ kubernetes {args} }} steps {{ echo 'x' }} }} }} }}"

              for label, args in
                  [ "same-line missing comma", "label: 'docker' yaml: 'apiVersion: v1'"
                    "newline missing comma", "label: 'docker'\n yaml: 'apiVersion: v1'"
                    "newline after kind", "\n label: 'docker', yaml: 'apiVersion: v1'"
                    "line comment after kind", "// before\n label: 'docker', yaml: 'apiVersion: v1'"
                    "duplicate key", "label: 'one', label: 'two'"
                    "positional extra", "label: 'docker', yaml: 'apiVersion: v1', 'extra'" ] do
                  let error = err (stageAgent args)
                  Expect.equal error.Code MalformedSyntax $"{label}: model-invalid form is refused"

              let unbraced =
                  "pipeline { agent kubernetes label: 'docker', yaml: 'apiVersion: v1'\n stages { stage('a') { steps { echo 'x' } } } }"

              Expect.equal (err unbraced).Code MalformedSyntax "the inline plugin form requires the agent block"
          }

          test "FG-014 duplicate agent sections cannot hide a plugin-defined agent" {
              // DIRECTLY PROBED on Jenkins 2.568.1: duplicates are rejected at both
              // scopes. Guard the collected nodes before first-match projection.
              for label, source in
                  [ "pipeline",
                    "pipeline { agent any agent { kubernetes label: 'docker', yaml: 'apiVersion: v1' } stages { stage('a') { steps { echo 'x' } } } }"
                    "stage",
                    "pipeline { agent any stages { stage('a') { agent any agent { kubernetes label: 'docker', yaml: 'apiVersion: v1' } steps { echo 'x' } } } }" ] do
                  Expect.equal (err source).Code MalformedSyntax $"{label}: duplicate agent sections are refused"
          }

          test "environment is captured as key/value" {
              let src =
                  "pipeline {\n  agent any\n  environment {\n    FOO = 'bar'\n  }\n  stages {\n    stage('a') { steps { echo 'x' } }\n  }\n}\n"

              Expect.equal (ok src).Environment [ "FOO", "bar" ] "environment pair"
          }

          test "FG-014 balanced map and list named values retain structure and provenance" {
              // DIRECTLY PROBED on Jenkins 2.568.1. Brackets delimit one argument even
              // when inner maps, lists, strings and commas are nested inside it.
              let src =
                  "pipeline {\n  agent any\n  parameters {\n    choice(name: 'v', choices: ['a,b', 'c'], description: 'd')\n  }\n  stages {\n    stage('a') {\n      steps {\n        publishHTML target: [allowMissing: true, nested: [name: 'r'], providers: [[$class: 'C']]], label: 'report'\n      }\n    }\n  }\n}"

              let pipeline = ok src
              let choice = pipeline.Parameters |> List.exactlyOne
              let publish = pipeline.Stages.[0].Steps |> List.exactlyOne

              Expect.equal
                  (choice.Named |> List.find (fun (name, _) -> name = "choices") |> snd)
                  "['a,b', 'c']"
                  "the complete list source survives"
              Expect.contains choice.ExpressionArgs "choices" "the list remains an expression"
              Expect.isFalse (choice.LiteralNamedArgs.Contains "choices") "a collection is not a quoted literal"
              Expect.equal choice.ArgumentOrder [ "name"; "choices"; "description" ] "source order survives"

              Expect.equal
                  (publish.Named |> List.find (fun (name, _) -> name = "target") |> snd)
                  "[allowMissing: true, nested: [name: 'r'], providers: [[$class: 'C']]]"
                  "the complete nested map/list source survives"
              Expect.contains publish.ExpressionArgs "target" "the map remains an expression"
              Expect.equal (publish.Named |> List.map fst) [ "target"; "label" ] "the following outer argument survives"
          }

          test "FG-014 malformed or non-bracket collection boundaries remain refused" {
              let sources =
                  [ "unclosed list",
                    mk "    stage('a') { steps { publishHTML target: [allowMissing: true\n echo 'must-not-disappear' } }"
                    "unclosed nested map",
                    mk "    stage('a') { steps { publishHTML target: [nested: [name: 'r']\n echo 'must-not-disappear' } }" ]

              for label, source in sources do
                  match Parser.parse source with
                  | Error error ->
                      Expect.equal error.Code MalformedSyntax $"{label}: unchecked collection source stays refused"
                  | Ok pipeline -> failtestf "%s unexpectedly admitted: %A" label pipeline
          }

          test "FG-014 string-labelled structural sections preserve their bodies" {
              // DIRECTLY PROBED on Jenkins 2.568.1 at all three corpus scopes.
              // The labels do not enter the Declarative model; the section body does.
              let src =
                  "pipeline {\n  agent any\n  stages('outer') {\n    stage('a') {\n      steps(\"work\") { echo 'x' }\n      post('stage-notify') {\n        failure { echo 'failed' }\n      }\n    }\n  }\n  post('notify') {\n    always { echo 'done' }\n  }\n}\n"

              let pipeline = ok src
              Expect.equal pipeline.Stages.Length 1 "the labelled stages body survives"
              Expect.equal pipeline.Stages.[0].Name "a" "the stage survives"
              Expect.equal pipeline.Stages.[0].Steps.Length 1 "the labelled steps body survives"
              Expect.equal pipeline.Stages.[0].Steps.[0].Name "echo" "the enclosed step is retained"
              Expect.equal pipeline.Stages.[0].Steps.[0].Positional [ "x" ] "the step value is retained"
              Expect.equal (pipeline.Stages.[0].Post |> List.map fst) [ Failure ] "a labelled stage post body survives"
              Expect.equal (pipeline.Post |> List.map fst) [ Always ] "the labelled post body survives"

              let nested =
                  ok
                      "pipeline {\n  agent any\n  stages {\n    stage('outer') {\n      stages('group') {\n        stage('inner') { steps('work') { echo 'nested' } }\n      }\n    }\n  }\n}"

              Expect.equal nested.Stages.[0].Name "outer" "the outer sequential stage survives"
              Expect.equal nested.Stages.[0].Nested.Length 1 "the labelled nested stages body survives"
              Expect.equal nested.Stages.[0].Nested.[0].Name "inner" "the nested stage survives"
              Expect.equal nested.Stages.[0].Nested.[0].Steps.[0].Positional [ "nested" ] "the nested body is retained"
          }

          test "FG-014 section labels stay on the narrow single-string boundary" {
              // Jenkins' Groovy front-end accepts broader argument expressions, but
              // blindly skipping balanced raw text would also accept expressions this
              // parser never validated. This slice covers only the three corpus-proven
              // string labels and keeps every other call shape fail-closed.
              let sources =
                  [ "empty steps args",
                    "pipeline {\n  agent any\n  stages { stage('a') { steps() { echo 'x' } } }\n}"
                    "numeric steps arg",
                    "pipeline {\n  agent any\n  stages { stage('a') { steps(1) { echo 'x' } } }\n}"
                    "multiple steps args",
                    "pipeline {\n  agent any\n  stages { stage('a') { steps('a', 'b') { echo 'x' } } }\n}"
                    "named post arg",
                    "pipeline {\n  agent any\n  stages { stage('a') { steps { echo 'x' } } }\n  post(name: 'n') { always { echo 'y' } }\n}"
                    "expression stages arg",
                    "pipeline {\n  agent any\n  stages(env.LABEL) { stage('a') { steps { echo 'x' } } }\n}" ]

              for label, source in sources do
                  Expect.equal (err source).Code MalformedSyntax $"{label}: unsupported section arguments remain refused"
          }

          test "FG-014 duplicate labelled structural sections refuse before first-pick body loss" {
              // DIRECTLY PROBED on Jenkins 2.568.1. Labelled/unlabelled and
              // empty/non-empty pairs all report "Multiple occurrences" at top,
              // stage and nested scope. In particular, the second steps body may
              // contain an input gate and must never disappear behind `tryPick`.
              let sources =
                  [ "steps plain then labelled",
                    "pipeline { agent any stages { stage('x') { steps { echo 'first' } steps('second') { input message: 'Deploy?' } } } }"
                    "steps labelled empty then plain",
                    "pipeline { agent any stages { stage('x') { steps('empty') { } steps { echo 'second' } } } }"
                    "stage post plain then labelled",
                    "pipeline { agent any stages { stage('x') { steps { echo 'x' } post { always { echo 'first' } } post('second') { failure { echo 'second' } } } } }"
                    "stage post labelled empty then plain",
                    "pipeline { agent any stages { stage('x') { steps { echo 'x' } post('empty') { } post { always { echo 'second' } } } } }"
                    "top post plain then labelled",
                    "pipeline { agent any stages { stage('x') { steps { echo 'x' } } } post { always { echo 'first' } } post('second') { failure { echo 'second' } } }"
                    "top post labelled empty then plain",
                    "pipeline { agent any stages { stage('x') { steps { echo 'x' } } } post('empty') { } post { always { echo 'second' } } }"
                    "top stages plain then labelled",
                    "pipeline { agent any stages { stage('a') { steps { echo 'a' } } } stages('second') { stage('b') { steps { echo 'b' } } } }"
                    "top stages labelled empty then plain",
                    "pipeline { agent any stages('empty') { } stages { stage('b') { steps { echo 'b' } } } }"
                    "nested stages plain then labelled",
                    "pipeline { agent any stages { stage('outer') { stages { stage('a') { steps { echo 'a' } } } stages('second') { stage('b') { steps { echo 'b' } } } } } }"
                    "nested stages labelled empty then plain",
                    "pipeline { agent any stages { stage('outer') { stages('empty') { } stages { stage('b') { steps { echo 'b' } } } } } }" ]

              for label, source in sources do
                  Expect.equal (err source).Code MalformedSyntax $"{label}: duplicate section is an admission refusal"
          }

          test "FG-014 a stage has exactly one structural body kind" {
              // DIRECTLY PROBED on Jenkins 2.568.1. Every pair drawn from steps,
              // stages, parallel and matrix reports that only one body is allowed.
              // The same rule holds for all four together, labelled empty-first
              // forms, and recursively nested stages. Validate the complete node
              // collection before a first-match projection can discard a body.
              let wrap body =
                  "pipeline {\n  agent any\n  stages {\n    stage('target') {\n"
                  + body
                  + "\n    }\n  }\n}"

              let bodies =
                  [ "steps", "      steps { echo 'step' }"
                    "stages", "      stages { stage('child') { steps { echo 'nested' } } }"
                    "parallel", "      parallel { stage('branch') { steps { echo 'branch' } } }"
                    "matrix",
                    "      matrix {\n        axes {\n          axis {\n            name 'OS'\n            values 'linux'\n          }\n        }\n        stages { stage('cell') { steps { echo 'cell' } } }\n      }" ]

              for label, body in bodies do
                  ok (wrap body) |> ignore

              for leftIndex in 0 .. bodies.Length - 2 do
                  for rightIndex in leftIndex + 1 .. bodies.Length - 1 do
                      let leftLabel, leftBody = bodies.[leftIndex]
                      let rightLabel, rightBody = bodies.[rightIndex]
                      let e = err (wrap (leftBody + "\n" + rightBody))
                      Expect.equal e.Code MalformedSyntax $"{leftLabel} + {rightLabel}: competing bodies refuse"

              let labelledAndNested =
                  [ "labelled empty steps then stages",
                    wrap
                        "      steps('empty') { }\n      stages { stage('child') { steps { echo 'nested' } } }"
                    "labelled empty stages then steps",
                    wrap "      stages('empty') { }\n      steps { echo 'step' }"
                    "empty parallel then parallel",
                    wrap
                        "      parallel { }\n      parallel { stage('branch') { steps { echo 'branch' } } }"
                    "empty matrix then matrix",
                    wrap
                        "      matrix { }\n      matrix { axes { axis { name 'OS'; values 'linux' } } stages { stage('cell') { steps { echo 'cell' } } } }"
                    "all four",
                    wrap (bodies |> List.map snd |> String.concat "\n")
                    "nested sequential child",
                    wrap
                        "      stages {\n        stage('inner') {\n          steps('work') { echo 'x' }\n          parallel { stage('branch') { steps { echo 'y' } } }\n        }\n      }"
                    "nested parallel child",
                    wrap
                        "      parallel {\n        stage('branch') {\n          stages('group') { stage('inner') { steps { echo 'x' } } }\n          matrix { axes { axis { name 'OS'; values 'linux' } } stages { stage('cell') { steps { echo 'y' } } } }\n        }\n      }" ]

              for label, source in labelledAndNested do
                  Expect.equal (err source).Code MalformedSyntax $"{label}: the recursive guard refuses body loss"
          }

          test "FG-014 command-form tools preserve kind, value and source order" {
              let src =
                  "pipeline {\n  agent any\n  tools {\n    maven 'm3'; jdk \"j8\"\n  }\n  stages {\n    stage('a') { steps { echo 'x' } }\n  }\n}\n"

              Expect.equal
                  (ok src).Tools
                  [ "maven", "m3"; "jdk", "j8" ]
                  "the parser keeps each tool kind paired with its configured installation"
          }

          test "FG-014 duplicate tools sections are rejected at pipeline and stage scope" {
              // DIRECTLY PROBED on Jenkins 2.568.1: both scopes report "Multiple
              // occurrences of the tools section". An empty first section does not
              // make the second legal, and two non-empty sections fail identically.
              let topEmptyFirst =
                  "pipeline {\n  agent any\n  tools { }\n  tools { jdk 'j8' }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let topTwoNonempty =
                  "pipeline {\n  agent any\n  tools { maven 'm3' }\n  tools { jdk 'j8' }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let stageEmptyFirst =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools { }\n      tools { jdk 'j8' }\n      steps { echo 'x' }\n    }\n  }\n}"

              let stageTwoNonempty =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools { maven 'm3' }\n      tools { jdk 'j8' }\n      steps { echo 'x' }\n    }\n  }\n}"

              for label, source in
                  [ "pipeline empty then non-empty", topEmptyFirst
                    "pipeline two non-empty", topTwoNonempty
                    "stage empty then non-empty", stageEmptyFirst
                    "stage two non-empty", stageTwoNonempty ] do
                  let e = err source
                  Expect.equal e.Code MalformedSyntax $"{label}: duplicate section is an admission refusal"
          }

          test "FG-132 a duplicate top-level options section is refused" {
              // MEASURED on Jenkins 2.568.1, UNPROVEN by receipt (FG-129: a
              // compile-shaped refusal cannot seal one): two top-level `options { }`
              // blocks, both holding valid directives, give `Multiple occurrences of
              // the options section` — refused on cardinality alone, before anything
              // runs. The guard counts sections, not their contents, so the
              // empty-first form falls under the same rule by construction.
              let topTwoNonempty =
                  "pipeline {\n  agent any\n  options { timestamps() }\n  options { disableConcurrentBuilds() }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let topEmptyFirst =
                  "pipeline {\n  agent any\n  options { }\n  options { timestamps() }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              for label, source in
                  [ "two non-empty", topTwoNonempty; "empty then non-empty", topEmptyFirst ] do
                  let e = err source
                  Expect.equal e.Code MalformedSyntax $"{label}: duplicate options section is an admission refusal"
                  Expect.stringContains
                      e.Message
                      "multiple occurrences of the `options` section"
                      $"{label}: the refusal names the duplicated section"
                  // The guard runs after the section collection, so the position is
                  // the pipeline block's closing brace — coarser than Jenkins, which
                  // names the duplicate's own line (`WorkflowScript: 4:`). The same
                  // shape as every FG-014 duplicate-section guard; pinned exactly so
                  // a position regression cannot hide behind a vacuous >0 check.
                  Expect.equal e.Position.Line 6L $"{label}: the refusal position is the collection point"
                  Expect.equal e.Position.Column 1L $"{label}: at the closing brace's column"
          }

          test "FG-132 one options block per scope stays admitted" {
              // The legal shape five corpus files use: one pipeline-level block plus
              // one stage-level block. The cardinality guard is per scope and must
              // not see this pair as a repeat.
              let src =
                  "pipeline {\n  agent any\n  options { timestamps() }\n  stages {\n    stage('a') {\n      options { timeout(time: 1, unit: 'MINUTES') }\n      steps { echo 'x' }\n    }\n  }\n}"

              let p = ok src
              Expect.equal p.Options.Length 1 "the pipeline block keeps its single directive"
              Expect.equal p.Stages.[0].Options.Length 1 "the stage block keeps its single directive"
          }

          test "FG-132 duplicate stage-level options still concatenate" {
              // Deliberately unguarded: stage-scope duplication has no Jenkins
              // measurement, and refusing without one risks rejecting a pipeline
              // Jenkins accepts. Concatenation keeps every directive visible to the
              // FG-053 validators, which is what makes the repeat detectable at all.
              // If this test starts failing because stage duplicates are refused,
              // that refusal needs its own measurement first.
              let src =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      options { timeout(time: 1, unit: 'MINUTES') }\n      options { timeout(time: 2, unit: 'MINUTES') }\n      steps { echo 'x' }\n    }\n  }\n}"

              let p = ok src
              Expect.equal p.Stages.[0].Options.Length 2 "both stage-level directives survive into the IR"
          }

          test "FG-014 tools require a Jenkins-measured newline or semicolon between entries" {
              let newline =
                  "pipeline {\n  agent any\n  tools {\n    maven 'm3'\n    jdk 'j8'\n  }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let adjacent =
                  "pipeline {\n  agent any\n  tools { maven 'm3' jdk 'j8' }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              Expect.equal (ok newline).Tools [ "maven", "m3"; "jdk", "j8" ] "newline separates entries"

              let e = err adjacent
              Expect.equal e.Code MalformedSyntax "space-only adjacency is not two declarations on Jenkins"
          }

          test "FG-014 tools keep the previously accepted assignment spelling" {
              let quoted =
                  "pipeline { agent any tools { maven = 'm3' } stages { stage('a') { steps { echo 'x' } } } }"

              let unquoted =
                  "pipeline {\n  agent any\n  tools {\n    maven = m3\n  }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              Expect.equal (ok quoted).Tools [ "maven", "m3" ] "quoted assignment remains accepted"
              Expect.equal
                  (ok unquoted).Tools
                  [ "maven", "m3" ]
                  "the command-form slice does not silently narrow the old unquoted assignment surface"
          }

          test "FG-014 adjacent quoted assignments retain the exact legacy body at both scopes" {
              let top =
                  "pipeline { agent any tools { maven = 'm3' jdk = 'j8' } stages { stage('a') { steps { echo 'x' } } } }"

              let stageSource =
                  "pipeline { agent any stages { stage('a') { tools { maven = 'm3' jdk = 'j8' } steps { echo 'x' } } } }"

              Expect.equal
                  (ok top).Tools
                  [ "maven", "m3"; "jdk", "j8" ]
                  "the explicit legacy lane preserves adjacent quoted assignments"

              let stage = (ok stageSource).Stages.[0]
              Expect.equal stage.Name "a" "stage assignment body survives"
              Expect.equal
                  stage.Tools
                  [ "maven", "m3"; "jdk", "j8" ]
                  "stage tools survive as typed IR rather than an opaque section"
              Expect.equal stage.Steps.Length 1 "the following stage steps survive"
              Expect.equal stage.Steps.[0].Name "echo" "complete-pipeline parsing reaches the step"
          }

          test "FG-014 stage tools use the same command and assignment grammar end-to-end" {
              let command =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools { maven 'm3'; jdk 'j8' }\n      steps { echo 'x' }\n    }\n  }\n}"

              let quotedAssignment =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools { maven = 'm3' }\n      steps { echo 'x' }\n    }\n  }\n}"

              let unquotedAssignment =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools {\n        maven = m3\n      }\n      steps { echo 'x' }\n    }\n  }\n}"

              for label, source, expected in
                  [ "command", command, [ "maven", "m3"; "jdk", "j8" ]
                    "quoted assignment", quotedAssignment, [ "maven", "m3" ]
                    "newline-terminated unquoted assignment", unquotedAssignment, [ "maven", "m3" ] ] do
                  let stage = (ok source).Stages.[0]
                  Expect.equal stage.Name "a" $"{label}: stage survives"
                  Expect.equal stage.Tools expected $"{label}: selections survive in typed stage IR"
                  Expect.equal stage.Steps.Length 1 $"{label}: steps survive"
                  Expect.equal stage.Steps.[0].Name "echo" $"{label}: parsed through the complete pipeline"
          }

          test "FG-014 stage tools refuse space-only adjacent entries" {
              let adjacent =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools { maven 'm3' jdk 'j8' }\n      steps { echo 'x' }\n    }\n  }\n}"

              let e = err adjacent
              Expect.equal e.Code MalformedSyntax "stage scope must not invent two tools Jenkins did not model"
          }

          test "FG-014 command kind and value must share a line at both scopes" {
              let topSplit =
                  "pipeline {\n  agent any\n  tools {\n    maven\n    'm3'\n  }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let stageSplit =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools {\n        maven\n        'm3'\n      }\n      steps { echo 'x' }\n    }\n  }\n}"

              let tabbed =
                  "pipeline {\n  agent any\n  tools { maven\t'm3' }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              Expect.equal (err topSplit).Code MalformedSyntax "pipeline command newline is refused"
              Expect.equal (err stageSplit).Code MalformedSyntax "stage command newline is refused"
              Expect.equal (ok tabbed).Tools [ "maven", "m3" ] "a horizontal tab is a valid command gap"
          }

          test "FG-014 mixed command and assignment entries require a statement boundary" {
              let newline =
                  "pipeline {\n  agent any\n  tools {\n    maven = 'm3'\n    jdk 'j8'\n  }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let semicolon =
                  "pipeline {\n  agent any\n  tools { maven 'm3'; jdk = 'j8' }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let adjacent =
                  "pipeline {\n  agent any\n  tools { maven = 'm3' jdk 'j8' }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              let stageNewline =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') {\n      tools {\n        maven = 'm3'\n        jdk 'j8'\n      }\n      steps { echo 'x' }\n    }\n  }\n}"

              Expect.equal (ok newline).Tools [ "maven", "m3"; "jdk", "j8" ] "newline separates mixed entries"
              Expect.equal (ok semicolon).Tools [ "maven", "m3"; "jdk", "j8" ] "semicolon separates mixed entries"
              Expect.equal (err adjacent).Code MalformedSyntax "space-only mixed adjacency is refused"

              let stage = (ok stageNewline).Stages.[0]
              Expect.equal stage.Tools [ "maven", "m3"; "jdk", "j8" ] "stage mixed forms retain both selections"
              Expect.equal stage.Steps.Length 1 "stage mixed newline form parses through its steps"
          }

          test "named step arguments are separated from positional" {
              let p = ok (mk "    stage('a') { steps { archiveArtifacts artifacts: '*.jar', fingerprint: true } }")
              let step = p.Stages.[0].Steps.[0]
              Expect.equal step.Name "archiveArtifacts" "step name"
              Expect.contains (step.Named |> List.map fst) "artifacts" "named arg present"
          }

          test "when { environment name: 'X', value: 'Y' } parses — the shape Jenkins accepts" {
              // Found by a differential receipt: the parser expected
              // `environment X = 'Y'`, which Jenkins REJECTS, so every real
              // condition fell through to the unmodelled branch and from there
              // out of the `when` section entirely.
              let p = ok (mk "    stage('a') { when { environment name: 'FOO', value: 'bar' } steps { echo 'x' } }")
              Expect.equal p.Stages.[0].When (Some(WhenEnvironment("FOO", "bar"))) "modelled condition"
          }

          test "an unparseable when is recorded as unmodelled, never dropped" {
              // THE dangerous case. Before the backstop, a `when` the parser could
              // not understand vanished into the stage's generic section fallback,
              // leaving the stage unconditional — silently running a stage Jenkins
              // skips. A refusal is recoverable; a silent wrong answer is not.
              let p = ok (mk "    stage('a') { when { someFutureCondition foo: 'bar' } steps { echo 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtest $"an unrecognised when must be Some(WhenUnmodelled …), got {other}"
          }

          test "allOf composes not and environment" {
              let p = ok (mk "    stage('a') { when { allOf { environment name: 'FOO', value: 'bar'\n not { environment name: 'FOO', value: 'no' } } } steps { echo 'x' } }")
              Expect.equal
                  p.Stages.[0].When
                  (Some(WhenAllOf [ WhenEnvironment("FOO", "bar"); WhenNot(WhenEnvironment("FOO", "no")) ]))
                  "nested composition"
          }

          test "tag and equals conditions are modelled" {
              let t = ok (mk "    stage('a') { when { tag 'v*' } steps { echo 'x' } }")
              Expect.equal t.Stages.[0].When (Some(WhenTag "v*")) "tag pattern"

              let e = ok (mk "    stage('a') { when { equals expected: 2, actual: 2 } steps { echo 'x' } }")
              Expect.equal e.Stages.[0].When (Some(WhenEquals("2", "2"))) "equals pair"
          }

          test "context-dependent when conditions are modelled, not refused" {
              // MEASURED: on a plain job all six are FALSE and their stages are skipped,
              // with the build succeeding. They used to fail CLOSED, refusing the file.
              // Receipt: `when-context-conditions`.
              let one w =
                  (ok (mk $"    stage('a') {{ when {{ {w} }} steps {{ echo 'x' }} }}")).Stages.[0].When

              Expect.equal (one "buildingTag()") (Some WhenBuildingTag) "buildingTag"
              Expect.equal (one "changeRequest()") (Some WhenChangeRequest) "changeRequest"
              Expect.equal (one "isRestartedRun()") (Some WhenIsRestartedRun) "isRestartedRun"
              Expect.equal (one "changeset '**/*.java'") (Some(WhenChangeset "**/*.java")) "changeset"
              Expect.equal (one "changelog '.*fix.*'") (Some(WhenChangelog ".*fix.*")) "changelog"
              Expect.equal (one "triggeredBy 'TimerTrigger'") (Some(WhenTriggeredBy "TimerTrigger")) "triggeredBy"
          }

          test "a zero-argument when condition rejects arguments" {
              // These accepted ANY balanced parens and DISCARDED the contents, so
              // `buildingTag('x')` — which Jenkins rejects — was silently modelled as the
              // argument-free form, and `changeRequest(target: 'main')` lost its filter.
              let one w = (ok (mk $"    stage('a') {{ when {{ {w} }} steps {{ echo 'x' }} }}")).Stages.[0].When

              Expect.equal (one "buildingTag()") (Some WhenBuildingTag) "empty parens are fine"
              let invalid = err (mk "    stage('a') { when { buildingTag('x') } steps { echo 'x' } }")
              Expect.equal invalid.Code MalformedSyntax "non-empty arguments are refused at admission"
              Expect.stringContains invalid.Message "does not accept arguments" "the refusal names the rule"

              match one "changeRequest(target: 'main')" with
              | Some(WhenAllOf [ WhenChangeRequest; WhenUnmodelled("changeRequest", _) ]) -> ()
              | other -> failtest $"a changeRequest filter must not be dropped, got {other}"
          }

          test "a wrong named key never becomes the pattern" {
              // The same failure mode already fixed for `tag`, reintroduced for the three
              // conditions added in this batch.
              let one w = (ok (mk $"    stage('a') {{ when {{ {w} }} steps {{ echo 'x' }} }}")).Stages.[0].When

              // MEASURED: `pattern` is the data-bound name Jenkins accepts; `glob` is
              // REJECTED with a compilation error. An earlier version of this test asserted
              // `glob` was legal — it encoded a key I had invented, so the test agreed with
              // the bug instead of catching it.
              // Receipt: `when-scm-pattern-keys`.
              Expect.equal (one "changeset pattern: '**/*.java'") (Some(WhenChangeset "**/*.java")) "the measured key works"
              Expect.equal (one "changelog pattern: '.*fix.*'") (Some(WhenChangelog ".*fix.*")) "same key for changelog"
              Expect.equal (one "triggeredBy cause: 'TimerTrigger'") (Some(WhenTriggeredBy "TimerTrigger")) "cause for triggeredBy"

              let invalid = err (mk "    stage('a') { when { changeset glob: '**/*.java' } steps { echo 'x' } }")
              Expect.equal invalid.Code MalformedSyntax "the measured-invalid key is refused"
              Expect.stringContains invalid.Message "`glob`" "the diagnostic names the invalid key"

              match one "changeset comparator: 'REGEXP'" with
              | Some(WhenUnmodelled("changeset", _)) -> ()
              | other -> failtest $"a wrong key must be unmodelled, got {other}"
          }

          test "semicolon-separated conditions inside anyOf parse" {
              // `anyOf { branch 'a'; branch 'b' }` is idiomatic and appeared in 6 corpus
              // files. `many whenCondition` stopped at the semicolon, the closing brace
              // failed, and the WHOLE anyOf degraded to unmodelled — so it failed closed.
              let p = ok (mk "    stage('a') { when { anyOf { branch 'master'; branch 'staging' } } steps { echo 'x' } }")
              Expect.equal p.Stages.[0].When (Some(WhenAnyOf [ WhenBranch "master"; WhenBranch "staging" ])) "both branches"
          }

          test "beforeAgent is an evaluation option, not a condition" {
              // It changes WHEN the condition is evaluated, never WHETHER it holds.
              // Treated as unmodelled it made the whole `when` fail closed.
              // A directive contributes NOTHING to the condition, so only the condition
              // survives. The earlier assertion kept it as a neutral conjunct, which is
              // also why it could be nested — and MEASURED, Jenkins rejects that:
              //   Unknown conditional beforeAgent. Valid conditionals are: allOf, anyOf,
              //   branch, buildingTag, changeRequest, changelog, changeset, environment,
              //   equals, expression, isRestartedRun, not, tag, triggeredBy
              // UNPROVEN BY RECEIPT: see the note in Parser.fs — a rejection cannot be receipted
              // without over-fitting to compiler wording. THIS test is the evidence.
              let p = ok (mk "    stage('a') { when { beforeAgent true\n branch 'main' } steps { echo 'x' } }")
              Expect.equal p.Stages.[0].When (Some(WhenBranch "main")) "the directive contributes nothing"

              // Nested inside a condition it must NOT be accepted, because Jenkins refuses
              // to compile such a pipeline.
              let nested = err (mk "    stage('a') { when { anyOf { beforeAgent true\n branch 'x' } } steps { echo 'y' } }")
              Expect.equal nested.Code MalformedSyntax "a nested directive is refused at admission"
              Expect.stringContains nested.Message "cannot be nested" "the refusal names the invalid placement"

              for body in [ "beforeAgent maybe"; "beforeInput 1"; "beforeOptions" ] do
                  let malformed = err (mk $"    stage('a') {{ when {{ {body}\n branch 'main' }} steps {{ echo 'x' }} }}")
                  Expect.equal malformed.Code MalformedSyntax $"{body}: malformed directive is refused"
                  Expect.stringContains malformed.Message "requires `true` or `false`" $"{body}: rule is named"
          }

          test "empty and directive-only when sections refuse at admission" {
              for label, body in
                  [ "empty", ""; "beforeAgent only", "beforeAgent true"; "all directives", "beforeAgent true\n beforeInput false\n beforeOptions true" ] do
                  let e = err (mk $"    stage('a') {{ when {{ {body} }} steps {{ echo 'x' }} }}")
                  Expect.equal e.Code MalformedSyntax $"{label}: named admission code"
                  Expect.stringContains e.Message "empty when closure" $"{label}: Jenkins rule named"
          }

          test "an equals operand may be a bare identifier with an underscore" {
              // The last unmodelled `when` in the corpus was
              // `equals expected: 'False', actual: _deploy_to_nexus` — one character
              // missing from an identifier charset made the stage fail closed.
              let p = ok (mk "    stage('a') { when { equals expected: 'False', actual: _deploy_to_nexus } steps { echo 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenEquals(e, a)) ->
                  Expect.equal e "'False'" "quoted literal keeps its quotes"
                  Expect.equal a "_deploy_to_nexus" "identifier operand survives"
              | other -> failtest $"expected WhenEquals, got {other}"
          }

          test "a named when argument that is not `pattern` fails closed" {
              // REVIEW FIX (Copilot, PR #13): the named form accepted ANY key, so
              // `tag comparator: 'REGEXP'` was read as pattern = "REGEXP" — a
              // silently wrong gate, which is the failure mode this whole area
              // keeps producing.
              let t = ok (mk "    stage('a') { when { tag comparator: 'REGEXP' } steps { echo 'x' } }")

              match t.Stages.[0].When with
              | Some(WhenUnmodelled("tag", _)) -> ()
              | other -> failtest $"an unknown named arg must not become the pattern, got {other}"

              let ok' = ok (mk "    stage('a') { when { tag pattern: 'v*' } steps { echo 'x' } }")
              Expect.equal ok'.Stages.[0].When (Some(WhenTag "v*")) "pattern: is accepted"
          }

          test "equals keeps operand source form so a string is not an int" {
              // Jenkins compares objects: Integer 2 != String '2'. Storing both as
              // bare text made them compare equal and ran a stage Jenkins skips.
              let same = ok (mk "    stage('a') { when { equals expected: 2, actual: 2 } steps { echo 'x' } }")
              Expect.equal same.Stages.[0].When (Some(WhenEquals("2", "2"))) "both bare"

              let mixed = ok (mk "    stage('a') { when { equals expected: 2, actual: '2' } steps { echo 'x' } }")

              match mixed.Stages.[0].When with
              | Some(WhenEquals(e, a)) -> Expect.notEqual e a "a quoted 2 differs from a bare 2"
              | other -> failtest $"expected WhenEquals, got {other}"
          }

          test "failFast is a STAGE-level directive, as Jenkins requires" {
              // MEASURED: Jenkins 2.568.1 rejects `failFast true` INSIDE the
              // parallel block with "Expected a stage". The accepted form is a
              // sibling of `parallel`, and a differential receipt is what
              // corrected the first implementation.
              // UNPROVEN BY RECEIPT: see the note in Parser.fs. THIS test is the evidence.
              let src =
                  mk
                      "    stage('outer') {\n      failFast true\n      parallel {\n        stage('a') { steps { echo 'a' } }\n        stage('b') { steps { echo 'b' } }\n      }\n    }"

              let p = ok src
              Expect.isTrue p.Stages.[0].IsParallel "marked parallel"
              Expect.isTrue p.Stages.[0].FailFast "failFast recorded"
              Expect.equal p.Stages.[0].Nested.Length 2 "both branches present"
          }

          test "a parallel block without failFast defaults to false" {
              let src =
                  mk
                      "    stage('outer') {\n      parallel {\n        stage('a') { steps { echo 'a' } }\n      }\n    }"

              Expect.isFalse (ok src).Stages.[0].FailFast "default is let siblings finish"
          }

          test "failFast false is honoured as written" {
              let src =
                  mk
                      "    stage('outer') {\n      failFast false\n      parallel {\n        stage('a') { steps { echo 'a' } }\n      }\n    }"

              let p = ok src
              Expect.isFalse p.Stages.[0].FailFast "explicit false"
              Expect.equal p.Stages.[0].Nested.Length 1 "branch survived"
          }

          test "parallelsAlwaysFailFast is captured as a pipeline option" {
              let src =
                  "pipeline {\n  agent any\n  options {\n    parallelsAlwaysFailFast()\n  }\n  stages {\n    stage('a') { steps { echo 'x' } }\n  }\n}\n"

              Expect.contains ((ok src).Options |> List.map (fun o -> o.Name)) "parallelsAlwaysFailFast" "option recorded"
          }

          test "nested parallel stages are recorded and flattened" {
              let src =
                  mk
                      "    stage('outer') {\n      parallel {\n        stage('a') { steps { echo 'a' } }\n        stage('b') { steps { echo 'b' } }\n      }\n    }"

              let p = ok src
              Expect.isTrue p.Stages.[0].IsParallel "marked parallel"
              Expect.equal (Pipeline.flattenStages p.Stages |> List.length) 3 "outer + two children"
          }

          test "post conditions are keyed by condition" {
              let src =
                  "pipeline {\n  agent any\n  stages {\n    stage('a') { steps { echo 'x' } }\n  }\n  post {\n    always { echo 'a' }\n    failure { echo 'f' }\n  }\n}\n"

              let p = ok src
              Expect.equal p.Post.Length 2 "two conditions"
              Expect.contains (p.Post |> List.map fst) Always "always present"
              Expect.contains (p.Post |> List.map fst) Failure "failure present"
          }

          test "a when-expression body is kept as source, not evaluated (ADR 0002)" {
              let src = mk "    stage('a') { when { expression { return env.BRANCH == 'main' } }\n steps { echo 'x' } }"
              let p = ok src

              match p.Stages.[0].When with
              | Some(WhenExpression body) -> Expect.stringContains body "env.BRANCH" "raw source retained"
              | other -> failtestf "expected WhenExpression, got %A" other
          }

          test "source before and after the pipeline block is retained exactly" {
              let before = "#!/usr/bin/env groovy\n@Library('x') _\ndef helper() { return 1 }\n"
              let block = mk "    stage('a') { steps { echo 'x' } }"
              let after = "\ndef trailing() { return 2 }\n"
              let p = ok (before + block + after)

              Expect.equal p.Stages.Length 1 "block found amid surrounding statements"
              Expect.equal p.Preamble before "every byte before the pipeline token is retained"
              Expect.equal p.Epilogue ("\n" + after) "every byte after the exact outer brace is retained"
          }

          test "outer-boundary capture ignores braces in nested bodies, strings, slashy text and comments" {
              let before = "def before() { return '}' }\n"

              let block =
                  "pipeline {\n"
                  + "  agent any\n"
                  + "  stages {\n"
                  + "    stage('a') { steps { script {\n"
                  + "      def quoted = \"}\"\n"
                  + "      def pattern = /}/\n"
                  + "      def nested = [value: [text: '{']]\n"
                  + "      /* }}} */\n"
                  + "      echo quoted\n"
                  + "    } } }\n"
                  + "  }\n"
                  + "}"

              let after = "\n// } trailing comment\n/* { pipeline { } } */\ndef after() { return \"}\" }\n"
              let p = ok (before + block + after)

              Expect.equal p.Preamble before "nested syntax did not move the opening boundary"
              Expect.equal p.Epilogue after "nested syntax did not move the closing boundary"
          }

          test "Pipeline.empty has explicit empty surrounding-source provenance" {
              Expect.equal Pipeline.empty.Preamble "" "empty preamble"
              Expect.equal Pipeline.empty.Epilogue "" "empty epilogue"
          }

          test "a pipeline with no stages is a named rejection" {
              Expect.equal (err "pipeline {\n  agent any\n}\n").Code NoStages "no_stages"
          }

          test "a scripted file reports no_pipeline_block, not a syntax error" {
              Expect.equal (err "node { sh 'make' }").Code NoPipelineBlock "no_pipeline_block"
          }

          // FG-185. THE SAME SOURCE THROUGH BOTH PUBLIC ENTRY POINTS, and the pair is the
          // test. Script-body validation lived in `parse`, so `parseWithLimits` — equally
          // public, and the one a caller reaches for precisely when it wants different
          // bounds — returned Ok for a body `parse` rejects, restoring the delayed
          // execution-time failure the check exists to prevent. Asserting only the
          // `parseWithLimits` half would pass a build that moved the check and broke
          // `parse`; asserting both pins the actual invariant, which is that the two
          // cannot disagree about admissibility.
          test "a malformed script body is rejected through parseWithLimits, not only parse" {
              let src = mk "    stage('a') { steps { script { def x = } } }"

              // NOT `Limits.defaults` — that goes down the very path `parse` uses and
              // would prove nothing about the custom-limit route.
              let custom =
                  { Limits.defaults with
                      MaxSourceBytes = 100_000 }

              match Parser.parseWithLimits custom src with
              | Ok _ -> failtest "parseWithLimits admitted a malformed script body"
              | Error e -> Expect.equal e.Code MalformedSyntax "malformed_syntax"

              Expect.equal (err src).Code MalformedSyntax "and `parse` still agrees"
          } ]

/// FG-126a. Jenkins 2.568.1 refuses the measured `\8` spelling at Groovy
/// compilation while Fogell used to drop the backslash and run the command.
/// This tranche is intentionally narrow: it pins directly decoded Declarative
/// literals only and makes no claim about `\9`, arbitrary invalid letters,
/// provenance sentinels, dollar-slashy strings, opaque raw expressions or a
/// purely Scripted Jenkinsfile.
let invalidEightEscape =
    let diagnostic = "invalid Groovy escape `\\8`: `8` is not an octal digit"

    let directStep literal =
        mk $"    stage('S') {{ steps {{ sh {literal} }} }}"

    let expectRefusal label source =
        let assertError route result =
            match result with
            | Ok _ -> failtestf "%s/%s admitted the measured-invalid escape" label route
            | Error e ->
                Expect.equal e.Code MalformedSyntax $"{label}/{route}: named admission code"
                Expect.equal e.Message diagnostic $"{label}/{route}: exact diagnostic"
                Expect.isGreaterThan e.Position.Line 0L $"{label}/{route}: positive line"
                Expect.isGreaterThan e.Position.Column 0L $"{label}/{route}: positive column"

        let custom =
            { Limits.defaults with
                MaxSourceBytes = 100_000 }

        assertError "parse" (Parser.parse source)
        assertError "parseWithLimits" (Parser.parseWithLimits custom source)

    let onlyPositional source =
        let pipeline = ok source
        match pipeline.Stages with
        | [ stage ] ->
            match stage.Steps with
            | [ step ] -> step.Positional
            | other -> failtestf "expected one step, got %A" other
        | other -> failtestf "expected one stage, got %A" other

    testList
        "FG-126a measured invalid-eight refusal"
        [ test "all four decoded quote consumers refuse through both public entry points" {
              for label, literal in
                  [ "single", "'prefix\\8suffix'"
                    "triple-single", "'''prefix\\8suffix'''"
                    "double", "\"prefix\\8suffix\""
                    "triple-double", "\"\"\"prefix\\8suffix\"\"\"" ] do
                  expectRefusal label (directStep literal)
          }

          test "positional, named and environment fallbacks cannot swallow the refusal" {
              expectRefusal "positional" (directStep "'prefix\\8suffix'")

              expectRefusal
                  "named"
                  (mk "    stage('S') { steps { sh(script: 'prefix\\8suffix') } }")

              expectRefusal
                  "environment"
                  "pipeline { agent any environment { BAD = 'prefix\\8suffix' } stages { stage('S') { steps { sh 'true' } } } }"
          }

          test "an escaped backslash and slashy text retain backslash-eight exactly" {
              Expect.equal
                  (onlyPositional (directStep "'prefix\\\\8suffix'"))
                  [ "prefix\\8suffix" ]
                  "escaped backslash is data, so the following 8 is ordinary text"

              Expect.equal
                  (onlyPositional (directStep "/prefix\\8suffix/"))
                  [ "prefix\\8suffix" ]
                  "slashy strings keep non-delimiter escapes literal"
          }

          test "valid octal and ordinary quoted values keep their exact decoding" {
              Expect.equal
                  (onlyPositional (directStep "'\\7\\77\\377'"))
                  [ "\u0007?\u00ff" ]
                  "one-, two- and valid three-digit octal remain admitted"

              Expect.equal
                  (onlyPositional (directStep "'ordinary'"))
                  [ "ordinary" ]
                  "the historical catch-all still serves ordinary quoted text"
          } ]

/// FG-141. Slashy versus division, decided by POSITION: a `/` after something
/// that can end an expression is division; with no left operand it can only
/// open a slashy. The over-broad fix (every `/` opens a span) was an approval
/// bypass — `input message: 10 / 2` lost its prompt — and the narrow one here
/// must hold both directions at both scanner sites.
/// FG-123a. `Block` carries parsed children, so it deliberately collapses an
/// absent trailing block and an empty one to the same list. Option validation
/// needs source PRESENCE, including trivia-only bodies Jenkins still sees as a
/// closure. Keep that fact in the IR rather than trying to reconstruct it from
/// raw arguments or braces that may be ordinary string content.
let stepBlockPresence =
    let pipeline optionBody =
        $"pipeline {{ agent any options {{ {optionBody} }} stages {{ stage('S') {{ steps {{ sh 'true' }} }} }} }}"

    let ansi optionBody =
        let parsed = ok (pipeline optionBody)
        parsed.Options |> List.find (fun option -> option.Name = "ansiColor")

    testList
        "FG-123a trailing-block presence"
        [ test "absent, empty, comment, separator and nonempty blocks remain distinct" {
              let absent = ansi "ansiColor('xterm')"
              Expect.isFalse absent.HasBlock "an ordinary option has no trailing block"
              Expect.isEmpty absent.Block "absence still has no parsed children"

              for label, source in
                  [ "empty", "ansiColor('xterm') {}"
                    "line-comment", "ansiColor('xterm') { // trivia only\n }"
                    "block-comment", "ansiColor('xterm') { /* trivia only */ }"
                    "semicolon", "ansiColor('xterm') { ; }" ] do
                  let option = ansi source
                  Expect.isTrue option.HasBlock $"{label}: source presence survives"
                  Expect.isEmpty option.Block $"{label}: trivia invents no child step"

              let nonempty = ansi "ansiColor('xterm') { sh 'inside' }"
              Expect.isTrue nonempty.HasBlock "a populated trailing block is present"
              Expect.equal (nonempty.Block |> List.map (fun step -> step.Name)) [ "sh" ] "its child still parses"
          }

          test "braces inside arguments are not mistaken for a trailing block" {
              for source in
                  [ "ansiColor('x{term}')"
                    "ansiColor(colorMapName: 'x}term')" ] do
                  let option = ansi source
                  Expect.isFalse option.HasBlock $"argument content is not a block: {source}"
                  Expect.isEmpty option.Block "argument braces create no children"
          }

          test "script bodies carry the same source-presence bit" {
              let withoutBody = ok (mk "    stage('S') { steps { script() } }")
              let absent = withoutBody.Stages.[0].Steps.[0]
              Expect.isFalse absent.HasBlock "bodyless script is absent"
              Expect.isNone absent.ScriptBody "bodyless script has no source"

              let withBody = ok (mk "    stage('S') { steps { script { /* empty */ } } }")
              let present = withBody.Stages.[0].Steps.[0]
              Expect.isTrue present.HasBlock "opaque script body is present"
              Expect.isSome present.ScriptBody "opaque source is retained"
              Expect.isEmpty present.Block "script source is never reparsed as declarative children"
          }

          test "an unterminated trailing block remains a named syntax rejection" {
              let malformed =
                  "pipeline { agent any options { ansiColor('xterm') { sh 'inside' } stages { stage('S') { steps { sh 'true' } } } }"

              Expect.equal (err malformed).Code MalformedSyntax "the missing option-block brace cannot downgrade to absence"
          } ]

let slashyPosition =
    let steps body =
        mk $"    stage(\"S\") {{\n      steps {{\n{body}\n      }}\n    }}"

    testList
        "FG-141 slashy versus division"
        [ test "division in a named argument never opens a span (scenario W's shape)" {
              let p = ok (steps "        input message: 10 / 2")
              Expect.equal (Pipeline.totalSteps p) 1 "the gate survives"
          }

          test "a slashy carrying a stop character is consumed whole" {
              // `}` is in the raw-argument stop set; truncating there loses the
              // literal's tail and the closing delimiter.
              let p = ok (steps "        input message: /a}b/")
              Expect.equal (Pipeline.totalSteps p) 1 "arg intact"
          }

          test "a slashy after an operator inside a larger expression" {
              let p = ok (steps "        echo 'x' + /a}b/")
              Expect.equal (Pipeline.totalSteps p) 1 "mid-expression span"
          }

          test "a script body's brace-carrying slashy does not end the block" {
              let p =
                  ok (steps "        script {\n          def pattern = /}/\n          sh \"saw:${pattern}\"\n        }")

              Expect.equal (Pipeline.totalSteps p) 1 "block intact"
          }

          test "a script body's division is not read as a span opener" {
              let p =
                  ok (steps "        script {\n          def x = 10 / 2\n          echo \"v:${x}\"\n        }")

              Expect.equal (Pipeline.totalSteps p) 1 "division intact"
          }

          test "a when-expression's slashy does not end the expression body" {
              let p =
                  ok (
                      mk
                          "    stage(\"S\") {\n      when { expression { env.B ==~ /a}b/ } }\n      steps {\n        echo 'ran'\n      }\n    }"
                  )

              Expect.equal (Pipeline.totalSteps p) 1 "when body intact"
          }

          test "an unterminated slashy candidate falls back to the old reading" {
              // The `/` stays ordinary text; the argument still parses and the
              // nonsense fails loudly at evaluation, not silently at parse.
              let p = ok (steps "        echo env.A + / 2")
              Expect.equal (Pipeline.totalSteps p) 1 "no cross-line hunt"
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs
        []
        argv
        (testList
            "Fogell.Pipeline.Parser"
            [ admissionLimits
              sourceExcerpts
              malformedInputSweep
              declarativeDetection
              structure
              invalidEightEscape
              stepBlockPresence
              slashyPosition ])
