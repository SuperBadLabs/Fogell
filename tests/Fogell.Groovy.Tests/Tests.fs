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


let predicateValues =
    testList
        "FG-048 a `when` predicate's VALUE"
        [ test "a bare trailing expression is the value" {
              Expect.equal (run "1 == 1").Returned (Some(VBool true)) "single expression"
          }

          test "a multi-statement closure returns its LAST expression" {
              // REVIEW FIX (Codex, PR #13): only a script that was exactly one
              // expression produced a value, so `def deploy = true; deploy` gave
              // None — which a `when` reads as unevaluable and FAILS THE BUILD on,
              // where Jenkins simply runs the stage. Groovy returns the last
              // expression of a closure regardless of what precedes it.
              Expect.equal (run "def deploy = true\ndeploy").Returned (Some(VBool true)) "last expression wins"
          }

          test "an explicit return still wins" {
              Expect.equal (run "return false").Returned (Some(VBool false)) "explicit return"
          }

          test "an assignment IS a value, as in Groovy" {
              // This test previously asserted `def x = 1` produced NO value. That was
              // wrong about Groovy — a declaration with an initialiser evaluates to the
              // assigned value — and a Codex review on PR #13 caught the code matching
              // the wrong assertion. `expression { def deploy = true; deploy = false }`
              // must read as false, not as unevaluable.
              Expect.equal (run "def x = 1").Returned (Some(VInt 1L)) "declaration yields its value"
              Expect.equal (run "def d = true\nd = false").Returned (Some(VBool false)) "reassignment yields its value"
          }

          test "a predicate that truly produces nothing stays None" {
              // None must remain distinguishable from false: nothing ran, so there is
              // no value — which is unevaluable, not negative.
              Expect.equal (run "if (false) { 1 }").Returned None "untaken branch yields nothing"
          }

          test "an untaken trailing conditional yields nothing, not an earlier value" {
              // REVIEW FIX (Codex, PR #14 round 3): `true` set the trailing value and
              // the untaken `if` left it there, so this read as TRUE. The trailing
              // value belongs to the FINAL statement, not to whatever ran last.
              Expect.equal (run "true\nif (false) { false }").Returned None "untaken if clears it"
              Expect.equal (run "true\nif (true) { false }").Returned (Some(VBool false)) "taken branch supplies it"
              Expect.equal (run "true\nwhile (false) { 1 }").Returned None "loop that never runs clears it"
          }

          test "an uninitialised trailing declaration yields null, not the previous value" {
              // Round-6 finding: value tracking covered only INITIALISED declarations,
              // so `true; def x` left `true` in place and `[1].any { … }` was true.
              Expect.equal (run "true\ndef x").Returned (Some VNull) "uninitialised def is null"
          }

          test "an assignment through a property or index target yields its RHS" {
              // Round-7 finding: only the bare-variable form recorded a value, so
              // `env.DEPLOY = false` or `values[0] = false` as the FINAL statement left
              // LastValue absent or stale — reported unevaluable, or reusing an earlier
              // truthy value. Groovy assignments yield their RHS whatever the target is.
              Expect.equal (run "true\nenv.DEPLOY = false").Returned (Some(VBool false)) "property target"

              // The INDEX form (`xs[0] = false`) is not asserted here because the
              // Groovy parser cannot parse it at all yet — a separate gap, measured at
              // 9 corpus files, tracked as FG-015b. Asserting it here would have made
              // this test fail for a reason unrelated to what it is testing.
          }

          test "a closure returns its trailing expression without `return`" {
              // `[1].any { it == 1 }` was FALSE because applyClosure discarded the
              // block's value unless an explicit `return` appeared.
              Expect.equal (run "[1].any { it == 1 }").Returned (Some(VBool true)) "implicit closure return"
          } ]

