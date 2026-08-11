// FG-178. `echo()` with no message prints `null` and does NOT fail — and this case
// exists because the review finding that prompted it was WRONG about Jenkins.
//
// The report said Jenkins REJECTS the call and asked for a required-argument check.
// MEASURED instead: the pipeline SUCCEEDS on Jenkins, the following shell RUNS, and the
// console shows the literal `null` — Groovy stringifying a null message. Fogell already
// agreed on terminal result and workspace; it printed nothing where Jenkins printed one
// line.
//
// Implementing the finding as written would have REFUSED a pipeline Jenkins accepts. That
// is the second review claim on this branch to be materially wrong about Jenkins, and the
// second caught by probing before implementing rather than after.
//
// The assertion is the OUTPUT line, which is why `ran.txt` is here too: without a
// following step the case could not show that execution continued past the `echo`.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    echo()
                    sh 'echo ran > ran.txt'
                }
            }
        }
    }
}
