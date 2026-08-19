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
          // FG-174. Both parsers hold this rule, because both produce calls and a rule
          // held by only one of them is the shape of half the findings on this branch.
          test "a duplicate named argument is refused, parenthesised" {
              Expect.isFalse (parses "sh(script: 'exit 7', returnStatus: true, returnStatus: 'false')\n") "map literal duplicate"
          }

          test "a duplicate named argument is refused, command form" {
              // The form without parentheses goes through a DIFFERENT parser path, and
              // covering only the parenthesised one would leave the commonest spelling of
              // a step call unchecked.
              Expect.isFalse (parses "sh script: 'exit 7', returnStatus: true, returnStatus: false\n") "command form too"
          }

          test "DISTINCT named arguments still parse in both forms" {
              Expect.isTrue (parses "sh(script: 'make', returnStatus: true)\n") "parenthesised"
              Expect.isTrue (parses "sh script: 'make', returnStatus: true\n") "command form"
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

          test "a trailing closure ARGUMENT is a body, not a positional (batch mode)" {
              // The normalisation above the host split existed for the LIVE path only:
              // batch recorded the raw positional list, so this logged the closure as an
              // argument AND ran it as a body — the same block counted twice. Raised in
              // review on PR #53. Nothing in the walker read `Effects`, so no receipt
              // could have caught it; this is the only thing that can.
              let o = run "node 'label', { sh 'make' }\n"

              Expect.equal
                  (stepArgs o)
                  [ "node", [ "label" ]; "sh", [ "make" ] ]
                  "the closure is the body, and its contents are their own effects"
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
          SetEnv = setEnv
          // FG-178. These tests exercise the SEAM, not a walker environment; an empty
          // list keeps the body's bindings exactly as `runHosted` set them.
          CurrentEnv = fun () -> []
          // FG-184. The seam's own answer, spelled out here rather than imported from
          // `WalkerRules`: these tests drive `dir`/`retry`/`withEnv` bodies directly, and
          // borrowing the production set would make them pass because of what the walker
          // happens to register rather than because the normalisation asked at all.
          TakesBlock = fun name -> Set.contains name (set [ "dir"; "timeout"; "retry"; "withEnv" ]) }

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

          test "a NESTED wrapper still refreshes the Jenkins env binding" {
              // FG-201. The refresh's "ours" check was a per-invocation cell, so a
              // wrapper nested inside another found the OUTER wrapper's cell, failed
              // the identity check, and silently skipped its refresh — `${env.A}`
              // interpolated null under a green build. Receipt
              // `script-nested-wrappers-env` diverged on exactly this shape for four
              // days before a suite run caught it; CI cannot run the differential
              // suite, so this pins the refresh where CI can see it. The host below
              // maintains a real overlay stack for `withEnv`, which is the one thing
              // the recording host's empty CurrentEnv cannot exercise.
              let overlay: (string * string) list ref = ref []
              let log = ResizeArray<string>()

              let host =
                  { Perform =
                      fun name positional _named runBody ->
                          (match name, positional with
                           | "withEnv", [ VList entries ] ->
                               let pairs =
                                   entries
                                   |> List.choose (function
                                       | VStr s ->
                                           match s.Split([| '=' |], 2) with
                                           | [| k; v |] -> Some(k, v)
                                           | _ -> None
                                       | _ -> None)

                               let saved = overlay.Value
                               overlay.Value <- pairs @ saved

                               // restore in a FINALLY, as the walker does — a body
                               // fault must leave CurrentEnv reporting the enclosing
                               // overlay, or the FG-202 abnormal-exit read below
                               // would be asserted against a harness bug
                               try
                                   runBody |> Option.iter (fun run -> run ())
                               finally
                                   overlay.Value <- saved
                           | "sh", [ VStr rendered ] -> log.Add rendered
                           | _ -> runBody |> Option.iter (fun run -> run ()))

                          VNull
                    SetEnv = fun _ _ -> ()
                    CurrentEnv = fun () -> overlay.Value
                    TakesBlock = fun name -> Set.contains name (set [ "dir"; "withEnv" ]) }

              let outcome =
                  Interpreter.runHosted host Budget.defaults (set [ "sh"; "dir"; "withEnv" ]) Env.empty
                      (parseOk
                          "dir('sub') {\n  withEnv(['A=one']) { sh \"a:${env.A}\" }\n  sh \"after:${env.A}\"\n  try { withEnv(['B=two']) { def x = 1 / 0 } } catch (e) { }\n  sh \"catch:${env.B}\"\n  withEnv(['TARGET=prod']) { sh \"t:${env.TARGET}\" }\n}")

              Expect.isNone outcome.Fault "the script ran"

              // Both SIBLING wrappers under the outer `dir` must see their own
              // binding: under the per-invocation check both interpolate null.
              // The `catch:` read is the ABNORMAL exit — the faulting body leaves
              // `withEnv` by exception, a script-level catch swallows it, and the
              // read after it must still see the restored environment: the
              // verifier's construction against the first (return-only) spelling
              // of the FG-202 exit refresh.
              // And the read BETWEEN them must see the RESTORED outer environment,
              // not the first wrapper's retained snapshot — FG-202, the shape the
              // verifier constructed against the FG-201 fix (probe
              // `post-exit-env-read`: jenkins after:null, fogell read after:one
              // until the exit re-refresh landed).
              Expect.equal
                  (List.ofSeq log)
                  [ "a:one"; "after:null"; "catch:null"; "t:prod" ]
                  "entry refreshes inside each wrapper; exit restores on return AND on fault"
          }
        ]

