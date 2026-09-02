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

type private AdmissionNegativeCase =
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
              Expect.equal (err null).Code EmptySource "a null CLR caller is also refused"
          }

          test "oversized source is rejected before parsing" {
              let big = String.replicate 300_000 "x"
              Expect.equal (err big).Code SourceTooLarge "source_too_large"
          }

          test "source and scalar limits count UTF-8 bytes, not UTF-16 code units" {
              let sourceLimits =
                  { Limits.defaults with
                      MaxSourceBytes = 4 }

              Expect.isOk (Limits.precheck sourceLimits "éé") "two two-byte scalars fit exactly"
              Expect.isOk (Limits.precheck sourceLimits "😀") "one surrogate pair is four UTF-8 bytes"

              let astralSourceError =
                  match Limits.precheck { sourceLimits with MaxSourceBytes = 3 } "😀" with
                  | Error e -> e
                  | Ok() -> failtest "expected a four-byte surrogate pair to cross a three-byte limit"

              Expect.equal astralSourceError.Code SourceTooLarge "the source rejects an astral scalar by UTF-8 size"
              Expect.equal
                  astralSourceError.Message
                  "source is 4 UTF-8 bytes, limit is 3"
                  "the astral source count is exact"

              let sourceError =
                  match Limits.precheck { sourceLimits with MaxSourceBytes = 3 } "éé" with
                  | Error e -> e
                  | Ok() -> failtest "expected the UTF-8 source byte limit to reject"

              Expect.equal sourceError.Code SourceTooLarge "four UTF-8 bytes cross a three-byte source limit"
              Expect.equal sourceError.Message "source is 4 UTF-8 bytes, limit is 3" "the byte count is explicit"

              let scalarLimits =
                  { Limits.defaults with
                      MaxSourceBytes = 100
                      MaxScalarBytes = 4 }

              Expect.isOk (Limits.precheck scalarLimits "'éé'") "four UTF-8 content bytes fit exactly"
              Expect.isOk (Limits.precheck scalarLimits "'😀'") "one astral scalar fits the scalar limit exactly"

              let astralScalarError =
                  match Limits.precheck scalarLimits "'😀a'" with
                  | Error e -> e
                  | Ok() -> failtest "expected an astral scalar plus ASCII to cross the scalar limit"

              Expect.equal astralScalarError.Code ScalarTooLong "the scalar rejects astral content by UTF-8 size"
              Expect.equal astralScalarError.Position.Line 1L "the astral scalar line is exact"
              Expect.equal astralScalarError.Position.Column 6L "the astral scalar closing-column position is exact"

              let scalarError =
                  match Limits.precheck scalarLimits "'ééa'" with
                  | Error e -> e
                  | Ok() -> failtest "expected the UTF-8 scalar byte limit to reject"

              Expect.equal scalarError.Code ScalarTooLong "five UTF-8 content bytes cross a four-byte scalar limit"
              Expect.equal
                  scalarError.Message
                  "string literal exceeds 4 UTF-8 bytes"
                  "the scalar limit names its encoding"

              let unterminatedError =
                  match Limits.precheck scalarLimits "'ééa" with
                  | Error e -> e
                  | Ok() -> failtest "expected an overlong unterminated scalar"

              Expect.isOk (Limits.precheck scalarLimits "'éé") "an unterminated scalar may fit exactly"
              Expect.equal unterminatedError.Code ScalarTooLong "an unterminated scalar is bounded before parsing"
              Expect.equal unterminatedError.Position.Line 1L "the unterminated scalar EOF line is exact"
              Expect.equal unterminatedError.Position.Column 5L "the unterminated scalar EOF column is exact"
              Expect.equal
                  unterminatedError.Message
                  "string literal exceeds 4 UTF-8 bytes"
                  "the unterminated scalar reports the exact UTF-8 limit"

              Expect.isOk (Limits.precheck scalarLimits "'😀") "an unterminated astral scalar fits exactly"

              let unterminatedAstralError =
                  match Limits.precheck scalarLimits "'😀a" with
                  | Error e -> e
                  | Ok() -> failtest "expected overlong unterminated astral content"

              Expect.equal unterminatedAstralError.Position.Column 5L "astral EOF counts source columns, not bytes"
          }

          test "closing quotes use complete backslash-run parity" {
              let limits =
                  { Limits.defaults with
                      MaxSourceBytes = 100
                      MaxScalarBytes = 4 }

              Expect.isOk
                  (Limits.precheck limits "'aa\\\\'")
                  "two trailing backslashes leave the single quote as a delimiter"

              Expect.isOk
                  (Limits.precheck limits "\"aa\\\\\"")
                  "two trailing backslashes leave the double quote as a delimiter"

              let evenRunError =
                  match Limits.precheck { limits with MaxScalarBytes = 3 } "'aa\\\\'" with
                  | Error e -> e
                  | Ok() -> failtest "expected four content bytes to cross a three-byte limit"

              Expect.equal evenRunError.Code ScalarTooLong "the closing delimiter is excluded after an even run"
              Expect.equal evenRunError.Position.Column 7L "the even-run refusal points after the closing quote"

              Expect.isOk
                  (Limits.precheck limits "'aa\\'")
                  "one trailing backslash escapes the quote and leaves four exact content bytes at EOF"

              Expect.isOk
                  (Limits.precheck limits "\"aa\\\"")
                  "one trailing backslash escapes the double quote and leaves four exact content bytes at EOF"

              let oddRunError =
                  match Limits.precheck limits "'aa\\'a" with
                  | Error e -> e
                  | Ok() -> failtest "expected escaped-quote content plus ASCII to cross the limit"

              Expect.equal oddRunError.Code ScalarTooLong "an odd run keeps the quote in unterminated content"
              Expect.equal oddRunError.Position.Column 7L "the odd-run refusal points at EOF"

              let oddDoubleRunError =
                  match Limits.precheck limits "\"aa\\\"a" with
                  | Error e -> e
                  | Ok() -> failtest "expected escaped double-quote content plus ASCII to cross the limit"

              Expect.equal oddDoubleRunError.Code ScalarTooLong "an odd run keeps the double quote in content"
              Expect.equal oddDoubleRunError.Position.Column 7L "the double-quote odd-run refusal points at EOF"
          }

          test "ordinary quotes cannot shield later structure across a physical line ending" {
              let limits =
                  { Limits.defaults with
                      MaxSourceBytes = 10_000
                      MaxDepth = 0
                      MaxScalarBytes = 10_000 }

              let pipeline =
                  "pipeline { agent any stages { stage('B') { steps { echo 'x' } } } }"

              let refusal label result =
                  match result with
                  | Error e ->
                      Expect.equal e.Code MalformedSyntax $"{label}: the raw ending is a syntax refusal"
                      Expect.equal e.Position.Line 2L $"{label}: the refusal identifies the following physical line"
                      Expect.equal e.Position.Column 1L $"{label}: the refusal identifies the physical-line boundary"
                      e
                  | Ok _ -> failtestf "%s allowed an invalid quote to shield later structure" label

              for quoteLabel, quote in [ "single", "'"; "double", "\"" ] do
                  for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "CR", "\r" ] do
                      let label = $"{quoteLabel}/{newlineLabel}"
                      let invalid = quote + "bad" + newline + pipeline

                      Limits.precheck limits invalid
                      |> refusal (label + "/precheck")
                      |> ignore

                      Parser.parseWithLimits limits invalid
                      |> refusal (label + "/Declarative")
                      |> ignore

                      let continued = quote + "bad\\" + newline + "continued" + quote
                      Expect.isOk (Limits.precheck limits continued) $"{label}: an odd run continues the quote"

                      let evenRun = quote + "bad\\\\" + newline + "continued" + quote
                      Limits.precheck limits evenRun
                      |> refusal (label + "/even-run")
                      |> ignore

              Expect.isOk (Limits.precheck limits "'''a\nb'''") "triple-single quotes remain multiline"
              Expect.isOk (Limits.precheck limits "\"\"\"a\nb\"\"\"") "triple-double quotes remain multiline"
          }

          test "GString interpolation is structurally bounded before recursive parsing" {
              let depthLimits =
                  { Limits.defaults with
                      MaxSourceBytes = 10_000
                      MaxDepth = 2
                      MaxNodes = 10_000
                      MaxScalarBytes = 10_000 }

              let expectCode label expected result =
                  match result with
                  | Error e -> Expect.equal e.Code expected $"{label}: named admission refusal"
                  | Ok _ -> failtestf "%s bypassed the structural precheck" label

              for label, source in
                  [ "ordinary", "\"${(((1)))}\""
                    "triple-double", "\"\"\"${(((1)))}\"\"\"" ] do
                  Limits.precheck depthLimits source
                  |> expectCode (label + "/precheck") NestingTooDeep

                  Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits source
                  |> expectCode (label + "/Groovy") NestingTooDeep

              let deep =
                  String.replicate (Limits.defaults.MaxDepth + 16) "("
                  + "1"
                  + String.replicate (Limits.defaults.MaxDepth + 16) ")"

              let divisionGString = "\"${amount / " + deep + " / 2}\""

              Limits.precheck Limits.defaults divisionGString
              |> expectCode "division inside interpolation/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults divisionGString
              |> expectCode "division inside interpolation/Groovy" NestingTooDeep

              let declarativeDivision =
                  "pipeline { agent any stages { stage('B') { steps { script { def x = \"${amount / "
                  + deep
                  + " / 2}\" } } } } }"

              Parser.parseWithLimits Limits.defaults declarativeDivision
              |> expectCode "division inside interpolation/Declarative" NestingTooDeep

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits "\"${/(((())))/}\"")
                  "a primary-position slashy remains shielded inside interpolation"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits "\"${/}/}\"")
                  "a slashy closing brace does not end interpolation"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits "\"${tool 'M3'}\"")
                  "a supported command expression remains admitted inside interpolation"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits
                      { depthLimits with MaxDepth = 3 }
                      "\"${[1].each { echo /((((/ }}\"")
                  "a closure inside interpolation still begins a command-capable statement body"

              let nestedDivisionGString =
                  "\"${\"${amount / " + deep + " / 2}\"}\""

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults nestedDivisionGString
              |> expectCode "nested GString division" NestingTooDeep

              let scalarRestorationLimits =
                  { depthLimits with
                      MaxDepth = 64
                      MaxScalarBytes = 4 }

              Expect.isOk
                  (Limits.precheck scalarRestorationLimits "\"${1}\"")
                  "a closed interpolated scalar fits at its whole-content boundary"

              Limits.precheck scalarRestorationLimits "\"${1}a\""
              |> expectCode "closed interpolated scalar accounting" ScalarTooLong

              let shorthandNodeLimits =
                  { depthLimits with
                      MaxDepth = 64
                      MaxNodes = 1 }

              Limits.precheck shorthandNodeLimits "\"$a\""
              |> expectCode "shorthand GString/precheck" TooManyNodes

              Fogell.Groovy.Parser.Parser.parseWithLimits shorthandNodeLimits "\"$a\""
              |> expectCode "shorthand GString/Groovy" TooManyNodes

              let nodeLimits =
                  { depthLimits with
                      MaxDepth = 64
                      MaxNodes = 2 }

              Limits.precheck nodeLimits "\"${(((1)))}\""
              |> expectCode "interpolation nodes" TooManyNodes

              Expect.isOk
                  (Limits.precheck depthLimits "\"\\${(((1)))}\"")
                  "an escaped dollar remains literal GString content"

              Limits.precheck depthLimits "\"\\\\${(((1)))}\""
              |> expectCode "an even backslash run leaves interpolation live" NestingTooDeep

              Expect.isOk
                  (Limits.precheck depthLimits "'${(((1)))}'")
                  "single quotes do not interpolate"

              Expect.isOk
                  (Limits.precheck depthLimits "'''${(((1)))}'''")
                  "triple-single quotes do not interpolate"

              let unterminatedLimits =
                  { depthLimits with
                      MaxDepth = 64
                      MaxScalarBytes = 2 }

              Limits.precheck unterminatedLimits "\"${1"
              |> expectCode "unterminated GString scalar accounting" ScalarTooLong

              let declarativeLimits =
                  { depthLimits with
                      MaxDepth = 12 }

              let interpolationBomb = String.replicate 16 "(" + "1" + String.replicate 16 ")"

              let declarative =
                  "pipeline { agent any stages { stage('B') { steps { script { echo \"${"
                  + interpolationBomb
                  + "}\" } } } } }"

              Parser.parseWithLimits declarativeLimits declarative
              |> expectCode "Declarative script GString" NestingTooDeep
          }

          test "recursive unary chains are bounded before every Groovy route" {
              let depthLimits =
                  { Limits.defaults with
                      MaxSourceBytes = 10_000
                      MaxDepth = 4
                      MaxNodes = 10_000
                      MaxScalarBytes = 10_000 }

              let expectCode label expected result =
                  match result with
                  | Error e -> Expect.equal e.Code expected $"{label}: named admission refusal"
                  | Ok _ -> failtestf "%s bypassed unary admission" label

              let exact = String.replicate depthLimits.MaxDepth "!" + "true"
              let over = "!" + exact

              Expect.isOk (Limits.precheck depthLimits exact) "MaxDepth unary chain fits exactly"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits exact)
                  "the exact unary boundary reaches and survives the recursive grammar"

              let overError =
                  match Limits.precheck depthLimits over with
                  | Error e -> e
                  | Ok() -> failtest "the plus-one unary chain bypassed precheck"

              Expect.equal overError.Code NestingTooDeep "the plus-one unary chain has a depth refusal"
              Expect.equal overError.Position.Line 1L "the unary refusal line is exact"
              Expect.equal overError.Position.Column 6L "the fifth prefix operator is the refusal point"

              Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits over
              |> expectCode "direct Groovy unary chain" NestingTooDeep

              let triviaSeparated = "! /* block */ -\n// line\n! - true"

              Expect.isOk
                  (Limits.precheck depthLimits triviaSeparated)
                  "block comments, line comments and physical trivia preserve one exact unary chain"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits triviaSeparated)
                  "the recursive grammar agrees on the trivia-separated exact chain"

              let nodeLimits =
                  { depthLimits with
                      MaxDepth = 64
                      MaxNodes = 2 }

              Expect.isOk (Limits.precheck nodeLimits "!true") "one EUnary plus its operand fits two nodes"

              Limits.precheck nodeLimits "!!true"
              |> expectCode "unary EUnary node accounting/precheck" TooManyNodes

              Fogell.Groovy.Parser.Parser.parseWithLimits nodeLimits "!!true"
              |> expectCode "unary EUnary node accounting/Groovy" TooManyNodes

              let nested count unaryCount =
                  String.replicate count "("
                  + String.replicate unaryCount "!"
                  + "true"
                  + String.replicate count ")"

              let combinedExact = nested 63 1
              let combinedOver = nested 63 2

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults combinedExact)
                  "63 structural groups plus one unary frame fit the combined depth boundary"

              Limits.precheck Limits.defaults combinedOver
              |> expectCode "combined structural and unary depth/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults combinedOver
              |> expectCode "combined structural and unary depth/Groovy" NestingTooDeep

              let outerUnaryGrouped =
                  String.replicate 40 "!"
                  + nested 40 0

              Limits.precheck Limits.defaults outerUnaryGrouped
              |> expectCode "outer unary frames survive a grouped primary/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults outerUnaryGrouped
              |> expectCode "outer unary frames survive a grouped primary/Groovy" NestingTooDeep

              let interpolated unaryCount =
                  "\"${"
                  + nested 62 unaryCount
                  + "}\""

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults (interpolated 1))
                  "interpolation plus groups and one unary frame fit the combined boundary"

              Limits.precheck Limits.defaults (interpolated 2)
              |> expectCode "interpolated combined unary depth/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults (interpolated 2)
              |> expectCode "interpolated combined unary depth/Groovy" NestingTooDeep

              let outerUnaryInterpolation =
                  String.replicate 40 "!"
                  + "\"${"
                  + String.replicate 40 "!"
                  + "true}\""

              Limits.precheck Limits.defaults outerUnaryInterpolation
              |> expectCode "outer unary GString plus placeholder unary/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults outerUnaryInterpolation
              |> expectCode "outer unary GString plus placeholder unary/Groovy" NestingTooDeep

              let outerUnaryList =
                  String.replicate 40 "!"
                  + "[1, "
                  + String.replicate 40 "!"
                  + "true]"

              Limits.precheck Limits.defaults outerUnaryList
              |> expectCode "outer unary list plus later item unary/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults outerUnaryList
              |> expectCode "outer unary list plus later item unary/Groovy" NestingTooDeep

              let unary count = String.replicate count "!"

              let postfixCases outerCount innerCount =
                  let outer = unary outerCount
                  let inner = unary innerCount

                  [ "free call", outer + "foo(" + inner + "true)"
                    "constructor call", outer + "new Foo(" + inner + "true)"
                    "index", outer + "foo[" + inner + "true]"
                    "member call", outer + "foo.bar(" + inner + "true)"
                    "safe member call", outer + "foo?.bar(" + inner + "true)"
                    "spread member call", outer + "foo*.bar(" + inner + "true)"
                    "grouped receiver member call", outer + "(foo).bar(" + inner + "true)"
                    "list receiver member call", outer + "[foo].bar(" + inner + "true)"
                    "string receiver member call", outer + "'foo'.bar(" + inner + "true)"
                    "trailing closure", outer + "foo { return " + inner + "true }"
                    "member trailing closure", outer + "foo.bar { return " + inner + "true }" ]

              for label, source in postfixCases 40 40 do
                  Limits.precheck Limits.defaults source
                  |> expectCode (label + " retains outer unary/precheck") NestingTooDeep

                  Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults source
                  |> expectCode (label + " retains outer unary/Groovy") NestingTooDeep

              for label, source in postfixCases 40 24 do
                  Limits.precheck Limits.defaults source
                  |> expectCode (label + " plus-one postfix depth/precheck") NestingTooDeep

                  Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults source
                  |> expectCode (label + " plus-one postfix depth/Groovy") NestingTooDeep

              for label, source in postfixCases 40 22 do
                  if label <> "spread member call" then
                      Expect.isOk
                          (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults source)
                          $"{label}: unary, one postfix step and its suffix group fit exactly"

              let exactConstructor = unary 40 + "new Foo(" + unary 23 + "true)"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults exactConstructor)
                  "constructor arguments remain a primary, not a postfix-loop step"

              let exactSpreadCall = unary 40 + "foo*.bar(" + unary 21 + "true)"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults exactSpreadCall)
                  "spread property plus the following call are two exact postfix-loop steps"

              for label, source in postfixCases 0 0 do
                  Expect.isOk
                      (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults source)
                      $"{label}: every postfix form still parses at an ordinary depth"

              let siblingArguments =
                  unary 40
                  + "foo("
                  + unary 22
                  + "true, "
                  + unary 22
                  + "false)"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults siblingArguments)
                  "sibling call arguments each restart at the inherited outer-unary floor"

              let independentControls =
                  [ "binary seam", unary 40 + "foo + foo(" + unary 40 + "true)"
                    "semicolon seam", unary 40 + "foo; foo(" + unary 40 + "true)"
                    "newline index seam", unary 40 + "foo\n[" + unary 40 + "true]" ]

              for label, source in independentControls do
                  Expect.isOk (Limits.precheck Limits.defaults source) $"{label}: precheck resets the completed primary"

                  Expect.isOk
                      (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults source)
                      $"{label}: the recursive grammar agrees that the expressions are independent"

              let groupedNewlineIndexes =
                  [ "parenthesised", "(" + unary 40 + "foo\n[" + unary 24 + "true])"
                    "list", "[" + unary 40 + "foo\n[" + unary 24 + "true]]"
                    "GString interpolation", "\"${" + unary 40 + "foo\n[" + unary 24 + "true]}\"" ]

              for label, source in groupedNewlineIndexes do
                  Limits.precheck Limits.defaults source
                  |> expectCode (label + " newline index retains unary/precheck") NestingTooDeep

                  Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults source
                  |> expectCode (label + " newline index retains unary/Groovy") NestingTooDeep

              let statementBodyNewlineIndex =
                  "({ " + unary 40 + "foo\n[" + unary 40 + "true] })"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults statementBodyNewlineIndex)
                  "a nested statement body resets expression-group ownership before a newline list statement"

              let flatPostfixChain suffixCount =
                  "x" + String.replicate suffixCount ".x"

              let flatPostfixLimits =
                  { Limits.defaults with
                      MaxSourceBytes = 100_000
                      MaxDepth = 4
                      MaxNodes = 100
                      MaxScalarBytes = 100_000 }

              let exactFlatPostfix = flatPostfixChain flatPostfixLimits.MaxDepth
              let overFlatPostfix = flatPostfixChain (flatPostfixLimits.MaxDepth + 1)

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits flatPostfixLimits exactFlatPostfix)
                  "MaxDepth flat postfix-loop steps fit exactly"

              Limits.precheck flatPostfixLimits overFlatPostfix
              |> expectCode "plus-one flat postfix depth/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits flatPostfixLimits overFlatPostfix
              |> expectCode "plus-one flat postfix depth/Groovy" NestingTooDeep

              let flatPostfixNodeLimits =
                  { flatPostfixLimits with
                      MaxDepth = 64
                      MaxNodes = 5 }

              Expect.isOk
                  (Limits.precheck flatPostfixNodeLimits (flatPostfixChain 4))
                  "a primary plus four suffix identifiers fit five nodes exactly"

              Limits.precheck flatPostfixNodeLimits (flatPostfixChain 5)
              |> expectCode "plus-one flat postfix node/precheck" TooManyNodes

              Fogell.Groovy.Parser.Parser.parseWithLimits flatPostfixNodeLimits (flatPostfixChain 5)
              |> expectCode "plus-one flat postfix node/Groovy" TooManyNodes

              let sourceSizedPostfix =
                  flatPostfixChain (Limits.defaults.MaxNodes - 1)

              Limits.precheck Limits.defaults sourceSizedPostfix
              |> expectCode "MaxNodes-sized postfix chain/precheck" NestingTooDeep

              Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults sourceSizedPostfix
              |> expectCode "MaxNodes-sized postfix chain/Groovy" NestingTooDeep

              let exactCompoundPostfix =
                  [ 2, "x.y()"
                    2, "x?.y()"
                    2, "x() { true }"
                    2, "x.y { true }"
                    2, "x[true]"
                    3, "x*.y()"
                    3, "x()()"
                    3, "x() { true } { false }"
                    3, "x(y.z)" ]

              for maxDepth, source in exactCompoundPostfix do
                  Expect.isOk
                      (Fogell.Groovy.Parser.Parser.parseWithLimits { flatPostfixLimits with MaxDepth = maxDepth } source)
                      $"{source}: compound postfix steps meet their exact boundary"

              let unaryPostfixLimits =
                  { flatPostfixLimits with
                      MaxDepth = 4
                      MaxNodes = 16 }

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits unaryPostfixLimits "!!x.y.y")
                  "two unary plus two postfix frames fit the combined boundary"

              Fogell.Groovy.Parser.Parser.parseWithLimits unaryPostfixLimits "!!x.y.y.y"
              |> expectCode "plus-one combined unary/postfix chain" NestingTooDeep

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits flatPostfixLimits "(x.y.y.y)")
                  "one structural plus three postfix frames fit the combined boundary"

              Fogell.Groovy.Parser.Parser.parseWithLimits flatPostfixLimits "(x.y.y.y.y)"
              |> expectCode "plus-one combined structural/postfix chain" NestingTooDeep

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits flatPostfixLimits "\"$x.y.y.y.y\"")
                  "four shorthand GString property frames fit exactly"

              Fogell.Groovy.Parser.Parser.parseWithLimits flatPostfixLimits "\"$x.y.y.y.y.y\""
              |> expectCode "plus-one shorthand GString property chain" NestingTooDeep

              let hostile = String.replicate (Limits.defaults.MaxDepth + 1) "!" + "true"

              let scripted =
                  hostile
                  + "\npipeline { agent any stages { stage('B') { steps { echo 'x' } } } }"

              Parser.parseWithLimits Limits.defaults scripted
              |> expectCode "scripted preamble unary chain" NestingTooDeep

              let declarativeScript =
                  "pipeline { agent any stages { stage('B') { steps { script { def x = "
                  + hostile
                  + " } } } } }"

              Parser.parseWithLimits Limits.defaults declarativeScript
              |> expectCode "Declarative script unary chain" NestingTooDeep

              let opaqueDeclarative =
                  "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return "
                  + hostile
                  + " } } } }"

              Parser.parseWithLimits Limits.defaults opaqueDeclarative
              |> expectCode "opaque Declarative unary chain" NestingTooDeep

              let zeroUnaryDepth =
                  { Limits.defaults with
                      MaxDepth = 0 }

              Expect.isOk
                  (Limits.precheck zeroUnaryDepth "x-- / 2")
                  "postfix decrement is not charged as two recursive unary calls"

              Expect.isOk
                  (Limits.precheck Limits.defaults "[1].each { x -> !x }")
                  "a closure arrow remains distinct from unary minus"
          }

          test "balanced raw expressions reuse the admission slashy classification" {
              let limits =
                  { Limits.defaults with
                      MaxSourceBytes = 10_000
                      MaxScalarBytes = 4 }

              let expectAccepted label source =
                  match Parser.parseWithLimits limits source with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s should remain admitted, got %A" label e

              let expectScalarTooLong label source =
                  match Parser.parseWithLimits limits source with
                  | Error e -> Expect.equal e.Code ScalarTooLong $"{label}: the DFA-classified slashy is bounded"
                  | Ok _ -> failtestf "%s bypassed the scalar limit" label

              for label, exact, over in
                  [ "opaque stage extension",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return /aaaa/ } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return /aaaaa/ } } } }"
                    "escaped-hint opaque stage extension",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { \\/ /aaaa/ } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { \\/ /aaaaa/ } } } }"
                    "escaped-hint raw positional",
                    "pipeline { agent any stages { stage('B') { steps { echo \\/ /aaaa/ } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo \\/ /aaaaa/ } } } }"
                    "escaped-hint raw named",
                    "pipeline { agent any stages { stage('B') { steps { echo message: \\/ /aaaa/ } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo message: \\/ /aaaaa/ } } } }"
                    "command in collection",
                    "pipeline { agent any stages { stage('B') { steps { echo [{ echo /aaaa/ }] } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo [{ echo /aaaaa/ }] } } } }"
                    "unbraced control body",
                    "foo { if (ok) /aaaa/ }\npipeline { agent any stages { stage('B') { steps { echo 'x' } } } }",
                    "foo { if (ok) /aaaaa/ }\npipeline { agent any stages { stage('B') { steps { echo 'x' } } } }"
                    "isolated parenthesised reparse",
                    "pipeline { agent any stages { stage('B') { steps { echo(return /aaaa/) } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo(return /aaaaa/) } } } }"
                    "parenthesised named collection reparse",
                    "pipeline { agent any stages { stage('B') { steps { echo(values: [{ echo /a]aa/ }]) } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo(values: [{ echo /aa]aaa/ }]) } } } }"
                    "parenthesised nested-call reparse",
                    "pipeline { agent any stages { stage('B') { steps { echo(wrapper(return /a)aa/)) } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo(wrapper(return /aa)aaa/)) } } } }"
                    "labeled command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { deploy: echo /aaaa/ } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { deploy: echo /aaaaa/ } } } }"
                    "case command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { switch (x) { case 1: echo /aaaa/; break } } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { switch (x) { case 1: echo /aaaaa/; break } } } } }"
                    "default command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { switch (x) { default: echo /aaaa/ } } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { switch (x) { default: echo /aaaaa/ } } } } }"
                    "closure arrow command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { [1].each { x -> echo /aaaa/ } } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { [1].each { x -> echo /aaaaa/ } } } } }"
                    "empty closure arrow command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { run { -> echo /aaaa/ } } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { run { -> echo /aaaaa/ } } } } }"
                    "switch arrow command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { switch (x) { case 1 -> echo /aaaa/ } } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { switch (x) { case 1 -> echo /aaaaa/ } } } } }"
                    "return fallback command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return tool /aaaa/ } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return tool /aaaaa/ } } } }"
                    "return fallback command expression",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return tool /aaaa/ + rhs } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return tool /aaaaa/ + rhs } } } }"
                    "return newline command body",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return\ntool /aaaa/(x) } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return\ntool /aaaaa/(x) } } } }"
                    "return command before terminal comment",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return tool /aaaa/ // c\n} } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return tool /aaaaa/ // c\n} } } }" ] do
                  expectAccepted label exact
                  expectScalarTooLong label over

              for label, source in
                  [ "return division",
                    "pipeline { agent any stages { stage('B') { steps { foo { return amount / 2 / 5 } } } } }"
                    "command division",
                    "pipeline { agent any stages { stage('B') { steps { foo { echo amount / 2 / 5 } } } } }"
                    "collection division",
                    "pipeline { agent any stages { stage('B') { steps { echo [foo / aaaaa / b] } } } }"
                    "labeled numeric division",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { deploy: 10 / aaaaa / 2 } } } }"
                    "case numeric division",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { switch (x) { case 1: 10 / aaaaa / 2 } } } } }"
                    "return chained division",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return amount / aaaaa / b } } } }"
                    "return division across a line comment",
                    "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return tool / aaaaa / // c\necho 'x' } } } }" ] do
                  expectAccepted label source

              for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "CR", "\r" ] do
                  let newlineReturnPosition =
                      "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return"
                      + newline
                      + "tool /aaaaa/(x) } } } }"

                  match Parser.parseWithLimits limits newlineReturnPosition with
                  | Error e ->
                      Expect.equal e.Code ScalarTooLong $"{newlineLabel} return position: scalar refusal"
                      Expect.equal e.Position.Line 2L $"{newlineLabel} return position: physical line"
                      Expect.equal e.Position.Column 13L $"{newlineLabel} return position: after the committed closer"
                  | Ok _ -> failtestf "%s return position bypassed the scalar limit" newlineLabel

              let structuralLimits =
                  { limits with
                      MaxDepth = 5
                      MaxScalarBytes = 10_000 }

              let structuralBomb = String.replicate 8 "(" + "1" + String.replicate 8 ")"

              for label, source in
                  [ "map value division", "[deploy: amount / " + structuralBomb + " / 2]"
                    "named value division", "echo message: amount / " + structuralBomb + " / 2"
                    "case ternary division",
                    "switch(x) { case ok ? left : amount / " + structuralBomb + " / 2: break }"
                    "case map division",
                    "switch(x) { case [k: amount / " + structuralBomb + " / 2]: break }"
                    "return division across a line comment",
                    "return tool / " + structuralBomb + " / // c\necho 'x'" ] do
                  match Limits.precheck structuralLimits source with
                  | Error e -> Expect.equal e.Code NestingTooDeep $"{label}: division structure remains visible"
                  | Ok _ -> failtestf "%s let a colon promote division to slashy shielding" label

              let spanSource = "return /aaaa/"

              let spans =
                  match Limits.precheckWithSlashySpans Limits.defaults spanSource with
                  | Ok classified -> classified
                  | Error e -> failtestf "slashy boundary control failed precheck: %A" e

              Expect.equal (spans.Boundary(7)) (Complete 12) "the complete closer is exported without a sentinel"

              Expect.equal
                  (spans.Slice(7, 3).Boundary(0))
                  (Incomplete 3)
                  "a closer beyond an isolated reparse is its local EOF boundary"

              let escapedHintSource = "\\/ /a/"

              let escapedHintSpans =
                  match Limits.precheckWithSlashySpans Limits.defaults escapedHintSource with
                  | Ok classified -> classified
                  | Error e -> failtestf "escaped-hint boundary control failed precheck: %A" e

              Expect.equal
                  (escapedHintSpans.Boundary(1))
                  NonConsuming
                  "an immediately escaped slash is explicitly non-consuming"

              Expect.equal
                  (escapedHintSpans.Boundary(3))
                  (Complete 5)
                  "the later real opener keeps its independent complete boundary"

              let escapedHintSlice = escapedHintSpans.Slice(1, 5)
              Expect.equal (escapedHintSlice.Boundary(0)) NonConsuming "a slice preserves the non-consuming hint"
              Expect.equal (escapedHintSlice.Boundary(2)) (Complete 4) "a slice rebases the later real opener"

              for label, source, slashIndex in
                  [ "EOF", "\\/", 1
                    "LF", "\\/\n", 1
                    "CRLF", "\\/\r\n", 1
                    "CR", "\\/\r", 1 ] do
                  match Limits.precheckWithSlashySpans Limits.defaults source with
                  | Ok classified ->
                      Expect.equal
                          (classified.Boundary(slashIndex))
                          NonConsuming
                          $"{label}: an escaped slash never becomes an incomplete slashy"
                  | Error e -> failtestf "%s escaped non-consuming control failed: %A" label e

              for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "CR", "\r" ] do
                  let prefix =
                      "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { return /"
                      + String.replicate 64 "\\/"

                  let source = prefix + newline + "tail/ } } } }"

                  match Parser.parseWithLimits Limits.defaults source with
                  | Error e ->
                      Expect.equal e.Code MalformedSyntax $"{newlineLabel}: incomplete classified slashy refuses"
                      Expect.equal e.Position.Line 1L $"{newlineLabel}: refusal stays before the physical ending"
                      Expect.equal
                          e.Position.Column
                          (int64 prefix.Length + 1L)
                          $"{newlineLabel}: cached boundary preserves the exact refusal column"
                  | Ok _ -> failtestf "%s incomplete classified slashy parsed" newlineLabel

              let escapedSlashAdversary =
                  "pipeline { agent any stages { stage('B') { steps { echo 'x' } foo { "
                  + String.replicate 80_000 "\\/"
                  + " } } } }"

              let escapedSlashStarted = Diagnostics.Stopwatch.StartNew()
              let escapedSlashResult = Parser.parseWithLimits Limits.defaults escapedSlashAdversary
              escapedSlashStarted.Stop()

              match escapedSlashResult with
              | Ok _ -> ()
              | Error e -> failtestf "source-sized escaped-slash opaque block failed: %A" e

              Expect.isLessThan
                  escapedSlashStarted.Elapsed.TotalSeconds
                  2.0
                  "cached incomplete boundaries keep public balancedRaw parsing bounded-linear"

              let rawArgumentAdversary =
                  "pipeline { agent any stages { stage('B') { steps { input(message: "
                  + String.replicate 80_000 "\\/"
                  + ") } } } } }"

              let rawArgumentStarted = Diagnostics.Stopwatch.StartNew()
              let rawArgumentResult = Parser.parseWithLimits Limits.defaults rawArgumentAdversary
              rawArgumentStarted.Stop()

              match rawArgumentResult with
              | Ok _ -> ()
              | Error e -> failtestf "source-sized escaped-slash raw argument failed: %A" e

              Expect.isLessThan
                  rawArgumentStarted.Elapsed.TotalSeconds
                  2.0
                  "sliced cached incomplete boundaries keep public rawArgValue parsing bounded-linear"
          }

          test "return command slashy fallback exposes speculative division depth" {
              let limits =
                  { Limits.defaults with
                      MaxDepth = 10
                      MaxSourceBytes = 10_000
                      MaxScalarBytes = 10_000 }

              let grouped count =
                  String.replicate count "(" + "true" + String.replicate count ")"

              let direct count = "return tool / " + grouped count + " /;"

              let scripted count =
                  "pipeline { agent any stages { stage('B') { steps { script { "
                  + direct count
                  + " } } } } } }"

              let scriptLimits =
                  { limits with MaxDepth = 15 }

              Expect.isOk
                  (Limits.precheck limits (direct 10))
                  "the exact direct speculative-division depth remains admitted"

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits limits (direct 10))
                  "the exact direct return-command slashy fallback remains compatible"

              for label, result in
                  [ "direct precheck", Limits.precheck limits (direct 11)
                    "direct Groovy", Fogell.Groovy.Parser.Parser.parseWithLimits limits (direct 11) |> Result.map ignore
                    "Declarative script", Parser.parseWithLimits scriptLimits (scripted 6) |> Result.map ignore ] do
                  match result with
                  | Error e -> Expect.equal e.Code NestingTooDeep $"{label}: speculative division depth is bounded"
                  | Ok _ -> failtestf "%s hid plus-one depth inside the eventual slashy fallback" label

              Expect.isOk
                  (Parser.parseWithLimits scriptLimits (scripted 5))
                  "the exact Declarative script depth retains return-command slashy compatibility"

              let scalarExact =
                  { limits with
                      MaxDepth = Limits.defaults.MaxDepth
                      MaxScalarBytes = 4 }

              Expect.isOk
                  (Fogell.Groovy.Parser.Parser.parseWithLimits scalarExact "return tool /aaaa/;")
                  "the exact scalar return-command slashy remains admitted"

              match Fogell.Groovy.Parser.Parser.parseWithLimits scalarExact "return tool /aaaaa/;" with
              | Error e -> Expect.equal e.Code ScalarTooLong "the fallback slashy still owns scalar admission"
              | Ok _ -> failtest "the structurally visible fallback slashy bypassed its scalar limit"
          }

          test "all parser-supported scalar delimiters enforce UTF-8 content bytes" {
              let limits =
                  { Limits.defaults with
                      MaxSourceBytes = 1_000
                      MaxScalarBytes = 4 }

              let pipeline scalar =
                  mk ("    stage('B') { steps { sh(" + scalar + ") } }")

              let expectAccepted label scalar =
                  match Parser.parseWithLimits limits (pipeline scalar) with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s should parse at the exact scalar boundary, got %A" label e

              let expectScalarTooLong label scalar =
                  match Parser.parseWithLimits limits (pipeline scalar) with
                  | Error e -> Expect.equal e.Code ScalarTooLong $"{label} is refused by the scalar limit"
                  | Ok _ -> failtestf "%s bypassed the scalar limit" label

              let expectGroovyAccepted label source =
                  match Fogell.Groovy.Parser.Parser.parseWithLimits limits source with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s should parse at the exact scalar boundary, got %A" label e

              let expectGroovyScalarTooLong label source =
                  match Fogell.Groovy.Parser.Parser.parseWithLimits limits source with
                  | Error e -> Expect.equal e.Code ScalarTooLong $"{label} is refused by the scalar limit"
                  | Ok _ -> failtestf "%s bypassed the scalar limit" label

              let tripleSingle content = "'''" + content + "'''"
              let tripleDouble content = "\"\"\"" + content + "\"\"\""

              expectAccepted "triple-single" (tripleSingle "aa'a")
              expectScalarTooLong "triple-single" (tripleSingle "aaaa'bbbb'cccc")
              expectAccepted "UTF-8 triple-single" (tripleSingle "éé")
              expectScalarTooLong "UTF-8 triple-single" (tripleSingle "ééa")
              expectAccepted "triple-double" (tripleDouble "aa\"a")
              expectScalarTooLong "triple-double" (tripleDouble "aaaa\"bbbb\"cccc")
              expectAccepted "UTF-8 triple-double" (tripleDouble "éé")
              expectScalarTooLong "UTF-8 triple-double" (tripleDouble "ééa")
              expectAccepted "slashy" "/aaaa/"
              expectScalarTooLong "slashy" "/aaaaa/"
              expectAccepted "UTF-8 slashy" "/éé/"
              expectScalarTooLong "UTF-8 slashy" "/ééa/"
              expectAccepted "pipeline escaped-slash" "/aa\\//"
              expectScalarTooLong "pipeline escaped-slash" "/aa\\/a/"
              expectAccepted "pipeline even backslash run plus escaped slash" "/a\\\\//"
              expectScalarTooLong "pipeline even backslash run plus escaped slash" "/aa\\\\//"

              for label, exact, over in
                  [ "return keyword", "return /aaaa/", "return /aaaaa/"
                    "throw keyword", "throw /aaaa/", "throw /aaaaa/"
                    "case keyword",
                    "switch (x) { case /aaaa/: break }",
                    "switch (x) { case /aaaaa/: break }"
                    "in keyword", "for (x in /aaaa/) { break }", "for (x in /aaaaa/) { break }"
                    "command call", "echo /aaaa/", "echo /aaaaa/"
                    "unbraced body", "if (true) /aaaa/", "if (true) /aaaaa/"
                    "new statement", "return\n/aaaa/", "return\n/aaaaa/"
                    "block seam", "if (true) {}\n/aaaa/", "if (true) {}\n/aaaaa/" ] do
                  expectGroovyAccepted label exact
                  expectGroovyScalarTooLong label over

              let basePipeline =
                  "pipeline { agent any stages { stage('B') { steps { echo 'x' } } } }"

              let pipelineCases =
                  [ "stage name",
                    "pipeline { agent any stages { stage /aaaa/ { steps { echo 'x' } } } }",
                    "pipeline { agent any stages { stage /aaaaa/ { steps { echo 'x' } } } }"
                    "agent label",
                    "pipeline { agent label /aaaa/ stages { stage('B') { steps { echo 'x' } } } }",
                    "pipeline { agent label /aaaaa/ stages { stage('B') { steps { echo 'x' } } } }"
                    "tool entry",
                    "pipeline { agent any tools { maven /aaaa/ } stages { stage('B') { steps { echo 'x' } } } }",
                    "pipeline { agent any tools { maven /aaaaa/ } stages { stage('B') { steps { echo 'x' } } } }"
                    "when condition",
                    "pipeline { agent any stages { stage('B') { when { branch /aaaa/ } steps { echo 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { when { branch /aaaaa/ } steps { echo 'x' } } } }"
                    "step argument",
                    "pipeline { agent any stages { stage('B') { steps { echo /aaaa/ } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo /aaaaa/ } } } }"
                    "raw positional expression",
                    "pipeline { agent any stages { stage('B') { steps { echo /aaaa/ + 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo /aaaaa/ + 'x' } } } }"
                    "raw quote-bearing slashy expression",
                    "pipeline { agent any stages { stage('B') { steps { echo /a'aa/ + 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo /aa'aa/ + 'x' } } } }"
                    "raw double-quote-bearing slashy expression",
                    "pipeline { agent any stages { stage('B') { steps { echo /a\"aa/ + 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo /aa\"aa/ + 'x' } } } }"
                    "list-nested slashy",
                    "pipeline { agent any stages { stage('B') { steps { echo [/aaaa/] } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo [/aaaaa/] } } } }"
                    "parenthesised list-nested slashy",
                    "pipeline { agent any stages { stage('B') { steps { echo([/aaaa/]) } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo([/aaaaa/]) } } } }"
                    "named map-nested slashy",
                    "pipeline { agent any stages { stage('B') { steps { echo message: [x: /aaaa/] } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo message: [x: /aaaaa/] } } } }"
                    "raw parenthesised positional expression",
                    "pipeline { agent any stages { stage('B') { steps { echo(/aaaa/ + 'x') } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo(/aaaaa/ + 'x') } } } }"
                    "raw named expression",
                    "pipeline { agent any stages { stage('B') { steps { echo message: /aaaa/ + env.X } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo message: /aaaaa/ + env.X } } } }"
                    "raw parenthesised named expression",
                    "pipeline { agent any stages { stage('B') { steps { echo(message: /aaaa/ + env.X) } } } }",
                    "pipeline { agent any stages { stage('B') { steps { echo(message: /aaaaa/ + env.X) } } } }"
                    "named branch condition",
                    "pipeline { agent any stages { stage('B') { when { branch pattern: /aaaa/ } steps { echo 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { when { branch pattern: /aaaaa/ } steps { echo 'x' } } } }"
                    "named branch nested slashy",
                    "pipeline { agent any stages { stage('B') { when { branch pattern: [/aaaa/] } steps { echo 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { when { branch pattern: [/aaaaa/] } steps { echo 'x' } } } }"
                    "named environment condition",
                    "pipeline { agent any stages { stage('B') { when { environment name: /aaaa/, value: 'x' } steps { echo 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { when { environment name: /aaaaa/, value: 'x' } steps { echo 'x' } } } }"
                    "named equals condition",
                    "pipeline { agent any stages { stage('B') { when { equals expected: /aaaa/, actual: /aaaa/ } steps { echo 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { when { equals expected: /aaaaa/, actual: /aaaaa/ } steps { echo 'x' } } } }"
                    "script body",
                    "pipeline { agent any stages { stage('B') { steps { script { echo /aaaa/ } } } } }",
                    "pipeline { agent any stages { stage('B') { steps { script { echo /aaaaa/ } } } } }"
                    "when expression",
                    "pipeline { agent any stages { stage('B') { when { expression { echo /aaaa/ } } steps { echo 'x' } } } }",
                    "pipeline { agent any stages { stage('B') { when { expression { echo /aaaaa/ } } steps { echo 'x' } } } }"
                    "preamble", "echo /aaaa/\n" + basePipeline, "echo /aaaaa/\n" + basePipeline
                    "epilogue", basePipeline + "\necho /aaaa/", basePipeline + "\necho /aaaaa/" ]

              for label, exact, over in pipelineCases do
                  match Parser.parseWithLimits limits exact with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s should parse at the exact scalar boundary, got %A" label e

                  match Parser.parseWithLimits limits over with
                  | Error e -> Expect.equal e.Code ScalarTooLong $"{label} is refused by the scalar limit"
                  | Ok _ -> failtestf "%s bypassed the scalar limit" label

              let positionAfterLastSlash (source: string) =
                  let slash = source.LastIndexOf('/')
                  let line = 1L + int64 (source.Substring(0, slash) |> Seq.filter ((=) '\n') |> Seq.length)
                  let lastNewline = source.LastIndexOf('\n', slash)
                  { Line = line
                    Column = int64 (slash - lastNewline + 1) }

              let expectPositionedScalar label source =
                  match Parser.parseWithLimits limits source with
                  | Error e ->
                      Expect.equal e.Code ScalarTooLong $"{label} is a scalar refusal"
                      Expect.equal e.Position (positionAfterLastSlash source) $"{label} is positioned in the Jenkinsfile"
                  | Ok _ -> failtestf "%s bypassed the scalar limit" label

              let scalarAt column =
                  AdmissionError.at ScalarTooLong 1L column "synthetic nested scalar refusal"

              let beforeNestedScan = Lexeme.parserStateWithLimits limits
              let provisional = scalarAt 57L
              let exactNested = scalarAt 66L

              let afterNestedScan =
                  { beforeNestedScan with
                      ScalarRefusal = Some provisional
                      RawArgumentLineBoundary = true }

              let refinedNested =
                  Parser.refineScalarAfterIsolatedReparse beforeNestedScan afterNestedScan exactNested

              Expect.equal
                  refinedNested.ScalarRefusal
                  (Some exactNested)
                  "a nested parse replaces provisional scalar state from its enclosing scan"

              Expect.isTrue
                  refinedNested.RawArgumentLineBoundary
                  "scalar refinement preserves unrelated state produced by the enclosing scan"

              let earlierBeforeNestedScan =
                  { beforeNestedScan with
                      ScalarRefusal = Some(scalarAt 10L) }

              let afterEarlierNestedScan =
                  { earlierBeforeNestedScan with
                      RawArgumentLineBoundary = true }

              let refinedEarlier =
                  Parser.refineScalarAfterIsolatedReparse
                      earlierBeforeNestedScan
                      afterEarlierNestedScan
                      exactNested

              Expect.equal
                  refinedEarlier.ScalarRefusal
                  earlierBeforeNestedScan.ScalarRefusal
                  "a nested parse retains scalar state that predates its enclosing scan"

              for label, source in
                  [ "parenthesised reparse",
                    "pipeline { agent any stages { stage('B') { steps { echo(/aaaaa/) } } } }"
                    "failed parenthesised reparse",
                    "pipeline { agent any stages { stage('B') { steps { echo(/aaaaa/ +) } } } }"
                    "parenthesised CRLF reparse",
                    "pipeline { agent any stages { stage('B') { steps { echo(\r\n /aaaaa/\r\n) } } } }"
                    "nested script",
                    "pipeline { agent any stages { stage('B') { steps { script {\n echo /aaaaa/\n} } } } }"
                    "nested when expression",
                    "pipeline { agent any stages { stage('B') { when { expression {\n echo /aaaaa/\n} } steps { echo 'x' } } } }"
                    "preamble", "echo /aaaaa/\n" + basePipeline
                    "epilogue", basePipeline + "\necho /aaaaa/"
                    "same-line epilogue", basePipeline + " echo /aaaaa/" ] do
                  expectPositionedScalar label source

              let malformedBefore scalar = ")\ndef later = " + scalar + "\n"

              for label, source in
                  [ "malformed preamble before slashy", malformedBefore "/aaaaa/" + basePipeline
                    "malformed epilogue before slashy", basePipeline + "\n" + malformedBefore "/aaaaa/" ] do
                  expectPositionedScalar label source

              for label, source in
                  [ "bounded slashy after malformed preamble", malformedBefore "/aaaa/" + basePipeline
                    "bounded slashy after malformed epilogue", basePipeline + "\n" + malformedBefore "/aaaa/"
                    "division after malformed preamble", ")\ndef later = amount / aaaaa / 2\n" + basePipeline
                    "return division after malformed preamble",
                    ")\nreturn tool/ aaaaa /+1\n" + basePipeline
                    "masked division after malformed preamble",
                    ")\ndef later = \\/ amount / aaaaa / 2\n" + basePipeline
                    "masked return division after malformed preamble",
                    ")\ndef later = \\/ return tool/ aaaaa /+1\n" + basePipeline
                    "slashy-looking comments after malformed preamble",
                    ")\n// /aaaaa/\n/* /aaaaa/ */\n" + basePipeline ] do
                  Expect.isOk
                      (Parser.parseWithLimits limits source)
                      $"{label}: recovery preserves surrounding-source tolerance without inventing a scalar"

              match Fogell.Groovy.Parser.Parser.parseWithLimits limits (malformedBefore "/aaaaa/") with
              | Error e ->
                  Expect.equal e.Code ScalarTooLong "direct Groovy recovery enforces the classified slashy cap"
                  Expect.equal e.Position { Line = 2L; Column = 20L } "the recovered closer position is exact"
              | Ok _ -> failtest "malformed Groovy hid a later overlong classified slashy"

              match Fogell.Groovy.Parser.Parser.parseWithLimits limits ")\nreturn tool/ aaaaa /+1" with
              | Error e ->
                  Expect.equal e.Code MalformedSyntax "an ambiguous return-command boundary remains division"
              | Ok _ -> failtest "the malformed return-division control unexpectedly parsed"

              let poisonedSlashy scalar = ")\ndef later = \\/ " + scalar + "\n"

              for label, source in
                  [ "masked slashy in malformed preamble", poisonedSlashy "/aaaaa/" + basePipeline
                    "masked slashy in malformed epilogue", basePipeline + "\n" + poisonedSlashy "/aaaaa/" ] do
                  expectPositionedScalar label source

              let terminalReturnSlashy = ")\ndef helper() { return tool /aaaaa/; }\n"
              let commentedReturnSlashy = ")\ndef helper() { return tool/*c*//aaaaa/; }\n"
              let boundedCommentedReturnSlashy = ")\ndef helper() { return tool/*c*//aaaa/; }\n"

              for label, source in
                  [ "terminal return slashy in malformed preamble", terminalReturnSlashy + basePipeline
                    "terminal return slashy in malformed epilogue", basePipeline + "\n" + terminalReturnSlashy
                    "commented return slashy in malformed preamble", commentedReturnSlashy + basePipeline
                    "commented return slashy in malformed epilogue",
                    basePipeline + "\n" + commentedReturnSlashy ] do
                  expectPositionedScalar label source

              for label, source in
                  [ "bounded commented return slashy in malformed preamble",
                    boundedCommentedReturnSlashy + basePipeline
                    "bounded commented return slashy in malformed epilogue",
                    basePipeline + "\n" + boundedCommentedReturnSlashy ] do
                  Expect.isOk
                      (Parser.parseWithLimits limits source)
                      $"{label}: parser-equivalent inline trivia preserves a bounded slashy"

              for label, source in
                  [ "bounded masked slashy in malformed preamble", poisonedSlashy "/aaaa/" + basePipeline
                    "bounded masked slashy in malformed epilogue",
                    basePipeline + "\n" + poisonedSlashy "/aaaa/" ] do
                  Expect.isOk
                      (Parser.parseWithLimits limits source)
                      $"{label}: recovery resynchronization preserves a bounded scalar"

              let recoveryLimits =
                  { limits with
                      MaxSourceBytes = 100_000
                      MaxNodes = 100_000 }

              let recoveryAdversary = ")\n" + String.replicate 10_000 "x=/a/;"
              let recoveryStarted = Diagnostics.Stopwatch.StartNew()
              let recoveryResult = Fogell.Groovy.Parser.Parser.parseWithLimits recoveryLimits recoveryAdversary
              recoveryStarted.Stop()

              match recoveryResult with
              | Error e -> Expect.equal e.Code MalformedSyntax "bounded classified spans do not become scalar errors"
              | Ok _ -> failtest "the malformed recovery adversary unexpectedly parsed"

              Expect.isLessThan
                  recoveryStarted.Elapsed.TotalSeconds
                  2.0
                  "grammar-failure slashy recovery walks cached complete spans once"

              let escapedHintAdversary = ")\ndef x = " + String.replicate 10_000 "\\/ " + "/aaaa/"
              let escapedHintStarted = Diagnostics.Stopwatch.StartNew()
              let escapedHintResult = Fogell.Groovy.Parser.Parser.parseWithLimits recoveryLimits escapedHintAdversary
              escapedHintStarted.Stop()

              match escapedHintResult with
              | Error e -> Expect.equal e.Code MalformedSyntax "non-consuming hints do not invent a scalar"
              | Ok _ -> failtest "the malformed escaped-hint adversary unexpectedly parsed"

              Expect.isLessThan
                  escapedHintStarted.Elapsed.TotalSeconds
                  2.0
                  "repeated non-consuming hints never trigger suffix scans"

              let positionedScalarError label result =
                  match result with
                  | Error e when e.Code = ScalarTooLong -> e
                  | Error e -> failtestf "%s returned the wrong refusal: %A" label e
                  | Ok _ -> failtestf "%s admitted an overlong scalar" label

              let firstGroovy = "def first = /aaaaa/\n"

              let firstGroovyError =
                  Fogell.Groovy.Parser.Parser.parseWithLimits limits firstGroovy
                  |> positionedScalarError "first Groovy scalar"

              let twoGroovyScalars = firstGroovy + "def second = /bbbbbb/\n"

              Expect.equal
                  (Fogell.Groovy.Parser.Parser.parseWithLimits limits twoGroovyScalars
                   |> positionedScalarError "two Groovy scalars")
                  firstGroovyError
                  "a later Groovy scalar cannot displace the first positioned refusal"

              let pipelineWithSteps body =
                  mk $"    stage('B') {{ steps {{\n{body}\n    }} }}"

              let firstPipelineBody = "      echo /aaaaa/"
              let firstPipeline = pipelineWithSteps firstPipelineBody

              let firstPipelineError =
                  Parser.parseWithLimits limits firstPipeline
                  |> positionedScalarError "first Declarative scalar"

              for label, laterStep in
                  [ "direct slashy", "      echo /bbbbbb/"
                    "balanced nested value", "      echo [/bbbbbb/]"
                    "raw positional expression", "      echo /bbbbbb/ + env.X"
                    "successful parenthesised reparse", "      echo(/bbbbbb/)"
                    "failed parenthesised reparse", "      echo(/bbbbbb/ +)"
                    "semantically failed parenthesised reparse",
                    "      sh(script: /bbbbbb/, returnStatus: true, returnStatus: false)"
                    "nested script validation", "      script { def later = /bbbbbb/ }" ] do
                  let source = pipelineWithSteps (firstPipelineBody + "\n" + laterStep)

                  Expect.equal
                      (Parser.parseWithLimits limits source
                       |> positionedScalarError label)
                      firstPipelineError
                      $"{label}: a later scalar cannot displace the first positioned refusal"

              expectPositionedScalar
                  "semantic failure commits its own scalar without an earlier refusal"
                  (pipelineWithSteps "      sh(script: /bbbbbb/, returnStatus: true, returnStatus: false)")

              let successfulFallbackWithRefusal = "      echo(/bbbbbb/, bad: '\\8')"

              let successfulFallbackError =
                  pipelineWithSteps successfulFallbackWithRefusal
                  |> Parser.parseWithLimits limits
                  |> positionedScalarError "successful fallback with a semantic refusal"

              Expect.equal
                  (pipelineWithSteps
                      (successfulFallbackWithRefusal + "\n      echo(message: 'x', message: 'y')")
                   |> Parser.parseWithLimits limits
                   |> positionedScalarError "successful fallback before a later structural failure")
                  successfulFallbackError
                  "a semantic refusal from a successful isolated reparse commits its scalar"

              Expect.equal
                  (pipelineWithSteps
                      (successfulFallbackWithRefusal
                       + "\n      echo(/ccccccc/, bad: '\\8')"
                       + "\n      echo(message: 'x', message: 'y')")
                   |> Parser.parseWithLimits limits
                   |> positionedScalarError "two committed scalar refusals")
                  successfulFallbackError
                  "a later committed scalar cannot overwrite the first committed refusal"

              let earlierPreambleError =
                  ("def first = /aaaaa/\n" + pipelineWithSteps "      echo 'x'")
                  |> Parser.parseWithLimits limits
                  |> positionedScalarError "preamble before a bounded body"

              Expect.equal
                  (("def first = /aaaaa/\n"
                    + pipelineWithSteps "      sh(script: /bbbbbb/, returnStatus: true, returnStatus: false)")
                   |> Parser.parseWithLimits limits
                   |> positionedScalarError "preamble before a committed body refusal")
                  earlierPreambleError
                  "preamble validation precedes a later committed body refusal"

              let helperPreamble = "def helper() { return /aaaaa/ }\n"

              let helperPreambleError =
                  (helperPreamble + pipelineWithSteps "      echo 'x'")
                  |> Parser.parseWithLimits limits
                  |> positionedScalarError "balanced helper preamble"

              let nestedHelperPreambleError =
                  Fogell.Groovy.Parser.Parser.parseWithLimits limits helperPreamble
                  |> positionedScalarError "standalone helper preamble"

              Expect.equal
                  helperPreambleError
                  nestedHelperPreambleError
                  "top-level preamble validation exposes the exact nested Groovy refusal"

              Expect.equal
                  helperPreambleError.Position
                  { Line = 1L; Column = 30L }
                  "nested Groovy validation refines the preamble balanced-scan position"

              Expect.equal
                  ((helperPreamble
                    + pipelineWithSteps "      sh(script: /bbbbbb/, returnStatus: true, returnStatus: false)")
                   |> Parser.parseWithLimits limits
                   |> positionedScalarError "balanced helper before a committed body refusal")
                  helperPreambleError
                  "an exact earlier helper refusal remains authoritative over the body"

              let boundedScalarDuplicate =
                  pipelineWithSteps "      sh(script: /bbbb/, returnStatus: true, returnStatus: false)"

              match Parser.parseWithLimits limits boundedScalarDuplicate with
              | Error e ->
                  Expect.equal e.Code MalformedSyntax "an in-bound scalar leaves duplicate-name refusal authoritative"
                  Expect.stringContains e.Message "duplicate named argument `returnStatus`" "the duplicate key survives"
              | Ok _ -> failtest "a bounded duplicate named argument parsed"

              let pipelineWithWhenScalar scalar =
                  mk
                      ("    stage('A') { steps { echo /aaaaa/ } }\n"
                       + $"    stage('B') {{ when {{ expression {{ def later = {scalar} }} }} steps {{ echo 'x' }} }}")

              let firstWhenError =
                  Parser.parseWithLimits limits (pipelineWithWhenScalar "/bbbb/")
                  |> positionedScalarError "first scalar before a bounded when expression"

              Expect.equal
                  (Parser.parseWithLimits limits (pipelineWithWhenScalar "/bbbbbb/")
                   |> positionedScalarError "nested when validation")
                  firstWhenError
                  "when-expression validation cannot displace the first positioned refusal"

              let pipelineThenEpilogue = firstPipeline + "def later = /bbbbbb/\n"

              Expect.equal
                  (Parser.parseWithLimits limits pipelineThenEpilogue
                   |> positionedScalarError "epilogue validation")
                  firstPipelineError
                  "epilogue validation cannot displace the first positioned refusal"

              let firstPreamble = "def first = /aaaaa/\n" + basePipeline

              let firstPreambleError =
                  Parser.parseWithLimits limits firstPreamble
                  |> positionedScalarError "first preamble scalar"

              for label, laterStep in
                  [ "direct pipeline body", "      echo /bbbbbb/"
                    "raw pipeline body", "      echo /bbbbbb/ + env.X"
                    "balanced pipeline body", "      echo [/bbbbbb/]"
                    "parenthesised pipeline body", "      echo(/bbbbbb/)"
                    "script pipeline body", "      script { def later = /bbbbbb/ }" ] do
                  let preambleThenBody = "def first = /aaaaa/\n" + pipelineWithSteps laterStep

                  Expect.equal
                      (Parser.parseWithLimits limits preambleThenBody
                       |> positionedScalarError label)
                      firstPreambleError
                      $"{label}: delayed preamble validation must restore source order"

              let preambleThenWhenBody =
                  "def first = /aaaaa/\n"
                  + mk "    stage('B') { when { expression { def later = /bbbbbb/ } } steps { echo 'x' } }"

              Expect.equal
                  (Parser.parseWithLimits limits preambleThenWhenBody
                   |> positionedScalarError "when-expression pipeline body")
                  firstPreambleError
                  "delayed preamble validation precedes a later when-expression refusal"

              let boundedPreambleThenBody =
                  "def first = /aaaa/\n" + pipelineWithSteps "      echo /bbbbbb/"

              expectPositionedScalar
                  "bounded preamble before overlong body"
                  boundedPreambleThenBody

              let preambleThenEpilogue = firstPreamble + "\ndef later = /bbbbbb/"

              Expect.equal
                  (Parser.parseWithLimits limits preambleThenEpilogue
                   |> positionedScalarError "preamble and epilogue validation")
                  firstPreambleError
                  "epilogue validation cannot displace a preamble refusal"

              expectGroovyAccepted
                  "escaped slash"
                  "/aa\\//"

              expectGroovyScalarTooLong
                  "raw escaped-slash bytes"
                  "/aa\\/a/"

              expectGroovyAccepted
                  "Groovy even backslash run plus escaped slash"
                  "/a\\\\//"

              expectGroovyScalarTooLong
                  "Groovy even backslash run plus escaped slash"
                  "/aa\\\\//"

              for quote in [ "'"; "\"" ] do
                  let exact = "return /a" + quote + "aa/\nbreak"

                  match Fogell.Groovy.Parser.Parser.parseWithLimits limits exact with
                  | Ok _ -> ()
                  | Error e -> failtestf "a quote inside a slashy must not corrupt scalar scanning: %A" e

                  let nested = "return /a" + quote + "aa/; if (((true))) {}"
                  let depthLimits = { limits with MaxDepth = 2; MaxScalarBytes = 100 }

                  match Limits.precheck depthLimits nested with
                  | Error e -> Expect.equal e.Code NestingTooDeep "slashy content cannot hide following depth"
                  | Ok _ -> failtest "a quote inside a slashy bypassed the structural precheck"

                  match Fogell.Groovy.Parser.Parser.parseWithLimits depthLimits nested with
                  | Error e -> Expect.equal e.Code NestingTooDeep "slashy content cannot hide parser admission depth"
                  | Ok _ -> failtest "a quote inside a slashy admitted excessive grammar depth"

                  let nodeLimits = { limits with MaxNodes = 1; MaxScalarBytes = 100 }

                  match Limits.precheck nodeLimits exact with
                  | Error e -> Expect.equal e.Code TooManyNodes "slashy content cannot hide subsequent nodes"
                  | Ok _ -> failtest "a quote inside a slashy bypassed the node precheck"

              let hiddenDepth =
                  String.replicate (Limits.defaults.MaxDepth + 16) "("
                  + "2"
                  + String.replicate (Limits.defaults.MaxDepth + 16) ")"

              for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "bare CR", "\r" ] do
                  for closureLabel, closure in
                      [ "assigned", "def x = { -> 4 }"
                        "bare", "{ -> 4 }"
                        "trailing", "foo { -> 4 }"
                        "member-keyword", "foo.do { -> 4 }"
                        "control-body closure", "if (true) { -> 4 }" ] do
                      for triviaLabel, trivia in [ "plain", ""; "commented", " /* trivia */" ] do
                          let closureDivisionDepth = closure + trivia + newline + " / " + hiddenDepth + " / 2"
                          let label = $"{closureLabel}/{triviaLabel}/{newlineLabel}"

                          match Limits.precheck Limits.defaults closureDivisionDepth with
                          | Error e ->
                              Expect.equal e.Code NestingTooDeep $"{label}: division after a closure cannot hide depth"
                          | Ok _ -> failtestf "%s: a closure operand bypassed the structural precheck" label

                          match Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults closureDivisionDepth with
                          | Error e -> Expect.equal e.Code NestingTooDeep $"{label}: Groovy retains the closure guard"
                          | Ok _ -> failtestf "%s: the Groovy route admitted depth hidden after a closure" label

                          let scriptBody =
                              "pipeline { agent any stages { stage('B') { steps { script { "
                              + closureDivisionDepth
                              + " } } } } } }"

                          match Parser.parseWithLimits Limits.defaults scriptBody with
                          | Error e ->
                              Expect.equal e.Code NestingTooDeep $"{label}: Declarative script body retains guard"
                          | Ok _ -> failtestf "%s: Declarative admitted depth hidden after a closure" label

                  let lowDepth = { limits with MaxDepth = 2; MaxScalarBytes = 100 }
                  let statementSlashy = "if (true) {} /* trivia */" + newline + "/(((2)))/"

                  match Limits.precheck lowDepth statementSlashy with
                  | Error e ->
                      Expect.equal e.Code NestingTooDeep $"{newlineLabel}: ambiguous block/slashy is conservative"
                  | Ok _ -> failtestf "%s: an ambiguous brace reset and hid slashy-shaped depth" newlineLabel

                  let separatedSlashy = "if (true) {}; /* trivia */" + newline + "/(((2)))/"

                  Expect.isOk
                      (Limits.precheck lowDepth separatedSlashy)
                      $"{newlineLabel}: an explicit separator permits the following slashy"

                  Expect.isOk
                      (Fogell.Groovy.Parser.Parser.parseWithLimits lowDepth separatedSlashy)
                      $"{newlineLabel}: Groovy preserves the explicit-separator slashy control"

                  let shallowStatementSlashy = "if (true) {}" + newline + "/((2))/"
                  Expect.isOk
                      (Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults shallowStatementSlashy)
                      $"{newlineLabel}: ordinary shallow statement slashies remain within the conservative bound"

              let elseDepth = "if (true) {} else /a'aa/; if (((true))) {}"

              match Limits.precheck { limits with MaxDepth = 2; MaxScalarBytes = 100 } elseDepth with
              | Error e -> Expect.equal e.Code NestingTooDeep "an else-body slashy cannot hide following depth"
              | Ok _ -> failtest "an else-body slashy bypassed the depth precheck"

              match
                  Fogell.Groovy.Parser.Parser.parseWithLimits
                      { limits with MaxDepth = 2; MaxScalarBytes = 100 }
                      elseDepth
              with
              | Error e -> Expect.equal e.Code NestingTooDeep "an else-body slashy cannot hide Groovy depth"
              | Ok _ -> failtest "an else-body slashy bypassed Groovy admission depth"

              let deepPreamble =
                  "if (true) {} else /a'aa/; if ((((((true)))))) {}\n" + basePipeline

              match Parser.parseWithLimits { limits with MaxDepth = 5; MaxScalarBytes = 100 } deepPreamble with
              | Error e -> Expect.equal e.Code NestingTooDeep "a preamble slashy cannot hide depth"
              | Ok _ -> failtest "a preamble slashy bypassed Declarative admission depth"

              for literalLooking in
                  [ "'aaaaa'"; "\"aaaaa\""; "'''aaaaa'''"; "\"\"\"aaaaa\"\"\""; "/aaaaa/" ] do
                  let source =
                      "pipeline { agent any stages { stage('B') { steps { echo(x /* "
                      + literalLooking
                      + " */ + y) } } } }"

                  match Parser.parseWithLimits limits source with
                  | Ok _ -> ()
                  | Error e -> failtestf "comment content %s must not become a scalar: %A" literalLooking e

              let leadingComment =
                  "pipeline { agent any stages { stage('B') { steps { echo(/* 'aaaaa' */ x) } } } }"

              match Parser.parseWithLimits limits leadingComment with
              | Ok _ -> ()
              | Error e -> failtestf "a leading block comment must not become a slashy scalar: %A" e

              let expectPipelineScalar label source =
                  match Parser.parseWithLimits limits source with
                  | Error e -> Expect.equal e.Code ScalarTooLong $"{label}: the grammar-owned slashy is bounded"
                  | Ok _ -> failtestf "%s bypassed the scalar limit" label

              // `- -` is binary minus followed by unary minus, not the adjacent
              // postfix token `--`. Whitespace and comments are therefore part
              // of the slashy/division decision, not disposable trivia.
              for label, expression in
                  [ "inline separated unary", "x - - /aaaaa/"
                    "comment-separated unary", "x - /* c */ - /aaaaa/"
                    "three-character operator run", "x--- /aaaaa/" ] do
                  expectPipelineScalar
                      label
                      ("pipeline { agent any stages { stage('B') { steps { echo "
                       + expression
                       + " } } } }")

                  expectPipelineScalar
                      (label + " in parens")
                      ("pipeline { agent any stages { stage('B') { steps { echo("
                       + expression
                       + ") } } } }")

                  expectPipelineScalar
                      (label + " in a balanced list")
                      ("pipeline { agent any stages { stage('B') { steps { echo(["
                       + expression
                       + "]) } } } }")

              for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "bare CR", "\r" ] do
                  expectPipelineScalar
                      (newlineLabel + " separated unary")
                      ("pipeline { agent any stages { stage('B') { steps { echo([x -"
                       + newline
                       + "- /aaaaa/]) } } } }")

              for unaryLabel, namedUnaryValue in
                  [ "separated unary", "[x - - /aaaaa/]"
                    "operator run", "[x--- /aaaaa/]" ] do
                  for kind, condition in
                      [ "branch", "branch pattern: " + namedUnaryValue
                        "tag", "tag pattern: " + namedUnaryValue
                        "changeset", "changeset pattern: " + namedUnaryValue
                        "changelog", "changelog pattern: " + namedUnaryValue
                        "triggeredBy", "triggeredBy cause: " + namedUnaryValue
                        "environment", "environment name: " + namedUnaryValue + ", value: 'x'"
                        "equals", "equals expected: " + namedUnaryValue + ", actual: 1"
                        "changeRequest", "changeRequest target: " + namedUnaryValue ] do
                      expectPipelineScalar
                          (kind + " named " + unaryLabel)
                          ("pipeline { agent any stages { stage('B') { when { "
                           + condition
                           + " } steps { echo 'x' } } } }")

              expectGroovyScalarTooLong "three-character operator-run interpretation" "x--- /aaaaa/"

              // Raw arguments are later evaluated as top-level Groovy fragments,
              // where a leading identifier is a command head. Grouping or a
              // collection changes that context back to ordinary division.
              for commentLabel, expression in
                  [ "ordinary comment", "x /* c */ / aaaaa / b"
                    "inner-opener comment", "x /* /* */ / aaaaa / b" ] do
                  expectPipelineScalar
                      (commentLabel + " inline command")
                      ("pipeline { agent any stages { stage('B') { steps { echo "
                       + expression
                       + " } } } }")

                  for label, argument in
                      [ "grouped division", "(" + expression + ")"
                        "list division", "[" + expression + "]"
                        "named division", "message: " + expression
                        "paren named division", "(message: " + expression + ")" ] do
                      let source =
                          "pipeline { agent any stages { stage('B') { steps { echo "
                          + argument
                          + " } } } }"

                      match Parser.parseWithLimits limits source with
                      | Ok _ -> ()
                      | Error e -> failtestf "%s %s became a scalar: %A" commentLabel label e

                  for condition in
                      [ "branch pattern: " + expression
                        "branch(pattern: " + expression + ")" ] do
                      let source =
                          "pipeline { agent any stages { stage('B') { when { "
                          + condition
                          + " } steps { echo 'x' } } } }"

                      match Parser.parseWithLimits limits source with
                      | Ok _ -> ()
                      | Error e -> failtestf "%s named when division became a scalar: %A" commentLabel e

                  expectGroovyScalarTooLong (commentLabel + " command interpretation") expression
                  expectGroovyAccepted (commentLabel + " assigned division") ("def y = " + expression)

              expectPipelineScalar
                  "raw identifier-headed command slashy"
                  "pipeline { agent any stages { stage('B') { steps { echo foo /aaaaa/ } } } }"

              expectGroovyScalarTooLong "identifier-headed command interpretation" "foo /aaaaa/"

              // Once `bar` begins the first argument expression, both slashes
              // are division. Carrying command-head state through `bar` would
              // misread the first slash as a literal opener and let its
              // contents evade scalar, node and depth admission accounting.
              let commandArgumentDivision = "foo bar / aaaaa / 2"

              Expect.isOk
                  (Limits.precheck limits commandArgumentDivision)
                  "a command argument's division is not a slashy scalar"

              expectGroovyAccepted
                  "command argument division"
                  commandArgumentDivision

              let pipelineCommandArgumentDivision =
                  "pipeline { agent any stages { stage('B') { steps { echo "
                  + commandArgumentDivision
                  + " } } } }"

              match Parser.parseWithLimits limits pipelineCommandArgumentDivision with
              | Ok _ -> ()
              | Error e -> failtestf "command argument division became a slashy scalar: %A" e

              let hiddenCommandArgumentDepth = "foo bar / " + hiddenDepth + " / 2"

              match Limits.precheck Limits.defaults hiddenCommandArgumentDepth with
              | Error e -> Expect.equal e.Code NestingTooDeep "command argument division exposes structural depth"
              | Ok _ -> failtest "command-head state hid depth inside its first argument expression"

              match Fogell.Groovy.Parser.Parser.parseWithLimits Limits.defaults hiddenCommandArgumentDepth with
              | Error e -> Expect.equal e.Code NestingTooDeep "the Groovy route retains command-argument depth"
              | Ok _ -> failtest "the Groovy route admitted depth hidden in a command argument"

              let hiddenPipelineCommandArgumentDepth =
                  "pipeline { agent any stages { stage('B') { steps { script { "
                  + hiddenCommandArgumentDepth
                  + " } } } } } }"

              match Parser.parseWithLimits Limits.defaults hiddenPipelineCommandArgumentDepth with
              | Error e -> Expect.equal e.Code NestingTooDeep "the Declarative script route retains command-argument depth"
              | Ok _ -> failtest "Declarative admitted depth hidden in a command argument"

              let innerOpenerSlashy = "x + /* a /* */ /aaaaa/"

              for label, argument in
                  [ "inline", innerOpenerSlashy
                    "paren", "(" + innerOpenerSlashy + ")"
                    "named", "message: " + innerOpenerSlashy
                    "paren named", "(message: " + innerOpenerSlashy + ")" ] do
                  expectPipelineScalar
                      (label + " non-nesting comment slashy")
                      ("pipeline { agent any stages { stage('B') { steps { echo "
                       + argument
                       + " } } } }")

              for condition in
                  [ "branch pattern: " + innerOpenerSlashy
                    "branch(pattern: " + innerOpenerSlashy + ")" ] do
                  expectPipelineScalar
                      "named when non-nesting comment slashy"
                      ("pipeline { agent any stages { stage('B') { when { "
                       + condition
                       + " } steps { echo 'x' } } } }")

              expectGroovyScalarTooLong "non-nesting comment slashy interpretation" innerOpenerSlashy

              // FParsec accepts LF, CRLF and bare CR as physical line endings.
              // Raw arguments and balanced capture must end on the same three forms.
              for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "bare CR", "\r" ] do
                  let commentSeparatedExpression =
                      "pipeline { agent any stages { stage('B') { steps { echo 1 /* comment"
                      + newline
                      + "*/ 2 } } } }"

                  match Parser.parseWithLimits Limits.defaults commentSeparatedExpression with
                  | Error e ->
                      Expect.equal
                          e.Code
                          MalformedSyntax
                          $"{newlineLabel} inside a block comment ends the raw argument"
                  | Ok _ -> failtestf "%s inside a block comment swallowed a new statement" newlineLabel

                  let commentSeparatedSteps =
                      "pipeline { agent any stages { stage('B') { steps { echo 1 /* comment"
                      + newline
                      + "*/ input /aaaaa/ } } } }"

                  match Parser.parseWithLimits Limits.defaults commentSeparatedSteps with
                  | Ok pipeline ->
                      Expect.equal
                          pipeline.Stages.[0].Steps.Length
                          2
                          $"{newlineLabel} inside a block comment preserves the following step"

                      Expect.equal
                          pipeline.Stages.[0].Steps.[1].Name
                          "input"
                          $"{newlineLabel} inside a block comment retains the approval gate"
                  | Error e -> failtestf "%s block-comment-separated steps did not parse: %A" newlineLabel e

                  match Parser.parseWithLimits limits commentSeparatedSteps with
                  | Error e ->
                      Expect.equal
                          e.Code
                          ScalarTooLong
                          $"{newlineLabel} inside a block comment exposes the following gate scalar"
                  | Ok _ -> failtestf "%s inside a block comment swallowed the approval gate" newlineLabel

                  let namedCommentSeparatedSteps =
                      "pipeline { agent any stages { stage('B') { steps { echo message: 1 /* comment"
                      + newline
                      + "*/ input /aaaaa/ } } } }"

                  match Parser.parseWithLimits Limits.defaults namedCommentSeparatedSteps with
                  | Ok pipeline ->
                      Expect.equal
                          pipeline.Stages.[0].Steps.Length
                          2
                          $"{newlineLabel} inside a named raw argument preserves the following step"
                  | Error e -> failtestf "%s named block-comment-separated steps did not parse: %A" newlineLabel e

                  match Parser.parseWithLimits limits namedCommentSeparatedSteps with
                  | Error e ->
                      Expect.equal
                          e.Code
                          ScalarTooLong
                          $"{newlineLabel} inside a named raw argument exposes the following gate scalar"
                  | Ok _ -> failtestf "%s inside a named raw argument swallowed the approval gate" newlineLabel

                  let commentSeparatedWhenValue =
                      "pipeline { agent any stages { stage('B') { when { branch pattern: x /* comment"
                      + newline
                      + "*/ branch 'main' } steps { echo 'x' } } } }"

                  match Parser.parseWithLimits Limits.defaults commentSeparatedWhenValue with
                  | Ok pipeline ->
                      match pipeline.Stages.[0].When with
                      | Some(WhenAllOf [ WhenUnmodelled("branch", firstRaw); WhenBranch "main" ]) ->
                          Expect.isFalse
                              (firstRaw.Contains("branch 'main'"))
                              $"{newlineLabel} inside a block comment ends the first named when value"
                      | other -> failtestf "%s block-comment-separated when conditions rejoined: %A" newlineLabel other
                  | Error e -> failtestf "%s block-comment-separated when conditions failed: %A" newlineLabel e

                  let parenthesisedCommaControl =
                      "pipeline { agent any stages { stage('B') { steps { echo(1 /* comment"
                      + newline
                      + "*/, 2) } } } }"

                  match Parser.parseWithLimits Limits.defaults parenthesisedCommaControl with
                  | Ok pipeline ->
                      Expect.equal
                          pipeline.Stages.[0].Steps.[0].Positional.Length
                          2
                          $"{newlineLabel} inside a balanced argument preserves its comma separator"
                  | Error e -> failtestf "%s parenthesised comment/comma control failed: %A" newlineLabel e

                  let slashyMultiline =
                      "pipeline { agent any stages { stage('B') { steps { echo(/before"
                      + newline
                      + "after/) } } } }"

                  match Parser.parseWithLimits Limits.defaults slashyMultiline with
                  | Error e -> Expect.equal e.Code MalformedSyntax $"{newlineLabel} terminates a slashy literal"
                  | Ok _ -> failtestf "a slashy literal crossed a raw %s boundary" newlineLabel

                  // The opaque top/stage fallbacks rely on balancedRaw. Its
                  // slashy shielding must use the same physical-line boundary
                  // as the grammar-owned slashy parsers, or a later slash can
                  // hide the balanced region's real closing brace.
                  let opaqueSlashyContexts =
                      [ "top-level opaque block",
                        "pipeline { agent any stages { stage('B') { steps { echo 'x' } } } libraries { /x"
                        + newline
                        + "} y/ } }"
                        "stage opaque block",
                        "pipeline { agent any stages { stage('B') { steps { echo 'x' } mystery { /x"
                        + newline
                        + "} y/ } } } }"
                        "nested top-level opaque block",
                        "pipeline { agent any stages { stage('B') { steps { echo 'x' } } } libraries { outer { /x"
                        + newline
                        + "} y/ } } }"
                        "nested stage opaque block",
                        "pipeline { agent any stages { stage('B') { steps { echo 'x' } mystery { outer { /x"
                        + newline
                        + "} y/ } } } } }" ]

                  for contextLabel, source in opaqueSlashyContexts do
                      match Parser.parseWithLimits Limits.defaults source with
                      | Error e ->
                          Expect.equal
                              e.Code
                              MalformedSyntax
                              $"{newlineLabel} terminates slashy shielding in a {contextLabel}"
                      | Ok _ -> failtestf "%s crossed slashy shielding in a %s" newlineLabel contextLabel

                  for contextLabel, source in
                      [ "top-level opaque control",
                        "pipeline { agent any stages { stage('B') { steps { echo 'x' } } } libraries { /x}y/ } }"
                        "stage opaque control",
                        "pipeline { agent any stages { stage('B') { steps { echo 'x' } mystery { /x}y/ } } } }" ] do
                      match Parser.parseWithLimits Limits.defaults source with
                      | Ok _ -> ()
                      | Error e -> failtestf "same-line slashy failed in %s: %A" contextLabel e

                  let balancedArgumentSplits =
                      [ "named bracket",
                        "pipeline { agent any stages { stage('B') { steps { echo message: [/x"
                        + newline
                        + "] y/] } } } }"
                        "positional bracket",
                        "pipeline { agent any stages { stage('B') { steps { echo [/x"
                        + newline
                        + "] y/] } } } }"
                        "named parenthesis",
                        "pipeline { agent any stages { stage('B') { steps { echo message: (/x"
                        + newline
                        + ") y/) } } } }" ]

                  for contextLabel, source in balancedArgumentSplits do
                      match Parser.parseWithLimits Limits.defaults source with
                      | Error e ->
                          Expect.equal
                              e.Code
                              MalformedSyntax
                              $"{newlineLabel} refuses the balanced {contextLabel} split"
                      | Ok _ -> failtestf "%s admitted a balanced %s split" newlineLabel contextLabel

                  let lowScalar =
                      { Limits.defaults with
                          MaxScalarBytes = 4 }

                  let multilineSlashyContexts =
                      [ "positional raw",
                        "pipeline { agent any stages { stage('B') { steps { echo /aaaaa"
                        + newline
                        + "tail/ } } } }"
                        "command-head raw",
                        "pipeline { agent any stages { stage('B') { steps { echo foo /aaaaa"
                        + newline
                        + "tail/ } } } }"
                        "operator-position raw",
                        "pipeline { agent any stages { stage('B') { steps { echo env.A + /aaaaa"
                        + newline
                        + "tail/ } } } }"
                        "named raw",
                        "pipeline { agent any stages { stage('B') { steps { echo message: /aaaaa"
                        + newline
                        + "tail/ } } } }"
                        "named when",
                        "pipeline { agent any stages { stage('B') { when { branch pattern: /aaaaa"
                        + newline
                        + "tail/ } steps { echo 'x' } } } }" ]

                  for contextLabel, source in multilineSlashyContexts do
                      match Parser.parseWithLimits lowScalar source with
                      | Error e ->
                          Expect.equal
                              e.Code
                              MalformedSyntax
                              $"raw {newlineLabel} terminates the {contextLabel} slashy-shaped value"
                      | Ok _ -> failtestf "%s %s slashy value split across grammar items" newlineLabel contextLabel

                  for firstLabel, firstStep in [ "positional raw", "echo x"; "named raw", "echo message: x" ] do
                      let adjacentSteps =
                          "pipeline { agent any stages { stage('B') { steps { "
                          + firstStep
                          + newline
                          + " input /aaaaa/"
                          + newline
                          + "} } } }"

                      match Parser.parseWithLimits Limits.defaults adjacentSteps with
                      | Ok pipeline ->
                          Expect.equal
                              pipeline.Stages.[0].Steps.Length
                              2
                              $"{newlineLabel} after {firstLabel} preserves the approval step"

                          Expect.equal
                              pipeline.Stages.[0].Steps.[1].Name
                              "input"
                              $"{newlineLabel} after {firstLabel} retains the approval gate"
                      | Error e -> failtestf "%s-separated %s steps did not parse: %A" newlineLabel firstLabel e

                      match Parser.parseWithLimits limits adjacentSteps with
                      | Error e ->
                          Expect.equal e.Code ScalarTooLong $"{newlineLabel} after {firstLabel} exposes the gate's scalar"
                      | Ok _ -> failtestf "%s after %s swallowed the approval gate" newlineLabel firstLabel

                  let sameLineComment =
                      "pipeline { agent any stages { stage('B') { steps { echo 1 /* comment */ + 2 } } } }"

                  match Parser.parseWithLimits Limits.defaults sameLineComment with
                  | Ok pipeline ->
                      Expect.equal pipeline.Stages.[0].Steps.Length 1 "a same-line block comment stays inside one argument"
                  | Error e -> failtestf "same-line block comment failed in %s control: %A" newlineLabel e

                  for quoteLabel, quote in [ "single", "'"; "double", "\"" ] do
                      let ordinaryMultiline =
                          "pipeline { agent any stages { stage('B') { steps { echo(["
                          + quote
                          + "a"
                          + newline
                          + "b"
                          + quote
                          + "]) } } } }"

                      match Parser.parseWithLimits Limits.defaults ordinaryMultiline with
                      | Error e ->
                          Expect.equal
                              e.Code
                              MalformedSyntax
                              $"{newlineLabel} refuses an ordinary {quoteLabel}-quoted multiline string"
                      | Ok _ -> failtestf "%s was swallowed inside an ordinary %s-quoted string" newlineLabel quoteLabel

                      // Direct Declarative arguments take the scalar parser. These
                      // controls must not be hidden inside a list/balancedRaw value:
                      // that path is reparsed by the nested Groovy parser and once
                      // masked a decoder that retained the physical line ending.
                      let directMultiline =
                          "pipeline { agent any stages { stage('B') { steps { echo "
                          + quote
                          + "a"
                          + newline
                          + "b"
                          + quote
                          + " } } } }"

                      match Parser.parseWithLimits Limits.defaults directMultiline with
                      | Error e ->
                          Expect.equal
                              e.Code
                              MalformedSyntax
                              $"{newlineLabel} refuses a direct {quoteLabel}-quoted multiline argument"
                      | Ok _ -> failtestf "%s crossed a direct %s-quoted argument" newlineLabel quoteLabel

                      for argumentLabel, prefix in [ "positional", "echo "; "named", "echo message: " ] do
                          let directContinuation =
                              "pipeline { agent any stages { stage('B') { steps { "
                              + prefix
                              + quote
                              + "a\\"
                              + newline
                              + "b"
                              + quote
                              + " } } } }"

                          match Parser.parseWithLimits Limits.defaults directContinuation with
                          | Ok pipeline ->
                              let step = pipeline.Stages.[0].Steps.[0]

                              if argumentLabel = "positional" then
                                  Expect.equal
                                      step.Positional
                                      [ "ab" ]
                                      $"{newlineLabel} is removed from a direct {quoteLabel}-quoted positional"
                              else
                                  Expect.equal
                                      step.Named
                                      [ "message", "ab" ]
                                      $"{newlineLabel} is removed from a direct {quoteLabel}-quoted named value"
                          | Error e ->
                              failtestf
                                  "%s direct %s %s-quoted continuation was rejected: %A"
                                  newlineLabel
                                  argumentLabel
                                  quoteLabel
                                  e

                          for secondLabel, second in [ "LF", "\n"; "CRLF", "\r\n"; "bare CR", "\r" ] do
                              let adjacentEnding =
                                  "pipeline { agent any stages { stage('B') { steps { "
                                  + prefix
                                  + quote
                                  + "a\\"
                                  + newline
                                  + second
                                  + "b"
                                  + quote
                                  + " } } } }"

                              // These two source fragments join into ONE CRLF
                              // byte sequence. Every other pair is two physical
                              // endings, only the first of which is escaped.
                              let oneCrLf = newline = "\r" && second = "\n"

                              match Parser.parseWithLimits Limits.defaults adjacentEnding with
                              | Ok pipeline when oneCrLf ->
                                  let step = pipeline.Stages.[0].Steps.[0]
                                  let actual =
                                      if argumentLabel = "positional" then step.Positional
                                      else step.Named |> List.map snd

                                  Expect.equal
                                      actual
                                      [ "ab" ]
                                      $"bare CR + LF is one direct {quoteLabel}-quoted CRLF continuation"
                              | Error e when not oneCrLf ->
                                  Expect.equal
                                      e.Code
                                      MalformedSyntax
                                      $"{newlineLabel} + {secondLabel} refuses the second unescaped direct {quoteLabel}-quoted ending"
                              | Ok _ ->
                                  failtestf
                                      "%s + %s crossed a direct %s %s-quoted argument"
                                      newlineLabel
                                      secondLabel
                                      argumentLabel
                                      quoteLabel
                              | Error e ->
                                  failtestf
                                      "one CRLF direct %s %s-quoted continuation was rejected: %A"
                                      argumentLabel
                                      quoteLabel
                                      e

                  let tripleMultiline =
                      "pipeline { agent any stages { stage('B') { steps { echo(['''a"
                      + newline
                      + "b''']) } } } }"

                  match Parser.parseWithLimits Limits.defaults tripleMultiline with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s was rejected inside a triple-single-quoted string: %A" newlineLabel e

                  for tripleLabel, tripleQuote in [ "triple-single", "'''"; "triple-double", "\"\"\"" ] do
                      for argumentLabel, prefix in [ "positional", "echo "; "named", "echo message: " ] do
                          for secondLabel, second in [ "LF", "\n"; "CRLF", "\r\n"; "bare CR", "\r" ] do
                              let adjacentTripleEnding =
                                  "pipeline { agent any stages { stage('B') { steps { "
                                  + prefix
                                  + tripleQuote
                                  + "a\\"
                                  + newline
                                  + second
                                  + "b"
                                  + tripleQuote
                                  + " } } } }"

                              match Parser.parseWithLimits Limits.defaults adjacentTripleEnding with
                              | Ok pipeline ->
                                  let step = pipeline.Stages.[0].Steps.[0]
                                  let actual =
                                      if argumentLabel = "positional" then step.Positional
                                      else step.Named |> List.map snd

                                  let expected =
                                      if newline = "\r" && second = "\n" then [ "ab" ]
                                      else [ "a\nb" ]

                                  Expect.equal
                                      actual
                                      expected
                                      $"{newlineLabel} + {secondLabel} has exact direct {tripleLabel} continuation semantics"
                              | Error e ->
                                  failtestf
                                      "%s + %s direct %s %s continuation was rejected: %A"
                                      newlineLabel
                                      secondLabel
                                      argumentLabel
                                      tripleLabel
                                      e

                  let continuedOrdinary =
                      "pipeline { agent any stages { stage('B') { steps { echo(['a\\"
                      + newline
                      + "b']) } } } }"

                  match Parser.parseWithLimits Limits.defaults continuedOrdinary with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s escaped physical continuation was rejected: %A" newlineLabel e

                  let continuedRawConcat =
                      "pipeline { agent any stages { stage('B') { steps { echo x + 'a\\"
                      + newline
                      + "b' } } } }"

                  match Parser.parseWithLimits Limits.defaults continuedRawConcat with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s escaped continuation in a raw expression was rejected: %A" newlineLabel e

                  let scriptComment =
                      "pipeline { agent any stages { stage('B') { steps { script { echo x // c"
                      + newline
                      + " } } } } } }"

                  match Parser.parseWithLimits limits scriptComment with
                  | Ok _ -> ()
                  | Error e -> failtestf "%s script line comment did not terminate: %A" newlineLabel e

                  for literalLooking in [ "'aaaaa'"; "\"aaaaa\""; "'''aaaaa'''"; "\"\"\"aaaaa\"\"\""; "/aaaaa/" ] do
                      let parenComment =
                          "pipeline { agent any stages { stage('B') { steps { echo(// "
                          + literalLooking
                          + newline
                          + " x) } } } }"

                      match Parser.parseWithLimits limits parenComment with
                      | Ok _ -> ()
                      | Error e -> failtestf "%s comment content %s escaped the comment: %A" newlineLabel literalLooking e

              // Refusing an unclosed block comment in the linear precheck
              // prevents raw-parser alternatives from rescanning each suffix.
              let unterminatedComments =
                  "pipeline { agent any stages { stage('B') { steps { echo "
                  + String.replicate 8_000 "/*a"
                  + " } } } }"

              let commentStarted = Diagnostics.Stopwatch.StartNew()
              let commentResult = Parser.parseWithLimits Limits.defaults unterminatedComments
              commentStarted.Stop()

              match commentResult with
              | Error e -> Expect.equal e.Code MalformedSyntax "unterminated block comments fail closed"
              | Ok _ -> failtest "repeated unterminated block comments parsed"

              Expect.isLessThan
                  commentStarted.Elapsed.TotalSeconds
                  1.0
                  "source-sized unterminated block comments remain bounded-linear"

              Expect.isOk (Limits.precheck limits "10 / 2 / 5") "division operators do not open slashy spans"

              for source in
                  [ "x = a / b / c"
                    "return a / b"
                    "foo() / 2"
                    "x++ / 2"
                    "a\n / b / c" ] do
                  Expect.isOk (Limits.precheck limits source) $"division remains code in {source}"

              for source in
                  [ "pipeline { agent any stages { stage('B') { steps { echo x++ / aaaaa / b } } } }"
                    "pipeline { agent any stages { stage('B') { steps { echo(x++ / aaaaa / b) } } } }" ] do
                  match Parser.parseWithLimits limits source with
                  | Ok _ -> ()
                  | Error e -> failtestf "postfix division must not become a slashy scalar: %A" e

              match Limits.precheck limits "return\r'aaaaa'" with
              | Error e ->
                  Expect.equal e.Code ScalarTooLong "the bare-CR scalar is refused"
                  Expect.equal e.Position { Line = 2L; Column = 8L } "bare CR advances the refusal line"
              | Ok _ -> failtest "the bare-CR scalar bypassed the scalar limit"

              let zeroDepth =
                  { limits with
                      MaxDepth = 0 }

              match Limits.precheck zeroDepth "/x\r{/" with
              | Error e -> Expect.equal e.Code NestingTooDeep "a later-line slash cannot shield structure across bare CR"
              | Ok _ -> failtest "the slashy closer cache crossed a bare-CR boundary"

              match Fogell.Groovy.Parser.Parser.parseWithLimits limits "return\r'aaaaa'" with
              | Error e ->
                  Expect.equal e.Code ScalarTooLong "Groovy refuses the bare-CR scalar"
                  Expect.equal e.Position { Line = 2L; Column = 8L } "Groovy retains the bare-CR source position"
              | Ok _ -> failtest "Groovy admitted the bare-CR scalar"

              Expect.isOk
                  (Limits.precheck limits "// /aaaaa/\nnode")
                  "slashy-looking text in a line comment is ignored"

              Expect.isOk
                  (Limits.precheck limits "/* /aaaaa/ */ node")
                  "slashy-looking text in a block comment is ignored"

              Expect.isOk
                  (Limits.precheck limits "/aaaaa\nnode")
                  "an unterminated same-line slashy candidate falls back to ordinary text"

              let fallbackNodeError =
                  match Limits.precheck { limits with MaxNodes = 2 } "/ a b c\n" with
                  | Error e -> e
                  | Ok() -> failtest "expected raw text after an unterminated slashy candidate to count nodes"

              Expect.equal fallbackNodeError.Code TooManyNodes "slashy fallback cannot bypass the node limit"

              // Identifier-headed `a / b / c` is already parsed by this
              // deliberately partial Groovy grammar as a command call with a
              // slashy argument. Numeric division and division after a
              // completed literal are unambiguous controls for admission.
              for source in [ "10 / 20000 / 5"; "/aa/ / 2" ] do
                  match Fogell.Groovy.Parser.Parser.parseWithLimits limits source with
                  | Error e when e.Code = ScalarTooLong -> failtestf "division was misclassified in %s" source
                  | _ -> ()

              let adversarial = String.Concat(Array.replicate 131_000 "\\/")
              Limits.precheck Limits.defaults "\\/" |> ignore
              let started = Diagnostics.Stopwatch.StartNew()
              let adversarialResult = Limits.precheck Limits.defaults adversarial
              started.Stop()
              Expect.isOk adversarialResult "the source-sized escaped-slash adversary remains ordinary raw text"
              Expect.isLessThan started.Elapsed.TotalSeconds 1.0 "the admission scan remains bounded-linear"
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
              // 40k braces: the precheck uses bounded linear passes, so this must
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
          }

          test "every code the parser can emit renders an exact excerpt end to end" {
              // The per-code goldens above render a FABRICATED error. These go through
              // the real parser, so the position each producer records is what lands
              // under the caret. Admission limits are shrunk so the offending line stays
              // readable rather than clipped. The scanned-limit carets sit one source
              // column past the character that crosses a count or closes an overlong
              // scalar; FG-004b pins those positions exactly.
              // The MalformedSyntax case uses a `refuse` message Fogell owns rather
              // than FParsec's expectation list, which changes with every grammar edit.
              let tiny =
                  { Limits.defaults with
                      MaxSourceBytes = 12
                      MaxNodes = 4
                      MaxDepth = 2
                      MaxScalarBytes = 3 }

              let rendered limits (source: string) =
                  match Parser.parseWithLimits limits source with
                  | Ok _ -> failtestf "expected a rejection, %A parsed" source
                  | Error e -> AdmissionError.render source e

              let duplicateOptions =
                  "pipeline {\n  agent any\n  options { timestamps() }\n  options { timestamps() }\n  stages { stage('a') { steps { echo 'x' } } }\n}"

              for label, limits, source, expected in
                  [ "source_too_large",
                    tiny,
                    "pipeline { x }",
                    "source_too_large at 1:1: source is 14 UTF-8 bytes, limit is 12\npipeline { x }\n^"
                    "nesting_too_deep", tiny, "a\n{{{", "nesting_too_deep at 2:3: grammar depth 3 exceeds limit 2\n{{{\n  ^"
                    "too_many_nodes", tiny, "a b\nc d e", "too_many_nodes at 2:6: node count exceeds 4\nc d e\n     ^"
                    "scalar_too_long",
                    tiny,
                    "x\n'abcd'",
                    "scalar_too_long at 2:7: string literal exceeds 3 UTF-8 bytes\n'abcd'\n      ^"
                    "empty_source", Limits.defaults, "  \n\t", "empty_source at 1:1: source is empty\n  \n^"
                    "no_pipeline_block",
                    Limits.defaults,
                    "node {\n  sh 'x'\n}",
                    "no_pipeline_block at 1:1: no declarative `pipeline { }` block found\nnode {\n^"
                    "no_stages",
                    Limits.defaults,
                    "pipeline {\n  agent any\n}",
                    "no_stages at 1:1: pipeline declares no stages\npipeline {\n^"
                    "malformed_syntax",
                    Limits.defaults,
                    duplicateOptions,
                    "malformed_syntax at 6:1: multiple occurrences of the `options` section: Jenkins rejects duplicate sections before running anything\n}\n^" ] do
                  Expect.equal (rendered limits source) expected $"{label}: exact end-to-end diagnostic"
          } ]

