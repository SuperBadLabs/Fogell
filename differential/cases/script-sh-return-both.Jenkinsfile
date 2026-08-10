// FG-174. `returnStdout` AND `returnStatus` TOGETHER: the flags are not orthogonal, and
// `returnStatus` wins. Jenkins returns the exit code as a Groovy Integer and the build
// continues.
//
// FOUND BY THE PRE-PUSH VERIFIER, which ran it on a disposable 2.568.1 container rather
// than reasoning about it. Fogell returned the STDOUT, so `code == 7` compared a String
// to an Integer, went quietly false, and the guarded step never ran — while the build
// reported SUCCESS. That is a false success whose only evidence is WORK THAT DID NOT
// HAPPEN: comparing terminal result or console output cannot see it, because a step that
// was skipped leaves nothing behind to differ on. Only the WORKSPACE shows it.
//
// So the assertions are files, deliberately:
//   - `code.txt` records the value through the Integer comparison. An engine returning
//     the captured stdout writes `wrong`, and the build still says success.
//   - `after.txt` proves execution continued past a non-zero exit, which is the whole
//     point of asking for a status instead of a failure.
// The third assertion is the CONSOLE: `returnStdout` was requested, so durable-task
// captures — the output must NOT appear in the log even though the status is what came
// back. That is why the shell prints something as well as exiting non-zero.
pipeline {
    agent any
    stages {
        stage('Both') {
            steps {
                script {
                    def code = sh(script: 'printf captured; exit 7', returnStdout: true, returnStatus: true)
                    if (code == 7) {
                        sh 'echo integer-seven > code.txt'
                    } else {
                        sh 'echo wrong > code.txt'
                    }
                    sh 'echo reached > after.txt'
                }
            }
        }
    }
}
