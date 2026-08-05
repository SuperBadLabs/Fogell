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

[<EntryPoint>]
let main argv =

    runTestsWithCLIArgs
        []
        argv
        (testList
            "Fogell.Differential"
            [ userOutputSurvives
              stringModel
              concurrentFoldAccounting
              continuationResolution
              timestampPrefixIsConditional ])