/// FG-121. An `options` entry must be a call. Jenkins' Declarative parser matches
/// each entry as a method call and reports `Expected an option` for a bare
/// identifier; this grammar parsed `timestamps` alone as a zero-argument step —
/// indistinguishable from `timestamps()` in the IR — and ran the build.
let bareOptionEntries =
    let top options =
        $"pipeline {{\n  agent any\n  options {{ {options} }}\n  stages {{ stage('a') {{ steps {{ echo 'x' }} }} }}\n}}"

    let stage options =
        $"pipeline {{\n  agent any\n  stages {{\n    stage('a') {{\n      options {{ {options} }}\n      steps {{ echo 'x' }}\n    }}\n  }}\n}}"

    let refused label source name =
        let e = err source
        Expect.equal e.Code MalformedSyntax $"{label}: a bare option entry is an admission refusal"

        Expect.stringContains
            e.Message
            $"options entry `{name}` is not a call: Jenkins reports `Expected an option`"
            $"{label}: the refusal names the entry and Jenkins' own diagnostic"

    testList
        "FG-121 bare option entries"
        [ test "a bare option name is refused at pipeline and stage scope" {
              // The guard is by FORM, not by name: a Groovy bare identifier is a
              // property read, never a method call, so no option name can be valid
              // without parentheses, arguments or a block. MEASURED on Jenkins
              // 2.568.1 for `timestamps` by FG-053's verifier; UNPROVEN by receipt
              // (FG-129: a compile-shaped refusal seals none).
              refused "top-level timestamps" (top "timestamps") "timestamps"
              refused "stage-level timestamps" (stage "timestamps") "timestamps"
              refused "top-level disableConcurrentBuilds" (top "disableConcurrentBuilds") "disableConcurrentBuilds"
              refused "bare before a semicolon" (top "timestamps; skipDefaultCheckout()") "timestamps"
              refused "bare against the closing brace" (top "timestamps}") "timestamps"
              refused "bare with a trailing comment" (top "timestamps // stamp\n") "timestamps"
              // A block comment is trivia too; the verifier found this form fail-open
              // when the lookahead knew only the line-comment spelling.
              refused "bare with a trailing block comment" (top "timestamps /* stamp */") "timestamps"
              refused "bare with a block comment spanning lines" (top "timestamps /* one\n  two */\n  skipDefaultCheckout()") "timestamps"
              refused "bare with a block comment and no space" (top "timestamps/* stamp */") "timestamps"
              refused "bare after a valid entry" (top "skipDefaultCheckout()\n    timestamps\n  ") "timestamps"
          }

          test "call forms still reach the step grammar" {
              // Parentheses, command-form arguments and a trailing block are all
              // method calls in Groovy. The block form is deliberately ADMITTED here
              // so the walker can refuse it with a named reason; refusing it in the
              // grammar would collapse two different Jenkins diagnostics into one.
              Expect.equal (ok (top "timestamps()")).Options.Length 1 "parenthesised zero-argument call"

              Expect.equal
                  (ok (top "timeout time: 1, unit: 'MINUTES'")).Options.Head.Named.Length
                  2
                  "command-form named arguments without parentheses"

              Expect.equal (ok (top "timestamps(); skipDefaultCheckout()")).Options.Length 2 "two calls on one line"

              let block = ok (top "timestamps { }")
              Expect.isTrue block.Options.Head.HasBlock "a trailing block is a call, carried as HasBlock for the walker"

              // A block comment between the name and its arguments is trivia Groovy
              // discards; the guard must skip it rather than read it as a terminator,
              // or a call Jenkins accepts is refused — measured by the verifier.
              Expect.equal (ok (top "timestamps /* x */ ()")).Options.Length 1 "comment before the parentheses is still a call"
              Expect.isTrue (ok (top "timestamps /* x */ { }")).Options.Head.HasBlock "comment before the block is still a call"

              Expect.equal
                  (ok (top "timeout /* x */ time: 1, unit: 'MINUTES'")).Options.Head.Named.Length
                  2
                  "comment before command-form arguments is still a call"

              let stageBlock = ok (stage "timeout(time: 1, unit: 'MINUTES')")
              Expect.equal stageBlock.Stages.Head.Options.Length 1 "stage-scope call"
          } ]

