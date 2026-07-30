module Fogell.Groovy.Tests

open Expecto
open Fogell.Groovy
open Fogell.Groovy.Interpreter
open Fogell.Admission

let private parseOk src =
    match Fogell.Groovy.Parser.Parser.parse src with
    | Ok s -> s
    | Error e -> failtestf "parse failed: %s" (string e)

let private parses src =
    match Fogell.Groovy.Parser.Parser.parse src with
    | Ok _ -> true
    | Error _ -> false

let private steps = set [ "sh"; "echo"; "node"; "stage"; "library"; "import"; "archiveArtifacts" ]

let private run src =
    Interpreter.runDefault steps (parseOk src)

let private stepNames (o: Outcome) =
    o.Effects |> List.map (function StepCall(n, _, _) -> n)

let private stepArgs (o: Outcome) =
    o.Effects
    |> List.map (function
        | StepCall(n, pos, _) -> n, pos |> List.map Value.toDisplay)

/// FG-011: every construct below was confirmed necessary by measuring the real
/// corpus. Grammar coverage is demand-driven, not speculative.
let grammar =
    testList
        "FG-011 scripted grammar"
        [ test "shebang, annotation and import are tolerated" {
              Expect.isTrue (parses "#!/usr/bin/env groovy\n@Library('x') _\nimport a.b.C\nsh 'make'\n") "preamble"
          }
          test "@Library is preserved as a library() call, not discarded" {
              let o = run "@Library('mylib') _\nsh 'make'\n"
              Expect.contains (stepNames o) "library" "dependency survives into the AST"
          }
          test "trailing commas in lists and maps" {
              Expect.isTrue (parses "def a = [1, 2,]\ndef b = [k: 1,]\n") "sepEndBy"
          }
          test "slashy strings and regex operators" {
              Expect.isTrue (parses "def m = ('main' =~ /ma.n/)\ndef n = ('x' ==~ /x/)\n") "=~ and ==~"
          }
          test "typed closure parameters" {
              Expect.isTrue (parses "def f = { String s -> s }\n") "typed params"
          }
          test "final and postfix increment" {
              Expect.isTrue (parses "final X = 1\ndef i = 0\ni++\n") "final, ++"
          }
          test "C-style for loop" {
              Expect.isTrue (parses "for (int i = 0; i < 3; i++) { echo 'x' }\n") "C-style for"
          }
          test "ranges, switch, instanceof, multi-assign" {
              Expect.isTrue (parses "def r = (1..3)\n") "range"
              Expect.isTrue (parses "switch (a) { case 1: break\n default: break }\n") "switch"
              Expect.isTrue (parses "if (a instanceof String) { echo 'x' }\n") "instanceof"
              Expect.isTrue (parses "def (a, b) = [1, 2]\n") "multi-assign"
          }
          test "spread-dot and safe navigation" {
              Expect.isTrue (parses "def a = b*.c\ndef d = e?.f\n") "*. and ?."
          }
          test "user-defined functions are recognised, not treated as unknown steps" {
              let script = parseOk "def helper(a) { return a }\nhelper(1)\n"
              Expect.contains (Ast.definedFunctions script) "helper" "declared function found"
          } ]

/// FG-013: the sandbox is the reason ADR 0002 was affordable. These tests are
/// the acceptance criterion.
let sandbox =
    testList
        "FG-013 sandbox denies escape"
        [ test "file access is denied by name" {
              let o = run "def f = new File('/etc/passwd')\n"

              match o.Fault with
              | Some(Denied d) -> Expect.stringContains d.Attempted "File" "names what was attempted"
              | other -> failtestf "expected a denial, got %A" other
          }

          test "process execution is denied" {
              let o = run "'ls'.execute()\n"
              Expect.isSome o.Fault "denied"
          }

          test "reflection is denied" {
              let o = run "def c = getClass()\n"
              Expect.isSome o.Fault "denied"
          }

          test "an unregistered step is denied and names itself" {
              let o = run "kubernetesDeploy(configs: 'x')\n"

              match o.Fault with
              | Some(Denied d) ->
                  Expect.equal d.Attempted "kubernetesDeploy" "names the step"
                  Expect.stringContains d.Reason "registered step" "explains why"
              | other -> failtestf "expected a denial, got %A" other
          }

          test "a registered step becomes a host effect, never a direct action" {
              let o = run "sh 'make'\necho 'done'\n"
              Expect.equal (stepNames o) [ "sh"; "echo" ] "effects recorded in order"
              Expect.isNone o.Fault "no fault"
          }

          test "denial happens before the step is emitted" {
              let o = run "sh 'ok'\nnew File('/etc/passwd')\nsh 'never'\n"
              Expect.equal (stepNames o) [ "sh" ] "the second sh is never reached"
              Expect.isSome o.Fault "faulted"
          }

          test "every known escape name is denied" {
              for name in Sandbox.knownEscapes do
                  match Sandbox.admitCall steps Set.empty name with
                  | Error _ -> ()
                  | Ok _ -> failtestf "%s must not be admissible" name
          } ]