/// FG-195: resolution is by SIGNATURE, as Groovy's is. The four measured shapes are
/// asserted TOGETHER because three refusals were added one at a time, each looking
/// complete, and the fourth was found inside the guard written for the third — a
/// signature model is only a fix if all four hold at once.
let callableResolution =
    // preamble helpers arrive exactly as the walker supplies them: SFunc folded into
    // the incoming env, so the tests exercise the same seam the script block uses
    let helperEnv src =
        parseOk src
        |> List.fold
            (fun acc s ->
                match s with
                | Fogell.Groovy.SFunc(n, ps, b) -> Env.withFunc n ps b acc
                | _ -> acc)
            Env.empty

    let runWith env src =
        Interpreter.runStrictVars Budget.defaults steps env (parseOk src)

    let faultText (o: Outcome) =
        match o.Fault with
        | Some(Unsupported m) -> m
        | other -> failtestf "expected an Unsupported refusal, got %A" other

    testList
        "FG-195 callable resolution by signature"
        [ test "shape (a): a local closure SHADOWS a preamble helper" {
              let o = runWith (helperEnv "def x() { return 'HELPER' }") "def x = { 'LOCAL' }\nreturn x()"
              Expect.isNone o.Fault "runs"
              Expect.equal o.Returned (Some(VStr "LOCAL")) "Groovy resolves the local"
          }

          test "shape (b): overloads resolve by ARITY, both directions" {
              let env = helperEnv "def pick() { return 'zero' }\ndef pick(v) { return 'one' }"
              Expect.equal ((runWith env "return pick()").Returned) (Some(VStr "zero")) "zero-arg body"
              Expect.equal ((runWith env "return pick(1)").Returned) (Some(VStr "one")) "one-arg body"
          }

          test "shape (c): no matching arity is a refusal NAMING the signature" {
              let o = runWith (helperEnv "def constant(v) { return v }") "return constant()"
              let m = faultText o
              Expect.stringContains m "0 argument(s)" "names what was attempted"
              Expect.stringContains m "candidates take 1" "names what exists"
          }

          test "shape (d): a named-argument group is ONE Map argument" {
              let o = runWith (helperEnv "def zero() { return 'z' }") "return zero(foo: 1)"
              Expect.stringContains (faultText o) "1 argument(s)" "the Map counted as an argument"
          }

          test "a named-argument group binds as a Map, FIRST" {
              let env = helperEnv "def one(m) { return m }"
              match (runWith env "return one(foo: 'bar')").Returned with
              | Some(VMap m) -> Expect.equal (Map.tryFind "foo" m.Value) (Some(VStr "bar")) "the group arrived as a Map"
              | other -> failtestf "expected a Map return, got %A" other
          }

          test "a trailing closure is a FINAL argument, and the parameter is callable" {
              // exercises helper resolution AND closure-value invocation in one shape:
              // the block binds to `c`, and `c()` is a local-closure call (FG-189)
              let env = helperEnv "def wrap(c) { return c() }"
              let o = runWith env "return wrap { 'FROM-BLOCK' }"
              Expect.isNone o.Fault "runs"
              Expect.equal o.Returned (Some(VStr "FROM-BLOCK")) "block passed, then invoked"
          }

          test "a closure with declared parameters binds by arity" {
              let o = runWith Env.empty "def f = { a, b -> a + b }\nreturn f(1, 2)"
              Expect.equal o.Returned (Some(VInt 3L)) "two arguments bound"
          }

          test "a closure arity mismatch is a refusal naming the signature" {
              let o = runWith Env.empty "def f = { a, b -> a + b }\nreturn f(1)"
              Expect.stringContains (faultText o) "2 parameter(s)" "names the closure's arity"
          }

          test "a SAME-ARITY duplicate is refused at the call, not silently last-wins" {
              let o = runWith Env.empty "def d() { return 'one' }\ndef d() { return 'two' }\nreturn d()"
              Expect.stringContains (faultText o) "more than once" "the ambiguity is named"
          }

          test "the .call spelling resolves exactly as the bare call does" {
              let o = runWith Env.empty "def f = { v -> v }\nreturn f.call('VIA-CALL')"
              Expect.equal o.Returned (Some(VStr "VIA-CALL")) "explicit call spelling"
          }

          test "a caught fault inside a closure does not leak call depth" {
              // the verifier's construction: each caught fault skipped the depth
              // decrement, so 70 caught faults exhausted the 64-deep call budget
              // with an UNCATCHABLE refusal where Jenkins prints and succeeds
              let o =
                  runWith
                      Env.empty
                      "def boom = { 1 / 0 }\nfor (i in 1..70) { try { boom() } catch (e) { } }\nreturn 'SURVIVED'"

              Expect.isNone o.Fault "depth restored on the fault path"
              Expect.equal o.Returned (Some(VStr "SURVIVED")) "the loop of caught faults completes"
          }

          test "in-script overloads resolve too, not only preamble ones" {
              let o = runWith Env.empty "def pick() { return 'zero' }\ndef pick(v) { return 'one' }\nreturn pick(9)"
              Expect.equal o.Returned (Some(VStr "one")) "arity picks among hoisted candidates"
          } ]

