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
        [ test "no flags means no value" {
              Expect.equal (contract "sh" false false) WalkerRules.NoValue "a plain sh answers nothing"
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
              for step in [ "echo"; "error"; "dir"; "withEnv" ] do
                  Expect.equal (contract step true true) WalkerRules.NoValue $"{step} answers nothing"
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
          // `hostedSignatureError` ends in a `| _ -> None` catch-all, so a hosted wrapper
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
              Expect.isSome ((s "withEnv").Check [ Fogell.Groovy.Interpreter.VList [ Fogell.Groovy.Interpreter.VStr "BADENTRY" ] ] []) "an entry without = refuses"
              Expect.isNone ((s "withEnv").Check [ Fogell.Groovy.Interpreter.VList [ Fogell.Groovy.Interpreter.VStr "A=1" ] ] []) "NAME=VALUE passes"
              // dir: exactly one positional
              Expect.isSome ((s "dir").Check [] []) "dir with nothing refuses"
              // timeout: types deliberately unchecked
              Expect.isNone ((s "timeout").Check [ Fogell.Groovy.Interpreter.VStr "weird" ] []) "timeout's argument types are deliberately unchecked"
          } ]

[<EntryPoint>]
let main argv =

    runTestsWithCLIArgs
        []
        argv
        (testList
            "Fogell.Differential"
            [ hostedSignatures
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
