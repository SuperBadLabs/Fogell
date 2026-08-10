// FG-174. `returnStatus: true` converts a SHELL EXIT into a value instead of a build
// failure. It says nothing about a WRAPPER INTERRUPTION — a timeout is the engine
// stopping the step from outside, not the script's answer to anything.
//
// FOUND BY THE PRE-PUSH VERIFIER, which ran it rather than reasoning about it: the
// suppression was keyed on the FLAG ALONE, so an aborted step took the same path as a
// non-zero exit. Fogell reported SUCCESS, wrote `after.txt`, and — worse — handed the
// script status 0 for a process it had just killed, because the code read
// `defaultArg result.ExitCode 0` and a timeout carries no exit code.
//
// A SAFETY BOUND DEFEATED, which ranks with a bypassed approval here, and the same
// hole `script-timeout-bound` was written to close: that case passed throughout only
// because its `sh` carried no flag.
//
// TWO ASSERTIONS, because the terminal result alone cannot tell these engines apart:
//   - `after.txt` must be ABSENT. An engine that swallowed the abort runs the next
//     step, and the workspace manifest is the only place that shows.
//   - `done.txt` must be ABSENT too, proving the bound HELD rather than being
//     announced and then waited out.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    timeout(time: 3, unit: 'SECONDS') {
                        sh script: 'sleep 30; echo never > done.txt', returnStatus: true
                    }
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