/// FG-193: a Groovy map is a REFERENCE object. Aliases share the ref; the Jenkins
/// environment is recognised by the MAP's identity, however many rebinds away.
let mapIdentity =
    testList
        "FG-193 map reference identity"
        [ test "a mutation through an alias is visible to every name" {
              // measured: jenkins=alias:x fogell=alias:null before the ref
              let o = Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk "def local = [:]\ndef other = local\nother.FOO = 'x'\nreturn local.FOO")
              Expect.equal o.Returned (Some(VStr "x")) "the write reached the shared map"
          }

          // the statement-level index spelling (`n['b'] = …`) is FG-015b's open
          // parse gap and cannot be covered here until it parses

          test "a non-map receiver's property write faults CATCHABLY in strict mode" {
              // measured: Jenkins throws and a script-level catch intercepts it —
              // the fault class matters as much as the fault (the first spelling
              // raised an uncatchable refusal and diverged where parity had held)
              let uncaught = Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk "def s = 'str'\ns.FOO = 'x'\nreturn 'SURVIVED'")

              match uncaught.Fault with
              | Some(UnknownProperty k) -> Expect.equal k "FOO" "names the property"
              | other -> failtestf "expected UnknownProperty, got %A" other

              let caught =
                  Interpreter.runStrictVars Budget.defaults steps Env.empty
                      (parseOk "def s = 'str'\ntry { s.FOO = 'x' } catch (Exception e) { }\nreturn 'SURVIVED'")

              Expect.isNone caught.Fault "the catch intercepted it"
              Expect.equal caught.Returned (Some(VStr "SURVIVED")) "execution continued, as on Jenkins"
          }

          test "the Jenkins env is recognised by VALUE identity through a rebind" {
              // measured: the aliased write reached Jenkins' shell and silently
              // vanished here — it must route to the host exactly as the direct
              // spelling does, however many rebinds separate it
              let sets = ResizeArray<string * string>()

              let host =
                  { Perform = fun _ _ _ runBody -> (runBody |> Option.iter (fun run -> run ())); VNull
                    SetEnv = fun k v -> sets.Add(k, v)
                    CurrentEnv = fun () -> []
                    TakesBlock = fun _ -> false }

              let seeded =
                  { Env.ofValues (Map.ofList [ "x", VNull ]) with Vars = Map.ofList [ "env", ref (VMap(ref Map.empty)) ] }

              let o =
                  Interpreter.runHosted host Budget.defaults steps seeded
                      (parseOk "def saved = env\ndef env = saved\nenv.FOO = 'bar'\nreturn 'DONE'")

              Expect.isNone o.Fault "ran"
              Expect.equal (List.ofSeq sets) [ "FOO", "bar" ] "the aliased write reached the host"
          } ]

