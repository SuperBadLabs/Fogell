module Fogell.Differential.Tests

open System
open Expecto
open Fogell.Differential
open Fogell.Ir

/// FG-002f. The acceptance this ticket actually demanded: emit each look-alike FROM A
/// BUILD and assert it survives normalisation.
///
/// Five separate defects of one class reached review — `Terminated`, a changelog warning,
/// a Groovy compile report, skipped-stage narration, and exception-class names — each a
/// pattern a user's own output can match, removed from the compared output AND counted as
/// a reported failure reason, so an engine failing silently could still read PROVEN.
/// Six such patterns turned out to be protecting nothing and were deleted; the rest are
/// context-gated. These tests are what stops a sixth from being added.
let userOutputSurvives =
    testList
        "FG-002f user output is never mistaken for engine narration"
        [ test "a build may print words that look like engine narration" {
              let userLines =
                  [ "FATAL: the deploy script says so"
                    "No artifacts found in my own search"
                    "3 of 7 test(s) failed"
                    "Aborted by the operator, says my script"
                    "Stage \"deploy\" skipped due to earlier failure(s)"
                    "Warning, empty changelog is what my tool prints"
                    "WorkflowScript: 1: deployment report"
                    "1 error"
                    "hudson.AbortException: printed by a build, not thrown"
                    "an ordinary line, so no stack trace is implied"
                    "at index.js(10)" ]

              let kept = Trace.normaliseOutput userLines

              for line in userLines do
                  Expect.contains kept line $"user output must survive: {line}"
          }

          test "KNOWN LIMIT: a user printing an exception head followed by a frame is indistinguishable" {
              // Stated rather than papered over. The context gate cannot tell a build that
              // prints an exception name and then a frame-shaped line from Jenkins doing
              // the same, because they are byte-identical. That residual ambiguity is far
              // narrower than matching either line's text alone — which is what this class
              // of defect did five times — but it is not zero, and a test that pretended
              // otherwise would be the same over-claim in a new place.
              let ambiguous = [ "hudson.AbortException: printed by a build"; "at mine(1)" ]
              Expect.isEmpty (Trace.normaliseOutput ambiguous) "both are read as a trace"
          }

          test "a real Jenkins stack trace IS recognised, by context" {
              // The head is suppressed only because a frame follows it; the frames are
              // suppressed only inside the window the head opened.
              let engineLines =
                  [ "hudson.AbortException: script returned exit code 4"
                    "at PluginClassLoader for workflow-durable-task-step//org.Foo.bar(Foo.java:1)"
                    "at java.base/java.util.concurrent.FutureTask.run(Unknown Source)"
                    "real build output" ]

              let kept = Trace.normaliseOutput engineLines
              Expect.equal kept [ "real build output" ] "the whole trace is narration, the output is not"
          }

          test "a build may print the secret-interpolation warning's words" {
              // SIXTH instance of this class, and I introduced it in the same PR that added
              // these tests. Jenkins' warning is a three-line SEQUENCE; each line alone is
              // text a build can legitimately print, so matching them as four standalone
              // prefixes reopened the false-PROVEN path this file exists to close.
              let userLines =
                  [ "Warning: A secret was passed to my audit script"
                    "and here is an unrelated line"
                    "Affected argument(s) used the following variable(s): my own note"
                    "See https://jenkins.io/redirect/groovy-string-interpolation for details." ]

              let kept = Trace.normaliseOutput userLines

              for line in userLines do
                  Expect.contains kept line $"user output must survive: {line}"
          }

          test "the real warning SEQUENCE is recognised" {
              let engineLines =
                  [ "Warning: A secret was passed to \"echo\" using Groovy String interpolation, which is insecure."
                    "Affected argument(s) used the following variable(s): [TOKEN]"
                    "See https://jenkins.io/redirect/groovy-string-interpolation for details."
                    "token is ****" ]

              Expect.equal (Trace.normaliseOutput engineLines) [ "token is ****" ] "only the value survives"
          }

          test "a stack-trace-only failure still counts as a reported reason" {
              // REVIEW FIX (Codex, PR #16 round 9): moving exception heads and frames out
              // of isDiagnosticLine into a contextual gate left reportedFailureReason
              // blind to them. A Jenkins failure explained ONLY by a stack trace would
              // report NO reason while Fogell's `ERROR:` reported one — a
              // DiagnosticSilence divergence on an otherwise matching run.
              let jenkinsFailure =
                  [ "hudson.AbortException: script returned exit code 4"
                    "at PluginClassLoader for x//org.Foo.bar(Foo.java:1)" ]

              Expect.isTrue (Trace.reportedFailureReason jenkinsFailure) "the trace IS the explanation"
              Expect.isEmpty (Trace.normaliseOutput jenkinsFailure) "and it is not compared as output"

              // A build merely printing the class name explains nothing.
              Expect.isFalse
                  (Trace.reportedFailureReason [ "hudson.AbortException: printed by a build" ])
                  "no frame, no trace, no reason"
          }

          test "the secret-interpolation warning is contextual for BOTH engines" {
              // FG-100. Fogell emits Jenkins' own head+body sequence — an earlier
              // one-line wording of Fogell's own had to be recognised by shape ALONE,
              // so a build printing that shape was dropped from the comparison
              // unconditionally and a case whose evidence was that line could go
              // falsely PROVEN. One sequence, one contextual gate, both engines.
              for step in [ "echo"; "sh"; "archiveArtifacts" ] do
                  Expect.isEmpty
                      (Trace.normaliseOutput
                          [ $"Warning: A secret was passed to \"{step}\" using Groovy String interpolation, which is insecure."
                            "Affected argument(s) used the following variable(s): [TOKEN]" ])
                      $"the {step} warning sequence is engine narration"

              // The head ALONE is user output: the gate needs the body to follow, so a
              // build that happens to print the opening sentence is still compared.
              Expect.contains
                  (Trace.normaliseOutput [ "Warning: A secret was passed to my auditor today" ])
                  "Warning: A secret was passed to my auditor today"
                  "an unaccompanied head is still user output"

              // And the RETIRED one-line wording is nobody's narration any more — a
              // build printing it survives, which is precisely what the old
              // unconditional shape-match got wrong.
              Expect.contains
                  (Trace.normaliseOutput [ "WARNING: a secret was interpolated into `sh` via a Groovy string: TOKEN" ])
                  "WARNING: a secret was interpolated into `sh` via a Groovy string: TOKEN"
                  "the retired wording is user output now"
          }

          test "`Terminated` survives unless an interrupt was just narrated" {
              Expect.contains (Trace.normaliseOutput [ "Terminated" ]) "Terminated" "on its own it is output"

              Expect.isEmpty
                  (Trace.normaliseOutput [ "Sending interrupt signal to process"; "Terminated" ])
                  "after an interrupt it is narration"
          }

          test "FG-102: the permitted narration prefixes are TIGHT" {
              // The standing rule (docs/REVIEW_CHECKLIST.md): nothing is dropped on
              // wording alone. The prefix-suppressed narration that remains is the
              // permitted third category — exact plugin sentences, receipt-cited —
              // and these rows prove a build printing NEARBY wording still compares.
              for line in
                  [ "Timeout reached for my own watchdog"
                    "Masking tape applied to the fixture"
                    "Cancelling my own retry loop"
                    "ERROR-adjacent text without the colon prefix" ] do
                  Expect.contains (Trace.normaliseOutput [ line ]) line $"'{line}' is user output"
          } ]

