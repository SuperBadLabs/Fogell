// FG-110/FG-049b. The ADR 0005 four-build probe, replayed as ONE receipt-backed
// sequence: the job persists across these four builds, so `previous` is real
// history rather than always-None. Together the four receipts prove the entire
// measured selection table for changed/fixed/regression — including the two
// rows a single-build case can never reach (fixed, regression) and the row
// where `changed` must stay QUIET (build 3).
// build 1: FAILS with no history -> always, changed, failure, cleanup (changed fires on a first build)
pipeline {
    agent any
    stages {
        stage('Work') {
            steps {
                sh 'exit 5'
            }
            post {
                always { sh "echo POST-always" }
                changed { sh "echo POST-changed" }
                fixed { sh "echo POST-fixed" }
                regression { sh "echo POST-regression" }
                aborted { sh "echo POST-aborted" }
                failure { sh "echo POST-failure" }
                success { sh "echo POST-success" }
                unstable { sh "echo POST-unstable" }
                cleanup { sh "echo POST-cleanup" }
            }
        }
    }
}
//// NEXT BUILD ////
// build 2: SUCCEEDS after failure -> always, changed, fixed, success, cleanup
pipeline {
    agent any
    stages {
        stage('Work') {
            steps {
                sh 'echo recovering'
            }
            post {
                always { sh "echo POST-always" }
                changed { sh "echo POST-changed" }
                fixed { sh "echo POST-fixed" }
                regression { sh "echo POST-regression" }
                aborted { sh "echo POST-aborted" }
                failure { sh "echo POST-failure" }
                success { sh "echo POST-success" }
                unstable { sh "echo POST-unstable" }
                cleanup { sh "echo POST-cleanup" }
            }
        }
    }
}
//// NEXT BUILD ////
// build 3: SUCCEEDS after success -> always, success, cleanup (changed is QUIET)
pipeline {
    agent any
    stages {
        stage('Work') {
            steps {
                sh 'echo steady'
            }
            post {
                always { sh "echo POST-always" }
                changed { sh "echo POST-changed" }
                fixed { sh "echo POST-fixed" }
                regression { sh "echo POST-regression" }
                aborted { sh "echo POST-aborted" }
                failure { sh "echo POST-failure" }
                success { sh "echo POST-success" }
                unstable { sh "echo POST-unstable" }
                cleanup { sh "echo POST-cleanup" }
            }
        }
    }
}
//// NEXT BUILD ////
// build 4: FAILS after success -> always, changed, regression, failure, cleanup
pipeline {
    agent any
    stages {
        stage('Work') {
            steps {
                sh 'exit 7'
            }
            post {
                always { sh "echo POST-always" }
                changed { sh "echo POST-changed" }
                fixed { sh "echo POST-fixed" }
                regression { sh "echo POST-regression" }
                aborted { sh "echo POST-aborted" }
                failure { sh "echo POST-failure" }
                success { sh "echo POST-success" }
                unstable { sh "echo POST-unstable" }
                cleanup { sh "echo POST-cleanup" }
            }
        }
    }
}
