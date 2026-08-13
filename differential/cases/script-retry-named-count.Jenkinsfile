// FG-174. `retry(count: 2)` is valid Jenkins, and refusing it was a FALSE REFUSAL —
// the opposite error to every other finding in this ticket, and the one `retry(0)`
// already taught once.
//
// The hosted signature arm accepted only a single POSITIONAL integer, while the
// ordinary stage-level reader (`WalkerRules.retryCountOpt`) has always accepted named
// `count:`. An arm stricter than the reader it guards is a refusal with no rule behind
// it: measured, Jenkins ran the body and succeeded where Fogell failed with an empty
// workspace. Raised in review on PR #53.
//
// The body APPENDS, so the marker file records the attempt COUNT and not merely that
// something ran — a `retry` that ran the body once and reported success would diverge on
// the workspace hash rather than passing quietly. The body succeeds first time, so the
// count must be exactly one line: this case pins the ACCEPTANCE of the spelling, and
// `script-retry-attempts` pins the retry behaviour itself.
pipeline {
    agent any
    stages {
        stage('Retry') {
            steps {
                script {
                    retry(count: 2) {
                        sh 'echo attempt >> attempts.txt'
                    }
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
