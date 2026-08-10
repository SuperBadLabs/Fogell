// FG-172. `retry(0)` inside a script: Jenkins SUCCEEDS and runs the body ONCE — the count
// is clamped, not rejected.
//
// This case exists because a review said Jenkins "rejects the count" for both retry(0) and
// retry('nope'), and that is only half true. Acting on it unmeasured made Fogell refuse a
// pipeline Jenkins runs — a FALSE REFUSAL, the opposite error to the false successes this
// ticket has been chasing, and still a divergence. Measured both shapes before settling:
// the non-integer IS rejected by Jenkins, the zero is not.
//
// Keeping it as a case means the next person tightening the signature validator finds out
// here rather than from a review round.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    retry(0) {
                        sh 'echo reached > out.txt'
                    }
                }
            }
        }
    }
}
