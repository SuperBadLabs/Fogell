// FG-101 acceptance. The CAUSE of a cancellation decides both the terminal result and
// which `post` arm runs. A deadline is an ABORT; a failFast sibling is a FAILURE, because
// the sibling's failure is the cause and this step's interruption is collateral.
//
// This pair is the invariant the model exists to hold. Its partner,
// `input-failfast-is-failure`, proves the sibling half; this proves the deadline half —
// `aborted.txt` must exist and `wrong.txt` must not.
pipeline {
    agent any
    stages {
        stage('Bounded') {
            options {
                timeout(time: 4, unit: 'SECONDS')
            }
            steps {
                sh 'echo started > started.txt; sleep 60'
            }
            post {
                aborted {
                    sh 'echo right > aborted.txt'
                }
                failure {
                    sh 'echo wrong > wrong.txt'
                }
            }
        }
    }
}
