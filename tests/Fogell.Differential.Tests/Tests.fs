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

          test "FG-102: the timeout narration COMPARES — both engines emit it" {
              // The standing rule''s preferred form: Fogell speaks Jenkins'' wording
              // (measured), the suppression is deleted, and the lines are ordinary
              // compared output. A build printing them, or anything near them, is
              // compared too — including the real prefixes the old suppression
              // dropped on wording alone.
              for line in
                  [ "Timeout set to expire in my dreams"
                    "Timeout set to expire in 3 sec"
                    "Timeout has been exceeded"
                    "Cancelling nested steps due to timeout"
                    "Sending interrupt signal to process"
                    "Terminated"
                    "Timeout reached for my own watchdog"
                    "Masking tape applied to the fixture"
                    "Cancelling my own retry loop"
                    "Running on my kite"
                    "Started by my alarm clock"
                    "Finished: painting the fence"
                    "Resuming build of the shed" ] do
                  Expect.contains (Trace.normaliseOutput [ line ]) line $"''{line}'' is compared"

              // `Sending interrupt` followed by `Terminated` is compared as a PAIR
              // now — the old context gate dropped the second line.
              Expect.equal
                  (Trace.normaliseOutput [ "Sending interrupt signal to process"; "Terminated" ])
                  [ "Sending interrupt signal to process"; "Terminated" ]
                  "the interrupt pair compares on both engines"

              // ...and the cluster still counts as the ABORT REASON for both sides.
              Expect.isTrue
                  (Trace.reportedFailureReason [ "Timeout has been exceeded" ])
                  "the exceeded line explains an aborted build"
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
          ScriptBody = None
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

              Expect.throws
                  (fun () ->
                      GString.render
                          env
                          (step [ "m", "n=${def target = null; target?.name = 'x'}" ] [] [] [] [] [])
                          "m"
                          "n=${def target = null; target?.name = 'x'}"
                      |> ignore)
                  "a null assignment receiver cannot degrade into raw placeholder success"

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

/// FG-102 round 48. dash prefixes only the FIRST physical line of a multiline
/// traced word; continuation rows are bare, so inherited-env differences there
/// are resolved two-sidedly at COMPARE time. These rows pin the rule's shape:
/// it collapses a pair differing only by same-name inherited values, and it
/// never manufactures a divergence from lines that were already equal.
/// FG-159. In CONCURRENT mode the fold list must record a row ONLY when the fold
/// actually DECIDED a comparison — i.e. the row pairs with one on the other side once
/// canonical. Three filters were needed to get there, each necessary and not
/// sufficient: `canon l <> l` credited every row CONTAINING an inherited value; the
/// multiset difference credited every row that DIFFERED; only pairing credits the rows
/// the fold resolved.
///
/// These live as unit tests because the failing shapes DIVERGE, and a diverging case
/// cannot sit in the differential suite — so the suite can never exercise them. Both
/// receipts that do exercise the concurrent path are PROVEN cases with matching counts,
/// which is exactly the blind spot that let two earlier versions of this filter ship.
/// FG-164. The seal binds the CASE SOURCE, not just its filename.
///
/// A seal over the name proves which FILE was compared, never WHAT was: editing a case
/// without renaming it left the old receipt valid — same expected name, same PROVEN
/// verdict — and the scorecard published the suite as fully proven with the changed case
/// never re-run. The generator's mtime check was a stated smoke alarm for this; these
/// tests are what let it be replaced by evidence rather than a timestamp.
let sealBindsCaseSource =
    let mkTrace output =
        { Result = "success"
          Output = output
          WorkspaceHash = "abc123"
          WorkspaceFiles = []
          Timestamps = (0, 0)
          Concurrent = false
          EngineNotes = []
          ReportedFailureReason = false }

    let sealOf (source: string) =
        (Compare.receipt "case.Jenkinsfile" (System.Text.Encoding.UTF8.GetBytes source) "2.568.1" []
             (Result.Ok(mkTrace [ "+ echo hi"; "hi" ]))
             (Result.Ok(mkTrace [ "+ echo hi"; "hi" ]))).Seal

    testList
        "FG-164 the receipt seal binds the case source"
        [ test "editing the case changes the seal" {
              let a = sealOf "pipeline { agent any; stages { stage('a') { steps { sh 'echo hi' } } } }"
              let b = sealOf "pipeline { agent any; stages { stage('b') { steps { sh 'echo hi' } } } }"

              Expect.notEqual a b "a changed case must not keep its old proof"
          }

          test "an unchanged case seals identically" {
              // The seal must be a pure function of the evidence — otherwise a re-run
              // would churn every receipt and the drift signal would be worthless.
              let src = "pipeline { agent any; stages { stage('a') { steps { sh 'echo hi' } } } }"
              Expect.equal (sealOf src) (sealOf src) "identical input, identical seal"
          }

          test "a BOM changes the digest though the decoded text is identical" {
              // File.ReadAllText strips a BOM and decodes UTF-16 to the same characters, so
              // a digest over the DECODED string let a re-encoded case keep its old proof —
              // while the claim beside it said "any byte". The digest is over raw bytes.
              let text = "pipeline { agent any }"
              let plain = System.Text.Encoding.UTF8.GetBytes text
              let withBom = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] plain

              Expect.notEqual
                  (Compare.caseDigest plain)
                  (Compare.caseDigest withBom)
                  "a BOM is a byte of the file, and can change how a shell reads a heredoc"
          }

          test "the same bytes digest identically" {
              let bytes = System.Text.Encoding.UTF8.GetBytes "pipeline { agent any }"
              Expect.equal (Compare.caseDigest bytes) (Compare.caseDigest bytes) "pure function of the bytes"
          }

          test "whitespace in the case still changes the seal" {
              // Nothing about a case is exempt: a reformat is a change the proof did not
              // cover, and deciding which edits are 'harmless' is how a seal starts lying.
              let a = sealOf "pipeline { agent any }"
              let b = sealOf "pipeline {  agent any }"

              Expect.notEqual a b "any byte of the case is bound"
          }
        ]

let concurrentSealIsOrderStable =
    // FG-167. MEASURED while resealing for FG-164: on an unchanged tree, 2 of 118 receipts
    // resealed differently — `parallel-post-selection` and `parallel-siblings-finish` —
    // and the only content change was a line ORDER change (`+ sleep 6` moving). The seal
    // bound each engine's literal output, and a concurrent case's branch interleaving is
    // decided by OS scheduling.
    //
    // A seal that cannot tell a re-run from an edit is not detecting tampering. It also
    // BLOCKED FG-161: a seal verifier would have flagged those two as tampered on every
    // single run, and a check that cries wolf twice per run is worse than no check.
    let mkTrace concurrent output =
        { Result = "success"
          Output = output
          WorkspaceHash = "ws-hash"
          WorkspaceFiles = []
          Timestamps = (0, 0)
          Concurrent = concurrent
          EngineNotes = []
          ReportedFailureReason = false }

    let sealOf jConcurrent fConcurrent jOut fOut =
        (Compare.receipt "parallel.Jenkinsfile" (Text.Encoding.UTF8.GetBytes "pipeline { }") "2.568.1" []
             (Result.Ok(mkTrace jConcurrent jOut))
             (Result.Ok(mkTrace fConcurrent fOut))).Seal

    // One interleaving of a two-branch case, and the same lines as another run scheduled
    // them. Same multiset, different sequence — nothing about the comparison changed.
    let runA = [ "+ echo alpha"; "alpha"; "+ sleep 6"; "+ echo beta"; "beta" ]
    let runB = [ "+ echo alpha"; "+ sleep 6"; "alpha"; "+ echo beta"; "beta" ]

    testList
        "FG-167 a concurrent seal is stable across interleavings"
        [ test "the same lines in a different order seal identically" {
              Expect.equal
                  (sealOf true true runA runA)
                  (sealOf true true runB runB)
                  "a re-run that only reordered concurrent output is not an edit"
          }

          test "one side reporting concurrent is enough to sort BOTH" {
              // `compareOutput` enters multiset mode on the DISJUNCTION, so a per-trace
              // test would seal one side sorted and the other literal — and the seal would
              // still churn on whichever side was left alone.
              Expect.equal
                  (sealOf true false runA runA)
                  (sealOf true false runB runB)
                  "multiset mode is decided the same way for the seal as for the comparison"

              Expect.equal
                  (sealOf false true runA runA)
                  (sealOf false true runB runB)
                  "and it does not matter which side reported it"
          }

          test "changed CONTENT still breaks a concurrent seal" {
              // The whole risk of sorting is that it erases evidence. It must drop ORDER
              // and nothing else.
              let edited = runA |> List.map (fun l -> if l = "beta" then "BETA" else l)

              Expect.notEqual
                  (sealOf true true runA runA)
                  (sealOf true true edited edited)
                  "sorting must drop order, not content"
          }

          test "changed MULTIPLICITY still breaks a concurrent seal" {
              // A sorted-and-DEDUPLICATED seal would pass the order test and quietly stop
              // binding how many times a line was printed — which the comparison DOES
              // compare, as a multiset.
              //
              // THE TOTAL LINE COUNT IS HELD FIXED, and that is the whole point of these
              // two inputs. The first version of this test appended a line, and it passed
              // against a deduplicating seal — not because the output render bound
              // multiplicity, but because the multiset DISCLOSURE embeds "N jenkins / N
              // fogell lines" and the seal binds the folds. The test was reading a number
              // from a sentence next to the thing it meant to check. Caught by running the
              // dedupe mutation, which is what the mutation pass is for.
              //
              // Same length, same SET of lines, different multiplicity — so only a seal
              // that binds the multiset can tell these apart.
              let twoAlpha = [ "alpha"; "alpha"; "beta" ]
              let twoBeta = [ "alpha"; "beta"; "beta" ]

              Expect.notEqual
                  (sealOf true true twoAlpha twoAlpha)
                  (sealOf true true twoBeta twoBeta)
                  "multiplicity is compared, so it must stay sealed"
          }

          test "a DROPPED line still breaks a concurrent seal" {
              // Weaker than it looks, and said so: dropping a line also changes the line
              // count in the multiset disclosure, which the seal binds — so this passes
              // even against a seal that renders no output at all. It is here as a
              // property of the whole receipt, not as evidence about the render; the
              // multiplicity test above is what isolates that.
              let missing = runA |> List.filter (fun l -> l <> "+ sleep 6")

              Expect.notEqual
                  (sealOf true true runA runA)
                  (sealOf true true missing missing)
                  "a line removed from a concurrent receipt must break its seal"
          }

          test "a NON-concurrent case still binds its output ORDER" {
              // The relaxation is scoped to cases whose order was not compared. Sorting
              // unconditionally would silently stop sealing sequence for every ordinary
              // case, where order IS the comparison.
              Expect.notEqual
                  (sealOf false false runA runA)
                  (sealOf false false runB runB)
                  "an ordered case's order is compared, so it stays sealed"
          }

          test "the parallel contract DISCLOSES that order is not sealed" {
              // An unsealed region of a sealed document that nobody names is how a seal
              // starts being trusted for more than it covers.
              let r =
                  Compare.receipt "parallel.Jenkinsfile" (Text.Encoding.UTF8.GetBytes "pipeline { }") "2.568.1" []
                      (Result.Ok(mkTrace true runA))
                      (Result.Ok(mkTrace true runA))

              let contract = String.concat "\n" r.ComparisonContract

              Expect.stringContains contract "SEAL binds these output lines SORTED" "the relaxation is stated"
              Expect.stringContains contract "NOT sealed" "and what it costs is stated"

              let ordered =
                  Compare.receipt "ordered.Jenkinsfile" (Text.Encoding.UTF8.GetBytes "pipeline { }") "2.568.1" []
                      (Result.Ok(mkTrace false runA))
                      (Result.Ok(mkTrace false runA))

              Expect.isFalse
                  ((String.concat "\n" ordered.ComparisonContract).Contains "SEAL binds these output lines SORTED")
                  "an ordered case must not claim a relaxation it did not take"
          }

          test "the FOLD DISCLOSURE is order-stable too" {
              // Sealing sorted output is not sufficient on its own: the folded-pair list is
              // built by walking the multiset difference in OUTPUT order, so two runs that
              // folded exactly the same pairs listed them in different sequences — and the
              // seal binds the folds. Both sources of churn had to go, and a fix that
              // closed only the obvious one would still have blocked FG-161.
              let env = [ "/home/srikanth", "${HOME}"; "/opt/tools", "${TOOLS}" ]

              let jA = [ "+ echo /home/srikanth"; "+ echo /opt/tools" ]
              let jB = [ "+ echo /opt/tools"; "+ echo /home/srikanth" ]
              let fA = [ "+ echo ${HOME}"; "+ echo ${TOOLS}" ]
              let fB = [ "+ echo ${TOOLS}"; "+ echo ${HOME}" ]

              let receiptOf jOut fOut =
                  Compare.receipt "parallel.Jenkinsfile" (Text.Encoding.UTF8.GetBytes "pipeline { }") "2.568.1" env
                      (Result.Ok(mkTrace true jOut))
                      (Result.Ok(mkTrace true fOut))

              let a, b = receiptOf jA fA, receiptOf jB fB

              // The precondition: if nothing folded, this test proves nothing at all.
              Expect.isNonEmpty a.OutputComparisonNotes "the case must actually record folds"

              Expect.isTrue
                  (a.OutputComparisonNotes |> List.exists (fun n -> n.Contains "compared canonically"))
                  "and they must be FOLD entries, not the multiset disclosure alone"

              Expect.equal a.OutputComparisonNotes b.OutputComparisonNotes "the same folds, listed in the same order"
              Expect.equal a.Seal b.Seal "so the seal does not move either"
          }
        ]

let silenceIsPerEngine =
    // FG-170. `DiagnosticSilence` was raised inside `if jenkins.Result = "failure" ||
    // "aborted"`, and that guard wrapped BOTH engines' checks — so a silent FOGELL failure
    // against a Jenkins SUCCESS could never be named, which is the shape most likely to be
    // a Fogell defect. Measured with a `script { sh ... }` probe: fogell=failure, zero
    // output, no DiagnosticSilence in the receipt.
    let trace result reported output =
        { Result = result
          Output = output
          WorkspaceHash = "ws"
          WorkspaceFiles = []
          Timestamps = (0, 0)
          Concurrent = false
          EngineNotes = []
          ReportedFailureReason = reported }

    let silences j f =
        let verdict, _ = Compare.traces [] j f

        match verdict with
        | Diverged ds ->
            ds
            |> List.choose (fun d ->
                match d with
                | DiagnosticSilence e -> Some e
                | _ -> None)
        | _ -> []

    testList
        "FG-170 diagnostic silence is judged per engine"
        [ test "a silent FOGELL failure is named even when Jenkins SUCCEEDED" {
              // The regression case, and the one the old guard could not reach.
              let names = silences (trace "success" true [ "+ echo hi"; "hi" ]) (trace "failure" false [])
              Expect.contains names "fogell" "an engine that failed without a reason must be named"
          }

          test "a silent JENKINS failure is named even when Fogell SUCCEEDED" {
              // The mirror. Written because the fix could have been applied to one arm and
              // left the other keyed on the wrong engine — the exact habit this session
              // kept catching.
              let names = silences (trace "failure" false []) (trace "success" true [ "+ echo hi"; "hi" ])
              Expect.contains names "jenkins" "the rule is symmetric or it is not a rule"
          }

          test "an engine that SUCCEEDED is never asked to explain itself" {
              // Judging on the other engine's result is precisely the defect; judging a
              // successful engine at all would be its twin.
              let names = silences (trace "success" false []) (trace "failure" false [])
              Expect.contains names "fogell" "the failing engine is named"
              Expect.isFalse (List.contains "jenkins" names) "a successful engine has nothing to explain"
          }

          test "a failure WITH a reported reason is not flagged" {
              let names = silences (trace "success" true [ "+ echo hi"; "hi" ]) (trace "failure" true [ "ERROR: boom" ])
              Expect.isEmpty names "a diagnosed failure is not silence"
          }

          test "UNSTABLE is still excluded, on both sides" {
              // Jenkins marks a build unstable from a test report and prints no ERROR line;
              // requiring one fired symmetrically and was the signature of a wrong rule.
              // Making the check per-engine must not quietly widen it.
              let names = silences (trace "unstable" false []) (trace "unstable" false [])
              Expect.isEmpty names "unstable is explained by the test report, not an ERROR line"
          }
        ]

