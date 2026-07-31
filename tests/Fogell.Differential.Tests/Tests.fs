module Fogell.Differential.Tests

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

          test "the secret-interpolation warning is normalised for ANY step, not just echo" {
              // FG-100. The receipt `sh-secret-interpolation-warning` CANNOT prove this.
              // Both engines' warnings are normalised away, so that case stays PROVEN even
              // if Fogell says nothing at all — a receipt that passes vacuously is exactly
              // what this harness exists to prevent. The emission is asserted here instead.
              //
              // The risk is specific: the recogniser used to pin the step name to `echo`.
              // The moment `sh` began warning, a CORRECT new warning would have surfaced as
              // a receipt divergence — a fix presenting as a regression.
              for step in [ "echo"; "sh"; "input" ] do
                  Expect.isEmpty
                      (Trace.normaliseOutput
                          [ $"WARNING: a secret was interpolated into `{step}` via a Groovy string: TOKEN" ])
                      $"the {step} warning is engine narration"

              // Not a blanket prefix: the shape has to be the whole thing, so a build that
              // happens to print the opening words is still compared as output.
              Expect.contains
                  (Trace.normaliseOutput [ "WARNING: a secret was interpolated into the report" ])
                  "WARNING: a secret was interpolated into the report"
                  "a build saying something similar is still user output"
          }

          test "`Terminated` survives unless an interrupt was just narrated" {
              Expect.contains (Trace.normaliseOutput [ "Terminated" ]) "Terminated" "on its own it is output"

              Expect.isEmpty
                  (Trace.normaliseOutput [ "Sending interrupt signal to process"; "Terminated" ])
                  "after an interrupt it is narration"
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
                      step [ "m", "x" ] [] [] [] [] [ "m", "\u0000TARGET is ${TARGET}" ], "m", "x", "$TARGET is production" ]

              for (why, st, key, raw, expected) in cases do
                  Expect.equal (GString.render env st key raw) expected why
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "Fogell.Differential" [ userOutputSurvives; stringModel ])