/// FG-191: equality and display survive cyclic values. Three shapes killed the
/// PROCESS before this — no fault, no receipt, walker dead — and each is pinned
/// here where CI can see it, beside the identity semantics that replaced the
/// structural walk for closures.
let cyclicValues =
    let runS src =
        Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk src)

    testList
        "FG-191 cyclic values and closure identity"
        [ test "displaying a self-referential map renders (this Map), as Groovy does" {
              let o = runS "def m = [:]\nm.self = m\nreturn \"${m}\""
              Expect.isNone o.Fault "survives"
              Expect.equal o.Returned (Some(VStr "[self:(this Map)]")) "Groovy's own rendering"
          }

          test "same-AST closures from two calls are NOT equal — identity, not structure" {
              // this exact comparison was a process-killing stack overflow
              let o = runS "def make() {\n    def r\n    r = { r }\n    return r\n}\ndef a = make()\ndef b = make()\nreturn a == b"
              Expect.isNone o.Fault "survives"
              Expect.equal o.Returned (Some(VBool false)) "distinct invocations, distinct closures"
          }

          test "an aliased closure IS equal; a distinct literal is not" {
              let o = runS "def a = { 1 }\ndef b = { 1 }\ndef c = a\nreturn [a == b, a == c]"
              Expect.equal o.Returned (Some(VList [ VBool false; VBool true ])) "reference semantics"
          }

          test "comparing two distinct cyclic maps FAULTS instead of dying" {
              // Groovy's own chase is a JVM StackOverflowError and a failed build;
              // the fault below is this runtime's survivable spelling of the same
              let o = runS "def m = [:]\nm.self = m\ndef n = [:]\nn.self = n\nreturn m == n"

              match o.Fault with
              | Some(Thrown(VStr s)) -> Expect.stringContains s "StackOverflowError" "the matching fault"
              | other -> failtestf "expected the cycle fault, got %A" other
          }

          test "closures minted by ONE literal in a loop are DISTINCT" {
              // the identity model's first spelling compared the captured env
              // RECORD, and a while body assigning through cells never changes
              // it — this returned true, a false equality this branch's own
              // probe caught before it shipped; every evaluation mints a fresh
              // record now (receipt script-loop-closure-eq)
              let o =
                  Interpreter.runStrictVars Budget.defaults steps Env.empty
                      (parseOk "def xs = []\ndef i = 0\nwhile (i < 2) {\n    xs = xs + [{ 9 }]\n    i = i + 1\n}\nreturn xs[0] == xs[1]")

              Expect.equal o.Returned (Some(VBool false)) "per evaluation, not per source location"
          }

          test "a map compared WITH ITSELF is simply equal — identity short-circuits" {
              let o = runS "def m = [:]\nm.self = m\nreturn m == m"
              Expect.equal o.Returned (Some(VBool true)) "same ref, no walk"
          } ]

