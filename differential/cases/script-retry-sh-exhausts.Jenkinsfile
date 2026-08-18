// FG-186/FG-176 COMPOSED, the verifier's finding: a SHELL failure under retry
// travels a different path than a Groovy fault (observing sink, the sanctioned
// raise, the hosted thunk's finally, the loop's catch, the final re-raise) and
// tracing is not sealing — three markers, two Retrying lines, the build fails
// with the step's own diagnostic.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    retry(3) {
                        sh 'printf a >> attempts.txt; exit 1'
                    }
                }
            }
        }
    }
}