/// FG-100 acceptance. ONE table for the string model, so adding a consumer means adding a
/// row rather than rediscovering the rules — which is what 52 review findings were.
///
/// Every row is a behaviour a receipt proved, restated where it can be run in
/// milliseconds instead of a Jenkins round trip.
let stringModel =
    let env = Map.ofList [ "TARGET", "production"; "N", "2" ]

    let step named positional literalNamed literalPos exprArgs sources =
        { Name = "probe"
          Positional = positional
          Named = named
          // positional-first mirrors the helper's call shape; the parser records
          // the TRUE source order for real pipelines
          ArgumentOrder =
            (positional |> List.mapi (fun i _ -> $"#{i}"))
            @ (named |> List.map fst)
          Block = []
          LiteralNamedArgs = Set.ofList literalNamed
          LiteralPositionalArgs = Set.ofList literalPos
          ExpressionArgs = Set.ofList exprArgs
          InterpolationSource = sources
          RawArgs = ""
          Position = Position.zero }

    testList
        "FG-100 the string model, one table"
        [ test "kind is decided in ONE place, for named and positional alike" {
              let named = step [ "m", "x" ] [] [ "m" ] [] [] []
              Expect.equal (GString.kindOf named "m") Literal "single-quoted named"

              let expr = step [ "m", "env.TARGET" ] [] [] [] [ "m" ] []
              Expect.equal (GString.kindOf expr "m") Expression "unquoted named is CODE"

              let gs = step [ "m", "hi ${TARGET}" ] [] [] [] [] []
              Expect.equal (GString.kindOf gs "m") Interpolating "double-quoted named"

              let pos = step [] [ "Deploy ${TARGET}?" ] [] [ 0 ] [] []
              Expect.equal (GString.kindOf pos "#0") Literal "single-quoted POSITIONAL — the
                                                              form that was interpolated for
                                                              a whole round"
          }

          test "the render matrix" {
              // description, step, key, raw, expected
              let cases =
                  [ "literal keeps its braces",
                      step [] [ "Deploy ${TARGET}?" ] [] [ 0 ] [] [], "#0", "Deploy ${TARGET}?", "Deploy ${TARGET}?"
                    "GString expands a name",
                      step [ "m", "Deploy ${TARGET}?" ] [] [] [] [] [], "m", "Deploy ${TARGET}?", "Deploy production?"
                    "GString expands a dotted env path",
                      step [ "m", "Deploy ${env.TARGET}?" ] [] [] [] [] [], "m", "Deploy ${env.TARGET}?", "Deploy production?"
                    "an unquoted argument is CODE, not text",
                      step [ "m", "env.TARGET" ] [] [] [] [ "m" ] [], "m", "env.TARGET", "production"
                    "a real Groovy expression is evaluated",
                      step [ "m", "half is ${10 / 2}" ] [] [] [] [] [], "m", "half is ${10 / 2}", "half is 5"
                    "division is not a slashy string",
                      step [ "m", "${N} and ${10 / 2}" ] [] [] [] [] [], "m", "${N} and ${10 / 2}", "2 and 5"
                    "a slashy literal's brace is content",
                      step [ "m", "brace ${/}/}" ] [] [] [] [] [], "m", "brace ${/}/}", "brace }"
                    "an escaped dollar stays literal",
                      step [ "m", "keep $TARGET" ] [] [] [] [] [ "m", "keep \u0000TARGET" ], "m", "keep $TARGET", "keep $TARGET"
                    "escaped and live dollars in ONE string",
                      step [ "m", "x" ] [] [] [] [] [ "m", "\u0000TARGET is ${TARGET}" ], "m", "x", "$TARGET is production"
                    "an assignment BINDS for the statements after it",
                      step [ "m", "${x = 'ok'; x}" ] [] [] [] [] [], "m", "${x = 'ok'; x}", "ok"
                    "a missing env path is the four letters null, not empty",
                      step [ "m", "v=${env.NOPE}" ] [] [] [] [] [], "m", "v=${env.NOPE}", "v=null"
                    "a comment's brace is text, not the placeholder close",
                      step [ "m", "c=${1 /* } */ + 1}" ] [] [] [] [] [], "m", "c=${1 /* } */ + 1}", "c=2"
                    "a line comment ends at the NEWLINE, not the text",
                      step [ "m", "n=${1 // note\n + 1}" ] [] [] [] [] [], "m", "n=${1 // note\n + 1}", "n=2" ]

              for (why, st, key, raw, expected) in cases do
                  Expect.equal (GString.render env st key raw) expected why

              // An INHERITED process variable participates in expressions exactly as
              // it does in the simple-name fallback — measured: declarative resolves a
              // bare `${PATH}` from the agent environment and succeeds, so an
              // expression on the same name must not raise.
              Environment.SetEnvironmentVariable("FOGELL_OSVAR_PROBE", "osval")

              Expect.equal
                  (GString.render env (step [ "m", "${FOGELL_OSVAR_PROBE.toUpperCase()}" ] [] [] [] [] []) "m" "${FOGELL_OSVAR_PROBE.toUpperCase()}")
                  "OSVAL"
                  "inherited environment reaches expression evaluation"

              // LAZY branches do not raise: Groovy never evaluates the untaken arm,
              // so a missing name there must not fail a build Jenkins accepts.
              // Enforcement is at READ time in the interpreter — no static scan can
              // know which arm runs — so the TAKEN arm raises while the untaken one
              // never does.
              for why, raw, expected in
                  [ "untaken ternary arm may be missing", "${true ? 'ok' : NOPE}", "ok"
                    "short-circuited operand may be missing", "${false && NOPE}", "false" ] do
                  Expect.equal (GString.render env (step [ "m", raw ] [] [] [] [] []) "m" raw) expected why

              Expect.throws
                  (fun () ->
                      GString.render env (step [ "m", "${true ? NOPE : 'ok'}" ] [] [] [] [] []) "m" "${true ? NOPE : 'ok'}"
                      |> ignore)
                  "the TAKEN arm reading a missing name raises"

              // Dotted chains are EXPRESSIONS, not flattened lookups. The sandbox
              // rejects the property form on a String and accepts the method form;
              // receipts gstring-string-property-fails, gstring-shared-binding.
              Expect.throws
                  (fun () ->
                      GString.render env (step [ "m", "p=${env.TARGET.length}" ] [] [] [] [] []) "m" "p=${env.TARGET.length}"
                      |> ignore)
                  "property access on a String raises"

              Expect.equal
                  (GString.render env (step [ "m", "m=${env.TARGET.length()}" ] [] [] [] [] []) "m" "m=${env.TARGET.length()}")
                  "m=10"
                  "the method form returns the count"

              // A method the bounded interpreter does NOT model refuses by name —
              // stringifying an invented null ran `deploy null` with the build green.
              Expect.throws
                  (fun () ->
                      GString.render env (step [ "m", "s=${env.TARGET.substring(3)}" ] [] [] [] [] []) "m" "s=${env.TARGET.substring(3)}"
                      |> ignore)
                  "an unmodelled method fails closed"

              // Placeholders in one GString share the script Binding, as VALUES —
              // `n` must reach the second placeholder as the number 2, or the
              // arithmetic silently becomes string concatenation.
              Expect.equal
                  (GString.render env (step [ "m", "${x = 'ok'; x}-${x}" ] [] [] [] [] []) "m" "${x = 'ok'; x}-${x}")
                  "ok-ok"
                  "an assignment is visible to the next placeholder"

              Expect.equal
                  (GString.render env (step [ "m", "${n = 2; n}+${n * 3}" ] [] [] [] [] []) "m" "${n = 2; n}+${n * 3}")
                  "2+6"
                  "carried bindings keep their TYPE across placeholders"

              // ...and across STEPS, through the build-scoped ScriptBinding — while
              // plain `render` stays stateless, so a fresh build starts clean.
              // Receipt `gstring-binding-across-steps`.
              let binding = GString.ScriptBinding()
              let assignStep = step [ "m", "${y = 'kept'; y}" ] [] [] [] [] []
              let readStep = step [ "m", "later:$y" ] [] [] [] [] []

              Expect.equal (GString.renderWith binding env assignStep "m" "${y = 'kept'; y}") "kept" "assignment renders"
              Expect.equal (GString.renderWith binding env readStep "m" "later:$y") "later:kept" "a later step reads it"

              Expect.throws
                  (fun () -> GString.render env readStep "m" "later:$y" |> ignore)
                  "a fresh build does not inherit the previous Binding"

              // A placeholder's side effects run ONCE per evaluation — the walker
              // must not render the same named script argument twice. The
              // increment-twice defect is observable right here: two renders of the
              // assignment leave n at 3 where Jenkins leaves 2.
              let b2 = GString.ScriptBinding()
              let inc = step [ "m", "${q = 1; q}" ] [] [] [] [] []
              GString.renderWith b2 env inc "m" "${q = 1; q}" |> ignore

              let incAgain = step [ "m", "${q = q + 1; q}" ] [] [] [] [] []

              Expect.equal
                  (GString.renderWith b2 env incAgain "m" "${q = q + 1; q}")
                  "2"
                  "one evaluation, one increment"

              // A fault in a LATER placeholder does not roll back an earlier
              // placeholder's assignment — Groovy already performed it, and a post
              // block reading it must succeed.
              let b3 = GString.ScriptBinding()
              let faulting = step [ "m", "${w = 'kept'; w}-${NOPE}" ] [] [] [] [] []

              Expect.throws
                  (fun () -> GString.renderWith b3 env faulting "m" "${w = 'kept'; w}-${NOPE}" |> ignore)
                  "the missing name still fails the argument"

              let readsW = step [ "m", "after:$w" ] [] [] [] [] []

              Expect.equal
                  (GString.renderWith b3 env readsW "m" "after:$w")
                  "after:kept"
                  "the assignment made before the fault survives it"

              // Safe navigation: a null receiver short-circuits to null with no
              // lookup — `${env.OPTIONAL?.value}` renders the text null and the
              // build runs, where the unsafe dot would fault.
              Expect.equal
                  (GString.render env (step [ "m", "s=${env.OPTIONAL?.value}" ] [] [] [] [] []) "m" "s=${env.OPTIONAL?.value}")
                  "s=null"
                  "safe navigation on an absent value is null, not a failure"

              // A `def` local SHADOWS a binding inside its own placeholder and never
              // merges back: the binding still reads 'outer' afterwards.
              let b4 = GString.ScriptBinding()
              let bindOuter = step [ "m", "${x = 'outer'; x}" ] [] [] [] [] []
              let shadow = step [ "m", "${def x = 'inner'; x}" ] [] [] [] [] []
              let readX = step [ "m", "read:$x" ] [] [] [] [] []

              Expect.equal (GString.renderWith b4 env bindOuter "m" "${x = 'outer'; x}") "outer" "binding set"
              Expect.equal (GString.renderWith b4 env shadow "m" "${def x = 'inner'; x}") "inner" "local shadows"
              Expect.equal (GString.renderWith b4 env readX "m" "read:$x") "read:outer" "the binding survives the shadow"

              // ...but a closure's `def` does NOT undo an OUTER binding assignment
              // that happens in the same placeholder — locals are scoped to their
              // closure, not name-blacklisted for the whole expression.
              let b5 = GString.ScriptBinding()
              let seed = step [ "m", "${x = 'outer'; x}" ] [] [] [] [] []
              let mixed = step [ "m", "${x = 'new'; [1].each { def x = 'local' }; x}" ] [] [] [] [] []
              let readAfter = step [ "m", "got:$x" ] [] [] [] [] []

              GString.renderWith b5 env seed "m" "${x = 'outer'; x}" |> ignore
              Expect.equal (GString.renderWith b5 env mixed "m" "${x = 'new'; [1].each { def x = 'local' }; x}") "new" "outer assignment wins"
              Expect.equal (GString.renderWith b5 env readAfter "m" "got:$x") "got:new" "the closure local did not block the merge"

              // Every strict fault fails the render — a thrown Groovy exception must
              // not degrade into raw placeholder text on a green build.
              Expect.throws
                  (fun () -> GString.render env (step [ "m", "d=${1 / 0}" ] [] [] [] [] []) "m" "d=${1 / 0}" |> ignore)
                  "an expression that throws fails the argument"

              // ...including an operator on operand types the interpreter does not
              // model: Groovy throws for `1 - 'x'`; null is an invented value.
              Expect.throws
                  (fun () -> GString.render env (step [ "m", "e=${1 - 'x'}" ] [] [] [] [] []) "m" "e=${1 - 'x'}" |> ignore)
                  "an unmodelled operator combination fails the argument"

              // A closure ASSIGNMENT hits the shared script Binding, even though the
              // closure environment itself is discarded.
              let b6 = GString.ScriptBinding()

              Expect.equal
                  (GString.renderWith b6 env (step [ "m", "${[1].each { z = 'kept' }; 'done'}" ] [] [] [] [] []) "m" "${[1].each { z = 'kept' }; 'done'}")
                  "done"
                  "the closure runs"

              Expect.equal
                  (GString.renderWith b6 env (step [ "m", "z:$z" ] [] [] [] [] []) "m" "z:$z")
                  "z:kept"
                  "the closure's binding assignment persists"

              // ...and SUCCESSIVE closures accumulate — each derives from the outer
              // environment, so a per-scope snapshot kept only the LAST closure's
              // assignment. Updates merge individually.
              let b7 = GString.ScriptBinding()

              GString.renderWith
                  b7 env
                  (step [ "m", "${[1].each { p = 'p1' }; [1].each { q = 'q1' }; 'done'}" ] [] [] [] [] [])
                  "m" "${[1].each { p = 'p1' }; [1].each { q = 'q1' }; 'done'}"
              |> ignore

              Expect.equal
                  (GString.renderWith b7 env (step [ "m", "$p-$q" ] [] [] [] [] []) "m" "$p-$q")
                  "p1-q1"
                  "both closures' assignments survive"

              // Safe navigation covers CALLS, not just properties: a null receiver
              // short-circuits the whole call — arguments included — while a present
              // receiver dispatches the method.
              Expect.equal
                  (GString.render env (step [ "m", "sc=${env.OPTIONAL?.toUpperCase()}" ] [] [] [] [] []) "m" "sc=${env.OPTIONAL?.toUpperCase()}")
                  "sc=null"
                  "safe call on null is null"

              Expect.equal
                  (GString.render env (step [ "m", "sp=${env.TARGET?.toUpperCase()}" ] [] [] [] [] []) "m" "sp=${env.TARGET?.toUpperCase()}")
                  "sp=PRODUCTION"
                  "safe call on a value dispatches"

              // Strict rendering refuses, by name, everything it cannot faithfully
              // evaluate: an argument to a zero-argument method, valid Groovy the
              // bounded parser cannot parse, and a withEnv entry naming nothing.
              Expect.throws
                  (fun () -> GString.render env (step [ "m", "a=${'abc'?.length(1)}" ] [] [] [] [] []) "m" "a=${'abc'?.length(1)}" |> ignore)
                  "an argument to a zero-arg method refuses"

              Expect.throws
                  (fun () -> GString.render env (step [ "m", "c=${'out.txt' as String}" ] [] [] [] [] []) "m" "c=${'out.txt' as String}" |> ignore)
                  "unparsable-but-valid Groovy refuses rather than emitting raw text"

              Expect.throws
                  (fun () -> GString.interpolateInto (GString.ScriptBinding()) ignore env "T=$NOPE" |> ignore)
                  "a withEnv entry naming nothing fails like any GString"

              // A triple-quoted literal's interior apostrophe and brace are CONTENT.
              let tripleRaw = "t=${'''it's } fine'''}"

              Expect.equal
                  (GString.render env (step [ "m", tripleRaw ] [] [] [] [] []) "m" tripleRaw)
                  "t=it's } fine"
                  "triple-quoted content survives the boundary scan"

              // Division stays exact or refuses: Groovy's `/` is decimal, and this
              // interpreter has no decimal value — truncation is a wrong answer.
              Expect.throws
                  (fun () -> GString.render env (step [ "m", "f=${1 / 2}" ] [] [] [] [] []) "m" "f=${1 / 2}" |> ignore)
                  "a non-integral quotient refuses"

              Expect.throws
                  (fun () -> GString.render env (step [ "m", "n=${'nope'.toInteger()}" ] [] [] [] [] []) "m" "n=${'nope'.toInteger()}" |> ignore)
                  "a failed conversion is a fault, not null"

              Expect.throws
                  (fun () -> GString.render env (step [ "m", "g=${'abc'?.length(foo: 1)}" ] [] [] [] [] []) "m" "g=${'abc'?.length(foo: 1)}" |> ignore)
                  "a NAMED argument to a zero-arg method refuses too"

              Expect.throws
                  (fun () -> GString.render env (step [ "m", "h=${[1].any(123)}" ] [] [] [] [] []) "m" "h=${[1].any(123)}" |> ignore)
                  "a positional argument to a closure method refuses"

              Expect.equal
                  (GString.render env (step [ "m", "k=${try { NOPE } catch (Exception e) { 'fallback' }}" ] [] [] [] [] []) "m" "k=${try { NOPE } catch (Exception e) { 'fallback' }}")
                  "k=fallback"
                  "MissingPropertyException is catchable"

              Expect.equal
                  (GString.render env (step [ "m", "cv=${def cx = 'before'; try { cx = 'after'; NOPE } catch (Exception e) { cx }}" ] [] [] [] [] []) "m" "cv=${def cx = 'before'; try { cx = 'after'; NOPE } catch (Exception e) { cx }}")
                  "cv=after"
                  "the catch observes locals as of the throw point"

              // ...and the DECLARED type decides what a clause intercepts: an
              // ArithmeticException handler does not catch a missing property.
              Expect.throws
                  (fun () ->
                      GString.render env (step [ "m", "tt=${try { NOPE } catch (ArithmeticException e) { 'no' }}" ] [] [] [] [] []) "m" "tt=${try { NOPE } catch (ArithmeticException e) { 'no' }}"
                      |> ignore)
                  "an incompatible catch type lets the fault escape"

              Expect.throws
                  (fun () -> GString.render env (step [ "m", "u=${1" ] [] [] [] [] []) "m" "u=${1" |> ignore)
                  "an unterminated placeholder refuses"

              // Rows that RAISE rather than render: an unknown bare name is a failed
              // Groovy property lookup — in the fast path and INSIDE an expression
              // alike. Receipts `gstring-unresolved-property` and
              // `gstring-unresolved-in-expression` carry the Jenkins side.
              for why, raw in
                  [ "bare unknown name raises", "${NOPE}"
                    "unknown name inside an expression raises", "${NOPE + '-sfx'}" ] do
                  Expect.throws
                      (fun () -> GString.render env (step [ "m", raw ] [] [] [] [] []) "m" raw |> ignore)
                      why
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "Fogell.Differential" [ userOutputSurvives; stringModel ])
