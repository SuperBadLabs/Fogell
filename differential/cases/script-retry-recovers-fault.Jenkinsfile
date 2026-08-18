// FG-186 shape (2). A body that faults on attempt 1 and succeeds on attempt 2
// is a SUCCESS build with two attempt markers — a retry exists to absorb
// exactly this. MEASURED before the fix: Fogell failed with ONE marker, the
// fault escaping the loop before it could turn.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def n = 0
                    retry(3) {
                        n = n + 1
                        sh 'printf a >> attempts.txt'
                        if (n == 1) {
                            echo "${MISSING}"
                        }
                    }
                }
            }
        }
    }
}
