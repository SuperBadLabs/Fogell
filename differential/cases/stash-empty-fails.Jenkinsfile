// FG-047 review fix, PR #15. MEASURED: with the default `allowEmpty: false`, a stash
// matching NO files FAILS the build — `after.txt` is never created because the pipeline
// stops there. Reporting success would let the build continue having silently lost the
// inputs it asked for, and a later `unstash` would succeed with nothing.
pipeline {
    agent any
    stages {
        stage('a') {
            steps {
                sh 'echo hi > present.txt'
                stash name: 'nothing', includes: 'missing/**'
                sh 'echo after-stash > after.txt'
            }
        }
    }
}
