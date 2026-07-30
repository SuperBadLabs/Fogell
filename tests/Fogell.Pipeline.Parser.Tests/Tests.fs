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