let caseSnapshotIsOneRead =
    // FG-168. `readCaseSnapshot` replaced a `File.ReadAllText` + `File.ReadAllBytes` pair
    // in the differential CLI so the sealed bytes and the executed text are one snapshot.
    //
    // STATED PLAINLY, so a green suite is not misread: these tests do NOT prove the
    // atomicity. The race needs a file edited between two reads, and there is no seam to
    // drive that from a unit test — the single read is structural, visible in the source,
    // not held by a checker. What IS held is the thing that could plausibly break while
    // making that structural change: the decoding must stay byte-for-byte what
    // `File.ReadAllText` produced, or the swap silently changes what both engines execute.
    let withCaseFile (bytes: byte[]) (f: string -> unit) =
        let path =
            IO.Path.Combine(IO.Path.GetTempPath(), $"fogell-snapshot-{Guid.NewGuid():N}.Jenkinsfile")

        IO.File.WriteAllBytes(path, bytes)

        try
            f path
        finally
            IO.File.Delete path

    let text = "pipeline { agent any\n  // é ✓ non-ascii, so an encoding slip is visible\n}\n"

    testList
        "FG-168 the case snapshot is one read"
        [ test "plain UTF-8 decodes exactly as File.ReadAllText" {
              withCaseFile (Text.Encoding.UTF8.GetBytes text) (fun path ->
                  let _, decoded = Compare.readCaseSnapshot path
                  Expect.equal decoded (IO.File.ReadAllText path) "the engines must see the same script as before")
          }

          test "a UTF-8 BOM is stripped, as File.ReadAllText strips it" {
              // A naive `Encoding.UTF8.GetString bytes` leaves U+FEFF at the head of the
              // script, and the parser then fails on a case that used to run.
              let bom = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (Text.Encoding.UTF8.GetBytes text)

              withCaseFile bom (fun path ->
                  let _, decoded = Compare.readCaseSnapshot path
                  Expect.equal decoded (IO.File.ReadAllText path) "a BOM must not reach the parser"
                  Expect.isFalse (decoded.StartsWith "﻿") "and must not survive as a character")
          }

          test "a UTF-16 case decodes by its BOM, not as UTF-8" {
              // The encoding a case is SAVED in is not the harness's business; decoding it
              // as UTF-8 regardless would turn every second byte into a NUL.
              let utf16 = Text.Encoding.Unicode.GetPreamble()

              withCaseFile (Array.append utf16 (Text.Encoding.Unicode.GetBytes text)) (fun path ->
                  let _, decoded = Compare.readCaseSnapshot path
                  Expect.equal decoded (IO.File.ReadAllText path) "byte-order marks are honoured"
                  Expect.equal decoded text "and the script survives the round trip")
          }

          test "the bytes are the file's own, not a re-encoding of the text" {
              // Returning `Encoding.UTF8.GetBytes decoded` would look right and quietly undo
              // FG-164: the seal would bind a normalised re-encoding, so a case re-saved as
              // UTF-16 would keep its old proof again.
              let bom = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (Text.Encoding.UTF8.GetBytes text)

              withCaseFile bom (fun path ->
                  let bytes, _ = Compare.readCaseSnapshot path

                  Expect.equal
                      (Compare.caseDigest bytes)
                      (Compare.caseDigest (IO.File.ReadAllBytes path))
                      "the sealed bytes are the file's raw bytes")
          }
        ]

let concurrentFoldAccounting =
    let mkTrace output =
        { Result = "success"
          Output = output
          WorkspaceHash = "not-collected"
          WorkspaceFiles = []
          Timestamps = (0, 0)
          Concurrent = true
          EngineNotes = []
          ReportedFailureReason = false }

    let repl = [ "/var/jenkins_home", "${HOME}"; "/root", "${HOME}" ]

    let foldNotes (notes: string list) =
        notes |> List.filter (fun n -> n.StartsWith "compared canonically")

    testList
        "FG-159 concurrent folds are recorded only when they decided a comparison"
        [ test "the UNMATCHED row is not recorded while the matched pair still is" {
              // Two Jenkins inherited-value rows against one Fogell row. One pair
              // resolves canonically; the leftover matches nothing and the case diverges.
              // Disclosing the pair is honest; crediting the leftover is not.
              //
              // MY FIRST VERSION OF THIS TEST asserted ZERO notes and failed — I wrote the
              // expectation I wanted (nothing recorded on a diverging case) instead of the
              // property (only unmatched rows are excluded). The test was wrong, not the
              // filter, which is worth keeping visible: I have written the conclusion
              // rather than the operation several times on this branch.
              let jenkins = mkTrace [ "+ echo a"; "/var/jenkins_home"; "/var/jenkins_home" ]
              let fogell = mkTrace [ "+ echo a"; "/root" ]
              let verdict, notes = Compare.traces repl jenkins fogell

              Expect.notEqual verdict Proven "the leftover row must still diverge"

              Expect.equal
                  (List.length (foldNotes notes))
                  2
                  "exactly the one resolved pair — never 4, which would credit the leftover"
          }

          test "unequal counts record only the rows that paired" {
              // Two Jenkins rows against one Fogell row: one pair resolves, one is left
              // over. Only the pair may be recorded.
              let jenkins = mkTrace [ "/var/jenkins_home"; "/var/jenkins_home" ]
              let fogell = mkTrace [ "/root" ]
              let _, notes = Compare.traces repl jenkins fogell

              Expect.equal
                  (List.length (foldNotes notes))
                  2
                  "one pair = one note per side, never the unmatched leftover"
          }

          test "a pair the fold resolves is recorded even when JENKINS is already canonical" {
              // The asymmetric shape: Jenkins prints the canonical token literally and
              // Fogell prints the raw inherited value. The fold decides the comparison,
              // so the receipt must say so — the filter that required the JENKINS row to
              // change under `canon` reported 0 decided while relying on the relaxation.
              let jenkins = mkTrace [ "${HOME}" ]
              let fogell = mkTrace [ "/home/srikanth" ]
              let verdict, notes = Compare.traces [ "/home/srikanth", "${HOME}" ] jenkins fogell

              Expect.equal verdict Proven "the pair resolves canonically"

              Expect.equal
                  (List.length (foldNotes notes))
                  2
                  "the decided pair is disclosed even though the jenkins row did not change"
          }

          test "rows both engines printed identically are never recorded" {
              // Byte-equal on both sides: canonicalisation rewrites them, but they would
              // have compared equal anyway, so the fold decided nothing.
              let jenkins = mkTrace [ "/var/jenkins_home"; "+ echo x" ]
              let fogell = mkTrace [ "/var/jenkins_home"; "+ echo x" ]
              let verdict, notes = Compare.traces repl jenkins fogell

              Expect.equal verdict Proven "identical output compares equal"
              Expect.equal (foldNotes notes) [] "a byte-equal row needs no fold"
          }

          test "a genuinely differing pair IS recorded, once per side" {
              let jenkins = mkTrace [ "/var/jenkins_home" ]
              let fogell = mkTrace [ "/root" ]
              let verdict, notes = Compare.traces repl jenkins fogell

              Expect.equal verdict Proven "the pair resolves canonically"

              Expect.equal
                  (List.length (foldNotes notes))
                  2
                  "the decided pair is disclosed on both sides"
          }
        ]

let continuationResolution =
    let mkTrace output =
        { Result = "success"
          Output = output
          WorkspaceHash = "not-collected"
          WorkspaceFiles = []
          Timestamps = (0, 0)
          Concurrent = false
          EngineNotes = []
          ReportedFailureReason = false }

    let repl =
        [ "/var/jenkins_home", "${HOME}"; "/root", "${HOME}" ]

    testList
        "FG-102 xtrace continuation rows resolve two-sidedly"
        [ test "a bare continuation row differing only by inherited HOME compares equal" {
              let jenkins = mkTrace [ "+ printf %s head"; "/var/jenkins_home/tail" ]
              let fogell = mkTrace [ "+ printf %s head"; "/root/tail" ]
              let verdict, folds = Compare.traces repl jenkins fogell
              Expect.equal verdict Proven "continuation resolves"
              Expect.equal folds [ "line 1 compared canonically: ${HOME}/tail" ] "the fold is reported"
          }

          test "a multi-line continuation chain resolves on every row" {
              let jenkins = mkTrace [ "+ printf %s top"; "middle"; "/var/jenkins_home" ]
              let fogell = mkTrace [ "+ printf %s top"; "middle"; "/root" ]
              let verdict, folds = Compare.traces repl jenkins fogell
              Expect.equal verdict Proven "chain resolves"
              Expect.equal (List.length folds) 1 "one folded pair reported"
          }

          test "a pair that differs beyond the inherited value still diverges" {
              let jenkins = mkTrace [ "+ printf %s head"; "/var/jenkins_home/tail-a" ]
              let fogell = mkTrace [ "+ printf %s head"; "/root/tail-b" ]

              match Compare.traces repl jenkins fogell with
              | Diverged [ OutputDiffers(1, Some "/var/jenkins_home/tail-a", Some "/root/tail-b") ], [] -> ()
              | v -> failtest $"expected the literal pair reported, got {v}"
          }

          test "identical literal lines never diverge from the rule (literals cancel)" {
              let jenkins = mkTrace [ "+ echo x"; "/root is a literal both engines printed" ]
              let fogell = mkTrace [ "+ echo x"; "/root is a literal both engines printed" ]
              let verdict, folds = Compare.traces repl jenkins fogell
              Expect.equal verdict Proven "equal lines stay equal"
              Expect.isEmpty folds "byte-equal lines are never reported as folds"
          }

          test "an empty replacement list leaves the comparison byte-exact" {
              let jenkins = mkTrace [ "/var/jenkins_home" ]
              let fogell = mkTrace [ "/root" ]

              match Compare.traces [] jenkins fogell with
              | Diverged [ OutputDiffers(0, _, _) ], [] -> ()
              | v -> failtest $"expected divergence without replacements, got {v}"
          }

          test "the GITVERSION fold is scoped to the plugin line (FG-111 P1 look-alike)" {
              // The fold pair is the FULL narration line, never the raw version
              // string: a build running `sh 'git --version'` prints each
              // engine's version as ORDINARY stdout, and that difference is one
              // a lift-and-shift user genuinely sees — it must DIVERGE, while
              // the plugin's own narration line folds.
              let repl =
                  [ "> git --version # 'git version 2.47.3'", "> git --version # '${GITVERSION}'"
                    "> git --version # 'git version 2.43.0'", "> git --version # '${GITVERSION}'" ]

              let jenkins = mkTrace [ "> git --version # 'git version 2.47.3'"; "git version 2.47.3" ]
              let fogell = mkTrace [ "> git --version # 'git version 2.43.0'"; "git version 2.43.0" ]

              match Compare.traces repl jenkins fogell with
              | Diverged [ OutputDiffers(1, Some "git version 2.47.3", Some "git version 2.43.0") ], folds ->
                  Expect.equal
                      folds
                      [ "line 0 compared canonically: > git --version # '${GITVERSION}'" ]
                      "the narration line folded and was reported; the stdout line diverged"
              | v -> failtest $"raw version stdout must diverge while the plugin line folds, got {v}"
          }

          test "ordinary output printing an inherited value folds VISIBLY (round 48 P1)" {
              // The reviewer's own example: `sh 'printenv HOME'`. The trace rows are
              // byte-equal; the stdout pair differs only by the engines' inherited
              // HOMEs — the environment-of-necessity class ${WORKSPACE} already
              // occupies. The verdict is Proven AND the receipt says which pair the
              // rule decided: the relaxation is declared per case, never silent.
              let jenkins = mkTrace [ "+ printenv HOME"; "/var/jenkins_home" ]
              let fogell = mkTrace [ "+ printenv HOME"; "/root" ]
              let verdict, folds = Compare.traces repl jenkins fogell
              Expect.equal verdict Proven "inherited-value output folds"
              Expect.equal folds [ "line 1 compared canonically: ${HOME}" ] "and the receipt names the pair"
          } ]

/// FG-053. A timestamp-shaped prefix is engine decoration ONLY when the script
/// declared `options { timestamps() }`. Nothing in a line's shape says which it
/// is, so the strip is conditional — and the first version was not, which meant
/// a build printing `[2026-08-03T03:54:07.729Z] value` had it removed from every
/// case, and two DIFFERENT user instants compared equal.
///
/// Tested HERE rather than as a differential case on purpose: a case can only
/// plant a LITERAL line, which both engines print identically, so it passes with
/// the bug present or absent. Discriminating needs two different instants, which
/// is a divergence a passing case cannot express. One was written, observed to
/// prove nothing, and deleted.
let timestampPrefixIsConditional =
    let stamped = "[2026-08-03T03:54:07.729Z] value"

    testList
        "timestamp prefix is conditional"
        [ test "kept when the script did not declare timestamps()" {
              Expect.equal
                  (Trace.normaliseLineWhen false stamped)
                  (Some stamped)
                  "a build's own timestamp-shaped output must survive comparison verbatim"
          }

          test "stripped when the script declared timestamps()" {
              Expect.equal
                  (Trace.normaliseLineWhen true stamped)
                  (Some "value")
                  "the engine's prefix is excluded — two clocks never agree"
          }

          test "two different user instants stay different" {
              let a = "[2026-08-03T03:54:07.729Z] value"
              let b = "[2026-08-03T03:54:09.113Z] value"

              Expect.notEqual
                  (Trace.normaliseLineWhen false a)
                  (Trace.normaliseLineWhen false b)
                  "unconditional stripping collapsed these to one line, so a divergence read as PROVEN"
          }

          test "an indented timestamp-shaped line is never decoration" {
              let indented = "   [2026-08-03T03:54:07.729Z] value"
              let trimmed = indented.Trim()

              Expect.equal
                  (Trace.normaliseLineWhen true indented)
                  (Some trimmed)
                  "an engine's prefix is at column zero; a build's is not"
          } ]