let stepValueUse =
    // FG-160/FG-174. A step call in a VALUE position is refused unless the step can
    // actually supply a value.
    //
    // The original reason was the batch model: it collected `StepCall` effects and every
    // call evaluated to `VNull`, so a body using a return value decided branches on null.
    // FG-172 replaced that with a live host and FG-174 taught `sh` to answer
    // `returnStdout`/`returnStatus`, so the rule is now narrower and the exemplars below
    // had to change with it: a call that OPTS IN is admitted, and a call that does not is
    // still refused — the walker's dispatch returns unit, so there is nothing to hand back.
    //
    // Which is why these tests use `sh(script: 'x')` where they once used
    // `sh(script: 'x', returnStdout: true)`. Left alone they would have kept passing on
    // the day the refusal was lifted, asserting a rule the code no longer has.
    let uses src =
        // A STUB of the real contract, deliberately. This assembly must not depend on
        // `Fogell.Differential` — keeping the step vocabulary out of the interpreter layer
        // is why `find` takes a predicate at all. These tests assert that `find` CONSULTS
        // the rule and walks every value position; the rule ITSELF (which steps, and
        // `returnStatus` winning when both are set) is `WalkerRules.returnContract` and is
        // tested against that function in Fogell.Differential.Tests.
        Fogell.Groovy.Interpreter.StepValueUse.find
            steps.Contains
            (fun n so st -> (n = "sh" || n = "bat") && (so || st))
            (parseOk src)

    let usedSteps src = uses src |> List.map (fun u -> u.Step)

    testList
        "FG-160 step calls whose value is used"
        [ test "a bare step statement is NOT a value use" {
              // The whole point: the common shape must still run. If this fires, the
              // refusal rejects the corpus it was meant to admit.
              Expect.isEmpty (uses "sh 'make'\necho 'done'") "a discarded call is safe"
          }

          test "a step in a variable initialiser IS a value use" {
              Expect.equal (usedSteps "def out = sh(script: 'x')") [ "sh" ] "def RHS"
          }

          test "a step in an if condition IS a value use" {
              Expect.equal (usedSteps "if (sh(script: 'x') == 0) { echo 'ok' }") [ "sh" ] "condition"
          }

          test "a step in an ASSIGNMENT is a value use" {
              Expect.equal (usedSteps "x = sh(script: 'x')") [ "sh" ] "assignment RHS"
          }

          test "a step nested in a string interpolation is a value use" {
              // The sneakiest shape, and the one a naive statement-level check misses.
              Expect.equal (usedSteps "echo \"got ${sh(script: 'x')}\"") [ "sh" ] "interpolation"
          }

          test "a step as an ARGUMENT to a bare statement call is a value use" {
              // The outer call is a discarded statement; the inner one is not. A check
              // that only asked "is this statement a call" would pass this.
              Expect.equal (usedSteps "echo sh(script: 'x')") [ "sh" ] "argument"
          }

          test "a step inside a TRAILING BLOCK is not a VALUE use" {
              // `timeout(5) { sh 'x' }` discards the inner value exactly as a statement
              // does — so this finder, which answers only the VALUE question, says
              // nothing about it.
              //
              // IT IS NOT THEREFORE RUNNABLE. This comment claimed a trailing block was a
              // "perfectly runnable shape", and the pre-push verifier showed it is not:
              // the interpreter runs the closure immediately and flattens it, so a
              // replayed wrapper arrives with no body and `dir('x') { sh 'pwd' }` runs in
              // the wrong directory. `findWrapperCalls` refuses it. One sentence cannot
              // answer both questions, and the old one tried.
              Expect.isEmpty (uses "node { sh 'make' }") "a trailing block holds statements, not a value"
          }

          test "a WRAPPER call is refused, because replay cannot carry its body" {
              let wrappers src =
                  Fogell.Groovy.Interpreter.StepValueUse.findWrapperCalls steps.Contains (parseOk src)
                  |> List.map (fun u -> u.Step)

              Expect.equal (wrappers "node { sh 'make' }") [ "node" ] "a step with a trailing block"
          }

          test "a wrapper NESTED in control flow is still found" {
              let wrappers src =
                  Fogell.Groovy.Interpreter.StepValueUse.findWrapperCalls steps.Contains (parseOk src)
                  |> List.map (fun u -> u.Step)

              Expect.equal (wrappers "if (true) { node { sh 'make' } }") [ "node" ] "the walk reaches into branches"
          }

          test "a bare step with NO trailing block is not a wrapper" {
              // The refusal must not swallow the shape FG-160 exists to run.
              let wrappers src =
                  Fogell.Groovy.Interpreter.StepValueUse.findWrapperCalls steps.Contains (parseOk src)

              Expect.isEmpty (wrappers "sh 'make'\necho 'done'") "plain steps stay runnable"
          }

          test "a step in a RETURN is a value use" {
              Expect.equal (usedSteps "return sh(script: 'x')") [ "sh" ] "return value"
          }

          test "a step in a for-in SOURCE is a value use" {
              Expect.equal
                  (usedSteps "for (f in sh(script: 'ls').split()) { echo f }")
                  [ "sh" ]
                  "loop source"
          }

          test "a NON-step call is never flagged" {
              // `isStep` decides, not call shape. A user function or builtin returning a
              // value is ordinary Groovy and must not be refused.
              Expect.isEmpty (uses "def x = someHelper()") "only registered steps batch their effects"
          }

          test "the position is NAMED, not just the step" {
              // A refusal that says only "a step value is used" sends someone hunting.
              let u = uses "def out = sh(script: 'x')" |> List.exactlyOne
              Expect.equal u.Where "a variable initialiser" "the position is reported"
          }

          test "several uses are all reported, in source order" {
              let src = "def a = sh(script: '1')\nif (sh(script: '2') == 0) { echo a }"
              Expect.equal (usedSteps src) [ "sh"; "sh" ] "every use, not the first"
          }

          // FG-174. THE OTHER SIDE OF THE RULE. `sh` can now answer both flags, so a call
          // that opts in is ADMITTED — and these are what fail if someone restores the
          // blanket refusal, which is the direction the two earlier attempts went.
          test "returnStdout: true is ADMITTED in a value position" {
              Expect.isEmpty (uses "def out = sh(script: 'x', returnStdout: true)") "the step supplies a value"
          }

          test "returnStatus: true is ADMITTED in a value position" {
              Expect.isEmpty (uses "if (sh(script: 'x', returnStatus: true) == 0) { echo 'ok' }") "the step supplies a value"
          }

          // THE FLAG MUST BE A LITERAL `true`, and these three are why the check reads the
          // argument LIST rather than looking for a key.
          test "returnStdout: false is still refused" {
              // Opting OUT is not opting in. A check that merely spotted the KEY would
              // admit this and hand the script null.
              Expect.equal (usedSteps "def out = sh(script: 'x', returnStdout: false)") [ "sh" ] "false is not true"
          }

          test "a NON-LITERAL flag is still refused" {
              // A static reader cannot know what `flag` holds, so assuming it is true
              // would be guessing in the direction that fails open.
              Expect.equal (usedSteps "def out = sh(script: 'x', returnStdout: flag)") [ "sh" ] "unknown at read time"
          }

          test "the flag must be NAMED, not positional" {
              // `sh('x', true)` says nothing about WHICH option is being set.
              Expect.equal (usedSteps "def out = sh('x', true)") [ "sh" ] "a bare true opts in to nothing"
          }

          test "the flags belong to the SHELL STEPS, not to every step" {
              // The verifier ran this one: Fogell handed the script "hello\n" where
              // Jenkins' `echo` returns null and only warns about the unknown parameter,
              // so `got == null` took the other branch and skipped work Jenkins runs —
              // while the build reported success. Refusing is the honest answer, and it
              // is what an engine that cannot answer must do.
              Expect.equal
                  (usedSteps "def got = echo(message: 'hello', returnStdout: true)")
                  [ "echo" ]
                  "echo does not answer returnStdout"
          }
        ]