/// FG-004b. The fixed-seed generator is intentionally length-delimited and
/// every generated source is guaranteed to be refused by the Declarative
/// parser boundary. Some controls are valid scripted inputs, so this is a
/// bounded admission-negative robustness sweep, not a grammar fuzzer claiming
/// arbitrary malformed-Jenkinsfile coverage.
let admissionNegativeSweep =
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

        let utf8SourceExact =
            String.replicate ((Limits.defaults.MaxSourceBytes - 2) / 2) "é" + "aa"

        let utf8ScalarExact =
            String.replicate ((Limits.defaults.MaxScalarBytes - 2) / 2) "é" + "aa"

        let slashyOver =
            "/" + String.replicate (Limits.defaults.MaxScalarBytes + 1) "a" + "/"

        let slashyPipeline body =
            "pipeline { agent any stages { stage('B') { " + body + " } } }"

        let slashyClosingColumn (source: string) =
            int64 (source.LastIndexOf('/') + 2)

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
              "utf8-source-limit-exact"
              "source-limit"
              utf8SourceExact
              NoPipelineBlock
              1L
              1L
          exact
              "utf8-source-limit-plus-one"
              "source-limit"
              (utf8SourceExact + "a")
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
              NoPipelineBlock
              1L
              1L
          exact
              "single-scalar-limit-plus-one"
              "single-scalar-limit"
              ("'" + String.replicate (Limits.defaults.MaxScalarBytes + 1) "a" + "'")
              ScalarTooLong
              1L
              (int64 Limits.defaults.MaxScalarBytes + 4L)
          exact
              "utf8-single-scalar-content-limit"
              "single-scalar-limit"
              ("'" + utf8ScalarExact + "'")
              NoPipelineBlock
              1L
              1L
          exact
              "utf8-single-scalar-limit-plus-one"
              "single-scalar-limit"
              ("'" + utf8ScalarExact + "a'")
              ScalarTooLong
              1L
              (int64 utf8ScalarExact.Length + 4L)
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
              "triple-single-scalar-content-limit"
              "single-scalar-limit"
              ("'''" + String.replicate Limits.defaults.MaxScalarBytes "a" + "'''")
              NoPipelineBlock
              1L
              1L
          exact
              "triple-single-scalar-limit-plus-one"
              "single-scalar-limit"
              ("'''" + String.replicate (Limits.defaults.MaxScalarBytes + 1) "a" + "'''")
              ScalarTooLong
              1L
              (int64 Limits.defaults.MaxScalarBytes + 8L)
          exact
              "triple-double-scalar-content-limit"
              "double-scalar-limit"
              ("\"\"\"" + String.replicate Limits.defaults.MaxScalarBytes "a" + "\"\"\"")
              NoPipelineBlock
              1L
              1L
          exact
              "triple-double-scalar-limit-plus-one"
              "double-scalar-limit"
              ("\"\"\"" + String.replicate (Limits.defaults.MaxScalarBytes + 1) "a" + "\"\"\"")
              ScalarTooLong
              1L
              (int64 Limits.defaults.MaxScalarBytes + 8L)
          let source =
              "pipeline { agent any stages { stage "
              + slashyOver
              + " { steps { echo 'x' } } } }"

          exact
              "slashy-stage-context-limit-plus-one"
              "slashy-scalar-limit"
              source
              ScalarTooLong
              1L
              (slashyClosingColumn source)
          let source =
              "pipeline { agent label "
              + slashyOver
              + " stages { stage('B') { steps { echo 'x' } } } }"

          exact
              "slashy-agent-context-limit-plus-one"
              "slashy-scalar-limit"
              source
              ScalarTooLong
              1L
              (slashyClosingColumn source)
          let source =
              "pipeline { agent any tools { maven "
              + slashyOver
              + " } stages { stage('B') { steps { echo 'x' } } } }"

          exact
              "slashy-tool-context-limit-plus-one"
              "slashy-scalar-limit"
              source
              ScalarTooLong
              1L
              (slashyClosingColumn source)
          let source =
              slashyPipeline ("when { branch " + slashyOver + " } steps { echo 'x' }")

          exact
              "slashy-when-context-limit-plus-one"
              "slashy-scalar-limit"
              source
              ScalarTooLong
              1L
              (slashyClosingColumn source)
          let source = slashyPipeline ("steps { echo " + slashyOver + " }")

          exact
              "slashy-step-context-limit-plus-one"
              "slashy-scalar-limit"
              source
              ScalarTooLong
              1L
              (slashyClosingColumn source)
          exact
              "lf-resets-depth-position"
              "lf-depth"
              (depthSource "x\n" "{")
              NestingTooDeep
              2L
              65L
          exact
              "crlf-resets-depth-position"
              "crlf-depth"
              (depthSource "x\r\n" "{")
              NestingTooDeep
              2L
              65L
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
                (int64 prefix.Length + 65L)

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

    let corpusDigest (inputs: AdmissionNegativeCase array) =
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
              "slashy-scalar-limit"
              "lf-depth"
              "crlf-depth" ]

    testList
        "FG-004b deterministic admission-negative sweep"
        [ test "the fixed-seed length-delimited corpus is exact and replayable" {
              let first = cases ()
              let replay = cases ()
              let firstDigest = corpusDigest first
              let replayDigest = corpusDigest replay

              Expect.equal first.Length inputCount "exactly 10,000 guaranteed-refused inputs are generated"
              Expect.equal replay.Length inputCount "the replay has the same exact size"

              for index in 0 .. inputCount - 1 do
                  Expect.equal replay.[index].Label first.[index].Label $"label replay at index {index}"

                  Expect.isTrue
                      (String.Equals(replay.[index].Source, first.[index].Source, StringComparison.Ordinal))
                      $"source code units replay at index {index}"

              Expect.equal replayDigest firstDigest "the complete length-delimited corpus replays byte-for-byte"
              Expect.equal
                  firstDigest
                  "365dc88fcfd41c86408dfa83a9b0729bb8cc45c2d3f2a0d8ecfb9c9ea7a54013"
                  "the fixed seed and recipe corpus are pinned"

              Expect.equal
                  (first |> Array.map (fun input -> input.Label) |> Set.ofArray |> Set.count)
                  inputCount
                  "every generated case label is unique"

              Expect.equal
                  (first |> Array.map (fun input -> input.Source) |> Set.ofArray |> Set.count)
                  inputCount
                  "every admission-negative source is unique"

              let observedFamilies = first |> Array.map (fun input -> input.Family) |> Set.ofArray
              Expect.equal observedFamilies requiredFamilies "every named admission-negative family is present"

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
                          "guaranteed-refused Declarative input was accepted; seed=0x%016X; index=%d; label=%s"
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

          test "an operand-position slashy candidate cannot fall through at a line ending" {
              // After `+`, `/` cannot be binary division. Letting it become raw
              // text at the generated line ending permits the next line to be
              // reinterpreted as a separate step and bypasses value admission.
              let e = err (steps "        echo env.A + / 2")
              Expect.equal e.Code MalformedSyntax "the unterminated operand is refused at admission"
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
              bareOptionEntries
              admissionNegativeSweep
              declarativeDetection
              structure
              invalidEightEscape
              stepBlockPresence
              slashyPosition ])