/// FG-174. `WalkerRules.returnContract` is the ONE answer to "what does this call
/// return", read by the static refusal and by the runtime publisher. It exists because
/// deciding it at each site produced three separate review findings, so the rule is
/// pinned here rather than inferred from either caller.
let returnFlagContract =
    let contract = WalkerRules.returnContract

    testList
        "FG-174 the return-flag contract"
        [ test "no flags means genuine null" {
              Expect.equal (contract "sh" false false) WalkerRules.GenuineNull "a plain sh returns null"
          }

          test "returnStdout alone captures stdout" {
              Expect.equal (contract "sh" true false) WalkerRules.CapturedStdout "stdout"
          }

          test "returnStatus alone gives the exit status" {
              Expect.equal (contract "sh" false true) WalkerRules.ExitStatus "status"
          }

          test "returnStatus WINS when both are set" {
              // MEASURED on a disposable 2.568.1 container by the pre-push verifier, and
              // held since by receipt `script-sh-return-both`:
              // `sh(script: 'exit 7', returnStdout: true, returnStatus: true)` returns
              // Integer 7 and the build continues. Fogell returned the stdout, so a
              // following `if (code == 7)` compared String to Integer, took the other
              // arm, and skipped work Jenkins runs while reporting success. The flags
              // are not orthogonal, and a boolean per call site cannot say so.
              Expect.equal (contract "sh" true true) WalkerRules.ExitStatus "status takes precedence"
          }

          test "the flags belong to the shell steps only" {
              // `echo(message: 'hello', returnStdout: true)` returned "hello\n" where
              // Jenkins' echo returns null, so a `got == null` branch was skipped.
              Expect.equal (contract "echo" true true) WalkerRules.GenuineNull "echo stays null"

              for step in [ "dir"; "withEnv" ] do
                  Expect.equal
                      (contract step true true)
                      WalkerRules.BodyResult
                      $"{step} returns its body"

              Expect.equal
                  (contract "error" true true)
                  (WalkerRules.UnsupportedValue WalkerRules.OutsideHostedVocabulary)
                  "an outside step acquires no implicit return model"
          }

          // FG-174. THE VALUE'S TYPE, not its rendered text. `returnStatus: true` and
          // `returnStatus: 'true'` both render to "true"; Jenkins treats them
          // differently, and comparing `Trim().ToLowerInvariant()` got two shapes wrong.
          test "a literal boolean true turns the flag on" {
              Expect.equal (WalkerRules.returnFlag true "true") WalkerRules.FlagOn "bare true"
          }

          test "a literal boolean false turns it off, and is not an error" {
              Expect.equal (WalkerRules.returnFlag true "false") WalkerRules.FlagOff "bare false"
          }

          test "a non-boolean literal is REJECTED, never treated as absent" {
              // MEASURED on the pinned lab: `sh script: '…', returnStatus: 1` makes
              // Jenkins throw `IllegalArgumentException: Could not instantiate … for
              // ShellStep` BEFORE running anything, leaving the workspace empty. Fogell
              // ran the shell and reported success. Treating an unusable flag as "off"
              // is what made that a false success, so the state is explicit.
              // UNPROVEN by receipt: Jenkins answers with a Java stack trace no engine
              // can match, so the case diverges on output text — scratch probe only.
              match WalkerRules.returnFlag true "1" with
              | WalkerRules.FlagRejected why -> Expect.stringContains why "boolean" "says what it wanted"
              | other -> failtestf "expected a rejection, got %A" other
          }

          test "a QUOTED value is refused rather than coerced" {
              // Narrower than Jenkins on purpose, and declared: Jenkins would run this
              // through `Boolean.valueOf`, which makes `' true '` FALSE — measured. Every
              // one of the 134 uses in the 228-file corpus is the literal form, so this
              // costs nothing real and avoids importing Java coercion semantics.
              match WalkerRules.returnFlag false "true" with
              | WalkerRules.FlagRejected _ -> ()
              | other -> failtestf "expected a rejection, got %A" other
          }

          // FG-174. THE PAIRING THAT TURNS THE NEXT BYPASS INTO A FAILING TEST.
          //
          // `validateHostedCall` once had a schema-less catch-all, so a hosted wrapper
          // admitted WITHOUT a case is validated by nothing and accepts any shape.
          // `timeout` sat in the hosted set with no case and shipped a false success:
          // `script { timeout(1, 2) { … } }` ran its body and reported success where
          // Jenkins raises `IllegalArgumentException: Expected named arguments but got
          // [1, 2]`. That was the ninth finding of a class whose own comment already
          // predicted it — "every newly admitted hosted step would have got its own
          // signature bypass, one per arm, found one review round at a time".
          //
          // Admitting a wrapper without validating it now fails HERE instead.
          // FG-177 slice 1. Same pairing idea as the wrapper test below: a step admitted
          // to the vocabulary with no arity entry falls back to a default, and a default
          // is what let `deleteDir('ignored')` delete the workspace. Data beats a default
          // only if the data is complete.
          test "every step in the script vocabulary has an arity entry" {
              let missing =
                  WalkerRules.scriptStepVocabulary
                  |> Set.filter (fun s -> not (Map.containsKey s WalkerRules.positionalArity))

              Expect.isEmpty missing "a vocabulary step with no arity entry falls back to a guess"
          }

          // FG-177. THE TWO TABLES MUST AGREE. `positionalArity` says how many
          // positionals a step takes; `soleRequiredParameter` says what its one required
          // parameter is called. A step with arity 0 has no parameter to name, and a step
          // with arity 1 must have one or the named spelling stays unreachable — which is
          // exactly the false refusal `dir(path: 'sub')` was.
          test "the arity table and the parameter table agree" {
              for step in WalkerRules.scriptStepVocabulary do
                  let arity = Map.find step WalkerRules.positionalArity
                  let named = Map.containsKey step WalkerRules.soleRequiredParameter

                  Expect.equal named (arity = 1) $"{step}: arity {arity} and named-parameter presence must match"
          }

          test "deleteDir takes NO positional argument" {
              // Measured: Jenkins keeps the workspace and FAILS on `deleteDir('ignored')`,
              // where Fogell deleted it and continued. The one value a blanket
              // zero-or-one rule could not express.
              Expect.equal (Map.tryFind "deleteDir" WalkerRules.positionalArity) (Some 0) "empty constructor"
          }

          // "every hosted wrapper has a signature case" retired with the arms it
          // compared against — the FG-177 schema's row-coverage test holds the
          // same line against the table itself

          test "bat carries the same contract as sh" {
              // On CONTRACT, not on evidence: there is no Windows lane and no receipt
              // covers it. This pins the two ends AGREEING about bat, which is all it
              // claims — see WalkerRules for why that is not coverage.
              Expect.equal (contract "bat" true true) WalkerRules.ExitStatus "same durable-task options"
              Expect.equal (contract "bat" true false) WalkerRules.CapturedStdout "same durable-task options"
              Expect.equal (contract "bat" false false) WalkerRules.GenuineNull "plain durable-task null"
          } ]

/// FG-177. The schema table is load-bearing: every hosted wrapper must carry a
/// row, and the rows must hold the measured caveats the arms they replaced held.
let hostedSignatures =
    testList
        "FG-177 hosted signature schema"
        [ test "every hosted wrapper has a schema row" {
              // the was-missing-arm class (timeout sat unchecked for a whole
              // cycle) is a TEST now, not a review round
              for name in WalkerRules.scriptWrappersWithHostedBody do
                  Expect.isTrue
                      (Map.containsKey name WalkerRules.hostedSignatures)
                      $"hosted wrapper '{name}' has no signature row — any call shape would pass unchecked"
          }

          test "the rows hold the arms' measured rules" {
              let s name = Map.find name WalkerRules.hostedSignatures
              // retry: named count valid, non-int refused, zero NOT refused (clamps, measured)
              Expect.isNone ((s "retry").Check [] [ "count", Fogell.Groovy.Interpreter.VInt 2L ]) "named count is valid Jenkins"
              Expect.isNone ((s "retry").Check [ Fogell.Groovy.Interpreter.VInt 0L ] []) "retry(0) runs clamped — refusing was the measured false refusal"
              Expect.isSome ((s "retry").Check [ Fogell.Groovy.Interpreter.VStr "nope" ] []) "a non-integer count refuses"
              // withEnv: entry without '=' refused, well-formed passes
              Expect.isSome ((s "withEnv").Check [ Fogell.Groovy.Interpreter.VList(ref [ Fogell.Groovy.Interpreter.VStr "BADENTRY" ]) ] []) "an entry without = refuses"
              Expect.isNone ((s "withEnv").Check [ Fogell.Groovy.Interpreter.VList(ref [ Fogell.Groovy.Interpreter.VStr "A=1" ]) ] []) "NAME=VALUE passes"
              // dir: exactly one positional
              Expect.isSome ((s "dir").Check [] []) "dir with nothing refuses"
              // timeout: types deliberately unchecked
              Expect.isNone ((s "timeout").Check [ Fogell.Groovy.Interpreter.VStr "weird" ] []) "timeout's argument types are deliberately unchecked"
          } ]

let stepDescriptorValidation =
    let v text = Fogell.Groovy.Interpreter.VStr text

    testList
        "FG-177 shared step descriptor and call validator"
        [ test "the descriptor is exhaustive over the 14-step vocabulary" {
              Expect.equal
                  (WalkerRules.stepDescriptors |> Map.keys |> Set.ofSeq)
                  WalkerRules.scriptStepVocabulary
                  "one row, no schema-less fallback"
          }

          test "return shapes partition null, body, JUnit summary, and nominal SCM rows" {
              let genuineNull =
                  set [ "sh"; "echo"; "archiveArtifacts"; "deleteDir"; "stash"; "unstable"; "unstash" ]

              let bodyResult = set [ "dir"; "timeout"; "retry"; "withEnv" ]
              let junitSummary = set [ "junit" ]
              let scmMap = set [ "git"; "checkout" ]

              for KeyValue(name, descriptor) in WalkerRules.stepDescriptors do
                  if genuineNull.Contains name then
                      Expect.equal descriptor.ReturnValue WalkerRules.GenuineNull $"{name} returns measured null"
                      Expect.isTrue
                          (WalkerRules.returnValueIsModelled descriptor.ReturnValue)
                          $"{name} is safe in a value position"
                  elif bodyResult.Contains name then
                      Expect.equal descriptor.ReturnValue WalkerRules.BodyResult $"{name} returns its body"
                      Expect.isTrue
                          (WalkerRules.returnValueIsModelled descriptor.ReturnValue)
                          $"{name} body result is safe in a value position"
                  elif junitSummary.Contains name then
                      Expect.equal descriptor.ReturnValue WalkerRules.JUnitSummary "junit returns the closed count projection"
                      Expect.isTrue
                          (WalkerRules.returnValueIsModelled descriptor.ReturnValue)
                          "the measured JUnit count projection is safe in a value position"
                  elif scmMap.Contains name then
                      Expect.equal descriptor.ReturnValue WalkerRules.ScmMap $"{name} returns the nominal SCM map"
                      Expect.isTrue
                          (WalkerRules.returnValueIsModelled descriptor.ReturnValue)
                          $"{name} measured map projection is safe in a value position"
                  else
                      failtestf "unclassified return contract for %s" name

              Expect.equal
                  (Set.unionMany [ genuineNull; bodyResult; junitSummary; scmMap ])
                  WalkerRules.scriptStepVocabulary
                  "the classifications partition all 14 descriptors"
          }

          test "all 14 descriptor rows match the pinned Jenkins schemas" {
              // Jenkins 2.568.1 GDSL owns thirteen rows; archiveArtifacts is
              // pinned by the direct archive-schema measurement receipt.
              let expected =
                  Map.ofList
                      [ "sh", set [ "script"; "encoding"; "label"; "returnStatus"; "returnStdout" ]
                        "echo", set [ "message" ]
                        "archiveArtifacts",
                        set
                            [ "artifacts"; "allowEmptyArchive"; "caseSensitive"; "defaultExcludes"
                              "excludes"; "fingerprint"; "followSymlinks"; "onlyIfSuccessful" ]
                        "junit",
                        set
                            [ "testResults"; "allowEmptyResults"; "checksName"; "healthScaleFactor"
                              "keepLongStdio"; "keepProperties"; "keepTestNames"
                              "skipMarkingBuildUnstable"; "skipMarkingStageUnstable"
                              "skipOldReports"; "skipPublishingChecks"; "stdioRetention"
                              "testDataPublishers" ]
                        "checkout", set [ "scm"; "changelog"; "poll" ]
                        "deleteDir", Set.empty
                        "git", set [ "url"; "branch"; "changelog"; "credentialsId"; "poll" ]
                        "stash", set [ "name"; "allowEmpty"; "excludes"; "includes"; "useDefaultExcludes" ]
                        "unstable", set [ "message" ]
                        "unstash", set [ "name" ]
                        "withEnv", set [ "overrides" ]
                        "dir", set [ "path" ]
                        "retry", set [ "count"; "conditions" ]
                        "timeout", set [ "time"; "unit"; "activity" ] ]

              for KeyValue(name, descriptor) in WalkerRules.stepDescriptors do
                  Expect.equal
                      (Set.union descriptor.NamedKeys descriptor.UnsupportedNamedKeys)
                      (Map.find name expected)
                      $"{name} keys drifted from the pinned Jenkins schema"

              Expect.equal
                  (Map.find "retry" WalkerRules.stepDescriptors).UnsupportedNamedKeys
                  (set [ "conditions" ])
                  "conditions is measured but remains an explicit unsupported capability"

              let measuredMissingPrimaryClasses =
                  Map.ofList
                      [ "sh", Fogell.Groovy.Interpreter.IllegalArgumentException
                        "archiveArtifacts", Fogell.Groovy.Interpreter.IllegalArgumentException
                        "junit", Fogell.Groovy.Interpreter.NullPointerException
                        "checkout", Fogell.Groovy.Interpreter.NullPointerException
                        "stash", Fogell.Groovy.Interpreter.IllegalArgumentException
                        "unstable", Fogell.Groovy.Interpreter.IllegalArgumentException
                        "unstash", Fogell.Groovy.Interpreter.IllegalArgumentException
                        "dir", Fogell.Groovy.Interpreter.NullPointerException
                        "withEnv", Fogell.Groovy.Interpreter.IllegalArgumentException ]

              for KeyValue(name, descriptor) in WalkerRules.stepDescriptors do
                  Expect.equal
                      descriptor.MissingPrimaryException
                      (Map.tryFind name measuredMissingPrimaryClasses)
                      $"{name} missing-primary exception class drifted from retained Jenkins evidence"

                  Expect.equal
                      descriptor.RequiresPrimary
                      descriptor.MissingPrimaryException.IsSome
                      $"{name} requiredness and measured exception-class data disagree"
          }

          test "unknown-key policy is measured per step" {
              let warned =
                  set
                      [ "sh"; "archiveArtifacts"; "junit"; "checkout"; "deleteDir"
                        "git"; "stash"; "timeout"; "retry" ]

              let thrown = set [ "echo"; "unstable"; "unstash"; "dir"; "withEnv" ]

              for KeyValue(name, descriptor) in WalkerRules.stepDescriptors do
                  match descriptor.UnknownNamed with
                  | WalkerRules.WarnAndContinue bindingClass ->
                      Expect.isTrue (warned.Contains name) $"{name} is a measured warning row"
                      Expect.isNonEmpty bindingClass $"{name} warning names its Jenkins binding class"
                  | WalkerRules.ConstructorMapThrow ->
                      Expect.isTrue (thrown.Contains name) $"{name} is a measured constructor-map row"

              Expect.equal (Set.union warned thrown) WalkerRules.scriptStepVocabulary "policies partition the vocabulary"
          }

          test "terminal hosted-status delivery is exhaustive over all 14 descriptors" {
              let catchable = set [ "sh"; "unstash" ]
              let deferred = Set.difference WalkerRules.scriptStepVocabulary catchable

              for step in WalkerRules.scriptStepVocabulary do
                  let expected =
                      if catchable.Contains step then
                          Some WalkerRules.CatchableStepFailure
                      else
                          Some WalkerRules.DeferredStatusHalt

                  Expect.equal
                      (WalkerRules.hostedStatusFailureDelivery step)
                      expected
                      $"{step} has an explicit measured terminal-status delivery"

              Expect.equal
                  (Set.union catchable deferred)
                  (WalkerRules.stepDescriptors |> Map.keys |> Set.ofSeq)
                  "catchable and deferred status paths cover every descriptor exactly"

              Expect.isNone
                  (WalkerRules.hostedStatusFailureDelivery "outside-vocabulary")
                  "an unregistered step never acquires an implicit status policy"
          }

          test "primary promotion cannot erase a constructor-map unknown" {
              match
                  WalkerRules.validateHostedCall
                      "echo"
                      []
                      [ "message", v "x"; "fogellProbeUnknown", Fogell.Groovy.Interpreter.VBool true ]
              with
              | Error(WalkerRules.JenkinsBindingThrow(exceptionClass, reason, warnings)) ->
                  Expect.equal exceptionClass Fogell.Groovy.Interpreter.IllegalArgumentException "constructor-map binding class"
                  Expect.stringContains reason "fogellProbeUnknown" "the raw unknown survives until classification"
                  Expect.isEmpty warnings "constructor-map throw does not warn first"
              | other -> failtestf "expected a catchable constructor-map throw, got %A" other
          }

          test "warning rows normalize the primary and return warning data" {
              match
                  WalkerRules.validateHostedCall
                      "sh"
                      []
                      [ "script", v "printf ok"; "fogellProbeUnknown", Fogell.Groovy.Interpreter.VBool true ]
              with
              | Ok validated ->
                  Expect.equal validated.Positional [ v "printf ok" ] "script: promoted once"
                  Expect.equal (validated.Named |> List.map fst) [ "fogellProbeUnknown" ] "unknown is retained for dispatch audit"
                  Expect.equal validated.Warnings.Length 1 "one structured warning"
                  Expect.equal validated.Warnings.Head.UnknownKeys [ "fogellProbeUnknown" ] "literal measured key"
              | Error error -> failtestf "warning-and-continue call refused: %A" error
          }

          test "every descriptor primary promotes through the shared boundary exactly once" {
              let primaryValue name =
                  match name with
                  | "retry" -> Fogell.Groovy.Interpreter.VInt 2L
                  | "withEnv" -> Fogell.Groovy.Interpreter.VList(ref [ v "A=1" ])
                  | _ -> v $"sample-{name}"

              let mutable promoted = 0

              for KeyValue(name, descriptor) in WalkerRules.stepDescriptors do
                  match descriptor.PrimaryParameter with
                  | None -> ()
                  | Some primary ->
                      let supplied = primaryValue name

                      match WalkerRules.validateHostedCall name [] [ primary, supplied ] with
                      | Ok validated ->
                          Expect.equal validated.Positional [ supplied ] $"{name}: primary promoted to one positional"
                          Expect.isEmpty validated.Named $"{name}: promoted key is removed from named arguments"
                          Expect.isEmpty validated.Warnings $"{name}: a supported primary never warns"
                          promoted <- promoted + 1
                      | Error error -> failtestf "%s primary failed shared-boundary promotion: %A" name error

              Expect.equal
                  promoted
                  (WalkerRules.stepDescriptors
                   |> Map.values
                   |> Seq.filter (fun descriptor -> descriptor.PrimaryParameter.IsSome)
                   |> Seq.length)
                  "every advertised primary passed through the shared promotion path"
          }

          test "typed primary checks run after named promotion" {
              match
                  WalkerRules.validateHostedCall
                      "retry"
                      []
                      [ "count", Fogell.Groovy.Interpreter.VInt 2L ]
              with
              | Ok validated ->
                  Expect.equal validated.Positional [ Fogell.Groovy.Interpreter.VInt 2L ] "named count promoted once"
                  Expect.isEmpty validated.Named "count is not left behind for a second interpretation"
              | Error error -> failtestf "valid named retry count refused: %A" error

              match WalkerRules.validateHostedCall "retry" [] [ "count", v "two" ] with
              | Error(WalkerRules.EngineRefusal reason) ->
                  Expect.equal reason "`retry` needs an integer attempt count, not `two`" "stable retry type refusal"
              | other -> failtestf "non-integer named retry count escaped its promoted type check: %A" other

              match
                  WalkerRules.validateHostedCall
                      "withEnv"
                      []
                      [ "overrides", Fogell.Groovy.Interpreter.VList(ref [ v "A=1" ]) ]
              with
              | Ok validated ->
                  Expect.equal
                      validated.Positional
                      [ Fogell.Groovy.Interpreter.VList(ref [ v "A=1" ]) ]
                      "named overrides promoted before its list contract"
              | Error error -> failtestf "valid named withEnv overrides refused: %A" error

              match WalkerRules.validateHostedCall "withEnv" [] [ "overrides", v "A=1" ] with
              | Error(WalkerRules.EngineRefusal reason) ->
                  Expect.equal reason "`withEnv` takes exactly one list argument of NAME=VALUE strings" "stable withEnv type refusal"
              | other -> failtestf "non-list named withEnv overrides escaped its promoted type check: %A" other
          }

          test "supported named keys never masquerade as unknowns" {
              match
                  WalkerRules.validateHostedCall
                      "archiveArtifacts"
                      []
                      [ "artifacts", v "*.txt"; "allowEmptyArchive", Fogell.Groovy.Interpreter.VBool true
                        "fingerprint", Fogell.Groovy.Interpreter.VBool true ]
              with
              | Ok validated -> Expect.isEmpty validated.Warnings "all three keys are descriptor-owned"
              | Error error -> failtestf "supported archive schema refused: %A" error
          }

          test "corrected sh and junit keys are accepted, while healthScale stays unknown" {
              match
                  WalkerRules.validateHostedCall
                      "sh"
                      []
                      [ "script", v "printf ok"; "encoding", v "UTF-8" ]
              with
              | Ok validated -> Expect.isEmpty validated.Warnings "encoding is a supported sh key"
              | Error error -> failtestf "supported sh encoding refused: %A" error

              let junitKeys =
                  [ "testResults", v "**/*.xml"
                    "healthScaleFactor", Fogell.Groovy.Interpreter.VInt 1L
                    "keepProperties", Fogell.Groovy.Interpreter.VBool true
                    "keepTestNames", Fogell.Groovy.Interpreter.VBool true
                    "skipMarkingStageUnstable", Fogell.Groovy.Interpreter.VBool true ]

              match WalkerRules.validateHostedCall "junit" [] junitKeys with
              | Ok validated -> Expect.isEmpty validated.Warnings "all corrected junit keys are supported"
              | Error error -> failtestf "supported junit schema refused: %A" error

              match
                  WalkerRules.validateHostedCall
                      "junit"
                      []
                      [ "testResults", v "**/*.xml"; "healthScale", Fogell.Groovy.Interpreter.VInt 1L ]
              with
              | Ok validated ->
                  Expect.equal validated.Warnings.Length 1 "invalid healthScale follows junit's measured warning policy"
                  Expect.equal validated.Warnings.Head.UnknownKeys [ "healthScale" ] "invalid spelling is never normalized away"
              | Error error -> failtestf "junit unknown-key policy changed: %A" error
          }

          test "warning data survives a later missing-primary binding throw" {
              match
                  WalkerRules.validateHostedCall
                      "sh"
                      []
                      [ "fogellProbeUnknown", Fogell.Groovy.Interpreter.VBool true ]
              with
              | Error(WalkerRules.JenkinsBindingThrow(exceptionClass, _, [ warning ])) ->
                  Expect.equal exceptionClass Fogell.Groovy.Interpreter.IllegalArgumentException "missing sh class is measured"
                  Expect.equal warning.UnknownKeys [ "fogellProbeUnknown" ] "Jenkins warns before the missing script throws"
              | other -> failtestf "warning was lost across missing-primary validation: %A" other
          }

          test "requiredness is separate from the primary promotion name" {
              for name in [ "echo"; "deleteDir"; "retry" ] do
                  match WalkerRules.validateHostedCall name [] [] with
                  | Ok _ -> ()
                  | Error error -> failtestf "%s zero-argument measured call refused: %A" name error

              for name in [ "git"; "timeout" ] do
                  match WalkerRules.validateHostedCall name [] [] with
                  | Ok _ -> ()
                  | Error error -> failtestf "%s binds before its downstream runtime outcome: %A" name error

              for name in [ "sh"; "archiveArtifacts"; "junit"; "checkout"; "stash"; "unstable"; "unstash"; "dir"; "withEnv" ] do
                  match WalkerRules.validateHostedCall name [] [] with
                  | Error(WalkerRules.JenkinsBindingThrow _) -> ()
                  | other -> failtestf "%s missing primary did not produce a binding throw: %A" name other
          }

          test "recognized but unimplemented named keys remain fail closed" {
              match
                  WalkerRules.validateHostedCall
                      "retry"
                      []
                      [ "count", Fogell.Groovy.Interpreter.VInt 2L
                        "conditions", Fogell.Groovy.Interpreter.VList(ref []) ]
              with
              | Error(WalkerRules.EngineRefusal reason) ->
                  Expect.stringContains reason "conditions" "the unsupported key is named"
              | other -> failtestf "retry conditions changed from refusal to silent ignore: %A" other
          }

          test "engine refusals stay distinct from catchable Jenkins binding throws" {
              match WalkerRules.validateHostedCall "deleteDir" [ v "ignored" ] [] with
              | Error(WalkerRules.EngineRefusal _) -> ()
              | other -> failtestf "deleteDir arity should remain an engine refusal, got %A" other

              match WalkerRules.validateHostedCall "withEnv" [ v "A=1" ] [] with
              | Error(WalkerRules.EngineRefusal _) -> ()
              | other -> failtestf "typed shape should remain an engine refusal, got %A" other
          }

          test "invalid hosted calls diagnose a shared DAG promptly without rendering it" {
              let mutable dag =
                  Fogell.Groovy.Interpreter.VList(ref [ Fogell.Groovy.Interpreter.VInt 1L ])

              for _ in 1..30 do
                  dag <- Fogell.Groovy.Interpreter.VList(ref [ dag; dag ])

              let stopwatch = Diagnostics.Stopwatch.StartNew()

              match WalkerRules.validateHostedCall "deleteDir" [ dag ] [] with
              | Error(WalkerRules.EngineRefusal reason) ->
                  stopwatch.Stop()
                  Expect.stringContains reason "<list>" "the bounded marker still identifies the rejected value type"
                  Expect.isLessThan reason.Length 200 "the refusal cannot contain an exponentially expanded value"
                  Expect.isLessThan stopwatch.ElapsedMilliseconds 5000L "validation reaches the refusal promptly"
              | other -> failtestf "invalid deleteDir call escaped its no-effect validation boundary: %A" other
          } ]