let hostedSteps =
    // FG-160 slice 2. The callback boundary, holding the four things the BATCH model
    // could not express — each one a refusal in slice 1, each found by review:
    //   a step's RETURN VALUE, a wrapper's BODY, `env` MUTATION, and per-step ordering
    //   the host can hang durability on.
    // These test the seam, not the walker; the walker side is separate work.
    let hostThat perform setEnv =
        { Perform = perform
          SetEnv = setEnv }

    /// Records what the host was asked to do, in order.
    let recording () =
        let log = ResizeArray<string>()

        let host =
            hostThat
                (fun name positional _named runBody ->
                    let args = positional |> List.map Value.toDisplay |> String.concat ","
                    log.Add $"{name}({args})"

                    match runBody with
                    | Some run ->
                        log.Add $"{name}:body-start"
                        run ()
                        log.Add $"{name}:body-end"
                    | None -> ()

                    VNull)
                (fun k v -> log.Add $"env:{k}={v}")

        host, log

    let runIt host src =
        Interpreter.runHosted host Budget.defaults steps (Env.empty) (parseOk src)

    testList
        "FG-160 slice 2 the host performs steps live"
        [ test "steps are performed IN ORDER, as they are reached" {
              let host, log = recording ()
              let outcome = runIt host "sh 'a'\nsh 'b'"

              Expect.isNone outcome.Fault "the script ran"
              Expect.equal (List.ofSeq log) [ "sh(a)"; "sh(b)" ] "source order, live"
          }

          test "Effects is EMPTY in hosted mode, because the steps already happened" {
              // A caller reading Effects here has asked for the wrong model; an empty list
              // makes that visible instead of handing back a replayable-looking log.
              let host, _ = recording ()
              let outcome = runIt host "sh 'a'"
              Expect.isEmpty outcome.Effects "hosted mode performs, it does not collect"
          }

          test "a step's RETURN VALUE reaches the script" {
              // The defect that started the refusals: batch mode evaluated every call to
              // null, so `def out = sh(...)` bound null and every branch on it was wrong.
              let host =
                  hostThat (fun _ _ _ _ -> VStr "deadbeef") (fun _ _ -> ())

              let outcome =
                  Interpreter.runHosted host Budget.defaults steps Env.empty
                      (parseOk "def out = sh(script: 'git rev-parse HEAD', returnStdout: true)\nreturn out")

              Expect.isNone outcome.Fault "no fault"
              Expect.equal outcome.Returned (Some(VStr "deadbeef")) "the host's value is the call's value"
          }

          test "a WRAPPER's body runs INSIDE the wrapper, not flattened before it" {
              // Batch mode evaluated a trailing closure immediately and flattened its
              // effects, so `dir('x') { sh 'pwd' }` ran `sh` outside `x`. The body arrives
              // as a thunk the host invokes where it chooses.
              let host, log = recording ()
              let outcome = runIt host "node('linux') { sh 'pwd' }"

              Expect.isNone outcome.Fault "no fault"

              Expect.equal
                  (List.ofSeq log)
                  [ "node(linux)"; "node:body-start"; "sh(pwd)"; "node:body-end" ]
                  "the inner step runs between the wrapper's setup and teardown"
          }

          test "a wrapper body is NOT run when the host declines to run it" {
              // `retry`, `timeout` and a skipped `when` all need this: the host decides
              // whether and how many times the body runs. Batch mode had already run it.
              let host = hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ())
              let ran = ref false

              let counting =
                  { host with
                      Perform =
                        fun name _ _ _ ->
                            if name = "sh" then ran.Value <- true
                            VNull }

              let outcome =
                  Interpreter.runHosted counting Budget.defaults steps Env.empty (parseOk "node('linux') { sh 'pwd' }")

              Expect.isNone outcome.Fault "no fault"
              Expect.isFalse ran.Value "an unrun body performs no steps"
          }

          test "a host FAULT stops the script where it happened" {
              // A failing step must not let the rest of the block run — batch mode could
              // not fail mid-script at all, because nothing had run yet.
              let host =
                  hostThat
                      (fun name _ _ _ ->
                          if name = "sh" then failwith "step failed"
                          VNull)
                      (fun _ _ -> ())

              Expect.throws
                  (fun () -> Interpreter.runHosted host Budget.defaults steps Env.empty (parseOk "sh 'a'\nsh 'b'") |> ignore)
                  "a host exception propagates rather than being swallowed"
          }
        ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "Fogell.Groovy" [ grammar; sandbox; budgets; semantics; predicateValues; stepValueUse; hostedSteps ])
