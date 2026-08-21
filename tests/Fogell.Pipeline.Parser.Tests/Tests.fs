module Fogell.Pipeline.Parser.Tests

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

          test "the refusal carries a named code and a position" {
              // WHAT IT ACTUALLY SAYS, not what I wanted it to. The parser's own message
              // names the duplicated argument, but an enclosing fallback generalises it
              // to `malformed_syntax at L:C: opaque section` before it reaches the
              // caller. That still satisfies tier 3 — a rejection with a named code and a
              // position — and the safety property (nothing runs) holds either way, so
              // the test asserts the reachable guarantee instead of a nicer sentence.
              // The lost detail is recorded on the board rather than papered over here.
              let e = err (mk "    stage('B') { steps { sh script: 'x', returnStatus: true, returnStatus: false } }")
              Expect.stringContains (string e) "malformed_syntax" "a named code"
              Expect.stringContains (string e) ":" "and a position"
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

          // FG-175. A KNOWN GAP, PINNED SO IT CANNOT BE MISTAKEN FOR COVERAGE.
          //
          // The duplicate rule is wired into `equals` and `environment` too, but it
          // CANNOT REACH the caller there: `when` falls back to `whenSectionOpaque`,
          // which consumes any unparseable body as raw text and marks the condition
          // UNMODELLED. So the refusal backtracks into that fallback and the stage merely
          // fails closed and SKIPS.
          //
          // That is still a divergence, MEASURED by scratch probe and UNPROVEN by receipt
          // (FG-129 — a compile-shaped refusal emits nothing comparable): Jenkins rejects
          // the pipeline at compile time and runs NOTHING, while Fogell runs the earlier
          // stage and reports SUCCESS. Closing it means changing how section fallbacks handle a REFUSAL as
          // opposed to an unmodelled shape — the fallback is deliberate (FG-152) and
          // load-bearing, so this is a design change, not a patch. FG-175 carries it.
          //
          // These tests assert TODAY'S behaviour on purpose. When FG-175 lands they will
          // fail, which is the point: they are the tripwire, not an endorsement.
          test "FG-175 gap: a duplicate in when-equals PARSES, and the condition is unmodelled" {
              let p = ok (mk "    stage('B') { when { equals expected: 1, actual: 1, actual: 2 }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtestf "expected the opaque fallback to swallow it, got %A" other
          }

          // The SINGLE-KEY conditions reach the same place by a different route: the
          // second pair is left UNCONSUMED, the condition fails, and the opaque fallback
          // absorbs the section. An earlier comment of mine claimed these "cannot
          // duplicate" a named argument — they can, and review caught the sentence.
          test "FG-175 gap: a duplicate in when-tag lands in the same fallback" {
              let p = ok (mk "    stage('B') { when { tag pattern: 'v1', pattern: 'v2' }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtestf "expected the opaque fallback to swallow it, got %A" other
          }

          test "FG-175 gap: a duplicate in when-branch lands in the same fallback" {
              let p = ok (mk "    stage('B') { when { branch pattern: 'a', pattern: 'b' }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtestf "expected the opaque fallback to swallow it, got %A" other
          }

          test "FG-175 gap: a duplicate in when-changelog lands in the same fallback" {
              let p = ok (mk "    stage('B') { when { changelog pattern: 'a', pattern: 'b' }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtestf "expected the opaque fallback to swallow it, got %A" other
          }

          test "FG-175 gap: a duplicate in when-triggeredBy lands in the same fallback" {
              let p = ok (mk "    stage('B') { when { triggeredBy cause: 'a', cause: 'b' }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtestf "expected the opaque fallback to swallow it, got %A" other
          }

          test "FG-175 gap: a duplicate in when-changeset lands in the same fallback" {
              let p = ok (mk "    stage('B') { when { changeset pattern: 'a', pattern: 'b' }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtestf "expected the opaque fallback to swallow it, got %A" other
          }

          // AND A SECOND ROUTE TO THE SAME GAP, which this test found by being WRONG.
          //
          // Four review rounds went on enumerations of mine that were each missing one
          // more condition — `equals`, then the single-key ones, then `changeset` — so
          // this was written to assert the PROPERTY instead: that leftover arguments
          // always end in `whenSectionOpaque`. They do not. `changeRequest` accepts only
          // empty parens here, and `changeRequest target: 'a', target: 'b'` parses as TWO
          // conditions implicitly ANDed — `WhenAllOf [WhenChangeRequest; WhenUnmodelled
          // ("target", ": 'a', target: 'b'")]`. Stray argument text becomes an extra
          // condition rather than a parse failure.
          //
          // The USER-VISIBLE outcome is identical (unmodelled -> fail closed -> the stage
          // SKIPS, where Jenkins rejects the pipeline), but the mechanism is different, so
          // a FG-175 fix aimed only at the opaque fallback would leave this route open.
          // Recorded here because the enumeration was never the hard part.
          test "FG-175 gap: leftover named args become a second, implicitly ANDed condition" {
              let p = ok (mk "    stage('B') { when { changeRequest target: 'a', target: 'b' }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenAllOf parts) ->
                  Expect.isTrue
                      (parts |> List.exists (function WhenUnmodelled _ -> true | _ -> false))
                      "the leftover arrives as an unmodelled sibling condition, so the stage fails closed"
              | other -> failtestf "expected an implicit AllOf, got %A" other
          }

          test "the ORDINARY single-key conditions still parse" {
              // What the tripwires above must not be confused with.
              ok (mk "    stage('B') { when { tag pattern: 'v1' }\n steps { sh 'x' } }") |> ignore
              ok (mk "    stage('B') { when { branch 'main' }\n steps { sh 'x' } }") |> ignore
          }

          test "FG-175 gap: a duplicate in when-environment PARSES, and the condition is unmodelled" {
              let p = ok (mk "    stage('B') { when { environment name: 'T', value: 'a', value: 'b' }\n steps { sh 'x' } }")

              match p.Stages.[0].When with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtestf "expected the opaque fallback to swallow it, got %A" other
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

          test "environment is captured as key/value" {
              let src =
                  "pipeline {\n  agent any\n  environment {\n    FOO = 'bar'\n  }\n  stages {\n    stage('a') { steps { echo 'x' } }\n  }\n}\n"

              Expect.equal (ok src).Environment [ "FOO", "bar" ] "environment pair"
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

              match one "buildingTag('x')" with
              | Some(WhenUnmodelled _) -> ()
              | other -> failtest $"arguments must not be discarded, got {other}"

              match one "changeRequest(target: 'main')" with
              | Some(WhenUnmodelled _) -> ()
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

              match one "changeset glob: '**/*.java'" with
              | Some(WhenUnmodelled("changeset", _)) -> ()
              | other -> failtest $"`glob` is rejected by Jenkins and must be unmodelled, got {other}"

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
              let nested = ok (mk "    stage('a') { when { anyOf { beforeAgent true\n branch 'x' } } steps { echo 'y' } }")

              match nested.Stages.[0].When with
              | Some(WhenAnyOf [ WhenUnmodelled("beforeAgent", _); _ ]) -> ()
              | other -> failtest $"a nested directive must be unmodelled, got {other}"
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

/// FG-141. Slashy versus division, decided by POSITION: a `/` after something
/// that can end an expression is division; with no left operand it can only
/// open a slashy. The over-broad fix (every `/` opens a span) was an approval
/// bypass — `input message: 10 / 2` lost its prompt — and the narrow one here
/// must hold both directions at both scanner sites.
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
        (testList "Fogell.Pipeline.Parser" [ admissionLimits; declarativeDetection; structure; slashyPosition ])