/// FG-177 slice 2. These cross the real hosted boundary: validation, walker dispatch,
/// the option result slot, and Groovy's ordinary null comparisons all participate.
let genuineNullRuntime =
    let withWorkspace (f: string -> string -> unit) =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-genuine-null-" + Guid.NewGuid().ToString("N"))
        let workspace = IO.Path.Combine(root, "job")
        IO.Directory.CreateDirectory(workspace) |> ignore

        try
            f root workspace
        finally
            if IO.Directory.Exists root then
                IO.Directory.Delete(root, true)

    let pipeline body =
        "pipeline { agent any stages { stage('probe') { steps { script { "
        + body
        + " } } } } }"

    let run body check =
        withWorkspace (fun root workspace ->
            match FogellSide.run [] root "job" (pipeline body) with
            | Error why -> failtestf "genuine-null pipeline refused: %s" why
            | Ok trace -> check workspace trace)

    testList
        "FG-177 genuine-null runtime publication"
        [ test "plain and false-flag sh, echo and successful unstable publish VNull" {
              let body =
                  "def plain = sh(script: 'true'); "
                  + "def falseFlags = sh(script: 'true', returnStdout: false, returnStatus: false); "
                  + "def echoed = echo(); "
                  + "def unstableValue = unstable(message: 'measured-null'); "
                  + "if (plain == null && falseFlags == null && echoed == null && unstableValue == null) { "
                  + "sh 'printf pass > basic-null.txt' }"

              run body (fun workspace trace ->
                  Expect.equal trace.Result "unstable" "unstable remains nonterminal and controls the build result"
                  Expect.equal
                      (IO.File.ReadAllText(IO.Path.Combine(workspace, "basic-null.txt")))
                      "pass"
                      "all four callback results were real Groovy null")
          }

          test "archive, stash, deleteDir and unstash publish VNull after their stateful effects" {
              let body =
                  "sh 'printf seed > seed.txt'; "
                  + "def archived = archiveArtifacts(artifacts: 'seed.txt'); "
                  + "def stashed = stash(name: 'fg177-null', includes: 'seed.txt'); "
                  + "def deleted = deleteDir(); "
                  + "def restored = unstash(name: 'fg177-null'); "
                  + "if (archived == null && stashed == null && deleted == null && restored == null) { "
                  + "sh 'printf pass > stateful-null.txt' }"

              run body (fun workspace trace ->
                  Expect.equal trace.Result "success" "all stateful steps completed"
                  Expect.equal
                      (IO.File.ReadAllText(IO.Path.Combine(workspace, "seed.txt")))
                      "seed"
                      "delete happened before unstash restored the seed"
                  Expect.equal
                      (IO.File.ReadAllText(IO.Path.Combine(workspace, "stateful-null.txt")))
                      "pass"
                      "all four successful stateful calls published null")
          }

          test "validation and catchability happen before genuine-null publication" {
              let body =
                  "def warned = sh(script: 'true', fogellProbeUnknown: true); "
                  + "if (warned == null) { sh 'printf warn > warning-null.txt' }; "
                  + "try { def bad = echo(message: 'never', fogellProbeUnknown: true); sh 'touch escaped-constructor.txt' } "
                  + "catch (IllegalArgumentException e) { sh 'printf constructor > constructor-caught.txt' }; "
                  + "try { def missing = stash(); sh 'touch escaped-required.txt' } "
                  + "catch (IllegalArgumentException e) { sh 'printf required > required-caught.txt' }; "
                  + "try { def failed = sh(script: 'exit 3'); sh 'touch escaped-shell.txt' } "
                  + "catch (Exception e) { sh 'printf shell > shell-caught.txt' }"

              run body (fun workspace trace ->
                  Expect.equal trace.Result "success" "all measured catchable paths were absorbed"

                  for file in
                      [ "warning-null.txt"; "constructor-caught.txt"; "required-caught.txt"; "shell-caught.txt" ] do
                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, file)))
                          $"{file}: the expected branch ran"

                  for file in [ "escaped-constructor.txt"; "escaped-required.txt"; "escaped-shell.txt" ] do
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, file)))
                          $"{file}: a fault never became a null result")
          }

          test "JUnit containment is stack-safe for deeply nested script collections" {
              let mutable withoutSummary = Fogell.Groovy.Interpreter.VInt 0L

              for _ in 1..20000 do
                  withoutSummary <- Fogell.Groovy.Interpreter.VList(ref [ withoutSummary ])

              Expect.isFalse
                  (Fogell.Groovy.Interpreter.Value.containsJUnitSummary withoutSummary)
                  "a deep ordinary value is scanned without consuming the native call stack"

              let summary =
                  Fogell.Groovy.Interpreter.VJUnitSummary(
                      ref
                          { TotalCount = 1L
                            FailCount = 0L
                            SkipCount = 0L })

              let mutable withSummary = summary

              for _ in 1..20000 do
                  withSummary <- Fogell.Groovy.Interpreter.VList(ref [ withSummary ])

              Expect.isTrue
                  (Fogell.Groovy.Interpreter.Value.containsJUnitSummary withSummary)
                  "a deeply wrapped summary is still found"
          }

          test "JUnit publishes only the three measured count properties and remains nonterminal when unstable" {
              let body =
                  "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"4\\\" failures=\\\"1\\\" errors=\\\"1\\\" skipped=\\\"1\\\"><testcase name=\\\"ok\\\"/></testsuite>' > reports/summary.xml\"; "
                  + "def got = junit(testResults: 'reports/summary.xml'); "
                  + "if (got.totalCount == 4 && got.failCount == 2 && got.skipCount == 1) { sh 'touch summary-ok.txt' }"

              run body (fun workspace trace ->
                  Expect.equal trace.Result "unstable" "failing tests keep the build unstable without halting the script"
                  Expect.isTrue
                      (IO.File.Exists(IO.Path.Combine(workspace, "summary-ok.txt")))
                      "all three typed count properties drove the measured branch")
          }

          test "JUnit build-instability suppression preserves the typed summary in scripted calls" {
              let body =
                  "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"4\\\" failures=\\\"1\\\" errors=\\\"1\\\" skipped=\\\"1\\\"><testcase name=\\\"ok\\\"/></testsuite>' > reports/summary.xml\"; "
                  + "def got = junit(testResults: 'reports/summary.xml', skipMarkingBuildUnstable: true); "
                  + "if (got.totalCount == 4 && got.failCount == 2 && got.skipCount == 1) { sh 'touch suppressed-summary-ok.txt' }"

              run body (fun workspace trace ->
                  Expect.equal trace.Result "success" "failed tests no longer mark the build unstable when suppression is true"
                  Expect.isTrue
                      (IO.File.Exists(IO.Path.Combine(workspace, "suppressed-summary-ok.txt")))
                      "suppression leaves all three typed counts observable")
          }

          test "JUnit build-instability suppression uses bare boolean provenance outside script" {
              let source =
                  "pipeline { agent any stages { stage('probe') { steps { "
                  + "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                  + "junit testResults: 'reports/summary.xml', skipMarkingBuildUnstable: true; "
                  + "sh 'touch declarative-suppressed.txt' } } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Error why -> failtestf "bare-boolean JUnit pipeline refused: %s" why
                  | Ok trace ->
                      Expect.equal trace.Result "success" "ExpressionArgs preserves the bare true flag"
                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "declarative-suppressed.txt")))
                          "the direct stage-level successor ran")
          }

          test "JUnit build-instability suppression refuses string text before scanning reports" {
              let body =
                  "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                  + "junit(testResults: 'reports/summary.xml', skipMarkingBuildUnstable: 'true'); "
                  + "sh 'touch string-coercion-ran.txt'"

              run body (fun workspace trace ->
                  Expect.equal trace.Result "failure" "Fogell does not infer a boolean from rendered string text"
                  Expect.isFalse
                      (IO.File.Exists(IO.Path.Combine(workspace, "string-coercion-ran.txt")))
                      "the refused call cannot reach its successor")

              let declarativeSource =
                  "pipeline { agent any environment { SKIP = 'true' } stages { stage('probe') { steps { "
                  + "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                  + "junit testResults: 'reports/summary.xml', skipMarkingBuildUnstable: env.SKIP; "
                  + "sh 'touch expression-coercion-ran.txt' } } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" declarativeSource with
                  | Error why -> failtestf "dynamic-boolean refusal pipeline could not run: %s" why
                  | Ok trace ->
                      Expect.equal trace.Result "failure" "rendered expression text cannot masquerade as a boolean literal"
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "expression-coercion-ran.txt")))
                          "the dynamic direct call was refused before its successor")
          }

          test "JUnit accepts typed scripted booleans for both independent instability channels" {
              let source =
                  "pipeline { agent any stages { stage('probe') { steps { script { "
                  + "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"2\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                  + "def suppressBuild = true; def suppressStage = false; "
                  + "def got = junit(testResults: 'reports/summary.xml', skipMarkingBuildUnstable: suppressBuild, skipMarkingStageUnstable: suppressStage); "
                  + "if (got.totalCount == 2 && got.failCount == 1) { sh 'touch scripted-summary.txt' } "
                  + "} } post { unstable { sh 'touch stage-unstable.txt' } success { sh 'touch wrong-stage-success.txt' } } } } "
                  + "post { success { sh 'touch pipeline-success.txt' } unstable { sh 'touch wrong-pipeline-unstable.txt' } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Error why -> failtestf "typed scripted JUnit flags were refused: %s" why
                  | Ok trace ->
                      Expect.equal trace.Result "success" "build suppression controls only the global result"

                      for file in [ "scripted-summary.txt"; "stage-unstable.txt"; "pipeline-success.txt" ] do
                          Expect.isTrue
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{file}: the matching scripted/stage/post branch ran"

                      for file in [ "wrong-stage-success.txt"; "wrong-pipeline-unstable.txt" ] do
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{file}: build and stage outcomes must not be conflated")
          }

          test "JUnit's two boolean flags preserve the measured build/stage result matrix" {
              let rows =
                  [ "default", "", "unstable", "stage-unstable.txt", "pipeline-unstable.txt"
                    "explicit-false",
                    ", skipMarkingBuildUnstable: false, skipMarkingStageUnstable: false",
                    "unstable",
                    "stage-unstable.txt",
                    "pipeline-unstable.txt"
                    "build-only",
                    ", skipMarkingBuildUnstable: true, skipMarkingStageUnstable: false",
                    "success",
                    "stage-unstable.txt",
                    "pipeline-success.txt"
                    "stage-only",
                    ", skipMarkingBuildUnstable: false, skipMarkingStageUnstable: true",
                    "success",
                    "stage-success.txt",
                    "pipeline-success.txt"
                    "both",
                    ", skipMarkingBuildUnstable: true, skipMarkingStageUnstable: true",
                    "success",
                    "stage-success.txt",
                    "pipeline-success.txt" ]

              for label, flags, expectedResult, expectedStagePost, expectedPipelinePost in rows do
                  let source =
                      "pipeline { agent any stages { stage('probe') { steps { "
                      + "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                      + $"junit testResults: 'reports/summary.xml'{flags}; sh 'touch successor.txt' "
                      + "} post { unstable { sh 'touch stage-unstable.txt' } success { sh 'touch stage-success.txt' } } } } "
                      + "post { unstable { sh 'touch pipeline-unstable.txt' } success { sh 'touch pipeline-success.txt' } } }"

                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Error why -> failtestf "%s matrix pipeline refused: %s" label why
                      | Ok trace ->
                          Expect.equal trace.Result expectedResult $"{label}: exact global result"
                          Expect.isTrue
                              (IO.File.Exists(IO.Path.Combine(workspace, "successor.txt")))
                              $"{label}: JUnit instability remains nonterminal"
                          Expect.isTrue
                              (IO.File.Exists(IO.Path.Combine(workspace, expectedStagePost)))
                              $"{label}: stage post reads the WarningAction channel"
                          Expect.isTrue
                              (IO.File.Exists(IO.Path.Combine(workspace, expectedPipelinePost)))
                              $"{label}: pipeline post reads only the global build result"

                          let wrongStagePost =
                              if expectedStagePost = "stage-unstable.txt" then "stage-success.txt" else "stage-unstable.txt"

                          let wrongPipelinePost =
                              if expectedPipelinePost = "pipeline-unstable.txt" then
                                  "pipeline-success.txt"
                              else
                                  "pipeline-unstable.txt"

                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, wrongStagePost)))
                              $"{label}: the opposite stage-post arm stayed closed"
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, wrongPipelinePost)))
                              $"{label}: the opposite pipeline-post arm stayed closed")
          }

          test "a nested sequential JUnit warning selects child and enclosing stage post without marking the build" {
              let source =
                  "pipeline { agent any stages { stage('parent') { stages { stage('child') { steps { "
                  + "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                  + "junit testResults: 'reports/summary.xml', skipMarkingBuildUnstable: true, skipMarkingStageUnstable: false; "
                  + "sh 'touch child-successor.txt' "
                  + "} post { unstable { sh 'touch child-unstable.txt' } success { sh 'touch wrong-child-success.txt' } } } } "
                  + "post { unstable { sh 'touch parent-unstable.txt' } success { sh 'touch wrong-parent-success.txt' } } } "
                  + "stage('later') { steps { sh 'touch later.txt' } } } "
                  + "post { success { sh 'touch pipeline-success.txt' } unstable { sh 'touch wrong-pipeline-unstable.txt' } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Error why -> failtestf "nested sequential stage-warning pipeline refused: %s" why
                  | Ok trace ->
                      Expect.equal trace.Result "success" "the nested warning never enters the global build sink"

                      for file in
                          [ "child-successor.txt"
                            "child-unstable.txt"
                            "parent-unstable.txt"
                            "later.txt"
                            "pipeline-success.txt" ] do
                          Expect.isTrue
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{file}: the nested stage-warning propagation selected this effect"

                      for file in
                          [ "wrong-child-success.txt"
                            "wrong-parent-success.txt"
                            "wrong-pipeline-unstable.txt" ] do
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{file}: a stage warning cannot select the opposite stage or pipeline arm")
          }

          test "a parallel JUnit warning stays branch-local while selecting the enclosing fanout post" {
              let source =
                  "pipeline { agent any stages { stage('fanout') { parallel { "
                  + "stage('warned') { steps { "
                  + "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/warned.xml\"; "
                  + "junit testResults: 'reports/warned.xml', skipMarkingBuildUnstable: true, skipMarkingStageUnstable: false; "
                  + "sh 'touch warned-successor.txt' "
                  + "} post { unstable { sh 'touch warned-unstable.txt' } success { sh 'touch wrong-warned-success.txt' } } } "
                  + "stage('clean') { steps { sh 'touch clean-branch.txt' } "
                  + "post { success { sh 'touch clean-success.txt' } unstable { sh 'touch wrong-clean-unstable.txt' } } } "
                  + "} post { unstable { sh 'touch fanout-unstable.txt' } success { sh 'touch wrong-fanout-success.txt' } } } "
                  + "stage('later') { steps { sh 'touch parallel-later.txt' } } } "
                  + "post { success { sh 'touch parallel-pipeline-success.txt' } unstable { sh 'touch wrong-parallel-pipeline-unstable.txt' } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Error why -> failtestf "parallel stage-warning pipeline refused: %s" why
                  | Ok trace ->
                      Expect.equal trace.Result "success" "the parallel warning never enters the global build sink"

                      for file in
                          [ "warned-successor.txt"
                            "warned-unstable.txt"
                            "clean-branch.txt"
                            "clean-success.txt"
                            "fanout-unstable.txt"
                            "parallel-later.txt"
                            "parallel-pipeline-success.txt" ] do
                          Expect.isTrue
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{file}: the branch-local or enclosing expected effect ran"

                      for file in
                          [ "wrong-warned-success.txt"
                            "wrong-clean-unstable.txt"
                            "wrong-fanout-success.txt"
                            "wrong-parallel-pipeline-unstable.txt" ] do
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{file}: the warned branch did not contaminate its sibling or global result")
          }

          test "both JUnit boolean flags refuse text, dynamic direct expressions and duplicates before report scanning" {
              for key in [ "skipMarkingBuildUnstable"; "skipMarkingStageUnstable" ] do
                  let scriptedText =
                      "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                      + $"junit(testResults: 'reports/summary.xml', {key}: 'true'); "
                      + "sh 'touch scripted-text-ran.txt'"

                  run scriptedText (fun workspace trace ->
                      Expect.equal trace.Result "failure" $"{key}: scripted text is not a boolean"
                      Expect.isFalse
                          (trace.Output |> List.contains "Recording test results")
                          $"{key}: text was refused before the report scanner emitted"
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "scripted-text-ran.txt")))
                          $"{key}: the successor did not run")

                  let dynamicDirect =
                      "pipeline { agent any environment { SKIP = 'true' } stages { stage('probe') { steps { "
                      + "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                      + $"junit testResults: 'reports/summary.xml', {key}: env.SKIP; "
                      + "sh 'touch dynamic-ran.txt' } } } }"

                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" dynamicDirect with
                      | Error why -> failtestf "%s dynamic-refusal pipeline could not run: %s" key why
                      | Ok trace ->
                          Expect.equal trace.Result "failure" $"{key}: rendered dynamic text is refused"
                          Expect.isFalse
                              (trace.Output |> List.contains "Recording test results")
                              $"{key}: the dynamic value was refused before scanning"
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, "dynamic-ran.txt")))
                              $"{key}: the direct successor did not run")

                  let duplicate =
                      "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"1\\\" skipped=\\\"0\\\"/>' > reports/summary.xml\"; "
                      + $"junit(testResults: 'reports/summary.xml', {key}: true, {key}: false); "
                      + "sh 'touch duplicate-ran.txt'"

                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" (pipeline duplicate) with
                      | Ok trace -> failtestf "%s duplicate binding unexpectedly ran: %A" key trace
                      | Error why ->
                          Expect.stringContains why "malformed_syntax" $"{key}: Groovy rejects the duplicate map key"
                          Expect.isFalse
                              (IO.Directory.Exists(IO.Path.Combine(workspace, "reports")))
                              $"{key}: the compile-shaped duplicate refusal happened before report setup"
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, "duplicate-ran.txt")))
                              $"{key}: the duplicate call cannot reach its successor")
          }

          test "unmeasured JUnit object surface remains catch-opaque and cannot reach a successor effect" {
              let operations =
                  [ "property", "def ignored = got.duration"
                    "method", "def ignored = got.getTotalCount()"
                    "index", "def ignored = got['totalCount']"
                    "truthiness", "def ignored = got ? 1 : 0"
                    "equality", "def ignored = got == got"
                    "stringification", "def ignored = \"${got}\""
                    "mutation", "got.totalCount = 0"
                    "spread", "def ignored = [got]*.totalCount"
                    "instanceof", "def ignored = got instanceof Object"
                    "throw", "throw got"
                    "nested-throw", "throw [got]"
                    "nested-host-argument", "echo([got])" ]

              for label, operation in operations do
                  let body =
                      "sh \"mkdir -p reports; printf '%s' '<testsuite tests=\\\"1\\\" failures=\\\"0\\\" skipped=\\\"0\\\"><testcase name=\\\"ok\\\"/></testsuite>' > reports/summary.xml\"; "
                      + "def got = junit(testResults: 'reports/summary.xml'); "
                      + $"try {{ {operation}; sh 'touch escaped.txt' }} catch (Exception e) {{ sh 'touch caught.txt' }}"

                  run body (fun workspace trace ->
                      Expect.equal trace.Result "failure" $"{label}: an unmodelled object operation fails closed"
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "escaped.txt")))
                          $"{label}: the successor inside try did not run"
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "caught.txt")))
                          $"{label}: ordinary Groovy catch cannot absorb a Fogell modelling refusal")
          }

          test "dir, timeout, retry and withEnv publish the body result" {
              let body =
                  "def dirValue = dir('child') { return 'DIR-BODY' }; "
                  + "def timeoutValue = timeout(time: 1, unit: 'MINUTES') { ['TIMEOUT-BODY', 6] }; "
                  + "def attempts = 0; "
                  + "def retryValue = retry(count: 2) { attempts = attempts + 1; "
                  + "if (attempts == 1) { sh 'exit 1' }; return \"RETRY-${attempts}\" }; "
                  + "def withEnvValue = withEnv(['FG177_BODY=value']) { return \"WITHENV-${env.FG177_BODY}\" }; "
                  + "if (dirValue == 'DIR-BODY' && timeoutValue[0] == 'TIMEOUT-BODY' "
                  + "&& timeoutValue[1] + 1 == 7 "
                  + "&& retryValue == 'RETRY-2' && withEnvValue == 'WITHENV-value') { "
                  + "sh 'printf pass > wrapper-body-result.txt' }"

              run body (fun workspace trace ->
                  Expect.equal trace.Result "success" "the recovered retry leaves the build successful"
                  Expect.equal
                      (IO.File.ReadAllText(IO.Path.Combine(workspace, "wrapper-body-result.txt")))
                      "pass"
                      "all four wrapper results reached Groovy with the measured values")
          } ]

