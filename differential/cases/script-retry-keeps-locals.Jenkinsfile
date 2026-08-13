// FG-178. A hosted body is re-invoked as a CLOSURE, so mutations to captured locals
// must survive between invocations.
//
// MEASURED before the fix, and it INVERTS THE BUILD RESULT: Jenkins SUCCEEDS — attempt 1
// fails with n = 1, attempt 2 passes with n = 2 — while Fogell FAILED, because the thunk
// discarded the environment `execBlock` returns and n was 1 on every attempt. A retry
// that cannot make progress is not a retry; it is the same command twice.
//
// `attempts.txt` APPENDS, so the count lands in the workspace hash: an engine that ran
// the body once, or twice without advancing n, diverges there rather than passing
// quietly. `done.txt` proves the block completed rather than exhausting its attempts.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def n = 0
                    retry(2) {
                        n = n + 1
                        sh 'echo attempt >> attempts.txt'
                        if (n == 1) {
                            sh 'exit 1'
                        }
                    }
                    sh 'echo done > done.txt'
                }
            }
        }
    }
}
