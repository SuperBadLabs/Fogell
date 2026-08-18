// FG-176. A failing SHELL step inside script{} is CATCHABLE, as Jenkins'
// AbortException is: the handler runs, the block continues, the build succeeds.
// MEASURED before the fix: Fogell failed this with an EMPTY workspace — the
// dispatch marked the branch failed where no try/catch could ever see it.
// Refusals stay uncatchable on purpose: recovering from a modelling gap while
// Jenkins ran the real step would be a silent divergence.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        sh 'exit 1'
                    } catch (e) {
                        sh 'echo recovered > recovered.txt'
                    }
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
