module Fogell.Differential.Tests

open Expecto
open Fogell.Differential

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

          test "`Terminated` survives unless an interrupt was just narrated" {
              Expect.contains (Trace.normaliseOutput [ "Terminated" ]) "Terminated" "on its own it is output"

              Expect.isEmpty
                  (Trace.normaliseOutput [ "Sending interrupt signal to process"; "Terminated" ])
                  "after an interrupt it is narration"
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "Fogell.Differential" [ userOutputSurvives ])
