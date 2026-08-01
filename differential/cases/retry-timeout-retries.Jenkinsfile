// FG-046b. A nested `timeout` expiring is a RETRYABLE failure: MEASURED on
// 2.568.1, `retry(3) { timeout { … } }` runs the body THREE times — three
// `+ echo attempt` rows, two `Retrying` lines — and the build ends ABORTED once
// the attempts are exhausted.
//
// This case exists because the opposite was asserted without measuring. A human
// REJECTING an `input` also aborts the attempt, and it must NOT be retried
// (asking someone who declined until they agree); the first fix for that read
// the aborted STATUS and so stopped retrying here too, silently costing every
// `retry { timeout { … } }` pipeline its remaining attempts. The attempt count
// is recorded in the workspace so the receipt proves THREE, not just the verdict.
pipeline {
    agent any
    stages {
        stage('Slow') {
            steps {
                retry(3) {
                    timeout(time: 3, unit: 'SECONDS') {
                        sh 'echo attempt >> attempts.txt; sleep 30'
                    }
                }
            }
        }
    }
}
