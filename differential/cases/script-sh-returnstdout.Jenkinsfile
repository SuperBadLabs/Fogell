// FG-174. `sh(returnStdout: true)` returns the program's stdout, and CAPTURES it —
// Jenkins' console shows the xtrace and not the output, because durable-task calls
// `captureOutput()`.
//
// THIRD ATTEMPT AT THIS, and the two failures are why the case looks like this. Both
// earlier attempts lifted the refusal, measured, and put it back because the value
// would have been WRONG rather than absent: Fogell echoed what Jenkins captures, and
// the returned text carried the `sh -x` trace, so `out` was "+ printf value\nvalue".
// The trace was never `sh`'s doing — the process wrapper merged stderr into stdout
// with `2>&1`, and it now does that only when stdout is not being captured.
//
// THE VALUE IS BYTES, NOT LINES, and this case exists in this shape because the first
// version of it FOUND THAT DEFECT. Jenkins returns stdout verbatim: `echo value` yields
// "value\n" and `printf value` yields "value" with no terminator. Fogell reassembled the
// captured text from line events through `AppendLine`, so BOTH came back as "value\n" —
// a value wrong by one byte, which `.trim()` hides in most pipelines and which nothing
// downstream would ever report. Measured: Jenkins printed `raw:[value]` on ONE line
// where Fogell printed two.
//
// So both shapes are asserted, and the difference between them is the whole point:
//   - `withnl:[...]` spans TWO output lines — the trailing newline Jenkins keeps, and
//     the reason pipelines call `.trim()`. An engine that stripped it prints one line.
//   - `nonl:[...]` stays on ONE line. An engine that re-terminates lines prints two.
//     A case asserting only the first shape passes that engine, which is exactly how
//     this got as far as a differential run.
//   - `trimmed:[value]` is the ordinary reading, and pins the value itself.
//   - `captured.txt` puts the value in the WORKSPACE HASH. Output alone would accept an
//     engine that echoed the right text while handing the script something else.
// The console must also NOT contain a bare `value` line: capture means the output does
// not reach the log, and an engine that streams it diverges on output.
pipeline {
    agent any
    stages {
        stage('Capture') {
            steps {
                script {
                    def withnl = sh(script: 'echo value', returnStdout: true)
                    def nonl = sh(script: 'printf value', returnStdout: true)
                    echo "withnl:[${withnl}]"
                    echo "nonl:[${nonl}]"
                    echo "trimmed:[${withnl.trim()}]"
                    sh "echo captured=${nonl} > captured.txt"
                }
            }
        }
    }
}