/// FG-177. The retained six-build oracle schedule, executed against Fogell's
/// real Git walker for BOTH producers. The remote refs advance from pipeline
/// post so the next retained build sees a new revision while one job, its
/// controller-side history, and its workspace survive.
let scmReturnMapRuntime =
    let runGit cwd args =
        let start = Diagnostics.ProcessStartInfo()
        start.FileName <- "git"
        start.WorkingDirectory <- cwd
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.Environment["GIT_AUTHOR_NAME"] <- "Fogell Test"
        start.Environment["GIT_AUTHOR_EMAIL"] <- "fogell@example.invalid"
        start.Environment["GIT_COMMITTER_NAME"] <- "Fogell Test"
        start.Environment["GIT_COMMITTER_EMAIL"] <- "fogell@example.invalid"

        for arg in args do
            start.ArgumentList.Add arg

        use child = Diagnostics.Process.Start start
        let stdout = child.StandardOutput.ReadToEnd()
        let stderr = child.StandardError.ReadToEnd()
        child.WaitForExit()

        if child.ExitCode <> 0 then
            let command = String.concat " " args
            failwith $"git {command} failed ({child.ExitCode}): {stderr}"

        stdout.Trim()

    let expectedKeys producer hasHistory =
        let baseKeys =
            if producer = "git" then
                [ "GIT_BRANCH"; "GIT_COMMIT"; "GIT_LOCAL_BRANCH"; "GIT_URL" ]
            else
                [ "GIT_BRANCH"; "GIT_COMMIT"; "GIT_URL" ]

        if hasHistory then
            baseKeys @ [ "GIT_PREVIOUS_COMMIT"; "GIT_PREVIOUS_SUCCESSFUL_COMMIT" ]
        else
            baseKeys
        |> List.sort
        |> String.concat ","

    test "git and checkout scm return exact retained per-branch history maps" {
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-scm-map-runtime-" + Guid.NewGuid().ToString("N"))
        let source = IO.Path.Combine(root, "source")
        let bare = IO.Path.Combine(root, "fixture.git")
        let control = IO.Path.Combine(root, "control")
        let workspaceRoot = IO.Path.Combine(root, "workspaces")

        IO.Directory.CreateDirectory source |> ignore
        IO.Directory.CreateDirectory control |> ignore
        IO.Directory.CreateDirectory workspaceRoot |> ignore

        let advancePost =
            "post { always { sh '''if [ -f \""
            + control
            + "/$BUILD_NUMBER.ref\" ]; then read branch sha < \""
            + control
            + "/$BUILD_NUMBER.ref\"; git --git-dir=\""
            + bare
            + "\" update-ref \"refs/heads/$branch\" \"$sha\"; fi''' } }"

        let pipeline producer branch =
            let producerCall =
                if producer = "checkout-scm" then
                    "checkout(scm)"
                else
                    "git(url: '" + bare + "', branch: '" + branch + "')"

            "pipeline { agent any options { skipDefaultCheckout() } stages { stage('capture') { steps { script { "
            + "if (env.BUILD_NUMBER == '2') { deleteDir() }; def value = "
            + producerCall
            + "; echo \"FG177-RUNTIME:${env.BUILD_NUMBER}:${value.keySet().join(',')}:${value.GIT_COMMIT}:${value.GIT_PREVIOUS_COMMIT}:${value.GIT_PREVIOUS_SUCCESSFUL_COMMIT}\"; "
            + "if (env.BUILD_NUMBER == '2') { sh 'exit 7' }"
            + " } } } } "
            + advancePost
            + " }"

        let checkoutScript = pipeline "checkout-scm" "main"

        let commit label =
            IO.File.WriteAllText(IO.Path.Combine(source, "payload.txt"), label + "\n")
            runGit source [ "add"; "Jenkinsfile"; "payload.txt" ] |> ignore
            runGit source [ "commit"; "-m"; label ] |> ignore
            runGit source [ "rev-parse"; "HEAD" ]

        try
            runGit source [ "init"; "-b"; "main" ] |> ignore
            IO.File.WriteAllText(IO.Path.Combine(source, "Jenkinsfile"), checkoutScript + "\n")
            let a = commit "A"
            let b = commit "B"
            let c = commit "C"
            let d = commit "D"
            runGit source [ "switch"; "--detach"; a ] |> ignore
            runGit source [ "switch"; "-c"; "feature" ] |> ignore
            let f = commit "F"
            let g = commit "G"

            runGit root [ "init"; "--bare"; bare ] |> ignore
            runGit source [ "push"; bare; "--all" ] |> ignore

            let setRef branch revision =
                runGit root [ "--git-dir"; bare; "update-ref"; $"refs/heads/{branch}"; revision ] |> ignore

            let resetRefs () =
                setRef "main" a
                setRef "feature" f

            let writeAdvance build branch revision =
                IO.File.WriteAllText(IO.Path.Combine(control, $"{build}.ref"), $"{branch} {revision}\n")

            writeAdvance 1 "main" b
            writeAdvance 2 "main" c
            writeAdvance 4 "feature" g
            writeAdvance 5 "main" d

            let schedule =
                [ "main", a, None, None
                  "main", b, Some a, Some a
                  "main", c, Some b, Some a
                  "feature", f, None, None
                  "feature", g, Some f, Some f
                  "main", d, Some c, Some c ]

            let assertSchedule (producer: string) (results: Result<Trace, string> list) =
                Expect.equal results.Length 6 $"{producer}: all retained builds ran"

                (schedule, results)
                ||> List.iter2 (fun (branch, revision, previous, previousSuccessful) result ->
                    let trace =
                        match result with
                        | Ok trace -> trace
                        | Error why -> failtestf "%s retained build failed to run: %s" producer why

                    let build =
                        schedule
                        |> List.findIndex (fun item -> item = (branch, revision, previous, previousSuccessful))
                        |> (+) 1

                    Expect.equal
                        trace.Result
                        (if build = 2 then "failure" else "success")
                        $"{producer} build {build}: terminal result"

                    let valueOrNull = Option.defaultValue "null"
                    let expected =
                        $"FG177-RUNTIME:{build}:{expectedKeys producer previous.IsSome}:{revision}:{valueOrNull previous}:{valueOrNull previousSuccessful}"

                    Expect.contains trace.Output expected $"{producer} build {build}: exact map projection")

            resetRefs ()
            let checkoutBuilds =
                schedule
                |> List.map (fun (branch, _, _, _) -> ({ Url = bare; Branch = branch }, checkoutScript))

            FogellSide.runScmMany [] workspaceRoot "checkout-job" checkoutBuilds
            |> assertSchedule "checkout-scm"

            resetRefs ()
            let gitBuilds = schedule |> List.map (fun (branch, _, _, _) -> pipeline "git" branch)
            FogellSide.runMany [] workspaceRoot "git-job" gitBuilds
            |> assertSchedule "git"

            // A freshly recreated job with the SAME name must erase the six
            // retained builds above. Build 1 returns the narrow base map again,
            // not D/C/C inherited from the deleted job.
            resetRefs ()
            let freshAgain = FogellSide.runMany [] workspaceRoot "git-job" [ pipeline "git" "main" ]
            match freshAgain with
            | [ Ok trace ] ->
                let baseKeys = expectedKeys "git" false
                let expected = $"FG177-RUNTIME:1:{baseKeys}:{a}:null:null"
                Expect.equal trace.Result "success" "recreated job build 1 succeeds"
                Expect.contains trace.Output expected "recreated job has no retained history"
            | other -> failtestf "recreated job did not run exactly once: %A" other

            // The retained oracle did not license a history with previous but
            // no previous-successful, nor one whose latest predecessor is
            // unstable. Both refuse before the silent repository discriminator
            // or any fetch/workspace mutation can run.
            resetRefs ()
            let firstFailureScript =
                (pipeline "git" "main").Replace(
                    "env.BUILD_NUMBER == '2') { sh 'exit 7'",
                    "env.BUILD_NUMBER == '1') { sh 'exit 7'"
                )

            let firstFailure =
                FogellSide.runMany [] workspaceRoot "first-failure-job" [ firstFailureScript; firstFailureScript ]

            let firstFailureTraces =
                firstFailure
                |> List.map (function Ok trace -> trace | Error why -> failtestf "first-failure run error: %s" why)

            Expect.equal (firstFailureTraces |> List.map (fun trace -> trace.Result)) [ "failure"; "failure" ] "partial history refuses"
            Expect.isFalse
                (firstFailureTraces.[1].Output |> List.exists (fun line -> line = "Selected Git installation does not exist. Using Default"))
                "partial history refuses before the first Git narration"

            resetRefs ()
            let unstableScript =
                (pipeline "git" "main").Replace(
                    "if (env.BUILD_NUMBER == '2') { sh 'exit 7' }",
                    "unstable(message: 'measured predecessor')"
                )

            let unstable =
                FogellSide.runMany [] workspaceRoot "unstable-history-job" [ unstableScript; unstableScript ]
                |> List.map (function Ok trace -> trace | Error why -> failtestf "unstable-history run error: %s" why)

            Expect.equal (unstable |> List.map (fun trace -> trace.Result)) [ "unstable"; "failure" ] "unstable history refuses"
            Expect.isFalse
                (unstable.[1].Output |> List.exists (fun line -> line = "Selected Git installation does not exist. Using Default"))
                "unstable predecessor refuses before the first Git narration"

            // A provisional or damaged controller-side record is evidence of
            // interrupted/corrupt history, never permission to fall back to an
            // older SHA (or to pretend this is the branch's first observation).
            // Damage happens in build 2 immediately before its git step so the
            // public retained runner exercises the production read boundary.
            let assertDamagedHistory (label: string) (jobName: string) (damage: string) =
                resetRefs ()
                let damaged =
                    (pipeline "git" "main").Replace(
                        "if (env.BUILD_NUMBER == '2') { deleteDir() }; def value = ",
                        $"if (env.BUILD_NUMBER == '2') {{ sh '{damage}'; deleteDir() }}; def value = "
                    )

                let traces =
                    FogellSide.runMany [] workspaceRoot jobName [ damaged; damaged ]
                    |> List.map (function Ok trace -> trace | Error why -> failtestf "%s run error: %s" label why)

                Expect.equal (traces |> List.map (fun trace -> trace.Result)) [ "success"; "failure" ] $"{label}: damaged history refuses"
                Expect.isFalse
                    (traces.[1].Output |> List.exists (fun line -> line = "Selected Git installation does not exist. Using Default"))
                    $"{label}: refusal precedes the first Git narration"

            let scmHistoryDir jobName =
                IO.Path.Combine(workspaceRoot, "_artifacts", "_scm", jobName)

            let missingDir = scmHistoryDir "missing-marker-job"
            let missingMarker = IO.Path.Combine(missingDir, "build@1.result")
            assertDamagedHistory
                "missing marker"
                "missing-marker-job"
                $"rm -f {missingMarker}"

            let corruptDir = scmHistoryDir "corrupt-marker-job"
            let corruptMarker = IO.Path.Combine(corruptDir, "build@1.result")
            assertDamagedHistory
                "corrupt marker"
                "corrupt-marker-job"
                $"printf corrupt > {corruptMarker}"

            let revisionDir = scmHistoryDir "invalid-revision-job"
            assertDamagedHistory
                "invalid revision"
                "invalid-revision-job"
                $"for f in {revisionDir}/*.revision; do printf not-a-sha > \"$f\"; done"
        finally
            if IO.Directory.Exists root then
                IO.Directory.Delete(root, true)
    }

