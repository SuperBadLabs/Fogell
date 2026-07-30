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
        [ test "stages and steps are recovered" {
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

          test "named step arguments are separated from positional" {
              let p = ok (mk "    stage('a') { steps { archiveArtifacts artifacts: '*.jar', fingerprint: true } }")
              let step = p.Stages.[0].Steps.[0]
              Expect.equal step.Name "archiveArtifacts" "step name"
              Expect.contains (step.Named |> List.map fst) "artifacts" "named arg present"
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

          test "helper defs before AND after the pipeline block are tolerated" {
              let src =
                  "#!/usr/bin/env groovy\n@Library('x') _\ndef helper() { return 1 }\n"
                  + mk "    stage('a') { steps { echo 'x' } }"
                  + "\ndef trailing() { return 2 }\n"

              Expect.equal (ok src).Stages.Length 1 "block found amid surrounding statements"
          }

          test "a pipeline with no stages is a named rejection" {
              Expect.equal (err "pipeline {\n  agent any\n}\n").Code NoStages "no_stages"
          }

          test "a scripted file reports no_pipeline_block, not a syntax error" {
              Expect.equal (err "node { sh 'make' }").Code NoPipelineBlock "no_pipeline_block"
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs
        []
        argv
        (testList "Fogell.Pipeline.Parser" [ admissionLimits; declarativeDetection; structure ])