/// Budgets: the interpreter runs on the admission path, so a runaway script is
/// a denial of service on the controller.
let budgets =
    testList
        "FG-013 evaluation budgets"
        [ test "an infinite while loop is stopped" {
              let o = run "def i = 0\nwhile (true) { i = i + 1 }\n"

              match o.Fault with
              | Some(BudgetExhausted _) -> ()
              | other -> failtestf "expected BudgetExhausted, got %A" other
          }

          test "an enormous range is refused rather than materialised" {
              let o = run "def r = (1..100000000)\n"

              match o.Fault with
              | Some(BudgetExhausted _) -> ()
              | other -> failtestf "expected BudgetExhausted, got %A" other
          }

          test "unbounded recursion is stopped by call depth" {
              let o = run "def f(n) { return f(n + 1) }\nf(0)\n"

              match o.Fault with
              | Some(BudgetExhausted _) -> ()
              | other -> failtestf "expected BudgetExhausted, got %A" other
          }

          test "a catastrophic regex does not hang" {
              let sw = System.Diagnostics.Stopwatch.StartNew()
              run "def s = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!'\ndef m = (s =~ /(a+)+$/)\n" |> ignore
              sw.Stop()
              Expect.isLessThan sw.ElapsedMilliseconds 5000L "regex has a timeout"
          } ]

let semantics =
    testList
        "interpreter semantics"
        [ test "GString interpolation resolves variables" {
              let o = run "def name = 'world'\nsh \"echo ${name}\"\n"
              Expect.equal (stepArgs o) [ "sh", [ "echo world" ] ] "interpolated"
          }
          test "a user function is called and its return used" {
              let o = run "def target() { return 'prod' }\nsh \"deploy ${target()}\"\n"
              Expect.equal (stepArgs o) [ "sh", [ "deploy prod" ] ] "function result interpolated"
          }
          test "if/else selects the right branch" {
              let o = run "if (1 > 2) { sh 'no' } else { sh 'yes' }\n"
              Expect.equal (stepArgs o) [ "sh", [ "yes" ] ] "else taken"
          }
          test "for-in iterates a list" {
              let o = run "for (x in ['a','b']) { echo x }\n"
              Expect.equal (stepArgs o) [ "echo", [ "a" ]; "echo", [ "b" ] ] "both iterations"
          }
          test "each with a closure iterates" {
              let o = run "['a','b'].each { echo it }\n"
              Expect.equal (stepArgs o) [ "echo", [ "a" ]; "echo", [ "b" ] ] "closure applied"
          }
          test "try/catch recovers and continues" {
              let o = run "try { throw 'boom' } catch (e) { echo 'caught' }\nsh 'after'\n"
              Expect.equal (stepNames o) [ "echo"; "sh" ] "handler ran, execution continued"
              Expect.isNone o.Fault "recovered"
          }
          test "short-circuit avoids evaluating the right side" {
              let o = run "if (false && (1/0 == 0)) { sh 'no' } else { sh 'yes' }\n"
              Expect.isNone o.Fault "division by zero never evaluated"
              Expect.equal (stepArgs o) [ "sh", [ "yes" ] ] "else branch"
          }
          test "division by zero faults rather than crashing the host" {
              match (run "def x = 1 / 0\n").Fault with
              | Some(Thrown _) -> ()
              | other -> failtestf "expected Thrown, got %A" other
          }
          test "named step arguments are preserved" {
              let o = run "archiveArtifacts artifacts: '*.jar'\n"

              match o.Effects with
              | [ StepCall("archiveArtifacts", _, named) ] ->
                  Expect.equal (named |> List.map fst) [ "artifacts" ] "named arg kept"
              | other -> failtestf "unexpected effects: %A" other
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "Fogell.Groovy" [ grammar; sandbox; budgets; semantics ])