/// FG-014 residual slice. Balanced named collections are admitted as source expressions,
/// not interpreted as runtime values. These real run entry points prove the shared
/// preflight runs before a fresh-workspace wipe, an earlier step, or a post effect.
let unsupportedNamedCollections =
    let stageMap =
        "pipeline { agent any stages { stage('before') { steps { sh 'echo ran > ran.txt' } } stage('publish') { steps { publishHTML target: [allowMissing: true, reportName: 'r'] } } } }"

    let parameterList =
        "pipeline { agent any parameters { choice(name: 'v', choices: ['a', 'b'], description: 'd') } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }"

    let nestedParallel =
        "pipeline { agent any stages { stage('outer') { parallel { stage('branch') { steps { publishHTML target: [allowMissing: true] } } } } stage('after') { steps { sh 'echo ran > ran.txt' } } } }"

    let pipelinePost =
        "pipeline { agent any stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } post { always { notify recipients: ['ops'] } } }"

    let scalarControl =
        "pipeline { agent any stages { stage('a') { steps { sh script: 'echo ran > ran.txt', returnStatus: true } } } }"

    let positionalCollectionControl =
        "pipeline { agent any stages { stage('a') { steps { withEnv(['A=one']) { sh 'echo $A > ran.txt' } } } } }"

    let rateLimitBuildsControl =
        "pipeline { agent any options { rateLimitBuilds(throttle: [count: 10, durationName: 'hour', userBoost: true]) } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }"

    let withWorkspace (f: string -> string -> unit) =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-named-collection-preflight-" + Guid.NewGuid().ToString("N"))
        let job = "job"
        let workspace = IO.Path.Combine(root, job)
        IO.Directory.CreateDirectory(workspace) |> ignore
        let sentinel = IO.Path.Combine(workspace, "sentinel.txt")
        IO.File.WriteAllText(sentinel, "keep")

        try
            f root workspace
        finally
            if IO.Directory.Exists root then
                IO.Directory.Delete(root, true)

    testList
        "FG-014 named collection execution contract"
        [ test "the descriptor-declared pipeline rateLimitBuilds throttle collection remains executable" {
              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" rateLimitBuildsControl with
                  | Error why -> failtestf "rateLimitBuilds control was refused: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "the inert single-build option remains accepted"

                  Expect.equal
                      (IO.File.ReadAllText(IO.Path.Combine(workspace, "ran.txt")).Trim())
                      "ran"
                      "the pipeline reached its executor effect")
          }

          test "the collection exception is closed over option name, argument name and pipeline scope" {
              for label, source, occurrence in
                  [ "different option",
                    "pipeline { agent any options { quietPeriod(throttle: [count: 10]) } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }",
                    "step 'quietPeriod' argument `throttle`"
                    "different argument",
                    "pipeline { agent any options { rateLimitBuilds(policy: [count: 10]) } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }",
                    "step 'rateLimitBuilds' argument `policy`"
                    "stage option scope",
                    "pipeline { agent any stages { stage('a') { options { rateLimitBuilds(throttle: [count: 10]) } steps { sh 'echo ran > ran.txt' } } } }",
                    "step 'rateLimitBuilds' argument `throttle`" ] do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s unexpectedly executed: %A" label trace
                      | Error why ->
                          Expect.stringStarts why "unsupported_named_collection:" $"{label}: stable named reason"
                          Expect.stringContains why occurrence $"{label}: offending argument named"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: workspace preparation was not reached"

                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt")))
                          $"{label}: executor effect was not reached")
          }

          test "stage, parameter, nested and post collections refuse before effects or workspace preparation" {
              for label, source, occurrence in
                  [ "stage map", stageMap, "step 'publishHTML' argument `target`"
                    "parameter list", parameterList, "step 'choice' argument `choices`"
                    "nested parallel map", nestedParallel, "step 'publishHTML' argument `target`"
                    "pipeline post list", pipelinePost, "step 'notify' argument `recipients`" ] do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s unexpectedly executed: %A" label trace
                      | Error why ->
                          Expect.stringStarts why "unsupported_named_collection:" $"{label}: stable named reason"
                          Expect.stringContains why occurrence $"{label}: offending argument named"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: fresh-run wipe was not reached"

                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt")))
                          $"{label}: no earlier or later executor effect was reached")
          }

          test "scalar named arguments and proven positional collections are unaffected" {
              for label, source, expected in
                  [ "scalar named", scalarControl, "ran"
                    "positional withEnv list", positionalCollectionControl, "one" ] do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Error why -> failtestf "%s control was refused: %s" label why
                      | Ok trace -> Expect.equal trace.Result "success" $"{label}: execution remains available"

                      Expect.equal
                          (IO.File.ReadAllText(IO.Path.Combine(workspace, "ran.txt")).Trim())
                          expected
                          $"{label}: the existing executor path ran")
          } ]