/// FG-180. Command-form calls in EXPRESSION position, and the constructs the
/// same corpus sweep recovered. The first test pins the defect that made this
/// P1: the positional form ADMITTED with a wrong AST — two statements, the
/// variable bound to an unresolved name — which no file count can see.
let fg180Grammar =
    let ast src = parseOk src

    testList
        "FG-180 expression-position command form"
        [ test "positional command-form initialiser is a CALL, not two statements" {
              match ast "def m = tool 'M3'" with
              | [ SDef("m", Some(ECall(FreeCall "tool", [ APos(EStr "M3") ], None))) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "the call actually runs — effects, not just shape" {
              let o = run "def out = sh 'make'\n"
              Expect.contains (stepNames o) "sh" "initialiser RHS reached the effect list"
          }

          test "named command-form initialiser" {
              let o = run "def st = sh script: 'make', returnStatus: true\n"
              Expect.contains (stepNames o) "sh" "named args in expression position"
          }

          test "command form inside a GString placeholder" {
              Expect.isTrue (parses "def p = \"${tool 'M3'}/bin\"\n") "placeholder holds a whole expression"
          }

          test "plain assignment RHS command form" {
              match ast "mvnHome = tool 'M3'" with
              | [ SAssign(EVar "mvnHome", ECall(FreeCall "tool", _, _)) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "a line break ends the command form — the next statement is not swallowed" {
              match ast "def x = foo\n'M3'" with
              | [ SDef("x", Some(EVar "foo")); SExpr(EStr "M3") ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "binary operators never read as command arguments" {
              match ast "def x = a - 1" with
              | [ SDef("x", Some(EBinary("-", _, _))) ] -> ()
              | other -> failtestf "subtraction was consumed: %A" other

              match ast "def y = 10 / 2" with
              | [ SDef("y", Some(EBinary("/", _, _))) ] -> ()
              | other -> failtestf "division was consumed: %A" other
          }

          test "a duplicate named argument is refused in expression position too" {
              Expect.isFalse (parses "def n = tool name: 'a', name: 'b'\n") "FG-174 reaches the new position"
          }

          test "string-named arguments in both call forms" {
              Expect.isTrue (parses "parallel 'UI Tests': { echo 'x' }, 'API': { echo 'y' }\n") "command form"
              Expect.isTrue (parses "parallel('UI Tests': { echo 'x' })\n") "parenthesised"
          }

          test "an index assignment is not swallowed into a command call" {
              match ast "builds['a'] = { echo 'x' }" with
              | [ SAssign(EIndex(EVar "builds", _), EClosure _) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "typed function declarations, return type and parameter types erased" {
              let s = ast "void report(Maven mvn) { echo 'x' }"
              Expect.contains (Ast.definedFunctions s) "report" "typed decl recognised"
              let s2 = ast "def helmLint(String chart_dir) { echo 'x' }"
              Expect.contains (Ast.definedFunctions s2) "helmLint" "typed param in def decl"
          }

          test "a default parameter value is REFUSED, not silently dropped" {
              // `SFunc` cannot carry the value; admitting the declaration and
              // losing the default would fault a valid zero-arg call at runtime.
              Expect.isFalse (parses "def f(x = true) { echo 'x' }\n") "honest refusal until the AST can hold it"
          }

          test "a bare return does not swallow the next line's step" {
              // The verifier's construction on this diff: `if (skip) return`
              // then `sh 'make'` must run the sh exactly when Groovy does —
              // on the fall-through path, never on the return path.
              match ast "if (skip) return\nsh 'make'" with
              | [ SIf(_, [ SReturn None ], []); SExpr(ECall(FreeCall "sh", _, _)) ] -> ()
              | other -> failtestf "the return swallowed the step: %A" other
          }

          test "a return value on the return's own line still parses" {
              match ast "def f() {\n  return tool 'M3'\n}" with
              | [ SFunc("f", [], [ SReturn(Some(ECall(FreeCall "tool", _, _))) ]) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "an operator chains after a slashy literal across a space" {
              // The slashy was the one literal not lexeme-wrapped; `/a/ + x`
              // consumed the slashy and stopped dead at the operator.
              match ast "def m = /Deploy; / + env.TARGET" with
              | [ SDef("m", Some(EBinary("+", EStr "Deploy; ", EProp(EVar "env", "TARGET")))) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "a multi-assignment's source is evaluated ONCE" {
              // The lowering copied the source into one binding per target, so
              // an effectful RHS ran once per name — step effects duplicated,
              // names possibly bound from different results (Codex P1, PR #98;
              // the copy predates the command form and bit paren calls too).
              let o = run "def (a, b) = sh 'once'\n"
              Expect.equal (stepNames o) [ "sh" ] "one call, not one per target"
          }

          test "a multi-assignment still binds by index" {
              let o = run "def (a, b) = ['first', 'second']\necho a\necho b\n"

              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "first" ]; "echo", [ "second" ] ]
                  "each name reads its own index of the one evaluation"
          }

          test "a typed declaration cannot merge two lines" {
              // `echo msg` then `(x) { … }` is two statements; reading them as
              // a declaration of `msg` drops the echo — FG-187's class.
              match ast "echo msg\n(x) { echo 'y' }" with
              | SExpr(ECall(FreeCall "echo", _, _)) :: _ -> ()
              | other -> failtestf "merged into a declaration: %A" other
          } ]

/// FG-015. The board row outlived the implementation that admitted most of its
/// six constructs. Keep one named semantic repro per construct so a closure
/// audit cannot mistake grammar acceptance for execution parity. Spread-dot is
/// deliberately a passing pin of the one remaining divergence: once it is
/// implemented, this assertion and the PARTIAL board row must change together.
let fg015ClosureAudit =
    let runStrict src =
        Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk src)

    testList
        "FG-015 six-construct closure audit"
        [ test "nested-quote GString renders the quoted inner text" {
              let o = runStrict "def who = 'world'\necho \"say \\\"${who}\\\"\"\n"
              Expect.isNone o.Fault "the GString evaluates"
              Expect.equal (stepArgs o) [ "echo", [ "say \"world\"" ] ] "quotes survive around the interpolation"
          }

          test "range is inclusive when used as a for-in source" {
              let o = runStrict "def seen = ''\nfor (i in 1..3) { seen = seen + i }\necho seen\n"
              Expect.isNone o.Fault "the range evaluates"
              Expect.equal (stepArgs o) [ "echo", [ "123" ] ] "both endpoints are visited"
          }

          test "switch selects an arm and break leaves the switch" {
              let src =
                  "def selected = 'miss'\n"
                  + "switch ('b') {\n"
                  + "  case 'a': selected = 'a'; break\n"
                  + "  case 'b': selected = 'b'; break\n"
                  + "  default: selected = 'default'; break\n"
                  + "}\necho selected\n"

              let o = runStrict src
              Expect.isNone o.Fault "the switch evaluates"
              Expect.equal (stepArgs o) [ "echo", [ "b" ] ] "the matching arm wins"
          }

          test "instanceof recognises a String" {
              let o = runStrict "def value = 'text'\nreturn value instanceof String\n"
              Expect.isNone o.Fault "the type check evaluates"
              Expect.equal o.Returned (Some(VBool true)) "String is recognised"
          }

          test "multi-assign binds list elements by index" {
              let o = runStrict "def (left, right) = ['L', 'R']\necho \"${left}:${right}\"\n"
              Expect.isNone o.Fault "the assignment evaluates"
              Expect.equal (stepArgs o) [ "echo", [ "L:R" ] ] "both names are bound"
          }

          test "spread-dot remains divergent instead of masquerading as closed" {
              let o = runStrict "def rows = [[name: 'a'], [name: 'b']]\ndef names = rows*.name\nreturn names\n"

              match o.Fault with
              | Some(UnknownProperty "name") -> ()
              | other -> failtestf "expected the measured spread-dot divergence, got %A" other
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "Fogell.Groovy" [ grammar; fg015ClosureAudit; fg180Grammar; sandbox; budgets; semantics; predicateValues; stepValueUse; hostedSteps; callableResolution; mapIdentity; cyclicValues ])
