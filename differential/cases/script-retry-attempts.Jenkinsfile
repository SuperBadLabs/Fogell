// FG-172. `retry(N)` inside a `script { }` runs N ATTEMPTS. This is the clearest proof
// that the body is handed over as a re-runnable THUNK: under the batch model the
// interpreter had already evaluated the closure once before the wrapper ever saw it, so
// N attempts were not expressible at all. The appended marker file makes the COUNT part
// of the workspace hash, so an engine that ran the body once and reported success would
// diverge on the manifest rather than pass quietly.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    retry(3) {
                        sh 'echo attempt >> attempts.txt; exit 1'
                    }
                }
            }
        }
    }
}