/// FG-014. Plugin-defined agents are admitted structurally, but Fogell has no
/// Kubernetes provisioning, workspace placement or environment semantics. Every run
/// entry converges on the same preflight before WalkerCtx and workspace preparation.
let unsupportedDeclarativeAgents =
    let args = "   /* retained leading trivia */ label: 'docker', yaml: 'apiVersion: v1' /* retained tail */  "

    let cases =
        [ "pipeline", $"pipeline {{ agent {{ kubernetes {args} }} stages {{ stage('a') {{ steps {{ sh 'echo ran > ran.txt' }} }} }} }}", "pipeline (`kubernetes`)"
          "stage", $"pipeline {{ agent any stages {{ stage('a') {{ agent {{ kubernetes {args} }} steps {{ sh 'echo ran > ran.txt' }} }} }} }}", "stage 'a' (`kubernetes`)"
          "nested sequential", $"pipeline {{ agent any stages {{ stage('outer') {{ stages {{ stage('inner') {{ agent {{ kubernetes {args} }} steps {{ sh 'echo ran > ran.txt' }} }} }} }} }} }}", "stage 'inner' (`kubernetes`)"
          "nested parallel", $"pipeline {{ agent any stages {{ stage('outer') {{ parallel {{ stage('branch') {{ agent {{ kubernetes {args} }} steps {{ sh 'echo ran > ran.txt' }} }} }} }} }} }}", "stage 'branch' (`kubernetes`)" ]

    let control =
        "pipeline { agent any stages { stage('a') { agent any steps { sh 'echo ran > ran.txt' } } } }"

    let duplicates =
        [ "pipeline", "pipeline { agent any agent { kubernetes label: 'docker', yaml: 'apiVersion: v1' } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }"
          "stage", "pipeline { agent any stages { stage('a') { agent any agent { kubernetes label: 'docker', yaml: 'apiVersion: v1' } steps { sh 'echo ran > ran.txt' } } } }" ]

    let withWorkspace (f: string -> string -> unit) =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-agent-preflight-" + Guid.NewGuid().ToString("N"))
        let job = "job"
        let workspace = IO.Path.Combine(root, job)
        IO.Directory.CreateDirectory(workspace) |> ignore
        let sentinel = IO.Path.Combine(workspace, "sentinel.txt")
        IO.File.WriteAllText(sentinel, "keep")

        try
            f root workspace
        finally
            if IO.Directory.Exists root then
                IO.Directory.Delete(root, true)

    testList
        "FG-014 plugin-defined agents fail closed before execution"
        [ test "pipeline and every flattened stage form refuse before effects or workspace wipe" {
              for label, source, scope in cases do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s unexpectedly executed: %A" label trace
                      | Error why ->
                          Expect.stringStarts why "unsupported_agent:" $"{label}: stable named reason"
                          Expect.stringContains why scope $"{label}: offending kind and scope named"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: fresh-run wipe was not reached"

                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt")))
                          $"{label}: executor effect was not reached")
          }

          test "SCM execution refuses the agent before any checkout" {
              let _, source, _ = cases.Head

              withWorkspace (fun root workspace ->
                  let unreachable = { Url = "file:///definitely/not/a/repository"; Branch = "main" }

                  match FogellSide.runScm [] root "job" unreachable source with
                  | Ok trace -> failtestf "SCM agent unexpectedly executed: %A" trace
                  | Error why -> Expect.stringStarts why "unsupported_agent:" "preflight wins over checkout"

                  Expect.isTrue (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt"))) "workspace retained"
                  Expect.isFalse (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt"))) "no effect")
          }

          test "duplicate agent sections refuse before first-match loss or effects" {
              for label, source in duplicates do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s duplicate agent unexpectedly executed: %A" label trace
                      | Error why -> Expect.stringContains why "malformed_syntax" $"{label}: admission refusal"

                      Expect.isTrue (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt"))) "workspace retained"
                      Expect.isFalse (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt"))) "no effect")
          }

          test "modelled pipeline and stage agents remain executable" {
              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" control with
                  | Error why -> failtestf "control was refused: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "existing agent path is unchanged"

                  Expect.equal
                      (IO.File.ReadAllText(IO.Path.Combine(workspace, "ran.txt")).Trim())
                      "ran"
                      "control effect ran")
          } ]

/// FG-014. Admission may retain tools syntax for the parse-only corpus metric, but
/// execution must refuse before it can inherit an unrelated host toolchain and report a
/// false success. These are real run entry points, so the sentinel proves the fresh-run
/// wipe and Executor dispatch were not reached.
let unsupportedDeclarativeTools =
    let top =
        "pipeline { agent any tools { maven 'm3' } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }"

    let stage =
        "pipeline { agent any stages { stage('a') { tools { jdk 'j8' } steps { sh 'echo ran > ran.txt' } } } }"

    let nestedParallel =
        "pipeline { agent any stages { stage('parent') { parallel { stage('branch') { tools { maven 'm3' } steps { sh 'echo ran > ran.txt' } } } } } }"

    let duplicateTop =
        "pipeline { agent any tools { } tools { maven 'm3' } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }"

    let duplicateStage =
        "pipeline { agent any stages { stage('a') { tools { } tools { jdk 'j8' } steps { sh 'echo ran > ran.txt' } } } }"

    let duplicateNestedParallel =
        "pipeline { agent any stages { stage('parent') { parallel { stage('branch') { tools { } tools { maven 'm3' } steps { sh 'echo ran > ran.txt' } } } } } }"

    let duplicateStructuralSections =
        [ "stage steps",
          "pipeline { agent any stages { stage('a') { steps('empty') { } steps { sh 'echo ran > ran.txt' } } } }"
          "stage post",
          "pipeline { agent any stages { stage('a') { steps { sh 'echo ran > ran.txt' } post('empty') { } post { always { echo 'later' } } } } }"
          "pipeline post",
          "pipeline { agent any stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } post('empty') { } post { always { echo 'later' } } }"
          "pipeline stages",
          "pipeline { agent any stages('empty') { } stages { stage('a') { steps { sh 'echo ran > ran.txt' } } } }"
          "nested stages",
          "pipeline { agent any stages { stage('outer') { stages('empty') { } stages { stage('inner') { steps { sh 'echo ran > ran.txt' } } } } } }" ]

    let competingStageBodies =
        [ "labelled steps gate then stages effect",
          "pipeline { agent any stages { stage('target') { steps('gate') { input message: 'Deploy?' } stages { stage('child') { steps { sh 'echo ran > ran.txt' } } } } } }"
          "labelled empty stages then steps effect",
          "pipeline { agent any stages { stage('target') { stages('empty') { } steps { sh 'echo ran > ran.txt' } } } }"
          "empty steps then parallel effect",
          "pipeline { agent any stages { stage('target') { steps { } parallel { stage('branch') { steps { sh 'echo ran > ran.txt' } } } } } }"
          "empty parallel then parallel effect",
          "pipeline { agent any stages { stage('target') { parallel { } parallel { stage('branch') { steps { sh 'echo ran > ran.txt' } } } } } }"
          "empty steps then matrix effect",
          "pipeline { agent any stages { stage('target') { steps { } matrix { axes { axis { name 'OS'; values 'linux' } } stages { stage('cell') { steps { sh 'echo ran > ran.txt' } } } } } } }"
          "empty matrix then matrix effect",
          "pipeline { agent any stages { stage('target') { matrix { } matrix { axes { axis { name 'OS'; values 'linux' } } stages { stage('cell') { steps { sh 'echo ran > ran.txt' } } } } } } }"
          "all four body kinds",
          "pipeline { agent any stages { stage('target') { steps('gate') { input message: 'Deploy?' } stages { stage('child') { steps { sh 'echo ran > ran.txt' } } } parallel { stage('branch') { steps { sh 'echo ran > parallel.txt' } } } matrix { axes { axis { name 'OS'; values 'linux' } } stages { stage('cell') { steps { sh 'echo ran > matrix.txt' } } } } } } }"
          "nested sequential child",
          "pipeline { agent any stages { stage('outer') { stages { stage('inner') { steps('gate') { input message: 'Deploy?' } parallel { stage('branch') { steps { sh 'echo ran > ran.txt' } } } } } } } }"
          "nested parallel child",
          "pipeline { agent any stages { stage('outer') { parallel { stage('branch') { stages('group') { stage('inner') { steps { sh 'echo ran > ran.txt' } } } matrix { axes { axis { name 'OS'; values 'linux' } } stages { stage('cell') { steps { sh 'echo ran > matrix.txt' } } } } } } } } }" ]

    let empty =
        "pipeline { agent any tools { } stages { stage('a') { tools { } steps { sh 'echo ran > ran.txt' } } } }"

    let withWorkspace (f: string -> string -> unit) =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-tools-preflight-" + Guid.NewGuid().ToString("N"))
        let job = "job"
        let workspace = IO.Path.Combine(root, job)
        IO.Directory.CreateDirectory(workspace) |> ignore
        let sentinel = IO.Path.Combine(workspace, "sentinel.txt")
        IO.File.WriteAllText(sentinel, "keep")

        try
            f root workspace
        finally
            if IO.Directory.Exists root then
                IO.Directory.Delete(root, true)

    testList
        "FG-014 admission and unsupported tools fail closed before execution"
        [ test "pipeline, stage and nested parallel selections refuse before effects or workspace preparation" {
              for label, source, scope in
                  [ "pipeline", top, "pipeline"
                    "stage", stage, "stage 'a'"
                    "nested parallel stage", nestedParallel, "stage 'branch'" ] do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s unexpectedly executed: %A" label trace
                      | Error why ->
                          Expect.stringStarts why "unsupported_tools:" $"{label}: stable named reason"
                          Expect.stringContains why scope $"{label}: offending scope named"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: fresh-run wipe was not reached"

                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt")))
                          $"{label}: executor effect was not reached")
          }

          test "duplicate tools sections refuse before effects or workspace preparation" {
              // The first section is deliberately empty: the former tryPick path
              // retained it and hid the later selection from the execution preflight.
              for label, source in
                  [ "pipeline", duplicateTop
                    "stage", duplicateStage
                    "nested parallel stage", duplicateNestedParallel ] do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s duplicate tools unexpectedly executed: %A" label trace
                      | Error why ->
                          Expect.stringContains why "malformed_syntax" $"{label}: Jenkins-matched admission refusal"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: fresh-run wipe was not reached"

                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt")))
                          $"{label}: executor effect was not reached")
          }

          test "duplicate labelled structural sections refuse before effects or workspace preparation" {
              // Each first section is deliberately empty and labelled. Before the
              // duplicate guard, the first-match projection could hide the later
              // non-empty body. A parser refusal must precede both workspace wipe
              // and every shell effect, at all structural scopes this slice admits.
              for label, source in duplicateStructuralSections do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s duplicate structural section unexpectedly executed: %A" label trace
                      | Error why ->
                          Expect.stringContains why "malformed_syntax" $"{label}: Jenkins-matched admission refusal"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: fresh-run wipe was not reached"

                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt")))
                          $"{label}: executor effect was not reached")
          }

          test "competing stage body kinds refuse before effects or workspace preparation" {
              // A stage body is a single tagged choice in Jenkins, not four
              // independently optional projections. These plants put approval and
              // shell effects in bodies that the former first-match construction
              // could discard or run. Recursive stages use the same admission guard.
              for label, source in competingStageBodies do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" source with
                      | Ok trace -> failtestf "%s competing bodies unexpectedly executed: %A" label trace
                      | Error why ->
                          Expect.stringContains why "malformed_syntax" $"{label}: Jenkins-matched admission refusal"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: fresh-run wipe was not reached"

                      for file in [ "ran.txt"; "parallel.txt"; "matrix.txt" ] do
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{label}: {file} effect was not reached")
          }

          test "empty tools sections are not refused" {
              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" empty with
                  | Error why -> failtestf "empty tools changed execution: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "ordinary execution remains available"

                  Expect.isTrue
                      (IO.File.Exists(IO.Path.Combine(workspace, "ran.txt")))
                      "the control reached its shell effect")
          } ]

