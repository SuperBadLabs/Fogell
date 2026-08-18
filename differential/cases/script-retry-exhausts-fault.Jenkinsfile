// FG-186 shape (1). A body that faults EVERY attempt runs all three — three
// markers on both engines — and the final fault fails the build with its own
// diagnostic, not a generic exhausted-retries one. MEASURED before the fix:
// Fogell held one marker.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    retry(3) {
                        sh 'printf a >> attempts.txt'
                        echo "${MISSING_VARIABLE}"
                    }
                }
            }
        }
    }
}