let spreadAssignmentPreflight =
    let pipelineWithBody body =
        "pipeline { agent any stages { stage('probe') { steps { script { "
        + "def rows = [[name: 'a'], [name: 'b']]; "
        + body
        + " } } } } }"

    let expectNamedRefusal label source =
        match FogellSide.preflightExecution source with
        | Ok _ -> failtestf "%s unexpectedly passed execution preflight" label
        | Error why ->
            Expect.equal
                why
                Fogell.Groovy.Interpreter.Interpreter.spreadAssignmentRefusal
                $"{label}: exact stable reason"

    let expectSpreadIndexAccepted label source =
        match FogellSide.preflightExecution source with
        | Ok _ -> ()
        | Error why -> failtestf "%s unexpectedly failed execution preflight: %s" label why

    let expectPreambleAnalysisRefusal label source =
        match FogellSide.preflightExecution source with
        | Ok _ -> failtestf "%s unexpectedly passed execution preflight" label
        | Error why ->
            Expect.equal
                why
                FogellSide.preambleAnalysisRefusal
                $"{label}: exact stable preamble-analysis reason"

    let expectEpilogueRefusal label source =
        match FogellSide.preflightExecution source with
        | Ok _ -> failtestf "%s unexpectedly passed execution preflight" label
        | Error why ->
            Expect.equal
                why
                FogellSide.epilogueRefusal
                $"{label}: exact stable epilogue reason"

    let withWorkspace (f: string -> string -> unit) =
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-spread-assignment-preflight-" + Guid.NewGuid().ToString("N"))
        let job = "job"
        let workspace = IO.Path.Combine(root, job)
        IO.Directory.CreateDirectory(workspace) |> ignore
        let sentinel = IO.Path.Combine(workspace, "sentinel.txt")
        IO.File.WriteAllText(sentinel, "keep")

        try
            f root workspace
        finally
            if IO.Directory.Exists root then
                IO.Directory.Delete(root, true)

    testList
        "FG-015 spread assignment fails closed before execution"
        [ test "plain, compound, increment, decrement and wrapped targets share one refusal" {
              for label, statement in
                  [ "plain", "rows*.name = 'x'"
                    "compound", "rows*.name += 'x'"
                    "increment", "rows*.name++"
                    "decrement", "rows*.name--"
                    "property wrapper", "rows*.child.name = 'x'"
                    "safe wrapper", "rows*.child?.name = 'x'" ] do
                  expectNamedRefusal label (pipelineWithBody statement)
          }

          test "direct projected indexes pass preflight across assignment forms and executable locations" {
              for label, statement in
                  [ "plain", "rows*.child[0] = 'x'"
                    "compound", "rows*.child[0] += 'x'"
                    "increment", "rows*.count[0]++"
                    "decrement", "rows*.count[0]--"
                    "nested index", "rows*.children[0][1] = 'x'" ] do
                  expectSpreadIndexAccepted label (pipelineWithBody statement)

              let preamble =
                  "def rows = [[child: [name: 'a']]]; rows*.child[0] = [name: 'x']\n"
                  + "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }"

              expectSpreadIndexAccepted "preamble" preamble

              let epilogue =
                  "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }\n"
                  + "def rows = [[child: [name: 'a']]]; rows*.child[0] = [name: 'x']\n"

              expectEpilogueRefusal "epilogue remains outside executable source semantics" epilogue

              expectSpreadIndexAccepted
                  "nested closure"
                  (pipelineWithBody "def later = { rows*.child[0] = [name: 'x'] }; echo 'never'")

              let whenBody =
                  "pipeline { agent any stages { stage('probe') { when { expression { "
                  + "def rows = [[child: [name: 'a']]]; rows*.child[0] = [name: 'x']; true } } "
                  + "steps { echo 'never' } } } }"

              expectSpreadIndexAccepted "when expression" whenBody
          }

          test "direct projected-index execution reaches ordinary earlier, later and post effects" {
              let source =
                  "pipeline { agent any stages { "
                  + "stage('before') { steps { sh 'touch before-index.txt' } } "
                  + "stage('bad') { steps { script { "
                  + "def rows = [[children: [[name: 'a']]]]; "
                  + "rows*.children.first()[0] = [name: 'x'] "
                  + "} sh 'touch after-index.txt' } } "
                  + "} post { always { sh 'touch post-index.txt' } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Ok trace -> Expect.equal trace.Result "success" "the projected-index write executes"
                  | Error why -> failtestf "projected-index assignment unexpectedly failed: %s" why

                  Expect.isFalse
                      (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                      "ordinary execution reaches workspace preparation"

                  for file in [ "before-index.txt"; "after-index.txt"; "post-index.txt" ] do
                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, file)))
                          $"{file} effect was reached exactly through ordinary execution")
          }

          test "nested closure, helper, post and nested-stage locations cannot hide the target" {
              let closure = pipelineWithBody "def later = { rows*.name = 'x' }; echo 'never'"
              expectNamedRefusal "closure" closure

              let helper =
                  "def change(rows) { rows*.name = 'x' }\n"
                  + "pipeline { agent any stages { stage('probe') { steps { script { "
                  + "change([[name: 'a']]) } } } } }"

              expectNamedRefusal "preamble helper" helper

              let preambleWithoutScriptBody =
                  "def rows = [[name: 'a']]; rows*.name = 'x'\n"
                  + "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }"

              expectNamedRefusal "preamble without script body" preambleWithoutScriptBody

              let locations =
                  [ "stage post",
                    "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } "
                    + "post { always { script { def rows = [[name: 'a']]; rows*.name = 'x' } } } } } }"
                    "pipeline post",
                    "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } "
                    + "post { always { script { def rows = [[name: 'a']]; rows*.name = 'x' } } } }"
                    "nested stage",
                    "pipeline { agent any stages { stage('outer') { stages { stage('inner') { steps { "
                    + "script { def rows = [[name: 'a']]; rows*.name = 'x' } } } } } } }"
                    "nested step block",
                    "pipeline { agent any stages { stage('probe') { steps { timeout(time: 1, unit: 'MINUTES') { "
                    + "script { def rows = [[name: 'a']]; rows*.name = 'x' } } } } } }" ]

              for label, source in locations do
                  expectNamedRefusal label source
          }

          test "an unparsable nonblank preamble fails closed instead of proving no spread write" {
              let ordinaryPipeline =
                  "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }"

              let defaultHelper = "def helper(x = true) { x }\n"

              let hiddenSpread =
                  defaultHelper
                  + "def rows = [[name: 'a']]; rows*.name = 'x'\n"
                  + ordinaryPipeline

              expectPreambleAnalysisRefusal "default helper then spread write" hiddenSpread
              expectPreambleAnalysisRefusal "default helper alone" (defaultHelper + ordinaryPipeline)

              let caught =
                  defaultHelper
                  + "try { def rows = [[name: 'a']]; rows*.name = 'x' } catch (Throwable e) { }\n"
                  + ordinaryPipeline

              expectPreambleAnalysisRefusal "caught top-level spread write" caught

              let nestedClosure =
                  "def helper(x = true) { def later = { def rows = [[name: 'a']]; rows*.name = 'x' }; later() }\n"
                  + ordinaryPipeline

              expectPreambleAnalysisRefusal "spread write nested in default-parameter helper" nestedClosure

              // The diagnostic is a stable capability name, not a reflection of parser
              // internals or user-controlled preamble text.
              let alternate = "def secretMarker(value = 'DO_NOT_REFLECT') { value }\n" + ordinaryPipeline
              expectPreambleAnalysisRefusal "diagnostic nonreflection" alternate

              match FogellSide.preflightExecution alternate with
              | Ok _ -> failtest "alternate preamble unexpectedly passed"
              | Error why ->
                  Expect.isFalse (why.Contains "DO_NOT_REFLECT") "refusal does not echo source text"
                  Expect.isFalse (why.Contains "secretMarker") "refusal does not echo identifiers"
          }

          test "unparsable preambles block earlier stages, post blocks and workspace preparation" {
              let source =
                  "def helper(x = true) { x }\n"
                  + "def rows = [[name: 'a']]; rows*.name = 'x'\n"
                  + "pipeline { agent any stages { "
                  + "stage('before') { steps { sh 'touch before-preamble.txt' } } "
                  + "stage('after') { steps { sh 'touch after-preamble.txt' } } "
                  + "} post { always { sh 'touch preamble-post.txt' } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Ok trace -> failtestf "unparsable preamble unexpectedly executed: %A" trace
                  | Error why ->
                      Expect.equal why FogellSide.preambleAnalysisRefusal "stable fail-closed reason"

                  Expect.isTrue
                      (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                      "workspace preparation was not reached"

                  for file in [ "before-preamble.txt"; "after-preamble.txt"; "preamble-post.txt" ] do
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, file)))
                          $"{file} effect was not reached")
          }

          test "blank and fully analyzable preambles remain admitted" {
              let ordinaryPipeline =
                  "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }"

              for label, source in
                  [ "blank", ordinaryPipeline
                    "whitespace", "  \n\t" + ordinaryPipeline
                    "parsed helper", "def helper(x) { x }\n" + ordinaryPipeline ] do
                  match FogellSide.preflightExecution source with
                  | Error why -> failtestf "%s preamble was over-refused: %s" label why
                  | Ok _ -> ()
          }

          test "a trailing spread write uses the shared spread-assignment refusal" {
              let source =
                  "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }\n"
                  + "def rows = [[name: 'a']]; rows*.name = 'x'\n"

              expectNamedRefusal "trailing spread assignment" source

              let nested =
                  "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }\n"
                  + "def change(rows) { try { rows*.name = 'x' } catch (Throwable e) { } }\n"

              expectNamedRefusal "spread assignment nested in trailing helper and catch" nested
          }

          test "parsed and unparsable nontrivial epilogues share one conservative refusal" {
              let pipeline =
                  "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }\n"

              let sources =
                  [ "parsed helper and call", "def trailing(v) { echo v }; trailing('ok')\n"
                    "default-parameter helper", "def trailing(v = 'ok') { echo v }; trailing()\n"
                    "ordinary top-level call", "echo 'tail'\n" ]

              for label, tail in sources do
                  expectEpilogueRefusal label (pipeline + tail)
          }

          test "epilogue comments and whitespace are parsed as empty and remain admitted" {
              let pipeline =
                  "pipeline { agent any stages { stage('probe') { steps { echo 'ordinary' } } } }"

              for label, tail in
                  [ "empty", ""
                    "whitespace", "  \n\t\r\n"
                    "line comment", "\n// trailing } comment\n"
                    "block comment", "\n/* trailing { pipeline { } } */\n  " ] do
                  match FogellSide.preflightExecution (pipeline + tail) with
                  | Error why -> failtestf "%s epilogue was over-refused: %s" label why
                  | Ok _ -> ()
          }

          test "trailing-source refusals block earlier stages, post and workspace preparation" {
              let pipeline =
                  "pipeline { agent any stages { "
                  + "stage('before') { steps { sh 'touch before-epilogue.txt' } } "
                  + "stage('after') { steps { sh 'touch after-epilogue.txt' } } "
                  + "} post { always { sh 'touch epilogue-post.txt' } } }\n"

              for label, tail, expected in
                  [ "spread", "def rows = [[name: 'a']]; rows*.name = 'x'\n",
                    Fogell.Groovy.Interpreter.Interpreter.spreadAssignmentRefusal
                    "unmodelled", "def trailing(v) { echo v }; trailing('ok')\n",
                    FogellSide.epilogueRefusal
                    "unparsable", "def trailing(v = 'ok') { echo v }; trailing()\n",
                    FogellSide.epilogueRefusal ] do
                  withWorkspace (fun root workspace ->
                      match FogellSide.run [] root "job" (pipeline + tail) with
                      | Ok trace -> failtestf "%s trailing source unexpectedly executed: %A" label trace
                      | Error why -> Expect.equal why expected $"{label}: stable refusal"

                      Expect.isTrue
                          (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                          $"{label}: workspace preparation was not reached"

                      for file in [ "before-epilogue.txt"; "after-epilogue.txt"; "epilogue-post.txt" ] do
                          Expect.isFalse
                              (IO.File.Exists(IO.Path.Combine(workspace, file)))
                              $"{label}: {file} effect was not reached")
          }
          test "when expressions in top, nested and parallel stages cannot hide the target" {
              let assignment = "def rows = [[name: 'a']]; rows*.name = 'x'; true"

              let sources =
                  [ "top allOf false sibling",
                    "pipeline { agent any stages { stage('probe') { when { allOf { "
                    + "expression { false } expression { " + assignment + " } } } "
                    + "steps { echo 'never' } } } }"
                    "nested anyOf",
                    "pipeline { agent any stages { stage('outer') { stages { stage('inner') { "
                    + "when { anyOf { branch 'never'; expression { " + assignment + " } } } "
                    + "steps { echo 'never' } } } } } }"
                    "parallel not",
                    "pipeline { agent any stages { stage('fanout') { parallel { "
                    + "stage('left') { steps { echo 'ordinary' } } "
                    + "stage('right') { when { not { expression { " + assignment + " } } } "
                    + "steps { echo 'never' } } } } } }" ]

              for label, source in sources do
                  expectNamedRefusal label source
          }

          test "a false when sibling cannot hide the refusal or permit earlier effects" {
              let source =
                  "pipeline { agent any stages { "
                  + "stage('before') { steps { sh 'touch before-when.txt' } } "
                  + "stage('guarded') { when { allOf { expression { false } expression { "
                  + "def rows = [[name: 'a']]; rows*.name = 'x'; true } } } "
                  + "steps { sh 'touch guarded.txt' } } "
                  + "} post { always { sh 'touch when-post.txt' } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Ok trace -> failtestf "when spread assignment unexpectedly executed: %A" trace
                  | Error why ->
                      Expect.equal
                          why
                          Fogell.Groovy.Interpreter.Interpreter.spreadAssignmentRefusal
                          "stable named refusal"

                  Expect.isTrue
                      (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                      "workspace preparation was not reached"

                  for file in [ "before-when.txt"; "guarded.txt"; "when-post.txt" ] do
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, file)))
                          $"{file} effect was not reached")
          }


          test "workspace preparation, RHS, later steps and post are all blocked" {
              let source =
                  "pipeline { agent any stages { "
                  + "stage('before') { steps { sh 'touch before.txt' } } "
                  + "stage('bad') { steps { script { def rows = [[name: 'a']]; "
                  + "rows*.name = sh 'touch rhs.txt' } sh 'touch after.txt' } } "
                  + "} post { always { sh 'touch post.txt' } } }"

              withWorkspace (fun root workspace ->
                  match FogellSide.run [] root "job" source with
                  | Ok trace -> failtestf "spread assignment unexpectedly executed: %A" trace
                  | Error why ->
                      Expect.equal
                          why
                          Fogell.Groovy.Interpreter.Interpreter.spreadAssignmentRefusal
                          "stable named refusal"

                  Expect.isTrue
                      (IO.File.Exists(IO.Path.Combine(workspace, "sentinel.txt")))
                      "fresh-workspace wipe was not reached"

                  for file in [ "before.txt"; "rhs.txt"; "after.txt"; "post.txt" ] do
                      Expect.isFalse
                          (IO.File.Exists(IO.Path.Combine(workspace, file)))
                          $"{file} effect was not reached")
          }

          test "catching Jenkins runtime exceptions is an explicit unsupported boundary" {
              let source =
                  pipelineWithBody
                      "try { rows*.name = 'x' } catch (Throwable e) { echo 'caught' }; echo 'after'"

              expectNamedRefusal "caught form" source
          }

          test "spread reads and ordinary assignments remain executable" {
              match FogellSide.preflightExecution (pipelineWithBody "def names = rows*.name; rows[0].name = 'x'") with
              | Error why -> failtestf "read-only spread or ordinary write was over-refused: %s" why
              | Ok _ -> ()
          }

          test "outer writes on spread-index results remain admitted" {
              for label, statement in
                  [ "property", "rows*.child[0].name = 'x'"
                    "safe property", "rows*.child[0]?.name = 'x'"
                    "compound property", "rows*.child[0].count += 1"
                    "increment property", "rows*.child[0].count++"
                    "decrement property", "rows*.child[0].count--"
                    "nested index then property", "rows*.children[0][1].name = 'x'"
                    "index then method then property", "rows*.children[0].first().name = 'x'" ] do
                  let source =
                      "pipeline { agent any stages { stage('probe') { steps { script { "
                      + "def rows = [[child: [name: 'a', count: 1], children: [[name: 'a0'], [name: 'a1']]]]; "
                      + statement
                      + " } } } } }"

                  match FogellSide.preflightExecution source with
                  | Error why -> failtestf "%s outer write was over-refused: %s" label why
                  | Ok _ -> ()
          }

          test "spread reads in call inputs, index keys and method receivers remain outside the write refusal" {
              for label, statement in
                  [ "positional call argument", "foo(rows*.name).bar = 1"
                    "named call argument", "foo(values: rows*.name).bar = 1"
                    "trailing closure read", "holder.foo { rows*.name }.bar = 1"
                    "index key", "xs[rows*.name[0]] = 'x'"
                    "method result property", "rows*.child.first().name = 'x'"
                    "method result safe property", "rows*.child.first()?.name = 'x'"
                    "safe-method result safe property", "rows*.child?.first()?.name = 'x'"
                    "named method result", "rows*.child.find(index: 0).name = 'x'"
                    "trailing method result", "rows*.child.find { true }.name = 'x'" ] do
                  match FogellSide.preflightExecution (pipelineWithBody statement) with
                  | Error why -> failtestf "%s spread read was over-refused: %s" label why
                  | Ok _ -> ()

              for label, statement in
                  [ "direct property wrapper", "rows*.child.name = 'x'"
                    "direct safe wrapper", "rows*.child?.name = 'x'"
                    "new spread after method", "rows*.child.first()*.name = 'x'" ] do
                  expectNamedRefusal label (pipelineWithBody statement)

              expectSpreadIndexAccepted "direct index wrapper" (pipelineWithBody "rows*.child[0] = 'x'")

              for label, statement in
                  [ "method-result list index", "rows*.children.first()[0] = [name: 'x']"
                    "safe-method-result list index", "rows*.children?.first()[0] = [name: 'x']"
                    "nested method-result list index", "rows*.children.first().first()[0] = [name: 'x']"
                    "method-result list compound", "rows*.counts.first()[0] += 1"
                    "method-result list increment", "rows*.counts.first()[0]++"
                    "method-result list decrement", "rows*.counts.first()[0]--"
                    "method-result ambiguous map index", "rows*.holder.first()['slot'] = 'x'" ] do
                  expectSpreadIndexAccepted label (pipelineWithBody statement)

              expectNamedRefusal
                  "actual assignment in method trailing closure"
                  (pipelineWithBody "holder.foo { rows*.name = 'x' }.bar = 1")
          } ]

let jenkinsBuildDataAttestation =
    testList
        "FG-177 Jenkins BuildData attestation"
        [ test "controller actions yield canonical distinct revisions only" {
              let a = String.replicate 40 "a"
              let b = String.replicate 40 "b"
              let body =
                  $"""{{"actions":[{{}},{{"lastBuiltRevision":{{"SHA1":"{b}"}}}},{{"lastBuiltRevision":{{"SHA1":"{a}"}}}},{{"lastBuiltRevision":{{"SHA1":"{b}"}}}},{{"lastBuiltRevision":{{"SHA1":"NOT-A-SHA"}}}}],"scriptText":"lastBuiltRevision SHA1 {String.replicate 40 "c"}"}}"""

              match Jenkins.parseBuildDataRevisions body with
              | Error why -> failtest why
              | Ok revisions -> Expect.equal revisions [ a; b ] "only structural controller actions attest"
          }

          test "missing or malformed API structure fails closed" {
              match Jenkins.parseBuildDataRevisions "not-json" with
              | Ok value -> failtestf "malformed JSON unexpectedly parsed: %A" value
              | Error why -> Expect.stringContains why "invalid Jenkins BuildData JSON" "parse error named"

              match Jenkins.parseBuildDataRevisions "{\"queueItem\":{}}" with
              | Ok value -> failtestf "missing actions unexpectedly parsed: %A" value
              | Error why -> Expect.stringContains why "no actions array" "missing structure named"
          }

          test "SCM evidence jobs use a full definition checkout" {
              let spec = { Url = "file:///fixture.git"; Branch = "fogell-pins/" + String.replicate 40 "a" }
              Expect.stringContains
                  (Jenkins.scmJobXml true spec)
                  "<lightweight>false</lightweight>"
                  "the attestation lane records the definition checkout before Pipeline code"
              Expect.stringContains
                  (Jenkins.scmJobXml false spec)
                  "<lightweight>true</lightweight>"
                  "ordinary differential runs retain lightweight retrieval"
          }

          test "definition revision comes only from the pre-Pipeline checkout" {
              let definitionRevision = String.replicate 40 "a"
              let laterCheckout = String.replicate 40 "b"
              let console =
                  $"""Started by user harness
Checking out Revision {definitionRevision} (refs/remotes/origin/pin)
[Pipeline] Start of Pipeline
Checking out Revision {laterCheckout} (refs/remotes/origin/pin)
script says Checking out Revision {laterCheckout} (spoof)
"""

              match Jenkins.parseScmDefinitionRevision console with
              | Error why -> failtest why
              | Ok revision ->
                  Expect.equal revision definitionRevision "later checkout and script text cannot replace the definition identity"
          }

          test "missing or conflicting pre-Pipeline definition revisions fail closed" {
              let a = String.replicate 40 "a"
              let b = String.replicate 40 "b"
              match Jenkins.parseScmDefinitionRevision "[Pipeline] Start of Pipeline\n" with
              | Ok revision -> failtestf "missing definition revision unexpectedly parsed: %s" revision
              | Error why -> Expect.stringContains why "no pre-Pipeline" "missing checkout named"

              match
                  Jenkins.parseScmDefinitionRevision(
                      $"Checking out Revision {a} (a)\nChecking out Revision {b} (b)\n[Pipeline] Start of Pipeline\n"
                  )
              with
              | Ok revision -> failtestf "conflicting definitions unexpectedly parsed: %s" revision
              | Error why -> Expect.stringContains why "multiple pre-Pipeline" "ambiguous checkout named"
          }

          test "SCM attestation URLs encode safe spaces and make unsafe components opaque" {
              let spaced = WalkerGit.attestationUrl "file:///tmp/fixture repo.git"
              Expect.equal spaced "file:///tmp/fixture%20repo.git" "spaces use a single URI encoding"

              let secret = "round26-" + "planted-secret"
              let assertOpaque label url =
                  let attested = WalkerGit.attestationUrl url
                  Expect.stringStarts attested "sha256:" $"{label} is represented opaquely"
                  Expect.equal attested.Length 71 $"{label} has exactly one SHA-256 identity"
                  Expect.isFalse (attested.Contains secret) $"{label} secret is absent"

              assertOpaque "userinfo" $"https://user:{secret}@example.test/repo.git"
              assertOpaque "raw query" $"https://example.test/repo.git?access_token={secret}"

              let encodedSecret =
                  secret
                  |> System.Text.Encoding.UTF8.GetBytes
                  |> Array.map (sprintf "%%%02X")
                  |> String.concat ""

              assertOpaque
                  "encoded query"
                  $"https://example.test/repo.git?%%61ccess_token={encodedSecret}"
              assertOpaque "raw fragment" $"https://example.test/repo.git#access_token={secret}"
              assertOpaque
                  "encoded fragment"
                  $"https://example.test/repo.git#%%61ccess_token={encodedSecret}"
              assertOpaque "semicolon parameter" $"https://example.test/repo.git;access_token={secret}"
              assertOpaque
                  "encoded semicolon parameter"
                  $"https://example.test/repo.git%%3Baccess_token={encodedSecret}"
              assertOpaque
                  "encoded query delimiter"
                  $"https://example.test/repo.git%%3Faccess_token={encodedSecret}"
              assertOpaque "malformed URI" $"https://[broken/{secret}"
              assertOpaque "malformed percent path" $"https://example.test/repo.git%%ZZ{secret}"
              assertOpaque "invalid UTF-8 path" $"https://example.test/repo.git/%%C3%%28{secret}"

              let opaque = WalkerGit.attestationUrl "git@example.test:repo.git"
              Expect.stringStarts opaque "sha256:" "non-URI remotes are represented opaquely"
              Expect.isFalse (opaque.Contains "git@example") "opaque spelling is absent"
          } ]

[<EntryPoint>]
let main argv =

    runTestsWithCLIArgs
        []
        argv
        (testList
            "Fogell.Differential"
            [ hostedSignatures
              stepDescriptorValidation
              genuineNullRuntime
              scmReturnMapRuntime
              jenkinsBuildDataAttestation
              unsupportedNamedCollections
              unsupportedDeclarativeAgents
              unsupportedDeclarativeTools
              spreadAssignmentPreflight
              userOutputSurvives
              stringModel
              sealBindsCaseSource
              caseSnapshotIsOneRead
              silenceIsPerEngine
              concurrentSealIsOrderStable
              concurrentFoldAccounting
              continuationResolution
              returnFlagContract
              timestampPrefixIsConditional ])
